# Delicate Courier Platform — WooCommerce plugin changelog

## 2.12.0 (Default) — 2026-10-24

### Stop paying twice to resolve the same address
- Places autocomplete already resolves the shopper's address on the merchant's
  own Google key, and that response carries coordinates. The plugin fetched
  them and threw them away, so the platform then geocoded the same address
  again — on every rate request, and a checkout makes several as somebody
  types. The pin is now kept and sent.
- Three optional fields on the rates request: `latitude`, `longitude`,
  `placeId`. Backward-compatible both ways — an older plugin sends none and
  the platform geocodes as before; an older platform ignores them.

### A stale pin is refused
- Pick a suggestion, then edit the street number by hand, and the stored
  coordinates belong to a different house. Pricing from them would quote, and
  later collect at, the wrong address with nothing on screen looking wrong.
- The signature of the address a pin came from is stored with it and checked
  before use. A mismatch falls back to geocoding. Paying for a lookup is the
  cheap failure; delivering to the wrong house is not.
- Coordinates that cannot be a place are refused outright rather than stored:
  zero/zero (which is what an empty field casts to) and anything out of range.

## 2.11.0 (Default) — 2026-10-23

### Several delivery options, chosen by the shopper
- The platform now returns a rate per service level the merchant offers, and
  each becomes its own option at checkout. The plugin already looped over the
  response; what changed is that there is now more than one to loop over.
- `DCP_DEFAULT_SERVICE_LEVEL_CODE` defaults to `''`, meaning "offer whatever
  the platform returned". It was `STD`, which filtered the list down a second
  time — a second source of truth that could only ever disagree with the
  merchant's own configuration. A branded build may still pre-bake a code.
- The chosen option travels back on the order as the WooCommerce method id,
  `delicate_courier_platform_<code>`, which is how the platform knows which
  service was actually paid for rather than assuming.

### Checkout widget
- Our rates now carry a brand mark, their name and the delivery estimate, with
  one "Powered by Delicate Courier" line beneath the shipping options.
- It decorates **only our own rates**. A merchant's other shipping methods are
  theirs and render exactly as their theme renders them, and the footer does
  not appear at all when none of our rates are on offer — a checkout showing
  only the merchant's own methods should not be crediting us for them.
- No price is rendered by the widget: WooCommerce appends its own, and two
  prices on one row is how a shopper ends up unsure what they are paying.
- Everything is filterable, because this ships to sites we do not control:
  `dcp_widget_enabled`, `dcp_widget_logo_url`, `dcp_widget_powered_by_text`,
  `dcp_widget_brand_colour`, `dcp_widget_show_estimate`, `dcp_widget_eta_text`.
  A wordmark is used rather than a bundled logo: shipping an image would mean
  an asset in a single-file plugin, and guessing at a branded build's identity.

## 2.10.0 (Default) — 2026-10-23

### Commerce endpoints move to the integration service
- Order push, checkout rates, waybill download and the connection test now go
  to `https://api.delicatecourier.co.za` instead of `api2`. Nothing about the
  requests changed — same paths, same JSON, same `X-Store-ID` +
  `X-Plugin-Signature` HMAC. Only the host differs, which is what freezing the
  contract in 2.1.0 was for.

### Licensing and updates deliberately stay on api2
- `/api/v1/plugins/*` (activate, deactivate, check-update, changelog,
  heartbeat, telemetry) now resolve through a separate `dcp_update_base_url()`
  against `https://api2.delicatecourier.co.za`. The integration service does
  not issue licences or host update packages, so sending everything to it
  would have stopped this plugin receiving updates — silently, since an
  inactive licence only disables the updater and leaves orders and checkout
  working. That failure would likely have gone unnoticed for months.
- Both endpoints have their own optional override on the settings page, and
  the status line now names each one rather than "Platform endpoint".

### Also included: order re-sync (previously unreleased)
- The source file in this repo had carried an unreleased change since the
  fork: a status update on an already-synced order re-pushes to the platform
  instead of returning early, and does so without adding a duplicate order
  note. It was sitting under the 2.9.1 version number without ever being
  packaged, so no merchant has it. It ships here, called out rather than
  smuggled in under a version they already think they have.

### Platform-side change shipped alongside this release
- The integration service gained `GET /api/webhooks/plugin/health`, which the
  "Test connection" button needs. It names itself in the response, so during
  the cutover — when both services answer the same path — a merchant saying
  "the test passes" means something specific.

## 2.9.1 (Default) — 2026-07-26

### Fix: update client restored (license & automatic updates)
- Restores the "License & automatic updates" section on the settings page and
  the whole Plugin Platform update client (activate/deactivate license, native
  WordPress update integration, heartbeat, telemetry). It was introduced in
  2.5.0 but accidentally dropped in 2.7.0 when that release was based on an
  older copy of the file, so 2.7.0–2.9.0 shipped without it. No other changes.

## 2.9.0 (Default) — 2026-07-26

### Waybill download from the order screen
- New "Download waybill (PDF)" button in the Delicate Checkout Data metabox
  (shown once a tracking number exists). The plugin fetches the shipping
  label from the platform over the signed plugin channel
  (`POST /api/webhooks/plugin/label`, same X-Store-ID + X-Plugin-Signature
  HMAC scheme as the order push) and streams it as a download. Admin-only,
  nonce-protected; clear error message when the label isn't available yet.

### Delicate Checkout Data on PDF invoices & packing slips
- The checkout data (fulfillment method, delivery date/time, courier
  collection time, alt phone) plus tracking number and courier are now
  printed on documents generated by "WooCommerce PDF Invoices & Packing
  Slips" (`wpo_wcpdf_after_order_data`, with an after-customer-notes
  fallback for custom templates). Rows are filterable via the new
  `dcp_document_rows` filter. No-op when that PDF plugin is not installed.

### Platform-side fixes shipped alongside this release
- Shipment meta write-back now survives site redirects (e.g. non-www → www
  301s), which was the root cause of Store 7 orders stuck on
  "Awaiting waybill" despite booked shipments.

## 2.8.0 (Default) — 2026-07-26

### Order metabox — "Delicate Checkout Data"
- The order-screen sidebar metabox (previously "Shipment & Tracking") is now
  titled "Delicate Checkout Data" and additionally shows the checkout details
  written by the Delicate Two-Step Checkout: fulfillment method, delivery
  date/time, courier collection time, alternative phone, Google Place ID,
  delivery coordinates (with a "View on Google Maps" link), and the courier
  quote (status / quoted price / message from `_dtsc_courier_quote`).
- Coordinates fall back to the Special Trip shipping-line meta (v2.7.0) when
  DTSC address meta is absent; delivery slot falls back to the existing
  `dcp_extract_delivery_meta()` extractor, so non-DTSC stores still benefit.
- Admin-UI only: no webhook/contract change; fields render only when the
  corresponding order meta exists, so non-DTSC stores see the same metabox
  as before plus a "Shipment & Tracking" heading.

## 2.5.0 (Default) / 2.5.0 (HoneyBee) / 2.4.0 (Nateleens) — 2026-07-25

### Automatic updates via the Plugin Platform (new update client)
- New "License & automatic updates" section on the settings page: activate /
  deactivate a platform-issued license key (`DCP-XXXX-XXXX-XXXX-XXXX`), see
  license status, and run "Check for updates now".
- Native WordPress updater integration: the plugin injects platform releases
  into `pre_set_site_transient_update_plugins`, so updates appear on the
  Plugins page like any wordpress.org plugin. The details popup ("View
  version x.y.z details") is served from the platform changelog endpoint via
  `plugins_api`.
- Secure downloads: download URLs are signed and expire in ~60 seconds, so
  the update offer carries an internal `dcp-uc://` marker; the real URL is
  fetched fresh at install time (`upgrader_pre_download`) and the ZIP is
  verified against the server-published SHA256 before WordPress installs it.
  A checksum mismatch aborts the install and reports a telemetry event.
- Twice-daily WP-cron job (`dcp_uc_cron_event`): update check, heartbeat
  (plugin/WP/WC/PHP versions, API latency, last 5 error-log entries), and
  telemetry flush (license activations, updates installed). All requests
  carry a unix timestamp + random nonce for server-side replay protection.
- New options: `dcp_license_key` (autoload=no), `dcp_license_status`,
  `dcp_install_key` (stable per-site UUID), `dcp_uc_telemetry_queue`.
- Branded builds can override the update channel slug via `DCP_UPDATE_SLUG`
  (Nateleens uses `we-deliver-platform`; Default and HoneyBee use
  `delicate-courier-platform`).

## 2.4.4 (Default) / 2.4.5 (HoneyBee) / 2.3.4 (Nateleens) — 2026-07-08

### Delivery slot extraction — ThemeHigh checkout picker support
- `dcp_extract_delivery_meta()` now also reads top-level `date_picker` /
  `time_picker` order meta (and underscore-prefixed variants) written by
  ThemeHigh Checkout Field Editor pickers — e.g. Baked By Nataleen.
- New helpers normalise those values before sending to the platform:
  `dcp_normalize_date_dmy()` converts DAY-FIRST `09/07/2026` → `2026-07-09`
  (never mis-read month-first), and `dcp_normalize_time_24h()` converts
  12h clock `05:00 PM` → `17:00`. Ranges and ISO dates pass through.
- Precedence unchanged: DTSC first, then `delivery_date`/`delivery_time`,
  then the new picker keys, then per-line-item fallbacks.

## 2.4.3 (Default) / 2.4.4 (HoneyBee) / 2.3.3 (Nateleens) — 2026-07-06

### Delivery slot extraction — plain order meta fallback
- `dcp_extract_delivery_meta()` now also reads the delivery slot from plain
  top-level order meta keys — `delivery_date` / `delivery_time` (and their
  underscore-prefixed variants) — used by generic checkout plugins such as
  the one on Crumble GF. Previously only DTSC, WP Time Slots Booking, and
  WAPF meta were recognised, so those stores' orders arrived with no
  delivery date and were booked for the platform default (next day).
- Precedence is unchanged: DTSC meta stays authoritative when present; the
  new keys are checked next, then the existing per-line-item fallbacks.
- No new settings. Collection time is still derived as 90 minutes before
  the delivery time (`dcp_collection_offset_minutes`, default 90).

## 2.3.0 — 2026-05-20

### Shipment & Tracking metabox (write-back)
- **Order metabox redesign.** The "Delicate Courier" sidebar on every
  WooCommerce order edit screen is now titled **Shipment & Tracking** and
  shows live shipment state instead of only sync status:
  - Tracking number (linked to Shiplogic's public tracking page when known),
    courier name, status label, Shiplogic consignment ID.
  - Customer Ref (WC order number), Customer Paid (Shipping), Courier Rate
    (Shiplogic), Markup Margin (highlighted red if negative).
  - Platform ID and last sync time are kept at the bottom as small print.
  - Empty state when no shipment yet: *"Awaiting waybill — shipment not
    yet created."*
- **New `_dcp_*` order meta keys** the platform writes onto every order:
  `_dcp_tracking_number`, `_dcp_consignment_id`, `_dcp_courier_name`,
  `_dcp_shipment_status`, `_dcp_shipment_status_label`,
  `_dcp_courier_rate`, `_dcp_tracking_url`, `_dcp_shipment_id`,
  `_dcp_synced_at`. All of them are written via
  `PUT /wp-json/wc/v3/orders/{id}` with a `meta_data` array using the
  store's existing WooCommerce REST Consumer Key/Secret — no extra plugin
  configuration required.
- **Refresh on every Shiplogic update.** The meta is re-pushed on every
  Shiplogic tracking webhook (submitted → collected → in-transit →
  delivered) so the merchant always sees the current status without
  refreshing the page or waiting for the customer-visible note.

### Notes for upgraders
- No new settings. Existing v2.2.x installations will see the new metabox
  immediately after the platform's first push for a freshly booked order.
- Orders booked before this release will only render the live panel
  after the next Shiplogic status webhook (the platform refreshes the
  meta on every webhook).
- Per-merchant variants (HoneyBee Bakery, Nateleens Bakery /
  We Deliver) are on the same code path and ship matching v2.3.0 ZIPs.
