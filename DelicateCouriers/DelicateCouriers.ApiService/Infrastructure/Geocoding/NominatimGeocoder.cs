using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Infrastructure.Geocoding;

/// <summary>
/// OpenStreetMap Nominatim geocoder. Free, no API key required, but rate
/// limited to ~1 request/second per IP by the public service. We serialize
/// requests with a semaphore and require a descriptive User-Agent string,
/// both mandated by https://operations.osmfoundation.org/policies/nominatim/.
///
/// To avoid empty results on imprecise addresses we try progressively coarser
/// queries: full -> street + suburb + city -> suburb + city -> city + country.
/// </summary>
public class NominatimGeocoder : IGeocoder
{
    public string Name => "nominatim";

    private readonly HttpClient _httpClient;
    private readonly ILogger<NominatimGeocoder> _logger;

    // Public Nominatim usage policy: max 1 request/second. We serialize with
    // a semaphore + sleep to stay polite even under bursty load.
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static readonly TimeSpan _minInterval = TimeSpan.FromMilliseconds(1100);

    public NominatimGeocoder(HttpClient httpClient, ILogger<NominatimGeocoder> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
        }
        // Nominatim rejects requests without a meaningful User-Agent.
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "DelicateCouriers/1.0 (+https://delicatecourier.co.za)");
        }
    }

    public async Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
    {
        // Progressively coarser candidate queries.
        var candidates = new List<string?>
        {
            Join(query.Street, query.Suburb, query.City, query.Province, query.PostalCode),
            Join(query.Street, query.Suburb, query.City, query.Country),
            Join(query.Suburb, query.City, query.Province, query.Country),
            Join(query.City, query.Province, query.Country),
            Join(query.PostalCode, query.City, query.Country)
        };

        foreach (var q in candidates)
        {
            if (string.IsNullOrWhiteSpace(q)) continue;
            var hit = await TryLookupAsync(q, query.Country, cancellationToken);
            if (hit != null)
            {
                _logger.LogInformation("Nominatim resolved '{Query}' to {Lat},{Lng}", q, hit.Latitude, hit.Longitude);
                return hit;
            }
        }

        _logger.LogWarning("Nominatim could not resolve address: {@Query}", query);
        return null;
    }

    private async Task<GeocodeResult?> TryLookupAsync(string q, string country, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sinceLast = DateTime.UtcNow - _lastRequestUtc;
            if (sinceLast < _minInterval)
            {
                await Task.Delay(_minInterval - sinceLast, cancellationToken);
            }

            var url = $"search?format=json&limit=1&addressdetails=0&q={Uri.EscapeDataString(q)}";
            if (!string.IsNullOrWhiteSpace(country))
            {
                url += $"&countrycodes={Uri.EscapeDataString(country.ToLowerInvariant())}";
            }

            using var resp = await _httpClient.GetAsync(url, cancellationToken);
            _lastRequestUtc = DateTime.UtcNow;

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Nominatim HTTP {Status} for query '{Query}'", (int)resp.StatusCode, q);
                return null;
            }

            var results = await resp.Content.ReadFromJsonAsync<NominatimResult[]>(cancellationToken: cancellationToken);
            if (results == null || results.Length == 0) return null;

            var first = results[0];
            if (!double.TryParse(first.Lat, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)) return null;
            if (!double.TryParse(first.Lon, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lng)) return null;
            return new GeocodeResult(lat, lng, Name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Join(params string?[] parts)
    {
        var clean = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim());
        var joined = string.Join(", ", clean);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private class NominatimResult
    {
        [JsonPropertyName("lat")] public string? Lat { get; set; }
        [JsonPropertyName("lon")] public string? Lon { get; set; }
    }
}
