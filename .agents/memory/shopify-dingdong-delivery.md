---
name: Shopify DingDong delivery date/time location
description: Where the DingDong Delivery date-picker app stores the customer's chosen delivery/pickup window on Shopify orders.
---

# DingDong Delivery date/time on Shopify orders

The "DingDong Delivery" Shopify app (used by Marone's cakeaways.myshopify.com store)
stores the customer's checkout selection on **`line_items[].properties`** as
name/value pairs — **NOT** on order-level `note_attributes` (those are empty).

- Delivery orders: `Method` = "Local delivery", `Delivery Date` = e.g. "Friday, 03 July 2026"
  (weekday-prefixed, parseable by InvariantCulture long-date), `Delivery Time` = "09:00 AM - 04:00 PM"
  (12-hour AM/PM range).
- Pickup orders: `Method` = "Store pickup", `Pickup Date` / `Pickup Time`.
- The same date/time values repeat across every line item — take the first non-empty.
- Order `tags` also contain the same info but comma-jumbled / unordered — unreliable, do not parse.

**Why:** Ingestion once assumed the slot lived in `note_attributes` and stored null,
so Shiplogic bookings got no scheduled window. Live API inspection (not docs)
revealed the real location.

**How to apply:** When wiring any Shopify date-picker app's selection into the
pipeline, inspect a real order via the Admin API first — don't assume `note_attributes`.
Downstream the time string can be passed through unchanged (OrderToShipmentMapper
ParseTimeRange/ParseTimeTo24H use DateTime.TryParse, which handles AM/PM ranges),
but normalise the date to ISO yyyy-MM-dd so PluginWebhookService's DateTime.TryParse
is reliable across cultures.
