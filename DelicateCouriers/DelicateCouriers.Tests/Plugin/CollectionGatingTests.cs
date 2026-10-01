using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Backend-only gating of collection-point orders (Baked By Nataleen
/// scenario): stores sell paid collection points as Woo `flat_rate` methods
/// whose only collection signal is the merchant-written title, so the plugin
/// tags them `delivery`. The platform must verify the real shipping method
/// via the store's Woo REST API and refuse to book collect orders — failing
/// safe (no booking + review flag) when the lookup fails.
/// </summary>
public class CollectionGatingTests
{
    private const int TenantId = 1;
    private const int StoreId = 7;

    // ---------------------------------------------------------------
    // Keyword matcher unit tests
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("flat_rate", "Collect from 1 Clifford road, Irene, 0157 Pretoria")]
    [InlineData("flat_rate", "COLLECTION POINT - Hazeldean")]
    [InlineData("local_pickup", "Local pickup")]
    [InlineData("flat_rate", "Pick up at store")]
    [InlineData("flat_rate", "Pick-up: Silver Lakes")]
    [InlineData("collect_plus", "Some Method")]
    public void IsCollectionMethod_matches_collect_and_pickup_variants(string id, string title)
    {
        var verifier = NewVerifier(new FakeWooCommerceService());
        Assert.True(verifier.IsCollectionMethod(id, title));
    }

    [Theory]
    [InlineData("flat_rate", "Overnight Courier")]
    [InlineData("free_shipping", "Free delivery")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void IsCollectionMethod_does_not_match_delivery_methods(string? id, string? title)
    {
        var verifier = NewVerifier(new FakeWooCommerceService());
        Assert.False(verifier.IsCollectionMethod(id, title));
    }

    // ---------------------------------------------------------------
    // Verifier outcome tests
    // ---------------------------------------------------------------

    [Fact]
    public async Task VerifyAsync_returns_Unverifiable_when_store_has_no_rest_credentials()
    {
        var woo = new FakeWooCommerceService();
        var verifier = NewVerifier(woo);
        var store = NewStore(withCreds: false);

        var result = await verifier.VerifyAsync(store, "49906");

        Assert.Equal(FulfillmentVerificationOutcome.Unverifiable, result.Outcome);
        Assert.Equal(0, woo.GetOrderCallCount); // no REST call attempted
    }

    [Fact]
    public async Task VerifyAsync_returns_Collection_for_flat_rate_with_collect_title()
    {
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = WooOrderWithShipping("flat_rate", "Collect from 1 Clifford road, Irene, 0157 Pretoria"),
        };
        var verifier = NewVerifier(woo);

        var result = await verifier.VerifyAsync(NewStore(withCreds: true), "49906");

        Assert.Equal(FulfillmentVerificationOutcome.Collection, result.Outcome);
        Assert.Equal("flat_rate", result.ShippingMethodId);
        Assert.Contains("Collect from 1 Clifford road", result.ShippingMethodTitle);
    }

    [Fact]
    public async Task VerifyAsync_returns_ConfirmedDelivery_for_courier_method()
    {
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = WooOrderWithShipping("flat_rate", "Courier - Gauteng"),
        };
        var verifier = NewVerifier(woo);

        var result = await verifier.VerifyAsync(NewStore(withCreds: true), "49909");

        Assert.Equal(FulfillmentVerificationOutcome.ConfirmedDelivery, result.Outcome);
    }

    [Fact]
    public async Task VerifyAsync_returns_LookupFailed_when_rest_lookup_returns_null()
    {
        var woo = new FakeWooCommerceService { OrderToReturn = null };
        var verifier = NewVerifier(woo);

        var result = await verifier.VerifyAsync(NewStore(withCreds: true), "49906");

        Assert.Equal(FulfillmentVerificationOutcome.LookupFailed, result.Outcome);
    }

    // ---------------------------------------------------------------
    // Webhook intake gating (PluginWebhookService)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Collection_order_is_reclassified_and_not_booked()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, withCreds: true);
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = WooOrderWithShipping("flat_rate", "Collect from 1 Clifford road, Irene, 0157 Pretoria"),
        };
        var (service, jobs, events) = NewWebhookService(ctx, woo);

        var response = await service.ProcessPluginOrderAsync(StoreId, NewPayload(49906));

        Assert.True(response.Success);
        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("collect", order.FulfillmentType);
        Assert.Equal("flat_rate", order.ShippingMethodId);
        Assert.Contains("Collect from", order.ShippingMethodTitle);
        Assert.Empty(jobs.Enqueued); // NO shipment booking
        Assert.Contains(events.Events, e => e.EventType == "order.reclassified_collect");
    }

    [Fact]
    public async Task Delivery_order_is_booked_and_method_persisted()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, withCreds: true);
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = WooOrderWithShipping("flat_rate", "Courier - Gauteng"),
        };
        var (service, jobs, _) = NewWebhookService(ctx, woo);

        var response = await service.ProcessPluginOrderAsync(StoreId, NewPayload(49909));

        Assert.True(response.Success);
        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("delivery", order.FulfillmentType);
        Assert.Equal("Courier - Gauteng", order.ShippingMethodTitle);
        Assert.Single(jobs.Enqueued); // booking proceeds
    }

    [Fact]
    public async Task Lookup_failure_fails_safe_no_booking_and_review_event()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, withCreds: true);
        var woo = new FakeWooCommerceService { OrderToReturn = null }; // REST failure
        var (service, jobs, events) = NewWebhookService(ctx, woo);

        var response = await service.ProcessPluginOrderAsync(StoreId, NewPayload(49910));

        Assert.True(response.Success); // order still persisted
        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("delivery", order.FulfillmentType); // NOT silently reclassified
        Assert.Empty(jobs.Enqueued); // fail safe: NO booking
        Assert.Contains(events.Events, e => e.EventType == "order.fulfillment_verification_failed");
    }

    [Fact]
    public async Task Store_without_rest_credentials_keeps_legacy_booking_behaviour()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, withCreds: false);
        var woo = new FakeWooCommerceService(); // would fail if ever called
        var (service, jobs, _) = NewWebhookService(ctx, woo);

        var response = await service.ProcessPluginOrderAsync(StoreId, NewPayload(49911));

        Assert.True(response.Success);
        Assert.Single(jobs.Enqueued); // legacy behaviour unaffected
        Assert.Equal(0, woo.GetOrderCallCount);
    }

    [Fact]
    public async Task Existing_order_update_is_also_gated()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, withCreds: true);
        var woo = new FakeWooCommerceService
        {
            OrderToReturn = WooOrderWithShipping("flat_rate", "Collect from 1 Clifford road, Irene"),
        };
        var (service, jobs, _) = NewWebhookService(ctx, woo);

        // First push creates + gates; second push hits the existing-order
        // path, which must also refuse to book.
        await service.ProcessPluginOrderAsync(StoreId, NewPayload(49912));
        await service.ProcessPluginOrderAsync(StoreId, NewPayload(49912));

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("collect", order.FulfillmentType);
        Assert.Empty(jobs.Enqueued);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static WooFulfillmentVerifier NewVerifier(IWooCommerceService woo)
        => new(woo, NullLogger<WooFulfillmentVerifier>.Instance);

    private static (PluginWebhookService service, FakeBackgroundJobClient jobs, FakeSystemEventLogger events)
        NewWebhookService(AppDbContext ctx, IWooCommerceService woo)
    {
        var jobs = new FakeBackgroundJobClient();
        var events = new FakeSystemEventLogger();
        var service = new PluginWebhookService(
            ctx, jobs, NullLogger<PluginWebhookService>.Instance,
            NewVerifier(woo), events,
            new DelicateCouriers.Tests.Shopify.FakeGeocoder(null));
        return (service, jobs, events);
    }

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"collect-gate-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static Store NewStore(bool withCreds) => new()
    {
        StoreID = StoreId,
        TenantID = TenantId,
        StoreName = "Baked By Nataleen (test)",
        Platform = "woocommerce",
        WooCommerceURL = "https://store.example.com",
        WooConsumerKey = withCreds ? "ck_test" : null,
        WooConsumerSecret = withCreds ? "cs_test" : null,
        WebhookSecret = "test-webhook-secret-32-chars-min!!",
        IsActive = true,
        CreatedBy = "tests",
    };

    private static void SeedStore(AppDbContext ctx, bool withCreds)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Test Tenant",
            TenantAPIKey = "test-api-key",
            CreatedBy = "tests",
        });
        ctx.Stores.Add(NewStore(withCreds));
        ctx.SaveChanges();
    }

    private static WooCommerceOrder WooOrderWithShipping(string methodId, string methodTitle) => new()
    {
        ShippingLines = new List<WooCommerceShippingLine>
        {
            new() { MethodId = methodId, MethodTitle = methodTitle },
        },
    };

    private static PluginWebhookPayload NewPayload(long wooOrderId) => new()
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
            Street = "1 Test Street",
            City = "Pretoria",
            State = "GP",
            Postcode = "0157",
            Country = "ZA",
        },
        LineItems = new List<PluginLineItem>
        {
            new() { Id = 1, Name = "Cake", Quantity = 1, Price = 500m, Sku = "CAKE-1" },
        },
    };
}
