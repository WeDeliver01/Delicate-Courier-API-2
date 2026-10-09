using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DelicateCouriers.Integration.Geo;

public sealed record ResolvedPlace(
    string Formatted,
    double Latitude,
    double Longitude,
    string? Suburb,
    string? City,
    string? PostalCode);

public interface IEngineGeocoder
{
    /// <summary>Best match for a free-text address, or null when nothing resolves.</summary>
    Task<ResolvedPlace?> ResolveAsync(string query, CancellationToken cancellationToken);
}

/// <summary>
/// Geocoding, delegated to the Engine.
///
/// The Engine already runs a provider chain with a database cache in front of it, and it is
/// the thing that will price the result. Running a second geocoder here would mean two
/// providers, two caches and two sets of coordinates for one address — and the quote would be
/// computed from coordinates this service never saw. One geocoder, in the place that prices.
/// </summary>
public sealed class EngineGeocoder : IEngineGeocoder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<EngineGeocoder> _logger;

    public EngineGeocoder(HttpClient http, ILogger<EngineGeocoder> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ResolvedPlace?> ResolveAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3) return null;

        var url = $"v1/public/geocode?query={Uri.EscapeDataString(query)}";
        using var response = await _http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Geocode failed with {Status} for {Query}", (int)response.StatusCode, query);
            return null;
        }

        var suggestions =
            await response.Content.ReadFromJsonAsync<List<Suggestion>>(Json, cancellationToken);
        var best = suggestions?.FirstOrDefault();
        if (best?.Location is null) return null;

        return new ResolvedPlace(
            best.Formatted,
            best.Location.Lat,
            best.Location.Lng,
            best.Suburb,
            best.City,
            best.PostalCode);
    }

    private sealed record Suggestion
    {
        [JsonPropertyName("formatted")] public string Formatted { get; init; } = string.Empty;
        [JsonPropertyName("location")] public LatLng? Location { get; init; }
        [JsonPropertyName("suburb")] public string? Suburb { get; init; }
        [JsonPropertyName("city")] public string? City { get; init; }
        [JsonPropertyName("postalCode")] public string? PostalCode { get; init; }
    }

    private sealed record LatLng
    {
        [JsonPropertyName("lat")] public double Lat { get; init; }
        [JsonPropertyName("lng")] public double Lng { get; init; }
    }
}
