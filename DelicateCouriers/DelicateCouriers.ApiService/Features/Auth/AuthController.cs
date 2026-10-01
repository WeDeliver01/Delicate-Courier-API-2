using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace DelicateCouriers.ApiService.Features.Auth;

/// <summary>
/// Authentication endpoints for the Supabase-backed login flow. Login,
/// register, password reset, OAuth — all handled by Supabase directly from
/// the frontend. The backend only validates the Supabase-issued JWT and
/// exposes a profile lookup for the currently authenticated user.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<AuthController> _logger;
    private readonly ISystemEventLogger _systemEvents;

    public AuthController(AppDbContext context, ILogger<AuthController> logger, ISystemEventLogger systemEvents)
    {
        _context = context;
        _logger = logger;
        _systemEvents = systemEvents;
    }

    /// <summary>
    /// Returns the local profile row for the Supabase user identified by the
    /// bearer token's <c>sub</c> claim. The first call after a fresh
    /// admin-provisioned account also stamps <see cref="User.LastLoginOn"/>.
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult> GetCurrentUser()
    {
        var supabaseUserIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                  ?? User.FindFirst("sub")?.Value;

        if (!Guid.TryParse(supabaseUserIdClaim, out var supabaseUserId))
        {
            return Unauthorized(new { message = "Token is missing a Supabase user id." });
        }

        // Supabase token has been validated by the JwtBearer middleware, but
        // there's a window where a brand-new user exists in Supabase and not
        // yet in our local table (e.g. the admin-provisioning flow inserts
        // the local row immediately after the Supabase user is created — a
        // failure between those two steps would leave the user orphaned).
        // We still 404 in that case — the frontend treats it as "your
        // account isn't fully set up yet, contact an admin".
        var user = await _context.User
            .IgnoreQueryFilters()  // unauth /me-style lookup; SupabaseUserId is the identity
            .Include(u => u.Tenant)
            .FirstOrDefaultAsync(u => u.SupabaseUserId == supabaseUserId);

        if (user == null)
        {
            _logger.LogWarning("No local profile for Supabase user {SupabaseUserId}", supabaseUserId);
            return NotFound(new { message = "No local profile exists for this Supabase user. Ask an admin to provision your account." });
        }

        if (!user.IsActive)
        {
            return StatusCode(403, new { message = "Account is inactive." });
        }

        if (!user.Tenant.IsActive)
        {
            return StatusCode(403, new { message = "Tenant account is inactive." });
        }

        // Best-effort last-login stamp; failure here must not block /me.
        // We treat this endpoint as the post-Supabase-login "first touch" — it
        // gets called by the frontend AuthProvider right after a successful
        // sign-in, so emitting an auth.login event here gives SuperAdmin a
        // sign-in trail in the SystemEvent log without needing a Supabase hook.
        var previousLogin = user.LastLoginOn;
        try
        {
            user.LastLoginOn = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastLoginOn for user {UserId}", user.UserID);
        }

        // Only emit on a fresh session — /auth/me is called on every page
        // load, but the LastLoginOn only changes when the frontend session
        // actually transitioned. Use a 60s debounce window to suppress the
        // refresh-storm.
        if (previousLogin == null || (DateTime.UtcNow - previousLogin.Value).TotalSeconds > 60)
        {
            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "auth.login",
                ActorKind = "User",
                ActorUserId = user.UserID,
                ActorLabel = user.Email,
                TenantId = user.TenantID,
                EntityType = "User",
                EntityRef = user.Email,
                Message = $"{user.Email} signed in",
                Details = new { role = user.Role, previousLoginOn = previousLogin },
            });
        }

        return Ok(new
        {
            userID = user.UserID,
            supabaseUserId = user.SupabaseUserId,
            tenantID = user.TenantID,
            tenantName = user.Tenant?.TenantName ?? "Unknown",
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            role = user.Role,
            createdOn = user.CreatedOn,
            lastLoginOn = user.LastLoginOn,
        });
    }

    /// <summary>Liveness probe for the auth feature.</summary>
    [HttpGet("health")]
    public IActionResult Health() => Ok(new { status = "auth ok", timestamp = DateTime.UtcNow });
}
