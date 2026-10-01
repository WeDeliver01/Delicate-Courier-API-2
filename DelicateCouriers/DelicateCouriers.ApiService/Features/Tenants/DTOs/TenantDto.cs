namespace DelicateCouriers.Features.Tenants.DTOs;

/// <summary>
/// DTO for tenant list view
/// </summary>
public class TenantDto
{
    public int TenantID { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedOn { get; set; }
    public bool HasShiplogicToken { get; set; }
}