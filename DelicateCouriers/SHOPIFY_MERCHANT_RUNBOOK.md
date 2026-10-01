# Delicate Courier — Shopify Setup Guide

This guide walks you through connecting your Shopify store to Delicate
Courier. You'll need about 15-20 minutes and access to your Shopify admin.

You'll need the following from your Delicate Courier rep, before you start:

- **Your Store ID** (a number, e.g. `42`)
- **Your Webhook Secret** (a string of random characters)
- **Your Rates Endpoint URL** (will look like
  `https://api2.delicatecourier.co.za/api/shopify/rates/42`)

If you don't have these, ask us before continuing.

---

## Part 1 — Create a custom app in your Shopify admin

This creates the secure connection between your Shopify store and our
platform. It's a one-time setup.

1. Log into your Shopify admin.
2. Go to **Settings** (gear icon in the bottom-left).
3. Click **Apps and sales channels**.
4. Click **Develop apps**.
5. If you see a "Allow custom app development" button, click it and confirm.
   (If you don't see this button, custom app development is already
   enabled — proceed to the next step.)
6. Click **Create an app** (top right).
7. Name the app **Delicate Courier**. Leave "App developer" as your account.
8. Click **Create app**.

You should now see an empty app screen with three tabs along the top:
**Overview**, **Configuration**, and **API credentials**.

---

## Part 2 — Configure permissions

Tell Shopify what our integration is allowed to do.

1. Click the **Configuration** tab.
2. Under **Admin API integration**, click **Configure**.
3. In the scopes list, tick the following:
   - `read_orders`
   - `write_assigned_fulfillment_orders`
   - `read_assigned_fulfillment_orders`
   - `write_merchant_managed_fulfillment_orders`
   - `read_merchant_managed_fulfillment_orders`
   - `read_locations`
   - `read_shipping`
   - `write_shipping`
   - `read_customers`
   - `read_products`
4. Click **Save** at the top of the page.

---

## Part 3 — Install the app on your store

1. Click the **API credentials** tab.
2. Click **Install app** (top right).
3. A confirmation screen will list the permissions you just configured.
   Click **Install** at the bottom.

You're now connected. Two important credentials are now visible on this
page:

- **Admin API access token** — starts with `shpat_`. **Reveal it, copy it,
  and send it to your Delicate Courier rep.** You'll only ever see this
  once. If you lose it, you'll need to regenerate it.
- **API secret key** — under "API key and secret key" section. **Copy
  this too and send to your Delicate Courier rep** — we use it to verify
  the order webhooks we receive from your store.

Send both to your Delicate Courier rep before continuing.

> **Security note:** these credentials let our platform talk to your
> store. Never share them outside Delicate Courier. If you ever suspect
> they've been exposed, click "Rotate" on the API credentials tab and
> send us the new values.

---

## Part 4 — Configure webhooks (order notifications)

Tell Shopify to notify us whenever an order is placed, updated, or
cancelled.

1. Still on the **API credentials** tab.
2. Scroll to the **Webhooks** section.
3. Set **Event version** to `2024-04` (or the most recent stable version).
4. Set **Format** to `JSON`.

You'll add three webhook subscriptions. For EACH of the three:

1. Click **Create webhook** (the link appears below the list).
2. Pick the **Event** from the dropdown.
3. Paste the URL.
4. Click **Save**.

The three webhooks:

| Event             | URL                                                          |
|-------------------|--------------------------------------------------------------|
| `Order creation`  | `https://api2.delicatecourier.co.za/api/webhooks/shopify/order` |
| `Order update`    | `https://api2.delicatecourier.co.za/api/webhooks/shopify/order` |
| `Order cancellation` | `https://api2.delicatecourier.co.za/api/webhooks/shopify/order` |

All three use the SAME URL. We distinguish them by the event type Shopify
sends in the request headers.

---

## Part 5 — Register Delicate Courier as a shipping carrier

This is the step that makes live Delicate Courier rates appear during
your customers' checkout.

**Important:** this step requires your Shopify plan to have the Carrier
Service API enabled — either via an annual plan, Advanced/Plus tier, or
the **Third-Party Calculated Shipping Rates** add-on. If you've been
using CI Shipping Rate By Distance, you already have this enabled.

The Carrier Service API doesn't have a UI form in Shopify's admin. We
register it for you. **Send your Delicate Courier rep these three values:**

- Your `myshopify.com` domain (e.g. `honeybeebaker.myshopify.com`)
- The Admin API access token you saved in Part 3 (starts with `shpat_`)
- Confirmation that you want "Delicate Courier" to appear at checkout

We will register the carrier service on our side, and within a few
minutes "Delicate Courier" will appear as a shipping option to your
customers at checkout, with prices calculated live from our system.

---

## Part 6 — Configure your shipping zones (only if needed)

You may need to update your shipping zones to allow Delicate Courier as
a carrier-calculated rate option.

1. Go to **Settings → Shipping and delivery**.
2. Click your shipping profile (often "General profile").
3. Under each zone you want Delicate Courier available in:
   - Click **Add rate**.
   - Choose **Use carrier or app to calculate rates**.
   - In the dropdown, find and select **Delicate Courier**.
   - (If you don't see Delicate Courier here, our rep hasn't yet completed
     Part 5. Wait a few minutes and refresh the page.)
   - Click **Done**.

You can keep your existing rates from CI Shipping (or others) in place
during testing. Once you're confident in our integration, you can remove
the old rates.

---

## Part 7 — Test the integration

1. Open your storefront in an incognito browser window.
2. Add an item to cart.
3. Proceed to checkout.
4. Enter a real delivery address.
5. At the "Shipping method" step, **Delicate Courier** should appear with
   a calculated price.
6. (Optional) Complete the order with a test payment method to verify
   that order webhooks flow through to our platform. Your Delicate
   Courier rep will confirm we received it.

---

## Troubleshooting

**"Delicate Courier doesn't appear at checkout"**
- Most common cause: the carrier service isn't yet registered. Confirm
  with your Delicate Courier rep that they've completed Part 5.
- Less common: the destination address can't be quoted for delivery. Try
  a known-good address (e.g. central Johannesburg).
- Check that the Carrier Service API is enabled on your plan
  (Settings → Plan in your Shopify admin).

**"My orders aren't being booked with Delicate Courier"**
- Check that the three webhooks in Part 4 are configured and active.
  Webhook history is visible in Settings → Notifications.
- Confirm the order's shipping method was "Delicate Courier" (not a
  flat rate or other carrier).

**"Customers aren't getting tracking emails"**
- Tracking info is pushed to Shopify automatically once Delicate Courier
  confirms collection. There may be a delay of 15-60 minutes after the
  order is placed.
- Shopify customer notifications must be enabled in Settings →
  Notifications.

---

## When you want to disconnect

To stop using Delicate Courier:

1. **Settings → Shipping and delivery → your profile** → remove the
   Delicate Courier rate from each zone.
2. **Settings → Apps and sales channels → Develop apps** → find the
   Delicate Courier app → uninstall.
3. Notify your Delicate Courier rep so we can deactivate the carrier
   service on our side.

---

## Appendix — Endpoint reference (for the Delicate Courier rep)

All Shopify integration endpoints live on the Delicate Courier API. The
public base URL differs per environment:

| Environment | API base URL                               |
|-------------|--------------------------------------------|
| Production  | `https://api2.delicatecourier.co.za`       |
| Dev         | `https://api2-dev.delicatecourier.co.za`   |
| Staging     | `https://api2-staging.delicatecourier.co.za` |

`{storeId}` below is the merchant's Delicate Courier Store ID.

### Merchant-facing (configured in the merchant's Shopify admin)

| Purpose | Method & path | Full production URL |
|---|---|---|
| Carrier Service live-rate callback (Part 5) | `POST /api/shopify/rates/{storeId}` | `https://api2.delicatecourier.co.za/api/shopify/rates/{storeId}` |
| Order webhook — create / update / cancel (Part 4) | `POST /api/webhooks/shopify/order` | `https://api2.delicatecourier.co.za/api/webhooks/shopify/order` |

Notes:
- The rates callback **always returns HTTP 200** (with a possibly-empty
  `rates` array) so Shopify never auto-deactivates the carrier service.
- The order webhook routes `orders/create`, `orders/updated` and
  `orders/cancelled` by the `X-Shopify-Topic` header; signatures are
  verified with the store's API secret key (the "Webhook Secret").

### Internal / SuperAdmin-only (used by the Delicate Courier rep, not the merchant)

These require a SuperAdmin Supabase JWT and are normally driven from the
SuperAdmin UI (**Shopify Rates** page) rather than called by hand.

| Purpose | Method & path |
|---|---|
| List active Shopify stores | `GET /api/shopify/stores` |
| Register / re-register the carrier service on a store (Part 5) | `POST /api/shopify/register-carrier-service/{storeId}` |
| Test a store's Admin API connection | `GET /api/shopify/test-connection/{storeId}` |
| Webhook receiver health check | `GET /api/webhooks/shopify/health` |
