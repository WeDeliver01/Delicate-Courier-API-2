namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Local profile row for a Supabase Auth user. Authentication itself
/// (password, OAuth, sessions, password reset) is handled entirely by
/// Supabase. This row carries the bits the app needs that aren't natural
/// to store in Supabase: tenant membership, role, audit timestamps, and
/// any platform-specific profile data.
///
/// The <see cref="SupabaseUserId"/> column is the bridge between the JWT
/// Supabase issues (sub claim = user UUID) and our multi-tenant data.
/// </summary>
public class User
{
    public int UserID { get; set; }

    public int TenantID { get; set; }

    /// <summary>
    /// auth.users.id from Supabase. Unique, immutable. Set at creation time
    /// when an admin provisions the user via the Supabase Admin API.
    /// </summary>
    public Guid SupabaseUserId { get; set; }

    public string Email { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    /// <summary>Values: "User", "Admin", "SuperAdmin".</summary>
    public string Role { get; set; } = "User";

    public bool IsActive { get; set; } = true;

    public DateTime? LastLoginOn { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime? ChangedOn { get; set; }

    public string? ChangedBy { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
