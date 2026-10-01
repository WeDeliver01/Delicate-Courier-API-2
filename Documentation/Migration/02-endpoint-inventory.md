# Deliverable 2: Complete Endpoint Inventory

106 endpoints across 27 controllers. No minimal-API routes: every endpoint is an
MVC controller action, which means the whole surface is discoverable by attribute
and nothing is hidden in `Program.cs`.

Extracted mechanically from `[Http*]` and `[Route]` attributes at `bdd7cda`.
`[controller]` tokens are resolved to their real segment.

## The part that matters: the external client contract

Of the 106 endpoints, **six** carry courier semantics to a party outside Delicate.
These are the contract the handover says to preserve, and they are the only
endpoints where a breaking change costs client migration work.

| Verb | Route | Caller | ShipLogic today | Must survive migration |
|---|---|---|---|---|
| POST | `/api/public/shipping/rates` | WooCommerce plugin at checkout | `POST /v2/rates` | **Yes** — contract frozen |
| POST | `/api/shopify/rates/{storeId}` | Shopify CarrierService callback | `POST /v2/rates` | **Yes** — Shopify dictates the response shape |
| POST | `/api/webhooks/plugin/order` | WooCommerce plugin (DCP/DTSC) | enqueues booking | **Yes** — contract frozen |
| POST | `/api/webhooks/plugin/label` | WooCommerce plugin | `GET /shipments/{id}/label` | **Yes** — contract frozen |
| POST | `/api/webhooks/woocommerce/order` | legacy Woo webhook | enqueues booking | **Yes** — older installs still use it |
| POST | `/api/webhooks/shopify/order` | Shopify order webhook | enqueues booking | **Yes** — contract frozen |

And one that **disappears** rather than migrating:

| Verb | Route | Caller | Fate |
|---|---|---|---|
| POST | `/api/webhooks/shiplogic/tracking` | ShipLogic -> us | Replaced by an internal Engine event. Keep the route live and accepting during the rollback window; retire it only at decommission. |

Everything else is an internal surface where Delicate controls both ends:

- **85 endpoints** require authentication (JWT via Supabase claims). These are the
  Next.js admin/dispatcher portal, tenant and store management, reporting, and
  SuperAdmin tooling. They can be changed freely alongside the frontend.
- **`/api/v1/plugins/*`** (7 endpoints: licensing, update check, download,
  changelog, heartbeat, telemetry) are unauthenticated but carry **no courier
  semantics**. They are the plugin distribution platform and are entirely
  unaffected by this migration.
- **Health endpoints** (`/api/Health`, and a `health` on each webhook controller)
  are unaffected.
- **`/api/testing/shiplogic/test-rates/{storeId}`** is a diagnostic endpoint that
  should be removed at decommission, not migrated.

### Practical consequence

The handover's framing is "preserve a large valuable integration surface". The code
says the surface is six endpoints. That is small enough to pin with contract tests
before any Engine work begins, which is the single highest-value thing to do first.

## Full inventory


### IntegrationsController

Class-level auth: `Authorize(Roles = "SuperAdmin")`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/admin/integrations/shiplogic-webhook` | `GetShiplogicWebhook` |  |

### PluginDebugLogIngestController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/webhooks/plugin/debug-log` | `Ingest` |  |

### PluginDebugLogsAdminController

Class-level auth: `Authorize(Roles = "SuperAdmin")`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/admin/plugin-debug-logs` | `GetLogs` |  |
| GET | `/api/admin/plugin-debug-logs/contexts` | `GetContexts` |  |
| GET | `/api/admin/plugin-debug-logs/stores` | `GetStores` |  |
| DELETE | `/api/admin/plugin-debug-logs/store/{storeId:int}` | `ClearStoreLogs` |  |

### SystemEventsController

Class-level auth: `Authorize(Roles = "SuperAdmin")`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/admin/system-events` | `GetEvents` |  |
| GET | `/api/admin/system-events/event-types` | `GetEventTypes` |  |

### AuthController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Auth/me` | `GetCurrentUser` | Authorize |
| GET | `/api/Auth/health` | `Health` |  |

### DashboardController

Class-level auth: `Authorize(Roles = "Admin,SuperAdmin")`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/admin/hangfire-login` | `HangfireLogin` | Authorize |

### HealthController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Health` | `Get` | AllowAnonymous |

### OrdersController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Orders` | `GetOrders` |  |

### PackageTypesController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/stores/{storeId}/PackageTypes` | `GetPackageTypes` |  |
| GET | `/api/stores/{storeId}/PackageTypes/{packageTypeId}` | `GetPackageType` |  |
| POST | `/api/stores/{storeId}/PackageTypes` | `CreatePackageType` |  |
| PUT | `/api/stores/{storeId}/PackageTypes/{packageTypeId}` | `UpdatePackageType` |  |
| DELETE | `/api/stores/{storeId}/PackageTypes/{packageTypeId}` | `DeletePackageType` |  |
| POST | `/api/stores/{storeId}/PackageTypes/seed-defaults` | `SeedDefaults` |  |
| POST | `/api/stores/{storeId}/PackageTypes/{packageTypeId}/rules` | `AddMappingRule` |  |
| DELETE | `/api/stores/{storeId}/PackageTypes/{packageTypeId}/rules/{ruleId}` | `DeleteMappingRule` |  |

### PluginPlatformAdminController

Class-level auth: `Authorize(Roles = "SuperAdmin")`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/admin/plugin-platform/plugins` | `ListPlugins` |  |
| POST | `/api/admin/plugin-platform/plugins` | `CreatePlugin` |  |
| GET | `/api/admin/plugin-platform/plugins/{pluginId:int}/releases` | `ListReleases` |  |
| POST | `/api/admin/plugin-platform/plugins/{pluginId:int}/releases` | `UploadRelease` |  |
| POST | `/api/admin/plugin-platform/releases/{releaseId:int}/rollout` | `SetRollout` |  |
| POST | `/api/admin/plugin-platform/releases/{releaseId:int}/withdraw` | `Withdraw` |  |
| POST | `/api/admin/plugin-platform/releases/{releaseId:int}/rules` | `AddRolloutRule` |  |
| DELETE | `/api/admin/plugin-platform/rules/{ruleId:int}` | `DeleteRolloutRule` |  |
| GET | `/api/admin/plugin-platform/plugins/{pluginId:int}/licenses` | `ListLicenses` |  |
| POST | `/api/admin/plugin-platform/plugins/{pluginId:int}/licenses` | `CreateLicense` |  |
| POST | `/api/admin/plugin-platform/licenses/{licenseId:int}/revoke` | `RevokeLicense` |  |
| GET | `/api/admin/plugin-platform/installations` | `ListInstallations` |  |
| GET | `/api/admin/plugin-platform/telemetry` | `ListTelemetry` |  |
| GET | `/api/admin/plugin-platform/plugins/{pluginId:int}/flags` | `ListFlags` |  |
| POST | `/api/admin/plugin-platform/plugins/{pluginId:int}/flags` | `UpsertFlag` |  |
| DELETE | `/api/admin/plugin-platform/flags/{flagId:int}` | `DeleteFlag` |  |
| GET | `/api/admin/plugin-platform/audit-logs` | `ListAuditLogs` |  |

### PluginUpdateController

Class-level auth: `AllowAnonymous`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/v1/plugins/activate-license` | `Activate` |  |
| POST | `/api/v1/plugins/deactivate-license` | `Deactivate` |  |
| POST | `/api/v1/plugins/check-update` | `CheckUpdate` |  |
| GET | `/api/v1/plugins/download` | `Download` |  |
| GET | `/api/v1/plugins/changelog` | `Changelog` |  |
| POST | `/api/v1/plugins/heartbeat` | `Heartbeat` |  |
| POST | `/api/v1/plugins/telemetry` | `Telemetry` |  |

### ReportsController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Reports/shipments-over-time` | `GetShipmentsOverTime` |  |
| GET | `/api/Reports/tenant-performance` | `GetTenantPerformance` |  |
| GET | `/api/Reports/status-breakdown` | `GetStatusBreakdown` |  |
| GET | `/api/Reports/orders-summary` | `GetOrdersSummary` |  |
| GET | `/api/Reports/bookings-by-hour` | `GetBookingsByHour` |  |
| GET | `/api/Reports/success-rate` | `GetSuccessRate` |  |

### ShipmentsController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/Shipments/create/{orderRef}` | `CreateShipment` |  |
| GET | `/api/Shipments/{shipmentId}` | `GetShipment` |  |
| POST | `/api/Shipments/{shipmentId}/resync-storefront` | `ResyncStorefront` |  |
| GET | `/api/Shipments/{shipmentId}/tracking` | `GetTracking` |  |
| GET | `/api/Shipments/{shipmentId}/label` | `GetShipmentLabel` |  |
| GET | `/api/Shipments` | `GetShipments` |  |
| GET | `/api/Shipments/stats` | `GetStats` |  |
| GET | `/api/Shipments/orders/unshipped` | `GetUnshippedOrders` |  |

### GetRatesController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/public/shipping/rates` | `GetRates` |  |

### ShopifyRatesController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/shopify/rates/{storeId:int}` | `GetRates` |  |

### ShopifyController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Shopify/test-connection/{storeId}` | `TestConnection` |  |
| POST | `/api/Shopify/fetch-orders` | `FetchOrders` |  |
| GET | `/api/Shopify/get-order/{storeId}/{shopifyOrderId}` | `GetOrder` |  |
| PUT | `/api/Shopify/update-order-status/{storeId}/{shopifyOrderId}` | `UpdateOrderStatus` |  |
| POST | `/api/Shopify/add-tracking-info/{storeId}/{shopifyOrderId}` | `AddTrackingInfo` |  |
| GET | `/api/Shopify/stores` | `ListStores` | Authorize(Roles = "SuperAdmin") |
| POST | `/api/Shopify/register-carrier-service/{storeId}` | `RegisterCarrierService` | Authorize(Roles = "SuperAdmin") |
| GET | `/api/Shopify/carrier-services/{storeId}` | `ListCarrierServices` | Authorize(Roles = "SuperAdmin") |
| POST | `/api/Shopify/register-webhooks/{storeId}` | `RegisterWebhooks` | Authorize(Roles = "SuperAdmin") |
| POST | `/api/Shopify/full-registration/{storeId}` | `FullRegistration` | Authorize(Roles = "SuperAdmin") |
| POST | `/api/Shopify/fetch-all-orders` | `FetchAllOrders` |  |

### ShopifyWebhookController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/webhooks/shopify/order` | `ReceiveShopifyOrder` |  |
| GET | `/api/webhooks/shopify/health` | `HealthCheck` |  |

### StoresController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Stores` | `GetStores` |  |
| GET | `/api/Stores/{id}` | `GetStore` |  |
| POST | `/api/Stores` | `CreateStore` |  |
| PUT | `/api/Stores/{id}` | `UpdateStore` |  |
| DELETE | `/api/Stores/{id}` | `DeleteStore` |  |
| POST | `/api/Stores/{storeId}/test-webhook` | `TestWebhookConnection` |  |

### SuperAdminController

Class-level auth: `Authorize(Roles = "SuperAdmin")`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/SuperAdmin/stats` | `GetStats` |  |
| PUT | `/api/SuperAdmin/users/{userId}/role` | `UpdateUserRole` |  |
| PUT | `/api/SuperAdmin/users/{userId}/status` | `UpdateUserStatus` |  |

### TenantsController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Tenants` | `GetTenants` |  |
| GET | `/api/Tenants/{id}` | `GetTenant` |  |
| POST | `/api/Tenants` | `CreateTenant` |  |
| PUT | `/api/Tenants/{id}` | `UpdateTenant` |  |
| DELETE | `/api/Tenants/{id}` | `DeleteTenant` |  |

### TestShiplogicController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/testing/shiplogic/test-rates/{storeId}` | `TestGetRates` |  |

### AdminUsersController

Class-level auth: `Authorize(Roles = "Admin,SuperAdmin")`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/admin/users` | `CreateUser` |  |

### UsersController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/Users` | `GetUsers` |  |
| GET | `/api/Users/{id}` | `GetUser` | Authorize(Roles = "Admin,SuperAdmin") |
| PUT | `/api/Users/{id}` | `UpdateUser` | Authorize(Roles = "Admin,SuperAdmin") |
| DELETE | `/api/Users/{id}` | `DeleteUser` | Authorize(Roles = "Admin,SuperAdmin") |

### ShiplogicTrackingWebhookController

Class-level auth: `AllowAnonymous,AllowAnonymous`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/webhooks/shiplogic/tracking` | `ReceiveTrackingUpdate` |  |

### PluginWebhookController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/webhooks/plugin/order` | `ReceivePluginOrder` |  |
| POST | `/api/webhooks/plugin/label` | `DownloadLabel` |  |
| GET | `/api/webhooks/plugin/health` | `HealthCheck` |  |

### WebhookController

Class-level auth: `none`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| POST | `/api/webhooks/woocommerce/order` | `ReceiveOrderWebhook` |  |
| GET | `/api/webhooks/woocommerce/health` | `HealthCheck` |  |

### WooCommerceController

Class-level auth: `Authorize`

| Verb | Route | Action | Extra auth |
|---|---|---|---|
| GET | `/api/WooCommerce/test-connection/{storeId}` | `TestConnection` |  |
| POST | `/api/WooCommerce/fetch-orders` | `FetchOrders` |  |
| GET | `/api/WooCommerce/fetch-all-orders/{storeId}` | `FetchAllOrders` |  |
| GET | `/api/WooCommerce/orders/{storeId}/{wooOrderId}` | `GetOrder` |  |
| PUT | `/api/WooCommerce/orders/{storeId}/{wooOrderId}/status` | `UpdateOrderStatus` |  |
| POST | `/api/WooCommerce/orders/{storeId}/{wooOrderId}/tracking` | `AddTrackingInfo` |  |
