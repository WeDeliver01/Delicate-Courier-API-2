# Deliverable 7: Delicate Engine Gap Analysis

> **Revised 2026-10-01.** The first version of this document was written before the Engine
> repository was available and concluded that the Engine did not exist and would have to be built
> greenfield. **That was wrong.** `WeDeliver01/Delicate-Engine` @ `c84998b` is a substantial,
> well-architected build. This revision replaces that analysis entirely. The conclusion changes
> direction: the capability gap is much smaller than feared, and the integration gap is much larger.

## What the Engine actually is

A TypeScript monorepo, pnpm workspaces + Turborepo:

| Part | Contents |
|---|---|
| `apps/api` | NestJS engine. 107 TS files, **17 bounded-context modules**, HTTP API + outbox worker |
| `apps/web` | Next.js 15, route groups `(marketing)` `(portal)` `(admin)` |
| `apps/driver` | Expo / React Native driver app |
| `packages/db` | Drizzle schema (17 files), **16 SQL migrations**, seed |
| `packages/contracts` | Zod DTOs, domain events, enums — shared by every app |
| `docs/` | Signed-off `ARCHITECTURE.md`, 3 ADRs, runbook, handover material |

Modules: `address-book`, `admin`, `analytics`, `billing`, `bookings`, `catalog`, `dispatch`,
`fleet`, `health`, `identity`, `ledger`, `loyalty`, `notifications`, `payments`, `scheduling`,
`treasury`, `wallet`.

Architecture signed off by Ashley 2026-09-19. The decision recorded there is explicit:
**"Own drivers only. No ShipLogic. We own waybills, tracking, POD, billing."**

### Phase status, with a caveat

`README.md` reports phases 0, 1 and 2 complete and phase 3 as next.
`docs/ARCHITECTURE.md` marks phases 3, 4 and 5 with checkmarks.

The two disagree. The `treasury`, `billing` and `payments` modules all have real schema, services
and tests, which suggests the README table is stale rather than the architecture doc being
optimistic. **Confirm with Ashley before planning against either.** Recorded here because an
estimate built on the wrong one would be wrong by months.

## Capability gap: much smaller than the handover assumed

Re-running the handover's capability list against the Engine's actual code:

| Capability the API needs | Engine status | Evidence |
|---|---|---|
| **Rate engine** | **Exists** | `catalog/quote.service.ts`, `packages/contracts/src/pricing.ts`. Rate cards, per-account overrides (`account_rate_cards`), service levels with multipliers, package types, options, fuel surcharge in bps, minimum fee, VAT. Pure deterministic function; quotes persist with the rule snapshot |
| **Booking** | **Exists** | `bookings/booking.service.ts`. One transaction: lock quote, reserve slot, place wallet hold, insert booking + shipments + events |
| **Capacity / availability** | **Exists** | `scheduling`. `slot_policies`, `delivery_slots` with capacity and `booked_count`, cut-offs, `blackout_dates`, row-locked in the booking transaction |
| **Waybill + tracking** | **Exists** | `DC-YYMMDD-XXXXX`, immutable `shipment_events`, public tracking by waybill |
| **Labels** | **Exists** | Printable waybill label. Format fit for the plugin still to be confirmed |
| **Driver allocation + dispatch** | **Exists** | `dispatch`, `fleet`. Drivers, vehicles, shifts, auto-assign on confirmation with proximity/load scoring, dispatcher override |
| **POD** | **Exists** | Photo, signature, name, geo, time. Expo driver app |
| **Settlement + ledger** | **Exists** | `ledger`, double-entry, balanced journals, integer cents, settles on **actual** km |
| **Payments / wallet** | **Exists** | `wallet`, `payments`. Wallets, append-only entries, holds, top-ups credited only on verified webhook, credit terms for postpaid |
| **Treasury** | **Exists** | Allocation engine, obligations, proposals with human approval |
| **Cancellation** | **Exists** | `POST /v1/account/bookings/:id/cancel` |
| **Idempotency** | **Exists** | Keys on wallet entries, holds, loyalty, bookings. Quote single-use under row lock |
| **Notifications** | **Exists** | Templates, channels, worker delivery with retries |
| **Weekend / public holiday surcharge** | **Does not exist** | Implemented surcharges are fuel, service level, parcel type and options. No date-conditional surcharge |
| **Any concept of this API, or of an e-commerce merchant** | **Does not exist** | A grep for woocommerce / shopify / shiplogic / merchant across `apps` and `packages` returns only payment-provider merchant IDs |

So of the handover's capability list, essentially everything exists except date-conditional
surcharges. **The Engine is not the bottleneck.**

## The real gap: the integration seam

The Engine was designed for customers booking directly through a portal. This API serves
e-commerce merchants whose customers book implicitly at checkout. Those are different shapes, and
the eight gaps below are where they fail to meet. Full detail, with the reasoning, is in the root
`CLAUDE.md`; summarised here with sizing.

| # | Gap | Size | Blocks |
|---|---|---|---|
| 1 | **No machine-to-machine auth.** `@RequireAccount()` needs a Supabase JWT + `X-Account-Id`. This API has no user principal | Small-medium | **Everything.** Build first |
| 2 | **Quote-then-book vs single-step create.** Engine books a `quoteId`; `CreateShipmentAsync` is one call | Medium | Booking path |
| 3 | **Quotes are single-use and expire; Hangfire retries for ~24h.** `quote_used` / `quote_expired` will fire on almost every retry | Small, high-risk | Correctness. Mishandled = duplicates or silent non-booking |
| 4 | **Pricing models are structurally different** (see below) | **Commercial decision, not code** | Phase 2 feasibility |
| 5 | **Wallet gating has no equivalent** in the merchant flow (see below) | **Commercial project** | Any real client migration |
| 6 | **No identity mapping.** `Tenant`→`Store` vs `organizations`→`accounts`→`memberships` | Small-medium | Routing |
| 7 | **Status vocabulary differs**, and ShipLogic's strings already leak to merchants via `_dcp_*` meta | Small, easy to miss | Merchant-visible behaviour |
| 8 | **Label format fit** for the plugin and for drivers unconfirmed | Small, verify early | One frozen endpoint |

## The two gaps that are not engineering problems

### Gap 4 — the pricing models cannot be made to agree everywhere

The Engine prices a **loop from the depot**, cost-plus-margin:

```
loopKm = depot→collection + collection→drop1 + … + dropN→depot
cogs   = loopKm × costPerKm
base   = cogs / (1 − margin)
base   = base × serviceLevel.multiplier + serviceLevel.surcharge
fuel   = base × fuelSurchargeBps
       + extraDropFee × (N−1) + Σ packageType.surcharge + options
total  = max(subtotal, minFee) + VAT
```

ShipLogic prices **point to point** from an account rate card. No configuration of the above
reproduces a point-to-point table across all routes, because the depot leg and the margin divisor
are structural.

The handover's section 29 requires that the migration not change pricing. Taken literally against
these two functions, **that requirement cannot be met**. So it needs restating as a decision, in
writing, before any pricing work:

- **Option A** — Engine reproduces ShipLogic prices within an agreed tolerance across a
  representative route matrix. Requires the exported ShipLogic rate card and accepts that
  outliers exist.
- **Option B** — Checkout prices change on migration; merchants are told in advance, and the
  change is defensible because Delicate now owns and can explain every price.

Shadow-comparing quotes on real traffic (Phase 4) sizes the gap before the decision is forced. Do
that first.

Worth noting what improves either way: the Engine's quote is a pure function, persisted with the
rule snapshot that produced it, so every charged booking can explain its own price. The current
position is a rate card nobody in this codebase can see.

### Gap 5 — wallet gating is a commercial onboarding project

The Engine gates every booking:

```
available = balance + credit_limit − holds ≥ price
```

placing a hold at booking and capturing it at settlement. Bookings through this API have no wallet
concept at all: the merchant's own checkout already collected from the customer, and Delicate bills
the merchant separately.

Routing API bookings into the Engine therefore requires **every merchant to have an Engine account
with a funded wallet or agreed credit terms**. For the anchor clients (Honey Bee Baker, Baked by
Nataleen, Cake Aways by Marone, Sugarplum Treats SA, Crumble GF, Taya Bakes) that is a commercial
conversation about payment terms, not a code change.

This is the **largest non-engineering dependency in the project** and it gates Phase 6 entirely. It
should start now, in parallel with Phase 1, because it has a lead time that engineering cannot
compress.

## What this repo brings that the Engine does not have

The seam is not one-directional. This API holds things worth keeping deliberately:

- **A clean provider abstraction already exists.** `IShiplogicService` is eight methods, nothing
  bypasses it, no ShipLogic type leaks past it except its DTOs. Renaming it to `IShipmentProvider`
  and adding a second implementation is a small, safe change.
- **Three-layer idempotency**, battle-tested against real incidents.
- **The guard sequence** — collect detection at three layers, terminal-status refusal, special-trip
  handling — encoding merchant behaviour the Engine has never seen.
- **Geocoding with a DB cache**, which becomes a primary input to loop pricing rather than the
  ShipLogic workaround it was built as.
- **Plugin and storefront knowledge** in `.agents/memory/`: delivery-slot meta keys that differ per
  checkout plugin, Woo timezone handling, polymorphic Woo meta, Shopify pickup detection, DingDong
  delivery date/time in `line_items[].properties`. None of this exists in the Engine and all of it
  is needed for merchant bookings to be correct.

## Two constraints the Engine removes

Both are costing the business today:

- **Special trips become bookable.** ShipLogic denies SPX creation on this account and the
  permission cannot be enabled, so out-of-coverage orders currently get a per-km checkout quote and
  then a human books the trip manually in the ShipLogic dashboard off an admin email. The Engine
  owns dispatch, so the manual step disappears.
- **Date-conditional pricing becomes possible.** Weekend and public-holiday surcharges have
  nowhere to live today. The Engine's rate card is the right place — but per the migration rules,
  build the framework switched off and enable it as a separate release.

## Revised critical path

1. **Engine: machine-to-machine auth** (gap 1). Nothing else starts without it.
2. **This repo: Phase 1** — contract tests on the six endpoints, provider rename, `Shipment.Provider`,
   routing columns. Ships to production as a no-op.
3. **In parallel, commercially**: export the ShipLogic rate card; decide Option A or B on pricing;
   begin merchant wallet/credit conversations.
4. **Engine: merchant account mapping** (gap 6) and the booking adapter (gaps 2 and 3).
5. **Shadow rate comparison** to size the pricing gap on real traffic.
6. Then the phased plan in [Deliverable 9](09-phased-plan.md) proceeds as written.

Step 3 is the long pole, and none of it is engineering work.
