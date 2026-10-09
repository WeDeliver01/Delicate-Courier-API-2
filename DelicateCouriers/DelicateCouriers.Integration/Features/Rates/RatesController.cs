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

        try
        {
            var destination = await _geocoder.ResolveAsync(
                Describe(request.DeliveryAddress), cancellationToken);
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

            var quote = await _engine.CreateQuoteAsync(
                merchant.EngineAccountRef,
                BuildQuoteRequest(merchant, request, destination),
                cancellationToken);

            return Ok(new GetRatesResponseDto
            {
                Success = true,
                Rates = new List<RateOptionDto>
                {
                    new()
                    {
                        ServiceLevelCode = merchant.CheckoutServiceLevelCode,
                        ServiceLevelName = merchant.CheckoutRateLabel,
                        Cost = ToRand(quote.Breakdown.TotalCents),
                        Currency = "ZAR",
                        EstimatedDeliveryDays = merchant.EstimatedDeliveryDays
                    }
                }
            });
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

    private static EngineQuoteRequest BuildQuoteRequest(
        Merchant merchant,
        GetRatesRequestDto request,
        ResolvedPlace destination)
    {
        // One collection, one drop: a checkout is always a single delivery. The Engine prices
        // it as a loop from the depot, which is why it needs both ends rather than a distance.
        return new EngineQuoteRequest
        {
            ServiceLevelCode = merchant.EngineServiceLevelCode,
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
