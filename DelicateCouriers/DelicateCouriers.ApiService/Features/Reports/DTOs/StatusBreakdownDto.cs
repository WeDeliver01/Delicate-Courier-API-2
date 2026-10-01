namespace DelicateCouriers.Features.Reports.DTOs;

/// <summary>
/// Breakdown of shipments by status
/// </summary>
public class StatusBreakdownDto
{
    public List<StatusCount> Statuses { get; set; } = new();
}

public class StatusCount
{
    public string Status { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Percentage { get; set; }
}