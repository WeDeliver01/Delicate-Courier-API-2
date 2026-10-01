using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Infrastructure.Geocoding;

/// <summary>
/// Google Geocoding API client. Requires GOOGLE_MAPS_API_KEY env var. Only
/// constructed by DI when that key is set. If Google returns REQUEST_DENIED
/// (key not authorised, billing disabled, application restriction blocks the
/// server, etc.) we log loudly and return null so the caller can fall back.
/// </summary>
public class GoogleGeocoder : IGeocoder
{
    public string Name => "google";

    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleGeocoder> _logger;
    private readonly string _apiKey;

    public GoogleGeocoder(HttpClient httpClient, ILogger<GoogleGeocoder> logger, string apiKey)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = apiKey;
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri("https://maps.googleapis.com/");
        }
    }

    public async Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
    {
        var parts = new[] { query.Street, query.Suburb, query.City, query.Province, query.PostalCode, query.Country }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        var address = string.Join(", ", parts);
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var url = $"maps/api/geocode/json?address={Uri.EscapeDataString(address)}";
        if (!string.IsNullOrWhiteSpace(query.Country))
        {
            url += $"&components=country:{Uri.EscapeDataString(query.Country)}";
        }
        url += $"&key={Uri.EscapeDataString(_apiKey)}";

        try
        {
            using var resp = await _httpClient.GetAsync(url, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Google Geocoding HTTP {Status} for '{Address}'", (int)resp.StatusCode, address);
                return null;
            }

            var payload = await resp.Content.ReadFromJsonAsync<GoogleGeocodeResponse>(cancellationToken: cancellationToken);
            if (payload == null) return null;

            if (!string.Equals(payload.Status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                // ZERO_RESULTS is a normal miss — log at info. Everything else
                // is a real problem the operator should see.
                if (string.Equals(payload.Status, "ZERO_RESULTS", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Google Geocoding ZERO_RESULTS for '{Address}'", address);
                }
                else
                {
                    _logger.LogWarning("Google Geocoding status={Status} error={Error} for '{Address}'",
                        payload.Status, payload.ErrorMessage, address);
                }
                return null;
            }

            var first = payload.Results?.FirstOrDefault();
            var loc = first?.Geometry?.Location;
            if (loc == null) return null;

            _logger.LogInformation("Google resolved '{Address}' to {Lat},{Lng}", address, loc.Lat, loc.Lng);
            return new GeocodeResult(loc.Lat, loc.Lng, Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Geocoding call failed for '{Address}'", address);
            return null;
        }
    }

    private class GoogleGeocodeResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; }
        [JsonPropertyName("results")] public List<GoogleGeocodeResult>? Results { get; set; }
    }

    private class GoogleGeocodeResult
    {
        [JsonPropertyName("geometry")] public GoogleGeometry? Geometry { get; set; }
    }

    private class GoogleGeometry
    {
        [JsonPropertyName("location")] public GoogleLocation? Location { get; set; }
    }

    private class GoogleLocation
    {
        [JsonPropertyName("lat")] public double Lat { get; set; }
        [JsonPropertyName("lng")] public double Lng { get; set; }
    }
}
