using Microsoft.Extensions.Caching.Memory;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// Replay protection for plugin-facing endpoints: requests carry a unix
/// timestamp + random nonce. A request is rejected when the timestamp is
/// outside the ±5 minute window or the nonce has been seen within the
/// last 10 minutes. Requests WITHOUT a nonce are accepted (older plugin
/// builds don't send one yet) — the update client added in the follow-up
/// task always sends both.
/// </summary>
public class PluginReplayGuard
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NonceRetention = TimeSpan.FromMinutes(10);

    private readonly IMemoryCache _cache;

    public PluginReplayGuard(IMemoryCache cache) => _cache = cache;

    /// <returns>null when acceptable, otherwise a machine-readable rejection reason.</returns>
    public string? Check(long? unixTimestamp, string? nonce)
    {
        if (string.IsNullOrEmpty(nonce) && unixTimestamp is null) return null; // legacy client

        if (unixTimestamp is null || string.IsNullOrEmpty(nonce))
            return "timestamp_and_nonce_required_together";

        DateTimeOffset ts;
        try
        {
            ts = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Absurd/malformed timestamp — reject cleanly, never 500.
            return "timestamp_out_of_window";
        }
        var skew = DateTimeOffset.UtcNow - ts;
        if (skew > TimestampTolerance || skew < -TimestampTolerance)
            return "timestamp_out_of_window";

        var key = $"plugin-nonce:{nonce}";
        if (_cache.TryGetValue(key, out _))
            return "nonce_replayed";

        _cache.Set(key, true, NonceRetention);
        return null;
    }
}
