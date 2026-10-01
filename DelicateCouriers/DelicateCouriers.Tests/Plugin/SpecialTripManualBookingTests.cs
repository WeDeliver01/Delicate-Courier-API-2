using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Notifications;
using DelicateCouriers.ApiService.Features.Orders;
using DelicateCouriers.ApiService.Features.Packaging;
using DelicateCouriers.ApiService.Features.Shipments;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.Features.Shiplogic;
using DelicateCouriers.Tests.Shopify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Layer-4 guard: special-trip orders must NOT be booked at Shiplogic
/// (the account cannot create SPX shipments). Instead the orchestration
/// service notifies the admin for manual booking, writes a note + meta back
/// to the Woo order, and logs an idempotency-marker system event.
/// </summary>
public class SpecialTripManualBookingTests
{
    private const int OrderId = 900;

    private sealed class StubVerifier : IWooFulfillmentVerifier
    {
        public Task<FulfillmentVerificationResult> VerifyAsync(Store store, string wooOrderId)
            => throw new NotSupportedException("must not be called for special trips");

        public bool IsCollectionMethod(string? methodId, string? methodTitle) => false;
    }

    private sealed class RecordingEmailSender : IAdminEmailSender
    {
        public List<(string Subject, string Body)> Sent { get; } = new();
        public bool IsConfigured => true;
        public string RecipientAddress => "admin@delicatecourier.co.za";
        public Task<bool> SendAsync(string subject, string htmlBody, CancellationToken ct = default)
        {
            Sent.Add((subject, htmlBody));
            return Task.FromResult(true);
        }
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"special-trip-manual-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static async Task SeedSpecialTripOrderAsync(AppDbContext context)
    {
        var tenant = new Tenant { TenantID = 1, TenantName = "Delicate", ShiplogicBearerToken = "token" };
        var store = new Store
        {
            StoreID = 7,
            TenantID = 1,
            Tenant = tenant,
            StoreName = "Baked By Nataleen",
            WooCommerceURL = "https://example.com",
            WooConsumerKey = "ck",
            WooConsumerSecret = "cs",
        };
        var order = new Order
        {
            OrderID = OrderId,
            StoreID = 7,
            Store = store,
            TenantID = 1,
            WooOrderID = "50824",
            WooOrderNumber = "50824",
            CustomerName = "Test Customer",
            CustomerEmail = "cust@example.com",
            CustomerPhone = "27820000000",
            OrderTotal = 1000m,
            FulfillmentType = "special_trip",
            SpecialTripDistanceKm = 100.4m,
            SpecialTripQuotedAmount = 702.80m,
            ShippingAddressLine1 = "16 Camelia Ave",
            ShippingCity = "Lenasia",
            ShippingPostalCode = "1821",
        };
        context.Tenants.Add(tenant);
        context.Stores.Add(store);
        context.Orders.Add(order);
        await context.SaveChangesAsync();
    }

    private static (ShipmentOrchestrationService svc, FakeWooCommerceService woo, FakeSystemEventLogger events, RecordingEmailSender email, AppDbContext ctx)
        CreateService()
    {
        var ctx = CreateContext();
        var woo = new FakeWooCommerceService();
        var events = new FakeSystemEventLogger();
        var email = new RecordingEmailSender();
        var shiplogic = new FakeShiplogicService(); // CreateShipmentAsync throws — proves no booking attempt
        var mapper = new OrderToShipmentMapper(
            new PackageMappingService(ctx, NullLogger<PackageMappingService>.Instance),
            new FakeGeocoder(new DelicateCouriers.ApiService.Infrastructure.Geocoding.GeocodeResult(-26.1, 28.0, "fake")),
            NullLogger<OrderToShipmentMapper>.Instance);
        var svc = new ShipmentOrchestrationService(
            ctx,
            shiplogic,
            NullLogger<ShipmentOrchestrationService>.Instance,
            new LabelService(ctx, NullLogger<LabelService>.Instance),
            mapper,
            events,
            woo,
            new FakeBackgroundJobClient(),
            new StubVerifier(),
            email);
        return (svc, woo, events, email, ctx);
    }

    [Fact]
    public async Task Special_trip_order_is_not_booked_and_admin_is_notified()
    {
        var (svc, woo, events, email, ctx) = CreateService();
        await SeedSpecialTripOrderAsync(ctx);

        var result = await svc.CreateShipmentForOrderAsync(OrderId);

        Assert.False(result.IsSuccess);                      // no booking, no retry-worthy throw
        Assert.Empty(ctx.Shipments);                          // no shipment row created

        // Admin email carries the manual-booking essentials.
        var (subject, body) = Assert.Single(email.Sent);
        Assert.Contains("50824", subject);
        Assert.Contains("702.80", subject);
        Assert.Contains("WC-50824", body);
        Assert.Contains("100.4", body);
        Assert.Contains("Test Customer", body);
        Assert.Contains("16 Camelia Ave", body);

        // Woo write-back: special-trip meta + note.
        var push = Assert.Single(woo.MetaPushes);
        Assert.Equal(50824, push.WooOrderId);
        Assert.Equal("yes", push.Meta["_dcp_special_trip"]);
        Assert.Equal("manual", push.Meta["_dcp_special_trip_booking"]);
        Assert.Contains("SPECIAL TRIP", push.Note);

        // System event emitted (audit + idempotency marker).
        var evt = Assert.Single(events.Events, e => e.EventType == "shipment.special_trip_manual_booking");
        Assert.Contains("MANUAL BOOKING", evt.Message);
    }

    [Fact]
    public async Task Second_run_is_idempotent_no_duplicate_email_or_note()
    {
        var (svc, woo, events, email, ctx) = CreateService();
        await SeedSpecialTripOrderAsync(ctx);

        // Simulate the marker event already persisted by a previous run.
        ctx.SystemEvents.Add(new SystemEvent
        {
            EventType = "shipment.special_trip_manual_booking",
            TenantID = 1,
            EntityType = "Order",
            EntityRef = "#50824",
            Message = "already handled",
        });
        await ctx.SaveChangesAsync();

        var result = await svc.CreateShipmentForOrderAsync(OrderId);

        Assert.False(result.IsSuccess);
        Assert.Empty(email.Sent);
        Assert.Empty(woo.MetaPushes);
        Assert.DoesNotContain(events.Events, e => e.EventType == "shipment.special_trip_manual_booking");
    }
}
