using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DelicateCouriers.Integration.Engine;
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
        _host.Engine.Quote = _ => throw new InvalidOperationException("no stub configured");
    }

    private static object PluginPayload(int storeId = 1) => new
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
            contactEmail = "jane@example.com"
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
        public Func<QuoteCall, EngineQuote> Quote { get; set; } =
            _ => throw new InvalidOperationException("no stub configured");

        public Task<EngineQuote> CreateQuoteAsync(
            string accountRef, EngineQuoteRequest request, CancellationToken cancellationToken)
            => Task.FromResult(Quote(new QuoteCall(accountRef, request)));

        // Quoting is the only thing the rates path may do. Booking from a checkout rate
        // request would be a serious bug, so these throw rather than returning something.
        public Task<EngineBookingResponse> CreateBookingAsync(
            string accountRef, EngineBookingRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the rates path must never book");

        public Task<EngineBooking?> FindBookingByCustomerReferenceAsync(
            string accountRef, string customerReference, CancellationToken cancellationToken)
            => throw new InvalidOperationException("the rates path must never look up bookings");
    }

    public sealed class StubGeocoder : IEngineGeocoder
    {
        public const double Lat = -25.8603;
        public const double Lng = 28.1894;

        public bool Resolves { get; set; } = true;

        public Task<ResolvedPlace?> ResolveAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult(Resolves
                ? new ResolvedPlace("12 Oak Street, Centurion", Lat, Lng, "Centurion", "Pretoria", "0157")
                : null);
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
                services.AddSingleton<IOptions<MerchantDirectoryOptions>>(
                    Options.Create(new MerchantDirectoryOptions
                    {
                        Stores = new List<Merchant>
                        {
                            new()
                            {
                                StoreId = 1,
                                Name = "Honey Bee Baker",
                                EngineAccountRef = "store-1",
                                CheckoutServiceLevelCode = "STD",
                                CheckoutRateLabel = "Delicate Courier Delivery",
                                EngineServiceLevelCode = "standard",
                                DefaultPackageTypeId = Guid.Empty.ToString(),
                                EstimatedDeliveryDays = 1,
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
