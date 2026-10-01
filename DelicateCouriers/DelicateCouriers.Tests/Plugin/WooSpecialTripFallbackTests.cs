using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Distance;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.Features.Shipping.GetRates;
using DelicateCouriers.Features.Shipping.GetRates.DTOs;
using DelicateCouriers.Features.Shipping.SpecialTrip;
using DelicateCouriers.Tests.Shopify;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Backend-served "Special Trip" fallback for WooCommerce checkouts.
///
/// The v2.9.x plugin CANNOT be updated, so the platform serves the
/// distance-priced fallback through the existing public rates endpoint in
/// the exact same payload shape as a Shiplogic rate (the plugin renders it
/// — and applies the merchant's checkout markup — unmodified). Order intake
/// then recognizes the chosen method by its "Special Trip Request (X km)"
/// label and reclassifies the order so it books as an SPX ad-hoc trip.
/// </summary>
public class WooSpecialTripFallbackTests
{
    private const int TenantId = 1;
    private const int StoreId = 7;

    // ---------------------------------------------------------------
    // Rates endpoint fallback
    // ---------------------------------------------------------------

    [Fact]
    public async Task Rates_endpoint_offers_special_trip_when_shiplogic_has_no_rates()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key");

        var controller = NewRatesController(ctx, distanceKm: 40m);
        var result = await controller.GetRates(NewRatesRequest(), CancellationToken.None);

        var response = AssertOkResponse(result);
        Assert.True(response.Success);
        var rate = Assert.Single(response.Rates);
        // MUST match the plugin's configured service level or its rate
        // filter drops the option silently.
        Assert.Equal("STD", rate.ServiceLevelCode);
        Assert.StartsWith(GetRatesController.SpecialTripLabelPrefix, rate.ServiceLevelName);
        Assert.Contains("(40 km)", rate.ServiceLevelName);
        Assert.Equal(280m, rate.Cost); // 40 km × R7 (above the R150 min fee)
    }

    [Fact]
    public async Task Rates_endpoint_applies_minimum_fee()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key");

        var controller = NewRatesController(ctx, distanceKm: 5m);
        var response = AssertOkResponse(await controller.GetRates(NewRatesRequest(), CancellationToken.None));

        var rate = Assert.Single(response.Rates);
        Assert.Equal(150m, rate.Cost); // 5 km × R7 = R35 → floored at R150
    }

    [Fact]
    public async Task Rates_endpoint_does_not_offer_special_trip_beyond_max_distance()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key");

        var controller = NewRatesController(ctx, distanceKm: 250m);
        var response = AssertOkResponse(await controller.GetRates(NewRatesRequest(), CancellationToken.None));

        Assert.False(response.Success);
        Assert.Empty(response.Rates);
    }

    [Fact]
    public async Task Rates_endpoint_stays_empty_when_fallback_not_configured()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: null, minFee: null, maxKm: null, googleKey: null);

        var controller = NewRatesController(ctx, distanceKm: 40m);
        var response = AssertOkResponse(await controller.GetRates(NewRatesRequest(), CancellationToken.None));

        Assert.False(response.Success);
        Assert.Empty(response.Rates);
    }

    [Fact]
    public async Task Rates_endpoint_does_not_offer_special_trip_without_woo_rest_credentials()
    {
        // Without Woo REST creds, intake could never recognize the chosen
        // method as a special trip and the order would book as a STANDARD
        // shipment — so the fallback must not be offered at all.
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key", withWooCreds: false);

        var controller = NewRatesController(ctx, distanceKm: 40m);
        var response = AssertOkResponse(await controller.GetRates(NewRatesRequest(), CancellationToken.None));

        Assert.False(response.Success);
        Assert.Empty(response.Rates);
    }

    [Fact]
    public async Task Rates_endpoint_prices_from_the_label_rounded_distance()
    {
        // Distance Matrix returns 40.04 km; the label shows one decimal
        // ("40 km") and intake re-derives the amount by parsing it — so the
        // quote must be priced from the SAME rounded value.
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key");

        var controller = NewRatesController(ctx, distanceKm: 40.04m);
        var response = AssertOkResponse(await controller.GetRates(NewRatesRequest(), CancellationToken.None));

        var rate = Assert.Single(response.Rates);
        Assert.Contains("(40 km)", rate.ServiceLevelName);
        Assert.Equal(280m, rate.Cost); // 40.0 km × R7, not 40.04 × R7
    }

    [Fact]
    public async Task Rates_endpoint_stays_empty_when_store_has_no_google_key()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: null);

        var controller = NewRatesController(ctx, distanceKm: 40m);
        var response = AssertOkResponse(await controller.GetRates(NewRatesRequest(), CancellationToken.None));

        Assert.False(response.Success);
        Assert.Empty(response.Rates);
    }

    [Fact]
    public async Task Rates_endpoint_uses_custom_checkout_rate_label_for_special_trip()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key",
            checkoutRateLabel: "Standard baked goods delivery");

        var controller = NewRatesController(ctx, distanceKm: 40m);
        var response = AssertOkResponse(await controller.GetRates(NewRatesRequest(), CancellationToken.None));

        var rate = Assert.Single(response.Rates);
        Assert.Equal("Standard baked goods delivery (40 km)", rate.ServiceLevelName);
        Assert.Equal(280m, rate.Cost);
    }

    // ---------------------------------------------------------------
    // Order intake reclassification
    // ---------------------------------------------------------------

    [Fact]
    public async Task Custom_labeled_special_trip_is_reclassified_but_normal_rate_with_same_label_is_not()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key",
            checkoutRateLabel: "Standard baked goods delivery");

        // Special trip variant: has the "(X km)" suffix → reclassified.
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = new WooCommerceOrder
            {
                ShippingLines = new List<WooCommerceShippingLine>
                {
                    new() { MethodId = "delicate_courier_platform_std", MethodTitle = "Standard baked goods delivery (40 km)" },
                },
            },
        };
        var (service, jobs) = NewWebhookService(ctx, woo);
        await service.ProcessPluginOrderAsync(StoreId, NewOrderPayload(50004));

        var special = await ctx.Orders.IgnoreQueryFilters().SingleAsync(o => o.WooOrderID == "50004");
        Assert.Equal("special_trip", special.FulfillmentType);
        Assert.Equal(280m, special.SpecialTripQuotedAmount);

        // Normal Shiplogic rate carries the SAME custom label but no km
        // suffix → must stay a normal delivery.
        woo.OrderToReturn = new WooCommerceOrder
        {
            ShippingLines = new List<WooCommerceShippingLine>
            {
                new() { MethodId = "delicate_courier_platform_std", MethodTitle = "Standard baked goods delivery" },
            },
        };
        await service.ProcessPluginOrderAsync(StoreId, NewOrderPayload(50005));

        var normal = await ctx.Orders.IgnoreQueryFilters().SingleAsync(o => o.WooOrderID == "50005");
        Assert.Equal("delivery", normal.FulfillmentType);
        Assert.Null(normal.SpecialTripQuotedAmount);
    }

    [Fact]
    public async Task Order_with_special_trip_method_is_reclassified_and_still_booked()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key", withWooCreds: true);
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = new WooCommerceOrder
            {
                ShippingLines = new List<WooCommerceShippingLine>
                {
                    new() { MethodId = "delicate_courier_platform_std", MethodTitle = "Special Trip Request (40 km)" },
                },
            },
        };
        var (service, jobs) = NewWebhookService(ctx, woo);

        var response = await service.ProcessPluginOrderAsync(StoreId, NewOrderPayload(50001));

        Assert.True(response.Success, response.Message);
        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("special_trip", order.FulfillmentType);
        Assert.Equal(40m, order.SpecialTripDistanceKm);
        Assert.Equal(280m, order.SpecialTripQuotedAmount); // pre-markup: 40 × R7
        Assert.Equal(-26.1, order.SpecialTripCustomerLat);
        Assert.Equal(28.0, order.SpecialTripCustomerLng);
        Assert.Single(jobs.Enqueued); // still books (as SPX via the mapper)
    }

    [Fact]
    public async Task Special_trip_label_without_parsable_distance_is_not_reclassified()
    {
        // A merchant-made method that merely contains the phrase must not
        // become an unpriced SPX booking.
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key");
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = new WooCommerceOrder
            {
                ShippingLines = new List<WooCommerceShippingLine>
                {
                    new() { MethodId = "flat_rate", MethodTitle = "Special Trip Request - ask us for pricing" },
                },
            },
        };
        var (service, jobs) = NewWebhookService(ctx, woo);

        await service.ProcessPluginOrderAsync(StoreId, NewOrderPayload(50003));

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("delivery", order.FulfillmentType);
        Assert.Null(order.SpecialTripQuotedAmount);
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task Normal_delivery_method_is_not_reclassified()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, costPerKm: 7m, minFee: 150m, maxKm: 200m, googleKey: "merchant-key", withWooCreds: true);
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = new WooCommerceOrder
            {
                ShippingLines = new List<WooCommerceShippingLine>
                {
                    new() { MethodId = "delicate_courier_platform_std", MethodTitle = "Standard Delivery" },
                },
            },
        };
        var (service, jobs) = NewWebhookService(ctx, woo);

        await service.ProcessPluginOrderAsync(StoreId, NewOrderPayload(50002));

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("delivery", order.FulfillmentType);
        Assert.Null(order.SpecialTripQuotedAmount);
        Assert.Single(jobs.Enqueued);
    }

    [Theory]
    [InlineData("Special Trip Request (40 km)", 40.0)]
    [InlineData("Special Trip Request (12.3 km)", 12.3)]
    [InlineData("Special Trip Request (12,3 km)", 12.3)]
    [InlineData("special trip request (7.5KM)", 7.5)]
    [InlineData("Standard Delivery", null)]
    [InlineData("Special Trip Request", null)]
    [InlineData(null, null)]
    public void ParseDistanceKmFromTitle_handles_label_variants(string? title, double? expected)
    {
        var km = PluginWebhookService.ParseDistanceKmFromTitle(title);
        if (expected is null) Assert.Null(km);
        else Assert.Equal((decimal)expected.Value, km);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static GetRatesResponseDto AssertOkResponse(ActionResult<GetRatesResponseDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<GetRatesResponseDto>(ok.Value);
    }

    private static GetRatesController NewRatesController(AppDbContext ctx, decimal? distanceKm)
    {
        var shiplogic = new FakeShiplogicService(); // canned response: no rates
        var quoter = new SpecialTripQuoter(
            new FixedDistanceServiceFactory(distanceKm),
            NullLogger<SpecialTripQuoter>.Instance);
        return new GetRatesController(
            ctx, shiplogic,
            new FakeGeocoder(new DelicateCouriers.ApiService.Infrastructure.Geocoding.GeocodeResult(-26.1, 28.0, "fake")),
            quoter,
            NullLogger<GetRatesController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            },
        };
    }

    private static (PluginWebhookService service, FakeBackgroundJobClient jobs)
        NewWebhookService(AppDbContext ctx, IWooCommerceService woo)
    {
        var jobs = new FakeBackgroundJobClient();
        var service = new PluginWebhookService(
            ctx, jobs, NullLogger<PluginWebhookService>.Instance,
            new WooFulfillmentVerifier(woo, NullLogger<WooFulfillmentVerifier>.Instance),
            new FakeSystemEventLogger(),
            new FakeGeocoder(new DelicateCouriers.ApiService.Infrastructure.Geocoding.GeocodeResult(-26.1, 28.0, "fake")));
        return (service, jobs);
    }

    private sealed class FixedDistanceServiceFactory : IDrivingDistanceServiceFactory
    {
        private readonly decimal? _km;
        public FixedDistanceServiceFactory(decimal? km) => _km = km;
        public IDrivingDistanceService Create(string apiKey) => new FixedDistanceService(_km);

        private sealed class FixedDistanceService : IDrivingDistanceService
        {
            private readonly decimal? _km;
            public FixedDistanceService(decimal? km) => _km = km;
            public Task<decimal?> GetDrivingDistanceKmAsync(
                double originLat, double originLng, double destLat, double destLng,
                CancellationToken ct = default) => Task.FromResult(_km);
        }
    }

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"woo-special-trip-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static void SeedStore(
        AppDbContext ctx,
        decimal? costPerKm, decimal? minFee, decimal? maxKm, string? googleKey,
        bool withWooCreds = true, string? checkoutRateLabel = null)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Test Tenant",
            TenantAPIKey = "test-api-key",
            ShiplogicBearerToken = "test-bearer-token",
            CreatedBy = "tests",
        });
        ctx.Stores.Add(new Store
        {
            StoreID = StoreId,
            TenantID = TenantId,
            StoreName = "Woo Special Trip Store",
            Platform = "woocommerce",
            WooCommerceURL = "https://store.example.com",
            WooConsumerKey = withWooCreds ? "ck_test" : null,
            WooConsumerSecret = withWooCreds ? "cs_test" : null,
            WebhookSecret = "test-webhook-secret-32-chars-min!!",
            IsActive = true,
            DefaultServiceLevel = "STD",
            CollectionAddressLine1 = "1 Bakery Lane",
            CollectionCity = "Pretoria",
            CollectionProvince = "Gauteng",
            CollectionPostalCode = "0157",
            CollectionCountry = "ZA",
            CollectionLatitude = -25.9,
            CollectionLongitude = 28.2,
            SpecialTripCostPerKm = costPerKm,
            SpecialTripMinFee = minFee,
            SpecialTripMaxKm = maxKm,
            GoogleMapsApiKey = googleKey,
            CheckoutRateLabel = checkoutRateLabel,
            CreatedBy = "tests",
        });
        ctx.SaveChanges();
    }

    private static GetRatesRequestDto NewRatesRequest() => new()
    {
        StoreId = StoreId,
        DeliveryAddress = new DeliveryAddressDto
        {
            StreetAddress = "99 Faraway Farm Road",
            LocalArea = "Bashewa",
            City = "Pretoria",
            Zone = "Gauteng",
            Country = "ZA",
            Code = "0181",
            ContactName = "Test Customer",
        },
        Parcels = new List<ParcelInfoDto>
        {
            new() { Description = "Cake", LengthCm = 30, WidthCm = 30, HeightCm = 20, WeightKg = 2 },
        },
    };

    private static PluginWebhookPayload NewOrderPayload(long wooOrderId) => new()
    {
        Event = "order.created",
        WooOrderId = wooOrderId,
        OrderNumber = wooOrderId.ToString(),
        Status = "processing",
        Total = 500m,
        Currency = "ZAR",
        FulfillmentType = "delivery",
        Customer = new PluginCustomer
        {
            Name = "Test Customer",
            Email = "customer@example.com",
            Phone = "0820000000",
        },
        ShippingAddress = new PluginShippingAddress
        {
            Street = "99 Faraway Farm Road",
            City = "Pretoria",
            State = "GP",
            Postcode = "0181",
            Country = "ZA",
        },
        LineItems = new List<PluginLineItem>
        {
            new() { Id = 1, Name = "Cake", Quantity = 1, Price = 500m, Sku = "CAKE-1" },
        },
    };
}
