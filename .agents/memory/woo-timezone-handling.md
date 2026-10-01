---
name: Woo order timestamps are site-local with per-merchant timezones
description: Woo `date_created` (REST and plugin webhooks) is bare wall-clock time in each WordPress site's configured timezone — which differs per merchant. Never stamp it as UTC.
---

# Woo order timestamps: site-local, per-merchant timezone

Woo reports `date_created` (REST API and our checkout plugins' `Y-m-d H:i:s` payloads) as bare wall-clock time in the **WordPress site's configured timezone**, and merchants configure it differently — production showed some stores on SAST (UTC+2) and others on UTC. Stamping the raw value as UTC made SAST-store order times render 2h ahead; blanket-assuming SAST would have broken the UTC-configured stores.

**Why:** July 2026 incident — orders page showed times in the future. Skew was per-store, only visible by comparing `OrderDate` against webhook arrival (`CreatedOn`) in the DB.

**How to apply:**
- REST path: always prefer `date_created_gmt` (present in Woo REST v3), never `date_created`.
- Plugin-webhook path: `StoreLocalTime.ParseToUtc` disambiguates bare timestamps by picking the interpretation (UTC vs Africa/Johannesburg) closest to webhook arrival time — valid because webhooks land seconds after checkout. Known limit: deliveries delayed >1h fall back to treat-as-UTC.
- Shopify sends ISO-8601 with explicit offset — honored directly, no inference.
- When debugging "wrong time" reports, first query `OrderDate - CreatedOn` per store; a consistent ±2h diff pinpoints the misinterpreted zone and which stores are affected.
- Durable fix if it recurs: have plugins send ISO-8601 with offset (PHP `format('c')`), or store a timezone per store.
