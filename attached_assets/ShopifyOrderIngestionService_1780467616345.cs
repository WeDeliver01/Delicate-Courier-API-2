// Features/Shopify/ShopifyOrderIngestionService.cs
//
// ============================================================================
// SHOPIFY ORDER INGESTION
// ============================================================================
//
// PURPOSE:
//   Convert a Shopify order webhook payload into the existing
//   PluginWebhookPayload shape and dispatch it through PluginWebhookService.
//   By doing this we reuse the WooCommerce code path for:
//     - Order creation/idempotency
//     - Status mapping
//     - Fulfillment-type ("collect" vs "delivery") handling
//     - Hangfire job enqueuing for shipment booking
//     - Tracking write-back hooks
//
//   This means Shopify orders behave identically to WooCommerce orders
//   inside our platform. No parallel pipeline to maintain.
//
// DESIGN DECISIONS:
//
//   1. We map Shopify's `id` (numeric, e.g. 5891234567890) to WooOrderId.
//      Cross-platform uniqueness within a Store is guaranteed because
//      ProcessPluginOrderAsync looks up by (StoreID, WooOrderID), and a
//      single Store is either Shopify or WooCommerce, never both.
//
//   2. We map Shopify's `order_number` (display number like "#1042")
//      to WooOrderNumber. This is what shows in the merchant's reports.
//
//   3. Status mapping: Shopify has financial_status, fulfillment_status,
//      and cancelled_at. We collapse to the WooCommerce status names that
//      PluginWebhookService's MapPluginStatus already knows.
//
//   4. Fulfillment-type (collect vs delivery) is determined from whether
//      the merchant configured "Local pickup" as the chosen shipping line.
//      Shopify's shipping_lines[0].code (or title) tells us.
//
// WHAT THIS SERVICE DOES NOT DO:
//   - It does not verify HMAC. That's done in ShopifyWebhookController
//     before this service is invoked.
//   - It does not call Shiplogic directly. ProcessPluginOrderAsync queues
//     a Hangfire job that calls ShipmentOrchestrationService, same as Woo.
// ============================================================================

using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.ApiService.Features.Shopify.DTOs;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.Shopify;

public class ShopifyOrderIngestionService
{
    private readonly PluginWebhookService _pluginWebhookService;
    private readonly ILogger<ShopifyOrderIngestionService> _logger;

    public ShopifyOrderIngestionService(
        PluginWebhookService pluginWebhookService,
        ILogger<ShopifyOrderIngestionService> logger)
    {
        _pluginWebhookService = pluginWebhookService;
        _logger = logger;
    }

    /// <summary>
    /// Entry point called by ShopifyWebhookController after signature
    /// verification. Returns the same WebhookResponse shape as the
    /// WooCommerce path, so the controller can produce a consistent
    /// 200/4xx response.
    /// </summary>
    public async Task<WebhookResponse> IngestOrderAsync(
        int storeId,
        string topic,
        string rawBody)
    {
        // 1. Parse. We use JsonDocument rather than a fully-typed DTO
        //    because Shopify's order payload is enormous and we only
        //    consume a small subset. Anything we don't recognise should
        //    not crash ingestion.
        ShopifyOrderPayload? shopifyOrder;
        try
        {
            shopifyOrder = JsonSerializer.Deserialize<ShopifyOrderPayload>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Shopify webhook rejected: invalid JSON body");
            return new WebhookResponse { Success = false, Message = "Invalid JSON" };
        }

        if (shopifyOrder == null || shopifyOrder.Id == null)
        {
            _logger.LogWarning("Shopify webhook payload had no order id");
            return new WebhookResponse { Success = false, Message = "Missing order id" };
        }

        // 2. Map status. Cancelled wins over everything else.
        //    For non-cancelled orders: financial_status drives the choice.
        //    `paid` and `partially_paid` → "processing" (we book shipments).
        //    `pending` / `authorized` → "pending" (no auto-book).
        //    `refunded` / `voided` → "refunded".
        string status;
        if (!string.IsNullOrEmpty(shopifyOrder.CancelledAt))
        {
            status = "cancelled";
        }
        else
        {
            status = shopifyOrder.FinancialStatus?.ToLowerInvariant() switch
            {
                "paid" or "partially_paid" => "processing",
                "pending" or "authorized" => "pending",
                "refunded" or "voided" or "partially_refunded" => "refunded",
                _ => "pending"
            };
        }

        // 3. Map fulfillment_type. Shopify communicates "local pickup" via
        //    shipping_lines[].code being "local_pickup" or via a custom
        //    pickup option the merchant configured. Default to delivery.
        //
        //    NOTE: when our carrier service quoted the rate, our
        //    service_code (e.g. "STD") will appear here — that's a
        //    delivery, NOT a pickup. Be careful to match "pickup" / "local"
        //    rather than equality on a specific code.
        var firstShippingLine = shopifyOrder.ShippingLines?.FirstOrDefault();
        var shippingCode = firstShippingLine?.Code?.ToLowerInvariant() ?? string.Empty;
        var shippingTitle = firstShippingLine?.Title?.ToLowerInvariant() ?? string.Empty;
        var fulfillmentType =
            (shippingCode.Contains("pickup") || shippingCode.Contains("local") ||
             shippingTitle.Contains("pickup") || shippingTitle.Contains("collection"))
                ? "collect"
                : "delivery";

        // 4. Build the plugin webhook payload.
        var payload = new PluginWebhookPayload
        {
            Event = topic switch
            {
                "orders/create"    => "order.created",
                "orders/updated"   => "order.updated",
                "orders/cancelled" => "order.cancelled",
                _                  => "order.updated"
            },
            WooOrderId      = ConvertToInt(shopifyOrder.Id.Value),
            OrderNumber     = shopifyOrder.OrderNumber?.ToString() ?? shopifyOrder.Name ?? shopifyOrder.Id.Value.ToString(),
            Status          = status,
            Total           = decimal.TryParse(shopifyOrder.TotalPrice, out var tot) ? tot : 0m,
            ShippingTotal   = ParseShippingTotal(shopifyOrder),
            Currency        = shopifyOrder.Currency ?? "ZAR",
            PaymentMethod   = shopifyOrder.PaymentGatewayNames?.FirstOrDefault() ?? "Shopify",
            DateCreated     = shopifyOrder.CreatedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
            CustomerNote    = shopifyOrder.Note,
            Customer        = MapCustomer(shopifyOrder),
            ShippingAddress = MapShippingAddress(shopifyOrder.ShippingAddress),
            LineItems       = MapLineItems(shopifyOrder.LineItems),
            TotalWeight     = SumWeightKg(shopifyOrder.LineItems),
            StoreUrl        = string.Empty,                              // populated by ProcessPluginOrderAsync from Store
            StoreId         = storeId.ToString(),
            DeliveryDate    = null,    // Shopify doesn't have native delivery-date fields; could read from note_attributes if you ask merchants to use them
            DeliveryTime    = null,
            CollectionDate  = null,
            CollectionTime  = null,
            Occasion        = null,
            FulfillmentType = fulfillmentType
        };

        _logger.LogInformation(
            "Mapped Shopify order {ShopifyOrderId} → WooOrderId={WooOrderId} Status={Status} Fulfillment={Fulfillment}",
            shopifyOrder.Id.Value, payload.WooOrderId, payload.Status, payload.FulfillmentType);

        // 5. Dispatch through the existing pipeline.
        return await _pluginWebhookService.ProcessPluginOrderAsync(storeId, payload);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Shopify order IDs exceed Int32 range. PluginWebhookPayload.WooOrderId
    /// is `int`. We truncate by taking the lower 31 bits — collisions are
    /// astronomically unlikely within a single store, but to be defensive
    /// the (StoreID + WooOrderId) compound unique constraint protects us.
    ///
    /// LONG-TERM: consider widening WooOrderId to long. Until then, this
    /// is the pragmatic bridge.
    /// </summary>
    private static int ConvertToInt(long shopifyId)
    {
        return (int)(shopifyId & 0x7FFFFFFF);
    }

    private static decimal ParseShippingTotal(ShopifyOrderPayload o)
    {
        if (o.ShippingLines == null || o.ShippingLines.Count == 0) return 0m;
        decimal total = 0;
        foreach (var line in o.ShippingLines)
        {
            if (decimal.TryParse(line.Price, out var p)) total += p;
        }
        return total;
    }

    private static PluginCustomer MapCustomer(ShopifyOrderPayload o)
    {
        var first = o.Customer?.FirstName ?? o.ShippingAddress?.FirstName ?? "";
        var last  = o.Customer?.LastName  ?? o.ShippingAddress?.LastName  ?? "";
        return new PluginCustomer
        {
            Name  = $"{first} {last}".Trim(),
            Email = o.Customer?.Email ?? o.Email ?? string.Empty,
            Phone = o.Customer?.Phone ?? o.ShippingAddress?.Phone ?? string.Empty
        };
    }

    private static PluginShippingAddress MapShippingAddress(ShopifyOrderAddress? addr)
    {
        if (addr == null) return new PluginShippingAddress();
        return new PluginShippingAddress
        {
            Street   = $"{addr.Address1} {addr.Address2}".Trim(),
            City     = addr.City     ?? string.Empty,
            State    = addr.Province ?? string.Empty,
            Postcode = addr.Zip      ?? string.Empty,
            Country  = addr.CountryCode ?? "ZA"
        };
    }

    private static List<PluginLineItem> MapLineItems(List<ShopifyOrderLineItem>? items)
    {
        var result = new List<PluginLineItem>();
        if (items == null) return result;
        foreach (var li in items)
        {
            result.Add(new PluginLineItem
            {
                Id        = (int)((li.Id ?? 0L) & 0x7FFFFFFF),
                ProductId = (int)((li.ProductId ?? 0L) & 0x7FFFFFFF),
                Name      = li.Title ?? li.Name ?? "Item",
                Quantity  = li.Quantity ?? 1,
                Price     = decimal.TryParse(li.Price, out var p) ? p : 0m,
                Sku       = li.Sku ?? string.Empty
            });
        }
        return result;
    }

    private static decimal SumWeightKg(List<ShopifyOrderLineItem>? items)
    {
        if (items == null || items.Count == 0) return 1m;  // default same as Woo path
        long grams = 0;
        foreach (var li in items)
        {
            grams += (li.Grams ?? 0L) * Math.Max(1, li.Quantity ?? 1);
        }
        var kg = grams / 1000m;
        return kg > 0 ? kg : 1m;
    }
}
