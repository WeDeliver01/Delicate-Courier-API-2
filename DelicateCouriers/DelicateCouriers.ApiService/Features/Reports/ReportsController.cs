using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DelicateCouriers.Features.Reports.DTOs;

namespace DelicateCouriers.Features.Reports;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ReportsController : ControllerBase
{
    private readonly ReportService _reportService;

    public ReportsController(ReportService reportService)
    {
        _reportService = reportService;
    }

    /// <summary>
    /// Get shipments over time
    /// GET /api/reports/shipments-over-time?from=2025-01-01&to=2025-12-31
    /// </summary>
    [HttpGet("shipments-over-time")]
    public async Task<ActionResult<ShipmentsOverTimeDto>> GetShipmentsOverTime(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        // Default to last 30 days if not specified, ensure UTC
        var fromDate = from.HasValue
            ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc)
            : DateTime.UtcNow.AddDays(-30);
        var toDate = to.HasValue
            ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc)
            : DateTime.UtcNow;

        var result = await _reportService.GetShipmentsOverTimeAsync(fromDate, toDate);
        return Ok(result);
    }

    /// <summary>
    /// Get performance by tenant
    /// GET /api/reports/tenant-performance?from=2025-01-01&to=2025-12-31
    /// </summary>
    [HttpGet("tenant-performance")]
    public async Task<ActionResult<TenantPerformanceDto>> GetTenantPerformance(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var fromDate = from.HasValue ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc) : DateTime.UtcNow.AddDays(-30);
        var toDate = to.HasValue ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc) : DateTime.UtcNow;

        var result = await _reportService.GetTenantPerformanceAsync(fromDate, toDate);
        return Ok(result);
    }

    /// <summary>
    /// Get status breakdown
    /// GET /api/reports/status-breakdown?from=2025-01-01&to=2025-12-31
    /// </summary>
    [HttpGet("status-breakdown")]
    public async Task<ActionResult<StatusBreakdownDto>> GetStatusBreakdown([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var fromDate = from.HasValue ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc) : DateTime.UtcNow.AddDays(-30);
        var toDate = to.HasValue ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc) : DateTime.UtcNow;

        var result = await _reportService.GetStatusBreakdownAsync(fromDate, toDate);

        return Ok(result);
    }

    /// <summary>
    /// Get order metrics summary (order count + revenue, booked shipments + shipping revenue)
    /// GET /api/reports/orders-summary?from=2025-01-01&to=2025-12-31
    /// </summary>
    [HttpGet("orders-summary")]
    public async Task<ActionResult<OrdersSummaryDto>> GetOrdersSummary([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var fromDate = from.HasValue ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc) : DateTime.UtcNow.AddDays(-30);
        var toDate = to.HasValue ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc) : DateTime.UtcNow;

        var result = await _reportService.GetOrdersSummaryAsync(fromDate, toDate);

        return Ok(result);
    }

    /// <summary>
    /// Get shipment bookings grouped by hour of day (0-23) in SAST (UTC+2)
    /// GET /api/reports/bookings-by-hour?from=2025-01-01&to=2025-12-31
    /// </summary>
    [HttpGet("bookings-by-hour")]
    public async Task<ActionResult<BookingsByHourDto>> GetBookingsByHour([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var fromDate = from.HasValue ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc) : DateTime.UtcNow.AddDays(-30);
        var toDate = to.HasValue ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc) : DateTime.UtcNow;

        var result = await _reportService.GetBookingsByHourAsync(fromDate, toDate);

        return Ok(result);
    }

    /// <summary>
    /// Get overall success rate
    /// GET /api/reports/success-rate?from=2025-01-01&to=2025-12-31
    /// </summary>
    [HttpGet("success-rate")]
    public async Task<ActionResult<SuccessRateDto>> GetSuccessRate([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var fromDate = from.HasValue ? DateTime.SpecifyKind(from.Value, DateTimeKind.Utc) : DateTime.UtcNow.AddDays(-30);
        var toDate = to.HasValue ? DateTime.SpecifyKind(to.Value, DateTimeKind.Utc) : DateTime.UtcNow;

        var result = await _reportService.GetSuccessRateAsync(fromDate, toDate);

        return Ok(result);
    }
}