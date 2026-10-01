using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// Core update-resolution logic: given a validated license + installation
/// context, find the best published release the caller is entitled to see
/// (version newer than installed, compatibility satisfied, rollout stage /
/// rules pass) and expose the plugin's feature flags.
/// </summary>
public class PluginUpdateService
{
    private readonly AppDbContext _context;

    public PluginUpdateService(AppDbContext context) => _context = context;

    public record LicenseResolution(PluginLicense? License, PluginProduct? Plugin, string? Error);

    /// <summary>
    /// Resolve + validate a license for a plugin slug. Errors are
    /// machine-readable strings the plugin surfaces to the merchant.
    /// </summary>
    public async Task<LicenseResolution> ResolveLicenseAsync(string slug, string licenseKey, string domain, bool requireActive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(licenseKey))
            return new LicenseResolution(null, null, "missing_slug_or_license");

        var plugin = await _context.PluginProducts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Slug == slug && p.IsActive, ct);
        if (plugin is null) return new LicenseResolution(null, null, "unknown_plugin");

        var license = await _context.PluginLicenses
            .FirstOrDefaultAsync(l => l.PluginId == plugin.PluginId && l.LicenseKey == licenseKey, ct);
        if (license is null) return new LicenseResolution(null, plugin, "invalid_license");

        if (license.Status == PluginLicenseStatus.Revoked)
            return new LicenseResolution(license, plugin, "license_revoked");
        if (license.ExpiresOn is not null && license.ExpiresOn < DateTime.UtcNow)
            return new LicenseResolution(license, plugin, "license_expired");
        if (requireActive && license.Status != PluginLicenseStatus.Active)
            return new LicenseResolution(license, plugin, "license_not_activated");

        // Domain binding: once bound, only that domain may use the key.
        var host = NormalizeDomain(domain);
        if (!string.IsNullOrEmpty(license.Domain) && !string.Equals(license.Domain, host, StringComparison.OrdinalIgnoreCase))
            return new LicenseResolution(license, plugin, "domain_mismatch");

        return new LicenseResolution(license, plugin, null);
    }

    /// <summary>
    /// Best release the given installation is entitled to. Returns null if
    /// already up to date (or nothing passes rollout/compat gates).
    /// </summary>
    public async Task<PluginRelease?> ResolveBestReleaseAsync(
        PluginLicense license, string installKey, string domain,
        string? currentVersion, string? wpVersion, string? wcVersion, string? phpVersion,
        CancellationToken ct)
    {
        var candidates = await _context.PluginReleases.AsNoTracking()
            .Include(r => r.RolloutRules)
            .Where(r => r.PluginId == license.PluginId && r.Status == PluginReleaseStatus.Published)
            .Select(r => new PluginRelease
            {
                // Explicit projection so the bytea column is never pulled here.
                ReleaseId = r.ReleaseId,
                PluginId = r.PluginId,
                Version = r.Version,
                Changelog = r.Changelog,
                MinWpVersion = r.MinWpVersion,
                MinWcVersion = r.MinWcVersion,
                MinPhpVersion = r.MinPhpVersion,
                Sha256 = r.Sha256,
                FileSizeBytes = r.FileSizeBytes,
                FileName = r.FileName,
                RolloutStage = r.RolloutStage,
                Status = r.Status,
                CreatedOn = r.CreatedOn,
                RolloutRules = r.RolloutRules,
            })
            .ToListAsync(ct);

        PluginRelease? best = null;
        var host = NormalizeDomain(domain);
        foreach (var release in candidates)
        {
            if (!string.IsNullOrEmpty(currentVersion) && PluginVersionHelper.Compare(release.Version, currentVersion) <= 0)
                continue;
            if (!PluginVersionHelper.MeetsMinimum(release.MinWpVersion, wpVersion)) continue;
            if (!PluginVersionHelper.MeetsMinimum(release.MinWcVersion, wcVersion)) continue;
            if (!PluginVersionHelper.MeetsMinimum(release.MinPhpVersion, phpVersion)) continue;
            if (!PassesRollout(release, license, installKey, host)) continue;
            if (best is null || PluginVersionHelper.Compare(release.Version, best.Version) > 0)
                best = release;
        }
        return best;
    }

    /// <summary>
    /// Rollout gate. Explicit domain rules win over the stage; the stage
    /// then widens the audience: internal → beta → 5% → 25% → 100%.
    /// Internal/beta licenses always see percentage stages too (they opted
    /// into early builds).
    /// </summary>
    public static bool PassesRollout(PluginRelease release, PluginLicense license, string installKey, string domain)
    {
        foreach (var rule in release.RolloutRules)
        {
            if (rule.RuleType == "deny_domain" && string.Equals(rule.Value, domain, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        foreach (var rule in release.RolloutRules)
        {
            if (rule.RuleType == "allow_domain" && string.Equals(rule.Value, domain, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return release.RolloutStage switch
        {
            PluginRolloutStage.Internal => license.IsInternal,
            PluginRolloutStage.Beta => license.IsInternal || license.IsBeta,
            PluginRolloutStage.Percent5 => license.IsInternal || license.IsBeta || PluginVersionHelper.Bucket(installKey, release.ReleaseId) < 5,
            PluginRolloutStage.Percent25 => license.IsInternal || license.IsBeta || PluginVersionHelper.Bucket(installKey, release.ReleaseId) < 25,
            PluginRolloutStage.Percent100 => true,
            _ => false,
        };
    }

    public async Task<Dictionary<string, object?>> GetFlagsAsync(int pluginId, CancellationToken ct)
    {
        var flags = await _context.PluginFeatureFlags.AsNoTracking()
            .Where(f => f.PluginId == pluginId)
            .ToListAsync(ct);
        var result = new Dictionary<string, object?>();
        foreach (var f in flags)
        {
            if (!f.Enabled) { result[f.Key] = false; continue; }
            if (string.IsNullOrEmpty(f.ValueJson)) { result[f.Key] = true; continue; }
            try { result[f.Key] = System.Text.Json.JsonSerializer.Deserialize<object>(f.ValueJson); }
            catch { result[f.Key] = true; }
        }
        return result;
    }

    /// <summary>Lowercased host with scheme/path/port/www stripped.</summary>
    public static string NormalizeDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return string.Empty;
        var d = domain.Trim().ToLowerInvariant();
        if (d.Contains("://") && Uri.TryCreate(d, UriKind.Absolute, out var uri)) d = uri.Host;
        var slash = d.IndexOf('/');
        if (slash >= 0) d = d[..slash];
        var colon = d.IndexOf(':');
        if (colon >= 0) d = d[..colon];
        if (d.StartsWith("www.")) d = d[4..];
        return d;
    }
}
