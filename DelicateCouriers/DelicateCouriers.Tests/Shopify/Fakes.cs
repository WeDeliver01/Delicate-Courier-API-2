using DelicateCouriers.ApiService.Infrastructure.Geocoding;
using DelicateCouriers.Features.Shiplogic;
using DelicateCouriers.Features.Shiplogic.DTOs;

namespace DelicateCouriers.Tests.Shopify;

/// <summary>
/// Records the rate request it received and replays a canned response. The
/// carrier-service callback only depends on GetRatesWithDiagnosticsAsync, so
/// the other interface members throw to surface unexpected use.
/// </summary>
public sealed class FakeShiplogicService : IShiplogicService
{
    public RateResponseDto RateResponse { get; set; } = new();
    public bool WasCalled { get; private set; }
    public List<ParcelDto>? LastParcels { get; private set; }
    public AddressDto? LastCollection { get; private set; }
    public AddressDto? LastDelivery { get; private set; }

    public Task<(RateResponseDto Rates, ShiplogicDiagnostics Diagnostics)> GetRatesWithDiagnosticsAsync(
        string bearerToken,
        AddressDto collectionAddress,
        AddressDto deliveryAddress,
        List<ParcelDto> parcels,
        string? serviceLevelCode = null,
        CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        LastCollection = collectionAddress;
        LastDelivery = deliveryAddress;
        LastParcels = parcels;
        return Task.FromResult((RateResponse, new ShiplogicDiagnostics()));
    }

    public Task<RateResponseDto> GetRatesAsync(
        string bearerToken,
        AddressDto collectionAddress,
        AddressDto deliveryAddress,
        List<ParcelDto> parcels,
        string? serviceLevelCode = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(RateResponse);

    public Task<CreateShipmentResponse> CreateShipmentAsync(string bearerToken, CreateShipmentRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<byte[]> GetLabelAsync(string bearerToken, string consignmentId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TrackingResponse> GetTrackingUpdatesAsync(string bearerToken, List<string>? consignmentIds = null, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<bool> CancelShipmentAsync(string bearerToken, string consignmentId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<byte[]?> GetShipmentLabelAsync(string bearerToken, int shipmentId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateShipmentResponse?> FindShipmentByCustomerReferenceAsync(string bearerToken, string customerReference, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

/// <summary>
/// Geocoder stub that returns a fixed coordinate for any query. The rate
/// controller calls this for the delivery address (and for collection when the
/// store has no persisted lat/lng); tests don't need a real geocode.
/// </summary>
public sealed class FakeGeocoder : IGeocoder
{
    private readonly GeocodeResult? _result;
    public FakeGeocoder(GeocodeResult? result) => _result = result;
    public string Name => "fake";
    public Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
        => Task.FromResult(_result);
}
