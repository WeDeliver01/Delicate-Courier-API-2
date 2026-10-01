# Staging Smoke Test — Delicate Courier Platform WooCommerce Plugin

This runbook is the **manual live-fire test** we run against a real WooCommerce
staging site before shipping a new build of `delicate-courier-platform.zip` (or
any merchant-branded variant) to merchants.

The automated harness in `DelicateCouriers.Tests` proves the HMAC contract and
the JSON shape, but it does NOT exercise:

- A real WordPress install (option storage, hook firing, HPOS, transients).
- The real public webhook URL going through whatever proxy / CDN sits in front
  of `api2.delicatecourier.co.za`.
- The real Shiplogic rates endpoint with real credentials.
- The real Hangfire job that turns a pushed order into a Shiplogic shipment.

One operator should be able to run this end-to-end in ~20 minutes. Record
actual values in the "Actual" column as you go and attach the completed file
(or a copy) to the release ticket.

---

## 0. Prerequisites

Tick everything in this list before starting Section 1. If any item is
missing, stop and resolve it first.

- [ ] A WooCommerce staging site you control (NOT production), reachable on
      the public internet, running WordPress 5.8+ / PHP 7.4+ / WooCommerce
      6.0+.
- [ ] WordPress admin login for that site.
- [ ] Access to the Delicate Courier **staging** platform
      (`https://app2-staging.delicatecourier.co.za`) as a Tenant Admin or
      SuperAdmin.
- [ ] A staging Store record created in the platform for this WooCommerce
      site. Note its **Store ID** (numeric) and **Webhook Secret**.
- [ ] A Shiplogic account with a working **bearer token**, **Account ID**, and
      at least one **Provider ID** quotable to a South African address.
- [ ] The plugin ZIP under test: the current release ZIP from
      `Plugins/WooCommerce/Default Plugin/` (e.g.
      `delicate-courier-platform-v2.0.0.zip`) or the matching branded ZIP.
      Confirm the version in the ZIP filename matches what you intend to
      ship.
- [ ] Browser dev tools available; ability to read the WooCommerce log viewer
      at **WooCommerce → Status → Logs**.
- [ ] Ability to view backend logs for `api2-staging.delicatecourier.co.za`
      (deployment logs or the Hangfire dashboard at `/hangfire`).

| # | Prereq | Actual / notes |
|---|---|---|
| 0.1 | Staging WP URL | |
| 0.2 | Store ID | |
| 0.3 | Webhook Secret (last 4 chars only) | |
| 0.4 | Shiplogic Bearer Token (last 4 chars only) | |
| 0.5 | Shiplogic Account ID | |
| 0.6 | Shiplogic Provider ID | |
| 0.7 | Plugin ZIP filename + sha256 | |

---

## 1. Install & activate the plugin

1. In WP admin, go to **Plugins → Add New → Upload Plugin**.
2. Choose the ZIP from Prereq 0.6 and click **Install Now**, then
   **Activate**.
3. Confirm a new top-level menu item **Delicate Courier** appears in the
   admin sidebar.
4. Open **WooCommerce → Status → Logs** and confirm there is a
   `delicate-courier-platform-*.log` source available (it will be empty until
   debug logging is enabled).
5. Open **WooCommerce → Settings → Advanced → Features** and confirm the
   plugin is listed as compatible with **High-Performance Order Storage
   (HPOS)** (no incompatibility warning).

| Step | Expected | Actual | Pass? |
|---|---|---|---|
| 1.1 | Upload succeeds, no PHP fatal | | |
| 1.2 | Plugin activates without error | | |
| 1.3 | "Delicate Courier" menu visible | | |
| 1.4 | Log source registered | | |
| 1.5 | HPOS compatibility shows green | | |

**If activation fails:** copy the WP error message and skip to Section 7.

---

## 2. Configure secrets

1. Open **Delicate Courier** in the sidebar.
2. Enter:
   - **Store ID** (from Prereq 0.2)
   - **Webhook Secret** (from Prereq 0.3 — paste it; the field is masked)
   - **Shiplogic Bearer Token** (from Prereq 0.4 — paste your actual token)
   - **Shiplogic Account ID** (from Prereq 0.5, if the field is shown —
     branded builds hide it)
   - **Shiplogic Provider ID** (from Prereq 0.6, if the field is shown)
3. Enable **Enable live rates at checkout**.
4. Enable **Auto-sync orders to platform**.
5. Enable **Debug logging** (so we can read what happens in Section 4).
6. Click **Save Changes**.
7. Re-open the settings page and confirm the read-only summary table at the
   bottom shows the correct **Service Level Code** and **Collection address**
   for this build (branded builds should show the merchant's pre-baked
   address; default build should show your WooCommerce store address).

| Step | Expected | Actual | Pass? |
|---|---|---|---|
| 2.6 | "Settings saved" notice appears | | |
| 2.7 | Read-only summary matches build | | |

**Sanity check (optional but recommended):** SSH or use the WP CLI to run
`wp option get dcp_store_id` and `wp option get dcp_webhook_secret` — both
should return non-empty strings. Run `wp option list --autoload=on | grep
dcp_` and confirm **none** of the three secret options appear (they must be
`autoload=no`).

---

## 3. Built-in connection tests

Still on the **Delicate Courier** settings page, click each of the three
buttons in turn and record the result. Each button writes to the
`#dcp-test-output` pre-formatted box on the page.

### 3a. Test platform connection

1. Click **Test platform connection**.
2. Expect: `Platform reachable` (or equivalent success), HTTP 200, latency
   under ~2 seconds.
3. Failure modes to record:
   - HTTP 0 / WP_Error → outbound network is blocked or DNS for
     `api2-staging.delicatecourier.co.za` is wrong.
   - HTTP 404 → platform routing / wrong base URL baked into the plugin.
   - HTTP 5xx → platform health endpoint is down.

### 3b. Send signed test order

1. Click **Send signed test order**.
2. Expect: HTTP 200 with `{ "accepted": true, ... }` (or whatever the current
   platform success envelope is).
3. Cross-check on the platform: open
   `https://app2-staging.delicatecourier.co.za/orders`, filter by this Store,
   and confirm a new order appears in the last minute with the synthetic
   payload (order number, dummy customer, dummy address).
4. Failure modes to record:
   - HTTP 401 with "signature mismatch" → Webhook Secret in plugin does not
     match the Store record on the platform. Re-paste from the Store record.
   - HTTP 401 with "store not found" → Store ID is wrong.
   - HTTP 429 → rate limit — wait 60s and retry once.

### 3c. Test Shiplogic rates

1. Click **Test Shiplogic rates**.
2. Expect: a small JSON dump showing at least one rate (price > 0, service
   level present).
3. Failure modes to record:
   - HTTP 401 → Shiplogic bearer is wrong or expired.
   - HTTP 400 with "account_id" / "provider_id" → those IDs are not set for
     this build, or are not valid for this account.
   - Empty rates array → the test destination is not serviceable by the
     configured provider; not necessarily a plugin bug, but record it.

| Test | HTTP | Latency | Pass? | Notes |
|---|---|---|---|---|
| 3a Platform health | | | | |
| 3b Signed test order | | | | |
| 3c Shiplogic rates | | | | |

**Do not proceed to Section 4 unless all three are PASS.**

---

## 4. Real order — checkout flow & rate quote

1. In a fresh **incognito** browser window (so you're not logged in as
   admin), open the storefront.
2. Add a real product to the cart (one that has weight and dimensions set —
   otherwise Shiplogic may refuse to quote).
3. Go to **Checkout** and fill in a real South African shipping address you
   control.
4. In the shipping methods list, expect to see **at least one Delicate
   Courier Platform** option with a non-zero rate, sourced live from
   Shiplogic.
5. Open browser dev tools → Network and confirm the rate quote responded in
   under ~3 seconds for the first checkout, and under ~500ms on a refresh
   (the second one should be served from the 5-minute WP transient cache).
6. Place the order using a payment method that resolves to **Processing**
   (e.g. Cash on Delivery, or your test payment gateway in sandbox mode).
7. Record the WooCommerce order number.

| Step | Expected | Actual | Pass? |
|---|---|---|---|
| 4.4 | At least one DCP rate shown | | |
| 4.5 first | Rate quote < 3s | | |
| 4.5 cached | Rate quote < 500ms | | |
| 4.6 | Order created, status = Processing | | |
| 4.7 | Order number recorded | | |

---

## 5. Verify the order on the platform

1. Wait ~10 seconds. The plugin pushes on `woocommerce_order_status_*` and
   `woocommerce_payment_complete` synchronously with bounded retries.
2. In **WooCommerce → Status → Logs**, open the most recent
   `delicate-courier-platform-*` log. Expect to see lines tagged
   `order.push.start` and `order.push.success` for your order number.
3. On the platform (`https://app2-staging.delicatecourier.co.za/orders`):
   - [ ] Order appears, linked to the Store from Prereq 0.2.
   - [ ] Order number matches the WooCommerce order number from Step 4.7.
   - [ ] Order total matches.
   - [ ] **Line items**: every product in the WooCommerce order appears with
         the right quantity, price, and SKU.
   - [ ] **Shipping address**: street, city, state, postcode, country (ZA)
         all match what you entered at checkout.
   - [ ] **Customer**: name, email, phone match.
   - [ ] **Delivery date / collection date / occasion**: present if the
         WooCommerce site has the optional plugins (WP Time Slots Booking,
         WAPF, DTSC) installed; null/empty otherwise — record which.
4. On the platform, open the Hangfire dashboard at
   `https://api2-staging.delicatecourier.co.za/hangfire` (Admin / SuperAdmin
   login required) and confirm a **shipment creation job** was enqueued for
   this order and moved to **Succeeded** within ~30 seconds. If it's in
   **Failed**, click in and capture the exception.
5. On the platform shipment record, confirm the Shiplogic tracking
   reference / shipment ID is present.

| Step | Expected | Actual | Pass? |
|---|---|---|---|
| 5.2 | Log shows `order.push.success` | | |
| 5.3 order match | All fields match | | |
| 5.4 Hangfire | Job Succeeded | | |
| 5.5 Shipment | Tracking ref present | | |

---

## 6. Negative checks

Quick sanity tests that prove the failure paths are still healthy. Each one
should take under a minute.

1. **Bad secret rejected.** On the settings page, change the Webhook Secret
   to garbage, save, click **Send signed test order**. Expect HTTP 401 with a
   signature error. **Restore the real secret afterwards.**
2. **Disable auto-sync.** Toggle **Auto-sync orders to platform** off, save,
   place another small order. Expect: no `order.push.*` log lines, no new
   order on the platform. **Turn auto-sync back on afterwards.**
3. **Disable live rates.** Toggle **Enable live rates at checkout** off,
   save, refresh checkout in an incognito window. Expect: no DCP rate option
   appears (only your other configured WC shipping methods). **Turn it back
   on afterwards.**

| Check | Expected | Actual | Pass? |
|---|---|---|---|
| 6.1 | 401 signature mismatch | | |
| 6.2 | No push, no platform order | | |
| 6.3 | DCP rate disappears | | |

---

## 7. What to capture if it fails

If **any** step above fails, gather the following before opening a ticket —
without these we cannot reproduce or fix:

1. **Plugin debug log.** WP admin → **WooCommerce → Status → Logs** → pick
   the `delicate-courier-platform-*` source for today. Download the full file
   (not a copy-paste of the visible lines). Include at least 10 minutes
   before and after the failure.
2. **WP debug log** if present (`wp-content/debug.log`). Any PHP warnings or
   fatals related to `dcp_*` functions.
3. **Browser HAR.** For checkout / rate quote issues: open dev tools →
   Network → tick "Preserve log" → reproduce → right-click → "Save all as
   HAR with content". Sensitive headers (bearer, Authorization) should be
   redacted before sharing externally.
4. **Backend logs.** Use the deployment logs tool for
   `api2-staging.delicatecourier.co.za` and grep for the Store ID from
   Prereq 0.2 and the WooCommerce order number from Step 4.7. Capture a
   window from ~1 minute before the order was placed to ~5 minutes after.
5. **Hangfire job details.** If a job moved to **Failed**, expand it and copy
   the full exception + stack trace from the Hangfire UI.
6. **The exact build under test.** ZIP filename, sha256, and the value of the
   `Version:` header at the top of `delicate-courier-platform.php` inside the
   ZIP.
7. **This checklist with the Actual columns filled in.**

Attach all of the above to the release ticket and tag the platform on-call.

---

## Sign-off

| | Name | Date | Build | Result |
|---|---|---|---|---|
| Operator | | | | PASS / FAIL |
| Reviewer | | | | PASS / FAIL |

A run is only **PASS** if every row in Sections 1–6 is PASS. A single FAIL
blocks the release until either the plugin is fixed or the platform owner
explicitly waives the failing row in writing on the release ticket.
