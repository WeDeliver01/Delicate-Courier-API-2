using DelicateCouriers.ApiService.Features.Shopify.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.ApiService.Features.Shopify
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class ShopifyController : ControllerBase
    {
        private readonly IShopifyService _shopifyService;
        private readonly ILogger<ShopifyController> _logger;

        public ShopifyController(IShopifyService shopifyService, ILogger<ShopifyController> logger)
        {
            _shopifyService = shopifyService;
            _logger = logger;
        }

        [HttpGet("test-connection/{storeId}")]
        public async Task<ActionResult<ShopifyConnectionResponse>> TestConnection(int storeId)
        {
            try
            {
                _logger.LogInformation("Testing Shopify connection for StoreID: {StoreId}", storeId);

                var result = await _shopifyService.TestConnectionAsync(storeId);

                if (result.IsConnected)
                {
                    return Ok(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing Shopify connection for StoreID: {StoreId}", storeId);

                return StatusCode(500, new { error = "Failed to test connection", message = ex.Message });
            }
        }

        [HttpPost("fetch-orders")]
        public async Task<ActionResult<ShopifyOrdersResponse>> FetchOrders([FromBody] FetchOrdersRequest request)
        {
            try
            {
                _logger.LogInformation("Fetching Shopify orders for StoreID: {StoreId}, Page: {Page}", request.StoreID, request.Page);

                var result = await _shopifyService.FetchOrdersAsync(request);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Shopify orders for StoreID: {StoreId}", request.StoreID);

                return StatusCode(500, new { error = "Failed to fetch orders", message = ex.Message });
            }
        }

        [HttpGet("get-order/{storeId}/{shopifyOrderId}")]
        public async Task<ActionResult<ShopifyOrderDto>> GetOrder(int storeId, string shopifyOrderId)
        {
            try
            {
                _logger.LogInformation("Fetching Shopify order {OrderId} from StoreID: {StoreId}", shopifyOrderId, storeId);

                var order = await _shopifyService.GetOrderAsync(storeId, shopifyOrderId);

                if (order == null)
                {
                    return NotFound(new { error = $"Shopify order {shopifyOrderId} not found in store {storeId}" });
                }

                return Ok(order);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Shopify order {OrderId} from StoreID: {StoreId}", shopifyOrderId, storeId);

                return StatusCode(500, new { error = "Failed to fetch order", message = ex.Message });
            }
        }

        [HttpPut("update-order-status/{storeId}/{shopifyOrderId}")]
        public async Task<ActionResult> UpdateOrderStatus(int storeId, string shopifyOrderId, [FromBody] UpdateOrderStatusRequest request)
        {
            try
            {
                _logger.LogInformation("Updating Shopify order {OrderId} status to {Status} in StoreID: {StoreId}", shopifyOrderId, request.Status, storeId);

                var success = await _shopifyService.UpdateOrderStatusAsync(storeId, shopifyOrderId, request.Status);

                if (success)
                {
                    return Ok(new { message = $"Shopify order {shopifyOrderId} status updated to {request.Status}" });
                }

                return BadRequest(new { error = "Failed to update order status" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Shopify order {OrderId} status in StoreID: {StoreId}", shopifyOrderId, storeId);

                return StatusCode(500, new { error = "Failed to update order status", message = ex.Message });
            }
        }

        [HttpPost("add-tracking-info/{storeId}/{shopifyOrderId}")]
        public async Task<ActionResult> AddTrackingInfo(int storeId, string shopifyOrderId, [FromBody] AddTrackingInfoRequest request)
        {
            try
            {
                _logger.LogInformation("Adding tracking info to Shopify order {OrderId} in StoreID: {StoreId}", shopifyOrderId, storeId);

                var success = await _shopifyService.AddTrackingInfoAsync(storeId, shopifyOrderId, request.TrackingNumber, request.CourierName);

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
                _logger.LogError(ex, "Error adding tracking info to Shopify order {OrderId} in StoreID: {StoreId}", shopifyOrderId, storeId);

                return StatusCode(500, new { error = "Failed to add tracking information", message = ex.Message });
            }
        }

        /// <summary>
        /// List active Shopify-platform stores for the SuperAdmin carrier-service
        /// UI. Returns no secrets — only enough to identify each store and whether
        /// it has an access token on file. SuperAdmin-only.
        /// </summary>
        [HttpGet("stores")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult<List<ShopifyStoreSummary>>> ListStores()
        {
            try
            {
                var stores = await _shopifyService.ListShopifyStoresAsync();
                return Ok(stores);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing Shopify stores");
                return StatusCode(500, new { error = "Failed to list Shopify stores", message = ex.Message });
            }
        }

        /// <summary>
        /// Register (or update) the Shopify Carrier Service for a store so our
        /// live-rate callback appears at the merchant's checkout. SuperAdmin-only.
        /// </summary>
        [HttpPost("register-carrier-service/{storeId}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> RegisterCarrierService(int storeId)
        {
            try
            {
                _logger.LogInformation("Registering Shopify carrier service for StoreID: {StoreId}", storeId);

                var result = await _shopifyService.RegisterCarrierServiceAsync(storeId);

                if (result.Success)
                {
                    return Ok(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error registering Shopify carrier service for StoreID: {StoreId}", storeId);

                return StatusCode(500, new { error = "Failed to register carrier service", message = ex.Message });
            }
        }

        /// <summary>
        /// List the carrier services registered on a store (read-only) so a
        /// SuperAdmin can verify the Delicate Couriers carrier service is present
        /// and pointing at the right callback. SuperAdmin-only.
        /// </summary>
        [HttpGet("carrier-services/{storeId}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> ListCarrierServices(int storeId)
        {
            try
            {
                _logger.LogInformation("Listing Shopify carrier services for StoreID: {StoreId}", storeId);

                var result = await _shopifyService.ListCarrierServicesAsync(storeId);

                if (result.Success)
                {
                    return Ok(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing Shopify carrier services for StoreID: {StoreId}", storeId);

                return StatusCode(500, new { error = "Failed to list carrier services", message = ex.Message });
            }
        }

        /// <summary>
        /// Subscribe the store to the order webhooks (orders/create, orders/updated,
        /// orders/cancelled) that drive order ingestion. Idempotent. SuperAdmin-only.
        /// </summary>
        [HttpPost("register-webhooks/{storeId}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> RegisterWebhooks(int storeId)
        {
            try
            {
                _logger.LogInformation("Subscribing Shopify order webhooks for StoreID: {StoreId}", storeId);

                var result = await _shopifyService.SubscribeOrderWebhooksAsync(storeId);

                if (result.Success)
                {
                    return Ok(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error subscribing Shopify order webhooks for StoreID: {StoreId}", storeId);

                return StatusCode(500, new { error = "Failed to subscribe order webhooks", message = ex.Message });
            }
        }

        /// <summary>
        /// One-shot store setup: test the connection, then register the carrier
        /// service and subscribe the order webhooks. Each step is idempotent, so
        /// this is safe to re-run. SuperAdmin-only.
        /// </summary>
        [HttpPost("full-registration/{storeId}")]
        [Authorize(Roles = "SuperAdmin")]
        public async Task<ActionResult> FullRegistration(int storeId)
        {
            try
            {
                _logger.LogInformation("Running Shopify full registration for StoreID: {StoreId}", storeId);

                var result = await _shopifyService.FullRegistrationAsync(storeId);

                if (result.Success)
                {
                    return Ok(result);
                }

                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running Shopify full registration for StoreID: {StoreId}", storeId);

                return StatusCode(500, new { error = "Failed to run full registration", message = ex.Message });
            }
        }

        [HttpPost("fetch-all-orders")]
        public async Task<ActionResult> FetchAllOrders([FromBody] FetchOrdersRequest request)
        {
            try
            {
                _logger.LogInformation("Bulk fetching Shopify orders for StoreID: {StoreId}", request.StoreID);

                var orders = await _shopifyService.FetchAllOrdersAsync(
                    request.StoreID,
                    request.Status,
                    request.After,
                    request.Before);

                return Ok(new
                {
                    storeId = request.StoreID,
                    totalOrders = orders.Count,
                    status = request.Status,
                    after = request.After,
                    before = request.Before,
                    orders
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error bulk fetching Shopify orders for StoreID: {StoreId}", request.StoreID);

                return StatusCode(500, new { error = "Failed to fetch orders in bulk", message = ex.Message });
            }
        }
    }
}
