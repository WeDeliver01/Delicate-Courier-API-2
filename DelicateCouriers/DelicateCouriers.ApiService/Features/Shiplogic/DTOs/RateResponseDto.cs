using System.Text.Json.Serialization;

namespace DelicateCouriers.Features.Shiplogic.DTOs;

/// <summary>
/// Response from getting rates
/// POST /rates
/// </summary>
public class RateResponseDto
{
    /// <summary>
    /// List of available rates
    /// </summary>
    [JsonPropertyName("rates")] 
    public List<ShippingRateDto> Rates { get; set; } = new();
}