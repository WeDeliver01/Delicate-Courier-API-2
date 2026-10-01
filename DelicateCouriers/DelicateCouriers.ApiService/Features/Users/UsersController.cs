using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DelicateCouriers.Features.Users.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Services;
using System.Security.Claims;

namespace DelicateCouriers.Features.Users;

/// <summary>
/// Read + update endpoints for users. User CREATION lives at
/// <c>POST /api/admin/users</c> (see <see cref="AdminUsersController"/>) so
/// it can also provision the corresponding Supabase Auth user.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly UserService _userService;
    private readonly ISystemEventLogger _systemEvents;

    public UsersController(UserService userService, ISystemEventLogger systemEvents)
    {
        _userService = userService;
        _systemEvents = systemEvents;
    }

    /// <summary>GET /api/users</summary>
    [HttpGet]
    public async Task<ActionResult<List<UserDto>>> GetUsers()
    {
        var users = await _userService.GetUsersAsync();
        return Ok(users);
    }

    /// <summary>GET /api/users/{id}</summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<UserDetailDto>> GetUser(int id)
    {
        var user = await _userService.GetUserByIdAsync(id);
        if (user == null) return NotFound(new { message = $"User {id} not found" });
        return Ok(user);
    }

    /// <summary>PUT /api/users/{id} — update profile fields, role, tenant, status.</summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<ActionResult<UserDetailDto>> UpdateUser(int id, [FromBody] UpdateUserRequest request)
    {
        var userEmail = User.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrEmpty(userEmail)) return Unauthorized("Invalid token");

        try
        {
            var user = await _userService.UpdateUserAsync(id, request, userEmail);
            if (user == null) return NotFound(new { message = $"User {id} not found" });
            return Ok(user);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    /// <summary>DELETE /api/users/{id} — soft delete: deactivate the user (and ban in Supabase).</summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,SuperAdmin")]
    public async Task<ActionResult> DeleteUser(int id)
    {
        var userEmail = User.FindFirst(ClaimTypes.Email)?.Value;
        if (string.IsNullOrEmpty(userEmail)) return Unauthorized("Invalid token");

        try
        {
            var user = await _userService.UpdateUserAsync(id, new UpdateUserRequest { IsActive = false }, userEmail);
            if (user == null) return NotFound(new { message = $"User {id} not found" });

            // Soft-delete records as user.deleted so SuperAdmin sees the
            // deactivation in the audit log alongside user.created.
            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "user.deleted",
                ActorKind = "User",
                ActorLabel = userEmail,
                TenantId = user.TenantID,
                EntityType = "User",
                EntityRef = user.Email,
                Message = $"Admin {userEmail} deactivated user {user.Email}",
                Details = new { targetUserId = user.UserID, targetEmail = user.Email, targetRole = user.Role },
            });

            return NoContent();
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }
}
