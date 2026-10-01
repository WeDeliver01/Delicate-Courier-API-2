namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a rule for mapping products to package types based on keywords.
/// Example: Product name contains "Cheesecake" → use "Medium Cheesecake Box"
/// </summary>
public class PackageMappingRule
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public int PackageMappingRuleID { get; set; }

    /// <summary>
    /// Foreign key to the package type this rule maps to
    /// </summary>
    public int PackageTypeID { get; set; }

    /// <summary>
    /// The keyword(s) to search for in the product name
    /// Example: "Cheesecake", "Cupcake", "Tier Cake"
    /// </summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>
    /// How to match the keyword
    /// Contains = product name contains keyword (default)
    /// StartsWith = product name starts with keyword
    /// Exact = product name exactly matches keyword
    /// </summary>
    public string MatchType { get; set; } = "Contains";

    /// <summary>
    /// Priority for rule evaluation (lower = higher priority)
    /// When multiple rules match, the one with lowest priority wins
    /// Example: "Chocolate Cheesecake" matches both "Chocolate" and "Cheesecake" rules
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Whether this rule is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    // Audit fields
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "System";
    public DateTime? ChangedOn { get; set; }
    public string? ChangedBy { get; set; }

    // Navigation property
    public PackageType PackageType { get; set; } = null!;
}