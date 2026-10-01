// Features/Shopify/Carriers/ShopifyRatesController.cs
//
// ============================================================================
// SHOPIFY CARRIER SERVICE CALLBACK
// ============================================================================
//
// PURPOSE:
//   Shopify calls this endpoint *synchronously* during a customer's checkout
//   to fetch live Delicate Courier rates. We translate Shopify's rate-request
//   shape into a Shiplogic call, return the rates Shopify's format expects,
//   and the customer sees Delicate Courier as a shipping option.
//
// SHOPIFY REQUIREMENTS (READ THIS CAREFULLY):
//   1. Must respond within 10 seconds — Shopify will time out and silently
//      drop us from the carrier options if we're slower. In practice, aim
//      for under 5s. There is NO retry.
//   2. Empty rates response = no Delicate Courier option appears at checkout.
//      Shopify treats "no rates" as "this carrier can't serve this address."
//   3. Shopify caches our response per shop+cart for up to a few minutes.
//      Same call within the cache window won't hit us. Good for performance,
//      bad for testing — if you change cart contents and want a fresh quote,
//      add or remove an item.
//   4. The callback URL is registered ONCE per shop, by the merchant, using
//      the steps in the merchant runbook (or via Shopify's Admin API).
//      We don't register it — they do.
//
// AUTHENTICATION:
//   This endpoint is intentionally UNAUTHENTICATED. Shopify does not sign
//   carrier service requests with HMAC the way it does for webhooks. The
//   only verification we can do is by the shop_id / origin in the body.
//
//   This is by design on Shopify's part — they treat carrier service URLs
//   as semi-secret. The URL itself is the credential. Anyone who knows our
//   exact callback URL CAN call it and get a quote. That's acceptable for
//   our use case (rates aren't sensitive; calling us just makes us do work).
//
//   Mitigation: rate-limit the endpoint by IP. The [EnableRateLimiting]
//   attribute below uses the same "WebhookIp" policy as your existing
//   webhook controllers.
//
// PATH-BASED TENANCY:
//   The route includes {storeId} so a single platform instance can serve
//   many merchants. Each merchant configures their unique
//   "/api/shopify/rates/{their-store-id}" URL on their Shopify side.
//   Without this, we'd have to guess which merchant a request belongs to
//   from the address — fragile.
//
// SHOPIFY REQUEST SHAPE (what we receive):
//   POST /api/shopify/rates/{storeId}
//   {
//     "rate": {
//       "origin": {                  // merchant's warehouse/store
//         "country": "ZA",
//         "postal_code": "0084",
//         "province": "GP",
//         "city": "Pretoria",
//         "name": "Honey Bee Baker",
//         "address1": "5 Graham Road",
//         "address2": null,
//         "address3": null,
//         "phone": "",
//         "fax": null,
//         "email": null,
//         "address_type": null,
//         "company_name": null
//       },
//       "destination": {            // customer's delivery address
//         "country": "ZA",
//         "postal_code": "2000",
//         "province": "GP",
//         "city": "Johannesburg",
//         "name": "Jane Doe",
//         "address1": "1 Test Street",
//         ...
//       },
//       "items": [                  // cart contents
//         {
//           "name": "Beaux 15cm Cake",
//           "sku": "BEAUX-15",
//           "quantity": 1,
//           "grams": 2000,          // ← total weight in grams, multiplied by qty by Shopify
//           "price": 157300,        // ← in cents
//           "vendor": "Honey Bee Baker",
//           "requires_shipping": true,
//           "taxable": true,
//           "fulfillment_service": "manual",
//           "properties": {},
//           "product_id": 1234567890,
//           "variant_id": 9876543210
//         }
//       ],
//       "currency": "ZAR",
//       "locale": "en"
//     }
//   }
//
// SHOPIFY RESPONSE SHAPE (what we return):
//   {
//     "rates": [
//       {
//         "service_name": "Delicate Courier — Standard",
//         "service_code": "STD",
//         "total_price": "9845",           // ← in cents, as STRING
//         "currency": "ZAR",
//         "description": "Delivered within 1-2 business days",
//         "min_delivery_date": "2026-06-04T00:00:00 +0200",   // optional
//         "max_delivery_date": "2026-06-05T00:00:00 +0200"    // optional
//       }
//     ]
//   }
//
//   If we have no rates to offer (unserviceable, error, anything): return
//   { "rates": [] }. Shopify will simply not show Delicate Courier at
//   checkout. NEVER return a 4xx/5xx — it doesn't help and Shopify may
//   penalize us by dropping the carrier registration.
//
// ============================================================================

using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shopify.DTOs;
using DelicateCouriers.Features.Shiplogic;
using DelicateCouriers.Features.Shiplogic.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Shopify.Carriers;

[ApiController]
[Route("api/shopify/rates")]
[EnableRateLimiting("WebhookIp")]
public class ShopifyRatesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IShiplogicService _shiplogicService;
    private readonly ILogger<ShopifyRatesController> _logger;

    public ShopifyRatesController(
        AppDbContext context,
        IShiplogicService shiplogicService,
        ILogger<ShopifyRatesController> logger)
    {
        _context = context;
        _shiplogicService = shiplogicService;
        _logger = logger;
    }

    /// <summary>
    /// The carrier service callback. Shopify hits this during checkout.
    /// </summary>
    /// <remarks>
    /// We ALWAYS return HTTP 200 with a "rates" array — possibly empty.
    /// Returning anything else causes Shopify to think our endpoint is
    /// unhealthy and they may auto-deactivate our carrier service.
    /// </remarks>
    [HttpPost("{storeId:int}")]
    public async Task<IActionResult> GetRates(int storeId, [FromBody] ShopifyRateRequest? request)
    {
        try
        {
            if (request?.Rate == null)
            {
                _logger.LogWarning("Shopify rate request for store {StoreId} had no body or missing rate", storeId);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            // 1. Resolve the store. The path-based store ID is the merchant's
            //    Platform store, NOT their Shopify store URL. Treat invalid
            //    or inactive store as a no-op (return empty rates) rather
            //    than an error — Shopify keeps calling our endpoint and we
            //    don't want to keep returning 4xx.
            var store = await _context.Stores
                .IgnoreQueryFilters()    // carrier callback is unauthenticated
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.StoreID == storeId
                                          && s.IsActive
                                          && s.Platform == "shopify");

            if (store == null)
            {
                _logger.LogWarning("Shopify rate request for unknown/inactive store {StoreId}", storeId);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            // 2. Resolve the Shiplogic bearer token for this tenant. Today
            //    this likely lives on a Tenant entity / config table. The
            //    method below is a placeholder — wire it up to wherever your
            //    existing GetRatesController fetches the token.
            var bearer = await ResolveShiplogicBearerAsync(store.TenantID);
            if (string.IsNullOrEmpty(bearer))
            {
                _logger.LogError(
                    "Cannot quote rates for store {StoreId}: no Shiplogic bearer configured for tenant {TenantId}",
                    storeId, store.TenantID);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            // 3. Build the Shiplogic rate request. We use the merchant's
            //    persisted collection address rather than what Shopify
            //    sent us as `origin` — Shopify's origin is whatever the
            //    merchant configured under Locations, which sometimes
            //    isn't the address we have on file for them. Using our
            //    stored value keeps Shiplogic consistent with WooCommerce
            //    behaviour and means we can geocode it once.
            var collectionAddress = BuildCollectionAddressFromStore(store);
            if (collectionAddress == null)
            {
                _logger.LogError(
                    "Cannot quote rates for store {StoreId}: collection address is incomplete on the Store record",
                    storeId);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            var deliveryAddress = BuildDeliveryAddressFromShopify(request.Rate.Destination);
            if (deliveryAddress == null)
            {
                // Customer hasn't entered enough address info yet. Shopify
                // sometimes calls us optimistically with a partial address;
                // empty rates is the correct response.
                _logger.LogInformation(
                    "Shopify rate request for store {StoreId} had insufficient delivery address — returning empty",
                    storeId);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            var parcels = BuildParcelsFromShopifyItems(request.Rate.Items);

            // 4. Call Shiplogic. We use the WithDiagnostics variant so that
            //    if rates come back empty we have visibility into why (this
            //    is the same null-rates trap the WordPress plugin hit).
            //    Cancellation token: cap the upstream call at 7 seconds so
            //    we have headroom inside the 10s Shopify deadline for our
            //    own mapping work.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            var (rateResponse, diagnostics) = await _shiplogicService.GetRatesWithDiagnosticsAsync(
                bearer,
                collectionAddress,
                deliveryAddress,
                parcels,
                serviceLevelCode: null,
                cancellationToken: cts.Token);

            if (rateResponse?.Rates == null || rateResponse.Rates.Count == 0)
            {
                _logger.LogWarning(
                    "Shiplogic returned no rates for store {StoreId} to {Postcode}. Diagnostic: {Diag}",
                    storeId, deliveryAddress.PostalCode, diagnostics?.RawResponse);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            // 5. Map Shiplogic rates → Shopify response shape.
            //    Shopify wants price as a STRING of integer cents (or
            //    cents-equivalent for other currencies). Multiply by 100
            //    and round; the leading zeros and decimals are NOT allowed.
            var responseRates = rateResponse.Rates
                .Where(r => r.Rate > 0)
                .Select(r => new ShopifyRate
                {
                    ServiceName = $"Delicate Courier — {r.ServiceLevel?.Name ?? "Standard"}",
                    ServiceCode = r.ServiceLevel?.Code ?? "STD",
                    TotalPrice = ((int)Math.Round(r.Rate * 100m)).ToString(),
                    Currency = request.Rate.Currency ?? "ZAR",
                    Description = r.ServiceLevel?.Description ?? "Delivered by Delicate Courier"
                })
                .ToList();

            _logger.LogInformation(
                "Quoted {Count} Shopify rate(s) for store {StoreId} to {Postcode}",
                responseRates.Count, storeId, deliveryAddress.PostalCode);

            return Ok(new ShopifyRateResponse { Rates = responseRates });
        }
        catch (OperationCanceledException)
        {
            // Hit our 7-second internal deadline. Better to return empty
            // rates fast than time out and have Shopify mark us unhealthy.
            _logger.LogWarning(
                "Shopify rate request for store {StoreId} exceeded internal deadline — returning empty",
                storeId);
            return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
        }
        catch (Exception ex)
        {
            // Same defensive principle: never let Shopify see a 5xx from
            // this endpoint. Log and return empty.
            _logger.LogError(ex,
                "Unexpected error producing Shopify rates for store {StoreId}",
                storeId);
            return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // TODO(@engineer): wire this to wherever GetRatesController.cs reads
    // the bearer from today. If the bearer is on a Tenant entity, switch
    // the method body to:
    //
    //     var tenant = await _context.Tenants
    //         .AsNoTracking()
    //         .FirstOrDefaultAsync(t => t.TenantID == tenantId);
    //     return tenant?.ShiplogicToken;
    //
    // Keep the method async even if the body is synchronous; we may add
    // per-tenant override caching later.
    // ────────────────────────────────────────────────────────────────────
    private Task<string?> ResolveShiplogicBearerAsync(int tenantId)
    {
        throw new NotImplementedException(
            "Wire this to your existing tenant-token resolution. See GetRatesController for the pattern.");
    }

    private static AddressDto? BuildCollectionAddressFromStore(Domain.Entities.Store store)
    {
        if (string.IsNullOrWhiteSpace(store.CollectionAddressLine1) ||
            string.IsNullOrWhiteSpace(store.CollectionCity) ||
            string.IsNullOrWhiteSpace(store.CollectionPostalCode))
        {
            return null;
        }

        // NOTE: lat/lng deliberately omitted here. Your Shiplogic service's
        // geocoding wrapper (the one Wired into GetRatesController) handles
        // server-side geocoding. If this controller is called BEFORE that
        // wrapper, you'll need to call your IGeocoder here too — same
        // pattern as GetRatesController.GetRates.
        return new AddressDto
        {
            Type = "business",
            Company = store.StoreName,
            Street = store.CollectionAddressLine1!,
            Suburb = store.CollectionAddressLine2,
            City = store.CollectionCity!,
            Province = store.CollectionProvince,
            PostalCode = store.CollectionPostalCode!,
            Country = store.CollectionCountry ?? "ZA"
        };
    }

    private static AddressDto? BuildDeliveryAddressFromShopify(ShopifyAddress? dest)
    {
        if (dest == null) return null;
        if (string.IsNullOrWhiteSpace(dest.Address1) ||
            string.IsNullOrWhiteSpace(dest.City) ||
            string.IsNullOrWhiteSpace(dest.PostalCode))
        {
            return null;
        }

        return new AddressDto
        {
            Type = "residential",
            Street = dest.Address1!,
            Suburb = dest.Address2,
            City = dest.City!,
            Province = dest.Province,
            PostalCode = dest.PostalCode!,
            Country = string.IsNullOrWhiteSpace(dest.Country) ? "ZA" : dest.Country!
        };
    }

    private static List<ParcelDto> BuildParcelsFromShopifyItems(List<ShopifyItem>? items)
    {
        // Shopify sends items, not parcels. We collapse the cart into one
        // parcel sized to the cumulative weight. Production-grade packing
        // logic (multiple boxes, dimensional rules, etc.) is a future
        // optimization — same approach the WordPress plugin uses today.
        if (items == null || items.Count == 0)
        {
            return new List<ParcelDto>
            {
                new ParcelDto { SubmittedLengthCm = 20, SubmittedWidthCm = 15, SubmittedHeightCm = 10, SubmittedWeightKg = 1m }
            };
        }

        var totalGrams = items.Sum(i => (long)(i.Grams ?? 0) * Math.Max(1, i.Quantity ?? 1));
        var totalKg = Math.Max(1m, totalGrams / 1000m);

        return new List<ParcelDto>
        {
            new ParcelDto
            {
                ParcelDescription = "Order contents",
                SubmittedLengthCm = 30,
                SubmittedWidthCm = 25,
                SubmittedHeightCm = 20,
                SubmittedWeightKg = totalKg
            }
        };
    }
}
