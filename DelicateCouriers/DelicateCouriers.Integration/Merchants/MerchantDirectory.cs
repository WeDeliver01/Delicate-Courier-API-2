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
public sealed class Merchant
{
    public int StoreId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The merchant's identifier as the Engine knows it, mapped there through
    /// `account_external_refs`. Sent as `X-Account-Ref`, so no Engine uuid lives here.
    /// </summary>
    public string EngineAccountRef { get; set; } = string.Empty;

    /// <summary>Where the courier collects. Priced as one leg of the Engine's depot loop.</summary>
    public MerchantAddress Collection { get; set; } = new();

    /// <summary>
    /// The service-level code the plugin is configured to accept, almost always "STD". The
    /// plugin filters rates by this; get it wrong and the customer sees no delivery option.
    /// </summary>
    public string CheckoutServiceLevelCode { get; set; } = "STD";

    /// <summary>What the customer reads at checkout. The merchant's words, not ours.</summary>
    public string CheckoutRateLabel { get; set; } = "Delicate Courier Delivery";

    /// <summary>The Engine service level to price against, e.g. "standard".</summary>
    public string EngineServiceLevelCode { get; set; } = "standard";

    /// <summary>
    /// Engine package type to quote each parcel as. The plugin sends dimensions and weight,
    /// the Engine prices by catalogued package type, and nothing can derive one from the other
    /// — so a merchant who sells cakes says so once, here.
    /// </summary>
    public string DefaultPackageTypeId { get; set; } = string.Empty;

    public int EstimatedDeliveryDays { get; set; } = 1;

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
}

public sealed class ConfiguredMerchantDirectory : IMerchantDirectory
{
    private readonly IReadOnlyDictionary<int, Merchant> _byStoreId;

    public ConfiguredMerchantDirectory(IOptions<MerchantDirectoryOptions> options)
    {
        _byStoreId = options.Value.Stores
            .GroupBy(s => s.StoreId)
            .ToDictionary(g => g.Key, g => g.Last());
    }

    public Merchant? Find(int storeId) => _byStoreId.TryGetValue(storeId, out var m) ? m : null;
}
