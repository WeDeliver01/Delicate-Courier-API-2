using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.WooCommerce.DTOs
{
    /// <summary>
    /// Payload sent by the Delicate Courier WooCommerce plugin
    /// This is a custom format, NOT the standard WooCommerce webhook format
    /// </summary>
    public class PluginWebhookPayload
    {
        [JsonPropertyName("event")]
        public string Event { get; set; } = string.Empty;

        [JsonPropertyName("woo_order_id")]
        public long WooOrderId { get; set; }

        [JsonPropertyName("order_number")]
        public string OrderNumber { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("total")]
        public decimal Total { get; set; }

        [JsonPropertyName("shipping_total")]
        public decimal ShippingTotal { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = string.Empty;

        [JsonPropertyName("payment_method")]
        public string PaymentMethod { get; set; } = string.Empty;

        [JsonPropertyName("date_created")]
        public string DateCreated { get; set; } = string.Empty;

        [JsonPropertyName("customer_note")]
        public string? CustomerNote { get; set; }

        [JsonPropertyName("customer")]
        public PluginCustomer Customer { get; set; } = new();

        [JsonPropertyName("shipping_address")]
        public PluginShippingAddress ShippingAddress { get; set; } = new();

        [JsonPropertyName("line_items")]
        public List<PluginLineItem> LineItems { get; set; } = new();

        [JsonPropertyName("total_weight")]
        public decimal TotalWeight { get; set; }

        [JsonPropertyName("store_url")]
        public string StoreUrl { get; set; } = string.Empty;

        [JsonPropertyName("store_id")]
        public string StoreId { get; set; } = string.Empty;

        [JsonPropertyName("delivery_date")]
        public string? DeliveryDate { get; set; }

        [JsonPropertyName("delivery_time")]
        public string? DeliveryTime { get; set; }

        [JsonPropertyName("collection_date")]
        public string? CollectionDate { get; set; }

        [JsonPropertyName("collection_time")]
        public string? CollectionTime { get; set; }

        [JsonPropertyName("occasion")]
        public string? Occasion { get; set; }

        // ─── Fulfillment ──────────────────────────────────────────────────
        // 'delivery' (default) | 'collect' | 'special_trip'.
        // When 'collect', the customer picks up at the merchant and the
        // platform must NOT create a Shiplogic shipment. When omitted
        // (older plugin versions, third-party integrations), defaults
        // to null and ShouldAutoCreateShipment falls through to its
        // legacy status-only behaviour so existing installations are
        // unaffected.
        [JsonPropertyName("fulfillment_type")]
        public string? FulfillmentType { get; set; }

        // ─── Special Trip (plugin v2.7.0+) ────────────────────────────────
        // Present only when fulfillment_type == "special_trip" and the
        // customer accepted the plugin's "Special Trip Request" fallback
        // rate. Older plugins never send it; System.Text.Json leaves the
        // property null, which is the "absent" behaviour we want.
        [JsonPropertyName("special_trip")]
        public PluginSpecialTrip? SpecialTrip { get; set; }
    }

    /// <summary>
    /// Quote details for the plugin's "Special Trip Request" fallback rate
    /// (driving distance × merchant-configured R/km). The quoted amount is
    /// used as the declared value when booking the Shiplogic SPX shipment.
    /// </summary>
    public class PluginSpecialTrip
    {
        [JsonPropertyName("quoted_amount")]
        public decimal QuotedAmount { get; set; }

        [JsonPropertyName("distance_km")]
        public decimal DistanceKm { get; set; }

        [JsonPropertyName("customer_lat")]
        public double? CustomerLat { get; set; }

        [JsonPropertyName("customer_lng")]
        public double? CustomerLng { get; set; }
    }

    public class PluginCustomer
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;
    }

    public class PluginShippingAddress
    {
        [JsonPropertyName("street")]
        public string Street { get; set; } = string.Empty;

        // Optional. Older plugin versions don't send this field — the
        // System.Text.Json default of leaving the property at its empty-string
        // initializer is exactly the "missing" behaviour we want, so older
        // installations continue to work unchanged.
        [JsonPropertyName("suburb")]
        public string? Suburb { get; set; }

        [JsonPropertyName("city")]
        public string City { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("postcode")]
        public string Postcode { get; set; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;
    }

    public class PluginLineItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("product_id")]
        public int ProductId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("price")]
        public decimal Price { get; set; }

        [JsonPropertyName("sku")]
        public string Sku { get; set; } = string.Empty;
    }
}