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
using System.Globalization;
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
        // 1. Parse. ShopifyOrderPayload carries explicit JsonPropertyName
        //    attributes for Shopify's snake_case keys; case-insensitive
        //    matching alone is not enough (underscores).
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
        // Pickup-scheduling apps tag the order rather than (or in addition
        // to) naming the shipping line — e.g. Marone's pickup option is
        // "Kempton Park - Shop" but the order is tagged "pickup". Match on
        // whole comma-separated tags only, so an operational tag like
        // "pickup-issue-resolved" can't reclassify a genuine delivery.
        var tagTokens = (shopifyOrder.Tags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();
        var fulfillmentType =
            (shippingCode.Contains("pickup") || shippingCode.Contains("local") ||
             shippingTitle.Contains("pickup") || shippingTitle.Contains("collection") ||
             tagTokens.Contains("pickup") || tagTokens.Contains("collection") ||
             tagTokens.Contains("local pickup") ||
             // The strongest signal: a genuine Shopify delivery order
             // ALWAYS carries a shipping_address. Pickup / in-store
             // collection orders have shipping_address = null, so never
             // classify those as deliveries (there is no address to
             // ship to anyway).
             shopifyOrder.ShippingAddress == null)
                ? "collect"
                : "delivery";

        // Special trip: our carrier callback quoted the fallback rate with
        // ServiceCode SPECIAL_TRIP, which Shopify echoes back as the
        // shipping line's code. These orders book as Shiplogic SPX ad-hoc
        // trips (the same path the WooCommerce plugin's special trips use).
        // Only a delivery can be a special trip — a null shipping address
        // stays "collect" no matter what the line code says.
        PluginSpecialTrip? specialTrip = null;
        if (fulfillmentType == "delivery" &&
            string.Equals(firstShippingLine?.Code, Carriers.ShopifyRatesController.SpecialTripServiceCode,
                StringComparison.OrdinalIgnoreCase))
        {
            fulfillmentType = "special_trip";
            specialTrip = new PluginSpecialTrip
            {
                QuotedAmount = decimal.TryParse(firstShippingLine?.Price, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var quoted) ? quoted : 0m,
                DistanceKm   = ParseDistanceKmFromTitle(firstShippingLine?.Title),
                CustomerLat  = shopifyOrder.ShippingAddress?.Latitude,
                CustomerLng  = shopifyOrder.ShippingAddress?.Longitude
            };
        }

        // 4. Extract the customer-selected delivery window (DingDong app).
        var (deliveryDate, deliveryTime) = ExtractDeliveryWindow(shopifyOrder);

        // 5. Build the plugin webhook payload.
        var payload = new PluginWebhookPayload
        {
            Event = topic switch
            {
                "orders/create"    => "order.created",
                "orders/updated"   => "order.updated",
                "orders/cancelled" => "order.cancelled",
                _                  => "order.updated"
            },
            WooOrderId      = shopifyOrder.Id.Value,
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
            DeliveryDate    = deliveryDate,    // From DingDong date-picker (line-item properties); null when not present
            DeliveryTime    = deliveryTime,
            CollectionDate  = null,
            CollectionTime  = null,
            Occasion        = null,
            FulfillmentType = fulfillmentType,
            SpecialTrip     = specialTrip
        };

        _logger.LogInformation(
            "Mapped Shopify order {ShopifyOrderId} → WooOrderId={WooOrderId} Status={Status} Fulfillment={Fulfillment} DeliveryDate={DeliveryDate} DeliveryTime={DeliveryTime}",
            shopifyOrder.Id.Value, payload.WooOrderId, payload.Status, payload.FulfillmentType,
            payload.DeliveryDate ?? "(none)", payload.DeliveryTime ?? "(none)");

        // 6. Dispatch through the existing pipeline.
        return await _pluginWebhookService.ProcessPluginOrderAsync(storeId, payload);
    }

    // ─── helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the quoted distance out of the special-trip rate title we
    /// generated, e.g. "Delicate Courier — Special Trip (12.3 km)" → 12.3.
    /// Returns 0 when the title doesn't carry a parsable distance — the
    /// booking still works; distance is informational.
    /// </summary>
    private static decimal ParseDistanceKmFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return 0m;
        var m = System.Text.RegularExpressions.Regex.Match(title, @"\(([\d.,]+)\s*km\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return 0m;
        return decimal.TryParse(m.Groups[1].Value.Replace(",", "."), System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var km) ? km : 0m;
    }

    /// <summary>
    /// Pulls the customer-selected delivery date/time out of a Shopify order.
    ///
    /// Marone's store uses the "DingDong Delivery" date-picker app, which
    /// writes the selection onto each line item's <c>properties</c> (the
    /// order-level <c>note_attributes</c> are empty). Delivery orders carry
    /// "Delivery Date" / "Delivery Time"; in-store pickup orders carry
    /// "Pickup Date" / "Pickup Time" — but collect orders skip Shiplogic
    /// booking downstream, so only the delivery keys are mapped here.
    ///
    /// We also scan order-level note_attributes as a fallback, since other
    /// date-picker apps / merchant configurations store the data there.
    ///
    /// Matching is defensive: case-insensitive, tolerant of hyphen / under-
    /// score / extra-space variations, and never throws. The date is
    /// normalised to ISO (yyyy-MM-dd) so the downstream DateTime.TryParse in
    /// PluginWebhookService is reliable; the time string is passed through
    /// unchanged because OrderToShipmentMapper.ParseTimeRange already
    /// understands ranges like "09:00 AM - 04:00 PM".
    /// </summary>
    private static (string? deliveryDate, string? deliveryTime) ExtractDeliveryWindow(ShopifyOrderPayload order)
    {
        var pairs = new List<ShopifyNameValue>();

        // Line-item properties first (where DingDong actually writes it).
        if (order.LineItems != null)
        {
            foreach (var li in order.LineItems)
            {
                if (li.Properties != null) pairs.AddRange(li.Properties);
            }
        }

        // Order-level note_attributes as a fallback for other configurations.
        if (order.NoteAttributes != null) pairs.AddRange(order.NoteAttributes);

        if (pairs.Count == 0) return (null, null);

        string? Find(params string[] candidateKeys)
        {
            foreach (var pair in pairs)
            {
                if (pair == null || string.IsNullOrWhiteSpace(pair.Name) || string.IsNullOrWhiteSpace(pair.Value))
                    continue;

                var normalised = pair.Name!.Replace('-', ' ').Replace('_', ' ').Trim().ToLowerInvariant();
                while (normalised.Contains("  ")) normalised = normalised.Replace("  ", " ");

                foreach (var candidate in candidateKeys)
                {
                    if (normalised == candidate) return pair.Value!.Trim();
                }
            }
            return null;
        }

        var rawDate = Find("delivery date", "deliverydate", "dingdong date", "scheduled date", "delivery on");
        var time    = Find("delivery time", "deliverytime", "dingdong time", "scheduled time", "delivery window");

        return (NormaliseDeliveryDate(rawDate), time);
    }

    /// <summary>
    /// Normalises a DingDong delivery date such as "Friday, 03 July 2026"
    /// (also "03 July 2026", "2026-07-03", etc.) to ISO "yyyy-MM-dd". If the
    /// value can't be parsed it is returned unchanged so downstream parsing
    /// decides what to do — extraction never throws.
    /// </summary>
    private static string? NormaliseDeliveryDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var candidate = raw.Trim();
        if (DateTime.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Strip a leading weekday (e.g. "Friday, ") and retry.
        var commaIndex = candidate.IndexOf(',');
        if (commaIndex >= 0 && commaIndex < candidate.Length - 1)
        {
            var afterComma = candidate.Substring(commaIndex + 1).Trim();
            if (DateTime.TryParse(afterComma, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed2))
                return parsed2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return raw;
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
