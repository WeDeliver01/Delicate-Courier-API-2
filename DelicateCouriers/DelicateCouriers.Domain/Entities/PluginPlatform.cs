namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Plugin Platform module — the API is the single source of truth for
/// WordPress/WooCommerce plugin releases, licenses, downloads, telemetry,
/// feature flags and staged rollouts. Table names follow the platform spec
/// (snake_case) and are mapped explicitly in AppDbContext.
/// </summary>

/// <summary>A distributable plugin product (e.g. the default plugin, a branded variant). Table: plugins.</summary>
public class PluginProduct
{
    public int PluginId { get; set; }
    /// <summary>Stable machine identifier used by installations, e.g. "delicate-courier-platform".</summary>
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;

    public ICollection<PluginRelease> Releases { get; set; } = new List<PluginRelease>();
    public ICollection<PluginFeatureFlag> FeatureFlags { get; set; } = new List<PluginFeatureFlag>();
    public ICollection<PluginLicense> Licenses { get; set; } = new List<PluginLicense>();
}

/// <summary>Release lifecycle status.</summary>
public static class PluginReleaseStatus
{
    public const string Published = "published";
    public const string Withdrawn = "withdrawn";
}

/// <summary>Progressive rollout stages, in order of expanding audience.</summary>
public static class PluginRolloutStage
{
    public const string Internal = "internal";
    public const string Beta = "beta";
    public const string Percent5 = "pct5";
    public const string Percent25 = "pct25";
    public const string Percent100 = "pct100";
}

/// <summary>A versioned plugin build. Table: plugin_releases.</summary>
public class PluginRelease
{
    public int ReleaseId { get; set; }
    public int PluginId { get; set; }
    /// <summary>Semantic version, e.g. "2.5.0".</summary>
    public string Version { get; set; } = string.Empty;
    public string Changelog { get; set; } = string.Empty;
    /// <summary>Minimum compatible versions; null = no constraint.</summary>
    public string? MinWpVersion { get; set; }
    public string? MinWcVersion { get; set; }
    public string? MinPhpVersion { get; set; }
    /// <summary>Lowercase hex SHA256 of the ZIP bytes.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Opaque storage locator. Currently "db:{ReleaseId}" (bytes in PackageData); swaps to an object-storage key later.</summary>
    public string StorageKey { get; set; } = string.Empty;
    /// <summary>ZIP bytes (DB-backed storage; behind IPluginPackageStorage so S3/R2 can replace it without schema churn elsewhere).</summary>
    public byte[] PackageData { get; set; } = Array.Empty<byte>();
    public long FileSizeBytes { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string RolloutStage { get; set; } = PluginRolloutStage.Internal;
    public string Status { get; set; } = PluginReleaseStatus.Published;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? WithdrawnOn { get; set; }
    public string? WithdrawnBy { get; set; }

    public PluginProduct Plugin { get; set; } = null!;
    public ICollection<PluginRolloutRule> RolloutRules { get; set; } = new List<PluginRolloutRule>();
}

/// <summary>License lifecycle status.</summary>
public static class PluginLicenseStatus
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string Revoked = "revoked";
}

/// <summary>A license key bound to a domain on activation. Table: licenses.</summary>
public class PluginLicense
{
    public int LicenseId { get; set; }
    public int PluginId { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    /// <summary>Optional link to an existing platform tenant/store.</summary>
    public int? TenantId { get; set; }
    public int? StoreId { get; set; }
    /// <summary>Domain the license is bound to (set on first activation; normalised lowercase host).</summary>
    public string? Domain { get; set; }
    public string Status { get; set; } = PluginLicenseStatus.Inactive;
    /// <summary>Rollout rings: internal installs see "internal"-stage releases, beta sees "beta" and later.</summary>
    public bool IsInternal { get; set; }
    public bool IsBeta { get; set; }
    public DateTime? ActivatedOn { get; set; }
    public DateTime? DeactivatedOn { get; set; }
    public DateTime? ExpiresOn { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;

    public PluginProduct Plugin { get; set; } = null!;
    public ICollection<PluginInstallation> Installations { get; set; } = new List<PluginInstallation>();
}

/// <summary>A concrete WordPress site running the plugin. Table: installations.</summary>
public class PluginInstallation
{
    public int InstallationId { get; set; }
    public int LicenseId { get; set; }
    /// <summary>Stable random key generated by the plugin on first run; used for deterministic rollout bucketing.</summary>
    public string InstallKey { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string? PluginVersion { get; set; }
    public string? WpVersion { get; set; }
    public string? WcVersion { get; set; }
    public string? PhpVersion { get; set; }
    public DateTime? LastHeartbeatOn { get; set; }
    public int? ApiLatencyMs { get; set; }
    /// <summary>JSON array of recent error summaries reported by the site.</summary>
    public string? RecentErrorsJson { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public PluginLicense License { get; set; } = null!;
}

/// <summary>Raw telemetry events pushed by installations. Table: telemetry_events.</summary>
public class PluginTelemetryEvent
{
    public long TelemetryEventId { get; set; }
    public int? InstallationId { get; set; }
    public int? PluginId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
    public DateTime OccurredOn { get; set; } = DateTime.UtcNow;
    public DateTime ReceivedOn { get; set; } = DateTime.UtcNow;
}

/// <summary>Per-plugin feature flags surfaced to installations. Table: feature_flags.</summary>
public class PluginFeatureFlag
{
    public int FeatureFlagId { get; set; }
    public int PluginId { get; set; }
    public string Key { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string? ValueJson { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public DateTime? ChangedOn { get; set; }
    public string? ChangedBy { get; set; }

    public PluginProduct Plugin { get; set; } = null!;
}

/// <summary>Extra rollout targeting on top of the release stage. Table: rollout_rules.</summary>
public class PluginRolloutRule
{
    public int RolloutRuleId { get; set; }
    public int ReleaseId { get; set; }
    /// <summary>"allow_domain" (always offer to this domain) or "deny_domain" (never offer).</summary>
    public string RuleType { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = string.Empty;

    public PluginRelease Release { get; set; } = null!;
}

/// <summary>Immutable audit trail of sensitive plugin-platform actions. Table: audit_logs.</summary>
public class PluginAuditLog
{
    public long AuditLogId { get; set; }
    /// <summary>e.g. release.published, release.withdrawn, license.activated, download.served.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>Email of the admin, or "plugin:{domain}" for plugin-originated actions.</summary>
    public string Actor { get; set; } = string.Empty;
    public string? EntityType { get; set; }
    public string? EntityRef { get; set; }
    public string? DetailsJson { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}
