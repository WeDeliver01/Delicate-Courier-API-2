namespace DelicateCouriers.Features.Shiplogic.DTOs;

/// <summary>
/// Raw diagnostic information captured during a Shiplogic call. Only ever
/// exposed to clients on the debug-gated public rates endpoint (header
/// X-Debug-Key must match the RATES_DEBUG_SECRET env var). Never returned
/// to unauthenticated callers without that header.
/// </summary>
public class ShiplogicDiagnostics
{
    public string Endpoint { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public int? StatusCode { get; set; }
    public string RawResponseBody { get; set; } = "";
}
