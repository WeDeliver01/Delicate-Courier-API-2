using System.Text.Json.Serialization;

namespace DelicateCouriers.Features.Shiplogic.DTOs;

/// <summary>
/// Parcel details for a shipment
/// </summary>
public class ParcelDto
{
    /// <summary>
    /// Package type name (e.g., "Medium Cheesecake Box")
    /// This shows as PACKAGE TYPE in Shiplogic
    /// </summary>
    [JsonPropertyName("packaging")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Packaging { get; set; }

    /// <summary>
    /// Parcel category/description (e.g., "Single Tier Cake", "Macarons")
    /// This shows as PARCEL CATEGORY in Shiplogic
    /// </summary>
    [JsonPropertyName("parcel_description")]
    public string? Description { get; set; }

    [JsonPropertyName("submitted_length_cm")]
    public decimal? Length { get; set; }

    [JsonPropertyName("submitted_width_cm")]
    public decimal? Width { get; set; }

    [JsonPropertyName("submitted_height_cm")]
    public decimal? Height { get; set; }

    [JsonPropertyName("submitted_weight_kg")]
    public decimal Weight { get; set; }

    [JsonIgnore]
    public int ParcelCount { get; set; } = 1;
}