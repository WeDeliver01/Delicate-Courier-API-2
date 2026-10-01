using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace DelicateCouriers.ApiService.Features.Users;

/// <summary>
/// Admin-only user provisioning. Creates the user inside Supabase Auth
/// (so they can log in immediately with email + password) AND inserts the
/// matching local profile row in one transaction-shaped flow. If the
/// local insert fails, we delete the Supabase user to avoid orphans.
///
/// Tenant + role are stamped into the Supabase user's app_metadata so the
/// Custom Access Token Auth Hook can read them back and write them as
/// claims into every JWT Supabase issues for that user.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Roles = "Admin,SuperAdmin")]
public class AdminUsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly SupabaseAdminService _supabase;
    private readonly ILogger<AdminUsersController> _logger;
    private readonly ISystemEventLogger _systemEvents;

    public AdminUsersController(AppDbContext context, SupabaseAdminService supabase, ILogger<AdminUsersController> logger, ISystemEventLogger systemEvents)
    {
        _context = context;
        _supabase = supabase;
        _logger = logger;
        _systemEvents = systemEvents;
    }

    /// <summary>
    /// Provision a new user. SuperAdmins can target any tenant; tenant Admins
    /// can only create users inside their own tenant (SuperAdmin role is also
    /// reserved for SuperAdmin callers).
    /// </summary>
    [HttpPost]
    public async Task<ActionResult> CreateUser([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        var caller = ResolveCaller();
        if (caller is null) return Unauthorized();

        var (callerTenantId, callerRole, callerEmail) = caller.Value;

        // Tenant admins can only provision into their own tenant; only
        // SuperAdmin may grant the SuperAdmin role.
        if (!string.Equals(callerRole, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
        {
            if (req.TenantID != callerTenantId)
                return Forbid();
            if (string.Equals(req.Role, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
                return Forbid();
        }

        if (!IsValidRole(req.Role))
            return BadRequest(new { message = $"Invalid role '{req.Role}'. Must be User, Admin, or SuperAdmin." });

        var tenantExists = await _context.Tenants.IgnoreQueryFilters().AnyAsync(t => t.TenantID == req.TenantID, ct);
        if (!tenantExists)
            return BadRequest(new { message = $"Tenant {req.TenantID} does not exist." });

        var emailTaken = await _context.User.IgnoreQueryFilters().AnyAsync(u => u.Email == req.Email, ct);
        if (emailTaken)
            return Conflict(new { message = $"A user with email {req.Email} already exists." });

        // 1) Create in Supabase first — that's the system of record for auth.
        Guid supabaseUserId;
        try
        {
            supabaseUserId = await _supabase.CreateUserAsync(req.Email, req.Password, req.TenantID, req.Role, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Supabase user creation failed for {Email}", req.Email);
            return StatusCode(502, new { message = "Failed to create the user in Supabase Auth.", detail = ex.Message });
        }

        // 2) Insert the local profile row. If this fails, roll back Supabase.
        try
        {
            var newUser = new User
            {
                SupabaseUserId = supabaseUserId,
                TenantID = req.TenantID,
                Email = req.Email,
                FirstName = req.FirstName,
                LastName = req.LastName,
                Role = req.Role,
                IsActive = true,
                CreatedOn = DateTime.UtcNow,
                CreatedBy = callerEmail,
            };
            _context.User.Add(newUser);
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation("Admin {Caller} provisioned user {Email} (tenant {Tenant}, role {Role})", callerEmail, req.Email, req.TenantID, req.Role);

            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "user.created",
                ActorKind = "User",
                ActorLabel = callerEmail,
                TenantId = req.TenantID,
                EntityType = "User",
                EntityRef = req.Email,
                Message = $"Admin {callerEmail} created user {req.Email} (role {req.Role})",
                Details = new { req.TenantID, req.Email, req.Role, req.FirstName, req.LastName },
            }, ct);

            return Ok(new
            {
                userID = newUser.UserID,
                supabaseUserId = newUser.SupabaseUserId,
                tenantID = newUser.TenantID,
                email = newUser.Email,
                firstName = newUser.FirstName,
                lastName = newUser.LastName,
                role = newUser.Role,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local profile insert failed for {Email}; rolling back Supabase user", req.Email);
            try { await _supabase.DeleteUserAsync(supabaseUserId, ct); }
            catch (Exception rollbackEx) { _logger.LogError(rollbackEx, "Rollback of Supabase user {Id} also failed", supabaseUserId); }
            return StatusCode(500, new { message = "Failed to persist the local profile. The Supabase user has been rolled back." });
        }
    }

    private (int tenantId, string role, string email)? ResolveCaller()
    {
        var tenantClaim = User.FindFirst("TenantId")?.Value;
        if (!int.TryParse(tenantClaim, out var tid)) return null;
        var role = User.FindFirst(ClaimTypes.Role)?.Value ?? "User";
        var email = User.FindFirst(ClaimTypes.Email)?.Value
                    ?? User.FindFirst("email")?.Value
                    ?? "unknown";
        return (tid, role, email);
    }

    private static bool IsValidRole(string role) =>
        new[] { "User", "Admin", "SuperAdmin" }.Contains(role, StringComparer.OrdinalIgnoreCase);

    public class CreateUserRequest
    {
        [Required] public int TenantID { get; set; }
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, MinLength(8)] public string Password { get; set; } = string.Empty;
        [Required] public string FirstName { get; set; } = string.Empty;
        [Required] public string LastName { get; set; } = string.Empty;
        [Required] public string Role { get; set; } = "User";
    }
}
