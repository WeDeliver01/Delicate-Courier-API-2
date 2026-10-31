using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Labels;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// The checkout contract, asserted against the shape three plugin lines already deserialize.
///
/// These tests exist because the plugins cannot be changed. They send a real payload and read
/// the raw JSON back rather than a typed object, because a renamed property still round-trips
/// through our own DTO and would pass a typed assertion while breaking every merchant.
/// </summary>
public class RatesContractTests : IClassFixture<RatesContractTests.Host>
{
    private readonly Host _host;

    public RatesContractTests(Host host)
    {
        _host = host;
        // One fixture serves the whole class, and xunit does not promise an order, so each
        // test starts from the same place rather than from whatever the last one left.
        _host.Geocoder.Resolves = true;
        _host.Geocoder.Calls = 0;
        _host.Engine.LastQuote = null;
        _host.Engine.Quote = _ => throw new InvalidOperationException("no stub configured");
    }

    [Fact]
    public async Task A_merchant_offering_two_levels_gets_two_rates_at_checkout()
    {
        // Priced per level, so each carries its own code, label and price. The shopper is
        // choosing between them, which is the whole point.
        _host.Engine.Quote = call => StubQuote(
            totalCents: call.Body.ServiceLevelCode == "express" ? 24_000 : 17_250);

        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload(storeId: 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var rates = body.RootElement.GetProperty("rates").EnumerateArray().ToList();
        Assert.Equal(2, rates.Count);

        Assert.Equal("STD", rates[0].GetProperty("serviceLevelCode").GetString());
        Assert.Equal(172.50m, rates[0].GetProperty("cost").GetDecimal());
        Assert.Equal("EXP", rates[1].GetProperty("serviceLevelCode").GetString());
        Assert.Equal(240.00m, rates[1].GetProperty("cost").GetDecimal());
    }

    [Fact]
    public async Task One_level_failing_still_offers_the_other()
    {
        // A shopper shown Standard when Express could not be priced is better served than one
        // shown nothing, and the merchant keeps the sale either way.
        _host.Engine.Quote = call => call.Body.ServiceLevelCode == "express"
            ? throw new EngineCallException(
                HttpStatusCode.InternalServerError, "engine_error", "express is unavailable", "req-1")
            : StubQuote(totalCents: 17_250);

        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload(storeId: 2));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        var rate = Assert.Single(body.RootElement.GetProperty("rates").EnumerateArray().ToList());
        Assert.Equal("STD", rate.GetProperty("serviceLevelCode").GetString());
    }

    [Fact]
    public async Task Every_level_failing_is_reported_as_a_failure_not_an_empty_shelf()
    {
        // All of them down is the Engine being unavailable, which the merchant needs to hear
        // about. "No rates available" would read as a working checkout that sells nothing.
        _host.Engine.Quote = _ => throw new EngineCallException(
            HttpStatusCode.InternalServerError, "engine_error", "everything is down", "req-2");

        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload(storeId: 2));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private static Merchant TwoLevelStore() => new()
    {
        StoreId = 2,
        Name = "Two Level Bakery",
        EngineAccountRef = "store-2",
        ServiceLevels = new List<MerchantServiceLevel>
        {
            new() { CheckoutCode = "STD", CheckoutLabel = "Standard", EngineCode = "standard", EstimatedDeliveryDays = 2 },
            new() { CheckoutCode = "EXP", CheckoutLabel = "Express", EngineCode = "express", EstimatedDeliveryDays = 1 },
        },
        DefaultPackageTypeId = Guid.Empty.ToString(),
        Collection = new MerchantAddress
        {
            Formatted = "Two Level Bakery, Menlyn",
            City = "Pretoria",
            Country = "ZA",
            Latitude = -25.7826,
            Longitude = 28.2755,
            ContactName = "Bakery",
            ContactPhone = "0821111111",
        },
    };

    /// <summary>A merchant saved before the collection pin was validated: still at 0,0.</summary>
    private static Merchant StrandedStore()
    {
        var stranded = TwoLevelStore();
        stranded.StoreId = 9;
        stranded.Collection.Latitude = 0;
        stranded.Collection.Longitude = 0;
        return stranded;
    }

    [Fact]
    public async Task A_store_with_no_collection_pin_is_not_priced_at_all()
    {
        // 0,0 is in the Gulf of Guinea and the Engine will price a loop from there without
        // complaint: the same Pretoria delivery came to R101 169 instead of R211. A checkout
        // offering no delivery is a bad afternoon; one offering it at six figures is worse,
        // because it looks like it is working.
        var quoted = false;
        _host.Engine.Quote = _ => { quoted = true; return StubQuote(totalCents: 17_250); };

        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload(storeId: 9));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.False(quoted, "the Engine should never have been asked to price from 0,0");
    }

    [Fact]
    public async Task Coordinates_from_the_storefront_are_used_instead_of_geocoding()
    {
        // The merchant's own Google Places autocomplete already resolved this address and
        // the merchant already paid for it. Asking again is a second charge for an answer
        // we were handed, on every rate request a checkout makes.
        _host.Engine.Quote = _ => StubQuote(totalCents: 17_250);

        var response = await _host.CreateClient().PostAsJsonAsync(
            "/api/public/shipping/rates", PluginPayload(latitude: -26.1076, longitude: 28.0567));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _host.Geocoder.Calls);

        // And priced from the pin that was sent, not from one we invented.
        var drop = Assert.Single(_host.Engine.LastQuote!.Drops);
        Assert.Equal(-26.1076, drop.Address.Location.Lat, 4);
        Assert.Equal(28.0567, drop.Address.Location.Lng, 4);
    }

    [Fact]
    public async Task An_address_with_no_coordinates_still_geocodes()
    {
        // Older plugins send none, and so does anyone who typed an address without
        // picking a suggestion. Both must still get a price.
        _host.Engine.Quote = _ => StubQuote(totalCents: 17_250);

        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _host.Geocoder.Calls);
    }

    [Theory]
    [InlineData(0, 0)]              // an uninitialised pair, which is the Gulf of Guinea
    [InlineData(-200, 28.0567)]     // not a latitude
    [InlineData(-26.1076, 400)]     // not a longitude
    public async Task Implausible_coordinates_fall_through_to_the_geocoder(double lat, double lng)
    {
        // Pricing a delivery to nowhere without complaint is worse than paying for a
        // lookup. Zero/zero in particular is what an uninitialised field looks like.
        _host.Engine.Quote = _ => StubQuote(totalCents: 17_250);

        var response = await _host.CreateClient().PostAsJsonAsync(
            "/api/public/shipping/rates", PluginPayload(latitude: lat, longitude: lng));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _host.Geocoder.Calls);
    }

    private static object PluginPayload(
        int storeId = 1, double? latitude = null, double? longitude = null) => new
    {
        storeId,
        deliveryAddress = new
        {
            company = (string?)null,
            streetAddress = "12 Oak Street",
            localArea = "Centurion",
            city = "Pretoria",
            zone = "Gauteng",
            country = "ZA",
            code = "0157",
            contactName = "Jane Smith",
            contactPhone = "0821234567",
            contactEmail = "jane@example.com",
            // Null unless a test is about them, which is also how an older plugin sends it.
            latitude,
            longitude
        },
        parcels = new[]
        {
            new { description = "Birthday cake", lengthCm = 30m, widthCm = 30m, heightCm = 20m, weightKg = 4m }
        }
    };

    [Fact]
    public async Task Returns_a_rate_in_the_shape_the_plugin_reads()
    {
        _host.Engine.Quote = _ => StubQuote(totalCents: 17_250);

        var client = _host.CreateClient();
        var response = await client.PostAsJsonAsync("/api/public/shipping/rates", PluginPayload());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean());
        var rate = Assert.Single(root.GetProperty("rates").EnumerateArray().ToList());

        // Property names, casing and types are the contract. R172.50, not 17250.
        Assert.Equal("STD", rate.GetProperty("serviceLevelCode").GetString());
        Assert.Equal("Delicate Courier Delivery", rate.GetProperty("serviceLevelName").GetString());
        Assert.Equal(172.50m, rate.GetProperty("cost").GetDecimal());
        Assert.Equal("ZAR", rate.GetProperty("currency").GetString());
        Assert.Equal(1, rate.GetProperty("estimatedDeliveryDays").GetInt32());
    }

    [Fact]
    public async Task Prices_against_the_merchant_account_and_the_resolved_destination()
    {
        EngineQuoteRequest? sent = null;
        string? accountRef = null;
        _host.Engine.Quote = request =>
        {
            sent = request.Body;
            accountRef = request.AccountRef;
            return StubQuote(10_000);
        };

        await _host.CreateClient().PostAsJsonAsync("/api/public/shipping/rates", PluginPayload());

        Assert.Equal("store-1", accountRef);
        Assert.NotNull(sent);
        Assert.Equal("standard", sent!.ServiceLevelCode);
        // Collection is the merchant's, not anything the shopper typed.
        Assert.Equal(-25.7826, sent.Collection.Address.Location.Lat, 4);
        var drop = Assert.Single(sent.Drops);
        Assert.Equal(StubGeocoder.Lat, drop.Address.Location.Lat, 4);
        Assert.Equal("Jane Smith", drop.Recipient.Name);
        Assert.Equal(4m, Assert.Single(drop.Parcels).WeightKg);
    }

    [Fact]
    public async Task Unknown_store_is_a_404_in_the_same_body_shape()
    {
        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload(storeId: 999));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Store not found or inactive",
            body.RootElement.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task An_engine_failure_returns_a_reference_and_never_the_detail()
    {
        _host.Engine.Quote = _ => throw new EngineCallException(
            HttpStatusCode.InternalServerError,
            "insufficient_funds",
            "wallet balance is R12.34 and the account holder is Jane Smith",
            "req-abc");

        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();

        // The endpoint is unauthenticated: nothing about wallets, people or the Engine.
        Assert.DoesNotContain("wallet", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jane Smith", raw);
        Assert.DoesNotContain("insufficient_funds", raw);
        Assert.Matches(@"Failed to retrieve shipping rates \(ref [0-9a-f]{8}\)",
            JsonDocument.Parse(raw).RootElement.GetProperty("errorMessage").GetString()!);
    }

    [Fact]
    public async Task An_unresolvable_address_is_no_rates_rather_than_an_error()
    {
        _host.Geocoder.Resolves = false;

        var response = await _host.CreateClient()
            .PostAsJsonAsync("/api/public/shipping/rates", PluginPayload());

        // 200 with success:false. A shopper mid-typing must not look like an outage.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("No shipping rates available",
            body.RootElement.GetProperty("errorMessage").GetString());
    }

    private static EngineQuote StubQuote(long totalCents) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ServiceLevelCode = "standard",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        Breakdown = new EngineQuoteBreakdown { TotalCents = totalCents, SubtotalCents = totalCents }
    };

    // ── test host ───────────────────────────────────────────────────────────

    public sealed record QuoteCall(string AccountRef, EngineQuoteRequest Body);

    public sealed class StubEngine : IEngineClient
    {

        public Task<EngineAccountStatus> CheckAccountAsync(
            string accountRef, CancellationToken cancellationToken) =>
            throw new NotSupportedException("this stub is not used for readiness checks");
        public Func<QuoteCall, EngineQuote> Quote { get; set; } =
            _ => throw new InvalidOperationException("no stub configured");

        /// <summary>The last request priced, so a test can assert what was actually sent.</summary>
        public EngineQuoteRequest? LastQuote { get; set; }

        public Task<EngineQuote> CreateQuoteAsync(
            string accountRef, EngineQuoteRequest request, CancellationToken cancellationToken)
        {
            LastQuote = request;
            return Task.FromResult(Quote(new QuoteCall(accountRef, request)));
        }

        // Quoting is the only thing the rates path may do. Booking from a checkout rate
        // request would be a serious bug, so these throw rather than returning something.
        public Task<EngineBookingResponse> CreateBookingAsync(
            string accountRef, EngineBookingRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the rates path must never book");

        public Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
            string accountRef, string customerReference, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the rates path must never look up bookings");

        public Task<WaybillDocument> GetWaybillAsync(
            string accountRef, string shipmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException("this path never prints a label");

        public Task<EngineBooking?> GetBookingAsync(
            string accountRef, string bookingId, CancellationToken cancellationToken)
            => throw new NotSupportedException("this path never reads a booking by id");
    }

    public sealed class StubGeocoder : IEngineGeocoder
    {
        public const double Lat = -25.8603;
        public const double Lng = 28.1894;

        public bool Resolves { get; set; } = true;

        /// <summary>How many times it was asked. The point of sending coordinates is that
        /// this stays at zero.</summary>
        public int Calls { get; set; }

        public Task<ResolvedPlace?> ResolveAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Resolves
                ? new ResolvedPlace("12 Oak Street, Centurion", Lat, Lng, "Centurion", "Pretoria", "0157")
                : null);
        }
    }

    public sealed class Host : WebApplicationFactory<Program>
    {
        public StubEngine Engine { get; } = new();
        public StubGeocoder Geocoder { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEngineClient>();
                services.RemoveAll<IEngineGeocoder>();
                services.AddSingleton<IEngineClient>(Engine);
                services.AddSingleton<IEngineGeocoder>(Geocoder);

                // One merchant, fixed, so the assertions describe the contract and not
                // whatever happens to be in a settings file.
                services.RemoveAll<IMerchantDirectory>();
                services.AddSingleton<IMerchantDirectory, ConfiguredMerchantDirectory>();
                services.AddSingleton<IOptions<MerchantDirectoryOptions>>(
                    Options.Create(new MerchantDirectoryOptions
                    {
                        Stores = new List<Merchant>
                        {
                            // Offers two levels, so the multi-rate behaviour has something
                            // real to assert against rather than a single-item list.
                            TwoLevelStore(),
                            StrandedStore(),
                            new()
                            {
                                StoreId = 1,
                                Name = "Honey Bee Baker",
                                EngineAccountRef = "store-1",
                                ServiceLevels = new List<MerchantServiceLevel>
                                {
                                    new()
                                    {
                                        CheckoutCode = "STD",
                                        CheckoutLabel = "Delicate Courier Delivery",
                                        EngineCode = "standard",
                                        EstimatedDeliveryDays = 1,
                                    },
                                },
                                DefaultPackageTypeId = Guid.Empty.ToString(),
                                Collection = new MerchantAddress
                                {
                                    Formatted = "Honey Bee Baker, Menlyn",
                                    City = "Pretoria",
                                    Country = "ZA",
                                    Latitude = -25.7826,
                                    Longitude = 28.2755,
                                    ContactName = "Bakery",
                                    ContactPhone = "0821111111"
                                }
                            }
                        }
                    }));
            });
        }

    }
}
