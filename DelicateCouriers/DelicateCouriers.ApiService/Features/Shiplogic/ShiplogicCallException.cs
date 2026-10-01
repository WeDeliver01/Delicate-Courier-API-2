using DelicateCouriers.Features.Shiplogic.DTOs;

namespace DelicateCouriers.Features.Shiplogic;

/// <summary>
/// Thrown when a Shiplogic HTTP call fails. Carries the raw diagnostics so
/// the debug-gated path on the public rates endpoint can surface them.
/// </summary>
public class ShiplogicCallException : InvalidOperationException
{
    public ShiplogicDiagnostics Diagnostics { get; }

    public ShiplogicCallException(string message, Exception inner, ShiplogicDiagnostics diagnostics)
        : base(message, inner)
    {
        Diagnostics = diagnostics;
    }
}
