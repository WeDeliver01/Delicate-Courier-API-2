using DelicateCouriers.Integration.Contract.Rates;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Rates;

/// <summary>
/// Checkout rates for the WooCommerce plugin.
///
/// Public and unauthenticated, because the plugin calls it from a shopper's checkout. That
/// shapes the error handling: a failure returns a short reference the merchant can quote at us,
/// and the detail goes to the log, because anything in the response body is readable by anyone
/// who can reach the endpoint.
/// </summary>
[ApiController]
[Route("api/public/shipping")]
public class RatesController : ControllerBase
{
    private readonly IMerchantDirectory _merchants;
    private readonly IEngineClient _engine;
    private readonly IEngineGeocoder _geocoder;
    private readonly ILogger<RatesController> _logger;

    public RatesController(
        IMerchantDirectory merchants,
        IEngineClient engine,
        IEngineGeocoder geocoder,
        ILogger<RatesController> logger)
    {
        _merchants = merchants;
        _engine = engine;
        _geocoder = geocoder;
        _logger = logger;
    }

    [HttpPost("rates")]
    public async Task<ActionResult<GetRatesResponseDto>> GetRates(
        [FromBody] GetRatesRequestDto request,
        CancellationToken cancellationToken)
    {
        var merchant = _merchants.Find(request.StoreId);
        if (merchant is null)
        {
            _logger.LogWarning("Store not found or inactive: {StoreId}", request.StoreId);
            return NotFound(new GetRatesResponseDto
            {
                Success = false,
                ErrorMessage = "Store not found or inactive"
            });
        }

        // The pin every quote is measured from. A merchant saved before this was validated
        // can still be sitting at 0,0, and the Engine does not refuse that — it prices it,
        // as a seven-thousand-kilometre collection. Quoting nothing is recoverable; quoting
        // six figures at a stranger's checkout is not.
        if (!IsPlausible(merchant.Collection.Latitude, merchant.Collection.Longitude))
        {
            _logger.LogError(
                "Store {StoreId} has no usable collection pin ({Lat},{Lng}); refusing to price",
                request.StoreId, merchant.Collection.Latitude, merchant.Collection.Longitude);
            return Ok(new GetRatesResponseDto
            {
                Success = false,
                ErrorMessage = "No shipping rates available"
            });
        }

        try
        {
            var destination = await ResolveDestination(request, cancellationToken);
            if (destination is null)
            {
                // Not an error: the shopper is probably still typing. Saying so plainly beats
                // a 500 that makes the merchant think we are down.
                _logger.LogInformation(
                    "No rate for store {StoreId}: delivery address could not be resolved",
                    request.StoreId);
                return Ok(new GetRatesResponseDto
                {
                    Success = false,
                    ErrorMessage = "No shipping rates available"
                });
            }

            if (merchant.ServiceLevels.Count == 0)
            {
                // Nothing to offer. Says so rather than returning an empty success, which the
                // plugin would render as "no delivery available" with no way to tell the
                // difference from a service that is down.
                _logger.LogWarning(
                    "Store {StoreId} has no service levels configured", request.StoreId);
                return Ok(new GetRatesResponseDto
                {
                    Success = false,
                    ErrorMessage = "No shipping rates available"
                });
            }

            // Priced together rather than one after another: a shopper is waiting on this, and
            // three levels in sequence is three round trips of latency at checkout.
            var quotes = await Task.WhenAll(merchant.ServiceLevels.Select(async level =>
            {
                try
                {
                    var quote = await _engine.CreateQuoteAsync(
                        merchant.EngineAccountRef,
                        BuildQuoteRequest(merchant, level, request, destination),
                        cancellationToken);

                    return (Rate: (RateOptionDto?)new RateOptionDto
                    {
                        ServiceLevelCode = level.CheckoutCode,
                        ServiceLevelName = level.CheckoutLabel,
                        Cost = ToRand(quote.Breakdown.TotalCents),
                        Currency = "ZAR",
                        EstimatedDeliveryDays = level.EstimatedDeliveryDays
                    }, Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    // One level failing must not take the others down with it. A shopper
                    // offered Standard when Express could not be priced is better served than
                    // one offered nothing. Kept rather than swallowed, because if they all
                    // fail the caller is owed the real answer.
                    _logger.LogWarning(ex,
                        "Store {StoreId}: could not price service level {Code}",
                        request.StoreId, level.CheckoutCode);
                    return (Rate: (RateOptionDto?)null, Error: ex);
                }
            }));

            var priced = quotes.Where(q => q.Rate is not null).Select(q => q.Rate!).ToList();

            if (priced.Count == 0)
            {
                // Nothing priced at all. That is the Engine being unavailable, not a shopper
                // typing an address we cannot reach, so it keeps the original contract: a 500
                // carrying a reference the merchant can quote, with the detail only in the log.
                // Reporting it as "no rates available" would tell a merchant their checkout is
                // working as intended while it silently sells nothing.
                var first = quotes.Select(q => q.Error).FirstOrDefault(e => e is not null);
                if (first is not null) throw first;

                _logger.LogError(
                    "Store {StoreId}: no service level could be priced", request.StoreId);
                return Ok(new GetRatesResponseDto
                {
                    Success = false,
                    ErrorMessage = "No shipping rates available"
                });
            }

            return Ok(new GetRatesResponseDto { Success = true, Rates = priced });
        }
        catch (Exception ex)
        {
            // A short reference in the response, the whole chain in the log. The caller is
            // unauthenticated, so the body must not describe our internals.
            var errorId = Guid.NewGuid().ToString("N")[..8];
            _logger.LogError(ex,
                "RATES_FAIL [{ErrorId}] StoreId={StoreId} Code={Code}",
                errorId, request.StoreId, (ex as EngineCallException)?.Code);

            return StatusCode(500, new GetRatesResponseDto
            {
                Success = false,
                ErrorMessage = $"Failed to retrieve shipping rates (ref {errorId})"
            });
        }
    }

    /// <summary>
    /// Integer cents to rand.
    ///
    /// The Engine holds money as cents precisely so nothing is ever rounded twice, and this is
    /// the one place that converts. Decimal, never double: the plugin puts this number on an
    /// invoice.
    /// </summary>
    internal static decimal ToRand(long cents) => cents / 100m;

    internal static string Describe(DeliveryAddressDto address) => string.Join(", ", new[]
    {
        address.StreetAddress,
        address.LocalArea,
        address.City,
        address.Zone,
        address.Code,
        address.Country
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// Where to deliver, preferring what the storefront already established.
    ///
    /// The plugin runs Google Places autocomplete on the merchant's own key, so by the
    /// time a shopper has picked a suggestion their coordinates are already known and
    /// already paid for. Geocoding the same address again is a second charge for an
    /// answer we were handed — on every rate request, and a checkout makes several as
    /// the shopper types.
    ///
    /// Falls back to geocoding when they are absent, which covers older plugins and
    /// anyone who typed an address without picking a suggestion.
    /// </summary>
    private async Task<ResolvedPlace?> ResolveDestination(
        GetRatesRequestDto request, CancellationToken cancellationToken)
    {
        var address = request.DeliveryAddress;

        if (address.Latitude is { } lat && address.Longitude is { } lng && IsPlausible(lat, lng))
        {
            _logger.LogDebug(
                "Store {StoreId}: pricing from storefront coordinates (place {PlaceId})",
                request.StoreId, address.PlaceId ?? "(none)");

            return new ResolvedPlace(
                Describe(address), lat, lng, address.LocalArea, address.City, address.Code);
        }

        return await _geocoder.ResolveAsync(Describe(address), cancellationToken);
    }

    /// <summary>
    /// A sanity check, not a validation.
    ///
    /// Zero/zero is the Gulf of Guinea and is what an uninitialised pair looks like;
    /// out-of-range values cannot be a place at all. Either would price a delivery to
    /// nowhere without complaint, so they fall through to the geocoder instead.
    /// </summary>
    private static bool IsPlausible(double lat, double lng) =>
        lat is >= -90 and <= 90
        && lng is >= -180 and <= 180
        && !(Math.Abs(lat) < 0.0001 && Math.Abs(lng) < 0.0001);

    private static EngineQuoteRequest BuildQuoteRequest(
        Merchant merchant,
        MerchantServiceLevel level,
        GetRatesRequestDto request,
        ResolvedPlace destination)
    {
        // One collection, one drop: a checkout is always a single delivery. The Engine prices
        // it as a loop from the depot, which is why it needs both ends rather than a distance.
        return new EngineQuoteRequest
        {
            ServiceLevelCode = level.EngineCode,
            Collection = new EngineCollection
            {
                Address = ToEngineAddress(merchant.Collection),
                Contact = new EngineContact
                {
                    Name = merchant.Collection.ContactName ?? merchant.Name,
                    Phone = merchant.Collection.ContactPhone ?? string.Empty
                }
            },
            Drops = new List<EngineDrop>
            {
                new()
                {
                    Address = new EngineAddress
                    {
                        Formatted = destination.Formatted,
                        Line1 = request.DeliveryAddress.StreetAddress,
                        Suburb = request.DeliveryAddress.LocalArea ?? destination.Suburb,
                        City = request.DeliveryAddress.City,
                        PostalCode = string.IsNullOrWhiteSpace(request.DeliveryAddress.Code)
                            ? destination.PostalCode
                            : request.DeliveryAddress.Code,
                        Country = "ZA",
                        Location = new EngineLatLng(destination.Latitude, destination.Longitude)
                    },
                    Recipient = new EngineContact
                    {
                        // A quote needs a recipient shaped like a real one, but at checkout
                        // the shopper may not have typed their details yet. These are not
                        // stored against anything — the booking sends the real ones.
                        Name = Fallback(request.DeliveryAddress.ContactName, "Customer"),
                        Phone = Fallback(request.DeliveryAddress.ContactPhone, "0000000000"),
                        Email = request.DeliveryAddress.ContactEmail
                    },
                    Parcels = ToParcels(merchant, request.Parcels)
                }
            }
        };
    }

    private static List<EngineParcel> ToParcels(Merchant merchant, List<ParcelInfoDto> parcels)
    {
        if (parcels.Count == 0)
        {
            return new List<EngineParcel>
            {
                new() { PackageTypeId = merchant.DefaultPackageTypeId, Quantity = 1 }
            };
        }

        return parcels
            .Select(p => new EngineParcel
            {
                PackageTypeId = merchant.DefaultPackageTypeId,
                Quantity = 1,
                WeightKg = p.WeightKg > 0 ? p.WeightKg : null,
                Description = p.Description
            })
            .ToList();
    }

    private static EngineAddress ToEngineAddress(MerchantAddress a) => new()
    {
        Formatted = a.Formatted,
        Line1 = a.Line1,
        Suburb = a.Suburb,
        City = a.City,
        PostalCode = a.PostalCode,
        Country = a.Country,
        Location = new EngineLatLng(a.Latitude, a.Longitude)
    };

    private static string Fallback(string? value, string whenMissing) =>
        string.IsNullOrWhiteSpace(value) ? whenMissing : value;
}
