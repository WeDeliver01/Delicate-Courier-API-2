# Deliverable 4: Database Map and Identifier Strategy

Database: **Supabase-managed Postgres**, accessed via EF Core + Npgsql with
`EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: 5s)`. Hangfire uses the same
Postgres instance for job storage via `Hangfire.PostgreSql`, in a separate
connection pool. 38 applied migrations at `bdd7cda`.

## Tables

21 `DbSet`s in `Data/AppDbContext.cs`, in three groups.

**Core courier domain (the migration's concern)**

| Entity | Role |
|---|---|
| `Tenant` | Top-level account. Owns the ShipLogic bearer token today |
| `Store` | A merchant storefront. Owns collection address, coordinates, service level, special-trip pricing, Woo/Shopify credentials |
| `User` | Portal user, tied to a Tenant. Auth via Supabase JWT |
| `Order` | Ingested storefront order |
| `OrderLineItem` | Line items, used for parcel weight/sizing |
| `Shipment` | One per Order. **Carries the ShipLogic consignment and tracking number** |
| `Label` | Label PDF bytes/URL for a Shipment (one-to-one) |
| `TrackingEvent` | Status events for a Shipment, deduped by `ExternalEventId` |
| `PackageType` | Per-store parcel presets |
| `PackageMappingRule` | Product -> package type rules |
| `SystemEvent` | Audit trail. Also used as an idempotency marker |

**Plugin distribution platform (unaffected by this migration)**

`PluginProduct`, `PluginRelease`, `PluginLicense`, `PluginInstallation`,
`PluginTelemetryEvent`, `PluginFeatureFlag`, `PluginRolloutRule`,
`PluginAuditLog`, `PluginDebugLog`.

**Infrastructure**

`DataProtectionKeys` (ASP.NET Data Protection), plus a geocode cache table
referenced by `GeocodeCacheStore`.

## Tenant isolation

Global query filters on tenant-scoped entities:
`BypassTenantFilter || e.TenantID == CurrentTenantId`, where `CurrentTenantId`
resolves from the Supabase JWT claim `tenant_id` (also accepts `TenantId`).

This matters for the migration in one specific way: **webhook paths have no JWT**,
so they use `IgnoreQueryFilters()` explicitly (see
`ShiplogicTrackingWebhookController`). Any Engine event consumer must do the same
or it will silently fail to find rows.

## Every column that carries ShipLogic identity

This is the complete list. Six columns, across three tables.

| Table | Column | Type | Contains | Fate at decommission |
|---|---|---|---|---|
| `Shipment` | `ConsignmentID` | `string` | ShipLogic's shipment id, stringified. Webhook lookup key | **Retain as historical provider reference.** Do not reuse for Engine ids |
| `Shipment` | `TrackingNumber` | `string` | ShipLogic short tracking reference (e.g. `RPLNWM`). Secondary webhook lookup key | **Client-visible.** See below |
| `Store` | `ShiplogicProviderId` | `int` | ShipLogic provider id (35 for this account) | Drop |
| `Store` | `ShiplogicAccountId` | `int` | ShipLogic account id | Drop |
| `Store` | `DefaultServiceLevel` | `string` | ShipLogic service level code, default `"STD"` | **Keep the column, redefine the vocabulary.** Client-visible via the plugin's rate filter |
| `Tenant` | `ShiplogicBearerToken` | `string?` | Per-tenant ShipLogic API credential | Drop. Engine uses internal service auth, not per-tenant provider tokens |

### What this means

The handover (section 15) warns about ShipLogic IDs being load-bearing core
identifiers and distinguishes external client reference, ShipLogic ID, and
Delicate shipment ID. **That separation already exists correctly in this schema:**

```
Order.OrderID            int, Delicate-owned PK
Shipment.ShipmentID      int, Delicate-owned PK          <- already the system identity
Shipment.ConsignmentID   string, ShipLogic's id          <- a plain data column, not a key
Shipment.TrackingNumber  string, ShipLogic's short ref   <- a plain data column, not a key
Order.WooOrderID         string, the merchant's order id  <- external client reference
customer_reference       "WC-{WooOrderID}", computed, never stored
```

No ShipLogic value is a primary key, a foreign key, or part of a unique
constraint. `ConsignmentID` and `TrackingNumber` are used only as **lookup keys in
the inbound webhook handler**, which is a query concern, not a schema constraint.

There is therefore **no identifier migration to perform**. This removes what the
handover anticipated would be one of the riskiest parts of the project.

## The one genuinely hard identifier question

`Shipment.TrackingNumber` is **client-visible and customer-visible**:

- pushed to WooCommerce as a customer-visible order note ("Your order has been
  shipped via …"), gated by `Shipment.TrackingPushedOn` so it is sent exactly once
- written to Woo order meta via `ShipmentStorefrontMeta`
- pushed to Shopify as a fulfilment tracking number by `ShopifyTrackingWriteback`
- returned by `GET /api/Shipments/{id}` and `GET /api/Shipments/{id}/tracking`
- the lookup key the inbound tracking webhook falls back to

So when the Engine issues tracking numbers, it is issuing a **customer-facing
identifier in a new format**, on an order whose merchant may already have the old
one on screen.

Recommended handling:

1. The Engine mints its own tracking reference in its own format. Do not imitate
   ShipLogic's format; that would make provenance unknowable during the parallel
   window.
2. Keep `ConsignmentID` populated with the Engine's internal shipment id, so the
   existing webhook lookup path keeps working with a one-line change.
3. **Never re-issue a tracking number for an already-booked shipment.** A shipment
   booked through ShipLogic keeps its ShipLogic tracking number for life, including
   after its client is migrated. Migration is per-client and forward-only: new
   bookings go to the Engine, existing shipments stay where they were created.
4. Add a `Provider` discriminator column to `Shipment` (`"shiplogic"` /
   `"delicate"`) in the **first** migration of this project, before any Engine
   work. Without it, reconciliation during the parallel window requires guessing
   from the tracking number format, and the rollback path cannot tell which system
   owns a given shipment.

Point 4 is the single most useful schema change available and it is additive,
nullable, and zero-risk. It should ship in Phase 1.

## Schema changes this project will need

All additive. No destructive change is required before decommission.

| Migration | Change | Phase |
|---|---|---|
| `AddShipmentProvider` | `Shipment.Provider` string, nullable, default `"shiplogic"` for existing rows | 1 |
| `AddClientProviderRouting` | `Tenant.ProviderMode` / `Store.ProviderMode` + `MigrationStatus` for per-client cutover | 1 |
| `AddEngineShipmentReference` | Engine's own shipment/booking id, if it needs to live alongside `ConsignmentID` | 3 |
| Rate engine tables | Rate cards, zones, surcharges, holiday calendar, capacity. **New build** — see [Deliverable 7](07-engine-gap-analysis.md) | 2 |
| `DropShiplogicCredentials` | Remove the four droppable columns above | 9, decommission only |

Do not edit the 38 existing migrations. `AddShiplogicCredentialsToStores` and
`AddShiplogicBearerTokenToTenant` are applied history.

## Data integrity notes carried over from the current design

Worth stating explicitly because the Engine must not regress them:

- Shipment creation and its DB row are committed in **one transaction**, holding a
  `pg_advisory_xact_lock(orderId)`, with the label fetch deliberately **outside**
  that transaction so a slow label call cannot replay the booking.
- The transaction must run inside `Database.CreateExecutionStrategy()`, because
  `NpgsqlRetryingExecutionStrategy` rejects user-initiated transactions. This bug
  previously caused every Hangfire shipment job to fail silently; the fix is load
  bearing and the comment in `ShipmentOrchestrationService` says so.
- Per-attempt state inside the strategy delegate is reset at the top, because the
  strategy may re-invoke the delegate on a transient DB failure.
- `TrackingEvent` dedup is a batched existence check on `ExternalEventId`, not an
  N+1, and it also guards against duplicates within a single payload.
