using System.Security.Cryptography;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// License issuing, activation (domain binding), deactivation, and
/// installation upserts (heartbeats).
/// </summary>
public class PluginLicenseService
{
    private readonly AppDbContext _context;
    private readonly PluginAuditService _audit;

    public PluginLicenseService(AppDbContext context, PluginAuditService audit)
    {
        _context = context;
        _audit = audit;
    }

    /// <summary>Format: DCP-XXXX-XXXX-XXXX-XXXX (crypto-random, unambiguous alphabet).</summary>
    public static string GenerateLicenseKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[16];
        var bytes = RandomNumberGenerator.GetBytes(16);
        for (var i = 0; i < 16; i++) chars[i] = alphabet[bytes[i] % alphabet.Length];
        return $"DCP-{new string(chars[..4])}-{new string(chars[4..8])}-{new string(chars[8..12])}-{new string(chars[12..])}";
    }

    /// <summary>
    /// Activate a license for a domain: binds the domain on first use and
    /// upserts the installation record. Returns a rejection reason or null.
    /// </summary>
    public async Task<string?> ActivateAsync(PluginLicense license, string domain, string installKey,
        string? pluginVersion, string? wpVersion, string? wcVersion, string? phpVersion, string? ip, CancellationToken ct)
    {
        var host = PluginUpdateService.NormalizeDomain(domain);
        if (string.IsNullOrEmpty(host)) return "invalid_domain";
        if (string.IsNullOrWhiteSpace(installKey)) return "missing_install_key";

        if (string.IsNullOrEmpty(license.Domain))
            license.Domain = host;
        else if (!string.Equals(license.Domain, host, StringComparison.OrdinalIgnoreCase))
            return "domain_mismatch";

        if (license.Status != PluginLicenseStatus.Active)
        {
            license.Status = PluginLicenseStatus.Active;
            license.ActivatedOn = DateTime.UtcNow;
            license.DeactivatedOn = null;
        }

        await UpsertInstallationAsync(license, installKey, host, pluginVersion, wpVersion, wcVersion, phpVersion, null, null, ct);

        _audit.Append("license.activated", $"plugin:{host}", "PluginLicense", license.LicenseKey,
            new { domain = host, pluginVersion }, ip);
        await _context.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> DeactivateAsync(PluginLicense license, string domain, string? ip, CancellationToken ct)
    {
        var host = PluginUpdateService.NormalizeDomain(domain);
        if (!string.IsNullOrEmpty(license.Domain) && !string.Equals(license.Domain, host, StringComparison.OrdinalIgnoreCase))
            return "domain_mismatch";

        license.Status = PluginLicenseStatus.Inactive;
        license.DeactivatedOn = DateTime.UtcNow;

        _audit.Append("license.deactivated", $"plugin:{host}", "PluginLicense", license.LicenseKey, null, ip);
        await _context.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Upsert the installation row keyed by (license, installKey). Does not SaveChanges.</summary>
    public async Task<PluginInstallation> UpsertInstallationAsync(PluginLicense license, string installKey, string domain,
        string? pluginVersion, string? wpVersion, string? wcVersion, string? phpVersion,
        int? apiLatencyMs, string? recentErrorsJson, CancellationToken ct)
    {
        var installation = await _context.PluginInstallations
            .FirstOrDefaultAsync(i => i.LicenseId == license.LicenseId && i.InstallKey == installKey, ct);
        if (installation is null)
        {
            installation = new PluginInstallation
            {
                LicenseId = license.LicenseId,
                InstallKey = installKey,
                Domain = domain,
                CreatedOn = DateTime.UtcNow,
            };
            _context.PluginInstallations.Add(installation);
        }

        installation.Domain = domain;
        if (!string.IsNullOrEmpty(pluginVersion)) installation.PluginVersion = pluginVersion;
        if (!string.IsNullOrEmpty(wpVersion)) installation.WpVersion = wpVersion;
        if (!string.IsNullOrEmpty(wcVersion)) installation.WcVersion = wcVersion;
        if (!string.IsNullOrEmpty(phpVersion)) installation.PhpVersion = phpVersion;
        if (apiLatencyMs is not null) installation.ApiLatencyMs = apiLatencyMs;
        if (recentErrorsJson is not null) installation.RecentErrorsJson = recentErrorsJson;
        installation.LastHeartbeatOn = DateTime.UtcNow;
        return installation;
    }
}
