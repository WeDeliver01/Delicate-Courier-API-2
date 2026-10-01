using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shiplogic.DTOs;
using DelicateCouriers.Domain.Entities;

//using DelicateCouriers.Features.Shiplogic.DTOs;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Shipments;

/// <summary>
/// Service for querying and managing shipments
/// </summary>
public class ShipmentService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ShipmentService> _logger;

    public ShipmentService(AppDbContext context, ILogger<ShipmentService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get shipments with optional filtering
    /// </summary>
    public async Task<ShipmentListResult> GetShipmentsAsync(int days = 7, string? status = null)
    {
        _logger.LogInformation("Fetching shipments. Days: {Days}, Status: {Status}", days, status);

        var startDate = DateTime.UtcNow.AddDays(-days);

        // Query shipments with related data
        var query = _context.Shipments.Include(s => s.Order).ThenInclude(o => o.Store)
                                                            .ThenInclude(st => st.Tenant)
                                                            .Where(s => s.CreatedOn >= startDate);

        // Filter by status if provided
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.ShipmentStatus == status);
        }

        var shipments = await query.OrderByDescending(s => s.CreatedOn).ToListAsync();

        var result = new ShipmentListResult
        {
            Shipments = shipments.Select(s => new ShipmentDto
            {
                ShipmentId = s.ShipmentID,
                OrderId = s.OrderID,
                TrackingNumber = s.TrackingNumber,
                ConsignmentId = s.ConsignmentID,
                TenantName = s.Order.Store.Tenant.TenantName,
                StoreName = s.Order.Store.StoreName,
                CourierName = s.CourierName,
                CourierService = s.CourierService,
                Status = s.ShipmentStatus,
                ShippingCost = s.ShippingCost,
                EstimatedDeliveryDate = s.EstimatedDeliveryDate,
                CreatedOn = s.CreatedOn,
                OrderNumber = s.Order.WooOrderNumber
            }).ToList(),

            Total = shipments.Count
        };

        _logger.LogInformation("Found {Count} shipments", result.Total);

        return result;
    }

    /// <summary>
    /// Get dashboard statistics
    /// </summary>
    public async Task<ShipmentStatsResult> GetStatsAsync(int days = 7)
    {
        _logger.LogInformation("Fetching shipment stats for last {Days} days", days);

        var startDate = DateTime.UtcNow.AddDays(-days);

        var shipments = await _context.Shipments.Where(s => s.CreatedOn >= startDate).ToListAsync();

        var stats = new ShipmentStatsResult
        {
            Total = shipments.Count,
            Pending = shipments.Count(s => s.ShipmentStatus == "Created" || s.ShipmentStatus == "Pending"),
            InTransit = shipments.Count(s => s.ShipmentStatus == "InTransit" || s.ShipmentStatus == "PickedUp" || s.ShipmentStatus == "OutForDelivery"),
            Delivered = shipments.Count(s => s.ShipmentStatus == "Delivered"),
            Failed = shipments.Count(s => s.ShipmentStatus == "Failed")
        };

        _logger.LogInformation("Stats: Total={Total}, Pending={Pending}, InTransit={InTransit}, Delivered={Delivered}, Failed={Failed}",
            stats.Total, stats.Pending, stats.InTransit, stats.Delivered, stats.Failed);

        return stats;
    }


    /// <summary>
    /// Get shipment by ID with all related data
    /// </summary>
    public async Task<ShipmentDetailDto?> GetShipmentByIdAsync(int shipmentId)
    {
        _logger.LogInformation("Fetching shipment details for ShipmentID: {ShipmentId}", shipmentId);

        var shipment = await _context.Shipments.Include(s => s.Order)
                                               .ThenInclude(o => o.Store)
                                               .ThenInclude(st => st.Tenant)
                                               .Include(s => s.Label)
                                               .Include(s => s.TrackingEvents)
                                               .FirstOrDefaultAsync(s => s.ShipmentID == shipmentId);

        if (shipment == null)
        {
            _logger.LogWarning("Shipment {ShipmentId} not found", shipmentId);

            return null;
        }

        var result = new ShipmentDetailDto
        {
            ShipmentId = shipment.ShipmentID,
            OrderId = shipment.OrderID,
            TrackingNumber = shipment.TrackingNumber,
            ConsignmentId = shipment.ConsignmentID,
            CourierName = shipment.CourierName,
            CourierService = shipment.CourierService,
            Status = shipment.ShipmentStatus,
            ShippingCost = shipment.ShippingCost,
            EstimatedDeliveryDate = shipment.EstimatedDeliveryDate,
            ActualDeliveryDate = shipment.ActualDeliveryDate,
            CreatedOn = shipment.CreatedOn,
            // Requested schedule
            RequestedDeliveryDate = shipment.Order.RequestedDeliveryDate,
            RequestedDeliveryTime = shipment.Order.RequestedDeliveryTime,
            RequestedCollectionDate = shipment.Order.RequestedCollectionDate,
            RequestedCollectionTime = shipment.Order.RequestedCollectionTime,
            Occasion = shipment.Order.Occasion,

            // Order details
            OrderNumber = shipment.Order.WooOrderNumber,
            CustomerName = shipment.Order.CustomerName,
            CustomerEmail = shipment.Order.CustomerEmail,
            CustomerPhone = shipment.Order.CustomerPhone,

            // Delivery address
            DeliveryAddress = new AddressDto
            {
                Line1 = shipment.Order.ShippingAddressLine1,
                Line2 = shipment.Order.ShippingAddressLine2,
                City = shipment.Order.ShippingCity,
                Province = shipment.Order.ShippingProvince,
                PostalCode = shipment.Order.ShippingPostalCode,
                Country = shipment.Order.ShippingCountry
            },

            // Tenant and Store
            TenantName = shipment.Order.Store.Tenant.TenantName,
            StoreName = shipment.Order.Store.StoreName,

            // Label
            HasLabel = shipment.Label != null,
            LabelUrl = shipment.Label != null ? $"/api/shipments/{shipmentId}/label" : null,

            // Tracking events
            TrackingEvents = shipment.TrackingEvents.OrderByDescending(t => t.CreatedOn)
                                                    .Select(t => new TrackingEventDto
                                                    {
                                                        EventDate = t.CreatedOn,
                                                        //Status = t.Status,
                                                        //Location = t.Location,
                                                        //Description = t.Description
                                                    }).ToList()
        };

        _logger.LogInformation("Retrieved shipment {ShipmentId} with {EventCount} tracking events", shipmentId, result.TrackingEvents.Count);

        return result;
    }

    /// <summary>
    /// Get orders that don't have shipments yet
    /// Returns orders ready for manual shipment creation
    /// </summary>
    public async Task<List<OrderSummaryDto>> GetUnshippedOrdersAsync(int limit = 50)
    {
        _logger.LogInformation("Fetching orders without shipments (limit: {Limit})", limit);

        var orders = await _context.Orders.Include(o => o.Store).ThenInclude(s => s.Tenant)
                                          .Where(o => o.Shipment == null)  // No shipment exists
                                          .OrderByDescending(o => o.CreatedOn)
                                          .Take(limit)
                                          .Select(o => new OrderSummaryDto
                                          {
                                              OrderId = o.OrderID,
                                              OrderNumber = o.WooOrderNumber,
                                              CustomerName = o.CustomerName,
                                              TenantName = o.Store.Tenant.TenantName,
                                              StoreName = o.Store.StoreName,
                                              OrderTotal = o.OrderTotal,
                                              CreatedOn = o.CreatedOn
                                          })
                                          .ToListAsync();

        _logger.LogInformation("Found {Count} orders without shipments", orders.Count);

        return orders;
    }
}