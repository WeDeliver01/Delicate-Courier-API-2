// Features/Shopify/ShopifyTrackingWriteback.cs
//
// ============================================================================
// SHOPIFY TRACKING WRITEBACK
// ============================================================================
//
// PURPOSE:
//   When Shiplogic confirms a shipment booking and gives us a tracking
//   number, push that number back to Shopify so the merchant sees the
//   order as fulfilled and the customer gets Shopify's automatic
//   tracking-email notification.
//
// HOW IT'S TRIGGERED:
//   ShipmentOrchestrationService.CreateShipmentForOrderAsync (which Wires
//   into both WooCommerce and Shopify orders via the unified pipeline)
//   should call this writeback at the end, when the shipment is confirmed.
//   The hook point is the same as where you already trigger tracking
//   updates for the WooCommerce side.
//
//   Suggested wiring in ShipmentOrchestrationService:
//
//       if (order.Store.Platform == "shopify")
//       {
//           _backgroundJobClient.Enqueue<ShopifyTrackingWriteback>(
//               w => w.WriteTrackingAsync(order.OrderID));
//       }
//       else
//       {
//           // existing WooCommerce tracking writeback
//       }
//
// IDEMPOTENCY:
//   Shopify's fulfillment endpoint is idempotent on (fulfillment_order_id +
//   tracking_number). Calling twice with the same tracking number is safe.
//   Calling with a NEW tracking number for an already-fulfilled
//   fulfillment_order will fail with 422; the only correct action is to
//   create a new fulfillment, which we don't auto-handle. If you need
//   that flow later, we can extend.
//
// FAILURE BEHAVIOUR:
//   Throws on any non-2xx response so Hangfire's retry handles it.
//   Default Hangfire retry is 10 attempts with exponential backoff,
//   which is appropriate for "Shopify is briefly unavailable" or "rate
//   limit hit" type failures. Don't catch and swallow here.
// ============================================================================

using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shopify;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

namespace DelicateCouriers.ApiService.Features.Shopify;

public class ShopifyTrackingWriteback
{
    private const string ShopifyApiVersion = "2024-04";

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShopifyTrackingWriteback> _logger;

    public ShopifyTrackingWriteback(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<ShopifyTrackingWriteback> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Push tracking info for the given Order to Shopify.
    /// </summary>
    /// <remarks>
    /// Order.WooOrderID for Shopify orders holds our truncated 31-bit
    /// rendering of the Shopify numeric order id. We need the FULL
    /// numeric id to call Shopify's API, so we re-fetch it via the
    /// shop's order list using the order_number as a unique key.
    ///
    /// If/when we widen WooOrderID to long, this lookup gets simpler.
    /// </remarks>
    public async Task WriteTrackingAsync(int orderId)
    {
        var order = await _context.Orders
            .Include(o => o.Store)
            .Include(o => o.Shipment)
            .FirstOrDefaultAsync(o => o.OrderID == orderId);

        if (order == null)
        {
            _logger.LogWarning("ShopifyTrackingWriteback: Order {OrderId} not found", orderId);
            return;
        }

        if (order.Store?.Platform != "shopify")
        {
            _logger.LogWarning("ShopifyTrackingWriteback: Order {OrderId} is not on Shopify platform", orderId);
            return;
        }

        if (order.Shipment == null || string.IsNullOrEmpty(order.Shipment.TrackingNumber))
        {
            _logger.LogInformation("ShopifyTrackingWriteback: Order {OrderId} has no tracking yet, skipping", orderId);
            return;
        }

        if (string.IsNullOrEmpty(order.Store.ShopifyAccessToken) || string.IsNullOrEmpty(order.Store.ShopifyStoreUrl))
        {
            _logger.LogError(
                "ShopifyTrackingWriteback: Store {StoreId} missing Shopify access token or URL",
                order.StoreID);
            return;
        }

        using var http = CreateShopifyClient(order.Store.ShopifyStoreUrl!, order.Store.ShopifyAccessToken!);

        // ── 1. Look up the order's fulfillment orders ──────────────────
        // Shopify splits each order into one or more "fulfillment orders"
        // by location/inventory. We need to find the one(s) we can fulfill
        // and POST a fulfillment against them.
        var orderNumber = order.WooOrderNumber;
        var shopifyOrderId = await ResolveShopifyOrderIdAsync(http, orderNumber);
        if (shopifyOrderId == null)
        {
            _logger.LogWarning(
                "ShopifyTrackingWriteback: could not resolve Shopify order id for OrderNumber {Num}",
                orderNumber);
            return;
        }

        var fulfillmentOrders = await http.GetFromJsonAsync<FulfillmentOrdersWrapper>(
            $"/admin/api/{ShopifyApiVersion}/orders/{shopifyOrderId}/fulfillment_orders.json");

        var pendingFOs = fulfillmentOrders?.FulfillmentOrders?
            .Where(fo => fo.Status == "open" || fo.Status == "in_progress")
            .ToList();

        if (pendingFOs == null || pendingFOs.Count == 0)
        {
            _logger.LogInformation(
                "ShopifyTrackingWriteback: no open fulfillment orders for Shopify order {ShopifyOrderId}",
                shopifyOrderId);
            return;
        }

        // ── 2. Create a fulfillment per fulfillment order ──────────────
        foreach (var fo in pendingFOs)
        {
            var body = new
            {
                fulfillment = new
                {
                    line_items_by_fulfillment_order = new[]
                    {
                        new { fulfillment_order_id = fo.Id }
                    },
                    tracking_info = new
                    {
                        number  = order.Shipment.TrackingNumber,
                        url     = BuildTrackingUrl(order.Shipment.TrackingNumber),
                        company = "Delicate Courier"
                    },
                    notify_customer = true
                }
            };

            var response = await http.PostAsJsonAsync(
                $"/admin/api/{ShopifyApiVersion}/fulfillments.json", body);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "ShopifyTrackingWriteback: Shopify rejected fulfillment for FO {FoId}. {Status}: {Body}",
                    fo.Id, response.StatusCode, errorBody);
                response.EnsureSuccessStatusCode();  // throws → Hangfire retries
            }

            _logger.LogInformation(
                "ShopifyTrackingWriteback: posted fulfillment for Shopify order {ShopifyOrderId}, FO {FoId}, tracking {Tracking}",
                shopifyOrderId, fo.Id, order.Shipment.TrackingNumber);
        }
    }

    private HttpClient CreateShopifyClient(string storeUrl, string accessToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(NormaliseStoreBaseUrl(storeUrl));
        client.DefaultRequestHeaders.Add("X-Shopify-Access-Token", accessToken);
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private static string NormaliseStoreBaseUrl(string url)
    {
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return $"https://{uri.Host}";
        }
        return $"https://{trimmed.TrimEnd('/')}";
    }

    private async Task<long?> ResolveShopifyOrderIdAsync(HttpClient http, string orderNumber)
    {
        // Shopify's `name` field is "#1042"; we may have stored "1042".
        var stripped = orderNumber.TrimStart('#');
        var response = await http.GetFromJsonAsync<OrdersWrapper>(
            $"/admin/api/{ShopifyApiVersion}/orders.json?status=any&name=%23{stripped}&fields=id,name");

        var match = response?.Orders?.FirstOrDefault(o =>
            string.Equals(o.Name?.TrimStart('#'), stripped, StringComparison.Ordinal));
        return match?.Id;
    }

    private static string BuildTrackingUrl(string trackingNumber)
    {
        // TODO(@engineer): replace with your real tracking URL pattern.
        return $"https://app2.delicatecourier.co.za/track/{trackingNumber}";
    }

    // ── inline DTOs for the Admin API calls ─────────────────────────────
    private sealed class FulfillmentOrdersWrapper
    {
        public List<FulfillmentOrder>? FulfillmentOrders { get; set; }
    }

    private sealed class FulfillmentOrder
    {
        public long Id { get; set; }
        public string? Status { get; set; }
    }

    private sealed class OrdersWrapper
    {
        public List<ShopifyOrderSummary>? Orders { get; set; }
    }

    private sealed class ShopifyOrderSummary
    {
        public long? Id { get; set; }
        public string? Name { get; set; }
    }
}
