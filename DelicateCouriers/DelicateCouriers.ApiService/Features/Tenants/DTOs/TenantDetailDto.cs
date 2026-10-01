namespace DelicateCouriers.Features.Tenants.DTOs;

/// <summary>
/// Detailed tenant information with all fields
/// </summary>
public class TenantDetailDto
{
    public int TenantID { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string TenantAPIKey { get; set; } = string.Empty;
    public string? ShiplogicBearerToken { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ChangedOn { get; set; }
    public string? ChangedBy { get; set; }

    // Computed fields
    public int StoreCount { get; set; }
    public bool HasShiplogicToken { get; set; }
}