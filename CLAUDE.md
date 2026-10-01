# Delicate Courier API — ShipLogic decoupling fork. Working notes for Claude Code.

This repo is the **migration fork** of the production Delicate Courier API. The job is to move
Delicate off ShipLogic and onto the **Delicate Engine**, without the merchants who integrate with
us ever noticing.

Read `Documentation/Migration/00-README.md` next. It indexes the ten Phase 0 discovery documents
and leads with the findings that shape the plan.

## The two repositories

| | This repo | The Engine |
|---|---|---|
| Name | `WeDeliver01/Delicate-Courier-API-2` | `WeDeliver01/Delicate-Engine` |
| Role | Client-facing integration layer. **Frozen contract** | The new courier platform. System of record |
| Stack | .NET 8, EF Core, Npgsql, Hangfire, Supabase Postgres | TypeScript monorepo: NestJS + Drizzle + Next.js + Expo |
| Forked from | `WeDeliver01/delicate-courier-api` @ `5d9507b` | fresh build, not a fork |
| Remotes here | `origin` = this fork, `upstream` = production | — |

Production (`upstream`) is live and untouched. **Merge `upstream` weekly** — it keeps shipping
plugin hardening and client onboarding, and a fork that diverges for months becomes unmergeable.

The Engine has its own `CLAUDE.md` and a signed-off `docs/ARCHITECTURE.md`. Read those before
touching it. Its rules are stricter than this repo's and they are not negotiable: integer cents,
append-only ledgers, balanced journals, one transaction per state change plus its event via the
outbox, and **the engine proposes money movements, a human executes**.

## What this repo actually is

- `DelicateCouriers/DelicateCouriers.ApiService` — 195 C# files, 21 feature slices, 27
  controllers, **106 endpoints**. All the business logic.
- `DelicateCouriers/DelicateCouriers.Domain` — 13 entities.
- `DelicateCouriers/DelicateCouriers.Tests` — 23 test files.
- `delicate-couriers-frontend` — Next.js admin/dispatcher portal.
- `Plugins/WooCommerce/{Default Plugin, HoneyBee Bakery, Nateleens Bakery}` — three merchant
  plugin lines, PHP. **A fix usually has to ship to all three.**
- `.agents/memory/` — 16 incident notes. Read `MEMORY.md` first; it is a one-line index. These
  are hard-won and they will save you a day each.
- `Documentation/Migration/` — the Phase 0 discovery set.

No `.sln`; projects reference each other directly. Target framework `net8.0` throughout.

## The contract that must not break

Of 106 endpoints, **six** carry courier semantics to anyone outside Delicate. These are the whole
constraint. Everything else is the admin portal, plugin licensing and reporting, where Delicate
controls both ends and can change freely.

| Endpoint | Caller | ShipLogic today | Engine equivalent |
|---|---|---|---|
| `POST /api/public/shipping/rates` | Woo plugin at checkout | `POST /v2/rates` | `POST /v1/account/quotes` |
| `POST /api/shopify/rates/{storeId}` | Shopify CarrierService | `POST /v2/rates` | `POST /v1/account/quotes` |
| `POST /api/webhooks/plugin/order` | Woo plugin | enqueues booking | `POST /v1/account/bookings` |
| `POST /api/webhooks/plugin/label` | Woo plugin | `GET /shipments/{id}/label` | waybill label |
| `POST /api/webhooks/woocommerce/order` | legacy Woo webhook | enqueues booking | `POST /v1/account/bookings` |
| `POST /api/webhooks/shopify/order` | Shopify webhook | enqueues booking | `POST /v1/account/bookings` |

Plus `POST /api/webhooks/shiplogic/tracking`, which **disappears** rather than migrating. Keep it
live and accepting through the whole rollback window; retire it last.

Record real request/response pairs from production for those six and commit them as contract test
fixtures. **If the same fixtures pass against both providers, the contract is preserved.** That is
the entire validation strategy, and it is the highest-value thing to build first.

## Eight integration gaps — this is the real work

The Engine is far more built than a reading of the original handover suggests: rate cards, slots
with capacity, drivers, dispatch, POD, double-entry settlement, treasury, wallets, billing,
loyalty and notifications all exist. What does **not** exist is any notion of this API, or of an
e-commerce merchant. A grep for woocommerce/shopify/merchant across the Engine returns only
payment-provider merchant IDs.

So the work is not "build the Engine". It is building the seam. In rough dependency order:

**1. No machine-to-machine auth on the Engine.**
`POST /v1/account/bookings` is `@RequireAccount()`: it needs a Supabase JWT principal plus an
`X-Account-Id` header. This API has no user principal; it acts on behalf of a merchant. The Engine
needs service-credential auth that can assert an account without a human JWT. **Nothing else on
this list can be built until this exists.**

**2. Quote-then-book versus single-step create.**
The Engine books a **quote**: `POST /v1/account/quotes` returns a priced, persisted quote, then
`POST /v1/account/bookings` takes its `quoteId`. `IShiplogicService.CreateShipmentAsync` is one
call. The mapper has to hold a quote between the rate call and the booking call, or re-quote at
booking time and accept that the price can move between checkout and booking.

**3. Quotes are single-use and they expire. Hangfire retries for 24 hours.**
`BookingService.create` calls `quotes.markBooked(tx, quote.id)` under a row lock and throws
`quote_used` on a second attempt, and `quote_expired` past `expiresAt`. The booking job retries up
to 10 times across roughly 24h (`[AutomaticRetry(Attempts = 10, DelaysInSeconds = 30 … 14400)]`).
A retry will therefore hit an expired or consumed quote almost every time.

Treat `quote_used` as **idempotent success** — find the booking that consumed the quote and return
it — and `quote_expired` as a re-quote, not a failure. Getting this wrong means either duplicate
bookings or orders that silently never book.

**4. The pricing models are structurally different, and this is a commercial decision.**

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

ShipLogic prices **point to point** off an account rate card. These are not the same function and
no configuration makes them agree everywhere. **So "reproduce the current ShipLogic rate card
exactly" may be impossible by construction**, and that is the first thing to establish rather than
discover during a cutover.

What this needs is a decision from Ashley, in writing, before any pricing work: either the Engine
reproduces ShipLogic prices within an agreed tolerance on a representative route matrix, or
checkout prices change on migration and merchants are told. Shadow-compare quotes on real traffic
to size the gap first (Phase 4 in the plan).

The quote function is pure and deterministic, and quotes persist with the rule snapshot that
produced them, so every charged booking can explain its own price. That is a better position than
the current setup, where the price comes from a rate card nobody in this codebase can see.

**5. Wallet gating has no equivalent in the current flow.**
The Engine gates every booking on `available = balance + credit_limit − holds ≥ price`, places a
hold at booking, and captures it at settlement. Merchant bookings through this API have no wallet:
the merchant's own checkout already collected from the customer.

So routing API bookings into the Engine means **every merchant needs an Engine account with either
a funded wallet or credit terms**. That is commercial onboarding, not a code change, and it has to
be agreed and sequenced before any client can move. It is the largest non-engineering dependency
in the project.

**6. No mapping between the two identity models.**
This repo has `Tenant` → `Store`. The Engine has `organizations` → `accounts` (one wallet, credit
terms, billing details, VAT number) → `memberships`. Each `Store` that books needs a corresponding
Engine account, and the mapping has to be stored and auditable.

**7. Status vocabulary.**
The Engine's lifecycle is `booked → assigned → collected → in_transit → delivered | failed |
cancelled`. ShipLogic's status strings are **already visible to merchants** through
`_dcp_shipment_status` order meta, and `delivered` maps to the Woo status `completed`. Merchant
automation may key on those values, and no endpoint test would catch a change.

Make the Engine-facing vocabulary a superset containing the existing strings verbatim, and keep
`ShipmentStorefrontMeta.StatusLabel` as the single translation point it already is. Query
production for the distinct live values of `Shipment.ShipmentStatus` and `TrackingEvent.EventType`
before freezing anything: the webhook writes `payload.Status` straight through, so the live set may
be wider than the entity docs suggest.

**8. Labels.**
`POST /api/webhooks/plugin/label` returns a PDF today. The Engine has waybills
(`DC-YYMMDD-XXXXX`) and a printable label. Confirm the Engine's label satisfies what the plugin and
the drivers need, including real printing and real scanning, before assuming this one is free.

## Rules for working in this repo

**Do not refactor `Features/Shipments/ShipmentOrchestrationService.cs`.** It is 1158 lines and it
encodes at least five production incidents, each named in its comments: the collect order booked
through a second trigger path, the `NpgsqlRetryingExecutionStrategy` bug that silently failed every
booking job for 24h at a time, order WC-49905 and the 30s timeout, an unobserved
`NullReferenceException` in the mapper, and the unbooked-completed-orders gap (#50829). It will look
like a refactoring opportunity. It is not.

Change exactly one thing in it: the provider call goes through the router. Treat as frozen:

- the guard sequence and its order (already-booked, collect, terminal status, special trip,
  collection shipping-method keyword)
- `pg_advisory_xact_lock(orderId)` inside `Database.CreateExecutionStrategy()`
- the cross-system idempotency lookup before create
- throw-for-transient versus return-Failure-for-permanent, which is what drives Hangfire retry
- the label fetch sitting **outside** the transaction
- storefront pushback, including the `TrackingPushedOn` gate that keeps the customer-visible note
  to exactly one per shipment

**Booking has five enqueue sites plus a manual endpoint.** That is why the guards live at the
chokepoint rather than at intake. Do not move them upstream.

```
Features/WooCommerce/WebhookService.cs:138
Features/WooCommerce/WebhookService.cs:185
Features/WooCommerce/PluginWebhookService.cs:102
Features/WooCommerce/PluginWebhookService.cs:176
Features/WooCommerce/WooStatusReconciliationService.cs:163
POST /api/Shipments/create/{orderRef}
```

**Preserve all three idempotency layers.** Local shipment check re-checked inside the lock;
`pg_advisory_xact_lock(orderId)` for concurrent workers; and
`FindShipmentByCustomerReferenceAsync` for the crash window between a successful provider create
and the local save. The third one is strict about false positives on purpose: it accepts only an
array response and only a row whose `customer_reference` matches exactly, so a provider that
ignores the query parameter cannot attach an unrelated shipment. Replicate that property.

Customer reference format is `WC-{WooOrderID}`. It is computed, never stored.

**Never shadow a create or a cancel.** Shadow mode compares rates only. No dry-run flag is
trustworthy enough to risk two real bookings for one customer order.

**Routing is configuration, not code.** Per-store and per-tenant `ProviderMode`, editable from the
SuperAdmin console, every change writing a `SystemEvent`. A rollback must never require a deploy
and must never require a client to change anything.

**Add `Shipment.Provider` in the first migration of this project**, before any Engine work.
Additive, nullable, backfilled `"shiplogic"`. Without it, reconciliation during the parallel window
means guessing from tracking-number format, and a rollback cannot tell which system owns a
shipment. It is the cheapest high-value change available.

**Do not enable new business rules with the migration.** The Engine makes weekend pricing, holiday
pricing and capacity possible for the first time. Build the framework with the conditions switched
off; turning them on is a separate release with its own commercial decision. Otherwise any pricing
complaint becomes unattributable.

**Never edit an applied migration** in either repo. 38 here, 16 in the Engine.

## Commands

This repo:

```bash
dotnet restore DelicateCouriers/DelicateCouriers.ApiService/DelicateCouriers.ApiService.csproj
dotnet build   DelicateCouriers/DelicateCouriers.ApiService
dotnet test    DelicateCouriers/DelicateCouriers.Tests
cd delicate-couriers-frontend && npm install && npm run dev
```

The Engine (always from its repo root, where `.env` and `docker-compose.yml` live):

```bash
docker compose up -d postgres
pnpm install && pnpm --filter @delicate/contracts --filter @delicate/db run build
pnpm db:migrate && pnpm db:seed
pnpm typecheck && pnpm test && pnpm format:check     # the CI gates
pnpm --filter @delicate/api run dev | dev:worker | dev:token <admin|owner|…>
```

## Deployment

API on `:8080`, Next.js standalone on `:5000`, nginx terminating TLS, both under systemd as a
non-login `delicate` user, on a dedicated Vultr instance. Public names
`api2` / `app2` / `webhooks2.delicatecourier.co.za`. DNS on GoDaddy. Supabase Postgres is managed
and external. The Engine deploys by Docker Compose on its own VPS with Caddy for TLS.

## Open questions

Blocking, in order:

1. **Does every merchant get an Engine account with a wallet or credit terms, and who onboards
   them?** Gap 5. Nothing can move a real client until this is answered.
2. **Is "reproduce ShipLogic prices exactly" the goal, or is a documented price change
   acceptable?** Gap 4. Determines whether Phase 2 is achievable as written.
3. **Export the current ShipLogic rate card.** Without it there is no baseline to prove the
   migration did not change pricing.
4. **Engine phase status**: its `README.md` says Phase 3 is next, while `docs/ARCHITECTURE.md`
   marks phases 3, 4 and 5 complete. The treasury, billing and payments modules all have real
   schema and code, so the README table looks stale. Confirm before planning against either.
5. **`Shiplogic__WebhookSecret` in production** — if unset, the tracking webhook accepts
   unauthenticated requests with only a log warning, so anyone who can reach
   `webhooks2.delicatecourier.co.za` can post status updates for any shipment whose consignment id
   or tracking number they can guess. Worth checking today, independently of this project.

Also open on the Engine side and listed in its `docs/ARCHITECTURE.md` §8: SMS/WhatsApp provider,
BobPay verification, PayCentral fuel-card API, Google Maps key, company VAT identity, real monthly
bills for treasury, and the real rate card.

## Where to start

Phase 1 in `Documentation/Migration/09-phased-plan.md`: contract-test the six endpoints, rename
`IShiplogicService` to `IShipmentProvider` with ShipLogic as the only implementation, add
`Shipment.Provider` and the routing columns, and ship it to production as a no-op. The seam gets
proven under real traffic long before the Engine is behind it.

Then answer open questions 1 and 2, because they decide whether the rest of the plan survives.
