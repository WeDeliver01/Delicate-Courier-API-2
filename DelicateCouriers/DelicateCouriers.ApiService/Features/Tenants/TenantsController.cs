using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DelicateCouriers.Features.Tenants.DTOs;
using System.Security.Claims;

namespace DelicateCouriers.Features.Tenants;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class TenantsController : ControllerBase
{
    private readonly TenantService _tenantService;

    public TenantsController(TenantService tenantService)
    {
        _tenantService = tenantService;
    }

    /// <summary>
    /// Get all tenants
    /// GET /api/tenants
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<TenantDto>>> GetTenants()
    {
        var tenants = await _tenantService.GetTenantsAsync();
        return Ok(tenants);
    }

    /// <summary>
    /// Get tenant by ID
    /// GET /api/tenants/{id}
    /// </summary>
    [HttpGet("{id}")]
    public async Task<ActionResult<TenantDetailDto>> GetTenant(int id)
    {
        var tenant = await _tenantService.GetTenantByIdAsync(id);

        if (tenant == null)
        {
            return NotFound(new { message = $"Tenant {id} not found" });
        }

        return Ok(tenant);
    }

    /// <summary>
    /// Create a new tenant
    /// POST /api/tenants
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<TenantDetailDto>> CreateTenant([FromBody] CreateTenantRequest request)
    {
        var userEmail = GetUserEmailFromToken();

        if (string.IsNullOrEmpty(userEmail))
        {
            return Unauthorized("Invalid token");
        }

        // Validate required fields
        if (string.IsNullOrWhiteSpace(request.TenantName))
        {
            return BadRequest(new { message = "Tenant name is required" });
        }

        if (string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            return BadRequest(new { message = "Contact email is required" });
        }

        var tenant = await _tenantService.CreateTenantAsync(request, userEmail);

        return CreatedAtAction(nameof(GetTenant), new { id = tenant.TenantID }, tenant);
    }

    /// <summary>
    /// Update an existing tenant
    /// PUT /api/tenants/{id}
    /// </summary>
    [HttpPut("{id}")]
    public async Task<ActionResult<TenantDetailDto>> UpdateTenant(int id, [FromBody] UpdateTenantRequest request)
    {
        var userEmail = GetUserEmailFromToken();

        if (string.IsNullOrEmpty(userEmail))
        {
            return Unauthorized("Invalid token");
        }

        var tenant = await _tenantService.UpdateTenantAsync(id, request, userEmail);

        if (tenant == null)
        {
            return NotFound(new { message = $"Tenant {id} not found" });
        }

        return Ok(tenant);
    }

    /// <summary>
    /// Delete a tenant (soft delete - set IsActive to false)
    /// DELETE /api/tenants/{id}
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteTenant(int id)
    {
        var userEmail = GetUserEmailFromToken();

        if (string.IsNullOrEmpty(userEmail))
        {
            return Unauthorized("Invalid token");
        }

        var request = new UpdateTenantRequest { IsActive = false };
        var tenant = await _tenantService.UpdateTenantAsync(id, request, userEmail);

        if (tenant == null)
        {
            return NotFound(new { message = $"Tenant {id} not found" });
        }

        return NoContent();
    }

    /// <summary>
    /// Helper method to extract user email from JWT token
    /// </summary>
    private string? GetUserEmailFromToken()
    {
        return User.FindFirst(ClaimTypes.Email)?.Value;
    }
}