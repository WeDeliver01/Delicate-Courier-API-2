using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.Features.Tenants.DTOs;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Features.Tenants;

/// <summary>
/// Service for managing tenants
/// </summary>
public class TenantService
{
    private readonly AppDbContext _context;
    private readonly ILogger<TenantService> _logger;

    public TenantService(AppDbContext context, ILogger<TenantService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get all tenants
    /// </summary>
    public async Task<List<TenantDto>> GetTenantsAsync()
    {
        _logger.LogInformation("Fetching all tenants");

        var tenants = await _context.Tenants.OrderBy(t => t.TenantName)
                                            .Select(t => new TenantDto
                                            {
                                                TenantID = t.TenantID,
                                                TenantName = t.TenantName,
                                                ContactEmail = t.ContactEmail,
                                                ContactPhone = t.ContactPhone,
                                                IsActive = t.IsActive,
                                                CreatedOn = t.CreatedOn,
                                                HasShiplogicToken = !string.IsNullOrEmpty(t.ShiplogicBearerToken)
                                            })
                                            .ToListAsync();

        _logger.LogInformation("Found {Count} tenants", tenants.Count);

        return tenants;
    }

    /// <summary>
    /// Get tenant by ID with full details
    /// </summary>
    public async Task<TenantDetailDto?> GetTenantByIdAsync(int tenantId)
    {
        _logger.LogInformation("Fetching tenant details for TenantID: {TenantId}", tenantId);

        var tenant = await _context.Tenants.Include(t => t.Stores).FirstOrDefaultAsync(t => t.TenantID == tenantId);

        if (tenant == null)
        {
            _logger.LogWarning("Tenant {TenantId} not found", tenantId);

            return null;
        }

        var result = new TenantDetailDto
        {
            TenantID = tenant.TenantID,
            TenantName = tenant.TenantName,
            ContactEmail = tenant.ContactEmail,
            ContactPhone = tenant.ContactPhone,
            TenantAPIKey = tenant.TenantAPIKey,
            ShiplogicBearerToken = tenant.ShiplogicBearerToken,
            IsActive = tenant.IsActive,
            CreatedOn = tenant.CreatedOn,
            CreatedBy = tenant.CreatedBy,
            ChangedOn = tenant.ChangedOn,
            ChangedBy = tenant.ChangedBy,
            StoreCount = tenant.Stores.Count,
            HasShiplogicToken = !string.IsNullOrEmpty(tenant.ShiplogicBearerToken)
        };

        _logger.LogInformation("Retrieved tenant {TenantId} with {StoreCount} stores", tenantId, result.StoreCount);

        return result;
    }

    /// <summary>
    /// Create a new tenant
    /// </summary>
    public async Task<TenantDetailDto> CreateTenantAsync(CreateTenantRequest request, string createdBy)
    {
        _logger.LogInformation("Creating new tenant: {TenantName}", request.TenantName);

        // Generate unique API key
        var apiKey = GenerateApiKey();

        var tenant = new Tenant
        {
            TenantName = request.TenantName,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            TenantAPIKey = apiKey,
            ShiplogicBearerToken = request.ShiplogicBearerToken,
            IsActive = true,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = createdBy
        };

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created tenant {TenantId}: {TenantName}", tenant.TenantID, tenant.TenantName);

        return new TenantDetailDto
        {
            TenantID = tenant.TenantID,
            TenantName = tenant.TenantName,
            ContactEmail = tenant.ContactEmail,
            ContactPhone = tenant.ContactPhone,
            TenantAPIKey = tenant.TenantAPIKey,
            ShiplogicBearerToken = tenant.ShiplogicBearerToken,
            IsActive = tenant.IsActive,
            CreatedOn = tenant.CreatedOn,
            CreatedBy = tenant.CreatedBy,
            StoreCount = 0,
            HasShiplogicToken = !string.IsNullOrEmpty(tenant.ShiplogicBearerToken)
        };
    }

    /// <summary>
    /// Update an existing tenant
    /// </summary>
    public async Task<TenantDetailDto?> UpdateTenantAsync(int tenantId, UpdateTenantRequest request, string changedBy)
    {
        _logger.LogInformation("Updating tenant {TenantId}", tenantId);

        var tenant = await _context.Tenants.Include(t => t.Stores).FirstOrDefaultAsync(t => t.TenantID == tenantId);

        if (tenant == null)
        {
            _logger.LogWarning("Tenant {TenantId} not found for update", tenantId);
            return null;
        }

        // Update only provided fields
        if (!string.IsNullOrEmpty(request.TenantName))
            tenant.TenantName = request.TenantName;

        if (!string.IsNullOrEmpty(request.ContactEmail))
            tenant.ContactEmail = request.ContactEmail;

        if (!string.IsNullOrEmpty(request.ContactPhone))
            tenant.ContactPhone = request.ContactPhone;

        if (request.ShiplogicBearerToken != null)
            tenant.ShiplogicBearerToken = request.ShiplogicBearerToken;

        if (request.IsActive.HasValue)
            tenant.IsActive = request.IsActive.Value;

        tenant.ChangedOn = DateTime.UtcNow;
        tenant.ChangedBy = changedBy;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated tenant {TenantId}", tenantId);

        return new TenantDetailDto
        {
            TenantID = tenant.TenantID,
            TenantName = tenant.TenantName,
            ContactEmail = tenant.ContactEmail,
            ContactPhone = tenant.ContactPhone,
            TenantAPIKey = tenant.TenantAPIKey,
            ShiplogicBearerToken = tenant.ShiplogicBearerToken,
            IsActive = tenant.IsActive,
            CreatedOn = tenant.CreatedOn,
            CreatedBy = tenant.CreatedBy,
            ChangedOn = tenant.ChangedOn,
            ChangedBy = tenant.ChangedBy,
            StoreCount = tenant.Stores.Count,
            HasShiplogicToken = !string.IsNullOrEmpty(tenant.ShiplogicBearerToken)
        };
    }

    /// <summary>
    /// Generate a unique API key for a tenant
    /// </summary>
    private string GenerateApiKey()
    {
        return $"dk_{Guid.NewGuid():N}";
    }
}