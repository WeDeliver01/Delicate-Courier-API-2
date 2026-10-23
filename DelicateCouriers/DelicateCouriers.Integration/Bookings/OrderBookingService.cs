using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Merchants;
using DelicateCouriers.Integration.Storefront;

namespace DelicateCouriers.Integration.Bookings;

public enum BookingOutcome
{
    /// <summary>A delivery now exists for this order.</summary>
    Booked,

    /// <summary>One already existed. The same answer, arriving late.</summary>
    AlreadyBooked,

    /// <summary>The rules say this order is not a delivery, or not one yet.</summary>
    Skipped,

    /// <summary>Did not book, and will not on a retry either.</summary>
    Rejected
}

public sealed record BookingResult(
    BookingOutcome Outcome,
    string Reason,
    string? BookingReference = null,
    string? Waybill = null)
{
    public bool Booked => Outcome is BookingOutcome.Booked or BookingOutcome.AlreadyBooked;
}

/// <summary>
/// Turning a WooCommerce order into a delivery.
///
/// Two things this deliberately does *not* do, because the Engine does them better:
///
/// It keeps no local lock and no local shipment row to guard against double-booking. The
/// idempotency key travels to the Engine, which holds a unique index on it and resolves the
/// race inside the transaction that creates the booking. A lock here could only protect this
/// process; the Engine's constraint protects every caller, including a human in the portal.
///
/// It does not re-price at booking time by default. The Engine quotes as part of booking, and
/// the shopper's checkout figure is carried as a ceiling rather than as the price — so a
/// quote that has drifted upward refuses instead of quietly charging more than was agreed.
/// </summary>
public sealed class OrderBookingService
{
    private readonly IEngineClient _engine;
    private readonly IEngineGeocoder _geocoder;
    private readonly IStorefront _storefront;
    private readonly ILogger<OrderBookingService> _logger;

    public OrderBookingService(
        IEngineClient engine,
        IEngineGeocoder geocoder,
        IStorefront storefront,
        ILogger<OrderBookingService> logger)
    {
        _engine = engine;
        _geocoder = geocoder;
        _storefront = storefront;
        _logger = logger;
    }

    public async Task<BookingResult> BookAsync(
        Merchant merchant,
        PluginOrderPayload payload,
        CancellationToken cancellationToken)
    {
        var decision = BookingRules.Evaluate(merchant, payload);
        if (!decision.ShouldBook)
        {
            _logger.LogInformation(
                "Not booking WooOrderID {WooOrderId} for store {StoreId}: {Reason}",
                payload.WooOrderId, merchant.StoreId, decision.Reason);
            return new BookingResult(BookingOutcome.Skipped, decision.Reason);
        }

        // The payload says this is a delivery, but the plugin decides that by looking only at
        // the shipping method's id — so a merchant selling collection as an ordinary flat-rate
        // method arrives here tagged "delivery". Ask the storefront what was really chosen.
        //
        // A lookup that fails does not fall through to booking. Not knowing whether the
        // customer is collecting is exactly the case this guard exists for, so it throws and
        // the job tries again later rather than guessing.
        var method = await _storefront.GetShippingMethodAsync(
            merchant, payload.WooOrderId, cancellationToken);
        if (Fulfillment.IsCollection(method))
        {
            _logger.LogInformation(
                "Not booking WooOrderID {WooOrderId}: chosen shipping method is collection ({Title})",
                payload.WooOrderId, method!.MethodTitle ?? method.MethodId);
            return new BookingResult(BookingOutcome.Skipped, "collection_shipping_method");
        }

        // Which delivery option to book. Prefer the one the shopper actually chose: a
        // merchant offering next-day and same-day must not deliver the cheaper one to someone
        // who paid for the dearer.
        //
        // No match is not a failure. A merchant may list our rates beside their own flat rate,
        // and a shopper who picks theirs still wants delivering — refusing would stop booking
        // orders that have always booked. The same goes for a merchant with no REST
        // credentials, who cannot be asked at all. Both fall back to the first level, which is
        // how every merchant behaved when there was only one.
        var level = Fulfillment.MatchServiceLevel(merchant, method)
                    ?? merchant.ServiceLevels.FirstOrDefault();

        if (level is null)
        {
            _logger.LogWarning(
                "Cannot book WooOrderID {WooOrderId}: store {StoreId} offers no service levels",
                payload.WooOrderId, merchant.StoreId);
            return new BookingResult(BookingOutcome.Rejected, "no_service_levels");
        }

        if (merchant.ServiceLevels.Count > 1
            && Fulfillment.MatchServiceLevel(merchant, method) is null)
        {
            // Worth saying out loud: several on offer, and we are booking the first because
            // the shopper's actual choice could not be read. Usually missing REST credentials.
            _logger.LogWarning(
                "WooOrderID {WooOrderId}: store {StoreId} offers {Count} service levels and the "
                + "chosen method {MethodId} matched none — booking {Code}. Without WooCommerce "
                + "REST credentials the shopper's choice cannot be read back.",
                payload.WooOrderId, merchant.StoreId, merchant.ServiceLevels.Count,
                method?.MethodId ?? "(not asked)", level.CheckoutCode);
        }

        var reference = BookingRules.CustomerReferenceFor(payload.WooOrderId);

        // Ask before booking. A previous attempt may have succeeded at the Engine and died
        // before it could say so here, and the cost of asking is one read against an indexed
        // column — far cheaper than the alternative, which is a second driver at the door.
        var existing = await _engine.FindBookingByCustomerReferenceAsync(
            merchant.EngineAccountRef, reference, cancellationToken);
        if (existing is not null)
        {
            _logger.LogInformation(
                "WooOrderID {WooOrderId} is already booked as {Reference} — idempotent skip",
                payload.WooOrderId, existing.Reference);
            return new BookingResult(
                BookingOutcome.AlreadyBooked,
                "already_booked",
                existing.Reference,
                existing.Shipments.FirstOrDefault()?.Waybill);
        }

        var destination = await _geocoder.ResolveAsync(Describe(payload.ShippingAddress), cancellationToken);
        if (destination is null)
        {
            // Permanent as far as retrying goes: the same address will not resolve in an hour.
            // Someone has to look at it, so say so rather than retrying for a day.
            _logger.LogWarning(
                "Cannot book WooOrderID {WooOrderId}: delivery address did not resolve",
                payload.WooOrderId);
            return new BookingResult(BookingOutcome.Rejected, "address_unresolvable");
        }

        var request = BuildRequest(merchant, level, payload, destination, reference);

        try
        {
            var result = await _engine.CreateBookingAsync(
                merchant.EngineAccountRef, request, cancellationToken);

            var waybill = result.Booking.Shipments.FirstOrDefault()?.Waybill;
            _logger.LogInformation(
                "Booked WooOrderID {WooOrderId} as {Reference} (waybill {Waybill}, replayed={Replayed})",
                payload.WooOrderId, result.Booking.Reference, waybill, result.Replayed);

            return new BookingResult(
                result.Replayed ? BookingOutcome.AlreadyBooked : BookingOutcome.Booked,
                result.Replayed ? "already_booked" : "booked",
                result.Booking.Reference,
                waybill);
        }
        catch (EngineCallException ex) when (!ex.IsTransient)
        {
            // A 4xx will be refused just as firmly on the tenth attempt. Retrying an
            // unfunded wallet for 24 hours only delays the moment someone is told.
            _logger.LogWarning(
                "Engine refused WooOrderID {WooOrderId}: {Code} ({Message})",
                payload.WooOrderId, ex.Code, ex.Message);
            return new BookingResult(BookingOutcome.Rejected, ex.Code);
        }
    }

    internal static string Describe(PluginShippingAddress address) => string.Join(", ", new[]
    {
        address.Street,
        address.Suburb,
        address.City,
        address.State,
        address.Postcode,
        address.Country
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static EngineBookingRequest BuildRequest(
        Merchant merchant,
        MerchantServiceLevel level,
        PluginOrderPayload payload,
        ResolvedPlace destination,
        string reference) => new()
    {
        ServiceLevelCode = level.EngineCode,
        IdempotencyKey = reference,
        CustomerReference = reference,

        // What the shopper was charged for delivery, as a ceiling. Zero means the merchant
        // did not charge separately for it, in which case there is nothing to hold us to.
        MaxTotalCents = payload.ShippingTotal > 0
            ? (long)Math.Round(payload.ShippingTotal * 100m, MidpointRounding.AwayFromZero)
            : null,

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
                    Line1 = payload.ShippingAddress.Street,
                    Suburb = payload.ShippingAddress.Suburb ?? destination.Suburb,
                    City = payload.ShippingAddress.City,
                    PostalCode = string.IsNullOrWhiteSpace(payload.ShippingAddress.Postcode)
                        ? destination.PostalCode
                        : payload.ShippingAddress.Postcode,
                    Country = "ZA",
                    Location = new EngineLatLng(destination.Latitude, destination.Longitude)
                },
                Recipient = new EngineContact
                {
                    Name = Fallback(payload.Customer.Name, "Customer"),
                    Phone = Fallback(payload.Customer.Phone, "0000000000"),
                    Email = string.IsNullOrWhiteSpace(payload.Customer.Email) ? null : payload.Customer.Email
                },
                Instructions = string.IsNullOrWhiteSpace(payload.CustomerNote) ? null : payload.CustomerNote,
                Parcels = new List<EngineParcel>
                {
                    new()
                    {
                        PackageTypeId = merchant.DefaultPackageTypeId,
                        Quantity = 1,
                        WeightKg = payload.TotalWeight > 0 ? payload.TotalWeight : null,
                        Description = $"Order {payload.OrderNumber}"
                    }
                }
            }
        }
    };

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
