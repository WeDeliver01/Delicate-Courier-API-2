using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Integration.Merchants;

/// <summary>
/// Where merchants live.
///
/// They started in a settings file, which meant onboarding a bakery was a developer editing
/// JSON and redeploying, a moved premises was a deploy, and a typo was silent — the merchant
/// simply stopped working with nothing to say why. A row in a table fixes all three: it can be
/// changed by whoever runs the business, it can be changed without a release, and every change
/// can be recorded.
/// </summary>
public sealed class MerchantDbContext : DbContext
{
    public MerchantDbContext(DbContextOptions<MerchantDbContext> options) : base(options) { }

    public DbSet<MerchantRecord> Merchants => Set<MerchantRecord>();
    public DbSet<MerchantChange> MerchantChanges => Set<MerchantChange>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var merchant = model.Entity<MerchantRecord>();
        merchant.ToTable("merchants");
        merchant.HasKey(m => m.StoreId);
        // The store id comes from the merchant's own plugin configuration, so it is their
        // number rather than ours to hand out.
        merchant.Property(m => m.StoreId).ValueGeneratedNever();
        merchant.Property(m => m.Name).IsRequired().HasMaxLength(200);
        merchant.Property(m => m.EngineAccountRef).IsRequired().HasMaxLength(120);
        merchant.Property(m => m.EngineAccountId).HasMaxLength(64);
        merchant.Property(m => m.CheckoutServiceLevelCode).IsRequired().HasMaxLength(32);
        merchant.Property(m => m.CheckoutRateLabel).IsRequired().HasMaxLength(200);
        merchant.Property(m => m.EngineServiceLevelCode).IsRequired().HasMaxLength(32);
        merchant.Property(m => m.DefaultPackageTypeId).IsRequired().HasMaxLength(64);
        merchant.Property(m => m.WebhookSecret).IsRequired().HasMaxLength(200);
        merchant.Property(m => m.BookingTriggerStatuses).HasColumnType("text[]");
        merchant.Property(m => m.WooCommerceUrl).HasMaxLength(300);
        merchant.Property(m => m.WooConsumerKey).HasMaxLength(200);
        merchant.Property(m => m.WooConsumerSecret).HasMaxLength(200);
        merchant.OwnsOne(m => m.Collection, address =>
        {
            address.ToJson();
        });

        // Routing an inbound status callback is a lookup on this, on every status change of
        // every shipment, so it is indexed rather than scanned.
        merchant.HasIndex(m => m.EngineAccountId);

        var change = model.Entity<MerchantChange>();
        change.ToTable("merchant_changes");
        change.HasKey(c => c.Id);
        change.Property(c => c.Action).IsRequired().HasMaxLength(40);
        change.Property(c => c.ActorLabel).HasMaxLength(200);
        change.Property(c => c.Detail).HasColumnType("jsonb");
        change.HasIndex(c => new { c.StoreId, c.OccurredAt });
    }
}

/// <summary>A merchant as stored. Maps onto <see cref="Merchant"/> for everything else to use.</summary>
public sealed class MerchantRecord
{
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string EngineAccountRef { get; set; } = string.Empty;
    public string? EngineAccountId { get; set; }
    public MerchantAddress Collection { get; set; } = new();
    public string CheckoutServiceLevelCode { get; set; } = "STD";
    public string CheckoutRateLabel { get; set; } = "Delicate Courier Delivery";
    public string EngineServiceLevelCode { get; set; } = "standard";
    public string DefaultPackageTypeId { get; set; } = string.Empty;
    public int EstimatedDeliveryDays { get; set; } = 1;

    /// <summary>
    /// Stored as given, because verifying an HMAC needs the original bytes — there is no
    /// hashing it. Anyone with read access to this table can forge a webhook for that
    /// merchant, so the database's own access control is the protection, and encrypting these
    /// at rest is worth doing before this holds real merchants.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public List<string> BookingTriggerStatuses { get; set; } = new();
    public string? WooCommerceUrl { get; set; }
    public string? WooConsumerKey { get; set; }
    public string? WooConsumerSecret { get; set; }

    /// <summary>Off means the service behaves as though this merchant does not exist.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Merchant ToMerchant() => new()
    {
        StoreId = StoreId,
        Name = Name,
        EngineAccountRef = EngineAccountRef,
        EngineAccountId = EngineAccountId,
        Collection = Collection,
        CheckoutServiceLevelCode = CheckoutServiceLevelCode,
        CheckoutRateLabel = CheckoutRateLabel,
        EngineServiceLevelCode = EngineServiceLevelCode,
        DefaultPackageTypeId = DefaultPackageTypeId,
        EstimatedDeliveryDays = EstimatedDeliveryDays,
        WebhookSecret = WebhookSecret,
        BookingTriggerStatuses = BookingTriggerStatuses,
        WooCommerceUrl = WooCommerceUrl,
        WooConsumerKey = WooConsumerKey,
        WooConsumerSecret = WooConsumerSecret,
    };
}

/// <summary>
/// What changed, when, and who said so.
///
/// The settings file had no answer to "who turned this merchant off on Friday". A row per
/// change does, and it costs almost nothing.
/// </summary>
public sealed class MerchantChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int StoreId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ActorLabel { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
