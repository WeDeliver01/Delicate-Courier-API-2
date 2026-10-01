using DelicateCouriers.Domain.Entities;

namespace DelicateCouriers.ApiService.Features.WooCommerce;

/// <summary>
/// Outcome of verifying an order's real shipping method against the
/// store's WooCommerce REST API.
/// </summary>
public enum FulfillmentVerificationOutcome
{
    /// <summary>Shipping lines fetched; none look like collection — safe to book.</summary>
    ConfirmedDelivery,

    /// <summary>At least one shipping line's method id/title matches a collection keyword — must NOT book.</summary>
    Collection,

    /// <summary>Store has no usable Woo REST credentials — cannot verify. Caller keeps legacy behaviour.</summary>
    Unverifiable,

    /// <summary>Woo REST lookup failed (timeout, bad credentials, 404, deserialisation).
    /// Caller must FAIL SAFE: do not book, flag for review.</summary>
    LookupFailed,
}

public sealed record FulfillmentVerificationResult(
    FulfillmentVerificationOutcome Outcome,
    string? ShippingMethodId,
    string? ShippingMethodTitle);

public interface IWooFulfillmentVerifier
{
    /// <summary>
    /// Fetch the order's `shipping_lines` from the store's WooCommerce REST
    /// API and decide whether the customer actually chose a collection /
    /// pickup method. Never throws.
    /// </summary>
    Task<FulfillmentVerificationResult> VerifyAsync(Store store, string wooOrderId);

    /// <summary>
    /// Keyword check shared by intake and the pre-booking guard: does the
    /// given shipping method id/title look like in-person collection?
    /// </summary>
    bool IsCollectionMethod(string? methodId, string? methodTitle);
}

/// <summary>
/// Backend-only gating of collection-point orders.
///
/// Some stores (e.g. Baked By Nataleen, store #7) sell paid collection
/// points at checkout built as WooCommerce `flat_rate` methods whose ONLY
/// collection signal is the merchant-written label ("Collect from
/// 1 Clifford road, Irene… R95.00"). The plugin's fulfillment heuristic
/// only inspects the method ID, so those orders arrive tagged `delivery`
/// and — without this check — the platform books a real Shiplogic
/// shipment for an order the customer is picking up in person.
///
/// The plugin cannot be updated on such stores, so the platform verifies
/// the chosen shipping method itself via the store's Woo REST credentials.
/// Applied platform-wide: a keyword match on "collect"/"pickup" in a
/// chosen shipping method is a safe universal signal.
/// </summary>
public class WooFulfillmentVerifier : IWooFulfillmentVerifier
{
    // Case-insensitive substrings that mark a shipping method as in-person
    // collection. Covers standard `local_pickup`, "Pickup"/"Pick up"/"Pick-up"
    // labels and "Collect from …" flat-rate labels.
    private static readonly string[] CollectionKeywords = { "collect", "pickup", "pick up", "pick-up" };

    private readonly IWooCommerceService _wooCommerceService;
    private readonly ILogger<WooFulfillmentVerifier> _logger;

    public WooFulfillmentVerifier(IWooCommerceService wooCommerceService, ILogger<WooFulfillmentVerifier> logger)
    {
        _wooCommerceService = wooCommerceService;
        _logger = logger;
    }

    public bool IsCollectionMethod(string? methodId, string? methodTitle)
    {
        return ContainsCollectionKeyword(methodId) || ContainsCollectionKeyword(methodTitle);
    }

    private static bool ContainsCollectionKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var keyword in CollectionKeywords)
        {
            if (value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task<FulfillmentVerificationResult> VerifyAsync(Store store, string wooOrderId)
    {
        // Not a REST-reachable WooCommerce store — cannot verify. The caller
        // keeps pre-existing behaviour so stores that never configured Woo
        // REST credentials are unaffected by this gate.
        if (store == null
            || !string.Equals(store.Platform, "woocommerce", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(store.WooCommerceURL)
            || string.IsNullOrWhiteSpace(store.WooConsumerKey)
            || string.IsNullOrWhiteSpace(store.WooConsumerSecret))
        {
            return new FulfillmentVerificationResult(FulfillmentVerificationOutcome.Unverifiable, null, null);
        }

        if (!int.TryParse(wooOrderId, out var parsedWooOrderId) || parsedWooOrderId <= 0)
        {
            return new FulfillmentVerificationResult(FulfillmentVerificationOutcome.Unverifiable, null, null);
        }

        try
        {
            // GetOrderAsync returns null on ANY failure (HTTP error, timeout,
            // bad credentials, deserialisation). We deliberately treat null as
            // LookupFailed so the caller fails safe (no booking + review flag)
            // instead of guessing.
            var wooOrder = await _wooCommerceService.GetOrderAsync(store.StoreID, parsedWooOrderId);
            if (wooOrder == null)
            {
                _logger.LogError(
                    "Fulfillment verification LOOKUP FAILED for Woo order {WooOrderId} on Store {StoreId} ({StoreName}) — " +
                    "Woo REST returned no order. Failing safe: shipment booking will be withheld pending manual review.",
                    wooOrderId, store.StoreID, store.StoreName);
                return new FulfillmentVerificationResult(FulfillmentVerificationOutcome.LookupFailed, null, null);
            }

            var lines = wooOrder.ShippingLines;
            var methodIds = lines == null ? null : string.Join(", ", lines.Select(l => l.MethodId).Where(v => !string.IsNullOrWhiteSpace(v)));
            var methodTitles = lines == null ? null : string.Join(", ", lines.Select(l => l.MethodTitle).Where(v => !string.IsNullOrWhiteSpace(v)));
            methodIds = string.IsNullOrWhiteSpace(methodIds) ? null : methodIds;
            methodTitles = string.IsNullOrWhiteSpace(methodTitles) ? null : methodTitles;

            if (lines != null && lines.Any(l => IsCollectionMethod(l.MethodId, l.MethodTitle)))
            {
                _logger.LogInformation(
                    "Fulfillment verification: Woo order {WooOrderId} on Store {StoreId} chose a COLLECTION shipping method " +
                    "(method_id(s): '{MethodIds}', title(s): '{MethodTitles}') — order will be gated from shipment booking.",
                    wooOrderId, store.StoreID, methodIds ?? "(none)", methodTitles ?? "(none)");
                return new FulfillmentVerificationResult(FulfillmentVerificationOutcome.Collection, methodIds, methodTitles);
            }

            _logger.LogInformation(
                "Fulfillment verification: Woo order {WooOrderId} on Store {StoreId} confirmed as delivery " +
                "(method_id(s): '{MethodIds}', title(s): '{MethodTitles}').",
                wooOrderId, store.StoreID, methodIds ?? "(none)", methodTitles ?? "(none)");
            return new FulfillmentVerificationResult(FulfillmentVerificationOutcome.ConfirmedDelivery, methodIds, methodTitles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Fulfillment verification threw for Woo order {WooOrderId} on Store {StoreId}. Failing safe: no booking.",
                wooOrderId, store.StoreID);
            return new FulfillmentVerificationResult(FulfillmentVerificationOutcome.LookupFailed, null, null);
        }
    }
}
