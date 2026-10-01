namespace DelicateCouriers.Features.Users.DTOs;

/// <summary>
/// Legacy DTO kept for any caller that still references it. New admin user
/// creation goes through <c>AdminUsersController.CreateUserRequest</c>,
/// which adds Supabase provisioning. This type is unused by the service
/// layer and will be removed in a follow-up cleanup.
/// </summary>
public class CreateUserRequest
{
    public int TenantID { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
}
