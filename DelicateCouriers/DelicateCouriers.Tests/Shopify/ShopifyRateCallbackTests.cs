using System.Text;
using System.Text.Json;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shopify.Carriers;
using DelicateCouriers.ApiService.Features.Shopify.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;
using DelicateCouriers.ApiService.Features.Delivery;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Shopify;

/// <summary>
/// Covers the Shopify Carrier Service rate callback (ShopifyRatesController).
///
/// The hard contract Shopify imposes: the endpoint must ALWAYS return HTTP 200
/// with a "rates" array (possibly empty). Returning a 4xx/5xx makes Shopify
/// mark the carrier service unhealthy and silently drop us from checkout. These
/// tests lock that in for malformed / empty / valid bodies, and confirm the
/// FlexibleLongConverter parses item price whether it arrives as a string or a
/// number (older vs newer Shopify API versions).
/// </summary>
public class ShopifyRateCallbackTests
{
    private const int StoreId = 7;
    private const int TenantId = 1;

    [Fact]
    public async Task Empty_body_returns_200_with_empty_rates()
    {
        await using var ctx = NewDbContext();
        SeedShopifyStore(ctx);
        var controller = NewController(ctx, out _);

        var result = await Post(controller, StoreId, rawBody: "");

        AssertEmptyRates(result);
    }

    [Fact]
    public async Task Malformed_json_returns_200_with_empty_rates()
    {
        await using var ctx = NewDbContext();
        SeedShopifyStore(ctx);
        var controller = NewController(ctx, out var delivery);

        var result = await Post(controller, StoreId, rawBody: "{ this is not valid json ");

        AssertEmptyRates(result);
        // Malformed body must short-circuit BEFORE any upstream call.
        Assert.False(delivery.WasCalled);
    }

    [Fact]
    public async Task Body_without_rate_object_returns_200_with_empty_rates()
    {
        await using var ctx = NewDbContext();
        SeedShopifyStore(ctx);
        var controller = NewController(ctx, out var delivery);

        var result = await Post(controller, StoreId, rawBody: "{\"not_a_rate\":true}");

        AssertEmptyRates(result);
        Assert.False(delivery.WasCalled);
    }

    [Fact]
    public async Task Unknown_store_returns_200_with_empty_rates()
    {
        await using var ctx = NewDbContext();
        SeedShopifyStore(ctx);
        var controller = NewController(ctx, out _);

        var result = await Post(controller, storeId: 999999, rawBody: ValidRateBody(itemPriceJson: "1000"));

        AssertEmptyRates(result);
    }

    [Theory]
    [InlineData("1000")]      // price as a JSON number (newer Shopify versions)
    [InlineData("\"1000\"")]  // price as a JSON string (older Shopify versions)
    public async Task Valid_body_returns_200_with_mapped_rates_for_string_or_number_price(string itemPriceJson)
    {
        await using var ctx = NewDbContext();
        SeedShopifyStore(ctx);
        var controller = NewController(ctx, out var delivery);

        // The engine returns a price; the words around it are the store's. 9550 cents is the
        // same R95.50 this test has always asserted on.
        delivery.Quote = new DeliveryQuote { TotalCents = 9550, ServiceLevelCode = "standard" };

        var result = await Post(controller, StoreId, ValidRateBody(itemPriceJson));

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ShopifyRateResponse>(ok.Value);
        var rate = Assert.Single(response.Rates);

        Assert.Equal("ECO", rate.ServiceCode);
        Assert.Equal("Delicate Courier — Economy", rate.ServiceName);
        // Shopify wants integer cents as a string: 95.50 → "9550".
        Assert.Equal("9550", rate.TotalPrice);
        Assert.Equal("ZAR", rate.Currency);

        Assert.True(delivery.WasCalled);
    }

    [Fact]
    public async Task Flexible_long_converter_parses_string_and_number_price()
    {
        // Direct converter coverage independent of the controller pipeline:
        // both representations must deserialize to the same long.
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var fromNumber = JsonSerializer.Deserialize<ShopifyItem>(
            "{\"price\":1500,\"quantity\":2}", opts);
        var fromString = JsonSerializer.Deserialize<ShopifyItem>(
            "{\"price\":\"1500\",\"quantity\":2}", opts);

        Assert.Equal(1500L, fromNumber!.Price);
        Assert.Equal(1500L, fromString!.Price);
    }

    [Fact]
    public async Task An_engine_that_cannot_price_returns_200_with_empty_rates()
    {
        // Shopify is waiting on this call. No rate is a poor answer; an error is no answer at
        // all, and the customer is shown nothing at checkout. The store under test has no
        // special-trip fallback configured, so the honest result is an empty list and a 200.
        await using var ctx = NewDbContext();
        SeedShopifyStore(ctx);
        var controller = NewController(ctx, out var delivery);
        delivery.QuoteThrows = new InvalidOperationException("engine unreachable");

        var result = await Post(controller, StoreId, ValidRateBody("1000"));

        AssertEmptyRates(result);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void AssertEmptyRates(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ShopifyRateResponse>(ok.Value);
        Assert.Empty(response.Rates);
    }

    private static string ValidRateBody(string itemPriceJson) => $$"""
        {
          "rate": {
            "origin": { "country": "ZA", "postal_code": "8001", "city": "Cape Town" },
            "destination": {
              "country": "ZA",
              "postal_code": "2196",
              "province": "Gauteng",
              "city": "Johannesburg",
              "address1": "1 Main Road",
              "name": "Jane Buyer"
            },
            "items": [
              { "name": "Widget", "quantity": 2, "grams": 500, "price": {{itemPriceJson}} }
            ],
            "currency": "ZAR"
          }
        }
        """;

    private static async Task<IActionResult> Post(ShopifyRatesController controller, int storeId, string rawBody)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(rawBody));
        http.Request.ContentLength = http.Request.Body.Length;
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return await controller.GetRates(storeId);
    }

    private static ShopifyRatesController NewController(AppDbContext ctx, out FakeDeliveryService delivery)
    {
        delivery = new FakeDeliveryService();
        var geocoder = new FakeGeocoder(new GeocodeResult(-26.1, 28.0, "fake"));
        return new ShopifyRatesController(
            ctx, delivery, geocoder,
            new DelicateCouriers.Features.Shipping.SpecialTrip.SpecialTripQuoter(
                new FakeDistanceServiceFactory(),
                NullLogger<DelicateCouriers.Features.Shipping.SpecialTrip.SpecialTripQuoter>.Instance),
            NullLogger<ShopifyRatesController>.Instance);
    }

    /// <summary>
    /// Distance factory whose service never finds a route — keeps the
    /// existing rate-callback tests on the plain engine-quoted path (stores
    /// under test have no Google key configured anyway).
    /// </summary>
    private sealed class FakeDistanceServiceFactory
        : DelicateCouriers.ApiService.Infrastructure.Distance.IDrivingDistanceServiceFactory
    {
        public DelicateCouriers.ApiService.Infrastructure.Distance.IDrivingDistanceService Create(string apiKey)
            => new NoRouteDistanceService();

        private sealed class NoRouteDistanceService
            : DelicateCouriers.ApiService.Infrastructure.Distance.IDrivingDistanceService
        {
            public Task<decimal?> GetDrivingDistanceKmAsync(
                double originLat, double originLng, double destLat, double destLng,
                CancellationToken ct = default) => Task.FromResult<decimal?>(null);
        }
    }

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"shopify-rates-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static void SeedShopifyStore(AppDbContext ctx)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Rate Tenant",
            TenantAPIKey = "key",
            CreatedBy = "tests",
        });
        ctx.Stores.Add(new Store
        {
            StoreID = StoreId,
            TenantID = TenantId,
            StoreName = "Shopify Store",
            Platform = "shopify",
            ShopifyStoreUrl = "https://test-shop.myshopify.com",
            IsActive = true,
            CollectionAddressLine1 = "10 Depot Street",
            CollectionCity = "Cape Town",
            CollectionProvince = "Western Cape",
            CollectionPostalCode = "8001",
            CollectionCountry = "ZA",
            CollectionLatitude = -33.92,
            CollectionLongitude = 18.42,
            EngineAccountRef = "SHP001",
            EnginePackageTypeId = "11111111-1111-4111-8111-111111111111",
            ServiceLevels = new List<StoreServiceLevel>
            {
                new()
                {
                    CheckoutCode = "ECO",
                    CheckoutLabel = "Economy",
                    EngineCode = "standard",
                    EstimatedDeliveryDays = 3,
                },
            },
            CreatedBy = "tests",
        });
        ctx.SaveChanges();
    }
}
