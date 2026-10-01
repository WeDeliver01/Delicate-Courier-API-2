using DelicateCouriers.ApiService.Features.Shopify.DTOs;

namespace DelicateCouriers.ApiService.Features.Shopify
{
    /// <summary>
    /// Service interface for interacting with the Shopify Admin API.
    /// Mirrors IWooCommerceService. Skeleton only — no real API calls yet.
    /// </summary>
    public interface IShopifyService
    {
        Task<ShopifyConnectionResponse> TestConnectionAsync(int storeId);

        Task<ShopifyOrdersResponse> FetchOrdersAsync(FetchOrdersRequest request);

        Task<ShopifyOrderDto?> GetOrderAsync(int storeId, string shopifyOrderId);

        Task<bool> UpdateOrderStatusAsync(int storeId, string shopifyOrderId, string status);

        Task<bool> AddTrackingInfoAsync(int storeId, string shopifyOrderId, string trackingNumber, string courierName);

        Task<List<ShopifyOrderDto>> FetchAllOrdersAsync(int storeId, string? status = null, DateTime? after = null, DateTime? before = null, int maxOrders = 10000);

        Task<ShopifyCarrierServiceResult> RegisterCarrierServiceAsync(int storeId);

        Task<ShopifyCarrierServiceListResult> ListCarrierServicesAsync(int storeId);

        Task<ShopifyWebhookSubscriptionResult> SubscribeOrderWebhooksAsync(int storeId);

        Task<ShopifyFullRegistrationResult> FullRegistrationAsync(int storeId);

        Task<List<ShopifyStoreSummary>> ListShopifyStoresAsync();
    }
}
