using System.Net;
using System.Text;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shopify;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Shopify;

/// <summary>
/// Covers ShopifyTrackingWriteback — the Hangfire job that pushes a Shiplogic
/// tracking number back to Shopify so the order is marked fulfilled and the
/// customer gets Shopify's tracking email.
///
/// The two contracts under test:
///   - Shopify's fulfillment_orders response is snake_case ("fulfillment_orders")
///     and must bind to the inline DTO; an open FO must be picked up.
///   - The customer-facing tracking URL is built from the shared storefront
///     meta format (the same link the WooCommerce writeback uses), so both
///     storefronts deep-link to the same page.
/// </summary>
public class ShopifyTrackingWritebackTests
{
    private const int StoreId = 88;
    private const int TenantId = 1;
    private const long ShopifyOrderId = 4567890123456L;
    private const long FulfillmentOrderId = 111222333L;
    private const string TrackingNumber = "XJ347V";

    [Fact]
    public async Task Posts_fulfillment_with_tracking_url_from_storefront_meta_format()
    {
        await using var ctx = NewDbContext();
        var orderId = SeedShopifyOrderWithShipment(ctx);

        var handler = new RecordingHandler();
        var writeback = new ShopifyTrackingWriteback(
            ctx,
            new SingleClientFactory(handler),
            NullLogger<ShopifyTrackingWriteback>.Instance);

        await writeback.WriteTrackingAsync(orderId);

        // The snake_case fulfillment_orders response bound and the open FO was
        // resolved, so a fulfillment POST was made.
        var post = Assert.Single(handler.FulfillmentPosts);

        // The FO id from the snake_case payload made it into the request body.
        Assert.Contains($"\"fulfillment_order_id\":{FulfillmentOrderId}", post);
        // Tracking number and the storefront-meta tracking URL are present.
        Assert.Contains($"\"number\":\"{TrackingNumber}\"", post);
        var expectedUrl = $"https://tracking.shiplogic.com/?tracking_reference={TrackingNumber}";
        Assert.Contains(expectedUrl, post);
    }

    [Fact]
    public async Task No_open_fulfillment_orders_results_in_no_post()
    {
        await using var ctx = NewDbContext();
        var orderId = SeedShopifyOrderWithShipment(ctx);

        var handler = new RecordingHandler { FulfillmentOrderStatus = "closed" };
        var writeback = new ShopifyTrackingWriteback(
            ctx,
            new SingleClientFactory(handler),
            NullLogger<ShopifyTrackingWriteback>.Instance);

        await writeback.WriteTrackingAsync(orderId);

        Assert.Empty(handler.FulfillmentPosts);
    }

    // -----------------------------------------------------------------------
    // Fakes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Routes the three Admin API calls the writeback makes by path and records
    /// the fulfillment POST body. Returns snake_case JSON so we exercise the
    /// real binding the production code relies on.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string FulfillmentOrderStatus { get; set; } = "open";
        public List<string> FulfillmentPosts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            // 1. Resolve the Shopify order id from the order number.
            if (path.EndsWith("/orders.json"))
            {
                var body = $"{{\"orders\":[{{\"id\":{ShopifyOrderId},\"name\":\"#1042\"}}]}}";
                return Json(body);
            }

            // 2. List fulfillment orders (snake_case top-level key).
            if (path.EndsWith($"/orders/{ShopifyOrderId}/fulfillment_orders.json"))
            {
                var body = $"{{\"fulfillment_orders\":[{{\"id\":{FulfillmentOrderId},\"status\":\"{FulfillmentOrderStatus}\"}}]}}";
                return Json(body);
            }

            // 3. Create a fulfillment — record the body.
            if (path.EndsWith("/fulfillments.json"))
            {
                var content = request.Content == null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                FulfillmentPosts.Add(content);
                return Json("{\"fulfillment\":{\"id\":999}}", HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"shopify-writeback-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static int SeedShopifyOrderWithShipment(AppDbContext ctx)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Writeback Tenant",
            TenantAPIKey = "key",
            CreatedBy = "tests",
        });
        var store = new Store
        {
            StoreID = StoreId,
            TenantID = TenantId,
            StoreName = "Shopify Store",
            Platform = "shopify",
            ShopifyStoreUrl = "https://test-shop.myshopify.com",
            ShopifyAccessToken = "shpat_test_token",
            IsActive = true,
            CreatedBy = "tests",
        };
        ctx.Stores.Add(store);

        var shipment = new Shipment
        {
            TrackingNumber = TrackingNumber,
            ConsignmentID = "113449601",
            CourierName = "Delicate Courier",
            ShipmentStatus = "submitted",
            ShippingCost = 95.5m,
            CreatedBy = "tests",
        };
        var order = new Order
        {
            TenantID = TenantId,
            StoreID = StoreId,
            WooOrderID = ShopifyOrderId.ToString(),
            WooOrderNumber = "1042",
            CustomerName = "Jane Buyer",
            OrderStatus = "Processing",
            CreatedBy = "tests",
            Shipment = shipment,
        };
        ctx.Orders.Add(order);
        ctx.SaveChanges();
        return order.OrderID;
    }
}
