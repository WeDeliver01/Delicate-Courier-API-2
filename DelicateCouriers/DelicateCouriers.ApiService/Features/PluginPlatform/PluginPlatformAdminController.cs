using System.Security.Claims;
using System.Security.Cryptography;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// SuperAdmin management API for the Plugin Platform: plugins, release
/// uploads (ZIP + SHA256), rollout staging, licenses, installations,
/// feature flags, rollout rules, telemetry and the audit trail. Backs the
/// upcoming "Plugin Management" dashboard tab.
/// </summary>
[ApiController]
[Route("api/admin/plugin-platform")]
[Authorize(Roles = "SuperAdmin")]
public class PluginPlatformAdminController : ControllerBase
{
    private const long MaxPackageBytes = 25 * 1024 * 1024; // 25 MB — far above any plugin ZIP

    private static readonly string[] ValidStages =
    {
        PluginRolloutStage.Internal, PluginRolloutStage.Beta,
        PluginRolloutStage.Percent5, PluginRolloutStage.Percent25, PluginRolloutStage.Percent100,
    };

    private readonly AppDbContext _context;
    private readonly IPluginPackageStorage _storage;
    private readonly PluginAuditService _audit;

    public PluginPlatformAdminController(AppDbContext context, IPluginPackageStorage storage, PluginAuditService audit)
    {
        _context = context;
        _storage = storage;
        _audit = audit;
    }

    private string CallerEmail =>
        User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value ?? "unknown";

    // ---------------------------------------------------------------- plugins

    [HttpGet("plugins")]
    public async Task<IActionResult> ListPlugins(CancellationToken ct)
    {
        var plugins = await _context.PluginProducts.AsNoTracking()
            .OrderBy(p => p.PluginId)
            .Select(p => new
            {
                p.PluginId, p.Slug, p.Name, p.Description, p.IsActive, p.CreatedOn,
                releaseCount = p.Releases.Count,
                latestVersion = p.Releases
                    .Where(r => r.Status == PluginReleaseStatus.Published)
                    .OrderByDescending(r => r.CreatedOn)
                    .Select(r => r.Version)
                    .FirstOrDefault(),
                licenseCount = p.Licenses.Count,
            })
            .ToListAsync(ct);
        return Ok(plugins);
    }

    public class CreatePluginRequest
    {
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    [HttpPost("plugins")]
    public async Task<IActionResult> CreatePlugin([FromBody] CreatePluginRequest req, CancellationToken ct)
    {
        var slug = (req.Slug ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(slug) || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "slug and name are required" });
        if (await _context.PluginProducts.AnyAsync(p => p.Slug == slug, ct))
            return Conflict(new { message = $"Plugin slug '{slug}' already exists." });

        var plugin = new PluginProduct { Slug = slug, Name = req.Name.Trim(), Description = req.Description, CreatedBy = CallerEmail };
        _context.PluginProducts.Add(plugin);
        _audit.Append("plugin.created", CallerEmail, "PluginProduct", slug);
        await _context.SaveChangesAsync(ct);
        return Ok(new { plugin.PluginId, plugin.Slug, plugin.Name });
    }

    // --------------------------------------------------------------- releases

    [HttpGet("plugins/{pluginId:int}/releases")]
    public async Task<IActionResult> ListReleases(int pluginId, CancellationToken ct)
    {
        var releases = await _context.PluginReleases.AsNoTracking()
            .Where(r => r.PluginId == pluginId)
            .OrderByDescending(r => r.CreatedOn)
            .Select(r => new
            {
                r.ReleaseId, r.Version, r.Changelog, r.MinWpVersion, r.MinWcVersion, r.MinPhpVersion,
                r.Sha256, r.FileSizeBytes, r.FileName, r.RolloutStage, r.Status,
                r.CreatedOn, r.CreatedBy, r.WithdrawnOn, r.WithdrawnBy,
                rules = r.RolloutRules.Select(x => new { x.RolloutRuleId, x.RuleType, x.Value }),
            })
            .ToListAsync(ct);
        return Ok(releases);
    }

    /// <summary>Upload a new release ZIP (multipart/form-data).</summary>
    [HttpPost("plugins/{pluginId:int}/releases")]
    [RequestSizeLimit(MaxPackageBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadRelease(
        int pluginId,
        [FromForm] IFormFile file,
        [FromForm] string version,
        [FromForm] string? changelog,
        [FromForm] string? minWpVersion,
        [FromForm] string? minWcVersion,
        [FromForm] string? minPhpVersion,
        [FromForm] string? rolloutStage,
        CancellationToken ct)
    {
        var plugin = await _context.PluginProducts.FirstOrDefaultAsync(p => p.PluginId == pluginId, ct);
        if (plugin is null) return NotFound(new { message = "Plugin not found." });

        version = (version ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(version))
            return BadRequest(new { message = "version is required" });
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "A ZIP file is required." });
        if (file.Length > MaxPackageBytes)
            return BadRequest(new { message = "Package exceeds the 25 MB limit." });

        var stage = string.IsNullOrWhiteSpace(rolloutStage) ? PluginRolloutStage.Internal : rolloutStage.Trim().ToLowerInvariant();
        if (!ValidStages.Contains(stage))
            return BadRequest(new { message = $"Invalid rolloutStage '{rolloutStage}'. Valid: {string.Join(", ", ValidStages)}" });

        if (await _context.PluginReleases.AnyAsync(r => r.PluginId == pluginId && r.Version == version, ct))
            return Conflict(new { message = $"Version {version} already exists for this plugin." });

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        // ZIP magic-byte sanity check (PK\x03\x04 or empty-archive PK\x05\x06).
        if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B)
            return BadRequest(new { message = "File does not look like a ZIP archive." });

        var release = new PluginRelease
        {
            PluginId = pluginId,
            Version = version,
            Changelog = changelog ?? string.Empty,
            MinWpVersion = Clean(minWpVersion),
            MinWcVersion = Clean(minWcVersion),
            MinPhpVersion = Clean(minPhpVersion),
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            FileName = Path.GetFileName(file.FileName),
            RolloutStage = stage,
            Status = PluginReleaseStatus.Published,
            CreatedBy = CallerEmail,
        };
        release.StorageKey = _storage.Attach(new PluginReleaseWriteModel { Release = release }, bytes);

        _context.PluginReleases.Add(release);
        _audit.Append("release.published", CallerEmail, "PluginRelease", $"{plugin.Slug}:{version}",
            new { stage, sha256 = release.Sha256, sizeBytes = release.FileSizeBytes });
        await _context.SaveChangesAsync(ct);

        return Ok(new { release.ReleaseId, release.Version, release.Sha256, release.RolloutStage, release.FileSizeBytes });
    }

    public class SetRolloutRequest { public string Stage { get; set; } = string.Empty; }

    [HttpPost("releases/{releaseId:int}/rollout")]
    public async Task<IActionResult> SetRollout(int releaseId, [FromBody] SetRolloutRequest req, CancellationToken ct)
    {
        var stage = (req.Stage ?? string.Empty).Trim().ToLowerInvariant();
        if (!ValidStages.Contains(stage))
            return BadRequest(new { message = $"Invalid stage '{req.Stage}'. Valid: {string.Join(", ", ValidStages)}" });

        var release = await _context.PluginReleases.FirstOrDefaultAsync(r => r.ReleaseId == releaseId, ct);
        if (release is null) return NotFound();
        if (release.Status != PluginReleaseStatus.Published)
            return BadRequest(new { message = "Release is withdrawn; republish is not supported — upload a new version." });

        var old = release.RolloutStage;
        release.RolloutStage = stage;
        _audit.Append("release.rollout_changed", CallerEmail, "PluginRelease", $"{release.PluginId}:{release.Version}", new { from = old, to = stage });
        await _context.SaveChangesAsync(ct);
        return Ok(new { release.ReleaseId, release.RolloutStage });
    }

    /// <summary>Withdraw (kill-switch) a bad release — it is never offered again.</summary>
    [HttpPost("releases/{releaseId:int}/withdraw")]
    public async Task<IActionResult> Withdraw(int releaseId, CancellationToken ct)
    {
        var release = await _context.PluginReleases.FirstOrDefaultAsync(r => r.ReleaseId == releaseId, ct);
        if (release is null) return NotFound();
        release.Status = PluginReleaseStatus.Withdrawn;
        release.WithdrawnOn = DateTime.UtcNow;
        release.WithdrawnBy = CallerEmail;
        _audit.Append("release.withdrawn", CallerEmail, "PluginRelease", $"{release.PluginId}:{release.Version}");
        await _context.SaveChangesAsync(ct);
        return Ok(new { release.ReleaseId, release.Status });
    }

    public class CreateRolloutRuleRequest
    {
        public string RuleType { get; set; } = string.Empty; // allow_domain | deny_domain
        public string Value { get; set; } = string.Empty;
    }

    [HttpPost("releases/{releaseId:int}/rules")]
    public async Task<IActionResult> AddRolloutRule(int releaseId, [FromBody] CreateRolloutRuleRequest req, CancellationToken ct)
    {
        if (req.RuleType is not ("allow_domain" or "deny_domain"))
            return BadRequest(new { message = "ruleType must be allow_domain or deny_domain" });
        var release = await _context.PluginReleases.FirstOrDefaultAsync(r => r.ReleaseId == releaseId, ct);
        if (release is null) return NotFound();

        var rule = new PluginRolloutRule
        {
            ReleaseId = releaseId,
            RuleType = req.RuleType,
            Value = PluginUpdateService.NormalizeDomain(req.Value),
            CreatedBy = CallerEmail,
        };
        if (string.IsNullOrEmpty(rule.Value)) return BadRequest(new { message = "value must be a domain" });
        _context.PluginRolloutRules.Add(rule);
        _audit.Append("release.rule_added", CallerEmail, "PluginRelease", $"{release.PluginId}:{release.Version}", new { req.RuleType, rule.Value });
        await _context.SaveChangesAsync(ct);
        return Ok(new { rule.RolloutRuleId, rule.RuleType, rule.Value });
    }

    [HttpDelete("rules/{ruleId:int}")]
    public async Task<IActionResult> DeleteRolloutRule(int ruleId, CancellationToken ct)
    {
        var rule = await _context.PluginRolloutRules.FirstOrDefaultAsync(r => r.RolloutRuleId == ruleId, ct);
        if (rule is null) return NotFound();
        _context.PluginRolloutRules.Remove(rule);
        _audit.Append("release.rule_removed", CallerEmail, "PluginRolloutRule", ruleId.ToString(), new { rule.RuleType, rule.Value });
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    // --------------------------------------------------------------- licenses

    [HttpGet("plugins/{pluginId:int}/licenses")]
    public async Task<IActionResult> ListLicenses(int pluginId, CancellationToken ct)
    {
        var licenses = await _context.PluginLicenses.AsNoTracking()
            .Where(l => l.PluginId == pluginId)
            .OrderByDescending(l => l.CreatedOn)
            .Select(l => new
            {
                l.LicenseId, l.LicenseKey, l.TenantId, l.StoreId, l.Domain, l.Status,
                l.IsInternal, l.IsBeta, l.ActivatedOn, l.DeactivatedOn, l.ExpiresOn, l.CreatedOn, l.CreatedBy,
                installations = l.Installations.Select(i => new
                {
                    i.InstallationId, i.Domain, i.PluginVersion, i.WpVersion, i.WcVersion, i.PhpVersion,
                    i.LastHeartbeatOn, i.ApiLatencyMs,
                }),
            })
            .ToListAsync(ct);
        return Ok(licenses);
    }

    public class CreateLicenseRequest
    {
        public int? TenantId { get; set; }
        public int? StoreId { get; set; }
        public bool IsInternal { get; set; }
        public bool IsBeta { get; set; }
        public DateTime? ExpiresOn { get; set; }
    }

    [HttpPost("plugins/{pluginId:int}/licenses")]
    public async Task<IActionResult> CreateLicense(int pluginId, [FromBody] CreateLicenseRequest req, CancellationToken ct)
    {
        var plugin = await _context.PluginProducts.FirstOrDefaultAsync(p => p.PluginId == pluginId, ct);
        if (plugin is null) return NotFound(new { message = "Plugin not found." });

        if (req.StoreId is not null &&
            !await _context.Stores.IgnoreQueryFilters().AnyAsync(s => s.StoreID == req.StoreId, ct))
            return BadRequest(new { message = $"Store {req.StoreId} does not exist." });
        if (req.TenantId is not null &&
            !await _context.Tenants.IgnoreQueryFilters().AnyAsync(t => t.TenantID == req.TenantId, ct))
            return BadRequest(new { message = $"Tenant {req.TenantId} does not exist." });

        var license = new PluginLicense
        {
            PluginId = pluginId,
            LicenseKey = PluginLicenseService.GenerateLicenseKey(),
            TenantId = req.TenantId,
            StoreId = req.StoreId,
            IsInternal = req.IsInternal,
            IsBeta = req.IsBeta,
            ExpiresOn = req.ExpiresOn,
            Status = PluginLicenseStatus.Inactive,
            CreatedBy = CallerEmail,
        };
        _context.PluginLicenses.Add(license);
        _audit.Append("license.issued", CallerEmail, "PluginLicense", license.LicenseKey,
            new { pluginId, req.TenantId, req.StoreId, req.IsInternal, req.IsBeta });
        await _context.SaveChangesAsync(ct);
        return Ok(new { license.LicenseId, license.LicenseKey, license.Status });
    }

    [HttpPost("licenses/{licenseId:int}/revoke")]
    public async Task<IActionResult> RevokeLicense(int licenseId, CancellationToken ct)
    {
        var license = await _context.PluginLicenses.FirstOrDefaultAsync(l => l.LicenseId == licenseId, ct);
        if (license is null) return NotFound();
        license.Status = PluginLicenseStatus.Revoked;
        license.DeactivatedOn = DateTime.UtcNow;
        _audit.Append("license.revoked", CallerEmail, "PluginLicense", license.LicenseKey);
        await _context.SaveChangesAsync(ct);
        return Ok(new { license.LicenseId, license.Status });
    }

    // ----------------------------------------------------- fleet / telemetry

    [HttpGet("installations")]
    public async Task<IActionResult> ListInstallations(CancellationToken ct)
    {
        var installs = await _context.PluginInstallations.AsNoTracking()
            .OrderByDescending(i => i.LastHeartbeatOn)
            .Select(i => new
            {
                i.InstallationId, i.Domain, i.PluginVersion, i.WpVersion, i.WcVersion, i.PhpVersion,
                i.LastHeartbeatOn, i.ApiLatencyMs, i.RecentErrorsJson, i.CreatedOn,
                license = new { i.License.LicenseId, i.License.LicenseKey, i.License.Status, i.License.StoreId, i.License.TenantId },
                pluginSlug = i.License.Plugin.Slug,
            })
            .ToListAsync(ct);
        return Ok(installs);
    }

    [HttpGet("telemetry")]
    public async Task<IActionResult> ListTelemetry([FromQuery] string? eventType, [FromQuery] int? pluginId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var query = _context.PluginTelemetryEvents.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(eventType)) query = query.Where(e => e.EventType == eventType);
        // Events with no PluginId (e.g. pre-attribution telemetry) are included so
        // they aren't silently dropped from every per-plugin view.
        if (pluginId.HasValue) query = query.Where(e => e.PluginId == null || e.PluginId == pluginId.Value);
        var events = await query.OrderByDescending(e => e.ReceivedOn).Take(limit).ToListAsync(ct);
        return Ok(events);
    }

    // ------------------------------------------------------------------ flags

    [HttpGet("plugins/{pluginId:int}/flags")]
    public async Task<IActionResult> ListFlags(int pluginId, CancellationToken ct)
    {
        var flags = await _context.PluginFeatureFlags.AsNoTracking()
            .Where(f => f.PluginId == pluginId).OrderBy(f => f.Key).ToListAsync(ct);
        return Ok(flags.Select(f => new { f.FeatureFlagId, f.Key, f.Enabled, f.ValueJson, f.ChangedOn, f.ChangedBy }));
    }

    public class UpsertFlagRequest
    {
        public string Key { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public string? ValueJson { get; set; }
    }

    [HttpPost("plugins/{pluginId:int}/flags")]
    public async Task<IActionResult> UpsertFlag(int pluginId, [FromBody] UpsertFlagRequest req, CancellationToken ct)
    {
        var key = (req.Key ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(key)) return BadRequest(new { message = "key is required" });
        if (!await _context.PluginProducts.AnyAsync(p => p.PluginId == pluginId, ct))
            return NotFound(new { message = "Plugin not found." });

        if (!string.IsNullOrEmpty(req.ValueJson))
        {
            try { System.Text.Json.JsonDocument.Parse(req.ValueJson); }
            catch { return BadRequest(new { message = "valueJson must be valid JSON." }); }
        }

        var flag = await _context.PluginFeatureFlags.FirstOrDefaultAsync(f => f.PluginId == pluginId && f.Key == key, ct);
        if (flag is null)
        {
            flag = new PluginFeatureFlag { PluginId = pluginId, Key = key };
            _context.PluginFeatureFlags.Add(flag);
        }
        flag.Enabled = req.Enabled;
        flag.ValueJson = string.IsNullOrEmpty(req.ValueJson) ? null : req.ValueJson;
        flag.ChangedOn = DateTime.UtcNow;
        flag.ChangedBy = CallerEmail;
        _audit.Append("flag.upserted", CallerEmail, "PluginFeatureFlag", $"{pluginId}:{key}", new { req.Enabled, req.ValueJson });
        await _context.SaveChangesAsync(ct);
        return Ok(new { flag.FeatureFlagId, flag.Key, flag.Enabled, flag.ValueJson });
    }

    [HttpDelete("flags/{flagId:int}")]
    public async Task<IActionResult> DeleteFlag(int flagId, CancellationToken ct)
    {
        var flag = await _context.PluginFeatureFlags.FirstOrDefaultAsync(f => f.FeatureFlagId == flagId, ct);
        if (flag is null) return NotFound();
        _context.PluginFeatureFlags.Remove(flag);
        _audit.Append("flag.deleted", CallerEmail, "PluginFeatureFlag", $"{flag.PluginId}:{flag.Key}");
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    // ------------------------------------------------------------------ audit

    [HttpGet("audit-logs")]
    public async Task<IActionResult> ListAuditLogs(
        [FromQuery] string? action, [FromQuery] int? pluginId, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        var query = _context.PluginAuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(action)) query = query.Where(a => a.Action == action);

        if (pluginId.HasValue)
        {
            // Audit entityRefs are plugin-scoped by convention:
            //  - PluginProduct              → "<slug>"
            //  - PluginRelease / FeatureFlag → "<pluginId>:<x>" or "<slug>:<x>"
            //  - PluginLicense              → the license key
            // Applying the same conventions server-side keeps per-plugin views
            // correct regardless of the total audit volume.
            var plugin = await _context.PluginProducts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PluginId == pluginId.Value, ct);
            if (plugin is null) return NotFound(new { error = "plugin_not_found" });

            var slug = plugin.Slug;
            var idPrefix = $"{pluginId.Value}:";
            var slugPrefix = $"{slug}:";
            var licenseKeys = _context.PluginLicenses
                .Where(l => l.PluginId == pluginId.Value)
                .Select(l => l.LicenseKey);

            query = query.Where(a =>
                (a.EntityType == "PluginProduct" && a.EntityRef == slug) ||
                ((a.EntityType == "PluginRelease" || a.EntityType == "PluginFeatureFlag") &&
                    a.EntityRef != null &&
                    (a.EntityRef.StartsWith(idPrefix) || a.EntityRef.StartsWith(slugPrefix))) ||
                (a.EntityType == "PluginLicense" && a.EntityRef != null && licenseKeys.Contains(a.EntityRef)));
        }

        var logs = await query.OrderByDescending(a => a.CreatedOn).Take(limit).ToListAsync(ct);
        return Ok(logs);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
