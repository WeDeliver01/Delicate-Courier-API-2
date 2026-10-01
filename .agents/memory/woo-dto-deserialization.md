---
name: Woo DTO deserialization fail-safe blast radius
description: WooCommerce REST meta values are polymorphic JSON; strict string-typed DTOs crash whole-order fetches and the LookupFailed fail-safe then withholds ALL bookings.
---

# Woo meta values are polymorphic — never type them `string`

Woo REST `meta_data[].value` and `display_value` (order-level AND line-item-level) can be a string, number, array, or object — product add-on / upload plugins routinely write objects. `System.Text.Json` fails the ENTIRE order deserialization on the first mismatch.

**Why:** Production incident (July 2026): one structured line-item meta entry made `GetOrderAsync` throw → return null → `WooFulfillmentVerifier` returned `LookupFailed` → the fail-safe withheld every shipment booking platform-wide (intake + the layer-3 orchestration guard, which also blocks manual booking). A tiny DTO typing bug had total-outage blast radius because the verifier treats "can't fetch" as "don't book".

**How to apply:**
- Type Woo meta `value`/`display_value` as `JsonElement` with a `StringValue` helper (`ValueKind == String ? GetString() : null`). Both `WooCommerceOrderMeta` and `WooCommerceMetaData` now follow this pattern.
- When adding Woo DTO fields, assume polymorphic JSON unless the field is a core Woo scalar.
- Strongest verification: fetch the real failing order JSON from the live store and deserialize it through the actual DTOs in a test — deserialization stops at the first error, so a fix can hide further mismatches.
- Fail-safes that block actions on lookup failure amplify small parse bugs into full outages — when a "verification failed" event floods SystemEvents, suspect deserialization before suspecting the remote API.
