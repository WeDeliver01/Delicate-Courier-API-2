using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using System.Security.Claims;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class WooCommerceController : ControllerBase
    {
        private readonly IWooCommerceService _wooCommerceService;
        private readonly ILogger<WooCommerceController> _logger;

        public WooCommerceController(IWooCommerceService wooCommerceService, ILogger<WooCommerceController> logger)
        {
            _wooCommerceService = wooCommerceService;
            _logger = logger;
        }

        /// <summary>
        /// Test connection to a WooCommerce store
        /// Verifies that the stored credentials work
        /// </summary>
        /// <param name="storeId">Store ID from your database</param>
        /// <returns>Connection test result</returns>
        [HttpGet("test-connection/{storeId}")]
        public async Task<ActionResult<WooCommerceConnectionResponse>> TestConnection(int storeId)
        {
            try
            {
                _logger.LogInformation("Testing WooCommerce connection for StoreID: {StoreId}", storeId);

                var result = await _wooCommerceService.TestConnectionAsync(storeId);

                if (result.IsConnected)
                {
                    return Ok(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing WooCommerce connection for StoreID: {StoreId}", storeId);

                return StatusCode(500, new { error = "Failed to test connection", message = ex.Message });
            }
        }

        /// <summary>
        /// Fetch orders from a WooCommerce store with optional filters
        /// Supports pagination for large order volumes
        /// </summary>
        /// <param name="request">Filter parameters (status, date range, pagination)</param>
        /// <returns>List of WooCommerce orders</returns>
        [HttpPost("fetch-orders")]
        public async Task<ActionResult<List<WooCommerceOrder>>> FetchOrders([FromBody] FetchOrdersRequest request)
        {
            try
            {
                _logger.LogInformation("Fetching orders for StoreID: {StoreId}, Page: {Page}", request.StoreID, request.Page);

                var orders = await _wooCommerceService.FetchOrdersAsync(request);

                return Ok(new
                {
                    storeId = request.StoreID,
                    page = request.Page,
                    perPage = request.PerPage,
                    count = orders.Count,
                    orders
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching orders for StoreID: {StoreId}", request.StoreID);

                return StatusCode(500, new { error = "Failed to fetch orders", message = ex.Message });
            }
        }

        /// <summary>
        /// Fetch ALL orders from a WooCommerce store with automatic pagination
        /// Efficiently handles bulk order sync (1000s of orders)
        /// </summary>
        /// <param name="storeId">Store ID from your database</param>
        /// <param name="status">Optional status filter</param>
        /// <param name="after">Optional - orders after this date</param>
        /// <param name="before">Optional - orders before this date</param>
        /// <param name="maxOrders">Maximum orders to fetch (default 10,000)</param>
        /// <returns>All orders matching criteria</returns>
        [HttpGet("fetch-all-orders/{storeId}")]
        public async Task<ActionResult> FetchAllOrders(int storeId, [FromQuery] string? status = null, [FromQuery] DateTime? after = null, [FromQuery] DateTime? before = null, [FromQuery] int maxOrders = 10000)
        {
            try
            {
                _logger.LogInformation("Bulk fetching orders for StoreID: {StoreId}, MaxOrders: {MaxOrders}", storeId, maxOrders);

                var orders = await _wooCommerceService.FetchAllOrdersAsync(storeId, status, after, before, maxOrders);

                return Ok(new
                {
                    storeId,
                    totalOrders = orders.Count,
                    status,
                    after,
                    before,
                    maxOrders,
                    orders
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error bulk fetching orders for StoreID: {StoreId}", storeId);

                return StatusCode(500, new { error = "Failed to fetch orders in bulk", message = ex.Message });
            }
        }

        /// <summary>
        /// Get a specific order by WooCommerce order ID
        /// </summary>
        /// <param name="storeId">Store ID from your database</param>
        /// <param name="wooOrderId">WooCommerce order ID</param>
        /// <returns>Order details</returns>
        [HttpGet("orders/{storeId}/{wooOrderId}")]
        public async Task<ActionResult<WooCommerceOrder>> GetOrder(int storeId, int wooOrderId)
        {
            try
            {
                _logger.LogInformation("Fetching order {OrderId} from StoreID: {StoreId}", wooOrderId, storeId);

                var order = await _wooCommerceService.GetOrderAsync(storeId, wooOrderId);

                if (order == null)
                {
                    return NotFound(new { error = $"Order {wooOrderId} not found in store {storeId}" });
                }

                return Ok(order);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching order {OrderId} from StoreID: {StoreId}", wooOrderId, storeId);

                return StatusCode(500, new { error = "Failed to fetch order", message = ex.Message });
            }
        }

        /// <summary>
        /// Update order status in WooCommerce
        /// </summary>
        /// <param name="storeId">Store ID from your database</param>
        /// <param name="wooOrderId">WooCommerce order ID</param>
        /// <param name="request">New status (processing, completed, etc.)</param>
        /// <returns>Success status</returns>
        [HttpPut("orders/{storeId}/{wooOrderId}/status")]
        public async Task<ActionResult> UpdateOrderStatus(int storeId, int wooOrderId, [FromBody] UpdateOrderStatusRequest request)
        {
            try
            {
                _logger.LogInformation("Updating order {OrderId} status to {Status} in StoreID: {StoreId}", wooOrderId, request.Status, storeId);

                var success = await _wooCommerceService.UpdateOrderStatusAsync(storeId, wooOrderId, request.Status);

                if (success)
                {
                    return Ok(new { message = $"Order {wooOrderId} status updated to {request.Status}" });
                }

                return BadRequest(new { error = "Failed to update order status" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating order {OrderId} status in StoreID: {StoreId}", wooOrderId, storeId);

                return StatusCode(500, new { error = "Failed to update order status", message = ex.Message });
            }
        }

        /// <summary>
        /// Add tracking information to a WooCommerce order
        /// This will add a customer-visible note with tracking details
        /// </summary>
        /// <param name="storeId">Store ID from your database</param>
        /// <param name="wooOrderId">WooCommerce order ID</param>
        /// <param name="request">Tracking information</param>
        /// <returns>Success status</returns>
        [HttpPost("orders/{storeId}/{wooOrderId}/tracking")]
        public async Task<ActionResult> AddTrackingInfo(int storeId, int wooOrderId, [FromBody] AddTrackingInfoRequest request)
        {
            try
            {
                _logger.LogInformation("Adding tracking info to order {OrderId} in StoreID: {StoreId}", wooOrderId, storeId);

                var success = await _wooCommerceService.AddTrackingInfoAsync(storeId,
                                                                             wooOrderId,
                                                                             request.TrackingNumber,
                                                                             request.CourierName
                );

                if (success)
                {
                    return Ok(new
                    {
                        message = "Tracking information added successfully",
                        trackingNumber = request.TrackingNumber,
                        courier = request.CourierName
                    });
                }

                return BadRequest(new { error = "Failed to add tracking information" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding tracking info to order {OrderId} in StoreID: {StoreId}", wooOrderId, storeId);
                
                return StatusCode(500, new { error = "Failed to add tracking information", message = ex.Message });
            }
        }
    }

    /// <summary>
    /// Request to update order status
    /// </summary>
    public class UpdateOrderStatusRequest
    {
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request to add tracking information
    /// </summary>
    public class AddTrackingInfoRequest
    {
        public string TrackingNumber { get; set; } = string.Empty;
        public string CourierName { get; set; } = string.Empty;
    }
}