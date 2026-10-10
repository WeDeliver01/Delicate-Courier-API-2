namespace DelicateCouriers.Integration.Status;

/// <summary>
/// Translating the Engine's shipment lifecycle into the words merchants already see.
///
/// This is the single translation point, and it exists because the old strings leaked. The
/// plugin writes the courier status into order meta, merchants built automations on the values
/// they found there, and no endpoint test would notice if they changed — the contract is in
/// somebody's Zapier rule, not in a schema.
///
/// So the Engine's vocabulary is mapped onto the vocabulary that is already in the field,
/// rather than the other way round. New Engine statuses with no established equivalent fall
/// back to something harmless rather than inventing a word a merchant's automation will not
/// recognise.
///
/// The set of values production actually carries has not been confirmed against the live
/// database yet. These are the documented ones; the webhook wrote whatever arrived straight
/// through, so the real set may be wider.
/// </summary>
public static class ShipmentStatusMap
{
    private static readonly Dictionary<string, string> MerchantVisible =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["booked"] = "submitted",
            ["assigned"] = "collection-assigned",
            ["collected"] = "collected",
            ["in_transit"] = "in-transit",
            ["out_for_delivery"] = "out-for-delivery",
            ["delivered"] = "delivered",
            ["failed"] = "delivery-failed",
            ["cancelled"] = "cancelled",
            ["on_hold"] = "on-hold",
            ["returned_to_sender"] = "returned",
        };

    /// <summary>
    /// The merchant-visible status string, or null when the Engine has moved somewhere we have
    /// no established word for.
    ///
    /// Null means "say nothing" rather than "say something new". A status nobody recognises is
    /// worse than silence: an automation keyed on the old values will not match it, and the
    /// merchant sees an order stuck in a state their shop has never heard of.
    /// </summary>
    public static string? ToMerchantVisible(string engineStatus) =>
        MerchantVisible.TryGetValue(engineStatus, out var mapped) ? mapped : null;

    /// <summary>
    /// The WooCommerce order status this should move the order to, if any.
    ///
    /// Only delivery completes an order. Everything else is progress the customer can watch
    /// but which must not touch the order's own state — moving an order to "completed" early
    /// triggers the merchant's fulfilment emails and their accounting.
    /// </summary>
    public static string? ToWooOrderStatus(string engineStatus) =>
        string.Equals(engineStatus, "delivered", StringComparison.OrdinalIgnoreCase)
            ? "completed"
            : null;

    /// <summary>
    /// A line for the customer-visible order notes, or null when this status does not deserve
    /// one.
    ///
    /// Deliberately few. The old platform learned to post exactly one customer-visible note
    /// per shipment, because a note per status change turns one delivery into five emails.
    /// </summary>
    public static string? CustomerNote(string engineStatus, string waybill) =>
        engineStatus.ToLowerInvariant() switch
        {
            "collected" => $"Your order has been collected by Delicate Courier. Waybill {waybill}.",
            "delivered" => $"Your order has been delivered. Waybill {waybill}.",
            _ => null
        };
}
