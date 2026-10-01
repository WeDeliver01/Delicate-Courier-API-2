using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shipments;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Platform-side safety net for "completed but never booked" orders
/// (order #50829 sat unbooked for hours with no alert). The sentinel must
/// flag exactly the orders that are Completed + shipment-less + past the
/// grace period, exactly once each, and ignore collect orders / orders that
/// were booked / orders still inside the grace window.
/// </summary>
public class UnbookedOrderSentinelTests
{
    private static readonly DateTime Now = new(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Flags_completed_order_without_shipment_after_grace_period()
    {
        await using var ctx = NewDbContext();
        var order = SeedOrder(ctx, status: "Completed", changedOn: Now.AddHours(-2));
        await ctx.SaveChangesAsync();

        var events = new FakeSystemEventLogger();
        var flagged = await NewSentinel(ctx, events).RunAsync(Now);

        Assert.Equal(1, flagged);
        var evt = Assert.Single(events.Events);
        Assert.Equal(UnbookedOrderSentinel.EventType, evt.EventType);
        Assert.Equal($"order:{order.OrderID}", evt.EntityRef);
        Assert.Equal(order.TenantID, evt.TenantId);
        Assert.Contains(order.WooOrderNumber, evt.Message);
    }

    [Fact]
    public async Task Does_not_flag_the_same_order_twice()
    {
        await using var ctx = NewDbContext();
        var order = SeedOrder(ctx, status: "Completed", changedOn: Now.AddHours(-2));
        // A previous sentinel run already emitted the event (persisted row).
        ctx.SystemEvents.Add(new SystemEvent
        {
            EventType = UnbookedOrderSentinel.EventType,
            EntityType = "Order",
            EntityRef = $"order:{order.OrderID}",
            Message = "already flagged",
        });
        await ctx.SaveChangesAsync();

        var events = new FakeSystemEventLogger();
        var flagged = await NewSentinel(ctx, events).RunAsync(Now);

        Assert.Equal(0, flagged);
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Ignores_orders_inside_grace_period()
    {
        await using var ctx = NewDbContext();
        SeedOrder(ctx, status: "Completed", changedOn: Now.AddMinutes(-5));
        await ctx.SaveChangesAsync();

        var events = new FakeSystemEventLogger();
        Assert.Equal(0, await NewSentinel(ctx, events).RunAsync(Now));
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Ignores_collect_orders_and_non_completed_statuses()
    {
        await using var ctx = NewDbContext();
        SeedOrder(ctx, status: "Completed", changedOn: Now.AddHours(-2), fulfillmentType: "collect");
        SeedOrder(ctx, status: "Processing", changedOn: Now.AddHours(-2), wooOrderId: "2", wooOrderNumber: "1002");
        SeedOrder(ctx, status: "Cancelled", changedOn: Now.AddHours(-2), wooOrderId: "3", wooOrderNumber: "1003");
        await ctx.SaveChangesAsync();

        var events = new FakeSystemEventLogger();
        Assert.Equal(0, await NewSentinel(ctx, events).RunAsync(Now));
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Ignores_completed_orders_that_have_a_shipment()
    {
        await using var ctx = NewDbContext();
        var order = SeedOrder(ctx, status: "Completed", changedOn: Now.AddHours(-2));
        await ctx.SaveChangesAsync();
        ctx.Shipments.Add(new Shipment
        {
            OrderID = order.OrderID,
            ConsignmentID = "CG-1",
            TrackingNumber = "TRK-1",
            CourierName = "Shiplogic",
            CourierService = "ECO",
        });
        await ctx.SaveChangesAsync();

        var events = new FakeSystemEventLogger();
        Assert.Equal(0, await NewSentinel(ctx, events).RunAsync(Now));
        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task Uses_CreatedOn_when_order_was_never_updated()
    {
        await using var ctx = NewDbContext();
        // Created already-completed, ChangedOn never set.
        SeedOrder(ctx, status: "Completed", changedOn: null, createdOn: Now.AddHours(-1));
        await ctx.SaveChangesAsync();

        var events = new FakeSystemEventLogger();
        Assert.Equal(1, await NewSentinel(ctx, events).RunAsync(Now));
    }

    [Fact]
    public async Task Flags_special_trip_orders_too()
    {
        // special_trip is still booked (as an ad-hoc trip) — going completed
        // without a shipment is just as wrong for it as for delivery.
        await using var ctx = NewDbContext();
        SeedOrder(ctx, status: "Completed", changedOn: Now.AddHours(-2), fulfillmentType: "special_trip");
        await ctx.SaveChangesAsync();

        var events = new FakeSystemEventLogger();
        Assert.Equal(1, await NewSentinel(ctx, events).RunAsync(Now));
    }

    // -----------------------------------------------------------------------

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"sentinel-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static UnbookedOrderSentinel NewSentinel(AppDbContext ctx, FakeSystemEventLogger events)
        => new(ctx, events, NullLogger<UnbookedOrderSentinel>.Instance);

    private static int _seed = 0;

    private static Order SeedOrder(
        AppDbContext ctx,
        string status,
        DateTime? changedOn,
        DateTime? createdOn = null,
        string? fulfillmentType = null,
        string wooOrderId = "1",
        string wooOrderNumber = "1001")
    {
        var n = ++_seed;
        if (!ctx.Tenants.Local.Any() && !ctx.Tenants.Any())
        {
            ctx.Tenants.Add(new Tenant
            {
                TenantID = 1,
                TenantName = "T",
                TenantAPIKey = "k",
                CreatedBy = "tests",
            });
            ctx.Stores.Add(new Store
            {
                StoreID = 7,
                TenantID = 1,
                StoreName = "S",
                WooCommerceURL = "https://s.example.com",
                WebhookSecret = "secret",
                IsActive = true,
                CreatedBy = "tests",
            });
        }
        var order = new Order
        {
            OrderID = 1000 + n,
            TenantID = 1,
            StoreID = 7,
            WooOrderID = wooOrderId,
            WooOrderNumber = wooOrderNumber,
            CustomerName = "Jane",
            OrderStatus = status,
            FulfillmentType = fulfillmentType,
            OrderDate = Now.AddHours(-3),
            CreatedOn = createdOn ?? Now.AddHours(-3),
            ChangedOn = changedOn,
            CreatedBy = "tests",
        };
        ctx.Orders.Add(order);
        return order;
    }
}
