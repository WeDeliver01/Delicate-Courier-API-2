// Features/Shopify/DTOs/ShopifyOrderPayload.cs
//
// Minimal Shopify order DTOs — covers JUST the fields we read during
// ingestion. Shopify's full order payload has ~100+ fields; we ignore
// everything that doesn't affect shipping. Adding more fields here is
// safe (JSON deserialization is case-insensitive and ignores unknowns).
//
// Reference: https://shopify.dev/docs/api/admin-rest/2024-04/resources/order

namespace DelicateCouriers.ApiService.Features.Shopify.DTOs;

public class ShopifyOrderPayload
{
    public long? Id { get; set; }

    /// <summary>
    /// The merchant-visible name like "#1042". Distinct from the numeric Id.
    /// </summary>
    public string? Name { get; set; }

    public long? OrderNumber { get; set; }

    public string? Email { get; set; }
    public string? CreatedAt { get; set; }
    public string? CancelledAt { get; set; }
    public string? Note { get; set; }
    public string? Currency { get; set; }
    public string? TotalPrice { get; set; }
    public string? FinancialStatus { get; set; }
    public string? FulfillmentStatus { get; set; }
    public List<string>? PaymentGatewayNames { get; set; }
    public ShopifyOrderCustomer? Customer { get; set; }
    public ShopifyOrderAddress? ShippingAddress { get; set; }
    public ShopifyOrderAddress? BillingAddress { get; set; }
    public List<ShopifyOrderLineItem>? LineItems { get; set; }
    public List<ShopifyOrderShippingLine>? ShippingLines { get; set; }
}

public class ShopifyOrderCustomer
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}

public class ShopifyOrderAddress
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? ProvinceCode { get; set; }
    public string? Zip { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? Phone { get; set; }
}

public class ShopifyOrderLineItem
{
    public long? Id { get; set; }
    public long? ProductId { get; set; }
    public long? VariantId { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Sku { get; set; }
    public int? Quantity { get; set; }
    public string? Price { get; set; }

    /// <summary>Per-unit weight in grams.</summary>
    public long? Grams { get; set; }
}

public class ShopifyOrderShippingLine
{
    public string? Code { get; set; }
    public string? Title { get; set; }
    public string? Price { get; set; }
}
