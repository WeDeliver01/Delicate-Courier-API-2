namespace DelicateCouriers.Features.Users.DTOs;

/// <summary>
/// DTO for user list view
/// </summary>
public class UserDto
{
    public int UserID { get; set; }
    public int TenantID { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime? LastLoginOn { get; set; }
    public DateTime CreatedOn { get; set; }
}