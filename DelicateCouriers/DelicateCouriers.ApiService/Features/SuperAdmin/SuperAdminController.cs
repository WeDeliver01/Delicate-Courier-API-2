using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static DelicateCouriers.ApiService.Features.SuperAdmin.DTOs.SuperAdminDTO;

namespace DelicateCouriers.ApiService.Features.SuperAdmin;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "SuperAdmin")]
public class SuperAdminController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly SupabaseAdminService _supabase;
    private readonly ILogger<SuperAdminController> _logger;

    public SuperAdminController(AppDbContext context, SupabaseAdminService supabase, ILogger<SuperAdminController> logger)
    {
        _context = context;
        _supabase = supabase;
        _logger = logger;
    }

    /// <summary>System statistics for the SuperAdmin landing page.</summary>
    [HttpGet("stats")]
    public async Task<ActionResult> GetStats()
    {
        var users = await _context.User.ToListAsync();
        return Ok(new
        {
            totalUsers = users.Count,
            activeUsers = users.Count(u => u.IsActive),
            inactiveUsers = users.Count(u => !u.IsActive),
            superAdmins = users.Count(u => u.Role == "SuperAdmin"),
            admins = users.Count(u => u.Role == "Admin"),
            users = users.Count(u => u.Role == "User"),
        });
    }

    /// <summary>Change a user's role. Mirrored to Supabase app_metadata.</summary>
    [HttpPut("users/{userId}/role")]
    public async Task<ActionResult> UpdateUserRole(int userId, [FromBody] UpdateRoleRequest request)
    {
        var user = await _context.User.FindAsync(userId);
        if (user == null) return NotFound(new { message = "User not found" });

        var validRoles = new[] { "User", "Admin", "SuperAdmin" };
        if (!validRoles.Contains(request.Role)) return BadRequest(new { message = "Invalid role" });

        user.Role = request.Role;
        user.ChangedOn = DateTime.UtcNow;
        user.ChangedBy = "SuperAdmin";
        await _context.SaveChangesAsync();

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
            _logger.LogError(ex, "Failed to sync Supabase app_metadata for user {UserId}", userId);
        }

        return Ok(new { message = "Role updated successfully" });
    }

    /// <summary>Activate / deactivate a user. Banned mirror in Supabase.</summary>
    [HttpPut("users/{userId}/status")]
    public async Task<ActionResult> UpdateUserStatus(int userId, [FromBody] UpdateStatusRequest request)
    {
        var user = await _context.User.FindAsync(userId);
        if (user == null) return NotFound(new { message = "User not found" });

        user.IsActive = request.IsActive;
        user.ChangedOn = DateTime.UtcNow;
        user.ChangedBy = "SuperAdmin";
        await _context.SaveChangesAsync();

        try { await _supabase.SetUserBannedAsync(user.SupabaseUserId, banned: !request.IsActive); }
        catch (Exception ex) { _logger.LogError(ex, "Failed to toggle Supabase ban for user {UserId}", userId); }

        return Ok(new { message = $"User {(request.IsActive ? "activated" : "deactivated")} successfully" });
    }
}
