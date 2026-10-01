using DelicateCouriers.ApiService.Infrastructure.Distance;
using DelicateCouriers.Domain.Entities;

namespace DelicateCouriers.Features.Shipping.SpecialTrip;

/// <summary>
/// A distance-priced "Special Trip" quote: one-way driving distance from the
/// shop to the customer × the store's R/km, with a minimum fee, only within
/// the store's max distance.
/// </summary>
public sealed record SpecialTripQuote(decimal DistanceKm, decimal Amount);

public interface ISpecialTripQuoter
{
    /// <summary>
    /// Try to build a special-trip quote for the given store and coordinates.
    /// Returns null when the fallback is disabled (no cost-per-km or no
    /// merchant Google Maps key), coordinates are missing, no driving route
    /// exists, or the distance exceeds the store's cap.
    /// </summary>
    Task<SpecialTripQuote?> TryQuoteAsync(
        Store store,
        double? collectionLat, double? collectionLng,
        double? deliveryLat, double? deliveryLng,
        CancellationToken ct);
}

/// <summary>
/// Shared implementation used by BOTH the Shopify carrier callback and the
/// WooCommerce public rates endpoint, so a merchant's special-trip pricing
/// behaves identically across platforms. Distance lookups run against the
/// merchant's OWN Google Maps key (Store.GoogleMapsApiKey) so Distance
/// Matrix usage bills to them, never to the platform.
/// </summary>
public class SpecialTripQuoter : ISpecialTripQuoter
{
    private readonly IDrivingDistanceServiceFactory _distanceServiceFactory;
    private readonly ILogger<SpecialTripQuoter> _logger;

    public SpecialTripQuoter(
        IDrivingDistanceServiceFactory distanceServiceFactory,
        ILogger<SpecialTripQuoter> logger)
    {
        _distanceServiceFactory = distanceServiceFactory;
        _logger = logger;
    }

    public async Task<SpecialTripQuote?> TryQuoteAsync(
        Store store,
        double? collectionLat, double? collectionLng,
        double? deliveryLat, double? deliveryLng,
        CancellationToken ct)
    {
        if (store.SpecialTripCostPerKm is not decimal perKm || perKm <= 0) return null;

        // The merchant must supply their OWN Google key (Distance Matrix
        // usage bills to them). No key = fallback off for this store.
        if (string.IsNullOrWhiteSpace(store.GoogleMapsApiKey))
        {
            _logger.LogInformation(
                "Special trip for store {StoreId}: enabled but no Google Maps API key configured — not offered",
                store.StoreID);
            return null;
        }

        if (collectionLat is not double oLat || collectionLng is not double oLng ||
            deliveryLat is not double dLat || deliveryLng is not double dLng)
        {
            _logger.LogInformation(
                "Special trip for store {StoreId}: skipped — missing coordinates (collection or delivery)",
                store.StoreID);
            return null;
        }

        var distanceService = _distanceServiceFactory.Create(store.GoogleMapsApiKey);
        var distanceKm = await distanceService.GetDrivingDistanceKmAsync(oLat, oLng, dLat, dLng, ct);
        if (distanceKm is not decimal km || km <= 0)
        {
            _logger.LogWarning("Special trip for store {StoreId}: no driving route found", store.StoreID);
            return null;
        }

        // Round to 1 decimal BEFORE pricing. The customer-facing label shows
        // the distance to one decimal ("Special Trip Request (12.3 km)") and
        // WooCommerce order intake re-derives the quoted amount by parsing
        // that label — pricing from the same rounded value guarantees the
        // checkout price and the booked amount always agree.
        km = Math.Round(km, 1);
        if (km <= 0) km = 0.1m;

        if (store.SpecialTripMaxKm is decimal maxKm && km > maxKm)
        {
            _logger.LogInformation(
                "Special trip for store {StoreId}: {Km} km exceeds cap of {MaxKm} km — not offered",
                store.StoreID, km, maxKm);
            return null;
        }

        var amount = ComputeAmount(store, km);
        if (amount is not decimal quoted) return null;

        _logger.LogInformation(
            "Special trip for store {StoreId}: offering R{Amount} for {Km} km (R{PerKm}/km, min R{MinFee})",
            store.StoreID, quoted, km, perKm, store.SpecialTripMinFee ?? 0m);

        return new SpecialTripQuote(km, quoted);
    }

    /// <summary>
    /// Pure pricing rule: km × R/km, floored at the store's minimum fee.
    /// Returns null when the store has no (positive) cost-per-km configured.
    /// Exposed so order ingestion can re-derive the quoted amount from a
    /// distance parsed off the chosen shipping method's label.
    /// </summary>
    public static decimal? ComputeAmount(Store store, decimal distanceKm)
    {
        if (store.SpecialTripCostPerKm is not decimal perKm || perKm <= 0) return null;
        var amount = Math.Round(distanceKm * perKm, 2);
        if (store.SpecialTripMinFee is decimal minFee && amount < minFee)
        {
            amount = minFee;
        }
        return amount;
    }
}
