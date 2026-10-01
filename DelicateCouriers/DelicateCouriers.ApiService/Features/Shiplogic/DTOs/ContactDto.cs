using System.Text.Json.Serialization;

namespace DelicateCouriers.Features.Shiplogic.DTOs;

/// <summary>
/// Contact information for collection or delivery
/// </summary>
public class ContactDto
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("mobile_number")]
    public string? Mobile { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonIgnore]
    public string? CompanyName { get; set; }
}