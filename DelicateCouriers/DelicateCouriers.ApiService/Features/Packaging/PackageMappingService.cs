using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Packaging;

/// <summary>
/// Service for matching products to package types based on configured rules
/// </summary>
public class PackageMappingService
{
    private readonly AppDbContext _context;
    private readonly ILogger<PackageMappingService> _logger;

    public PackageMappingService(AppDbContext context, ILogger<PackageMappingService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Find the best matching package type for a product name
    /// </summary>
    public async Task<PackageType?> GetPackageTypeForProductAsync(int storeId, string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return await GetDefaultPackageTypeAsync(storeId);
        }

        // Get all active package types with their active rules for this store
        var packageTypes = await _context.PackageTypes.Include(p => p.MappingRules.Where(r => r.IsActive)).Where(p => p.StoreID == storeId && p.IsActive).ToListAsync();

        if (!packageTypes.Any())
        {
            _logger.LogWarning("No package types configured for StoreID: {StoreId}", storeId);

            return null;
        }

        // Find matching rules, ordered by priority (lower = higher priority)
        PackageMappingRule? bestMatch = null;

        foreach (var packageType in packageTypes)
        {
            foreach (var rule in packageType.MappingRules)
            {
                if (IsMatch(productName, rule.Keyword, rule.MatchType))
                {
                    if (bestMatch == null || rule.Priority < bestMatch.Priority)
                    {
                        bestMatch = rule;
                        _logger.LogDebug("Found match: Product '{ProductName}' matches rule '{Keyword}' (Priority: {Priority}) -> Package '{PackageName}'", productName, rule.Keyword, rule.Priority, packageType.Name);
                    }
                }
            }
        }

        if (bestMatch != null)
        {
            var matchedPackage = packageTypes.First(p => p.PackageTypeID == bestMatch.PackageTypeID);

            _logger.LogInformation("Product '{ProductName}' mapped to package '{PackageName}' via keyword '{Keyword}'", productName, matchedPackage.Name, bestMatch.Keyword);

            return matchedPackage;
        }

        // No match found, return default
        _logger.LogDebug("No matching rule for product '{ProductName}', using default package", productName);

        return await GetDefaultPackageTypeAsync(storeId);
    }

    /// <summary>
    /// Get the default package type for a store
    /// </summary>
    public async Task<PackageType?> GetDefaultPackageTypeAsync(int storeId)
    {
        var defaultPackage = await _context.PackageTypes.Where(p => p.StoreID == storeId && p.IsActive && p.IsDefault).FirstOrDefaultAsync();

        if (defaultPackage == null)
        {
            // Fallback: get any active package type
            defaultPackage = await _context.PackageTypes.Where(p => p.StoreID == storeId && p.IsActive).OrderBy(p => p.SortOrder).FirstOrDefaultAsync();
        }

        return defaultPackage;
    }

    /// <summary>
    /// Check if a product name matches a keyword based on match type
    /// </summary>
    private static bool IsMatch(string productName, string keyword, string matchType)
    {
        var product = productName.ToLowerInvariant();
        var key = keyword.ToLowerInvariant();

        return matchType.ToLowerInvariant() switch
        {
            "startswith" => product.StartsWith(key),
            "exact" => product.Equals(key),
            "contains" or _ => product.Contains(key)
        };
    }
}