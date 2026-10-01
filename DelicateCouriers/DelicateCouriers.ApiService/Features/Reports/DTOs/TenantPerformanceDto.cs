namespace DelicateCouriers.Features.Reports.DTOs;

/// <summary>
/// Performance metrics by tenant
/// </summary>
public class TenantPerformanceDto
{
    public List<TenantPerformance> Tenants { get; set; } = new();
}

public class TenantPerformance
{
    public int TenantID { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int TotalShipments { get; set; }
    public int DeliveredShipments { get; set; }
    public int FailedShipments { get; set; }
    public decimal TotalRevenue { get; set; }
}