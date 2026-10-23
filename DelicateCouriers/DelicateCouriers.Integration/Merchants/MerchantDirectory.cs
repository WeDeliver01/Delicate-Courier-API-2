using Microsoft.Extensions.Options;

namespace DelicateCouriers.Integration.Merchants;

/// <summary>
/// Everything this service needs to know about a merchant to quote and book for them.
///
/// The old platform read this from its own `Stores` table. Where it lives for this service is
/// still an open decision — the Engine already owns the account and its wallet, while the
/// collection address and the plugin's expected service-level code are checkout concerns. Until
/// that is settled it is configuration, which keeps the decision visible instead of quietly
/// baking in whichever database happened to be nearest.
/// </summary>
/// <summary>
/// One delivery option a merchant offers at checkout.
///
/// Three identifiers, not one, because three systems name the same thing differently and
/// conflating them is how a shopper ends up buying Express and the courier being told
/// Standard. <see cref="CheckoutCode"/> is what WooCommerce shows and hands back on the
/// order; <see cref="EngineCode"/> is what the Engine prices and books; the label is the
/// merchant's own words for it.
/// </summary>
public sealed class MerchantServiceLevel
{
    /// <summary>
    /// The code the plugin builds its Woo method id from, e.g. "STD" becomes
    /// delicate_courier_platform_std. This is what comes back on the order, so it is how a
    /// shopper's choice is recognised.
    /// </summary>
    public string CheckoutCode { get; set; } = string.Empty;

    /// <summary>What the customer reads at checkout. The merchant's words, not ours.</summary>
    public string CheckoutLabel { get; set; } = string.Empty;

    /// <summary>The Engine service level to price and book against, e.g. "standard".</summary>
    public string EngineCode { get; set; } = string.Empty;

    /// <summary>Shown at checkout as the delivery estimate.</summary>
    public int EstimatedDeliveryDays { get; set; } = 1;
}

public sealed class Merchant
{
    public int StoreId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The merchant's identifier as the Engine knows it, mapped there through
    /// `account_external_refs`. Sent as `X-Account-Ref`, so no Engine uuid lives here.
    /// </summary>
    public string EngineAccountRef { get; set; } = string.Empty;

    /// <summary>
    /// The Engine's own account id, which is what its outbound deliveries name.
    ///
    /// Configured alongside the reference rather than derived from it: a status callback
    /// arrives with an account uuid and nothing else to route on, and resolving it by calling
    /// back to the Engine would mean a lookup per status change on every shipment.
    /// </summary>
    public string? EngineAccountId { get; set; }

    /// <summary>Where the courier collects. Priced as one leg of the Engine's depot loop.</summary>
    public MerchantAddress Collection { get; set; } = new();

    /// <summary>
    /// What this merchant offers at checkout, in the order the shopper sees it.
    ///
    /// A list rather than one level, because a merchant who sells next-day and same-day has
    /// two prices to show and the shopper picks. Empty means this merchant offers nothing and
    /// their checkout will show no delivery option, which is why it is validated on save
    /// rather than discovered by a customer.
    /// </summary>
    public List<MerchantServiceLevel> ServiceLevels { get; set; } = new();

    /// <summary>
    /// Engine package type to quote each parcel as. The plugin sends dimensions and weight,
    /// the Engine prices by catalogued package type, and nothing can derive one from the other
    /// — so a merchant who sells cakes says so once, here.
    /// </summary>
    public string DefaultPackageTypeId { get; set; } = string.Empty;

    /// <summary>
    /// Shared secret the plugin signs its webhooks with (HMAC-SHA256 over the raw body).
    ///
    /// Without it anyone who guessed a store id could inject orders for that merchant, so a
    /// merchant configured without one is a configuration error rather than an open door.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Statuses that may trigger a booking, when this merchant wants only some of them.
    ///
    /// Empty means the default: book on any non-terminal status. A merchant who lists statuses
    /// gets "not yet" rather than "no" for the others — the plugin fires on every transition,
    /// so a later webhook books the order once it qualifies.
    /// </summary>
    public List<string> BookingTriggerStatuses { get; set; } = new();

    /// <summary>
    /// The merchant's own WooCommerce, read over its REST API to check what the shopper
    /// actually chose at checkout. Absent credentials mean the store cannot be asked, which
    /// is treated as "nothing to verify" rather than as a failure.
    /// </summary>
    public string? WooCommerceUrl { get; set; }
    public string? WooConsumerKey { get; set; }
    public string? WooConsumerSecret { get; set; }
}

public sealed class MerchantAddress
{
    public string Formatted { get; set; } = string.Empty;
    public string? Line1 { get; set; }
    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? PostalCode { get; set; }
    public string Country { get; set; } = "ZA";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
}

public sealed class MerchantDirectoryOptions
{
    public const string SectionName = "Merchants";
    public List<Merchant> Stores { get; set; } = new();
}

public interface IMerchantDirectory
{
    Merchant? Find(int storeId);

    /// <summary>The merchant an Engine account belongs to, for routing inbound callbacks.</summary>
    Merchant? FindByAccountId(string? engineAccountId);
}

public sealed class ConfiguredMerchantDirectory : IMerchantDirectory
{
    private readonly IReadOnlyDictionary<int, Merchant> _byStoreId;
    private readonly IReadOnlyDictionary<string, Merchant> _byAccountId;

    public ConfiguredMerchantDirectory(IOptions<MerchantDirectoryOptions> options)
    {
        _byStoreId = options.Value.Stores
            .GroupBy(s => s.StoreId)
            .ToDictionary(g => g.Key, g => g.Last());

        _byAccountId = options.Value.Stores
            .Where(s => !string.IsNullOrWhiteSpace(s.EngineAccountId))
            .GroupBy(s => s.EngineAccountId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
    }

    public Merchant? Find(int storeId) => _byStoreId.TryGetValue(storeId, out var m) ? m : null;

    public Merchant? FindByAccountId(string? engineAccountId) =>
        !string.IsNullOrWhiteSpace(engineAccountId) && _byAccountId.TryGetValue(engineAccountId, out var m)
            ? m
            : null;
}
