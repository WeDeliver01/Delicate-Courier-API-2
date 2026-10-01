using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Webhooks;
using DelicateCouriers.ApiService.Features.Webhooks.DTOs;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.Tests.Plugin;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Webhooks;

/// <summary>
/// Covers the WooCommerce pushback in the Shiplogic tracking webhook:
///   - a cancelled shipment must NEVER change the WooCommerce order status
///     (the merchant may simply rebook delivery) — it only adds an internal
///     order note, while the local Shipment still records Cancelled;
///   - delivered still maps to a WooCommerce "completed" status update.
/// </summary>
public class ShiplogicTrackingWebhookPushbackTests
{
    private const int TenantId = 1;
    private const int StoreId = 77;
    private const string ConsignmentId = "113449601";
    private const string TrackingNumber = "XJ347V";
    private const int WooOrderId = 5001;

    [Fact]
    public async Task Cancelled_shipment_does_not_change_woo_order_status_but_adds_note()
    {
        await using var ctx = NewDbContext();
        SeedWooOrderWithShipment(ctx, shipmentStatus: "in-transit");
        var woo = new RecordingWooCommerceService();
        var controller = NewController(ctx, woo);

        var result = await controller.ReceiveTrackingUpdate(Payload("cancelled"));

        Assert.IsType<OkObjectResult>(result);

        // No order-status write of any kind.
        Assert.Empty(woo.StatusUpdates);

        // Merchant gets a plain (internal) note instead.
        var note = Assert.Single(woo.Notes);
        Assert.Equal(StoreId, note.StoreId);
        Assert.Equal(WooOrderId, note.WooOrderId);
        Assert.False(note.CustomerNote);
        Assert.Contains(TrackingNumber, note.Note);
        Assert.Contains("cancelled", note.Note, StringComparison.OrdinalIgnoreCase);

        // Local shipment still records the cancellation.
        var shipment = await ctx.Shipments.SingleAsync();
        Assert.Equal("cancelled", shipment.ShipmentStatus);
    }

    [Fact]
    public async Task Delivered_shipment_still_maps_to_completed()
    {
        await using var ctx = NewDbContext();
        SeedWooOrderWithShipment(ctx, shipmentStatus: "in-transit");
        var woo = new RecordingWooCommerceService();
        var controller = NewController(ctx, woo);

        var result = await controller.ReceiveTrackingUpdate(Payload("delivered"));

        Assert.IsType<OkObjectResult>(result);
        var update = Assert.Single(woo.StatusUpdates);
        Assert.Equal(StoreId, update.StoreId);
        Assert.Equal(WooOrderId, update.WooOrderId);
        Assert.Equal("completed", update.Status);
        Assert.Empty(woo.Notes);
    }

    [Fact]
    public async Task Replayed_cancelled_status_adds_no_second_note()
    {
        await using var ctx = NewDbContext();
        SeedWooOrderWithShipment(ctx, shipmentStatus: "cancelled");
        var woo = new RecordingWooCommerceService();
        var controller = NewController(ctx, woo);

        var result = await controller.ReceiveTrackingUpdate(Payload("cancelled"));

        Assert.IsType<OkObjectResult>(result);
        Assert.Empty(woo.StatusUpdates);
        Assert.Empty(woo.Notes); // no transition, no note
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ShiplogicTrackingWebhookDTO Payload(string status) => new()
    {
        ShipmentId = long.Parse(ConsignmentId),
        ShortTrackingReference = TrackingNumber,
        Status = status,
    };

    private static ShiplogicTrackingWebhookController NewController(
        AppDbContext ctx, IWooCommerceService woo)
    {
        var controller = new ShiplogicTrackingWebhookController(
            ctx,
            NullLogger<ShiplogicTrackingWebhookController>.Instance,
            new ConfigurationBuilder().Build(),
            new FakeSystemEventLogger(),
            woo)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return controller;
    }

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"shiplogic-webhook-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static void SeedWooOrderWithShipment(AppDbContext ctx, string shipmentStatus)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Webhook Tenant",
            TenantAPIKey = "key",
            CreatedBy = "tests",
        });
        ctx.Stores.Add(new Store
        {
            StoreID = StoreId,
            TenantID = TenantId,
            StoreName = "Woo Store",
            Platform = "woocommerce",
            WooCommerceURL = "https://shop.example.com",
            WooConsumerKey = "ck_test",
            WooConsumerSecret = "cs_test",
            IsActive = true,
            CreatedBy = "tests",
        });

        var shipment = new Shipment
        {
            TrackingNumber = TrackingNumber,
            ConsignmentID = ConsignmentId,
            CourierName = "Delicate Courier",
            ShipmentStatus = shipmentStatus,
            TrackingPushedOn = DateTime.UtcNow, // suppress the tracking-note fallback
            CreatedBy = "tests",
        };
        ctx.Orders.Add(new Order
        {
            TenantID = TenantId,
            StoreID = StoreId,
            WooOrderID = WooOrderId.ToString(),
            WooOrderNumber = WooOrderId.ToString(),
            CustomerName = "Jane Buyer",
            OrderStatus = "Processing",
            CreatedBy = "tests",
            Shipment = shipment,
        });
        ctx.SaveChanges();
    }

    /// <summary>
    /// Records status updates, order notes, and meta pushes; succeeds on all.
    /// </summary>
    private sealed class RecordingWooCommerceService : IWooCommerceService
    {
        public List<(int StoreId, int WooOrderId, string Status)> StatusUpdates { get; } = new();
        public List<(int StoreId, int WooOrderId, string Note, bool CustomerNote)> Notes { get; } = new();

        public Task<bool> UpdateOrderStatusAsync(int storeId, int wooOrderId, string status)
        {
            StatusUpdates.Add((storeId, wooOrderId, status));
            return Task.FromResult(true);
        }

        public Task<bool> AddOrderNoteAsync(int storeId, int wooOrderId, string note, bool customerNote = false)
        {
            Notes.Add((storeId, wooOrderId, note, customerNote));
            return Task.FromResult(true);
        }

        public Task<bool> AddTrackingInfoAsync(int storeId, int wooOrderId, string trackingNumber, string courierName)
            => Task.FromResult(true);

        public Task<bool> PushShipmentMetaAsync(int storeId, int wooOrderId, IReadOnlyDictionary<string, string?> meta)
            => Task.FromResult(true);

        public Task<PushShipmentMetaResult> PushShipmentMetaDetailedAsync(
            int storeId, int wooOrderId, IReadOnlyDictionary<string, string?> meta,
            string? note = null, bool noteIsCustomerNote = false)
            => Task.FromResult(new PushShipmentMetaResult { Success = true });

        public Task<WooCommerceOrder?> GetOrderAsync(int storeId, int wooOrderId)
            => Task.FromResult<WooCommerceOrder?>(null);

        public Task<WooCommerceConnectionResponse> TestConnectionAsync(int storeId)
            => throw new NotSupportedException();

        public Task<List<WooCommerceOrder>> FetchOrdersAsync(FetchOrdersRequest request)
            => throw new NotSupportedException();

        public Task<List<WooCommerceOrder>> FetchAllOrdersAsync(int storeId, string? status = null, DateTime? after = null, DateTime? before = null, int maxOrders = 10000)
            => throw new NotSupportedException();
    }
}
