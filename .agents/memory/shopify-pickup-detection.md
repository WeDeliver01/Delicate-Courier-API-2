---
name: Shopify pickup detection
description: How to reliably tell Shopify pickup orders from deliveries; why keyword matching on shipping lines fails.
---

**Rule:** A genuine Shopify delivery order ALWAYS has `shipping_address`. Pickup / in-store collection orders have `shipping_address: null` — treat null shipping_address as collect, never delivery. Order `tags` often contain a whole-token `pickup` tag (from pickup-scheduling apps); match tags as whole comma-separated tokens, not substrings.

**Why:** Merchants name pickup shipping lines freely (e.g. "Kempton Park - Shop") so keyword checks on code/title miss them. Misclassified pickups then book shipments with empty addresses and Shiplogic returns BadRequest "delivery postal_code is required", retrying forever (Cakeaways/Store 5 incident, July 2026 — 21 orders stuck).

**How to apply:** When classifying fulfillment type from a Shopify webhook, check shipping_address null first, then tag tokens, then shipping-line keywords. Also fail fast (clear error) before calling Shiplogic when a delivery order has an empty postal code — except special_trip orders, which route by lat/lng and don't need one.
