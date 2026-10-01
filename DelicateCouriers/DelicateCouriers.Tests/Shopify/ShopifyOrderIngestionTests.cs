using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shopify;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Shopify;

/// <summary>
/// Covers ShopifyOrderIngestionService — the adapter that maps a Shopify order
/// webhook payload onto the existing PluginWebhookPayload shape and dispatches
/// it through the shared WooCommerce ingestion pipeline.
///
/// The mapping is private, so we assert via its observable side effect: the
/// persisted Order row (and whether a shipment job was enqueued). The subtle
/// contracts under test:
///   - status derives from financial_status, with cancelled_at overriding all;
///   - collect vs delivery derives from the shipping line (code/title);
///   - the FULL 64-bit Shopify order id survives into WooOrderID (no truncation
///     like the 31-bit line-item ids).
/// </summary>
public class ShopifyOrderIngestionTests
{
    private const int StoreId = 55;
    private const int TenantId = 1;

    // Bigger than int.MaxValue (2,147,483,647) — proves no 32-bit truncation.
    private const long BigShopifyOrderId = 5891234567890L;

    [Fact]
    public async Task Paid_order_maps_to_processing_and_queues_shipment()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "paid");
        var result = await svc.IngestOrderAsync(StoreId, "orders/create", json);

        Assert.True(result.Success);
        var order = await ctx.Orders.IgnoreQueryFilters().Include(o => o.LineItems).SingleAsync();
        Assert.Equal("Processing", order.OrderStatus);
        Assert.Equal("delivery", order.FulfillmentType);
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task Pending_payment_maps_to_pending_status_and_still_books_by_default()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "pending");
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Pending", order.OrderStatus);
        // The shared pipeline books by default for any non-terminal status
        // (see PluginWebhookService.ShouldAutoCreateShipment) — only
        // cancelled/refunded/failed are skipped — so a pending order still
        // enqueues a shipment job.
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task Cancelled_at_overrides_paid_status_and_does_not_queue_shipment()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        // financial_status is "paid" but the order is cancelled — cancel wins.
        var json = ShopifyOrderJson(financialStatus: "paid", cancelledAt: "2026-06-01T10:00:00Z");
        await svc.IngestOrderAsync(StoreId, "orders/cancelled", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Cancelled", order.OrderStatus);
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task Refunded_order_maps_to_cancelled()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "refunded");
        await svc.IngestOrderAsync(StoreId, "orders/updated", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        // "refunded" → "refunded" → MapPluginStatus → "Cancelled".
        Assert.Equal("Cancelled", order.OrderStatus);
        Assert.Empty(jobs.Enqueued);
    }

    [Theory]
    [InlineData("local_pickup", "Local pickup")]
    [InlineData("pickup-cpt", "Collect in store")]
    [InlineData("STD", "Pickup from store")]   // collect detected via the title
    public async Task Local_pickup_shipping_line_maps_to_collect_and_skips_shipment(string code, string title)
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "paid", shippingCode: code, shippingTitle: title);
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("collect", order.FulfillmentType);
        // Collect orders must never auto-book a courier shipment.
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task Null_shipping_address_maps_to_collect_and_skips_shipment()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        // Store 5 (Cakeaways) regression: the merchant named the pickup
        // shipping line "Kempton Park - Shop" (no pickup keyword), but a
        // genuine Shopify delivery ALWAYS carries a shipping_address —
        // pickup orders have shipping_address = null. Null address must
        // win over an innocuous-looking shipping line.
        var json = ShopifyOrderJson(
            financialStatus: "paid",
            shippingCode: "kempton park - shop",
            shippingTitle: "Kempton Park - Shop",
            includeShippingAddress: false);
        var result = await svc.IngestOrderAsync(StoreId, "orders/create", json);

        Assert.True(result.Success);
        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("collect", order.FulfillmentType);
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task Whole_token_pickup_tag_maps_to_collect_and_skips_shipment()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        // Pickup-scheduling apps tag the order rather than naming the
        // shipping line. The tag list is comma-separated; "pickup" must
        // match as a whole token.
        var json = ShopifyOrderJson(
            financialStatus: "paid",
            shippingCode: "kempton park - shop",
            shippingTitle: "Kempton Park - Shop",
            tags: "vip, pickup, gift");
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("collect", order.FulfillmentType);
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task Substring_pickup_tag_does_not_reclassify_a_delivery()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        // An operational tag like "pickup-issue-resolved" contains the word
        // "pickup" but is NOT a pickup marker — whole-token matching only.
        var json = ShopifyOrderJson(
            financialStatus: "paid",
            tags: "pickup-issue-resolved");
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("delivery", order.FulfillmentType);
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task Standard_delivery_shipping_line_maps_to_delivery()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "paid", shippingCode: "STD", shippingTitle: "Delicate Courier — Standard");
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("delivery", order.FulfillmentType);
        Assert.Single(jobs.Enqueued);
    }

    [Fact]
    public async Task Full_64bit_order_id_is_preserved_in_woo_order_id()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, _) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "paid");
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        // No 31-bit truncation: the full numeric Shopify id round-trips.
        Assert.Equal(BigShopifyOrderId.ToString(), order.WooOrderID);
        Assert.Equal("1042", order.WooOrderNumber);
    }

    [Fact]
    public async Task Missing_order_id_is_a_soft_failure()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        var result = await svc.IngestOrderAsync(StoreId, "orders/create", "{\"financial_status\":\"paid\"}");

        Assert.False(result.Success);
        Assert.Empty(ctx.Orders.IgnoreQueryFilters());
        Assert.Empty(jobs.Enqueued);
    }

    [Fact]
    public async Task Invalid_json_is_a_soft_failure()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, _) = NewService(ctx);

        var result = await svc.IngestOrderAsync(StoreId, "orders/create", "{ not json ");

        Assert.False(result.Success);
        Assert.Empty(ctx.Orders.IgnoreQueryFilters());
    }

    [Fact]
    public async Task DingDong_line_item_properties_flow_into_requested_delivery_window()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, _) = NewService(ctx);

        // DingDong writes the customer selection onto each line item's
        // properties. The date carries a weekday prefix and the time is a
        // 12-hour AM/PM range — both must survive into the Order row.
        var props = """
        [
          { "name": "Method", "value": "Local delivery" },
          { "name": "Delivery Date", "value": "Friday, 03 July 2026" },
          { "name": "Delivery Time", "value": "09:00 AM - 04:00 PM" }
        ]
        """;
        var json = ShopifyOrderJson(financialStatus: "paid", lineItemPropertiesJson: props);
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc), order.RequestedDeliveryDate);
        Assert.Equal("09:00 AM - 04:00 PM", order.RequestedDeliveryTime);
    }

    [Theory]
    [InlineData("delivery_date", "delivery_time")]
    [InlineData("Delivery-Date", "Delivery-Time")]
    [InlineData("DELIVERY DATE", "DELIVERY TIME")]
    public async Task Delivery_key_variants_are_matched_case_and_separator_insensitively(string dateKey, string timeKey)
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, _) = NewService(ctx);

        var props = $$"""
        [
          { "name": "{{dateKey}}", "value": "Friday, 03 July 2026" },
          { "name": "{{timeKey}}", "value": "09:00 AM - 04:00 PM" }
        ]
        """;
        var json = ShopifyOrderJson(financialStatus: "paid", lineItemPropertiesJson: props);
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc), order.RequestedDeliveryDate);
        Assert.Equal("09:00 AM - 04:00 PM", order.RequestedDeliveryTime);
    }

    [Fact]
    public async Task Order_level_note_attributes_are_used_as_a_fallback()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, _) = NewService(ctx);

        // Some date-picker apps write to order-level note_attributes instead
        // of line-item properties. Inject those (line items have no props).
        var noteAttrs = """
        [
          { "name": "Delivery Date", "value": "Saturday, 04 July 2026" },
          { "name": "Delivery Time", "value": "10:00 AM - 12:00 PM" }
        ]
        """;
        var json = ShopifyOrderJson(financialStatus: "paid")
            .Replace("\"shipping_lines\"", $"\"note_attributes\": {noteAttrs},\n          \"shipping_lines\"");
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(new DateTime(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc), order.RequestedDeliveryDate);
        Assert.Equal("10:00 AM - 12:00 PM", order.RequestedDeliveryTime);
    }

    [Fact]
    public async Task Order_without_delivery_properties_has_null_delivery_window()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, _) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "paid"); // no properties
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Null(order.RequestedDeliveryDate);
        Assert.Null(order.RequestedDeliveryTime);
    }

    [Fact]
    public async Task Special_trip_shipping_line_maps_fulfillment_and_quote_fields()
    {
        await using var ctx = NewDbContext();
        SeedStore(ctx);
        var (svc, jobs) = NewService(ctx);

        var json = ShopifyOrderJson(financialStatus: "paid",
                shippingCode: "SPECIAL_TRIP",
                shippingTitle: "Delicate Courier — Special Trip (23.4 km)")
            .Replace("\"zip\": \"2196\"", "\"zip\": \"2196\", \"latitude\": -26.05, \"longitude\": 28.2");
        await svc.IngestOrderAsync(StoreId, "orders/create", json);

        var order = await ctx.Orders.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("special_trip", order.FulfillmentType);
        Assert.Equal(75.00m, order.SpecialTripQuotedAmount);   // shipping line price
        Assert.Equal(23.4m, order.SpecialTripDistanceKm);
        Assert.Equal(-26.05, order.SpecialTripCustomerLat);
        Assert.Equal(28.2, order.SpecialTripCustomerLng);
        Assert.Single(jobs.Enqueued); // special trips DO book (as SPX)
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static string ShopifyOrderJson(
        string financialStatus,
        string? cancelledAt = null,
        string shippingCode = "STD",
        string shippingTitle = "Delicate Courier — Standard",
        string lineItemPropertiesJson = "[]",
        bool includeShippingAddress = true,
        string tags = "")
    {
        var cancelledJson = cancelledAt == null ? "null" : $"\"{cancelledAt}\"";
        var shippingAddressJson = includeShippingAddress
            ? """
              {
                  "first_name": "Jane", "last_name": "Buyer",
                  "address1": "1 Main Road", "address2": "Unit 5",
                  "city": "Johannesburg", "province": "Gauteng",
                  "zip": "2196", "country_code": "ZA", "phone": "+27820000000"
                }
              """
            : "null";
        return $$"""
        {
          "id": {{BigShopifyOrderId}},
          "name": "#1042",
          "order_number": 1042,
          "email": "buyer@example.com",
          "created_at": "2026-06-01T09:00:00Z",
          "cancelled_at": {{cancelledJson}},
          "currency": "ZAR",
          "total_price": "350.00",
          "financial_status": "{{financialStatus}}",
          "payment_gateway_names": ["shopify_payments"],
          "customer": { "first_name": "Jane", "last_name": "Buyer", "email": "buyer@example.com", "phone": "+27820000000" },
          "tags": "{{tags}}",
          "shipping_address": {{shippingAddressJson}},
          "line_items": [
            { "id": 9988776655443322, "product_id": 111222333444, "title": "Widget", "sku": "SKU-W", "quantity": 2, "price": "100.00", "grams": 500, "properties": {{lineItemPropertiesJson}} }
          ],
          "shipping_lines": [
            { "code": "{{shippingCode}}", "title": "{{shippingTitle}}", "price": "75.00" }
          ]
        }
        """;
    }

    private static (ShopifyOrderIngestionService svc, DelicateCouriers.Tests.Plugin.FakeBackgroundJobClient jobs) NewService(AppDbContext ctx)
    {
        var jobs = new DelicateCouriers.Tests.Plugin.FakeBackgroundJobClient();
        // Shopify stores are Unverifiable to the Woo fulfillment verifier
        // (platform != "woocommerce"), so behaviour under test is unchanged.
        var verifier = new WooFulfillmentVerifier(
            new DelicateCouriers.Tests.Plugin.FakeWooCommerceService(),
            NullLogger<WooFulfillmentVerifier>.Instance);
        var pluginService = new PluginWebhookService(
            ctx, jobs, NullLogger<PluginWebhookService>.Instance,
            verifier, new DelicateCouriers.Tests.Plugin.FakeSystemEventLogger(),
            new FakeGeocoder(null));
        var svc = new ShopifyOrderIngestionService(pluginService, NullLogger<ShopifyOrderIngestionService>.Instance);
        return (svc, jobs);
    }

    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"shopify-ingest-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static void SeedStore(AppDbContext ctx)
    {
        ctx.Tenants.Add(new Tenant
        {
            TenantID = TenantId,
            TenantName = "Ingest Tenant",
            TenantAPIKey = "key",
            CreatedBy = "tests",
        });
        ctx.Stores.Add(new Store
        {
            StoreID = StoreId,
            TenantID = TenantId,
            StoreName = "Shopify Store",
            Platform = "shopify",
            ShopifyStoreUrl = "https://test-shop.myshopify.com",
            IsActive = true,
            CreatedBy = "tests",
        });
        ctx.SaveChanges();
    }
}
