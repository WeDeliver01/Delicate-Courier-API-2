# Deliverable 5: Webhook, Event and Background Job Map

## Inbound webhooks

Five webhook endpoints. All unauthenticated at the JWT layer; each validates its
own shared secret. All are rate-limited by IP where noted.

| Route | Source | Auth mechanism | Effect |
|---|---|---|---|
| `POST /api/webhooks/shiplogic/tracking` | ShipLogic | Auth key, delivered verbatim in `Authorization` (no `Bearer` prefix). Also accepts `X-Webhook-Secret`. Config: `Shiplogic:WebhookSecret`. **If unconfigured, accepts unauthenticated with a warning** | Updates `Shipment.ShipmentStatus`, delivery dates, inserts deduped `TrackingEvent` rows, pushes status to Woo |
| `POST /api/webhooks/plugin/order` | WooCommerce plugin (DCP/DTSC) | Validated in `PluginWebhookService` | Persists Order, applies the intake gate, enqueues booking |
| `POST /api/webhooks/woocommerce/order` | legacy Woo webhook | `WebhookSignatureValidator` | Persists Order, enqueues booking |
| `POST /api/webhooks/shopify/order` | Shopify | HMAC via Shopify convention | `ShopifyOrderIngestionService`, enqueues booking |
| `POST /api/webhooks/plugin/debug-log` | WooCommerce plugin | none | Writes `PluginDebugLog` |

Plus health probes: `/api/webhooks/{shopify,plugin,woocommerce}/health`.

### Behaviours of the ShipLogic tracking webhook that must be preserved

These are deliberate and each has a reason in the code:

- **Always returns HTTP 200**, even on "shipment not found" and even on an
  unhandled exception, specifically so ShipLogic does not retry. Only an invalid
  secret returns 401. An Engine event consumer should keep this shape if it is
  delivered over HTTP; if it becomes an in-process event, the equivalent is "never
  fail the producer because the consumer had a problem".
- **Two-step lookup**: `ConsignmentID == payload.ShipmentId`, falling back to
  `TrackingNumber == payload.ShortTrackingReference`.
- **`IgnoreQueryFilters()`** on every query, because there is no JWT and therefore
  no tenant claim.
- **`TrackingEvent` dedup** on `ExternalEventId`, batched, and also guarding
  against duplicates inside one payload.
- **Only `delivered` maps to a Woo status change** (`completed`). A cancelled
  shipment deliberately does **not** cancel the customer's order; it adds an
  internal note instead, because cancelling a booking just means the merchant will
  rebook. Getting this wrong would cancel real customer orders.
- **Meta is refreshed on every webhook** (cheap upsert, keeps the merchant metabox
  live), but **the customer-visible tracking note is sent at most once**, gated by
  `Shipment.TrackingPushedOn`.
- Constant-time secret comparison (`FixedTimeEquals`).

## Outbound calls Delicate makes

| Target | Purpose | Client |
|---|---|---|
| `api.shiplogic.com` | All 8 provider operations. 100s timeout spanning the whole Polly pipeline | typed `IShiplogicService` + retry + circuit breaker |
| merchant WooCommerce REST | Order fetch, status update, order notes, `_dcp_*` meta upsert | `IWooCommerceService` + retry + circuit breaker + `WooCommerceRateLimiter` |
| Shopify Admin API | Order fetch, fulfilment write-back, CarrierService + webhook registration | `IShopifyService` |
| `maps.googleapis.com` | Geocoding (platform key) and Distance Matrix (**merchant's own key**, so special-trip usage bills to them) | `GoogleGeocoder`, `GoogleDistanceMatrixService` via factory |
| `nominatim.openstreetmap.org` | Free geocoding fallback when no Google key | `NominatimGeocoder` |
| `api.resend.com` | Admin notification email (special-trip manual booking) | `AdminEmailSender` |
| S3 presigned URLs | Label PDF download. Requires a **bare** client: presigned URLs reject `Authorization` | static `_labelDownloadClient` |

Geocoding is wrapped in a read-through DB cache (`GeocodeCache` table) via
`CachedGeocoder` / `ChainGeocoder`.

## Outbound notifications to clients

Delicate does **not** currently emit webhooks to its API clients. Storefront
updates are made by Delicate **calling into** the merchant's platform:

- WooCommerce: order notes, order status, `_dcp_*` meta, via Woo REST
- Shopify: fulfilment with tracking, via `ShopifyTrackingWriteback` (its own
  Hangfire job, so Shopify hiccups get an independent retry schedule)

This is relevant to the Engine design: there is no client-facing webhook contract
to preserve, which means the Engine's event system is a free design choice
internally. The constraint is only that the **effects** on the merchant storefront
stay identical.

## Hangfire

Storage: Postgres, same instance, separate connection pool. Dashboard at
`/hangfire` behind `DashboardOptions` with an auth filter; a login shim exists at
`GET /admin/hangfire-login`. A `HangfireSystemEventFilter` emits a `job.failed`
system event on failure.

### Enqueued jobs

| Job | Enqueued from | Retry policy |
|---|---|---|
| `ShipmentOrchestrationService.CreateShipmentForOrderAsync` | 5 call sites + manual endpoint | `[AutomaticRetry(Attempts = 10, DelaysInSeconds = 30, 60, 120, 300, 600, 1200, 1800, 3600, 7200, 14400)]` — roughly a 24h window, then Failed queue |
| `ShopifyTrackingWriteback.WriteTrackingAsync` | `ShipmentOrchestrationService` after a successful booking | default |

The five enqueue sites:

```
Features/WooCommerce/WebhookService.cs:138
Features/WooCommerce/WebhookService.cs:185
Features/WooCommerce/PluginWebhookService.cs:102
Features/WooCommerce/PluginWebhookService.cs:176
Features/WooCommerce/WooStatusReconciliationService.cs:163
```
plus `POST /api/Shipments/create/{orderRef}` (manual admin trigger).

**This is why the orchestrator's guards live in the orchestrator and not at
intake.** The code comments record a real incident: a collect order was booked
because a second trigger path bypassed the intake gate. The fix was to guard at
the single chokepoint so new trigger paths are covered automatically. The Engine
migration must not undo this by moving guards upstream.

### Recurring jobs

| Id | Schedule | Purpose |
|---|---|---|
| `woo-status-reconciliation` | `*/10 * * * *` | Polls Woo REST for unshipped orders on trigger-status stores, books once the trigger status is reached. Covers stores whose plugin build only syncs each order once |
| `unbooked-completed-orders-sentinel` | `*/15 * * * *` | Flags Completed orders with no Shipment (after a grace period) via `order.completed_without_booking` + error log. Safety net from incident #50829 |
| `daa-pull-sweep` | — | **Removed** 2026-07-13. `RecurringJob.RemoveIfExists` is called at startup to deregister it |

Neither recurring job calls ShipLogic directly. Both reach it only by enqueuing the
booking job, so both are provider-neutral already.

## System events

`SystemEvent` is the audit trail, surfaced to SuperAdmin at
`GET /api/admin/system-events`. **It is also used as an idempotency marker**: the
special-trip flow checks for an existing
`shipment.special_trip_manual_booking` event (with `IgnoreQueryFilters`) to avoid
sending duplicate admin emails and Woo notes on webhook retries.

Complete set of event types in the code:

```
auth.login
job.failed
order.completed_without_booking
order.fulfillment_verification_failed
order.received
order.reclassified_collect
order.special_trip_detected
shipment.book_failed
shipment.booked
shipment.skipped_collect
shipment.special_trip_manual_booking
shipment.tracking_pushed
tracking.event_ingested
user.created
user.deleted
```

For the migration, this set is close to sufficient as a correlation backbone. The
events needed for a safe cutover that do **not** exist yet:

| Proposed event | Why the cutover needs it |
|---|---|
| `shipment.provider_selected` | Records which provider handled a booking and why (routing decision), per shipment |
| `shipment.shadow_compared` | Shadow-mode rate/payload comparison result — the primary Phase 2 validation signal |
| `shipment.provider_rolled_back` | A client moved back to ShipLogic, with the trigger |
| `rate.quoted` | Rate quotes are currently not evented at all, so there is no way to compare quoted vs booked price across the cutover |

`rate.quoted` is the notable gap: the rate path is the highest-volume client-facing
operation and it currently leaves no audit trail beyond log lines.
