using System.Net;
using System.Reflection;
using System.Text.Json;
using DelicateCouriers.Integration.Bookings;
using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Jobs;
using DelicateCouriers.Integration.Merchants;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// The retry contract.
///
/// Hangfire decides whether to try again purely by whether this method threw, so "throws" and
/// "returns" are not implementation details here — they are the difference between an order
/// that books an hour later and one that never books at all.
/// </summary>
public class BookOrderJobTests
{
    private readonly StubEngine _engine = new();

    private BookOrderJob Job(params Merchant[] merchants)
    {
        var directory = new ConfiguredMerchantDirectory(
            Options.Create(new MerchantDirectoryOptions { Stores = merchants.ToList() }));
        var bookings = new OrderBookingService(
            _engine, new AlwaysResolves(), NullLogger<OrderBookingService>.Instance);
        return new BookOrderJob(directory, bookings, NullLogger<BookOrderJob>.Instance);
    }

    private static Merchant Store(int storeId = 1) => new()
    {
        StoreId = storeId,
        Name = "Honey Bee Baker",
        EngineAccountRef = "store-1",
        DefaultPackageTypeId = Guid.Empty.ToString(),
        Collection = new MerchantAddress { Formatted = "Menlyn", Latitude = -25.78, Longitude = 28.27 }
    };

    private static string Payload(string status = "processing", string? fulfillment = null) =>
        JsonSerializer.Serialize(new
        {
            woo_order_id = 49905L,
            order_number = "49905",
            status,
            fulfillment_type = fulfillment,
            shipping_total = 172.50m,
            customer = new { name = "Jane", email = "j@example.com", phone = "0821234567" },
            shipping_address = new { street = "12 Oak Street", city = "Pretoria", postcode = "0157", country = "ZA" }
        });

    [Fact]
    public void Retries_ten_times_across_about_eight_hours()
    {
        // Pinned deliberately: an outage shorter than this window must not cost an order, and
        // shortening it silently is how the unbooked-order gap comes back.
        //
        // Eight hours, not the twenty-four the handover and the gap analysis both claim. The
        // schedule copied from the ShipLogic platform sums to 29_310s, and this service keeps
        // it unchanged rather than quietly lengthening a retry window during a migration. The
        // claim is worth correcting wherever it is written down, because the quote-expiry
        // reasoning elsewhere assumes a day.
        var attribute = typeof(BookOrderJob)
            .GetMethod(nameof(BookOrderJob.RunAsync))!
            .GetCustomAttribute<AutomaticRetryAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(10, attribute!.Attempts);
        Assert.Equal(AttemptsExceededAction.Fail, attribute.OnAttemptsExceeded);

        var delays = attribute.DelaysInSeconds;
        Assert.NotNull(delays);
        Assert.Equal(10, delays!.Length);
        Assert.Equal(29_310, delays.Sum());
        Assert.True(delays.Sum() > 8 * 60 * 60, "the retry window should outlast a long outage");
    }

    [Fact]
    public async Task Books_the_order_it_was_queued_with()
    {
        await Job(Store()).RunAsync(1, Payload(), default);

        var booked = Assert.Single(_engine.Booked);
        Assert.Equal("WC-49905", booked.IdempotencyKey);
    }

    [Fact]
    public async Task Throws_when_the_engine_is_briefly_down_so_hangfire_tries_again()
    {
        _engine.Throw = new EngineCallException(
            HttpStatusCode.BadGateway, "unavailable", "upstream down", null);

        await Assert.ThrowsAsync<EngineCallException>(() => Job(Store()).RunAsync(1, Payload(), default));
    }

    [Fact]
    public async Task Returns_quietly_when_the_engine_refuses_for_good()
    {
        // An unfunded wallet is not fixed by waiting. Ten more attempts would only delay
        // somebody being told, and fill the failed queue with noise.
        _engine.Throw = new EngineCallException(
            HttpStatusCode.PaymentRequired, "insufficient_funds", "no funds", null);

        await Job(Store()).RunAsync(1, Payload(), default);
        Assert.Empty(_engine.Booked);
    }

    [Fact]
    public async Task Returns_quietly_for_an_order_the_rules_will_never_book()
    {
        await Job(Store()).RunAsync(1, Payload(fulfillment: "collect"), default);
        Assert.Empty(_engine.Booked);
    }

    [Fact]
    public async Task Drops_a_job_for_a_store_that_no_longer_exists()
    {
        // Nothing to retry towards, so throwing would just fill the failed queue with
        // something nobody can act on.
        await Job().RunAsync(1, Payload(), default);
        Assert.Empty(_engine.Booked);
    }

    [Fact]
    public async Task Drops_a_job_whose_payload_no_longer_parses()
    {
        await Job(Store()).RunAsync(1, "{ not json", default);
        Assert.Empty(_engine.Booked);
    }

    private sealed class StubEngine : IEngineClient
    {
        public List<EngineBookingRequest> Booked { get; } = new();
        public EngineCallException? Throw { get; set; }

        public Task<EngineQuote> CreateQuoteAsync(
            string accountRef, EngineQuoteRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EngineBookingResponse> CreateBookingAsync(
            string accountRef, EngineBookingRequest request, CancellationToken cancellationToken)
        {
            if (Throw is not null) throw Throw;
            Booked.Add(request);
            return Task.FromResult(new EngineBookingResponse
            {
                Booking = new EngineBooking
                {
                    Reference = "BK-260101-0001",
                    Shipments = new List<EngineShipment> { new() { Waybill = "DC-260101-00001" } }
                }
            });
        }

        public Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
            string accountRef, string customerReference, CancellationToken cancellationToken)
            => Task.FromResult<EngineBooking?>(null);
    }

    private sealed class AlwaysResolves : IEngineGeocoder
    {
        public Task<ResolvedPlace?> ResolveAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult<ResolvedPlace?>(
                new ResolvedPlace("12 Oak Street", -25.86, 28.18, "Centurion", "Pretoria", "0157"));
    }
}
