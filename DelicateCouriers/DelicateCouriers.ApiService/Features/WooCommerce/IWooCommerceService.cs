using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Service interface for interacting with WooCommerce REST API
    /// This defines all operations we can perform with WooCommerce stores
    /// </summary>
    public interface IWooCommerceService
    {
        /// <summary>
        /// Test if we can connect to a WooCommerce store with given credentials
        /// </summary>
        /// <param name="storeId">The store ID from our database</param>
        /// <returns>Connection test result with status and version info</returns>
        Task<WooCommerceConnectionResponse> TestConnectionAsync(int storeId);

        /// <summary>
        /// Fetch orders from a WooCommerce store with optional filters
        /// </summary>
        /// <param name="request">Filter parameters (status, date range, pagination)</param>
        /// <returns>List of WooCommerce orders</returns>
        Task<List<WooCommerceOrder>> FetchOrdersAsync(FetchOrdersRequest request);

        /// <summary>
        /// Get a specific order by WooCommerce order ID
        /// </summary>
        /// <param name="storeId">The store ID from our database</param>
        /// <param name="wooOrderId">The WooCommerce order ID</param>
        /// <returns>The WooCommerce order details</returns>
        Task<WooCommerceOrder?> GetOrderAsync(int storeId, int wooOrderId);

        /// <summary>
        /// Update order status in WooCommerce (e.g., mark as completed)
        /// </summary>
        /// <param name="storeId">The store ID from our database</param>
        /// <param name="wooOrderId">The WooCommerce order ID</param>
        /// <param name="status">New status (processing, completed, etc.)</param>
        /// <returns>True if successful</returns>
        Task<bool> UpdateOrderStatusAsync(int storeId, int wooOrderId, string status);

        /// <summary>
        /// Add tracking information to a WooCommerce order
        /// </summary>
        /// <param name="storeId">The store ID from our database</param>
        /// <param name="wooOrderId">The WooCommerce order ID</param>
        /// <param name="trackingNumber">Shiplogic tracking number</param>
        /// <param name="courierName">Courier service name</param>
        /// <returns>True if successful</returns>
        Task<bool> AddTrackingInfoAsync(int storeId, int wooOrderId, string trackingNumber, string courierName);

        /// <summary>
        /// Add a plain order note to a WooCommerce order via
        /// <c>POST /wp-json/wc/v3/orders/{id}/notes</c>. Internal by default
        /// (customer_note = false) so no customer email is triggered.
        /// </summary>
        /// <returns>True on 2xx from WooCommerce.</returns>
        Task<bool> AddOrderNoteAsync(int storeId, int wooOrderId, string note, bool customerNote = false);

        /// <summary>
        /// Upsert a batch of <c>meta_data</c> entries on a WooCommerce order via
        /// <c>PUT /wp-json/wc/v3/orders/{id}</c>. Used to stamp the order with
        /// <c>_dcp_*</c> shipment fields (tracking number, courier, status,
        /// courier rate, etc.) so the plugin's "Shipment &amp; Tracking"
        /// metabox can render them. Keys/values are passed verbatim — null
        /// values delete the meta key.
        /// </summary>
        /// <returns>True on 2xx from WooCommerce.</returns>
        Task<bool> PushShipmentMetaAsync(int storeId, int wooOrderId, IReadOnlyDictionary<string, string?> meta);

        /// <summary>
        /// Same as <see cref="PushShipmentMetaAsync"/> but returns which path
        /// was used and the response body, so callers (the manual Resync
        /// endpoint in particular) can surface real diagnostics — including
        /// the plugin's "updated=N" count, which is what actually proves the
        /// meta landed in WP.
        /// </summary>
        Task<PushShipmentMetaResult> PushShipmentMetaDetailedAsync(
            int storeId,
            int wooOrderId,
            IReadOnlyDictionary<string, string?> meta,
            string? note = null,
            bool noteIsCustomerNote = false);

        /// <summary>
        /// Fetch all orders in bulk with automatic pagination
        /// Efficiently handles 1000s of orders by fetching in batches
        /// </summary>
        /// <param name="storeId">Store ID from database</param>
        /// <param name="status">Optional status filter (processing, completed, etc.)</param>
        /// <param name="after">Optional date filter - orders after this date</param>
        /// <param name="before">Optional date filter - orders before this date</param>
        /// <param name="maxOrders">Maximum number of orders to fetch (default 10,000)</param>
        /// <returns>List of all orders matching criteria</returns>
        Task<List<WooCommerceOrder>> FetchAllOrdersAsync(int storeId, string? status = null, DateTime? after = null, DateTime? before = null, int maxOrders = 10000);
    }
}