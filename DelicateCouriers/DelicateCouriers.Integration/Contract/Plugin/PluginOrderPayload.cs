using System.Text.Json.Serialization;

namespace DelicateCouriers.Integration.Contract.Plugin;

/// <summary>
/// What the Delicate Courier WooCommerce plugin posts when an order changes.
///
/// A custom shape, not WooCommerce's own webhook format, and reproduced here exactly as the
/// plugins already send it. Fields absent from older plugin versions must stay absent-tolerant:
/// a null <c>fulfillment_type</c> means "an older plugin that only ever did deliveries", and
/// treating it as anything else would change how existing installations behave.
/// </summary>
public sealed class PluginOrderPayload
{
    [JsonPropertyName("event")] public string Event { get; set; } = string.Empty;
    [JsonPropertyName("woo_order_id")] public long WooOrderId { get; set; }
    [JsonPropertyName("order_number")] public string OrderNumber { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("total")] public decimal Total { get; set; }
    [JsonPropertyName("shipping_total")] public decimal ShippingTotal { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = string.Empty;
    [JsonPropertyName("customer_note")] public string? CustomerNote { get; set; }
    [JsonPropertyName("customer")] public PluginCustomer Customer { get; set; } = new();
    [JsonPropertyName("shipping_address")] public PluginShippingAddress ShippingAddress { get; set; } = new();
    [JsonPropertyName("total_weight")] public decimal TotalWeight { get; set; }
    [JsonPropertyName("store_id")] public string StoreId { get; set; } = string.Empty;
    [JsonPropertyName("delivery_date")] public string? DeliveryDate { get; set; }
    [JsonPropertyName("delivery_time")] public string? DeliveryTime { get; set; }

    /// <summary>'delivery' (default) | 'collect' | 'special_trip'. Null on older plugins.</summary>
    [JsonPropertyName("fulfillment_type")] public string? FulfillmentType { get; set; }
}

public sealed class PluginCustomer
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("phone")] public string Phone { get; set; } = string.Empty;
}

public sealed class PluginShippingAddress
{
    [JsonPropertyName("street")] public string Street { get; set; } = string.Empty;
    [JsonPropertyName("suburb")] public string? Suburb { get; set; }
    [JsonPropertyName("city")] public string City { get; set; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; set; } = string.Empty;
    [JsonPropertyName("postcode")] public string Postcode { get; set; } = string.Empty;
    [JsonPropertyName("country")] public string Country { get; set; } = string.Empty;
}

/// <summary>The body the plugin reads back. Shape fixed by the installed plugin versions.</summary>
public sealed class PluginWebhookResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("orderNumber")] public string? OrderNumber { get; set; }
}
