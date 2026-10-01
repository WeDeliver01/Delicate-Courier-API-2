# Deliverable 3: Complete ShipLogic Dependency Map

Every ShipLogic touchpoint in the repository, from a case-insensitive sweep of all
`.cs`, `.ts`, `.tsx`, `.php`, `.json` and `.sql` files at `bdd7cda`, then read
individually to establish purpose. Nothing here is inferred from the handover.

## The provider surface: 8 operations

`Features/Shiplogic/IShiplogicService.cs` is the complete abstraction boundary.
Everything ShipLogic does for Delicate passes through these eight methods, and
**this interface is the contract the Delicate Engine must satisfy**.

| # | Method | ShipLogic wire call | Criticality |
|---|--------|---------------------|-------------|
| 1 | `CreateShipmentAsync` | `POST /shipments` | **Critical** |
| 2 | `GetRatesWithDiagnosticsAsync` | `POST /v2/rates` | **Critical** |
| 3 | `GetRatesAsync` | delegates to (2) | **Critical** |
| 4 | `FindShipmentByCustomerReferenceAsync` | `GET /shipments?customer_reference=` | **Critical** (idempotency) |
| 5 | `GetShipmentLabelAsync` | `GET /shipments/{id}/label` -> PDF bytes | High |
| 6 | `GetLabelAsync` | `GET /v2/shipments/label?id=` -> signed URL -> S3 | High |
| 7 | `GetTrackingUpdatesAsync` | `GET /v2/tracking/shipments?consignment_ids[]=` | Medium |
| 8 | `CancelShipmentAsync` | `POST /v2/shipments/cancel` `{tracking_reference}` | Medium |

Observed wire-level quirks, all documented in code comments as live-verified
findings. Each is a ShipLogic defect the Engine simply will not have, so each is a
behaviour to **delete**, not port:

- `POST /v2/rates` returns HTTP 200 with `rates: null` when ShipLogic's
  server-side geocoder cannot resolve an address. Worked around by geocoding
  Delicate-side and passing explicit lat/lng.
- `/v2/rates` ignores the service-level parameter and returns every service level
  for the account, so filtering is done in C#.
- `POST /v2/shipments/{id}/cancel` returns 404 "Unhandled resource path";
  the real endpoint is `POST /v2/shipments/cancel` with the short tracking
  reference in the body (verified 2026-07-09).
- Label signed URLs reject requests carrying an `Authorization` header, so a
  separate bare `HttpClient` exists purely for S3 downloads.
- `service_level_code` must be present in the rates body even when empty, or some
  account/provider/route combinations return `rates: null`.
- **SPX (special trip) creation is denied on this account** and the permission
  cannot be enabled ("You do not have permission to create rates for special
  trips", verified 2026-08-13).

## Dependency inventory by component

Populated from the code, as the handover requires. Reference counts are
case-insensitive mentions of "shiplogic", used only to indicate coupling density.

### Critical: blocks any cutover

| Component | Refs | ShipLogic dependency | Current purpose | Delicate Engine replacement |
|---|---|---|---|---|
| `Features/Shipments/ShipmentOrchestrationService.cs` | 71 | `CreateShipmentAsync`, `FindShipmentByCustomerReferenceAsync`, `GetShipmentLabelAsync`, token lookup | The single chokepoint all booking funnels through. Owns the guard sequence, the advisory lock, the three-layer idempotency, Hangfire retry semantics, label fetch, storefront pushback | Engine booking endpoint behind the same interface. **Guard sequence and idempotency layers must be preserved verbatim** |
| `Features/Shiplogic/ShiplogicService.cs` | 62 | All 8 HTTP calls, Polly retry, per-request bearer token mutation | The only HTTP client that talks to ShipLogic | `DelicateEngineProvider` implementing `IShiplogicService` (renamed to `IShipmentProvider`) |
| `Features/Shiplogic/IShiplogicService.cs` | 20 | The interface itself | Provider abstraction boundary — **already exists, which is the migration's biggest asset** | Rename to a provider-neutral name; keep the shape |
| `Features/Shipping/GetRates/GetRatesController.cs` | 26 | `GetRatesWithDiagnosticsAsync`, `Store.ShiplogicAccountId/ProviderId`, `Tenant.ShiplogicBearerToken` | Public checkout rate quote for WooCommerce | Engine rate engine. **This endpoint's request/response contract is frozen** |
| `Features/Orders/OrderToShipmentMapper.cs` | 22 | Builds `CreateShipmentRequest` (ShipLogic-shaped DTO) | Order -> provider payload translation, parcel sizing, slot handling | Becomes the translation layer the handover describes. Retarget to Engine DTOs |
| `Features/Webhooks/ShiplogicTrackingWebhookController.cs` | 29 | Inbound ShipLogic callback, auth-key validation | Status ingest, TrackingEvent dedup by `ExternalEventId`, Woo status pushback, delivered -> completed | Internal Engine event consumer. Route stays live through the rollback window |
| `Features/Shopify/Carriers/ShopifyRatesController.cs` | 16 | `GetRatesAsync` | Shopify CarrierService rate callback | Engine rate engine. Response shape dictated by Shopify |

### High: must be handled before decommission

| Component | Refs | Dependency | Replacement |
|---|---|---|---|
| `Program.cs` | 13 | `ShiplogicSettings` binding, typed `HttpClient` (100s timeout), Polly retry + circuit breaker, JWT bypass for the webhook path | Engine client registration; keep the circuit-breaker shape |
| `Features/Shiplogic/LabelService.cs` | 4 | Persists label bytes fetched from ShipLogic | Engine label generation. **Delicate will now generate labels itself — new capability** |
| `Features/Shiplogic/ShiplogicSettings.cs` | 9 | `ApiBaseUrl`, `BearerToken`, timeout, retry, circuit-breaker, rate-limit config | Engine equivalent |
| `Features/Shiplogic/ShiplogicCallException.cs` | 6 | Typed exception carrying diagnostics | Engine equivalent; keep diagnostics plumbing |
| `Features/Shiplogic/DTOs/*` (10 files) | — | `CreateShipmentRequest`, `AddressDto`, `ContactDto`, `ParcelDto`, `RateResponseDto`, `ShippingRateDto`, `ShipmentDto`, `ShiplogicResponses`, `ShiplogicDiagnostics`, `OrderSummaryDto` | Engine-native DTOs. **`AddressDto` carries `AccountId`/`ProviderId`, which are ShipLogic-specific and should not survive** |
| `Features/Stores/StoresController.cs` + `DTOs/StoreDTOs.cs` | 21 | Reads/writes `ShiplogicAccountId`, `ShiplogicProviderId` | Engine account binding, or drop entirely |
| `Features/Tenants/TenantService.cs` | 10 | Manages `Tenant.ShiplogicBearerToken` | Engine credential, or drop (internal auth replaces per-tenant provider tokens) |
| `Features/WooCommerce/PluginWebhookController.cs` / `PluginWebhookService.cs` | 15 | Intake gate `ShouldAutoCreateShipment`, special-trip reclassification | Provider-neutral; mostly naming cleanup |
| `Features/Shipments/ShipmentsController.cs` | 10 | Manual booking trigger, label endpoint, tracking endpoint | Provider-neutral behind the abstraction |
| `Features/Shipments/ShipmentStorefrontMeta.cs` | 10 | Maps ShipLogic status strings to `_dcp_*` Woo meta and human labels | **Status vocabulary is a client-visible contract** — see below |

### Medium: naming and surfacing

| Component | Refs | Dependency |
|---|---|---|
| `Features/Admin/IntegrationsController.cs` | 10 | `GET /api/admin/integrations/shiplogic-webhook` — surfaces webhook config to SuperAdmin |
| `Infrastructure/Services/SystemEventLogger.cs` | — | Event types contain `shiplogic` substrings |
| `Features/WooCommerce/*` (6 more files) | 40 | "Shiplogic" in log lines, notes, and the default courier-name fallback |
| `Features/Shipping/GetRates/DTOs/GetRatesResponseDto.cs` | 5 | `Debug` block exposes raw ShipLogic request/response under `X-Debug-Key` |
| Frontend: 11 `.tsx` files | — | `app/super-admin/shiplogic-webhook/page.tsx`, store/tenant edit forms for account/provider/token, sidebar nav, dashboard copy |

### Low / historical

| Component | Note |
|---|---|
| `Features/Testing/TestShiplogicController.cs` | 14 refs. Diagnostic only. **Delete at decommission, do not migrate** |
| `Migrations/*` (14 files) | `AddShiplogicCredentialsToStores`, `AddShiplogicBearerTokenToTenant`, plus snapshot files. **Historical — never edit an applied migration** |
| `attached_assets/Shopify*_1780467616*.cs` | 4 files. Scratch copies of Shopify code, not compiled. Ignore |

## Things the sweep proves are NOT ShipLogic-coupled

Useful because it bounds the work:

- **No ShipLogic ID is a primary or foreign key anywhere.** See [Deliverable 4](04-database-and-identifiers.md).
- **No ShipLogic-specific enum exists.** `Domain/enums` contains none.
- **No queue consumer or scheduled job calls ShipLogic directly.** Both recurring
  jobs (`woo-status-reconciliation`, `unbooked-completed-orders-sentinel`) reach
  ShipLogic only by enqueuing `CreateShipmentForOrderAsync`.
- **Auth is not ShipLogic-coupled.** User auth is Supabase JWT. ShipLogic's bearer
  token is a per-tenant provider credential, never a user identity. The handover's
  "AuthService / ShipLogic API auth -> Remove" row maps to deleting one column.
- **The plugins do not know ShipLogic exists.** The WooCommerce plugins call only
  Delicate endpoints. Confirmed by grep across `Plugins/`. This is what makes the
  "clients never find out" goal achievable.

## The one contract leak to be careful about

`ShipmentStorefrontMeta` writes ShipLogic status values into WooCommerce order
meta (`_dcp_shipment_status`, `_dcp_shipment_status_label`) on the merchant's order
screen, and `ShiplogicTrackingWebhookController` maps `"delivered"` to the Woo
status `"completed"`.

So ShipLogic's **status vocabulary** is already visible to merchants, even though
ShipLogic itself is not. If the Engine emits different status strings, merchant
order screens change and any merchant automation keyed on those meta values
breaks silently.

**Recommendation:** define the Engine's status vocabulary as a superset that
contains the existing ShipLogic strings verbatim, and keep
`ShipmentStorefrontMeta.StatusLabel` as the single translation point it already
is. This is the cheapest contract preservation in the whole migration and it is
easy to miss, because it is not an API endpoint.

Status strings currently persisted on `Shipment.ShipmentStatus`, per the entity
documentation: `Created`, `PickedUp`, `InTransit`, `OutForDelivery`, `Delivered`,
`Failed`, `Cancelled`. Note that the webhook writes `payload.Status` through
unmodified, so the live set is whatever ShipLogic actually sends, which should be
confirmed against production data before freezing the vocabulary.
