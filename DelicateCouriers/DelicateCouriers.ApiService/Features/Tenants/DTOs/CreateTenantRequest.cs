namespace DelicateCouriers.Features.Tenants.DTOs;

/// <summary>
/// Request DTO for creating a new tenant
/// </summary>
public class CreateTenantRequest
{
    public string TenantName { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string? ShiplogicBearerToken { get; set; }
}