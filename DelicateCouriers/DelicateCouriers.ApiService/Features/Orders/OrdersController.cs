using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DelicateCouriers.ApiService.Data;

namespace DelicateCouriers.ApiService.Features.Orders
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class OrdersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<OrdersController> _logger;

        public OrdersController(AppDbContext context, ILogger<OrdersController> logger)
        {
            _context = context;
            _logger = logger;
        }

        /// <summary>
        /// Get all orders with related store, tenant, and line items information
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetOrders()
        {
            try
            {
                var orders = await _context.Orders
                    .Include(o => o.Store)
                        .ThenInclude(s => s.Tenant)
                    .Include(o => o.LineItems) 
                    .OrderByDescending(o => o.OrderDate)
                    .Select(o => new
                    {
                        o.OrderID,
                        o.WooOrderID,
                        o.WooOrderNumber,
                        o.CustomerName,
                        o.CustomerEmail,
                        o.CustomerPhone,
                        o.OrderTotal,
                        o.OrderStatus,
                        o.OrderDate,
                        o.FulfillmentType,
                        o.SpecialTripDistanceKm,
                        o.SpecialTripQuotedAmount,
                        o.ShippingAddressLine1,
                        o.ShippingAddressLine2,
                        o.ShippingCity,
                        o.ShippingProvince,
                        o.ShippingPostalCode,
                        o.ShippingCountry,
                        StoreName = o.Store.StoreName,
                        TenantName = o.Store.Tenant.TenantName,
                        ShipmentID = o.Shipment != null ? (int?)o.Shipment.ShipmentID : null,
                        ShipmentStatus = o.Shipment != null ? o.Shipment.ShipmentStatus : null,
                        TrackingNumber = o.Shipment != null ? o.Shipment.TrackingNumber : null,
                        LineItems = o.LineItems.Select(li => new
                        {
                            li.OrderLineItemID,
                            li.ProductName,
                            li.ProductSKU,
                            li.Quantity,
                            li.UnitPrice,
                            li.LineTotal
                        }).ToList()
                    })
                    .ToListAsync();

                _logger.LogInformation("Retrieved {Count} orders", orders.Count);

                return Ok(orders);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving orders");
                return StatusCode(500, new { message = "Failed to retrieve orders" });
            }
        }
    }
}