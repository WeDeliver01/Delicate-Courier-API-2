namespace DelicateCouriers.Features.Shipping.GetRates.DTOs;

/// <summary>
/// Response containing available shipping rates
/// Returned to WooCommerce for display at checkout
/// </summary>
public class GetRatesResponseDto
{
    public bool Success { get; set; }
    public List<RateOptionDto> Rates { get; set; } = new();
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Populated ONLY when the caller passes a valid debug key (header
    /// X-Debug-Key matching the RATES_DEBUG_SECRET env var). Contains the
    /// raw Shiplogic request/response and any exception detail. Null on all
    /// normal (unauthenticated) public calls.
    /// </summary>
    public DebugInfoDto? Debug { get; set; }
}

public class DebugInfoDto
{
    public string? ShiplogicEndpoint { get; set; }
    public string? ShiplogicRequestJson { get; set; }
    public int? ShiplogicStatusCode { get; set; }
    public string? ShiplogicRawResponseBody { get; set; }
    public string? ExceptionChain { get; set; }
}