namespace DelicateCouriers.ApiService.Infrastructure.Distance;

/// <summary>
/// Computes one-way driving distance between two coordinates.
/// Returns null when the distance cannot be determined (no route, provider
/// error, provider not configured) — callers must treat null as "no quote".
/// </summary>
public interface IDrivingDistanceService
{
    Task<decimal?> GetDrivingDistanceKmAsync(
        double originLat, double originLng,
        double destLat, double destLng,
        CancellationToken cancellationToken);
}
