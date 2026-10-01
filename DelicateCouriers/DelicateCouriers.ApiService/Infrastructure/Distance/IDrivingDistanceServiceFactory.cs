namespace DelicateCouriers.ApiService.Infrastructure.Distance;

/// <summary>
/// Creates a driving-distance client for a specific Google Maps API key.
/// Each merchant supplies their OWN key (Store.GoogleMapsApiKey) so their
/// Distance Matrix usage bills to their Google account, not the platform's.
/// </summary>
public interface IDrivingDistanceServiceFactory
{
    IDrivingDistanceService Create(string apiKey);
}

public class GoogleDistanceMatrixServiceFactory : IDrivingDistanceServiceFactory
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    public GoogleDistanceMatrixServiceFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    public IDrivingDistanceService Create(string apiKey)
    {
        return new GoogleDistanceMatrixService(
            _httpClientFactory.CreateClient(nameof(GoogleDistanceMatrixService)),
            _loggerFactory.CreateLogger<GoogleDistanceMatrixService>(),
            apiKey);
    }
}
