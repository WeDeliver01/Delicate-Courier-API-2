using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Per-store booking-trigger override (Baked By Nataleen, store 7):
/// when Store.BookingTriggerStatuses is set (e.g. "completed"), only those
/// order statuses may enqueue an automatic shipment booking for that store.
/// Orders in other statuses are still persisted, and the booking happens
/// when a later webhook arrives with an allowed status. Stores without the
/// override keep the platform default (book on any non-terminal status).
/// </summary>
public class BookingTriggerStatusTests
{
    private const int TenantId = 1;
    private const int StoreId = 7;

    // ---------------------------------------------------------------
    // BookingStatusGate unit tests
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null, "pending")]
    [InlineData("", "on-hold")]
    [InlineData("   ", "processing")]
    [InlineData(",,", "pending")] // malformed config fails open to default
    public void No_override_allows_any_status(string? config, string status)
    {
        Assert.True(BookingStatusGate.IsAllowed(NewStore(config), status, out _));
    }

    [Theory]
    [InlineData("completed", "completed")]
    [InlineData("Completed", "completed")]
    [InlineData(" completed , processing ", "processing")]
    public void Override_allows_listed_statuses(string config, string status)
    {
        Assert.True(BookingStatusGate.IsAllowed(NewStore(config), status, out _));
    }

    [Theory]
    [InlineData("completed", "pending")]
    [InlineData("completed", "processing")]
    [InlineData("completed", "on-hold")]
    [InlineData("completed", null)]
    [InlineData("completed", "")]
    public void Override_blocks_unlisted_statuses(string config, string? status)
    {
        Assert.False(BookingStatusGate.IsAllowed(NewStore(config), status, out var allowed));
        Assert.Equal("completed", allowed);
    }

    // ---------------------------------------------------------------
    // PluginWebhookService end-to-end gating
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("pending")]
    [InlineData("on-hold")]
    [InlineData("processing")]
    public async Task Non_completed_statuses_save_order_but_never_book(string status)
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, bookingTriggerStatuses: "completed");
        var (service, jobs, _) = NewWebhookService(ctx);

        var response = await service.ProcessPluginOrderAsync(StoreId, NewPayload(50400, status));

        Assert.True(response.Success);
        Assert.Single(await ctx.Orders.IgnoreQueryFilters().ToListAsync()); // order persisted
        Assert.Empty(jobs.Enqueued); // but NOT booked
    }

    [Fact]
    public async Task Completed_status_books_immediately()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, bookingTriggerStatuses: "completed");
        var (service, jobs, _) = NewWebhookService(ctx);

        var response = await service.ProcessPluginOrderAsync(StoreId, NewPayload(50401, "completed"));

        Assert.True(response.Success);
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task Transition_to_completed_books_the_existing_order()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, bookingTriggerStatuses: "completed");
        var (service, jobs, _) = NewWebhookService(ctx);

        // Checkout push arrives as pending — saved, not booked.
        await service.ProcessPluginOrderAsync(StoreId, NewPayload(50402, "pending"));
        Assert.Empty(jobs.Enqueued);

        // Merchant later marks the order completed — plugin fires again,
        // existing-order path re-evaluates the gate and books.
        await service.ProcessPluginOrderAsync(StoreId, NewPayload(50402, "completed"));

        Assert.Single(jobs.Enqueued);
        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Completed", order.OrderStatus);
    }

    [Fact]
    public async Task Collect_orders_are_not_booked_even_when_completed()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, bookingTriggerStatuses: "completed");
        var (service, jobs, _) = NewWebhookService(ctx);

        var payload = NewPayload(50403, "completed");
        payload.FulfillmentType = "collect";
        await service.ProcessPluginOrderAsync(StoreId, payload);

        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task Store_without_override_keeps_default_booking_behaviour()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx, bookingTriggerStatuses: null);
        var (service, jobs, _) = NewWebhookService(ctx);

        await service.ProcessPluginOrderAsync(StoreId, NewPayload(50404, "processing"));

        Assert.Single(jobs.Enqueued); // other stores unaffected
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static (PluginWebhookService service, FakeBackgroundJobClient jobs, FakeSystemEventLogger events)
        NewWebhookService(AppDbContext ctx)
    {
        var jobs = new FakeBackgroundJobClient();
        var events = new FakeSystemEventLogger();
        // No Woo REST credentials on the store → verification is
        // Unverifiable → legacy behaviour (booking allowed) so these tests
        // isolate the status gate.
        var verifier = new WooFulfillmentVerifier(
            new FakeWooCommerceService(), NullLogger<WooFulfillmentVerifier>.Instance);
        var service = new PluginWebhookService(
            ctx, jobs, NullLogger<PluginWebhookService>.Instance, verifier, events,
            new DelicateCouriers.Tests.Shopify.FakeGeocoder(null));
        return (service, jobs, events);
    }

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"booking-trigger-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static Store NewStore(string? bookingTriggerStatuses) => new()
    {
        StoreID = StoreId,
        TenantID = TenantId,
        StoreName = "Baked By Nataleen (test)",
        Platform = "woocommerce",
        WooCommerceURL = "https://store.example.com",
        WebhookSecret = "test-webhook-secret-32-chars-min!!",
        IsActive = true,
        CreatedBy = "tests",
        BookingTriggerStatuses = bookingTriggerStatuses,
    };

    private static void SeedStore(AppDbContext ctx, string? bookingTriggerStatuses)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Test Tenant",
            TenantAPIKey = "test-api-key",
            CreatedBy = "tests",
        });
        ctx.Stores.Add(NewStore(bookingTriggerStatuses));
        ctx.SaveChanges();
    }

    private static PluginWebhookPayload NewPayload(long wooOrderId, string status) => new()
    {
        Event = "order.created",
        WooOrderId = wooOrderId,
        OrderNumber = wooOrderId.ToString(),
        Status = status,
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
