// Features/Shopify/DTOs/ShopifyRateDtos.cs
//
// DTOs for Shopify's Carrier Service rate-callback request and response.
// These are PURE Shopify shapes — do not confuse with the internal
// AddressDto/ParcelDto used by Shiplogic.
//
// Reference: https://shopify.dev/docs/api/admin-rest/2024-04/resources/carrierservice
//
// All field names use Shopify's snake_case keys via JsonPropertyName.

using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.Shopify.DTOs;

// ─── INBOUND (Shopify → us) ──────────────────────────────────────────

public class ShopifyRateRequest
{
    [JsonPropertyName("rate")]
    public ShopifyRateRequestBody? Rate { get; set; }
}

public class ShopifyRateRequestBody
{
    [JsonPropertyName("origin")]
    public ShopifyAddress? Origin { get; set; }

    [JsonPropertyName("destination")]
    public ShopifyAddress? Destination { get; set; }

    [JsonPropertyName("items")]
    public List<ShopifyItem>? Items { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }
}

public class ShopifyAddress
{
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("postal_code")]
    public string? PostalCode { get; set; }

    [JsonPropertyName("province")]
    public string? Province { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("address1")]
    public string? Address1 { get; set; }

    [JsonPropertyName("address2")]
    public string? Address2 { get; set; }

    [JsonPropertyName("address3")]
    public string? Address3 { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("company_name")]
    public string? CompanyName { get; set; }
}

public class ShopifyItem
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    /// <summary>
    /// Per-item weight in grams. Already multiplied by qty by Shopify in
    /// some payload versions, but defensive code should multiply on top
    /// to handle older API versions. Default to 0 if absent.
    /// </summary>
    [JsonPropertyName("grams")]
    public int? Grams { get; set; }

    /// <summary>Price in cents (or smallest currency subunit). String in older versions.</summary>
    [JsonPropertyName("price")]
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? Price { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("requires_shipping")]
    public bool? RequiresShipping { get; set; }

    [JsonPropertyName("product_id")]
    public long? ProductId { get; set; }

    [JsonPropertyName("variant_id")]
    public long? VariantId { get; set; }
}

// ─── OUTBOUND (us → Shopify) ─────────────────────────────────────────

public class ShopifyRateResponse
{
    [JsonPropertyName("rates")]
    public List<ShopifyRate> Rates { get; set; } = new();
}

public class ShopifyRate
{
    [JsonPropertyName("service_name")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("service_code")]
    public string ServiceCode { get; set; } = string.Empty;

    /// <summary>
    /// In cents (smallest currency subunit), as a STRING. Shopify is strict
    /// about this — sending a number or a decimal-with-period will be rejected.
    /// </summary>
    [JsonPropertyName("total_price")]
    public string TotalPrice { get; set; } = "0";

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "ZAR";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional. ISO 8601 with timezone offset. If both min and max are
    /// provided, Shopify shows "Arrives between X and Y" at checkout.
    /// </summary>
    [JsonPropertyName("min_delivery_date")]
    public string? MinDeliveryDate { get; set; }

    [JsonPropertyName("max_delivery_date")]
    public string? MaxDeliveryDate { get; set; }
}
