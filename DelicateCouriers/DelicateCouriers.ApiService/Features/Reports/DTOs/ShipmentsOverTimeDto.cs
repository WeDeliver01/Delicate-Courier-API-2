namespace DelicateCouriers.Features.Reports.DTOs;

/// <summary>
/// Shipment volume data over time
/// </summary>
public class ShipmentsOverTimeDto
{
    public List<ShipmentTimePoint> DataPoints { get; set; } = new();
}

public class ShipmentTimePoint
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
}