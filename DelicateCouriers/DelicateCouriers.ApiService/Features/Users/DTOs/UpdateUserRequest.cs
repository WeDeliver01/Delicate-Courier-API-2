namespace DelicateCouriers.Features.Users.DTOs;

/// <summary>
/// Request DTO for updating an existing user. Email + password are
/// intentionally NOT here — those mutations live in Supabase. Use the
/// admin reset-password flow on the Supabase dashboard, or the
/// self-service password reset on the login page.
/// </summary>
public class UpdateUserRequest
{
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Role { get; set; }
    public int? TenantID { get; set; }
    public bool? IsActive { get; set; }
}
