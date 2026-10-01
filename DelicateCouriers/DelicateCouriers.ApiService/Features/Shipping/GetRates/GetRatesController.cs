using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;
using DelicateCouriers.Features.Shiplogic;
using DelicateCouriers.Features.Shiplogic.DTOs;
using DelicateCouriers.Features.Shipping.GetRates.DTOs;
using DelicateCouriers.Features.Shipping.SpecialTrip;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Features.Shipping.GetRates;

[ApiController]
[Route("api/public/shipping")]
public class GetRatesController : ControllerBase
{
    /// <summary>
    /// Label prefix for the distance-priced fallback rate served to the
    /// WooCommerce plugin. Order intake recognizes a special-trip order by
    /// this prefix on the chosen shipping method's title (the v2.9.x plugin
    /// renders our serviceLevelName verbatim as the Woo rate label), so the
    /// two must stay in sync.
    /// </summary>
    public const string SpecialTripLabelPrefix = "Special Trip Request";

    /// <summary>
    /// The label prefix actually served for a store's special-trip rate:
    /// the store's custom CheckoutRateLabel when set (e.g. "Standard baked
    /// goods delivery"), otherwise the default "Special Trip Request".
    /// Intake distinguishes it from a normal rate by the " (X km)" suffix.
    /// </summary>
    public static string EffectiveSpecialTripLabelPrefix(DelicateCouriers.Domain.Entities.Store store) =>
        string.IsNullOrWhiteSpace(store.CheckoutRateLabel) ? SpecialTripLabelPrefix : store.CheckoutRateLabel.Trim();

    private readonly AppDbContext _context;
    private readonly IShiplogicService _shiplogicService;
    private readonly IGeocoder _geocoder;
    private readonly ISpecialTripQuoter _specialTripQuoter;
    private readonly ILogger<GetRatesController> _logger;

    public GetRatesController(
        AppDbContext context,
        IShiplogicService shiplogicService,
        IGeocoder geocoder,
        ISpecialTripQuoter specialTripQuoter,
        ILogger<GetRatesController> logger)
    {
        _context = context;
        _shiplogicService = shiplogicService;
        _geocoder = geocoder;
        _specialTripQuoter = specialTripQuoter;
        _logger = logger;
    }

    /// <summary>
    /// Get shipping rates for customer checkout
    /// Public endpoint - no authentication required
    /// Called by WooCommerce when customer enters delivery address
    /// </summary>
    [HttpPost("rates")]
    public async Task<ActionResult<GetRatesResponseDto>> GetRates(
    [FromBody] GetRatesRequestDto request,
    CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received WooCommerce rates request: {@Request}", request);

        // Debug mode: caller must pass header X-Debug-Key matching the
        // RATES_DEBUG_SECRET env var. When set, the response includes the raw
        // Shiplogic request/response and exception chain so operators can
        // troubleshoot directly from a curl without having to grep deployment
        // logs. The secret env var must be non-empty for debug mode to engage
        // — there is no implicit "open" fallback.
        ShiplogicDiagnostics? capturedDiagnostics = null;
        var debugMode = IsDebugRequest();

        try
        {
            // 1. Fetch store + tenant
            var store = await _context.Stores
                .Include(s => s.Tenant)
                .FirstOrDefaultAsync(s => s.StoreID == request.StoreId && s.IsActive, cancellationToken);

            if (store == null)
            {
                _logger.LogWarning("Store not found or inactive: {StoreID}", request.StoreId);
                return NotFound(new GetRatesResponseDto
                {
                    Success = false,
                    ErrorMessage = "Store not found or inactive"
                });
            }

            // 2. Validate Shiplogic token
            var bearerToken = store.Tenant?.ShiplogicBearerToken;

            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                _logger.LogError("Shiplogic bearer token missing for StoreID: {StoreID}", request.StoreId);
                return StatusCode(500, new GetRatesResponseDto
                {
                    Success = false,
                    ErrorMessage = "Shipping provider not configured for this store"
                });
            }

            // 3. Map collection address
            var collectionAddress = new AddressDto
            {
                Type = "business",
                Company = store.CollectionCompanyName ?? store.StoreName,
                Street = store.CollectionAddressLine1 ?? "",
                Suburb = store.CollectionAddressLine2,
                City = store.CollectionCity ?? "",
                Province = store.CollectionProvince ?? "",
                Country = CountryNormalizer.ToIsoCode(store.CollectionCountry),
                PostalCode = store.CollectionPostalCode ?? "",
                Contact = new ContactDto
                {
                    Name = store.CollectionContactName ?? store.StoreName,
                    Mobile = store.CollectionContactPhone,
                    Email = store.CollectionContactEmail
                },
                AccountId = store.ShiplogicAccountId,
                ProviderId = store.ShiplogicProviderId
            };

            // 3a. Resolve collection lat/lng. Shiplogic's server-side
            // geocoder silently fails for some of our addresses and returns
            // rates:null. Empirically, supplying explicit lat/lng makes the
            // same payload return real rates. Prefer the store's stored
            // coordinates, then fall back to our geocoder (cached).
            if (store.CollectionLatitude.HasValue && store.CollectionLongitude.HasValue)
            {
                collectionAddress.Latitude = store.CollectionLatitude;
                collectionAddress.Longitude = store.CollectionLongitude;
            }
            else
            {
                var collectionGeo = await _geocoder.GeocodeAsync(new GeocodeQuery(
                    Street: store.CollectionAddressLine1,
                    Suburb: store.CollectionSuburb ?? store.CollectionAddressLine2,
                    City: store.CollectionCity,
                    Province: store.CollectionProvince,
                    PostalCode: store.CollectionPostalCode,
                    Country: collectionAddress.Country), cancellationToken);
                if (collectionGeo != null)
                {
                    collectionAddress.Latitude = collectionGeo.Latitude;
                    collectionAddress.Longitude = collectionGeo.Longitude;
                    // Persist back to the store so we don't pay the lookup again.
                    store.CollectionLatitude = collectionGeo.Latitude;
                    store.CollectionLongitude = collectionGeo.Longitude;
                    try { await _context.SaveChangesAsync(cancellationToken); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to persist geocoded coords back to StoreID={StoreID}", store.StoreID);
                    }
                }
            }

            _logger.LogInformation("Mapped collection address: {@CollectionAddress}", collectionAddress);

            // 4. Map delivery address
            var deliveryAddress = new AddressDto
            {
                Type = "residential",
                Company = request.DeliveryAddress.Company,
                Street = request.DeliveryAddress.StreetAddress,
                Suburb = request.DeliveryAddress.LocalArea,
                City = request.DeliveryAddress.City,
                Province = request.DeliveryAddress.Zone,
                Country = CountryNormalizer.ToIsoCode(request.DeliveryAddress.Country),
                PostalCode = request.DeliveryAddress.Code,
                Contact = new ContactDto
                {
                    Name = request.DeliveryAddress.ContactName ?? "Customer",
                    Mobile = request.DeliveryAddress.ContactPhone,
                    Email = request.DeliveryAddress.ContactEmail
                }
            };

            // 4a. Resolve delivery lat/lng (cached). Same rationale as the
            // collection-side lookup — Shiplogic returns rates:null when its
            // own geocoder can't resolve the destination.
            var deliveryGeo = await _geocoder.GeocodeAsync(new GeocodeQuery(
                Street: request.DeliveryAddress.StreetAddress,
                Suburb: request.DeliveryAddress.LocalArea,
                City: request.DeliveryAddress.City,
                Province: request.DeliveryAddress.Zone,
                PostalCode: request.DeliveryAddress.Code,
                Country: deliveryAddress.Country), cancellationToken);
            if (deliveryGeo != null)
            {
                deliveryAddress.Latitude = deliveryGeo.Latitude;
                deliveryAddress.Longitude = deliveryGeo.Longitude;
            }

            _logger.LogInformation("Mapped delivery address: {@DeliveryAddress}", deliveryAddress);

            // 5. Map parcels
            var parcels = request.Parcels.Select(p => new ParcelDto
            {
                Description = p.Description ?? "Package",
                Length = p.LengthCm,
                Width = p.WidthCm,
                Height = p.HeightCm,
                Weight = p.WeightKg
            }).ToList();

            _logger.LogInformation("Mapped parcels: {@Parcels}", parcels);

            // 6. Call Shiplogic
            var (ratesResponse, diagnostics) = await _shiplogicService.GetRatesWithDiagnosticsAsync(
                bearerToken,
                collectionAddress,
                deliveryAddress,
                parcels,
                store.DefaultServiceLevel,  // Pass "STD" from store config
                cancellationToken);
            capturedDiagnostics = diagnostics;

            _logger.LogInformation("Shiplogic returned {Count} rates: {@Rates}", ratesResponse.Rates.Count, ratesResponse.Rates);

            // 7. Return all available rates
            if (!ratesResponse.Rates.Any())
            {
                _logger.LogWarning("No rates found for StoreID: {StoreID}", request.StoreId);

                // Fallback: offer a distance-priced "Special Trip" rate when
                // the merchant has enabled it in store settings (cost-per-km
                // + their own Google Maps key). Served in the same payload
                // shape as a normal rate so the v2.9.x plugin renders it —
                // and applies the merchant's checkout markup to it — without
                // any plugin change. The serviceLevelCode MUST match the
                // plugin's configured service level (default "STD") or the
                // plugin's rate filter drops it.
                //
                // ONLY offered when the store has Woo REST credentials:
                // order intake recognizes a special-trip order by fetching
                // the chosen shipping method's label via Woo REST. Without
                // credentials the order would book as a standard shipment
                // instead of an SPX ad-hoc trip — worse than not offering
                // the rate at all.
                if (string.IsNullOrWhiteSpace(store.WooCommerceURL) ||
                    string.IsNullOrWhiteSpace(store.WooConsumerKey) ||
                    string.IsNullOrWhiteSpace(store.WooConsumerSecret))
                {
                    _logger.LogInformation(
                        "Special trip for store {StoreId}: not offered — store has no Woo REST credentials, so intake could not recognize the order as a special trip",
                        store.StoreID);
                    return Ok(new GetRatesResponseDto
                    {
                        Success = false,
                        ErrorMessage = "No shipping rates available",
                        Debug = debugMode ? BuildDebugInfo(capturedDiagnostics, null) : null
                    });
                }

                var quote = await _specialTripQuoter.TryQuoteAsync(
                    store,
                    collectionAddress.Latitude, collectionAddress.Longitude,
                    deliveryAddress.Latitude, deliveryAddress.Longitude,
                    cancellationToken);
                if (quote != null)
                {
                    var code = string.IsNullOrWhiteSpace(store.DefaultServiceLevel) ? "STD" : store.DefaultServiceLevel;
                    return Ok(new GetRatesResponseDto
                    {
                        Success = true,
                        Rates = new List<RateOptionDto>
                        {
                            new RateOptionDto
                            {
                                ServiceLevelCode = code,
                                ServiceLevelName = $"{EffectiveSpecialTripLabelPrefix(store)} ({quote.DistanceKm:0.#} km)",
                                Cost = quote.Amount,
                                Currency = "ZAR",
                                EstimatedDeliveryDays = 1
                            }
                        },
                        Debug = debugMode ? BuildDebugInfo(capturedDiagnostics, null) : null
                    });
                }

                return Ok(new GetRatesResponseDto
                {
                    Success = false,
                    ErrorMessage = "No shipping rates available",
                    Debug = debugMode ? BuildDebugInfo(capturedDiagnostics, null) : null
                });
            }

            return Ok(new GetRatesResponseDto
            {
                Success = true,
                Rates = ratesResponse.Rates.Select(r => new RateOptionDto
                {
                    ServiceLevelId = r.ServiceLevelId,
                    ServiceLevelCode = r.ServiceLevelCode ?? "STD",
                    ServiceLevelName = !string.IsNullOrWhiteSpace(store.CheckoutRateLabel)
                        ? store.CheckoutRateLabel
                        : (r.ServiceLevelName ?? "Standard Delivery"),
                    Cost = r.Rate,
                    Currency = "ZAR",
                    EstimatedDeliveryDays = r.EstimatedDeliveryDays ?? 2
                }).ToList(),
                Debug = debugMode ? BuildDebugInfo(capturedDiagnostics, null) : null
            });
        }
        catch (Exception ex)
        {
            // Generate a short correlation id, log the full exception chain against it,
            // and return ONLY the id to the (unauthenticated, public) client. This keeps
            // Shiplogic response bodies / framework internals out of the public response
            // while still letting us grep prod deployment logs for the exact failure.
            var errorId = Guid.NewGuid().ToString("N").Substring(0, 8);

            var detail = ex.Message;
            var inner = ex.InnerException;
            while (inner != null)
            {
                detail += " -> " + inner.Message;
                inner = inner.InnerException;
            }

            _logger.LogError(ex,
                "RATES_FAIL [{ErrorId}] StoreID={StoreID} ExceptionChain={Detail}",
                errorId, request.StoreId, detail);

            // If the failure came from inside the Shiplogic call, pull
            // diagnostics off the typed exception so debug mode can surface
            // the raw response body that triggered the failure.
            if (ex is ShiplogicCallException sce)
            {
                capturedDiagnostics = sce.Diagnostics;
            }

            return StatusCode(500, new GetRatesResponseDto
            {
                Success = false,
                ErrorMessage = $"Failed to retrieve shipping rates (ref {errorId})",
                Debug = debugMode ? BuildDebugInfo(capturedDiagnostics, detail) : null
            });
        }
    }

    private bool IsDebugRequest()
    {
        var expected = Environment.GetEnvironmentVariable("RATES_DEBUG_SECRET");
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        if (!Request.Headers.TryGetValue("X-Debug-Key", out var provided))
        {
            return false;
        }

        var providedValue = provided.ToString();
        if (string.IsNullOrEmpty(providedValue))
        {
            return false;
        }

        // Hash both sides to a fixed 32-byte length BEFORE the constant-time
        // compare. FixedTimeEquals returns early on unequal lengths, so
        // comparing raw UTF-8 bytes would leak the secret's length via timing.
        // SHA-256 produces a fixed-length digest regardless of input length.
        using var sha = System.Security.Cryptography.SHA256.Create();
        var a = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(providedValue));
        var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(expected));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    private const int MaxDebugBodyChars = 8 * 1024;

    private static DebugInfoDto BuildDebugInfo(ShiplogicDiagnostics? d, string? exceptionChain)
    {
        return new DebugInfoDto
        {
            ShiplogicEndpoint = d?.Endpoint,
            ShiplogicRequestJson = RedactRequestJson(d?.RequestJson),
            ShiplogicStatusCode = d?.StatusCode,
            ShiplogicRawResponseBody = Truncate(d?.RawResponseBody, MaxDebugBodyChars),
            ExceptionChain = exceptionChain
        };
    }

    private static string? Truncate(string? value, int maxChars)
    {
        if (value == null) return null;
        if (value.Length <= maxChars) return value;
        return value.Substring(0, maxChars) + $"... [truncated, {value.Length - maxChars} more chars]";
    }

    /// <summary>
    /// Redacts customer / contact PII fields from the Shiplogic request JSON
    /// before exposing it via the debug endpoint. The secret guarding the
    /// debug endpoint is global, so a leaked secret would otherwise expose
    /// every customer's name / phone / email that flows through this call.
    /// </summary>
    private static string? RedactRequestJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node == null) return json;

            RedactAddressNode(node["collection_address"]);
            RedactAddressNode(node["delivery_address"]);

            return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            // If parsing fails for any reason, fall back to the safer option:
            // omit the field entirely rather than risk leaking unredacted PII.
            return "[redaction failed; request body omitted]";
        }
    }

    private static void RedactAddressNode(System.Text.Json.Nodes.JsonNode? addr)
    {
        if (addr == null) return;
        // Redact identifying fields. Street/city/postal stay so the operator
        // can still see what address was sent (which is the whole point of
        // debug mode).
        if (addr["company"] != null) addr["company"] = "[redacted]";
        if (addr["contact"] is { } contact)
        {
            if (contact["name"] != null) contact["name"] = "[redacted]";
            if (contact["mobile"] != null) contact["mobile"] = "[redacted]";
            if (contact["email"] != null) contact["email"] = "[redacted]";
        }
    }
}
