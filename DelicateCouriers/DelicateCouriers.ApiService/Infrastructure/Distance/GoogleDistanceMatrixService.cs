using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Infrastructure.Distance;

/// <summary>
/// Google Distance Matrix client (mode=driving). Same API family the
/// WooCommerce plugin's special-trip quote uses, so both platforms price
/// identical addresses identically. Requires GOOGLE_MAPS_API_KEY; only
/// registered in DI when the key is present.
///
/// Never throws to callers: any provider failure logs and returns null.
/// </summary>
public class GoogleDistanceMatrixService : IDrivingDistanceService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleDistanceMatrixService> _logger;
    private readonly string _apiKey;

    public GoogleDistanceMatrixService(HttpClient httpClient, ILogger<GoogleDistanceMatrixService> logger, string apiKey)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = apiKey;
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri("https://maps.googleapis.com/");
        }
    }

    public async Task<decimal?> GetDrivingDistanceKmAsync(
        double originLat, double originLng,
        double destLat, double destLng,
        CancellationToken cancellationToken)
    {
        var url = "maps/api/distancematrix/json" +
                  $"?origins={originLat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{originLng.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  $"&destinations={destLat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{destLng.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                  "&mode=driving" +
                  $"&key={Uri.EscapeDataString(_apiKey)}";

        try
        {
            using var resp = await _httpClient.GetAsync(url, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Distance Matrix HTTP {Status}", (int)resp.StatusCode);
                return null;
            }

            var payload = await System.Net.Http.Json.HttpContentJsonExtensions
                .ReadFromJsonAsync<DistanceMatrixResponse>(resp.Content, cancellationToken: cancellationToken);

            var element = payload?.Rows?.FirstOrDefault()?.Elements?.FirstOrDefault();
            if (payload?.Status != "OK" || element?.Status != "OK" || element.Distance?.Meters is not long meters)
            {
                _logger.LogWarning(
                    "Distance Matrix returned no route. TopStatus={TopStatus} ElementStatus={ElementStatus}",
                    payload?.Status, element?.Status);
                return null;
            }

            return Math.Round(meters / 1000m, 1);
        }
        catch (OperationCanceledException)
        {
            throw; // let deadline handling upstream decide
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Distance Matrix call failed");
            return null;
        }
    }

    private sealed class DistanceMatrixResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("rows")] public List<Row>? Rows { get; set; }
    }

    private sealed class Row
    {
        [JsonPropertyName("elements")] public List<Element>? Elements { get; set; }
    }

    private sealed class Element
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("distance")] public DistanceValue? Distance { get; set; }
    }

    private sealed class DistanceValue
    {
        [JsonPropertyName("value")] public long? Meters { get; set; }
    }
}
