namespace DelicateCouriers.ApiService.Infrastructure.Geocoding;

/// <summary>
/// Tries each inner geocoder in turn and returns the first hit. Lets us
/// keep a free fallback (Nominatim) live alongside a primary that may be
/// transiently broken (e.g. Google when its API key is misconfigured).
/// </summary>
public class ChainGeocoder : IGeocoder
{
    private readonly IReadOnlyList<IGeocoder> _providers;
    private readonly ILogger<ChainGeocoder> _logger;

    public string Name => "chain:" + string.Join("+", _providers.Select(p => p.Name));

    public ChainGeocoder(IEnumerable<IGeocoder> providers, ILogger<ChainGeocoder> logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public async Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
    {
        foreach (var p in _providers)
        {
            var hit = await p.GeocodeAsync(query, cancellationToken);
            if (hit != null) return hit;
            _logger.LogInformation("Geocoder '{Provider}' returned no match; trying next", p.Name);
        }
        return null;
    }
}
