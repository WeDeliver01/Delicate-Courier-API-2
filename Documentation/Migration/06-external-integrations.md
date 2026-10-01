# Deliverable 6: External Integration Map

Every third party the API depends on, and what happens to each at decommission.
Relevant because "remove ShipLogic" is sometimes assumed to mean "remove the only
external dependency", and it does not.

## Integrations

| Integration | Direction | Carries courier semantics | Fate |
|---|---|---|---|
| **ShipLogic** | out + in (webhook) | Yes, all of it | **Removed** |
| **WooCommerce REST** (per merchant) | out | Order data, status, notes, meta | **Stays.** Unaffected by provider change |
| **WooCommerce plugins** (DCP / DTSC / we-deliver) | in | Rate requests, order intake, label requests | **Stays.** Contract frozen |
| **Shopify Admin API** | out | Order fetch, fulfilment write-back, CarrierService registration | **Stays** |
| **Shopify webhooks** | in | Order intake | **Stays.** Contract frozen |
| **Google Maps Geocoding** | out | Address -> lat/lng | **Stays, purpose changes.** Today a ShipLogic workaround; after migration it is a primary input to Delicate's own distance pricing |
| **Google Distance Matrix** | out | Driving distance for special-trip quotes. **Merchant's own API key** (`Store.GoogleMapsApiKey`) so usage bills to them | **Stays and becomes load-bearing.** The Engine's distance pricing depends on it |
| **Nominatim (OpenStreetMap)** | out | Free geocoding fallback when no Google key | **Stays** |
| **Supabase Auth** | out | User identity, JWT issuance, `tenant_id` claim | **Stays.** Not courier-coupled |
| **Supabase Postgres** | out | System of record + Hangfire storage | **Stays** |
| **Resend** | out | Admin notification email | **Stays.** Its main current use (special-trip manual booking alerts) should become unnecessary once the Engine can book special trips |
| **S3 presigned URLs** | out | ShipLogic label PDF download | **Removed** with ShipLogic. Delicate generates its own labels |

## Three consequences worth planning around

**1. Label generation is a new capability, not a migration.**
Today `LabelService` persists bytes that ShipLogic produced. After migration
Delicate must **generate** a compliant courier label: barcode, tracking reference,
addresses, parcel details, in a printable format drivers can scan. This is real
work that does not appear anywhere in the handover's capability list, and it is on
the critical path because `POST /api/webhooks/plugin/label` is one of the six
frozen client endpoints.

**2. Google Distance Matrix moves from edge case to core dependency.**
It currently serves only the special-trip fallback, when ShipLogic returns no
rates. If the Engine prices by distance, it is in the hot path of every quote. Two
problems follow:

- **Billing**: the current design deliberately uses the *merchant's* key so
  special-trip lookups bill to them. If distance becomes the basis of all pricing,
  that model needs an explicit decision. Platform key with cost recovery, or
  continue per-merchant.
- **Latency and availability**: a checkout rate quote cannot wait on a slow
  third-party call, and a Distance Matrix outage would take down all rating.
  A distance cache keyed on rounded coordinate pairs, plus a zone-based fallback
  table, should be designed in from the start rather than retrofitted.

**3. ShipLogic's geocoder workaround becomes a genuine feature.**
The geocoding layer exists because ShipLogic returned `rates: null` for addresses
its own geocoder could not resolve. That reason disappears. But the coordinates it
produces are exactly what distance-based pricing needs, so the layer survives with
a better justification: keep `CachedGeocoder` / `ChainGeocoder` and the
`GeocodeCache` table as-is.

## Secrets and configuration

Per the handover's section 32, internal credentials must never reach clients.
Current state:

| Secret | Location | Notes |
|---|---|---|
| `Tenant.ShiplogicBearerToken` | DB column | Per-tenant provider credential. **Dropped at decommission** |
| `Shiplogic:WebhookSecret` | env `Shiplogic__WebhookSecret` | **If unset, the tracking webhook accepts unauthenticated with only a warning.** Worth confirming it is set in production before anything else in this project |
| `RATES_DEBUG_SECRET` | env | Gates the debug block on the public rates endpoint. The response redacts contact PII (company, name, mobile, email) and truncates bodies to 8KB; if redaction throws, the body is omitted entirely |
| `GOOGLE_MAPS_API_KEY` | env | Platform geocoding key |
| `Store.GoogleMapsApiKey` | DB column | Merchant's own Distance Matrix key |
| `Store.WooConsumerKey` / `WooConsumerSecret` | DB columns | Merchant Woo REST credentials |
| Supabase keys | env | Auth + DB |
| `ConnectionStrings__delicatedb` | env | Normalised at startup by `NormalizePostgresConnectionString` |

Two observations for the migration:

- The debug-block redaction on the public rates endpoint is careful work. When the
  Engine replaces the ShipLogic diagnostics, the equivalent redaction must be
  carried over, or the same endpoint starts leaking customer PII under a single
  global secret.
- `Shiplogic:WebhookSecret` defaulting to "accept unauthenticated" is the one
  security posture in this area that should be tightened rather than ported. The
  Engine's event path should fail closed.

## Resilience posture to carry forward

Both external-call pipelines are already hardened, and the Engine client should be
registered the same way:

- **ShipLogic client**: typed `HttpClient`, 100s timeout spanning the whole Polly
  pipeline (30s produced spurious booking failures, per the code comment and order
  WC-49905), retry policy, then circuit breaker.
- **Circuit breaker**: `handledEventsAllowedBeforeBreaking: 10`. Registered as a
  singleton at the `HttpClient` layer in `Program.cs` specifically so the failure
  counter accumulates across calls and actually trips. A per-instance breaker
  inside `ShiplogicService` was removed because it could never trip, and the field
  comment explains why. **Do not reintroduce a per-instance breaker in the Engine
  provider.**
- **WooCommerce client**: retry + circuit breaker + a dedicated
  `WooCommerceRateLimiter`.
- **Webhook endpoints**: IP rate limiting (`EnableRateLimiting("WebhookIp")`).
