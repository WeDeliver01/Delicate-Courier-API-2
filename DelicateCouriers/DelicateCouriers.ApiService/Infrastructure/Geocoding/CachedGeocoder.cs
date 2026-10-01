namespace DelicateCouriers.ApiService.Infrastructure.Geocoding;

/// <summary>
/// Wraps an inner IGeocoder with a database-backed read-through cache. On a
/// miss it calls the inner geocoder, then persists the result (or a negative
/// marker) so repeat checkouts for the same address don't pay the upstream
/// latency or quota again.
/// </summary>
public class CachedGeocoder : IGeocoder
{
    private readonly IGeocoder _inner;
    private readonly GeocodeCacheStore _cache;
    private readonly ILogger<CachedGeocoder> _logger;

    public string Name => $"cached:{_inner.Name}";

    public CachedGeocoder(IGeocoder inner, GeocodeCacheStore cache, ILogger<CachedGeocoder> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public async Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
    {
        var key = GeocodeCacheStore.BuildKey(query);

        try
        {
            var (found, cached) = await _cache.TryGetAsync(key, cancellationToken);
            if (found && cached != null)
            {
                _logger.LogInformation("Geocode cache hit for key '{Key}': {Lat},{Lng}", key, cached.Latitude, cached.Longitude);
                return cached;
            }
        }
        catch (Exception ex)
        {
            // Cache read failure is non-fatal — fall through to the live geocoder.
            _logger.LogWarning(ex, "Geocode cache read failed for key '{Key}'; falling through to live lookup", key);
        }

        var result = await _inner.GeocodeAsync(query, cancellationToken);

        // Only persist positive results. Negative caching is deliberately
        // disabled while the primary provider (Google) may be misconfigured —
        // we don't want a transient REQUEST_DENIED to poison every address
        // for the next operator who fixes the key.
        if (result != null)
        {
            try
            {
                await _cache.SaveAsync(key, result, _inner.Name, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Geocode cache write failed for key '{Key}'", key);
            }
        }

        return result;
    }
}
