using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Merchants;

namespace DelicateCouriers.Integration.Bookings;

public enum BookingVerdict
{
    Book,
    Skip
}

public sealed record BookingDecision(BookingVerdict Verdict, string Reason)
{
    public bool ShouldBook => Verdict == BookingVerdict.Book;

    public static BookingDecision Book() => new(BookingVerdict.Book, "bookable");
    public static BookingDecision Skip(string reason) => new(BookingVerdict.Skip, reason);
}

/// <summary>
/// Whether an order should become a delivery at all.
///
/// A pure function over the webhook payload and the merchant's settings, kept separate from
/// anything that books so it can be read and tested as a list of rules. The rules themselves
/// are not new: each one is here because an order was once booked that should not have been,
/// and the order they run in matters as much as the rules do.
///
/// Not yet covered: the collection-shipping-method check. Some merchants sell in-person
/// collection as an ordinary flat-rate method whose only signal is the label the merchant
/// wrote, which means reading the chosen method back over the WooCommerce REST API. That is a
/// network call and belongs with the intake work, not in a pure decision — but until it exists
/// those orders will book as deliveries, which is the bug that gate was written to stop.
/// </summary>
public static class BookingRules
{
    /// <summary>
    /// Statuses that mean the order is over. Both spellings of "cancelled" are here because
    /// WooCommerce and its plugins disagree, and a missed spelling books a refunded order.
    /// </summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancelled", "canceled", "refunded", "failed", "trash"
    };

    public static BookingDecision Evaluate(Merchant merchant, PluginOrderPayload payload)
    {
        // 1. The customer is collecting in person. Nothing to deliver, and booking one anyway
        //    sends a driver to fetch a parcel the customer is about to walk in and take.
        if (string.Equals(payload.FulfillmentType, "collect", StringComparison.OrdinalIgnoreCase))
        {
            return BookingDecision.Skip("collect");
        }

        var status = payload.Status?.Trim();

        // 2. The order is over. A refunded order that books is a delivery nobody paid for.
        if (!string.IsNullOrEmpty(status) && TerminalStatuses.Contains(status))
        {
            return BookingDecision.Skip($"terminal_status:{status.ToLowerInvariant()}");
        }

        // 3. The merchant's own trigger list, when they keep one. Some stores only want a
        //    delivery once an order reaches a particular status, and the plugin fires on every
        //    transition — so this is a "not yet", not a "no". A later webhook for the same
        //    order re-runs these rules and books once it qualifies.
        if (merchant.BookingTriggerStatuses.Count > 0)
        {
            var allowed = merchant.BookingTriggerStatuses
                .Any(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                return BookingDecision.Skip($"status_not_a_trigger:{status ?? "(blank)"}");
            }
        }

        return BookingDecision.Book();
    }

    /// <summary>
    /// The reference a booking is found by, in both systems.
    ///
    /// Computed from the WooCommerce order id and never stored, so it cannot drift from the
    /// order it names. The format is load-bearing: it is what the Engine is asked for when
    /// this service needs to know whether a previous attempt already booked.
    /// </summary>
    public static string CustomerReferenceFor(long wooOrderId) => $"WC-{wooOrderId}";
}
