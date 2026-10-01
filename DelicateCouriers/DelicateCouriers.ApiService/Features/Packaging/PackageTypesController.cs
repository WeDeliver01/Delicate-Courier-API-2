using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DelicateCouriers.ApiService.Features.Packaging.DTOs;

namespace DelicateCouriers.ApiService.Features.Packaging;

[Authorize]
[ApiController]
[Route("api/stores/{storeId}/[controller]")]
public class PackageTypesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<PackageTypesController> _logger;

    public PackageTypesController(AppDbContext context, ILogger<PackageTypesController> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get all package types for a store
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPackageTypes(int storeId)
    {
        var packageTypes = await _context.PackageTypes
            .Include(p => p.MappingRules.Where(r => r.IsActive))
            .Where(p => p.StoreID == storeId)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .Select(p => new PackageTypeDto
            {
                PackageTypeId = p.PackageTypeID,
                StoreId = p.StoreID,
                Name = p.Name,
                Description = p.Description,
                LengthCm = p.LengthCm,
                WidthCm = p.WidthCm,
                HeightCm = p.HeightCm,
                DefaultWeightKg = p.DefaultWeightKg,
                MaxWeightKg = p.MaxWeightKg,
                IsDefault = p.IsDefault,
                IsActive = p.IsActive,
                SortOrder = p.SortOrder,
                MappingRules = p.MappingRules.Select(r => new MappingRuleDto
                {
                    PackageMappingRuleId = r.PackageMappingRuleID,
                    Keyword = r.Keyword,
                    MatchType = r.MatchType,
                    Priority = r.Priority,
                    IsActive = r.IsActive
                }).ToList()
            })
            .ToListAsync();

        return Ok(packageTypes);
    }

    /// <summary>
    /// Get a single package type
    /// </summary>
    [HttpGet("{packageTypeId}")]
    public async Task<IActionResult> GetPackageType(int storeId, int packageTypeId)
    {
        var packageType = await _context.PackageTypes
            .Include(p => p.MappingRules)
            .FirstOrDefaultAsync(p => p.PackageTypeID == packageTypeId && p.StoreID == storeId);

        if (packageType == null)
            return NotFound();

        return Ok(new PackageTypeDto
        {
            PackageTypeId = packageType.PackageTypeID,
            StoreId = packageType.StoreID,
            Name = packageType.Name,
            Description = packageType.Description,
            LengthCm = packageType.LengthCm,
            WidthCm = packageType.WidthCm,
            HeightCm = packageType.HeightCm,
            DefaultWeightKg = packageType.DefaultWeightKg,
            MaxWeightKg = packageType.MaxWeightKg,
            IsDefault = packageType.IsDefault,
            IsActive = packageType.IsActive,
            SortOrder = packageType.SortOrder,
            MappingRules = packageType.MappingRules.Select(r => new MappingRuleDto
            {
                PackageMappingRuleId = r.PackageMappingRuleID,
                Keyword = r.Keyword,
                MatchType = r.MatchType,
                Priority = r.Priority,
                IsActive = r.IsActive
            }).ToList()
        });
    }

    /// <summary>
    /// Create a new package type
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreatePackageType(int storeId, [FromBody] CreatePackageTypeRequest request)
    {
        // Verify store exists
        var storeExists = await _context.Stores.AnyAsync(s => s.StoreID == storeId);
        if (!storeExists)
            return NotFound("Store not found");

        // If this is set as default, unset other defaults
        if (request.IsDefault)
        {
            var existingDefaults = await _context.PackageTypes
                .Where(p => p.StoreID == storeId && p.IsDefault)
                .ToListAsync();

            foreach (var existing in existingDefaults)
                existing.IsDefault = false;
        }

        var packageType = new PackageType
        {
            StoreID = storeId,
            Name = request.Name,
            Description = request.Description,
            LengthCm = request.LengthCm,
            WidthCm = request.WidthCm,
            HeightCm = request.HeightCm,
            DefaultWeightKg = request.DefaultWeightKg,
            MaxWeightKg = request.MaxWeightKg,
            IsDefault = request.IsDefault,
            IsActive = true,
            SortOrder = request.SortOrder,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        _context.PackageTypes.Add(packageType);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created PackageType '{Name}' for StoreID: {StoreId}", request.Name, storeId);

        return CreatedAtAction(nameof(GetPackageType), new { storeId, packageTypeId = packageType.PackageTypeID },
            new { packageType.PackageTypeID, packageType.Name });
    }

    /// <summary>
    /// Update a package type
    /// </summary>
    [HttpPut("{packageTypeId}")]
    public async Task<IActionResult> UpdatePackageType(int storeId, int packageTypeId, [FromBody] UpdatePackageTypeRequest request)
    {
        var packageType = await _context.PackageTypes
            .FirstOrDefaultAsync(p => p.PackageTypeID == packageTypeId && p.StoreID == storeId);

        if (packageType == null)
            return NotFound();

        // If setting as default, unset other defaults
        if (request.IsDefault && !packageType.IsDefault)
        {
            var existingDefaults = await _context.PackageTypes
                .Where(p => p.StoreID == storeId && p.IsDefault && p.PackageTypeID != packageTypeId)
                .ToListAsync();

            foreach (var existing in existingDefaults)
                existing.IsDefault = false;
        }

        packageType.Name = request.Name;
        packageType.Description = request.Description;
        packageType.LengthCm = request.LengthCm;
        packageType.WidthCm = request.WidthCm;
        packageType.HeightCm = request.HeightCm;
        packageType.DefaultWeightKg = request.DefaultWeightKg;
        packageType.MaxWeightKg = request.MaxWeightKg;
        packageType.IsDefault = request.IsDefault;
        packageType.IsActive = request.IsActive;
        packageType.SortOrder = request.SortOrder;
        packageType.ChangedOn = DateTime.UtcNow;
        packageType.ChangedBy = User.Identity?.Name ?? "System";

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated PackageType {PackageTypeId} for StoreID: {StoreId}", packageTypeId, storeId);

        return Ok(new { message = "Package type updated" });
    }

    /// <summary>
    /// Delete a package type
    /// </summary>
    [HttpDelete("{packageTypeId}")]
    public async Task<IActionResult> DeletePackageType(int storeId, int packageTypeId)
    {
        var packageType = await _context.PackageTypes
            .FirstOrDefaultAsync(p => p.PackageTypeID == packageTypeId && p.StoreID == storeId);

        if (packageType == null)
            return NotFound();

        _context.PackageTypes.Remove(packageType);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Deleted PackageType {PackageTypeId} from StoreID: {StoreId}", packageTypeId, storeId);

        return Ok(new { message = "Package type deleted" });
    }

    /// <summary>
    /// Seed the global default packaging catalogue into this store. Idempotent
    /// and additive — only inserts templates the store doesn't already have
    /// (matched by Name, case-insensitive).
    /// </summary>
    [HttpPost("seed-defaults")]
    public async Task<IActionResult> SeedDefaults(int storeId)
    {
        var storeExists = await _context.Stores.AnyAsync(s => s.StoreID == storeId);
        if (!storeExists) return NotFound("Store not found");

        var inserted = await DefaultPackageTypes.EnsureForStoreAsync(
            _context, storeId, createdBy: User.Identity?.Name ?? "system");

        if (inserted > 0) await _context.SaveChangesAsync();

        _logger.LogInformation("Seeded {Count} default package types into StoreID {StoreId}", inserted, storeId);
        return Ok(new { inserted });
    }

    /// <summary>
    /// Add a mapping rule to a package type
    /// </summary>
    [HttpPost("{packageTypeId}/rules")]
    public async Task<IActionResult> AddMappingRule(int storeId, int packageTypeId, [FromBody] CreateMappingRuleRequest request)
    {
        var packageType = await _context.PackageTypes
            .FirstOrDefaultAsync(p => p.PackageTypeID == packageTypeId && p.StoreID == storeId);

        if (packageType == null)
            return NotFound("Package type not found");

        var rule = new PackageMappingRule
        {
            PackageTypeID = packageTypeId,
            Keyword = request.Keyword,
            MatchType = request.MatchType ?? "Contains",
            Priority = request.Priority,
            IsActive = true,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        _context.PackageMappingRules.Add(rule);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Added mapping rule '{Keyword}' to PackageType {PackageTypeId}", request.Keyword, packageTypeId);

        return CreatedAtAction(nameof(GetPackageType), new { storeId, packageTypeId },
            new { rule.PackageMappingRuleID, rule.Keyword });
    }

    /// <summary>
    /// Delete a mapping rule
    /// </summary>
    [HttpDelete("{packageTypeId}/rules/{ruleId}")]
    public async Task<IActionResult> DeleteMappingRule(int storeId, int packageTypeId, int ruleId)
    {
        var rule = await _context.PackageMappingRules
            .Include(r => r.PackageType)
            .FirstOrDefaultAsync(r => r.PackageMappingRuleID == ruleId
                && r.PackageTypeID == packageTypeId
                && r.PackageType.StoreID == storeId);

        if (rule == null)
            return NotFound();

        _context.PackageMappingRules.Remove(rule);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Deleted mapping rule {RuleId} from PackageType {PackageTypeId}", ruleId, packageTypeId);

        return Ok(new { message = "Mapping rule deleted" });
    }
}