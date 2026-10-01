namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a package/box type configuration for a store.
/// Each store can define their own packaging options with dimensions.
/// Example: "Medium Cheesecake Box" - 20x20x8cm, 2kg
/// </summary>
public class PackageType
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public int PackageTypeID { get; set; }

    /// <summary>
    /// Foreign key to the store this package type belongs to
    /// </summary>
    public int StoreID { get; set; }

    /// <summary>
    /// Display name for the package type
    /// Example: "Medium Cheesecake Box", "Small Cake Box", "Cookie Box of 12"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Length in centimeters
    /// </summary>
    public int LengthCm { get; set; }

    /// <summary>
    /// Width in centimeters
    /// </summary>
    public int WidthCm { get; set; }

    /// <summary>
    /// Height in centimeters
    /// </summary>
    public int HeightCm { get; set; }

    /// <summary>
    /// Default weight in kilograms (used if product weight not available)
    /// </summary>
    public decimal DefaultWeightKg { get; set; }

    /// <summary>
    /// Maximum weight this box can hold in kilograms
    /// </summary>
    public decimal? MaxWeightKg { get; set; }

    /// <summary>
    /// Whether this is the default package type for the store
    /// Used when no mapping rules match a product
    /// </summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// Whether this package type is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Display order for UI
    /// </summary>
    public int SortOrder { get; set; } = 0;

    // Audit fields
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "System";
    public DateTime? ChangedOn { get; set; }
    public string? ChangedBy { get; set; }

    // Navigation properties
    public Store Store { get; set; } = null!;
    public ICollection<PackageMappingRule> MappingRules { get; set; } = new List<PackageMappingRule>();
}