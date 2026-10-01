# ShipLogic Decoupling: Phase 0 Discovery

Status: **Discovery complete. No production behaviour changed. No ShipLogic code removed.**

This directory is the Phase 0 deliverable set for migrating Delicate Courier off
ShipLogic and onto the Delicate Engine, as specified in the engineering handover.

Everything here was derived by reading the code in this repository at commit
`bdd7cda` (forked from production `delicate-courier-api` @ `5d9507b`). Where the
handover described an intended architecture that does not exist in the code yet,
that is stated explicitly rather than assumed into existence.

## The documents

| # | File | Answers |
|---|------|---------|
| 1 | [01-current-architecture.md](01-current-architecture.md) | How a booking actually flows today, end to end |
| 2 | [02-endpoint-inventory.md](02-endpoint-inventory.md) | All 106 endpoints, and which 6 are the real client contract |
| 3 | [03-shiplogic-dependency-map.md](03-shiplogic-dependency-map.md) | Every ShipLogic touchpoint, categorised by severity |
| 4 | [04-database-and-identifiers.md](04-database-and-identifiers.md) | Which columns carry ShipLogic identity, and the identifier strategy |
| 5 | [05-webhooks-and-events.md](05-webhooks-and-events.md) | Inbound/outbound webhooks, background jobs, system events |
| 6 | [06-external-integrations.md](06-external-integrations.md) | Every third party the API talks to, not just ShipLogic |
| 7 | [07-engine-gap-analysis.md](07-engine-gap-analysis.md) | What the Engine must provide, and what does not exist anywhere yet |
| 8 | [08-migration-architecture.md](08-migration-architecture.md) | Provider abstraction, routing, shadow mode, observability |
| 9 | [09-phased-plan.md](09-phased-plan.md) | Phase-by-phase plan with success criteria |
| 10 | [10-risk-and-rollback.md](10-risk-and-rollback.md) | What can go wrong and how to get back |

## The three findings that should change the plan

**1. The client contract is far smaller than the API.**
There are 106 endpoints, but only **six** carry courier semantics to an external
client. The other 100 are the admin portal, plugin licensing, and reporting:
internal surfaces Delicate controls on both sides. The "preserve the contract"
constraint therefore applies to a small, well-defined surface, which makes this
migration substantially less risky than the endpoint count suggests. See
[Deliverable 2](02-endpoint-inventory.md).

**2. The database barely knows ShipLogic exists.**
Only **six columns** across the entire schema carry ShipLogic identity. There is
no ShipLogic ID used as a primary or foreign key anywhere. `Shipment.ShipmentID`
is already a Delicate-owned identity, and ShipLogic's own numeric shipment ID is
never even persisted. The handover's concern about ShipLogic IDs being load-bearing
core identifiers does not apply here. See [Deliverable 4](04-database-and-identifiers.md).

**3. The commercial logic the Engine is supposed to own does not exist yet,
anywhere in this codebase.**
This is the largest finding and it reframes the project. The handover (sections
29 to 31) lists membership pricing, weekend surcharges, public holiday
surcharges, and capacity rules as business rules the migration must preserve.
**None of them are in this code.** Base rates come entirely from ShipLogic's
`/v2/rates`; merchant markup is applied client-side in the WooCommerce plugin;
the only Delicate-owned pricing is a per-km special-trip fallback. There is no
capacity model and no holiday calendar.

So the Engine is not replacing a rate engine. It is building the **first** one.
That is a build, not a port, and it is the critical path. See
[Deliverable 7](07-engine-gap-analysis.md).

## Two constraints ShipLogic imposes that the Engine removes

Worth noting as upside, because they are revenue-relevant and both are already
costing the business today:

- **Special trips cannot be auto-booked.** ShipLogic denies SPX creation on this
  account and the permission cannot be enabled. Out-of-coverage orders currently
  fall back to a per-km quote at checkout and then require a human to book the
  trip manually in the ShipLogic dashboard, triggered by an admin email. Owning
  the Engine removes the manual step entirely.
- **Weekend pricing cannot be expressed.** The handover wants weekend and public
  holiday pricing as distinct conditions. There is nowhere to put them today.

## What happens next

Per the handover's closing instruction, discovery stops here and waits for
approval before any destructive architectural change. The recommended first
implementation step is in [Deliverable 9](09-phased-plan.md): introduce the
provider abstraction and per-client routing **while ShipLogic is still the only
provider**, so the seam is proven in production before the Engine exists behind it.
