namespace DelicateCouriers.ApiService.Features.Packaging.DTOs;

public class PackageTypeDto
{
    public int PackageTypeId { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int LengthCm { get; set; }
    public int WidthCm { get; set; }
    public int HeightCm { get; set; }
    public decimal DefaultWeightKg { get; set; }
    public decimal? MaxWeightKg { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public List<MappingRuleDto> MappingRules { get; set; } = new();
}

public class MappingRuleDto
{
    public int PackageMappingRuleId { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public string MatchType { get; set; } = "Contains";
    public int Priority { get; set; }
    public bool IsActive { get; set; }
}

public class CreatePackageTypeRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int LengthCm { get; set; }
    public int WidthCm { get; set; }
    public int HeightCm { get; set; }
    public decimal DefaultWeightKg { get; set; }
    public decimal? MaxWeightKg { get; set; }
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
}

public class UpdatePackageTypeRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int LengthCm { get; set; }
    public int WidthCm { get; set; }
    public int HeightCm { get; set; }
    public decimal DefaultWeightKg { get; set; }
    public decimal? MaxWeightKg { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}

public class CreateMappingRuleRequest
{
    public string Keyword { get; set; } = string.Empty;
    public string? MatchType { get; set; }
    public int Priority { get; set; } = 100;
}