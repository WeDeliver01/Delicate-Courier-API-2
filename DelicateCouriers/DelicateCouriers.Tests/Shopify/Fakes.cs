using DelicateCouriers.ApiService.Features.Delivery;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;

namespace DelicateCouriers.Tests.Shopify;

/// <summary>
/// Records the delivery request it received and replays a canned answer.
///
/// Was a fake of IShiplogicService, which no longer exists: the engine is reached through the
/// integration service now, and this is the seam. The members a test does not exercise throw
/// rather than return a default, so a path that unexpectedly books something fails loudly
/// instead of silently passing.
/// </summary>
public sealed class FakeDeliveryService : IDeliveryService
{
    /// <summary>
    /// What <see cref="QuoteAsync"/> returns, or null for "this cannot be priced".
    ///
    /// Null by default, which is the behaviour the fake it replaced had: its canned response
    /// was an empty rate list. A great many tests are about what happens when no rate comes
    /// back — the special-trip fallback, the empty-rates contract — and a fake that quotes
    /// cheerfully by default would quietly stop all of them testing anything.
    /// </summary>
    public DeliveryQuote? Quote { get; set; }

    /// <summary>A specific failure to throw, when a test is about which failure it was.</summary>
    public Exception? QuoteThrows { get; set; }

    public bool WasCalled { get; private set; }
    public int CallCount { get; private set; }
    public DeliveryRequest? LastRequest { get; private set; }
    public List<DeliveryRequest> Requests { get; } = new();

    public Task<DeliveryQuote> QuoteAsync(
        DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        CallCount++;
        LastRequest = request;
        Requests.Add(request);
        if (QuoteThrows is not null) throw QuoteThrows;
        if (Quote is null) throw new InvalidOperationException("no rate available for this request");
        return Task.FromResult(Quote);
    }

    public Task<DeliveryBooking> BookAsync(
        DeliveryRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("this test should not be booking anything");

    public Task<DeliveryBooking?> FindByReferenceAsync(
        string accountRef, string customerReference, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<DeliveryBooking?> GetAsync(
        string accountRef, string bookingId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<byte[]?> GetLabelAsync(
        string accountRef, string shipmentId, CancellationToken cancellationToken = default)
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
