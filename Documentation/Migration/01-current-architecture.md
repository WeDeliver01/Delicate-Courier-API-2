# Deliverable 1: Current Architecture Map

Derived from code at `bdd7cda`. Every component named here exists in the repository.

## Solution layout

```
DelicateCouriers.ApiService     195 .cs   the API, all business logic, 21 feature slices
DelicateCouriers.Domain          14 .cs   13 entities
DelicateCouriers.Tests           23 .cs   PluginPlatform, Orders, Shopify, Webhooks, Plugin
DelicateCouriers.Web              2 .cs   Blazor shell (not the customer-facing frontend)
DelicateCouriers.AppHost          1 .cs   .NET Aspire orchestration
DelicateCouriers.ServiceDefaults  1 .cs   telemetry/health defaults
DelicateCouriers.Contracts        0 .cs   empty
delicate-couriers-frontend                Next.js admin/dispatcher portal
Plugins/WooCommerce/*                     3 merchant plugin lines (PHP)
```

Target framework `net8.0` throughout. No `.sln`; projects are referenced directly.

## Runtime topology

```
WooCommerce merchant site            Shopify store
  (DCP / DTSC / we-deliver plugin)     (CarrierService + order webhook)
         |                                    |
         |  POST /api/public/shipping/rates   |  POST /api/shopify/rates/{storeId}
         |  POST /api/webhooks/plugin/order   |  POST /api/webhooks/shopify/order
         v                                    v
   +------------------------------------------------------+
   |         DelicateCouriers.ApiService (.NET 8)         |
   |                                                      |
   |  Controllers (27)  ->  Feature services              |
   |                          |                           |
   |                          +-- ShipmentOrchestration    |
   |                          +-- OrderToShipmentMapper    |
   |                          +-- IShiplogicService  ------+---> api.shiplogic.com
   |                          +-- IWooCommerceService -----+---> merchant Woo REST
   |                          +-- IShopifyService ---------+---> Shopify Admin API
   |                          +-- IGeocoder --------------- +---> Google / Nominatim
   |                          +-- SpecialTripQuoter -------+---> Google Distance Matrix
   |                          +-- IAdminEmailSender                                   |
   |                                                      |
   |  Hangfire (Postgres storage) — booking jobs + 2 recurring sweeps
   |  EF Core + Npgsql, EnableRetryOnFailure(3)
   +------------------------------------------------------+
                          |
                          v
              Supabase / Postgres (managed)
                          ^
                          |
   api.shiplogic.com -> POST /api/webhooks/shiplogic/tracking  (status + events in)
```

Deployment (from the `/areas/delicate-courier` operational record, not the code):
.NET API on `:8080`, Next.js standalone on `:5000`, nginx terminating TLS, both
under systemd as a non-login `delicate` user, on a dedicated Vultr instance.
Public names `api2` / `app2` / `webhooks2.delicatecourier.co.za`.

## The booking flow, as actually implemented

This is the path that matters most, because it is the one the Engine replaces.
Source: `Features/Shipments/ShipmentOrchestrationService.cs`.

```
1. Order arrives
     POST /api/webhooks/plugin/order    (PluginWebhookService)   -- primary path
     POST /api/webhooks/woocommerce/order (WebhookService)       -- legacy path
     POST /api/webhooks/shopify/order   (ShopifyOrderIngestion)  -- Shopify path
         |
         v
2. Intake persists the Order, decides eligibility, and enqueues a Hangfire job:
     BackgroundJobClient.Enqueue<ShipmentOrchestrationService>(
         s => s.CreateShipmentForOrderAsync(orderId))

   There are FIVE call sites that enqueue this job:
     Features/WooCommerce/WebhookService.cs:138
     Features/WooCommerce/WebhookService.cs:185
     Features/WooCommerce/PluginWebhookService.cs:102
     Features/WooCommerce/PluginWebhookService.cs:176
     Features/WooCommerce/WooStatusReconciliationService.cs:163
   plus the manual admin trigger: POST /api/Shipments/create/{orderRef}

3. CreateShipmentForOrderAsync — the single chokepoint all booking funnels through.
   [AutomaticRetry(Attempts = 10, DelaysInSeconds = 30 .. 14400)]  (~24h window)

   Guard sequence, in order:
     a. Order not found                      -> throw (visible in Hangfire Failed)
     b. Order already has a Shipment         -> idempotent success, return
     c. FulfillmentType == "collect"         -> Failure, no retry  (Layer 2)
     d. OrderStatus terminal                 -> Failure, no retry
        (Cancelled/Canceled/Refunded/Failed/Trash)
     e. FulfillmentType == "special_trip"    -> manual-booking flow, Failure
     f. Collection shipping-method keyword   -> Failure, reclassify to collect (Layer 3)
        (persisted method id/title first, Woo REST re-fetch as fallback;
         lookup failure THROWS rather than booking unverified)
     g. Backfill RequestedDeliveryDate from Woo order meta if missing
     h. Map Order -> CreateShipmentRequest (OrderToShipmentMapper)
     i. Tenant.ShiplogicBearerToken missing  -> throw (admin-fixable, retries)

4. Inside an EF execution strategy (required: NpgsqlRetryingExecutionStrategy
   rejects user-initiated transactions), inside ONE transaction:

     SELECT pg_advisory_xact_lock(orderId)     -- per-order serialization
     re-check Shipments for OrderID            -- sibling-won short circuit
     IShiplogicService.FindShipmentByCustomerReferenceAsync(customerRef)
                                               -- cross-system idempotency guard
     if no existing: IShiplogicService.CreateShipmentAsync(...)
     INSERT Shipment row
     COMMIT                                    -- releases the advisory lock

5. After the transaction (deliberately outside, so a failure cannot replay the create):
     GetShipmentLabelAsync -> LabelService.SaveLabelAsync
     SystemEvent "shipment.booked"
     TryPushTrackingToStorefrontAsync
        Shopify -> enqueue ShopifyTrackingWriteback (own retry schedule)
        Woo     -> PushShipmentMetaAsync (unconditional upsert)
                 + AddTrackingInfoAsync (gated by Shipment.TrackingPushedOn,
                   because that note is customer-visible)
```

### Why the idempotency design is worth preserving verbatim

The handover (section 34) asks for an idempotency strategy. One already exists, at
three layers, and it was clearly built in response to real production incidents:

| Layer | Mechanism | Window it closes |
|-------|-----------|------------------|
| Local | `order.Shipment != null`, re-checked inside the lock | Job replay after restart |
| Concurrency | `pg_advisory_xact_lock(orderId)`, transaction-scoped | Two workers, same order |
| Cross-system | `GET /shipments?customer_reference={ref}` before create | Crash between ShipLogic create and local save |

The cross-system guard is strict about false positives: it only accepts an array
response, and only a row whose `customer_reference` matches exactly, specifically
so that a ShipLogic endpoint silently ignoring the query parameter cannot attach
an unrelated shipment to the order. Any Engine replacement must keep that
property. The customer reference format is `WC-{WooOrderID}`.

## The rate quote flow

Source: `Features/Shipping/GetRates/GetRatesController.cs`.

```
POST /api/public/shipping/rates   (unauthenticated, called at merchant checkout)
  1. Load Store + Tenant; 404 if store missing or inactive
  2. Tenant.ShiplogicBearerToken missing -> 500 "Shipping provider not configured"
  3. Build collection AddressDto from Store.Collection* fields
       + Store.ShiplogicAccountId / ShiplogicProviderId
  4. Resolve collection lat/lng: Store.CollectionLatitude/Longitude, else geocode
       and persist back to the Store (avoid paying the lookup twice)
  5. Build delivery AddressDto from the request; geocode (cached)
  6. POST api.shiplogic.com/v2/rates
  7. If rates present: project to RateOptionDto, override the display name with
       Store.CheckoutRateLabel when set
  8. If NO rates: special-trip fallback, but ONLY when the store has Woo REST
       credentials (intake needs them to recognise the order later).
       SpecialTripQuoter -> Google Distance Matrix with the MERCHANT'S OWN key
       (Store.GoogleMapsApiKey, so usage bills to them) x Store.SpecialTripCostPerKm
       Label: "{CheckoutRateLabel or 'Special Trip Request'} (X km)"
       ServiceLevelCode MUST stay the store's DefaultServiceLevel or the plugin
       filters the rate out.
```

Two non-obvious behaviours the Engine must not break:

- Explicit lat/lng is supplied on every address specifically because ShipLogic's
  own geocoder returns `rates: null` on an HTTP 200 for some addresses. The
  geocoding layer exists to work around a ShipLogic defect. Once the Engine owns
  rating, this becomes a Delicate-side input rather than a workaround, but the
  coordinates are still needed for distance pricing.
- Service-level filtering happens **client-side in C#**, because ShipLogic
  `/v2/rates` returns every service level for the account and ignores the
  parameter.

## Where business logic lives today

| Concern | Where it actually is |
|---------|---------------------|
| Base delivery rate | ShipLogic `/v2/rates` — entirely external |
| Merchant markup | WooCommerce plugin, client side (`markup_type`, `markup_value`) |
| Special-trip rate | Delicate: `SpecialTripQuoter` x `Store.SpecialTripCostPerKm` |
| Checkout rate label | Delicate: `Store.CheckoutRateLabel` |
| Service level | Delicate: `Store.DefaultServiceLevel` (default `"STD"`), filtered in C# |
| Collection/delivery windows | Delicate: `Store.CollectionTimeFrom/To`, `DeliveryTimeFrom/To` |
| Parcel sizing | Delicate: `PackageType` + `PackageMappingRule` per store |
| Collect vs deliver | Delicate: three layers of guards in the orchestrator |
| Weekend / holiday pricing | **Nowhere.** Does not exist. |
| Capacity / availability | **Nowhere.** Does not exist. |
| Membership pricing tiers | **Nowhere in this codebase.** |
| Driver allocation | **Nowhere.** ShipLogic's operation entirely. |

The last four rows are the project's real scope. See
[Deliverable 7](07-engine-gap-analysis.md).
