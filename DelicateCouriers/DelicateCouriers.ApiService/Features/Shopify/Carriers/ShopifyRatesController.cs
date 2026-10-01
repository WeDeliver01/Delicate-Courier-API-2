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
// SHOPIFY REQUIREMENTS:
//   1. Must respond within 10 seconds — Shopify will time out and silently
//      drop us from the carrier options if we're slower. There is NO retry.
//   2. Empty rates response = no Delicate Courier option appears at checkout.
//   3. Shopify caches our response per shop+cart for up to a few minutes.
//   4. The callback URL is registered ONCE per shop (Admin API / runbook).
//
// AUTHENTICATION:
//   This endpoint is intentionally UNAUTHENTICATED. Shopify does not sign
//   carrier service requests with HMAC the way it does for webhooks. The URL
//   itself (with the {storeId}) is the credential. We rate-limit by IP via
//   the shared "WebhookIp" policy.
//
// We ALWAYS return HTTP 200 with a "rates" array — possibly empty. Returning
// anything else causes Shopify to think the endpoint is unhealthy and they
// may auto-deactivate our carrier service.
// ============================================================================

using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shopify.DTOs;
using DelicateCouriers.ApiService.Infrastructure;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;
using DelicateCouriers.Features.Shipping.SpecialTrip;
using DelicateCouriers.Features.Shiplogic;
using DelicateCouriers.Features.Shiplogic.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.Shopify.Carriers;

[ApiController]
[Route("api/shopify/rates")]
[EnableRateLimiting("WebhookIp")]
public class ShopifyRatesController : ControllerBase
{
    /// <summary>
    /// ServiceCode returned for the distance-priced fallback rate. Shopify
    /// echoes it back as shipping_lines[].code on the order, which is how
    /// ingestion recognizes a special-trip order and books it as SPX.
    /// </summary>
    public const string SpecialTripServiceCode = "SPECIAL_TRIP";

    private readonly AppDbContext _context;
    private readonly IShiplogicService _shiplogicService;
    private readonly IGeocoder _geocoder;
    private readonly ISpecialTripQuoter _specialTripQuoter;
    private readonly ILogger<ShopifyRatesController> _logger;

    public ShopifyRatesController(
        AppDbContext context,
        IShiplogicService shiplogicService,
        IGeocoder geocoder,
        ISpecialTripQuoter specialTripQuoter,
        ILogger<ShopifyRatesController> logger)
    {
        _context = context;
        _shiplogicService = shiplogicService;
        _geocoder = geocoder;
        _specialTripQuoter = specialTripQuoter;
        _logger = logger;
    }

    /// <summary>
    /// The carrier service callback. Shopify hits this during checkout.
    /// </summary>
    [HttpPost("{storeId:int}")]
    public async Task<IActionResult> GetRates(int storeId)
    {
        try
        {
            // Read + parse the body MANUALLY rather than via [FromBody]. With
            // [ApiController], a model-binding/JSON failure on a bound body
            // parameter short-circuits to an automatic HTTP 400 BEFORE this
            // action runs — which would violate Shopify's "always 200" contract
            // and risk Shopify auto-deactivating our carrier service. Parsing
            // here means any malformed body is caught below and degrades to an
            // empty-rates 200.
            ShopifyRateRequest? request;
            using (var reader = new StreamReader(Request.Body))
            {
                var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);
                request = string.IsNullOrWhiteSpace(rawBody)
                    ? null
                    : JsonSerializer.Deserialize<ShopifyRateRequest>(
                        rawBody,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }

            if (request?.Rate == null)
            {
                _logger.LogWarning("Shopify rate request for store {StoreId} had no body or missing rate", storeId);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            // 1. Resolve the store + tenant. The path-based store ID is the
            //    merchant's Platform store. Treat invalid/inactive store as a
            //    no-op (empty rates) rather than an error — Shopify keeps
            //    calling and we don't want to return 4xx.
            var store = await _context.Stores
                .IgnoreQueryFilters()    // carrier callback is unauthenticated
                .AsNoTracking()
                .Include(s => s.Tenant)
                .FirstOrDefaultAsync(s => s.StoreID == storeId
                                          && s.IsActive
                                          && s.Platform == "shopify");

            if (store == null)
            {
                _logger.LogWarning("Shopify rate request for unknown/inactive store {StoreId}", storeId);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            // 2. Resolve the Shiplogic bearer token for this tenant (same
            //    source GetRatesController uses).
            var bearer = store.Tenant?.ShiplogicBearerToken;
            if (string.IsNullOrWhiteSpace(bearer))
            {
                _logger.LogError(
                    "Cannot quote rates for store {StoreId}: no Shiplogic bearer configured for tenant {TenantId}",
                    storeId, store.TenantID);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            // 3. Build the collection address from the merchant's persisted
            //    Store record (not Shopify's `origin`), mirroring the
            //    WooCommerce rates path so geocoding stays consistent.
            var collectionAddress = await BuildCollectionAddressFromStoreAsync(store, HttpContext.RequestAborted);
            if (collectionAddress == null)
            {
                _logger.LogError(
                    "Cannot quote rates for store {StoreId}: collection address is incomplete on the Store record",
                    storeId);
                return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
            }

            var deliveryAddress = await BuildDeliveryAddressFromShopifyAsync(request.Rate.Destination, HttpContext.RequestAborted);
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
            //    if rates come back empty we have visibility into why. Cap the
            //    upstream call at 7 seconds to stay inside Shopify's 10s
            //    deadline with headroom for our own mapping work.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(7));
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
                    storeId, deliveryAddress.PostalCode, diagnostics?.RawResponseBody);

                // Fallback: offer a distance-priced "Special Trip" rate when
                // the merchant has enabled it (SpecialTripCostPerKm set).
                var specialTrip = await TryBuildSpecialTripRateAsync(
                    store, collectionAddress, deliveryAddress, request.Rate.Currency, cts.Token);
                return Ok(new ShopifyRateResponse
                {
                    Rates = specialTrip != null
                        ? new List<ShopifyRate> { specialTrip }
                        : new List<ShopifyRate>()
                });
            }

            // 5. Map Shiplogic rates → Shopify response shape. Shopify wants
            //    price as a STRING of integer cents.
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
            // Hit our internal deadline (or the client aborted). Better to
            // return empty rates fast than time out and have Shopify mark us
            // unhealthy.
            _logger.LogWarning(
                "Shopify rate request for store {StoreId} was cancelled/exceeded internal deadline — returning empty",
                storeId);
            return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
        }
        catch (Exception ex)
        {
            // Never let Shopify see a 5xx from this endpoint. Log and return
            // empty.
            _logger.LogError(ex,
                "Unexpected error producing Shopify rates for store {StoreId}",
                storeId);
            return Ok(new ShopifyRateResponse { Rates = new List<ShopifyRate>() });
        }
    }

    /// <summary>
    /// Build the collection AddressDto from the store record, including the
    /// nested contact and explicit lat/lng. Mirrors GetRatesController so
    /// Shiplogic doesn't return rates:null for a geocode it can't resolve.
    /// </summary>
    /// <summary>
    /// Build the "Special Trip" fallback rate: one-way driving distance from
    /// the shop to the customer × the store's R/km, with a minimum fee, only
    /// within the store's max distance. Returns null when the fallback is
    /// disabled, coordinates are unknown, no route exists, or the distance
    /// exceeds the cap — the caller then returns empty rates as before.
    /// </summary>
    private async Task<ShopifyRate?> TryBuildSpecialTripRateAsync(
        Domain.Entities.Store store,
        AddressDto collectionAddress,
        AddressDto deliveryAddress,
        string? currency,
        CancellationToken ct)
    {
        var quote = await _specialTripQuoter.TryQuoteAsync(
            store,
            collectionAddress.Latitude, collectionAddress.Longitude,
            deliveryAddress.Latitude, deliveryAddress.Longitude,
            ct);
        if (quote == null) return null;

        return new ShopifyRate
        {
            ServiceName = $"Delicate Courier — Special Trip ({quote.DistanceKm:0.#} km)",
            ServiceCode = SpecialTripServiceCode,
            TotalPrice = ((int)Math.Round(quote.Amount * 100m)).ToString(),
            Currency = currency ?? "ZAR",
            Description = "Dedicated direct trip from the shop to your address"
        };
    }

    private async Task<AddressDto?> BuildCollectionAddressFromStoreAsync(Domain.Entities.Store store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(store.CollectionAddressLine1) ||
            string.IsNullOrWhiteSpace(store.CollectionCity) ||
            string.IsNullOrWhiteSpace(store.CollectionPostalCode))
        {
            return null;
        }

        var address = new AddressDto
        {
            Type = "business",
            Company = store.CollectionCompanyName ?? store.StoreName,
            Street = store.CollectionAddressLine1!,
            Suburb = store.CollectionSuburb ?? store.CollectionAddressLine2,
            City = store.CollectionCity!,
            Province = store.CollectionProvince ?? "",
            Country = CountryNormalizer.ToIsoCode(store.CollectionCountry),
            PostalCode = store.CollectionPostalCode!,
            Contact = new ContactDto
            {
                Name = store.CollectionContactName ?? store.StoreName,
                Mobile = store.CollectionContactPhone,
                Email = store.CollectionContactEmail
            },
            AccountId = store.ShiplogicAccountId,
            ProviderId = store.ShiplogicProviderId
        };

        // Prefer the store's persisted coordinates, fall back to the geocoder
        // (cached). We don't persist back here — this is an unauthenticated,
        // read-only (AsNoTracking) path called frequently during checkout.
        if (store.CollectionLatitude.HasValue && store.CollectionLongitude.HasValue)
        {
            address.Latitude = store.CollectionLatitude;
            address.Longitude = store.CollectionLongitude;
        }
        else
        {
            var geo = await _geocoder.GeocodeAsync(new GeocodeQuery(
                Street: store.CollectionAddressLine1,
                Suburb: store.CollectionSuburb ?? store.CollectionAddressLine2,
                City: store.CollectionCity,
                Province: store.CollectionProvince,
                PostalCode: store.CollectionPostalCode,
                Country: address.Country), ct);
            if (geo != null)
            {
                address.Latitude = geo.Latitude;
                address.Longitude = geo.Longitude;
            }
        }

        return address;
    }

    private async Task<AddressDto?> BuildDeliveryAddressFromShopifyAsync(ShopifyAddress? dest, CancellationToken ct)
    {
        if (dest == null) return null;
        if (string.IsNullOrWhiteSpace(dest.Address1) ||
            string.IsNullOrWhiteSpace(dest.City) ||
            string.IsNullOrWhiteSpace(dest.PostalCode))
        {
            return null;
        }

        var country = CountryNormalizer.ToIsoCode(string.IsNullOrWhiteSpace(dest.Country) ? "ZA" : dest.Country);
        var address = new AddressDto
        {
            Type = "residential",
            Street = dest.Address1!,
            Suburb = dest.Address2,
            City = dest.City!,
            Province = dest.Province ?? "",
            PostalCode = dest.PostalCode!,
            Country = country,
            Contact = new ContactDto
            {
                Name = string.IsNullOrWhiteSpace(dest.Name) ? "Customer" : dest.Name,
                Mobile = dest.Phone
            }
        };

        var geo = await _geocoder.GeocodeAsync(new GeocodeQuery(
            Street: dest.Address1,
            Suburb: dest.Address2,
            City: dest.City,
            Province: dest.Province,
            PostalCode: dest.PostalCode,
            Country: country), ct);
        if (geo != null)
        {
            address.Latitude = geo.Latitude;
            address.Longitude = geo.Longitude;
        }

        return address;
    }

    private static List<ParcelDto> BuildParcelsFromShopifyItems(List<ShopifyItem>? items)
    {
        // Shopify sends items, not parcels. We collapse the cart into one
        // parcel sized to the cumulative weight — same approach the
        // WordPress plugin uses today.
        if (items == null || items.Count == 0)
        {
            return new List<ParcelDto>
            {
                new ParcelDto { Description = "Order contents", Length = 20, Width = 15, Height = 10, Weight = 1m }
            };
        }

        var totalGrams = items.Sum(i => (long)(i.Grams ?? 0) * Math.Max(1, i.Quantity ?? 1));
        var totalKg = Math.Max(1m, totalGrams / 1000m);

        return new List<ParcelDto>
        {
            new ParcelDto
            {
                Description = "Order contents",
                Length = 30,
                Width = 25,
                Height = 20,
                Weight = totalKg
            }
        };
    }
}
