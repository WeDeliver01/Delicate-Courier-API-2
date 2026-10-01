---
name: Special-trip fallback (Shopify + WooCommerce)
description: How both platforms offer the distance-priced fallback rate, the Woo plugin's rate filter/label detection constraints, and the Google API prerequisite.
---

**Rule (shared):** When Shiplogic returns no checkout rates and the Store has `SpecialTripCostPerKm` + its OWN `GoogleMapsApiKey`, the backend offers a distance-priced "Special Trip" rate via the shared `SpecialTripQuoter`: price = max(MinFee, one-way driving km × CostPerKm), capped at MaxKm. Distance is rounded to 1 decimal BEFORE pricing so the label ("… (12.3 km)") and the booked amount always agree. Orders book as Shiplogic SPX with the pre-markup quote as declared value.

**Shopify:** the carrier callback returns ServiceCode `SPECIAL_TRIP`; Shopify echoes it on the shipping line, which is how ingestion recognizes it.

**WooCommerce (plugin frozen at v2.9.1 — no plugin updates allowed):**
- The plugin filters platform rates by ITS locally configured service level code (default `STD`) — a fallback rate with any other code is silently dropped. So the backend serves the special-trip rate with `serviceLevelCode = store.DefaultServiceLevel` and the label "Special Trip Request (X km)" as the rate name.
- The plugin applies the merchant's checkout markup to EVERY platform rate client-side, so the markup applies to special trips automatically; the merchant keeps the margin (confirmed intent).
- The order webhook arrives tagged `delivery`; intake recognizes special trips only via the Woo REST shipping-line label (in the fulfillment-verification step). Therefore the fallback is ONLY offered to stores with Woo REST credentials — otherwise the order would book as a standard shipment.
- Intake only reclassifies when the label parses to a priceable distance; merchant-made labels merely containing the phrase must not become unpriced SPX bookings.

**Why:** Merchants in areas Shiplogic can't rate would otherwise lose the sale; one shared quoter keeps pricing identical across platforms.

**How to apply:** The merchant's Google key must have the **Distance Matrix API** enabled (repeatedly seen REQUEST_DENIED; Geocoding also, falling back to Nominatim). If special trips silently don't appear at checkout, check backend logs for `Distance Matrix returned no route. TopStatus=REQUEST_DENIED`, then the no-key / no-Woo-creds / over-MaxKm info logs, before debugging code.
