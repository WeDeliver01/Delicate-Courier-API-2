// Features/Shopify/DTOs/ShopifyOrderPayload.cs
//
// Minimal Shopify order DTOs — covers JUST the fields we read during
// ingestion. Shopify's full order payload has ~100+ fields; we ignore
// everything that doesn't affect shipping. Adding more fields here is
// safe (JSON deserialization ignores unknowns).
//
// Shopify sends snake_case keys (e.g. "order_number", "shipping_address"),
// so every property carries an explicit JsonPropertyName. Relying on the
// case-insensitive matcher alone does NOT work because of the underscores.
//
// Reference: https://shopify.dev/docs/api/admin-rest/2024-04/resources/order

using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.Shopify.DTOs;

public class ShopifyOrderPayload
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    /// <summary>
    /// The merchant-visible name like "#1042". Distinct from the numeric Id.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("order_number")]
    public long? OrderNumber { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("cancelled_at")]
    public string? CancelledAt { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("total_price")]
    public string? TotalPrice { get; set; }

    [JsonPropertyName("financial_status")]
    public string? FinancialStatus { get; set; }

    [JsonPropertyName("fulfillment_status")]
    public string? FulfillmentStatus { get; set; }

    [JsonPropertyName("payment_gateway_names")]
    public List<string>? PaymentGatewayNames { get; set; }

    [JsonPropertyName("customer")]
    public ShopifyOrderCustomer? Customer { get; set; }

    /// <summary>
    /// Comma-separated order tags. Pickup-scheduling apps commonly tag
    /// orders with "pickup", which we use as a fulfillment-type signal.
    /// </summary>
    [JsonPropertyName("tags")]
    public string? Tags { get; set; }

    [JsonPropertyName("shipping_address")]
    public ShopifyOrderAddress? ShippingAddress { get; set; }

    [JsonPropertyName("billing_address")]
    public ShopifyOrderAddress? BillingAddress { get; set; }

    [JsonPropertyName("line_items")]
    public List<ShopifyOrderLineItem>? LineItems { get; set; }

    [JsonPropertyName("shipping_lines")]
    public List<ShopifyOrderShippingLine>? ShippingLines { get; set; }

    /// <summary>
    /// Order-level structured key/value pairs. Many date-picker apps write
    /// the customer's delivery selection here. (Marone's "DingDong Delivery"
    /// app actually writes to line_items[].properties instead — this is kept
    /// as a fallback for other merchant configurations.)
    /// </summary>
    [JsonPropertyName("note_attributes")]
    public List<ShopifyNameValue>? NoteAttributes { get; set; }
}

public class ShopifyOrderCustomer
{
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }
}

public class ShopifyOrderAddress
{
    [JsonPropertyName("first_name")]
    public string? FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; set; }

    [JsonPropertyName("address1")]
    public string? Address1 { get; set; }

    [JsonPropertyName("address2")]
    public string? Address2 { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("province")]
    public string? Province { get; set; }

    [JsonPropertyName("province_code")]
    public string? ProvinceCode { get; set; }

    [JsonPropertyName("zip")]
    public string? Zip { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("country_code")]
    public string? CountryCode { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    /// <summary>
    /// Shopify geocodes order addresses server-side and includes the result
    /// on the order webhook. Used for special-trip SPX bookings so Shiplogic
    /// doesn't have to geocode a possibly-vague ZA address itself.
    /// </summary>
    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
}

public class ShopifyOrderLineItem
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("product_id")]
    public long? ProductId { get; set; }

    [JsonPropertyName("variant_id")]
    public long? VariantId { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }

    /// <summary>Per-unit weight in grams.</summary>
    [JsonPropertyName("grams")]
    public long? Grams { get; set; }

    /// <summary>
    /// Per-line-item custom metadata. Date-picker apps like "DingDong
    /// Delivery" write the customer's chosen delivery/pickup date and time
    /// here (e.g. "Delivery Date" → "Friday, 03 July 2026").
    /// </summary>
    [JsonPropertyName("properties")]
    public List<ShopifyNameValue>? Properties { get; set; }
}

/// <summary>
/// Generic Shopify name/value pair, used for both order-level
/// note_attributes and line-item properties.
/// </summary>
public class ShopifyNameValue
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public class ShopifyOrderShippingLine
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("price")]
    public string? Price { get; set; }
}
