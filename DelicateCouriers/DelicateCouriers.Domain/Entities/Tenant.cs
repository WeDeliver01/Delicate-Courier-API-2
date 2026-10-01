namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a client/company using the Delicate Couriers platform.
/// Each tenant is a separate customer with isolated data.
/// </summary>
public class Tenant
{
    /// <summary>
    /// Unique identifier for the tenant
    /// </summary>
    public int TenantID { get; set; }

    /// <summary>
    /// Name of the company/organization
    /// </summary>
    public string TenantName { get; set; } = string.Empty;

    /// <summary>
    /// Primary contact email for this tenant
    /// </summary>
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>
    /// Primary contact phone number for this tenant
    /// </summary>
    public string ContactPhone { get; set; } = string.Empty;

    /// <summary>
    /// API key for authenticating this tenant's requests
    /// </summary>
    public string TenantAPIKey { get; set; } = string.Empty;

    /// <summary>
    /// Shiplogic API Bearer Token for this tenant's Shiplogic sub-account
    /// Each tenant has their own Shiplogic account managed by Delicate Couriers
    /// </summary>
    public string? ShiplogicBearerToken { get; set; }

    /// <summary>
    /// Whether this tenant account is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When this tenant was created in the system
    /// </summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who created this tenant
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// When this tenant was last modified
    /// </summary>
    public DateTime? ChangedOn { get; set; }

    /// <summary>
    /// User who last modified this tenant
    /// </summary>
    public string? ChangedBy { get; set; }

    // Navigation property - all stores owned by this tenant
    public ICollection<Store> Stores { get; set; } = new List<Store>();
    // Navigation property - all users who belong to this tenant
    public ICollection<User> Users { get; set; } = new List<User>();
}