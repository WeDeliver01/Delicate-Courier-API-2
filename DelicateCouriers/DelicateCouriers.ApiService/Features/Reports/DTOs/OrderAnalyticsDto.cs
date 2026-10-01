namespace DelicateCouriers.Features.Reports.DTOs;

/// <summary>
/// Order-level business metrics for a date range.
/// </summary>
public class OrdersSummaryDto
{
    /// <summary>Count of orders received in the range.</summary>
    public int TotalOrders { get; set; }

    /// <summary>Sum of order totals (R) in the range.</summary>
    public decimal TotalOrderRevenue { get; set; }

    /// <summary>Count of shipments booked in the range.</summary>
    public int BookedShipments { get; set; }

    /// <summary>Sum of shipping cost (R) across shipments booked in the range.</summary>
    public decimal ShippingRevenue { get; set; }
}

/// <summary>
/// Shipment bookings grouped by hour of day (0-23), in South African time (SAST / UTC+2).
/// </summary>
public class BookingsByHourDto
{
    public List<BookingHourPoint> Hours { get; set; } = new();
}

public class BookingHourPoint
{
    /// <summary>Hour of day (0-23) in SAST.</summary>
    public int Hour { get; set; }

    /// <summary>Number of shipment bookings in that hour bucket.</summary>
    public int Count { get; set; }
}
