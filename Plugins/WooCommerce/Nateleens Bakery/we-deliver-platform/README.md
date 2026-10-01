# Delicate Courier Platform — WooCommerce Plugin

Version **2.0.0**. Fresh rewrite for the current Delicate Courier Platform
(`https://api2.delicatecourier.co.za`). Single PHP file, no external
dependencies, no Azure-era code.

## What it does

1. **Live rates at checkout** — calls Shiplogic (`POST /v2/rates`) directly
   from the merchant's site using the merchant's own Shiplogic bearer token,
   so the platform is never in the rate-quote loop. Results are cached in a
   WordPress transient for 5 minutes (keyed by destination + cart contents)
   so the checkout stays snappy.
2. **Order push to platform** — on every order status change (processing,
   on-hold, completed, payment complete), POSTs the order to the platform at
   `/api/webhooks/plugin/order`. The request body is HMAC-SHA256 signed with
   the per-store Webhook Secret, base64-encoded into `X-Plugin-Signature`,
   with the Store ID in `X-Store-ID`. Bounded retry with exponential backoff
   (3 attempts, 1s/2s) is applied to transport errors and HTTP 5xx — 4xx
   errors are returned immediately.

There is no API key. There is no shared bearer token. There is no
`Authorization` header sent to the platform.

## Installation

1. Upload the ZIP via **Plugins → Add New → Upload** and activate.
2. Go to **Delicate Courier** in the admin sidebar.
3. Fill in the merchant-editable settings:
   - **Store ID** — the numeric ID shown on the store record in the platform.
   - **Webhook Secret** — the per-store HMAC key shown on the same record.
   - **Shiplogic Bearer Token** — the merchant's own Shiplogic API key.
   - **Shiplogic Account ID** — your numeric Shiplogic account ID (default
     plugin only — branded builds pre-bake this).
   - **Shiplogic Provider ID** — the numeric ID of the Shiplogic courier
     provider you want to quote (default plugin only — branded builds
     pre-bake this).
   - **Enable live rates at checkout** (toggle).
   - **Auto-sync orders to platform** (toggle).
   - **Debug logging** (toggle).
4. Save. The page shows the remaining pre-configured values (service level,
   collection address) in read-only form so you can verify them.
5. Click **Test platform connection** and **Send signed test order** to verify
   the wiring end-to-end. Click **Test Shiplogic rates** to verify the
   merchant's Shiplogic credentials.
6. Add the **Delicate Courier Platform** method to each shipping zone in
   **WooCommerce → Settings → Shipping**.

## What's hidden (and why)

These values are NOT editable on the settings page — they come from
constants baked into the plugin build (branded variant) or, where applicable,
are derived from your WooCommerce store settings:

| Value | Source | Override |
|---|---|---|
| Shiplogic Account ID | Editable on the settings page (default plugin). Branded builds set `DCP_DEFAULT_SHIPLOGIC_ACCOUNT_ID` constant, which takes precedence. | `add_filter('dcp_shiplogic_account_id', …)` |
| Shiplogic Provider ID | Editable on the settings page (default plugin). Branded builds set `DCP_DEFAULT_SHIPLOGIC_PROVIDER_ID` constant, which takes precedence. | `add_filter('dcp_shiplogic_provider_id', …)` |
| Service Level Code | `DCP_DEFAULT_SERVICE_LEVEL_CODE` constant (default `STD`) | `add_filter('dcp_service_level_code', …)` |
| Collection address | `DCP_DEFAULT_COLLECTION_*` constants, falling back to the WooCommerce store address | `add_filter('dcp_collection_address', …)` |
| Collection time offset (minutes) | filter, default 90 | `add_filter('dcp_collection_offset_minutes', …)` |
| Rate cache TTL (seconds) | filter, default 300 | `add_filter('dcp_rate_cache_ttl_seconds', …)` |
| Order-push retry attempts | filter, default 3 | `add_filter('dcp_platform_post_max_attempts', …)` |

If you need different values per-merchant, ask your platform administrator to
issue a branded plugin build, or drop a tiny snippet into your theme's
`functions.php` / a mu-plugin.

## Security: how secrets are stored

The three secrets (Store ID, Webhook Secret, Shiplogic Bearer Token) are
stored in `wp_options` with `autoload = no`, so they are NOT loaded into
memory on every WordPress page request — only when an order is being pushed,
a rate is being quoted, or you are on the settings page. The activation hook
and the settings-save handler both enforce this.

## What the activation hook cleans up

The activation hook deletes all legacy options from prior plugin generations
so stale credentials or configuration can never accidentally be used:

- `dcp_api_key`, `dcp_platform_api_key`, `dcp_shiplogic_api_key`,
  `dcp_api_base_url` (legacy bearer auth + URL — replaced by HMAC +
  hard-coded URL).
- `dcp_service_level_code` (moved to constant / filter).
- All `dcp_collection_*` and `dcp_collection_offset_minutes` (moved to
  constants / filters).

## Outgoing order payload

```jsonc
{
  "event": "order.created",
  "woo_order_id": 12345,
  "order_number": "12345",
  "status": "processing",
  "total": 250.00,
  "shipping_total": 80.00,
  "currency": "ZAR",
  "payment_method": "Card",
  "date_created": "2026-05-16 10:00:00",
  "customer_note": "Leave at door",
  "customer":         { "name": "...", "email": "...", "phone": "..." },
  "shipping_address": { "street": "...", "city": "...", "state": "...",
                         "postcode": "...", "country": "ZA" },
  "line_items": [
    { "id": 1, "product_id": 99, "name": "Box of macarons",
      "quantity": 2, "price": 170.00, "sku": "MAC-12" }
  ],
  "total_weight": 1.2,
  "store_url": "https://merchant.example",
  "store_id": "42",
  "delivery_date":   "2026-05-18",
  "delivery_time":   "14:00",
  "collection_date": "2026-05-18",
  "collection_time": "12:30",
  "occasion": "Birthday"
}
```

The platform validates `X-Plugin-Signature` against `WebhookSecret` for the
store identified by `X-Store-ID`. Any mismatch is rejected with `401`.

## Merchant-branded variants

This package ships in three flavours:

| Folder                                                | Plugin slug                  | Pre-baked merchant defaults |
|-------------------------------------------------------|------------------------------|-----------------------------|
| `Default Plugin/`                                     | `delicate-courier-platform`  | **None.** Generic build with no tenant data baked in. The merchant enters their own Shiplogic Account ID and Provider ID on the settings page. Service level defaults to `STD`. Collection address falls back to the WooCommerce store address. |
| `HoneyBee Bakery/delicate-courier-platform/`          | `delicate-courier-platform`  | HoneyBee Bakery's Shiplogic account/provider/service level + collection address. |
| `Nateleens Bakery/we-deliver-platform/`               | `we-deliver-platform`        | Nateleens Bakery's Shiplogic account/provider/service level + collection address. |

Branded variants differ from the default only by a small block of
`DCP_DEFAULT_*` constants at the top of the PHP file and the plugin header.
When those constants are present and non-empty, the corresponding settings
fields are hidden from the UI. The three secrets are still entered
per-install by the merchant.

## Live staging smoke test

Before shipping a new build of this ZIP to merchants, run the manual
end-to-end checklist in [`STAGING_SMOKE_TEST.md`](./STAGING_SMOKE_TEST.md)
against a real WooCommerce staging site. The automated harness in
`DelicateCouriers.Tests` covers the HMAC contract and JSON shape, but the
runbook is what proves the real network path, the real Shiplogic rates API,
and the real Hangfire shipment job all still work.

## Compatibility

- WordPress 5.8+ / PHP 7.4+ / WooCommerce 6.0+.
- Declares HPOS (Custom Order Tables) compatibility.
- Optional integrations (read-only): WP Time Slots Booking, Advanced Product
  Fields (WAPF), Delicate Two-Step Checkout (DTSC) — used to extract
  delivery date/time and occasion from order metadata when present.
