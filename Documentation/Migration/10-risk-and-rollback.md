# Deliverable 10: Risk Register and Rollback Plan

## Rollback by phase

| Phase | Rollback mechanism | Clean? | Client action needed |
|---|---|---|---|
| 1 Seam + contract tests | Revert deploy. Schema changes additive and nullable, leave them | Yes | None |
| 2 Engine build | Nothing routed, nothing to roll back | Yes | None |
| 3 Engine behind the seam | Revert deploy. Nothing routed | Yes | None |
| 4 Shadow rates | Config flag off | Yes | None |
| 5 Internal validation | Internal accounts only | Yes | None |
| 6 Client cohorts | Flip `Store.ProviderMode` to `SHIPLOGIC`. No deploy | **Partially** — see below | **None** |
| 7 Full migration | Flip global default back, flip stores back | Partially | None |
| 8 Remaining workflows | Per-workflow | Varies | None |
| 9 Decommission | **None after ShipLogic code is deleted** | **No** | None |

**No phase requires a client to change their integration to roll back.** That is the
handover's section 21 requirement and the architecture satisfies it, because routing
is server-side configuration behind a frozen contract.

## The one rollback that is not clean

Phase 6 and 7. Flipping `ProviderMode` back to `SHIPLOGIC` stops *new* bookings
going to the Engine. It does nothing about shipments already booked there.

Those shipments must remain, for their full lifecycle:

- deliverable — a driver must still collect and deliver them
- trackable — the customer has a tracking reference in their inbox
- labelled — the merchant may not have printed the label yet
- status-syncing — the Woo order still needs to reach `completed`

So a rollback leaves the business running **both** systems for as long as the
longest in-flight shipment, and Delicate's operations team has to work two
dispatch surfaces simultaneously.

Mitigations:

1. `Shipment.Provider` (Phase 1) makes ownership explicit per row. Without it,
   reconciliation means guessing from tracking-number format.
2. Dispatch tooling must show the provider on every shipment **before** Phase 6, not
   after a rollback forces it.
3. Migrate cohorts when in-flight volume is lowest, and never immediately before a
   peak trading period.
4. Define a rollback decision window: if the Engine is going to fail for a cohort,
   it should fail within the first hours, not the second week. Agree the monitoring
   thresholds and the person who can call it before the cohort moves.

## Risk register

Ordered by expected cost, not likelihood.

### R1 — Pricing changes silently during migration

**Severity: critical. Likelihood: high if unmanaged.**

The Engine's rating is a new implementation of commercial logic that currently lives
in a ShipLogic rate card nobody in this codebase can see. Any divergence directly
changes what customers are charged at checkout, and the handover (section 29) is
explicit that this must not happen as a side effect.

Mitigation: export the rate card in Phase 1 and commit it as a fixture. Make
"reproduces the fixture across a representative route matrix" the Phase 2 exit
criterion. Shadow rate comparison in Phase 4 on real traffic including a weekend.
Agree the acceptable tolerance in writing, as a commercial decision, before Phase 6.

**This is the project's defining risk.** Everything else is recoverable.

### R2 — Duplicate bookings

**Severity: critical. Likelihood: low, if the existing design is preserved.**

Two real courier bookings for one customer order means two drivers, two costs, and
a customer-facing mess.

Mitigation: never shadow `CreateShipmentAsync`. Preserve all three idempotency
layers verbatim. Replicate the Engine lookup's strict exact-match guard, including
the array-only, reference-must-match behaviour that prevents an unfiltered list
response attaching an unrelated shipment. Test the crash window explicitly: kill the
process between Engine create and local save, then let Hangfire retry, and assert
one shipment.

### R3 — The Engine is not operationally ready when the API is

**Severity: critical. Likelihood: medium.**

The API migration is the visible work and the easier half. Drivers, dispatch, and
operational status capture are the harder half and are not currently anywhere. A
booking the Engine accepts but cannot deliver is worse than no migration.

Mitigation: Phase 5 gates on the full lifecycle, not the booking endpoint. Treat
driver operations as a Phase 2 deliverable with equal weight to rating, and resist
the sequencing where the API is ready and waiting on operations.

### R4 — Merchant-visible status vocabulary changes

**Severity: high. Likelihood: medium. Easy to miss.**

ShipLogic's status strings already reach merchants through `_dcp_shipment_status`
meta and the `delivered -> completed` Woo mapping. Merchant automation may key on
them. This is not an API contract and will not be caught by endpoint tests.

Mitigation: query production for the distinct live values of
`Shipment.ShipmentStatus` and `TrackingEvent.EventType` before freezing anything.
Make the Engine's vocabulary a verbatim superset. Keep
`ShipmentStorefrontMeta.StatusLabel` as the single translation point.

### R5 — Label generation underestimated

**Severity: high. Likelihood: medium.**

Not in the handover's capability list, but `POST /api/webhooks/plugin/label` is one
of the six frozen client endpoints. Delicate must generate scannable labels, not
proxy them.

Mitigation: scope it explicitly in Phase 2. Validate with real printing and real
scanning, not a PDF that looks right on screen.

### R6 — Refactoring the orchestrator loses hard-won incident fixes

**Severity: high. Likelihood: medium.**

`ShipmentOrchestrationService` encodes at least five production incidents: the
collect order booked through a second trigger path, the
`NpgsqlRetryingExecutionStrategy` bug that silently failed booking jobs for 24h at a
time, the 30s timeout behind order WC-49905, the unobserved
`NullReferenceException` in the mapper, and the unbooked-completed-orders gap
(#50829). The file is long and heavily commented and will look like a refactoring
opportunity.

Mitigation: change exactly one thing — the provider call goes through the router.
Treat the guard sequence, the advisory lock, the transaction boundary, the
throw-vs-return-Failure distinction, and the label-fetch placement as frozen. Review
any diff to this file against the incident comments.

### R7 — Distance Matrix becomes a hot-path dependency

**Severity: medium. Likelihood: high if distance-based rating is chosen.**

Today it serves only the special-trip fallback. If it prices every quote, a
third-party outage takes down all rating, latency enters every checkout, and the
merchant-pays-for-their-own-key model needs revisiting.

Mitigation: cache distances on rounded coordinate pairs; zone table as the primary
path with distance as fallback (the recommended hybrid model); explicit decision on
whose API key pays.

### R8 — Capacity enforcement lands in the wrong layer

**Severity: medium. Likelihood: medium.**

The handover (section 31) is explicit that the Engine owns availability. Under
delivery pressure it is tempting to add a quick date check in the API or the plugin.

Mitigation: Engine is the source of truth; the API only relays its answer. No
capacity rule in the integration layer, ever.

### R9 — ShipLogic webhook secret is unset in production

**Severity: medium. Likelihood: unknown — worth checking today.**

If `Shiplogic__WebhookSecret` is unset, the tracking webhook accepts unauthenticated
requests with only a log warning, meaning anyone who can reach
`webhooks2.delicatecourier.co.za` can post arbitrary status updates for any shipment
whose consignment id or tracking number they can guess.

Mitigation: verify it is set, before anything else in this project. Make the Engine's
event ingress fail closed.

### R10 — Fork drifts from production during a long migration

**Severity: medium. Likelihood: high.**

Production will keep shipping plugin hardening, client onboarding and bug fixes while
this migration runs. A fork that diverges for months becomes unmergeable.

Mitigation: this repository has `upstream` pointing at production. Merge upstream on
a fixed cadence — weekly — rather than at the end. Keep the fork's own changes
narrow and additive so merges stay cheap. Any production fix to
`ShipmentOrchestrationService` must be merged, not reimplemented.

### R11 — Business-rule changes smuggled in with the migration

**Severity: medium. Likelihood: high.**

The Engine makes weekend pricing, holiday pricing and capacity possible for the
first time. The temptation to enable them in the same release as the cutover will be
strong, and it would make any pricing complaint unattributable.

Mitigation: build the surcharge framework in Phase 2 with the conditions switched
off. Enabling them is a separate release after the migration is stable, with its own
commercial decision and its own announcement to merchants.

## The pre-cutover checklist

Everything below must be true before the first external client moves:

- [ ] Contract tests pass against both providers, from the same fixtures
- [ ] Exported ShipLogic rate card committed; Engine reproduces it within the agreed
      written tolerance
- [ ] Shadow rate comparison clean for a full trading week including a weekend
- [ ] Full lifecycle validated on internal accounts: create, book, label, allocate,
      collect, track, deliver, complete, plus cancel and failed delivery
- [ ] Labels print and scan
- [ ] Status vocabulary confirmed as a superset of the live production values
- [ ] `Shipment.Provider` populated on every booking
- [ ] Dashboard showing booking success, rate success and p95 latency **split by
      provider**
- [ ] Dispatch tooling shows which provider owns each shipment
- [ ] `Shiplogic__WebhookSecret` set in production; Engine event ingress fails closed
- [ ] Rollback rehearsed: flip a store to `DELICATE`, book, flip back, confirm the
      in-flight shipment still delivers and tracks
- [ ] Rollback decision thresholds agreed, and the person who can call it named

The rollback rehearsal is the one most likely to be skipped and the one most likely
to matter.
