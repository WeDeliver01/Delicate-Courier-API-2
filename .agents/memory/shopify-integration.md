---
name: Shopify integration gotchas
description: Durable constraints when working on the .NET Shopify feature (rates callback, order intake, tracking writeback).
---

# Shopify integration (.NET ApiService, Features/Shopify)

## snake_case JSON binding
Shopify sends snake_case keys. `System.Text.Json` web defaults are
case-INSENSITIVE but do NOT strip underscores, so `fulfillment_orders` will
NOT bind to a `FulfillmentOrders` property by case-insensitivity alone.
**Rule:** every Shopify-facing DTO property whose key contains an underscore
MUST carry an explicit `[JsonPropertyName("snake_case")]`. This applies to
inline/private response DTOs too (e.g. the fulfillment-orders wrapper).

## Carrier Service rate callback must ALWAYS return HTTP 200
Shopify auto-deactivates a carrier service that returns non-200. The callback
controller is `[ApiController]`, which means a `[FromBody]` model-bind/JSON
failure short-circuits to an automatic 400 BEFORE the action runs — bypassing
any try/catch fallback.
**Rule:** the rates action reads + deserializes the body MANUALLY (no
`[FromBody]` param) inside the try, so malformed/empty bodies degrade to a
200-empty response. Keep monetary fields tolerant (a string-or-number
converter) so a value-type mismatch can't fail deserialization and cost a quote.

## Custom-app token rotation revokes the token AND deletes our carrier service
A carrier service that was created successfully can later VANISH from the store
while only third-party apps' carrier services remain. Root cause is almost always
the store's **custom-app Admin API access token being rotated / the app being
reinstalled or having its scopes/secret regenerated** on the Shopify side.
**Symptom:** the stored token starts returning HTTP `401` with body
`{"errors":"[API] Invalid API key or access token (unrecognized login or wrong password)"}`,
and the "Delicate Couriers" carrier service is gone (Shopify removes carrier
services tied to the old credential grant), even though our earlier create
returned `"Carrier service created"`.
**Why:** Shopify ties API-created carrier services to the app credential that
created them; regenerating that credential drops them and invalidates the token.
**How to apply:** when a Shopify call 401s with that body, it is NOT a code/scope
bug and NOT a plan issue — the merchant must supply a FRESH access token, then
re-run register-carrier-service + register-webhooks (webhooks are tied to the same
app and are likely gone too). On a successful create the raw Shopify body is now
logged in `RegisterCarrierServiceAsync` for exactly this kind of diagnosis.

## Admin API on a custom storefront domain 301s to the .myshopify.com host
Hitting `https://<custom-domain>/admin/api/.../*.json` returns a `301` whose
`Location` points at the canonical `https://<shop>.myshopify.com/admin/...`.
**Why:** Shopify only serves the Admin REST API from the `.myshopify.com` host;
the custom domain just redirects. .NET `HttpClient` auto-follows the redirect AND
preserves the custom `X-Shopify-Access-Token` header (it only strips
`Authorization`), so the backend works against the custom domain. Plain `curl`
does NOT follow by default, so a manual probe shows a bare `301` with no body.
**How to apply:** when probing Shopify by hand, first resolve the real host from
the `Location` header (e.g. `cakeaways.myshopify.com`) and call that directly with
the token, or pass `curl -L`. A `301` on a hand probe is NOT a token/store problem.

## Shopify 64-bit order IDs vs WooCommerce pipeline
Shopify order `id` is 64-bit; the reused WooCommerce `PluginWebhookPayload`
carried `WooOrderId` as `int`. The dedupe key is `(StoreID, WooOrderID)` and
`Order.WooOrderID` is a **string** column.
**Why:** truncating a 64-bit id into 31 bits (the original scaffold approach)
can collide two distinct Shopify orders onto the same dedupe key and silently
overwrite order history.
**How to apply:** `WooOrderId` is now `long`. Do NOT reintroduce truncation.
Widening needed no DB migration (column already string) and all consumers
`.ToString()` it or assign int literals (implicit widening).
