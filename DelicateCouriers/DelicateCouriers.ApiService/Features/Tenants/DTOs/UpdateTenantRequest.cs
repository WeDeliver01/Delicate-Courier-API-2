namespace DelicateCouriers.Features.Tenants.DTOs;

/// <summary>
/// Request DTO for updating an existing tenant
/// </summary>
public class UpdateTenantRequest
{
    public string? TenantName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? ShiplogicBearerToken { get; set; }
    public bool? IsActive { get; set; }
}