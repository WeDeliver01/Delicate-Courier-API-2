# Deliverable 9: Phased Implementation Plan

Each phase specifies objective, changes, dependencies, tests, deployment, rollback,
success criteria and risks, as the handover requires.

Phase numbering follows the handover. Two deviations are proposed and flagged.

---

## Phase 0 — Discovery

**Status: complete.** This document set. No production behaviour changed, no
ShipLogic code removed, no configuration touched.

**Exit criteria met:** architecture mapped, 106 endpoints inventoried, every
ShipLogic dependency located and categorised, database and identifier strategy
established, webhooks and jobs mapped, external integrations mapped, gap analysis
complete.

**Blocking question raised:** does a Delicate Engine exist outside this repository?
Phase 2 cannot be scoped until that is answered. See
[Deliverable 7](07-engine-gap-analysis.md).

---

## Phase 1 — Fork, contract tests, and the seam

**Status: fork complete** (this repository, `bdd7cda`). Remaining work below.

> **Deviation from the handover.** The handover's Phase 1 is fork-only, with the
> provider abstraction deferred to Phase 3. Recommend pulling the abstraction and
> routing forward into Phase 1, **while ShipLogic is still the only provider**.
> The seam then ships to production with zero behaviour change and is proven under
> real traffic long before the Engine exists. Done later, the seam and the new
> provider land together and a failure cannot be attributed to one or the other.

**Objective:** make the fork a safe place to work, pin the client contract, and
ship the provider seam with no behaviour change.

**Changes**

1. **Contract tests for the six frozen endpoints.** Record real request/response
   pairs from production for `POST /api/public/shipping/rates`,
   `POST /api/shopify/rates/{storeId}`, `POST /api/webhooks/plugin/order`,
   `POST /api/webhooks/plugin/label`, `POST /api/webhooks/woocommerce/order`,
   `POST /api/webhooks/shopify/order`. Assert status codes, response shape, and
   error structures. These tests are the definition of "did not break the clients".
2. `IShiplogicService` -> `IShipmentProvider`; `ShiplogicService` ->
   `ShiplogicProvider`. Mechanical rename, one implementation, no behaviour change.
3. `ProviderContext` replacing the per-call `bearerToken` parameter.
4. `ProviderRouter` with a global default of `shiplogic` and nothing else wired.
5. Migration `AddShipmentProvider`: `Shipment.Provider`, nullable, backfilled
   `"shiplogic"`.
6. Migration `AddClientProviderRouting`: `ProviderMode` + `MigrationStatus` on
   Tenant and Store, defaulting to `SHIPLOGIC` / `NOT_STARTED`.
7. Widen `GetShipmentLabelAsync` from `int shipmentId` to a string reference the
   system actually persists.
8. Correlation id per request, logged and returned in a response header.
9. `rate.quoted` and `shipment.provider_selected` system events.
10. Export the **current ShipLogic rate card** to a versioned fixture in this repo.
    This is the baseline every future pricing assertion compares against.

**Dependencies:** production access to capture contract fixtures; ShipLogic account
access to export the rate card.

**Tests:** the new contract tests pass against the fork; the existing 23 test files
still pass; a rename-only diff review confirms no logic changed.

**Deployment:** deployable to production as a no-op. Recommended: deploy it, because
an unexercised seam is not a proven seam.

**Rollback:** revert the deploy. Schema changes are additive and nullable, so they
can stay.

**Success criteria**
- Contract tests green and committed as fixtures
- `Shipment.Provider` populated on every new booking
- Rate card exported and committed
- Production behaviour byte-identical (verify: booking success rate and rate-quote
  success rate unchanged over a full trading week)

**Risks:** the rename touches ~25 files; a careless merge could alter logic. Keep it
as a mechanical commit with no behavioural change in the same commit.

---

## Phase 2 — Build the Engine

**Objective:** build the platform. This is the bulk of the project.

> **Deviation from the handover.** The handover frames Phase 2 as confirming the
> Engine has equivalent capabilities and filling gaps. In reality this is a
> greenfield build of a rate engine, a booking store, label generation, tracking,
> and driver operations. It should be planned and estimated as such.

**Gate before any code:** the rating model decision
([Deliverable 7](07-engine-gap-analysis.md)). Recommended: hybrid zone table with
per-km distance fallback, mirroring existing commercial behaviour so pricing does
not move as a side effect.

**Changes, in dependency order**

1. **Rating engine** — zones, per-client rate cards, service levels, minimums,
   surcharge framework (weekend, public holiday, capacity-constrained date) built
   as *conditions that exist but are switched off*, so the schema is right and the
   commercial change is a separate decision.
2. **Booking store** — shipments, addresses, contacts, parcels, references,
   lifecycle, status history.
3. **Lookup by customer reference** — exact-match semantics, replicating the strict
   false-positive guard in the current implementation.
4. **Label generation** — scannable barcode, tracking reference, addresses, parcel
   details, printable. Not in the handover's list; on the critical path because
   `POST /api/webhooks/plugin/label` is a frozen endpoint.
5. **Tracking and events** — status vocabulary as a verbatim superset of the live
   ShipLogic strings, confirmed against production data first.
6. **Cancellation.**
7. **Capacity and availability** — the source of truth for whether a date can be
   booked (handover section 31), enforced in the Engine, never in the API layer.
8. **Driver allocation and operational status capture** — large, and required
   before any real cutover, because a booking the Engine accepts must be
   deliverable.

**Tests:** unit tests on rating against the exported ShipLogic rate card fixture,
asserting the Engine reproduces current prices for a matrix of real routes. This is
the single most important test suite in the project.

**Deployment:** Engine deployed and reachable, no production traffic routed to it.

**Rollback:** nothing to roll back; nothing is routed yet.

**Success criteria**
- Engine reproduces the exported ShipLogic rate card within an agreed tolerance
  across a representative route matrix
- A shipment can be created, labelled, tracked and cancelled through internal calls
- Capacity rules enforced in the Engine
- Drivers can be allocated and can record collection and delivery

**Risks:** the rate card may not be exportable in a form that can be reproduced
exactly, in which case the acceptable tolerance becomes a commercial decision, made
explicitly and in writing rather than discovered after cutover.

---

## Phase 3 — Wire the Engine behind the seam

**Objective:** `DelicateEngineProvider` implementing `IShipmentProvider`,
registered but routed to nobody.

**Changes:** the provider implementation; Engine client registration mirroring the
ShipLogic topology (typed `HttpClient`, generous timeout spanning the Polly
pipeline, retry, **singleton circuit breaker at the `HttpClient` layer** — not
per-instance, for the reason recorded in `ShiplogicService`); diagnostics with the
same PII redaction as the ShipLogic path.

**Dependencies:** Phases 1 and 2.

**Tests:** the Phase 1 contract tests run a second time with the router forced to
`delicate`, against the Engine in a test environment. Same assertions, same
fixtures. **If the contract tests pass against both providers, the contract is
preserved** — that is the whole validation strategy in one sentence.

**Deployment:** deployable; global default still `shiplogic`.

**Rollback:** revert; nothing was routed.

**Success criteria:** contract tests green against both providers, using identical
fixtures.

---

## Phase 4 — Shadow rate comparison

**Objective:** measure Engine pricing against ShipLogic on real production traffic,
with no risk to bookings.

**Changes:** shadow mode on `GetRatesAsync` only, enqueued out of band so it cannot
add checkout latency or fail a quote. Emits `shipment.shadow_compared` with both
quotes and the delta. A dashboard of the distribution.

**Explicitly not shadowed:** `CreateShipmentAsync` and `CancelShipmentAsync`, ever.
See [Deliverable 8](08-migration-architecture.md).

**Dependencies:** Phase 3.

**Tests:** verify shadow failures are swallowed and never affect the client
response; verify no write path is reachable from the shadow call.

**Deployment:** enable per store, starting with one.

**Rollback:** config flag off.

**Success criteria:** rate deltas within the agreed tolerance across a full trading
week including a weekend, on a representative set of stores. Every outlier
explained, not averaged away.

**Risks:** a systematic delta discovered here is good news arriving at an awkward
time. Budget for it; it is cheaper now than after cutover.

---

## Phase 5 — Internal production validation

**Objective:** prove the full operational lifecycle on the Engine, with real
deliveries, before any external client.

**Changes:** none to code. Internal test accounts and real test deliveries.

**Validate end to end:** create, book, label, allocate a driver, collect, track,
deliver, complete — plus cancellation and failed delivery. The handover is right
that this is about the whole lifecycle, not the booking endpoint.

**Dependencies:** Phase 4 passed; driver operations working.

**Rollback:** not applicable; internal accounts only.

**Success criteria:** every lifecycle state reachable and correctly reflected on a
merchant storefront, including the `delivered -> completed` Woo transition and the
exactly-once customer-visible tracking note.

---

## Phase 6 — Controlled client migration

**Objective:** move real clients, one cohort at a time, by configuration.

**Cohort order** — lowest blast radius first:

1. One low-volume WooCommerce store on the current plugin build
2. Two or three more WooCommerce stores, mixed volume
3. One Shopify store (different ingestion path, separate write-back job)
4. Remaining WooCommerce
5. Anchor clients last. Honey Bee Baker, Baked by Nataleen and Cake Aways by
   Marone carry the most revenue and the most relationship risk

**Changes:** flip `Store.ProviderMode` to `DELICATE` via the SuperAdmin portal. No
deploy. Every change writes a `SystemEvent`.

**Monitor per cohort, split by provider:** booking success rate, booking failure
reasons, rate-quote success rate, p95 latency, label generation success, tracking
event flow, storefront pushback success, and support contacts.

**Rollback:** flip `ProviderMode` back to `SHIPLOGIC`. No deploy, no client change.
Shipments already booked on the Engine stay on the Engine and must still be
deliverable and trackable — this is the one rollback that is not clean, and it is
why `Shipment.Provider` exists.

**Success criteria:** each cohort stable for a full trading week including a
weekend before the next is moved. No unexplained pricing change on any migrated
client.

**Risks:** the highest-risk phase. Hold the line on one cohort at a time; the
pressure to accelerate will be real once the first cohort looks fine.

---

## Phase 7 — Full API migration

**Objective:** all API clients on the Engine.

**Changes:** remaining stores flipped; global default changed to `delicate` only
once no store relies on the old default.

**Rollback infrastructure stays in place.** ShipLogic provider remains registered
and functional.

**Success criteria:** no production API traffic reaching ShipLogic for new bookings,
confirmed by `Shipment.Provider` and by ShipLogic-side request volume.

---

## Phase 8 — Remaining workflows

**Objective:** migrate everything that is not API-driven: portal bookings, manual
bookings, internal operations, and anything still touching the ShipLogic dashboard.

The exact scope needs mapping against how operations actually work day to day,
which is outside this codebase and should be a separate discovery.

---

## Phase 9 — Decommission

**Only after confirming:** no production API traffic depends on ShipLogic; no manual
workflow does; no webhook, background job or report does; historical data preserved;
client integrations stable; rollback window elapsed.

**Then remove, in this order**

1. Stop routing. Confirm zero ShipLogic traffic for a full month.
2. Delete `TestShiplogicController`, the `shiplogic-webhook` admin endpoint, and the
   frontend pages for account/provider/token.
3. Delete `ShiplogicProvider`, `ShiplogicSettings`, `ShiplogicCallException`, and
   the ShipLogic DTOs. Rename remaining ShipLogic-flavoured log lines and the
   `"Shiplogic"` default courier-name fallback.
4. Retire `POST /api/webhooks/shiplogic/tracking` — last, because it is the final
   piece of the rollback path.
5. Migration `DropShiplogicCredentials`: drop `Store.ShiplogicProviderId`,
   `Store.ShiplogicAccountId`, `Tenant.ShiplogicBearerToken`.
   **Retain `Shipment.ConsignmentID` and `Shipment.TrackingNumber`** as historical
   provider references — they are customer-visible on past orders.
6. Revoke ShipLogic credentials. Close the account last of all.

**Rollback:** after step 3, there is none. Treat step 3 as the point of no return
and do not take it on a Friday.
