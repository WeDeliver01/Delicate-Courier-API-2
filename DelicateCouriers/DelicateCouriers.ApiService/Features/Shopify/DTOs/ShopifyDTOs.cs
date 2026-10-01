using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.Shopify.DTOs
{
    /// <summary>
    /// Shopify order DTO. Skeleton mirror of WooCommerceOrder — fields will be
    /// fleshed out when the real Shopify Admin API integration is implemented.
    /// </summary>
    public class ShopifyOrderDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("order_number")]
        public string OrderNumber { get; set; } = string.Empty;

        [JsonPropertyName("financial_status")]
        public string FinancialStatus { get; set; } = string.Empty;

        [JsonPropertyName("fulfillment_status")]
        public string? FulfillmentStatus { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("total_price")]
        public string TotalPrice { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of a Shopify connection test (mirror of WooCommerceConnectionResponse).
    /// </summary>
    public class ShopifyConnectionResponse
    {
        public bool IsConnected { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? StoreUrl { get; set; }
        public string? ShopifyApiVersion { get; set; }
    }

    /// <summary>
    /// Wrapper response for fetching a page of Shopify orders.
    /// </summary>
    public class ShopifyOrdersResponse
    {
        public int StoreID { get; set; }
        public int Count { get; set; }
        public List<ShopifyOrderDto> Orders { get; set; } = new();
    }

    /// <summary>
    /// Filter parameters for fetching Shopify orders.
    /// Same shape as the WooCommerce FetchOrdersRequest, kept independent
    /// so each integration can evolve its own filters later.
    /// </summary>
    public class FetchOrdersRequest
    {
        public int StoreID { get; set; }
        public string? Status { get; set; }
        public DateTime? After { get; set; }
        public DateTime? Before { get; set; }
        public int Page { get; set; } = 1;
        public int PerPage { get; set; } = 10;
    }

    public class UpdateOrderStatusRequest
    {
        public string Status { get; set; } = string.Empty;
    }

    public class AddTrackingInfoRequest
    {
        public string TrackingNumber { get; set; } = string.Empty;
        public string CourierName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Lightweight summary of a Shopify-platform store for the SuperAdmin
    /// carrier-service UI. No secrets — just enough to identify the store and
    /// whether it has the credentials needed to register live rates.
    /// </summary>
    public class ShopifyStoreSummary
    {
        public int StoreID { get; set; }
        public string StoreName { get; set; } = string.Empty;
        public int TenantID { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public string? ShopifyStoreUrl { get; set; }
        public bool HasAccessToken { get; set; }
        public bool IsActive { get; set; }
    }
}
