using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Data;

/// <summary>
/// Database context for the Delicate Couriers platform.
///
/// Multi-tenant isolation: every tenant-owned entity carries a per-request
/// global query filter that resolves the current TenantID and role from
/// the JWT claims on the active HTTP request:
///   - SuperAdmin role             -> filter bypassed (sees all tenants)
///   - No HTTP context / no auth   -> filter bypassed (background jobs, webhooks,
///                                    design-time migrations, startup seed)
///   - Authenticated User/Admin    -> rows restricted to their TenantID
///
/// Auth claims come from Supabase-issued JWTs. The "TenantId" and role
/// claims are written by the Supabase Custom Access Token Hook — see the
/// SQL in <c>SUPABASE_AUTH_HOOK.sql</c> at the repo root.
/// </summary>
public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? httpContextAccessor = null)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool BypassTenantFilter
    {
        get
        {
            var user = _httpContextAccessor?.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true) return true;
            return user.IsInRole("SuperAdmin");
        }
    }

    public int CurrentTenantId
    {
        get
        {
            var user = _httpContextAccessor?.HttpContext?.User;
            // Supabase hook emits "tenant_id"; we additionally accept "TenantId"
            // for any legacy code path that still writes the PascalCase form.
            var claim = user?.FindFirst("tenant_id")?.Value
                        ?? user?.FindFirst("TenantId")?.Value;
            return int.TryParse(claim, out var t) ? t : 0;
        }
    }

    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<Store> Stores { get; set; }
    public DbSet<User> User { get; set; }
    public DbSet<Order> Orders { get; set; }
    public DbSet<Shipment> Shipments { get; set; }
    public DbSet<Label> Labels { get; set; }
    public DbSet<TrackingEvent> TrackingEvents { get; set; }
    public DbSet<OrderLineItem> OrderLineItems { get; set; }
    public DbSet<PackageType> PackageTypes { get; set; }
    public DbSet<PackageMappingRule> PackageMappingRules { get; set; }
    public DbSet<SystemEvent> SystemEvents { get; set; }
    public DbSet<PluginDebugLog> PluginDebugLogs { get; set; }

    // ----- Plugin Platform (releases, licenses, updates, telemetry) -----
    // SuperAdmin-only module; no tenant query filters (licenses may
    // optionally reference a tenant but the module is platform-scoped).
    public DbSet<PluginProduct> PluginProducts { get; set; }
    public DbSet<PluginRelease> PluginReleases { get; set; }
    public DbSet<PluginLicense> PluginLicenses { get; set; }
    public DbSet<PluginInstallation> PluginInstallations { get; set; }
    public DbSet<PluginTelemetryEvent> PluginTelemetryEvents { get; set; }
    public DbSet<PluginFeatureFlag> PluginFeatureFlags { get; set; }
    public DbSet<PluginRolloutRule> PluginRolloutRules { get; set; }
    public DbSet<PluginAuditLog> PluginAuditLogs { get; set; }

    // ----- ASP.NET Data Protection key ring -----
    // Persisted in Postgres (rather than the local filesystem) so the
    // encryption keys survive a fresh Replit deploy and are shared across
    // any future API replicas.
    public DbSet<Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey> DataProtectionKeys { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        #region Tenant
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(e => e.TenantID);
            entity.Property(e => e.TenantName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TenantAPIKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasIndex(e => e.TenantAPIKey).IsUnique();
            entity.HasQueryFilter(e => BypassTenantFilter || e.TenantID == CurrentTenantId);
        });
        #endregion

        #region Store
        modelBuilder.Entity<Store>(entity =>
        {
            entity.HasKey(e => e.StoreID);
            entity.Property(e => e.StoreName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SpecialTripCostPerKm).HasPrecision(10, 2);
            entity.Property(e => e.SpecialTripMinFee).HasPrecision(18, 2);
            entity.Property(e => e.SpecialTripMaxKm).HasPrecision(10, 1);
            entity.Property(e => e.WooCommerceURL).HasMaxLength(500);
            entity.Property(e => e.WooConsumerKey).HasMaxLength(200);
            entity.Property(e => e.WooConsumerSecret).HasMaxLength(200);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Tenant).WithMany(t => t.Stores).HasForeignKey(e => e.TenantID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.TenantID);
            entity.HasQueryFilter(e => BypassTenantFilter || e.TenantID == CurrentTenantId);
        });
        #endregion

        #region User
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.UserID);
            entity.Property(e => e.SupabaseUserId).IsRequired();
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Tenant).WithMany(t => t.Users).HasForeignKey(e => e.TenantID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.TenantID);
            entity.HasIndex(e => e.SupabaseUserId).IsUnique();
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasQueryFilter(e => BypassTenantFilter || e.TenantID == CurrentTenantId);
        });
        #endregion

        #region Order
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(e => e.OrderID);
            entity.Property(e => e.WooOrderID).IsRequired().HasMaxLength(50);
            entity.Property(e => e.WooOrderNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CustomerName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CustomerEmail).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CustomerPhone).HasMaxLength(50);
            entity.Property(e => e.ShippingAddressLine1).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ShippingAddressLine2).HasMaxLength(500);
            entity.Property(e => e.ShippingSuburb).HasMaxLength(200);
            entity.Property(e => e.ShippingCity).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ShippingProvince).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ShippingPostalCode).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ShippingCountry).IsRequired().HasMaxLength(100);
            entity.Property(e => e.OrderTotal).HasPrecision(18, 2);
            entity.Property(e => e.OrderStatus).IsRequired().HasMaxLength(50);
            entity.Property(e => e.FulfillmentType).HasMaxLength(20);
            entity.Property(e => e.ShippingMethodId).HasMaxLength(200);
            entity.Property(e => e.ShippingMethodTitle).HasMaxLength(500);
            entity.Property(e => e.SpecialTripQuotedAmount).HasPrecision(18, 2);
            entity.Property(e => e.SpecialTripDistanceKm).HasPrecision(10, 1);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Tenant).WithMany().HasForeignKey(e => e.TenantID).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Store).WithMany(s => s.Orders).HasForeignKey(e => e.StoreID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.TenantID);
            entity.HasIndex(e => e.StoreID);
            entity.HasIndex(e => new { e.StoreID, e.WooOrderID }).IsUnique();
            entity.HasQueryFilter(e => BypassTenantFilter || e.TenantID == CurrentTenantId);
        });
        #endregion

        #region Shipment
        modelBuilder.Entity<Shipment>(entity =>
        {
            entity.HasKey(e => e.ShipmentID);
            entity.Property(e => e.ConsignmentID).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TrackingNumber).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CourierName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CourierService).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ShipmentStatus).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ShippingCost).HasPrecision(18, 2);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Order).WithOne(o => o.Shipment).HasForeignKey<Shipment>(e => e.OrderID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.OrderID).IsUnique();
            entity.HasIndex(e => e.ConsignmentID).IsUnique();
            entity.HasIndex(e => e.TrackingNumber);
            entity.HasQueryFilter(e => BypassTenantFilter || e.Order.TenantID == CurrentTenantId);
        });
        #endregion

        #region Label
        modelBuilder.Entity<Label>(entity =>
        {
            entity.HasKey(e => e.LabelID);
            entity.Property(e => e.BlobURL).IsRequired();
            entity.Property(e => e.BlobFileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Shipment).WithOne(s => s.Label).HasForeignKey<Label>(e => e.ShipmentID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.ShipmentID).IsUnique();
            entity.HasQueryFilter(e => BypassTenantFilter || e.Shipment.Order.TenantID == CurrentTenantId);
        });
        #endregion

        #region TrackingEvent
        modelBuilder.Entity<TrackingEvent>(entity =>
        {
            entity.HasKey(e => e.TrackingEventID);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.EventDescription).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.EventLocation).HasMaxLength(200);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Shipment).WithMany(s => s.TrackingEvents).HasForeignKey(e => e.ShipmentID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.ShipmentID);
            entity.HasIndex(e => e.EventTimestamp);
            entity.HasQueryFilter(e => BypassTenantFilter || e.Shipment.Order.TenantID == CurrentTenantId);
        });
        #endregion

        #region OrderLineItem
        modelBuilder.Entity<OrderLineItem>(entity =>
        {
            entity.HasKey(e => e.OrderLineItemID);
            entity.Property(e => e.ProductName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ProductSKU).HasMaxLength(100);
            entity.Property(e => e.WeightPerUnit).HasPrecision(10, 3);
            entity.Property(e => e.TotalWeight).HasPrecision(10, 3);
            entity.Property(e => e.UnitPrice).HasPrecision(18, 2);
            entity.Property(e => e.LineTotal).HasPrecision(18, 2);
            entity.Property(e => e.TaxAmount).HasPrecision(18, 2);
            entity.Property(e => e.VariationDetails).HasMaxLength(2000);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.HasOne(e => e.Order).WithMany(o => o.LineItems).HasForeignKey(e => e.OrderID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.OrderID);
            entity.HasIndex(e => e.WooLineItemID);
            entity.HasIndex(e => e.ProductSKU);
            entity.HasQueryFilter(e => BypassTenantFilter || e.Order.TenantID == CurrentTenantId);
        });
        #endregion

        #region PackageType
        modelBuilder.Entity<PackageType>(entity =>
        {
            entity.HasKey(e => e.PackageTypeID);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.DefaultWeightKg).HasPrecision(10, 3);
            entity.Property(e => e.MaxWeightKg).HasPrecision(10, 3);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Store).WithMany(s => s.PackageTypes).HasForeignKey(e => e.StoreID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.StoreID);
            entity.HasIndex(e => new { e.StoreID, e.IsDefault });
            entity.HasQueryFilter(e => BypassTenantFilter || e.Store.TenantID == CurrentTenantId);
        });
        #endregion

        #region PackageMappingRule
        modelBuilder.Entity<PackageMappingRule>(entity =>
        {
            entity.HasKey(e => e.PackageMappingRuleID);
            entity.Property(e => e.Keyword).IsRequired().HasMaxLength(200);
            entity.Property(e => e.MatchType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.PackageType).WithMany(p => p.MappingRules).HasForeignKey(e => e.PackageTypeID).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.PackageTypeID);
            entity.HasIndex(e => new { e.PackageTypeID, e.IsActive });
            entity.HasQueryFilter(e => BypassTenantFilter || e.PackageType.Store.TenantID == CurrentTenantId);
        });
        #endregion

        #region PluginPlatform
        modelBuilder.Entity<PluginProduct>(entity =>
        {
            entity.ToTable("plugins");
            entity.HasKey(e => e.PluginId);
            entity.Property(e => e.Slug).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.Slug).IsUnique();
        });

        modelBuilder.Entity<PluginRelease>(entity =>
        {
            entity.ToTable("plugin_releases");
            entity.HasKey(e => e.ReleaseId);
            entity.Property(e => e.Version).IsRequired().HasMaxLength(50);
            entity.Property(e => e.MinWpVersion).HasMaxLength(20);
            entity.Property(e => e.MinWcVersion).HasMaxLength(20);
            entity.Property(e => e.MinPhpVersion).HasMaxLength(20);
            entity.Property(e => e.Sha256).IsRequired().HasMaxLength(64);
            entity.Property(e => e.StorageKey).IsRequired().HasMaxLength(200);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(300);
            entity.Property(e => e.RolloutStage).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.Property(e => e.WithdrawnBy).HasMaxLength(100);
            entity.HasOne(e => e.Plugin).WithMany(p => p.Releases).HasForeignKey(e => e.PluginId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.PluginId, e.Version }).IsUnique();
            entity.HasIndex(e => new { e.PluginId, e.Status });
        });

        modelBuilder.Entity<PluginLicense>(entity =>
        {
            entity.ToTable("licenses");
            entity.HasKey(e => e.LicenseId);
            entity.Property(e => e.LicenseKey).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Domain).HasMaxLength(255);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.HasOne(e => e.Plugin).WithMany(p => p.Licenses).HasForeignKey(e => e.PluginId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.LicenseKey).IsUnique();
            entity.HasIndex(e => e.StoreId);
            entity.HasIndex(e => e.TenantId);
        });

        modelBuilder.Entity<PluginInstallation>(entity =>
        {
            entity.ToTable("installations");
            entity.HasKey(e => e.InstallationId);
            entity.Property(e => e.InstallKey).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Domain).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PluginVersion).HasMaxLength(50);
            entity.Property(e => e.WpVersion).HasMaxLength(20);
            entity.Property(e => e.WcVersion).HasMaxLength(20);
            entity.Property(e => e.PhpVersion).HasMaxLength(20);
            entity.HasOne(e => e.License).WithMany(l => l.Installations).HasForeignKey(e => e.LicenseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.LicenseId, e.InstallKey }).IsUnique();
            entity.HasIndex(e => e.Domain);
        });

        modelBuilder.Entity<PluginTelemetryEvent>(entity =>
        {
            entity.ToTable("telemetry_events");
            entity.HasKey(e => e.TelemetryEventId);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.HasIndex(e => e.InstallationId);
            entity.HasIndex(e => new { e.EventType, e.OccurredOn });
        });

        modelBuilder.Entity<PluginFeatureFlag>(entity =>
        {
            entity.ToTable("feature_flags");
            entity.HasKey(e => e.FeatureFlagId);
            entity.Property(e => e.Key).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ChangedBy).HasMaxLength(100);
            entity.HasOne(e => e.Plugin).WithMany(p => p.FeatureFlags).HasForeignKey(e => e.PluginId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.PluginId, e.Key }).IsUnique();
        });

        modelBuilder.Entity<PluginRolloutRule>(entity =>
        {
            entity.ToTable("rollout_rules");
            entity.HasKey(e => e.RolloutRuleId);
            entity.Property(e => e.RuleType).IsRequired().HasMaxLength(30);
            entity.Property(e => e.Value).IsRequired().HasMaxLength(255);
            entity.Property(e => e.CreatedBy).IsRequired().HasMaxLength(100);
            entity.HasOne(e => e.Release).WithMany(r => r.RolloutRules).HasForeignKey(e => e.ReleaseId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.ReleaseId);
        });

        modelBuilder.Entity<PluginAuditLog>(entity =>
        {
            entity.ToTable("audit_logs");
            entity.HasKey(e => e.AuditLogId);
            entity.Property(e => e.Action).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Actor).IsRequired().HasMaxLength(255);
            entity.Property(e => e.EntityType).HasMaxLength(50);
            entity.Property(e => e.EntityRef).HasMaxLength(255);
            entity.Property(e => e.IpAddress).HasMaxLength(45);
            entity.HasIndex(e => new { e.Action, e.CreatedOn });
        });
        #endregion
    }
}
