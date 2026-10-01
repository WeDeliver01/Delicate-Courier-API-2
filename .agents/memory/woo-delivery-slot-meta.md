---
name: WooCommerce delivery-slot meta locations
description: Where merchant checkout plugins store the delivery date/time on Woo orders, and how missing slots silently become next-day bookings.
---

# WooCommerce delivery-slot meta — inspect the live order first

DTSC also writes (seen on live Honey Bee Baker orders): `_dtsc_collection_time`/`_dtsc_collection_date`, `_dtsc_shipping_place_id`, `_dtsc_shipping_latitude`/`_dtsc_shipping_longitude`, and `_dtsc_courier_quote` (PHP array: status/price/message). Mirrored `_dcp_*` copies of the slot keys exist too.

Different merchant checkout plugins store the customer's delivery slot in
different Woo order meta locations, and the platform plugin's
`dcp_extract_delivery_meta()` only recognises an explicit allow-list of keys:

- DTSC: `_dtsc_delivery_date` / `_dtsc_delivery_time` (authoritative)
- Plain top-level meta: `delivery_date` / `delivery_time`
  (+ underscore variants) — e.g. Crumble GF's custom checkout, format
  `"2026-07-11"` / `"12:00 - 13:00"`
- WP Time Slots Booking and WAPF live on **line-item** meta

**Why:** When no key matches, the webhook payload carries null delivery
date/time and the booking silently defaults to next-day delivery — no error
anywhere, the merchant just gets a wrong-date consignment. Bookings that
already went to Shiplogic keep the wrong date; code fixes only help new or
retried bookings.

**How to apply:** When a store's bookings have wrong/default dates, fetch a
real order via the store's Woo REST API (creds on the `Stores` row) and look
at raw `meta_data` before touching code. The backend also has a booking-time
safety net that backfills the slot from Woo REST meta when the webhook didn't
carry one, so re-books work even before the merchant updates the plugin.
Collection defaults to same day, running 120→90 minutes before the END of
the delivery window (customer's chosen time), e.g. chosen 16:30 → delivery
16:00–16:30, collection 14:30–15:00 (backend mapper default; underflow near
midnight falls back to [00:00, delivery start]).

## Free-text product-addon slots (Baked By Nataleen)
Store 7 captures the slot as a typed sentence on a LINE ITEM addon (key starts "Please indicate date, day and TIME…"), e.g. "Thursday 20th August 11am delivery" / "Saturday 15th August Delivery 9.30am" — no year, varied time formats (9.30am, 11am, 17h00, 4:30 pm).
**How handled:** `ShipmentOrchestrationService.TryParseFreeTextDeliverySlot` regex fallback runs when known meta keys fail; year inferred as next occurrence on/after order date. Values without a real date ("Delivery, anytime, before 17h00") are rejected — never invent a date.
