using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Features.Reports.DTOs;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Features.Reports;

/// <summary>
/// Service for generating reports and analytics
/// </summary>
public class ReportService
{
    private readonly AppDbContext _context;
    private readonly ILogger<ReportService> _logger;

    public ReportService(AppDbContext context, ILogger<ReportService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get shipment volume over time
    /// </summary>
    public async Task<ShipmentsOverTimeDto> GetShipmentsOverTimeAsync(DateTime from, DateTime to)
    {
        _logger.LogInformation("Fetching shipments over time from {From} to {To}", from, to);

        var shipments = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to)
                                                .GroupBy(s => s.CreatedOn.Date)
                                                .Select(g => new ShipmentTimePoint
                                                {
                                                    Date = g.Key,
                                                    Count = g.Count()
                                                })
                                                .OrderBy(x => x.Date)
                                                .ToListAsync();

        return new ShipmentsOverTimeDto { DataPoints = shipments };
    }

    /// <summary>
    /// Get performance metrics by tenant
    /// </summary>
    public async Task<TenantPerformanceDto> GetTenantPerformanceAsync(DateTime from, DateTime to)
    {
        _logger.LogInformation("Fetching tenant performance from {From} to {To}", from, to);

        var tenantPerformance = await _context.Shipments.Include(s => s.Order)
                                                        .ThenInclude(o => o.Store)
                                                        .ThenInclude(st => st.Tenant)
                                                        .Where(s => s.CreatedOn >= from && s.CreatedOn <= to)
                                                        .GroupBy(s => new { s.Order.Store.Tenant.TenantID, s.Order.Store.Tenant.TenantName })
                                                        .Select(g => new TenantPerformance
                                                        {
                                                            TenantID = g.Key.TenantID,
                                                            TenantName = g.Key.TenantName,
                                                            TotalShipments = g.Count(),
                                                            DeliveredShipments = g.Count(s => s.ShipmentStatus.ToLower() == "delivered"),
                                                            FailedShipments = g.Count(s => s.ShipmentStatus.ToLower() == "failed" || s.ShipmentStatus.ToLower() == "failed-delivery"),
                                                            TotalRevenue = g.Sum(s => s.ShippingCost)
                                                        })
                                                        .OrderByDescending(x => x.TotalShipments)
                                                        .ToListAsync();

        return new TenantPerformanceDto { Tenants = tenantPerformance };
    }

    /// <summary>
    /// Get breakdown of shipments by status
    /// </summary>
    public async Task<StatusBreakdownDto> GetStatusBreakdownAsync(DateTime from, DateTime to)
    {
        _logger.LogInformation("Fetching status breakdown from {From} to {To}", from, to);

        var totalShipments = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to).CountAsync();

        if (totalShipments == 0)
        {
            return new StatusBreakdownDto { Statuses = new List<StatusCount>() };
        }

        var statusCounts = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to)
                                                    .GroupBy(s => s.ShipmentStatus)
                                                    .Select(g => new StatusCount
                                                    {
                                                        Status = g.Key,
                                                        Count = g.Count(),
                                                        Percentage = (decimal)g.Count() / totalShipments * 100
                                                    })
                                                    .OrderByDescending(x => x.Count)
                                                    .ToListAsync();

        return new StatusBreakdownDto { Statuses = statusCounts };
    }

    /// <summary>
    /// Get order-level business metrics for the range: order count + revenue,
    /// and the count + shipping revenue of shipments booked in the range.
    /// Tenant scoping is applied automatically by the global query filter.
    /// </summary>
    public async Task<OrdersSummaryDto> GetOrdersSummaryAsync(DateTime from, DateTime to)
    {
        _logger.LogInformation("Fetching orders summary from {From} to {To}", from, to);

        var orders = await _context.Orders.Where(o => o.CreatedOn >= from && o.CreatedOn <= to)
                                          .GroupBy(o => 1)
                                          .Select(g => new
                                          {
                                              TotalOrders = g.Count(),
                                              TotalOrderRevenue = g.Sum(o => o.OrderTotal)
                                          })
                                          .FirstOrDefaultAsync();

        var shipments = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to)
                                                .GroupBy(s => 1)
                                                .Select(g => new
                                                {
                                                    BookedShipments = g.Count(),
                                                    ShippingRevenue = g.Sum(s => s.ShippingCost)
                                                })
                                                .FirstOrDefaultAsync();

        return new OrdersSummaryDto
        {
            TotalOrders = orders?.TotalOrders ?? 0,
            TotalOrderRevenue = orders?.TotalOrderRevenue ?? 0m,
            BookedShipments = shipments?.BookedShipments ?? 0,
            ShippingRevenue = shipments?.ShippingRevenue ?? 0m
        };
    }

    /// <summary>
    /// Get shipment bookings grouped by hour of day (0-23) in South African time
    /// (SAST / UTC+2). Returns all 24 buckets, zero-filled, so the chart is stable.
    /// </summary>
    public async Task<BookingsByHourDto> GetBookingsByHourAsync(DateTime from, DateTime to)
    {
        _logger.LogInformation("Fetching bookings by hour from {From} to {To}", from, to);

        // Pull the booking timestamps (UTC) and bucket by SAST hour in memory so the
        // +2 offset conversion is unambiguous and DB-provider independent.
        var bookingTimes = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to)
                                                   .Select(s => s.CreatedOn)
                                                   .ToListAsync();

        var counts = new int[24];
        foreach (var utc in bookingTimes)
        {
            var sastHour = (utc.Hour + 2) % 24;
            counts[sastHour]++;
        }

        var hours = Enumerable.Range(0, 24)
                              .Select(h => new BookingHourPoint { Hour = h, Count = counts[h] })
                              .ToList();

        return new BookingsByHourDto { Hours = hours };
    }

    /// <summary>
    /// Get overall success rate
    /// </summary>
    public async Task<SuccessRateDto> GetSuccessRateAsync(DateTime from, DateTime to)
    {
        _logger.LogInformation("Fetching success rate from {From} to {To}", from, to);

        var totalShipments = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to).CountAsync();
        var successfulShipments = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to && s.ShipmentStatus.ToLower() == "delivered").CountAsync();
        var failedShipments = await _context.Shipments.Where(s => s.CreatedOn >= from && s.CreatedOn <= to && (s.ShipmentStatus.ToLower() == "failed" || s.ShipmentStatus.ToLower() == "failed-delivery" || s.ShipmentStatus.ToLower() == "failed-collection")).CountAsync();
        var successRate = totalShipments > 0 ? (decimal)successfulShipments / totalShipments * 100 : 0;
        var failureRate = totalShipments > 0 ? (decimal)failedShipments / totalShipments * 100 : 0;

        return new SuccessRateDto
        {
            TotalShipments = totalShipments,
            SuccessfulShipments = successfulShipments,
            FailedShipments = failedShipments,
            SuccessRate = Math.Round(successRate, 2),
            FailureRate = Math.Round(failureRate, 2)
        };
    }
}