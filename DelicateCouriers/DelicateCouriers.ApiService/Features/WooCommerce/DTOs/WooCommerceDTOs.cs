using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.WooCommerce.DTOs
{
    public class WooCommerceOrder
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("number")]
        public string Number { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = string.Empty;

        [JsonPropertyName("date_created")]
        public DateTime DateCreated { get; set; }

        /// <summary>
        /// UTC creation time. Woo's `date_created` is store-local; prefer
        /// this field when converting to UTC for persistence.
        /// </summary>
        [JsonPropertyName("date_created_gmt")]
        public DateTime? DateCreatedGmt { get; set; }

        [JsonPropertyName("total")]
        public string Total { get; set; } = string.Empty;

        [JsonPropertyName("billing")]
        public WooCommerceBilling Billing { get; set; } = new();

        [JsonPropertyName("shipping")]
        public WooCommerceShipping Shipping { get; set; } = new();

        [JsonPropertyName("line_items")]
        public List<WooCommerceLineItem> LineItems { get; set; } = new();

        [JsonPropertyName("meta_data")]
        public List<WooCommerceOrderMeta>? MetaData { get; set; }

        [JsonPropertyName("shipping_lines")]
        public List<WooCommerceShippingLine>? ShippingLines { get; set; }
    }

    /// <summary>
    /// One entry of a Woo order's `shipping_lines` — the shipping method
    /// the customer actually chose at checkout. `method_id` is the plugin
    /// slug ("flat_rate", "local_pickup", …) and `method_title` is the
    /// merchant-configured label ("Collect from 1 Clifford road…").
    /// </summary>
    public class WooCommerceShippingLine
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("method_id")]
        public string MethodId { get; set; } = string.Empty;

        [JsonPropertyName("method_title")]
        public string MethodTitle { get; set; } = string.Empty;

        [JsonPropertyName("total")]
        public string Total { get; set; } = string.Empty;
    }

    /// <summary>
    /// Order-level meta entry. Woo meta values can be strings, arrays or
    /// objects, so the value is kept as a raw JsonElement and only read
    /// when it is a plain string.
    /// </summary>
    public class WooCommerceOrderMeta
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public System.Text.Json.JsonElement Value { get; set; }

        public string? StringValue =>
            Value.ValueKind == System.Text.Json.JsonValueKind.String ? Value.GetString() : null;
    }

    public class WooCommerceBilling
    {
        [JsonPropertyName("first_name")]
        public string FirstName { get; set; } = string.Empty;

        [JsonPropertyName("last_name")]
        public string LastName { get; set; } = string.Empty;

        [JsonPropertyName("company")]
        public string Company { get; set; } = string.Empty;

        [JsonPropertyName("address_1")]
        public string Address1 { get; set; } = string.Empty;

        [JsonPropertyName("address_2")]
        public string Address2 { get; set; } = string.Empty;

        [JsonPropertyName("city")]
        public string City { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("postcode")]
        public string Postcode { get; set; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;
    }

    public class WooCommerceShipping
    {
        [JsonPropertyName("first_name")]
        public string FirstName { get; set; } = string.Empty;

        [JsonPropertyName("last_name")]
        public string LastName { get; set; } = string.Empty;

        [JsonPropertyName("company")]
        public string Company { get; set; } = string.Empty;

        [JsonPropertyName("address_1")]
        public string Address1 { get; set; } = string.Empty;

        [JsonPropertyName("address_2")]
        public string Address2 { get; set; } = string.Empty;

        [JsonPropertyName("city")]
        public string City { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("postcode")]
        public string Postcode { get; set; } = string.Empty;

        [JsonPropertyName("country")]
        public string Country { get; set; } = string.Empty;
    }

    public class WooCommerceLineItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("product_id")]
        public int ProductId { get; set; }

        [JsonPropertyName("quantity")]
        public int Quantity { get; set; }

        [JsonPropertyName("total")]
        public string Total { get; set; } = string.Empty;

        [JsonPropertyName("sku")]
        public string Sku { get; set; } = string.Empty;

        [JsonPropertyName("subtotal")]
        public string Subtotal { get; set; } = string.Empty;

        [JsonPropertyName("subtotal_tax")]
        public string SubtotalTax { get; set; } = string.Empty;

        [JsonPropertyName("total_tax")]
        public string TotalTax { get; set; } = string.Empty;

        [JsonPropertyName("weight")]
        public decimal? Weight { get; set; }

        [JsonPropertyName("meta_data")]
        public List<WooCommerceMetaData>? MetaData { get; set; }
    }

    /// <summary>
    /// Line-item meta entry. Woo meta values can be strings, numbers, arrays
    /// or objects (e.g. product add-on / upload plugins store objects), so
    /// both `value` and `display_value` are kept as raw JsonElements and only
    /// read as strings when they actually are strings. Typing these as
    /// `string` breaks deserialisation of the ENTIRE order for any order
    /// containing one structured meta entry.
    /// </summary>
    public class WooCommerceMetaData
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public System.Text.Json.JsonElement Value { get; set; }

        [JsonPropertyName("display_key")]
        public string DisplayKey { get; set; } = string.Empty;

        [JsonPropertyName("display_value")]
        public System.Text.Json.JsonElement DisplayValue { get; set; }

        [JsonIgnore]
        public string? StringValue =>
            Value.ValueKind == System.Text.Json.JsonValueKind.String ? Value.GetString() : null;

        [JsonIgnore]
        public string? DisplayStringValue =>
            DisplayValue.ValueKind == System.Text.Json.JsonValueKind.String ? DisplayValue.GetString() : null;
    }

    public class WooCommerceConnectionResponse
    {
        public bool IsConnected { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? StoreUrl { get; set; }
        public string? WooCommerceVersion { get; set; }
    }

    public class FetchOrdersRequest
    {
        public int StoreID { get; set; }
        public string? Status { get; set; }
        public DateTime? After { get; set; }
        public DateTime? Before { get; set; }
        public int Page { get; set; } = 1;
        public int PerPage { get; set; } = 10;
    }
}