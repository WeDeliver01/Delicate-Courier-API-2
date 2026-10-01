namespace DelicateCouriers.Features.Reports.DTOs;

/// <summary>
/// Overall success rate metrics
/// </summary>
public class SuccessRateDto
{
    public int TotalShipments { get; set; }
    public int SuccessfulShipments { get; set; }
    public int FailedShipments { get; set; }
    public decimal SuccessRate { get; set; }
    public decimal FailureRate { get; set; }
}