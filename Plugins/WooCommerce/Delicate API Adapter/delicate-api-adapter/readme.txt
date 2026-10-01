=== Delicate API Adapter ===
Contributors: delicatecourier
Requires at least: 5.8
Tested up to: 6.9.4
Requires PHP: 7.4
Stable tag: 0.2.0
License: Proprietary

Bridges merchant-specific checkout configurations to the Delicate Courier Platform (DCP) plugin.

== Description ==

Delicate API Adapter (DAA) is a companion plugin to Delicate Courier Platform (DCP). It writes the order meta keys DCP needs so the courier integration picks up the correct fulfillment type, delivery date, and shipping address on a per-merchant shipping-method basis.

The adapter is intentionally passive when DCP's own shipping method is the chosen one — it only acts on merchant-configured shipping methods that need translation into DCP's expected shape.

== Requirements ==

* WordPress 5.8 or higher
* WooCommerce 6.0 or higher (tested with 10.7)
* PHP 7.4 or higher
* Delicate Courier Platform 2.4.3 or higher (the adapter loads if DCP is absent or older, but behaviour features are disabled until DCP is present and compatible)

== Installation ==

1. Upload the `delicate-api-adapter` folder to `/wp-content/plugins/`.
2. Activate the plugin from the Plugins screen.
3. Visit WooCommerce → Delicate API Adapter to verify the dependency status panel shows DCP as compatible.
4. The master switch is OFF by default. Leave it OFF until you've reviewed the per-feature settings and are ready to test.

== Kill switch ==

If the adapter causes a problem at any point, you have three options in order of escalation:

1. Toggle off the master switch on the settings page. Adapter becomes dormant immediately.
2. Deactivate the plugin from the Plugins screen.
3. If admin is inaccessible, rename the folder via FTP/SSH: `delicate-api-adapter` → `delicate-api-adapter.disabled`. WordPress will deactivate it on next request without erroring.

== Changelog ==

= 0.2.0 =
* Remote monitoring & control: optional, OFF by default. When enabled and paired with platform credentials, the adapter pushes lifecycle events (plugin activation/deactivation, settings saved, master switch toggled, feature toggled, errors logged, DCP dependency change) to the configured platform URL using a 2s non-blocking HTTP POST — the bakery's checkout is never held up. Also exposes signed REST endpoints under `/wp-json/delicate-api-adapter/v1/*` so the platform can read heartbeat / logs / settings / dependency status / decision traces and remotely toggle the master switch, push setting changes, clear logs, or rotate the test-mode coupon. Every remote control call is auditable on both sides (a `remote_control` row is written to the local log table, and the platform writes its own). HMAC-SHA256 signatures and bearer-token gating on every call; constant-time comparison.
* Secrets handling: new "DAA secret" and "Platform API token" fields are write-only — they are never displayed in the admin UI and are redacted as `***` in any REST response.

= 0.1.0 =
* Initial bootstrap: settings page, log table, dependency check, kill switch, HPOS declaration, feature-class scaffolding. No behaviour features active yet.
