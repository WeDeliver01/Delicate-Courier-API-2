# Deliverable 7: Delicate Engine Gap Analysis

This is the deliverable that should change the project plan.

## The finding

**The Delicate Engine does not exist in this repository.** A sweep for any engine,
provider-abstraction, driver, capacity, availability, rate-card, surcharge or
holiday-calendar type returns nothing. There is no partial implementation, no
scaffold, and no interface waiting for one.

The handover's Phase 2 is written as *"before replacing ShipLogic calls, confirm
that the Engine has equivalent capabilities"*, which presumes something to confirm
against. Within this codebase there is nothing. If an Engine exists in another
repository or as part of another venture, **that repository needs to be attached
before Phase 2 can be scoped**, because this analysis cannot assess what it cannot
read. Everything below assumes a greenfield Engine; revise if that assumption is
wrong.

## The second finding, which is larger

The business rules the handover asks the migration to *preserve* mostly **do not
exist to be preserved**.

The handover (sections 29 to 31) lists standard rates, client-specific pricing,
membership pricing, weekend surcharges, public holiday surcharges, capacity
restrictions, same-day booking rules, service levels, liability, and payment
rules. Here is what the code actually contains:

| Business rule | Where it lives today | Status |
|---|---|---|
| Base delivery rate | ShipLogic `/v2/rates` | **External.** Nothing to port — must be built |
| Client-specific pricing | ShipLogic account rate card | **External.** Must be built |
| Membership tier pricing | Not in this codebase | **Does not exist in code** |
| Merchant markup | WooCommerce plugin, client side | Exists, but merchant-side. Stays where it is |
| Special-trip (per-km) rate | `SpecialTripQuoter` x `Store.SpecialTripCostPerKm` | **The only Delicate-owned pricing that exists** |
| Service level | `Store.DefaultServiceLevel` (`"STD"`), filtered in C# | Exists as a label; the pricing behind it is ShipLogic's |
| Checkout rate label | `Store.CheckoutRateLabel` | Exists |
| Weekend surcharge | nowhere | **Does not exist** |
| Public holiday surcharge | nowhere | **Does not exist** |
| Capacity / availability | nowhere | **Does not exist** |
| Same-day booking rules | nowhere | **Does not exist** |
| Collection/delivery windows | `Store.CollectionTimeFrom/To`, `DeliveryTimeFrom/To` | Exist as per-store strings, not enforced as capacity |
| Liability / protection | not implemented | **Does not exist** |
| Payments / transactions | not implemented | **Does not exist** |
| Driver allocation | nowhere | **ShipLogic's operation entirely** |

So the Engine is not replacing a rate engine. **It is building the first one.** The
project is therefore better understood as:

> *Build Delicate's courier platform, then route the existing API at it.*

rather than

> *Swap a provider behind an existing abstraction.*

The abstraction swap is the easy half and it is largely already done, because
`IShiplogicService` exists and is clean. The hard half is the platform behind it,
and the handover's phase numbering understates it.

## Gap table

Format as the handover specifies: capability, current ShipLogic implementation,
required Engine capability, current Engine status, work required, priority.

### Required for the API migration (the six frozen endpoints)

| Capability | ShipLogic today | Engine must provide | Engine status | Work | Priority |
|---|---|---|---|---|---|
| **Rate calculation** | `POST /v2/rates`, account rate card, returns all service levels | Rate engine: zones or distance, per-client rate cards, service levels, surcharges, weekend/holiday, minimums | **Does not exist** | **Large. Critical path.** Needs a commercial decision on the rating model before any code | **Critical** |
| **Shipment creation** | `POST /shipments` | Booking endpoint accepting the mapped payload, returning shipment id, tracking reference, status, rate, estimated delivery, collection branch | **Does not exist** | Medium | **Critical** |
| **Idempotent lookup** | `GET /shipments?customer_reference=` | Lookup by customer reference, exact-match semantics | **Does not exist** | Small, but **must replicate the strict false-positive guard** | **Critical** |
| **Label generation** | `GET /shipments/{id}/label` returns a ShipLogic-produced PDF | **Generate** a scannable courier label: barcode, tracking ref, addresses, parcel details, printable | **Does not exist** | **Medium-large, and not in the handover's capability list** | **Critical** (one of the six frozen endpoints) |
| **Status / tracking** | `GET /v2/tracking/shipments` + inbound webhook | Shipment status, event history, status vocabulary that is a **superset of ShipLogic's current strings** | **Does not exist** | Medium | **Critical** |
| **Cancellation** | `POST /v2/shipments/cancel` | Cancel by reference | **Does not exist** | Small | High |
| **Account context** | Per-tenant bearer token | Internal service auth; client credentials stay at the API edge | **Does not exist** | Small. Simpler than today | High |
| **Address handling** | ShipLogic geocoded (badly) | Validation + coordinates. Delicate already geocodes and caches | **Partially exists** (`CachedGeocoder`, `GeocodeCache`) | Small — reuse | Medium |

### Required for operations, but not for the API cutover

These are needed to actually run deliveries once the Engine owns bookings. They can
trail the API work but cannot trail the *cutover*, because a booking the Engine
accepts must be deliverable.

| Capability | Engine status | Work | Priority |
|---|---|---|---|
| Driver allocation and dispatch | Does not exist | **Large** | Critical before any real cutover |
| Collection / delivery status capture by drivers | Does not exist | Large | Critical before cutover |
| Capacity and availability model | Does not exist | Medium-large | High (handover section 31) |
| Weekend / holiday calendar as pricing + availability conditions | Does not exist | Medium (handover section 30) | High |
| Payments and transactions | Does not exist | Large | Deferred — not required for the API migration |
| Reporting | Partially exists (`ReportsController`, 6 endpoints over local data) | Small — repoint | Low |

## What already exists and should be reused, not rebuilt

Worth being explicit, because it is a meaningful head start:

- **`IShiplogicService` is already a clean provider abstraction.** The handover's
  section 16 asks whether one needs introducing. It effectively exists. Renaming it
  to `IShipmentProvider` and adding a second implementation is a small, safe
  change that can ship in Phase 1.
- **Three-layer idempotency** (local check, `pg_advisory_xact_lock`, cross-system
  reference lookup) is built and battle-tested. Keep it verbatim.
- **Hangfire retry semantics** with the deliberate throw-vs-return-Failure
  distinction: transient errors throw so they retry, permanent errors return
  Failure so they do not. This distinction is correct and hard-won.
- **The guard sequence** at the single booking chokepoint, with three layers of
  collect detection, terminal-status refusal, and special-trip handling.
- **Geocoding with DB cache** — becomes a primary input rather than a workaround.
- **`ShipmentStorefrontMeta`** as the single translation point for status to
  merchant-visible labels.
- **Circuit breaker and retry topology**, including the specific reason the
  breaker is a singleton at the `HttpClient` layer.
- **`SystemEvent`** as an audit trail and idempotency marker.

## The decision that gates everything

**What is the Engine's rating model?** Nothing can be built until this is decided,
because it determines the schema, the data Delicate must collect, and the
checkout latency budget:

- **Zone-based** (suburb or postal-code bands to price bands). Fast, cacheable, no
  third-party call in the hot path, predictable. Needs a zone table built and
  maintained for Gauteng.
- **Distance-based** (Google Distance Matrix x per-km, extending the existing
  special-trip model). Already proven in code, already priced per km, but puts a
  third-party call in every checkout quote and raises the billing question in
  [Deliverable 6](06-external-integrations.md).
- **Hybrid** — zone table for known coverage, distance fallback beyond it. This
  matches how the business already behaves: ShipLogic rates inside coverage,
  per-km special trip outside it. **This is the recommended model** precisely
  because it mirrors existing commercial behaviour, which satisfies the handover's
  requirement not to change pricing as a side effect of the migration.

Alongside it, two commercial inputs are needed that are not in the code and cannot
be derived from it:

1. The **current rate card as configured in ShipLogic**, exported so the Engine can
   reproduce it exactly. Without this there is no way to prove the migration did
   not change pricing, which is the handover's section 29 requirement.
2. The **membership tier definitions** (the record of a Starter/Growth/Enterprise
   model exists operationally but not in this codebase) and how they should
   interact with per-client rate cards.

Until the rate card is exported and the rating model chosen, Phase 2 cannot be
estimated. That export is the recommended immediate next action after this
discovery is approved.
