using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.Features.Users.DTOs;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Features.Users;

/// <summary>
/// User CRUD service used by admin screens. Authentication is owned by
/// Supabase — this service only manages the local profile row and
/// keeps Supabase's <c>app_metadata</c> in sync for role / tenant changes.
/// </summary>
public class UserService
{
    private readonly AppDbContext _context;
    private readonly SupabaseAdminService _supabase;
    private readonly ILogger<UserService> _logger;

    public UserService(AppDbContext context, SupabaseAdminService supabase, ILogger<UserService> logger)
    {
        _context = context;
        _supabase = supabase;
        _logger = logger;
    }

    public async Task<List<UserDto>> GetUsersAsync()
    {
        var users = await _context.User
            .Include(u => u.Tenant)
            .OrderBy(u => u.Email)
            .Select(u => new UserDto
            {
                UserID = u.UserID,
                TenantID = u.TenantID,
                TenantName = u.Tenant.TenantName,
                Email = u.Email,
                FirstName = u.FirstName,
                LastName = u.LastName,
                FullName = u.FirstName + " " + u.LastName,
                Role = u.Role,
                IsActive = u.IsActive,
                LastLoginOn = u.LastLoginOn,
                CreatedOn = u.CreatedOn
            })
            .ToListAsync();

        return users;
    }

    public async Task<UserDetailDto?> GetUserByIdAsync(int userId)
    {
        var user = await _context.User.Include(u => u.Tenant).FirstOrDefaultAsync(u => u.UserID == userId);
        if (user == null) return null;

        return ToDetail(user);
    }

    /// <summary>
    /// Update editable fields on a user. If role or tenant changes, Supabase
    /// app_metadata is updated too so the next JWT issued reflects the change.
    /// Email and password are NOT mutable here — those go through Supabase
    /// flows (admin update or self-service password reset) so we never get
    /// out of sync with the auth provider.
    /// </summary>
    public async Task<UserDetailDto?> UpdateUserAsync(int userId, UpdateUserRequest request, string changedBy)
    {
        var user = await _context.User.Include(u => u.Tenant).FirstOrDefaultAsync(u => u.UserID == userId);
        if (user == null) return null;

        var roleChanged = false;
        var tenantChanged = false;

        if (!string.IsNullOrEmpty(request.FirstName)) user.FirstName = request.FirstName;
        if (!string.IsNullOrEmpty(request.LastName)) user.LastName = request.LastName;

        if (!string.IsNullOrEmpty(request.Role))
        {
            if (!IsValidRole(request.Role))
                throw new ArgumentException($"Invalid role: {request.Role}. Must be User, Admin, or SuperAdmin.");
            if (!string.Equals(user.Role, request.Role, StringComparison.Ordinal))
            {
                user.Role = request.Role;
                roleChanged = true;
            }
        }

        if (request.TenantID.HasValue && request.TenantID.Value != user.TenantID)
        {
            user.TenantID = request.TenantID.Value;
            tenantChanged = true;
        }

        if (request.IsActive.HasValue && request.IsActive.Value != user.IsActive)
        {
            user.IsActive = request.IsActive.Value;
            // Mirror to Supabase so an inactive user can't get a fresh session
            // by signing in again.
            try { await _supabase.SetUserBannedAsync(user.SupabaseUserId, banned: !request.IsActive.Value); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to toggle Supabase ban for {UserId}", user.UserID); }
        }

        user.ChangedOn = DateTime.UtcNow;
        user.ChangedBy = changedBy;

        await _context.SaveChangesAsync();

        if (roleChanged || tenantChanged)
        {
            try
            {
                await _supabase.UpdateAppMetadataAsync(user.SupabaseUserId, new Dictionary<string, object?>
                {
                    ["tenant_id"] = user.TenantID,
                    ["app_role"] = user.Role,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync app_metadata for {UserId} after role/tenant change", user.UserID);
            }
        }

        return ToDetail(user);
    }

    private static UserDetailDto ToDetail(User user) => new()
    {
        UserID = user.UserID,
        TenantID = user.TenantID,
        TenantName = user.Tenant?.TenantName ?? string.Empty,
        Email = user.Email,
        FirstName = user.FirstName,
        LastName = user.LastName,
        FullName = user.FirstName + " " + user.LastName,
        Role = user.Role,
        IsActive = user.IsActive,
        LastLoginOn = user.LastLoginOn,
        CreatedOn = user.CreatedOn,
        CreatedBy = user.CreatedBy,
        ChangedOn = user.ChangedOn,
        ChangedBy = user.ChangedBy
    };

    private static bool IsValidRole(string role) =>
        new[] { "User", "Admin", "SuperAdmin" }.Contains(role, StringComparer.OrdinalIgnoreCase);
}
