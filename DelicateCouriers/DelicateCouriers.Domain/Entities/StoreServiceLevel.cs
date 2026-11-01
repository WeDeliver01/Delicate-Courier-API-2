namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// One delivery option a store offers at checkout.
///
/// Three names for the same thing, deliberately, because three parties disagree about what to
/// call it: the shopper reads <see cref="CheckoutLabel"/>, WooCommerce identifies the chosen
/// method by <see cref="CheckoutCode"/>, and the Engine prices <see cref="EngineCode"/>.
/// Collapsing them would mean one of the three dictating the other two, and the merchant's own
/// words are the ones that should not have to change.
/// </summary>
public sealed class StoreServiceLevel
{
    /// <summary>
    /// What the plugin builds its WooCommerce method id from: "STD" becomes
    /// delicate_courier_platform_std. This comes back on the order, so it is how a shopper's
    /// choice is recognised afterwards.
    /// </summary>
    public string CheckoutCode { get; set; } = string.Empty;

    /// <summary>What the customer reads at checkout. The merchant's words, not ours.</summary>
    public string CheckoutLabel { get; set; } = string.Empty;

    /// <summary>The Engine service level to price and book against, e.g. "standard".</summary>
    public string EngineCode { get; set; } = string.Empty;

    /// <summary>Shown at checkout as the delivery estimate.</summary>
    public int EstimatedDeliveryDays { get; set; } = 1;
}
