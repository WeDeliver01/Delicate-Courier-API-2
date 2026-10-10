using System.Net;
using DelicateCouriers.Integration.Bookings;
using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Labels;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Merchants;
using DelicateCouriers.Integration.Storefront;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// Booking an order, and — mostly — not booking it twice.
///
/// Duplicate deliveries are the expensive failure here: two drivers, two charges and a
/// customer who saw one order. So the interesting assertions are all about what happens on the
/// second attempt rather than the first.
/// </summary>
public class OrderBookingServiceTests
{
    private readonly RecordingEngine _engine = new();
    private readonly StubGeocoder _geocoder = new();
    private readonly StubStorefront _storefront = new();

    private OrderBookingService Service() =>
        new(_engine, _geocoder, _storefront, NullLogger<OrderBookingService>.Instance);

    private static Merchant Merchant() => new()
    {
        StoreId = 1,
        Name = "Honey Bee Baker",
        EngineAccountRef = "store-1",
        EngineServiceLevelCode = "standard",
        DefaultPackageTypeId = Guid.Empty.ToString(),
        Collection = new MerchantAddress
        {
            Formatted = "Honey Bee Baker, Menlyn",
            City = "Pretoria",
            Latitude = -25.7826,
            Longitude = 28.2755,
            ContactName = "Bakery",
            ContactPhone = "0821111111"
        }
    };

    private static PluginOrderPayload Order(
        string status = "processing",
        decimal shippingTotal = 172.50m,
        string? fulfillment = null) => new()
    {
        WooOrderId = 49905,
        OrderNumber = "49905",
        Status = status,
        ShippingTotal = shippingTotal,
        TotalWeight = 4m,
        FulfillmentType = fulfillment,
        CustomerNote = "Leave at reception",
        Customer = new PluginCustomer { Name = "Jane Smith", Phone = "0821234567", Email = "jane@example.com" },
        ShippingAddress = new PluginShippingAddress
        {
            Street = "12 Oak Street",
            Suburb = "Centurion",
            City = "Pretoria",
            State = "Gauteng",
            Postcode = "0157",
            Country = "ZA"
        }
    };

    [Fact]
    public async Task Books_an_order_against_the_merchants_engine_account()
    {
        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.Booked, result.Outcome);
        Assert.Equal("BK-260101-0001", result.BookingReference);
        Assert.Equal("DC-260101-00001", result.Waybill);

        var sent = Assert.Single(_engine.Bookings);
        Assert.Equal("store-1", sent.AccountRef);
        Assert.Equal("WC-49905", sent.Body.IdempotencyKey);
        Assert.Equal("WC-49905", sent.Body.CustomerReference);
    }

    [Fact]
    public async Task Carries_the_shoppers_delivery_charge_as_a_ceiling_not_as_the_price()
    {
        await Service().BookAsync(Merchant(), Order(shippingTotal: 172.50m), default);

        // R172.50 as integer cents. The Engine refuses rather than charging more than the
        // shopper agreed to at checkout.
        Assert.Equal(17_250, Assert.Single(_engine.Bookings).Body.MaxTotalCents);
    }

    [Fact]
    public async Task No_ceiling_when_the_merchant_did_not_charge_for_delivery()
    {
        await Service().BookAsync(Merchant(), Order(shippingTotal: 0m), default);
        Assert.Null(Assert.Single(_engine.Bookings).Body.MaxTotalCents);
    }

    [Fact]
    public async Task Asks_before_booking_and_returns_what_a_previous_attempt_made()
    {
        // The crash window: a prior attempt booked at the Engine and died before recording it.
        _engine.ExistingByReference["WC-49905"] = new EngineBooking
        {
            Reference = "BK-260101-0009",
            CustomerReference = "WC-49905",
            Shipments = new List<EngineShipment> { new() { Waybill = "DC-260101-00009" } }
        };

        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.AlreadyBooked, result.Outcome);
        Assert.Equal("BK-260101-0009", result.BookingReference);
        Assert.Equal("DC-260101-00009", result.Waybill);
        Assert.Empty(_engine.Bookings); // nothing was created
    }

    [Fact]
    public async Task Trusts_the_engines_own_replay_answer()
    {
        // The lookup missed but the Engine recognised the idempotency key — a sibling attempt
        // won the race between the two calls. One booking, reported honestly as a replay.
        _engine.NextReplayed = true;

        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.AlreadyBooked, result.Outcome);
        Assert.Single(_engine.Bookings);
    }

    [Fact]
    public async Task A_collect_order_never_reaches_the_engine()
    {
        var result = await Service().BookAsync(Merchant(), Order(fulfillment: "collect"), default);

        Assert.Equal(BookingOutcome.Skipped, result.Outcome);
        Assert.Empty(_engine.Bookings);
        Assert.Empty(_engine.Lookups); // not even asked about
    }

    [Fact]
    public async Task A_refunded_order_never_reaches_the_engine()
    {
        var result = await Service().BookAsync(Merchant(), Order(status: "refunded"), default);
        Assert.Equal(BookingOutcome.Skipped, result.Outcome);
        Assert.Empty(_engine.Bookings);
    }

    [Fact]
    public async Task An_address_that_will_not_resolve_is_rejected_rather_than_retried_forever()
    {
        _geocoder.Resolves = false;

        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.Rejected, result.Outcome);
        Assert.Equal("address_unresolvable", result.Reason);
        Assert.Empty(_engine.Bookings);
    }

    [Fact]
    public async Task A_refusal_from_the_engine_is_permanent_and_names_its_cause()
    {
        // An unfunded wallet will still be unfunded on the tenth attempt. Retrying for a day
        // only delays the moment somebody is told.
        _engine.Throw = new EngineCallException(
            HttpStatusCode.PaymentRequired, "insufficient_funds", "not enough in the wallet", "req-1");

        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.Rejected, result.Outcome);
        Assert.Equal("insufficient_funds", result.Reason);
    }

    [Fact]
    public async Task An_engine_outage_propagates_so_something_above_can_retry()
    {
        // The opposite case: a 5xx is the Engine having a bad moment and the order is still
        // perfectly bookable. Swallowing it here would lose the order silently.
        _engine.Throw = new EngineCallException(
            HttpStatusCode.ServiceUnavailable, "unavailable", "upstream down", null);

        await Assert.ThrowsAsync<EngineCallException>(
            () => Service().BookAsync(Merchant(), Order(), default));
    }

    [Fact]
    public async Task Sends_the_customers_note_and_weight_through_to_the_drop()
    {
        await Service().BookAsync(Merchant(), Order(), default);

        var drop = Assert.Single(Assert.Single(_engine.Bookings).Body.Drops);
        Assert.Equal("Leave at reception", drop.Instructions);
        Assert.Equal("Jane Smith", drop.Recipient.Name);
        Assert.Equal(4m, Assert.Single(drop.Parcels).WeightKg);
    }

    // ── the collection shipping-method guard ────────────────────────────────

    [Theory]
    [InlineData("local_pickup", "Local pickup")]
    [InlineData("flat_rate", "Collect from 1 Clifford road, Irene… R95.00")]
    [InlineData("flat_rate", "Pick up in store")]
    [InlineData("flat_rate", "Pick-up Saturday")]
    [InlineData("collection_point", "Saturday market")]
    public async Task Refuses_to_book_when_the_shopper_chose_collection(string id, string title)
    {
        // The payload says "delivery" because the plugin only looks at the method id. The
        // merchant's own label is the only signal that this is a collection.
        _storefront.Method = new ChosenShippingMethod(id, title);

        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.Skipped, result.Outcome);
        Assert.Equal("collection_shipping_method", result.Reason);
        Assert.Empty(_engine.Bookings);
    }

    [Fact]
    public async Task Books_normally_when_the_chosen_method_is_a_real_delivery()
    {
        _storefront.Method = new ChosenShippingMethod("flat_rate", "Standard baked goods delivery");

        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.Booked, result.Outcome);
    }

    [Fact]
    public async Task Books_when_the_merchant_cannot_be_asked_at_all()
    {
        // No REST credentials is "nothing to verify", which is different from a check that
        // failed. Treating it as a failure would stop every store without credentials.
        _storefront.Method = null;

        var result = await Service().BookAsync(Merchant(), Order(), default);

        Assert.Equal(BookingOutcome.Booked, result.Outcome);
    }

    [Fact]
    public async Task A_failed_storefront_lookup_withholds_the_booking_rather_than_guessing()
    {
        // Not knowing whether the customer is collecting is exactly the case this guard
        // exists for. It throws, so the job retries, rather than booking a delivery nobody
        // asked for.
        _storefront.Throw = new StorefrontLookupException("storefront unreachable");

        await Assert.ThrowsAsync<StorefrontLookupException>(
            () => Service().BookAsync(Merchant(), Order(), default));

        Assert.Empty(_engine.Bookings);
    }

    [Fact]
    public async Task Does_not_bother_the_storefront_about_an_order_the_rules_already_rejected()
    {
        // A refunded order is settled without a network call to anyone.
        await Service().BookAsync(Merchant(), Order(status: "refunded"), default);
        Assert.Equal(0, _storefront.Calls);
    }

    // ── stubs ───────────────────────────────────────────────────────────────

    private sealed record BookingCall(string AccountRef, EngineBookingRequest Body);

    private sealed class RecordingEngine : IEngineClient
    {
        public List<BookingCall> Bookings { get; } = new();
        public List<string> Lookups { get; } = new();
        public Dictionary<string, EngineBooking> ExistingByReference { get; } = new();
        public EngineCallException? Throw { get; set; }
        public bool NextReplayed { get; set; }

        public Task<EngineQuote> CreateQuoteAsync(
            string accountRef, EngineQuoteRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("booking does not quote separately");

        public Task<EngineBookingResponse> CreateBookingAsync(
            string accountRef, EngineBookingRequest request, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            Bookings.Add(new BookingCall(accountRef, request));
            return Task.FromResult(new EngineBookingResponse
            {
                Replayed = NextReplayed,
                Booking = new EngineBooking
                {
                    Reference = "BK-260101-0001",
                    CustomerReference = request.CustomerReference,
                    Status = "confirmed",
                    Shipments = new List<EngineShipment>
                    {
                        new() { Waybill = "DC-260101-00001", Status = "booked" }
                    }
                }
            });
        }

        public Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
            string accountRef, string customerReference, CancellationToken cancellationToken)
        {
            Lookups.Add(customerReference);
            return Task.FromResult(
                ExistingByReference.TryGetValue(customerReference, out var b) ? b : null);
        }

        public Task<WaybillDocument> GetWaybillAsync(
            string accountRef, string shipmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException("this path never prints a label");

        public Task<EngineBooking?> GetBookingAsync(
            string accountRef, string bookingId, CancellationToken cancellationToken)
            => throw new NotSupportedException("this path never reads a booking by id");
    }

    private sealed class StubStorefront : IStorefront
    {
        /// <summary>Null means "this merchant cannot be asked", which is not a failure.</summary>
        public ChosenShippingMethod? Method { get; set; }
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }

        public Task<ChosenShippingMethod?> GetShippingMethodAsync(
            Merchant merchant, long wooOrderId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Method);
        }

        public Task UpdateOrderAsync(
            Merchant merchant, long wooOrderId, StorefrontOrderUpdate update,
            CancellationToken cancellationToken)
            => throw new NotSupportedException("booking never writes back to a storefront");
    }

    private sealed class StubGeocoder : IEngineGeocoder
    {
        public bool Resolves { get; set; } = true;

        public Task<ResolvedPlace?> ResolveAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult(Resolves
                ? new ResolvedPlace("12 Oak Street, Centurion", -25.8603, 28.1894, "Centurion", "Pretoria", "0157")
                : null);
    }
}
