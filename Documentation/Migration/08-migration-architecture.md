# Deliverable 8: Recommended Migration Architecture

Designed against what the code actually contains, not against the handover's
assumed starting point. The guiding constraint is the handover's own:
**preserve the client experience, replace the infrastructure underneath it.**

## The seam already exists

The single most important architectural fact: `IShiplogicService` is already a
clean provider boundary. All eight ShipLogic operations go through it, nothing
bypasses it, and no ShipLogic type leaks past it except the DTOs in
`Features/Shiplogic/DTOs/`.

So the architecture is not "introduce an abstraction". It is "rename the one that
exists, add a second implementation behind it, and choose per request which one
runs".

```
                 Client (plugin / Shopify / Woo webhook)
                                  |
                      frozen contract: 6 endpoints
                                  |
                                  v
            +-------------------------------------------+
            |            Delicate .NET API              |
            |                                           |
            |  Authentication (Supabase JWT / secrets)  |
            |  Validation                               |
            |  OrderToShipmentMapper (translation)      |
            |  ShipmentOrchestrationService             |
            |    guards, advisory lock, idempotency     |
            |                   |                       |
            |                   v                       |
            |          IShipmentProvider                |
            |          (was IShiplogicService)          |
            |          + ProviderRouter                 |
            +-------------------------------------------+
                      |                      |
         +------------+                      +-------------+
         v                                                 v
  ShiplogicProvider                              DelicateEngineProvider
  (unchanged, 8 methods)                         (new, same 8 methods)
         |                                                 |
         v                                                 v
  api.shiplogic.com                               Delicate Engine
                                                     |
                                      +--------------+--------------+
                                      v              v              v
                                  Bookings        Rating        Tracking
                                      |              |              |
                                      +--------------+--------------+
                                                     |
                                              Delicate Database
                                                     |
                                              Delicate Operations
                                              (drivers, dispatch)
```

## Component plan

### 1. Rename the interface, keep the shape

`IShiplogicService` -> `IShipmentProvider`, with the eight methods unchanged except
for dropping the per-call `bearerToken` parameter, which is a ShipLogic-specific
credential that does not belong in a provider-neutral contract. Replace it with a
resolved provider context.

```csharp
public interface IShipmentProvider
{
    string ProviderKey { get; }   // "shiplogic" | "delicate"

    Task<CreateShipmentResponse> CreateShipmentAsync(ProviderContext ctx, CreateShipmentRequest r, CancellationToken ct = default);
    Task<CreateShipmentResponse?> FindShipmentByCustomerReferenceAsync(ProviderContext ctx, string customerReference, CancellationToken ct = default);
    Task<(RateResponseDto Rates, ProviderDiagnostics Diagnostics)> GetRatesWithDiagnosticsAsync(ProviderContext ctx, AddressDto collection, AddressDto delivery, List<ParcelDto> parcels, string? serviceLevelCode = null, CancellationToken ct = default);
    Task<RateResponseDto> GetRatesAsync(...);
    Task<byte[]?> GetShipmentLabelAsync(ProviderContext ctx, string shipmentRef, CancellationToken ct = default);
    Task<TrackingResponse> GetTrackingUpdatesAsync(ProviderContext ctx, List<string>? refs = null, CancellationToken ct = default);
    Task<bool> CancelShipmentAsync(ProviderContext ctx, string trackingReference, CancellationToken ct = default);
}
```

`ProviderContext` resolves per store/tenant: the ShipLogic implementation reads
`Tenant.ShiplogicBearerToken`, `Store.ShiplogicAccountId` and
`Store.ShiplogicProviderId` from it; the Engine implementation ignores all three
and uses internal service auth.

Note `GetShipmentLabelAsync` currently takes an `int shipmentId` — ShipLogic's
numeric id, which **is never persisted**, so a later label fetch cannot reproduce
it. Widening this to a string reference the system actually stores is a small fix
that the migration needs anyway.

### 2. Provider routing

A `ProviderRouter` resolving the provider for a given store, in this precedence
order:

```
1. Explicit per-request override   (internal test calls only, never client-settable)
2. Store.ProviderMode              (most specific wins)
3. Tenant.ProviderMode
4. Global default                  (config, "shiplogic" until the end of Phase 7)
```

With the migration state machine the handover suggests, as a column on Tenant and
Store:

```
MigrationStatus: NOT_STARTED | SHADOW | READY | MIGRATED | ROLLBACK
ProviderMode:    SHIPLOGIC | DELICATE
```

`MigrationStatus` is the operational narrative; `ProviderMode` is the switch the
router actually reads. Keeping them separate means a rollback flips one field and
leaves an audit trail in the other.

**Routing is configuration, not code.** The handover is explicit (section 25) that
clients move by configuration. That means these columns must be editable from the
SuperAdmin portal, with every change writing a `SystemEvent`.

### 3. Shadow mode, built so it cannot double-book

The handover's section 17 warning is the one that matters most:
**never create two real courier bookings for one customer shipment.**

The safe way to get that guarantee is structural rather than procedural. Shadow
mode must be restricted to operations that have **no side effects by
construction**:

| Operation | Shadow-able | Why |
|---|---|---|
| `GetRatesAsync` | **Yes** | Pure read. Compare price, service levels, latency. **This is where the validation value is** |
| `FindShipmentByCustomerReferenceAsync` | **Yes** | Pure read |
| `GetTrackingUpdatesAsync` | **Yes** | Pure read |
| `CreateShipmentAsync` | **No. Never.** | Creates a real booking. No "dry run" flag is trustworthy enough to risk it |
| `CancelShipmentAsync` | **No** | Mutating |
| label fetch | No value | Pointless without a shipment |

So shadow mode compares **rates only**, and that is sufficient: rate divergence is
the signal that the Engine's commercial logic differs from ShipLogic's, which is
precisely the risk the handover's section 29 is about. Booking correctness is
proven in Phase 5 with internal test accounts, not by shadowing production traffic.

Shadow comparison runs **out of band** — enqueued, not inline — so it cannot add
latency to a customer's checkout or fail a quote. Result goes to a
`shipment.shadow_compared` system event with quoted amounts from both providers and
the delta.

### 4. Preserve the orchestrator exactly

`ShipmentOrchestrationService` should change in **one** place: the provider call
goes through the router. Everything else stays byte-for-byte:

- the guard sequence and its order
- the `pg_advisory_xact_lock(orderId)` inside the execution strategy
- the cross-system idempotency lookup before create
- the throw-vs-return-Failure distinction that drives Hangfire retry
- label fetch outside the transaction
- storefront pushback, including the `TrackingPushedOn` gate

Each of these encodes a production incident. The code comments name them: the
collect order that slipped through a second trigger path, the
`NpgsqlRetryingExecutionStrategy` bug that silently failed every booking job for
24h at a time, order WC-49905 and the 30s timeout, incident #50829. A migration
that refactors this file is a migration that re-learns those lessons.

### 5. Status vocabulary as a compatibility layer

Per [Deliverable 3](03-shiplogic-dependency-map.md), ShipLogic's status strings are
already visible to merchants through `_dcp_shipment_status` meta and the
`delivered -> completed` Woo mapping.

The Engine's statuses must be a **superset containing the existing strings
verbatim**. `ShipmentStorefrontMeta.StatusLabel` stays the single translation
point. Before freezing the vocabulary, query production `Shipment.ShipmentStatus`
and `TrackingEvent.EventType` for their distinct values — the webhook writes
`payload.Status` through unmodified, so the live set is whatever ShipLogic actually
sends, which may be wider than the entity documentation suggests.

### 6. Observability

The handover's section 20 correlation chain, mapped to what exists:

| Link | Exists today | Gap |
|---|---|---|
| Client request | log lines | No request id surfaced to clients |
| API request id | — | **Missing.** Add a correlation id, return it in a response header |
| Client account | `Order.StoreID`, `TenantID` | Present |
| External reference | `Order.WooOrderID`, `customer_reference` | Present |
| Delicate shipment id | `Shipment.ShipmentID` | Present |
| Provider used | — | **Missing.** `Shipment.Provider` column, Phase 1 |
| Payment transaction | — | Not implemented at all |
| Operational shipment | — | Does not exist until the Engine does |

Minimum additions before any client moves:

- `Shipment.Provider` discriminator (also serves rollback and reconciliation)
- a correlation id per request, logged and returned
- `rate.quoted` system event — the rate path is the highest-volume client
  operation and currently leaves no audit trail
- `shipment.provider_selected` on every booking, recording the routing decision
- a dashboard comparing booking success rate, rate-quote success rate, and p95
  latency **split by provider**. Without the split, a regression in the migrated
  cohort is invisible inside the aggregate

### 7. Security

Per handover section 32, unchanged from today's correct posture: client credentials
terminate at the API edge; the Engine is reached with internal service auth that no
client ever sees. Two specific improvements this project should make rather than
port:

- The Engine's event ingress must **fail closed**. The current ShipLogic tracking
  webhook accepts unauthenticated requests with only a warning when
  `Shiplogic:WebhookSecret` is unset.
- Carry over the PII redaction on the rates debug block
  (`RedactRequestJson` / `RedactAddressNode`, 8KB truncation, omit-on-failure) to
  the Engine diagnostics. A single global debug secret guards it.

## What this architecture deliberately does not do

- **No client-facing API versioning.** The contract does not change, so there is
  nothing to version. Introducing `/v2/` would signal a breaking change that is not
  happening and would invite client migration work the handover explicitly wants to
  avoid.
- **No big-bang switch.** The global default stays `shiplogic` until the last
  cohort is migrated.
- **No removal of ShipLogic code** until Phase 9. The provider stays registered and
  working throughout, because it is the rollback path.
- **No business-rule changes smuggled in.** Weekend and holiday pricing are new
  capabilities the Engine makes *possible*; turning them on is a separate
  commercial decision with its own release, after the migration is stable. The
  handover's section 29 is explicit about separating technology migration from
  business-rule change, and this is the place that discipline is easiest to lose.
