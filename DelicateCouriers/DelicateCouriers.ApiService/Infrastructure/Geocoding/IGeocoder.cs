namespace DelicateCouriers.ApiService.Infrastructure.Geocoding;

/// <summary>
/// Address fragments to geocode. All fields optional except Country (ISO-2).
/// Implementations should be tolerant of missing components and try
/// progressively coarser queries (street -> suburb -> city) when a precise
/// match is not found.
/// </summary>
public record GeocodeQuery(
    string? Street,
    string? Suburb,
    string? City,
    string? Province,
    string? PostalCode,
    string Country);

public record GeocodeResult(double Latitude, double Longitude, string Provider);

/// <summary>
/// Resolves a postal address to lat/lng. Returns null when the address
/// cannot be located. Implementations must never throw on a normal lookup
/// failure — only on hard errors (network failure, auth failure).
/// </summary>
public interface IGeocoder
{
    string Name { get; }
    Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken);
}
