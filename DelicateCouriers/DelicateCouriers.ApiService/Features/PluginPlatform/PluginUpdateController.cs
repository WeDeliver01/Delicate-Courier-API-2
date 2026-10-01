using System.Text.Json;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// Plugin-facing endpoints (called by WordPress sites — no JWT). Auth is
/// license-key based; every mutating call optionally carries timestamp +
/// nonce for replay protection. Rate limited with the same per-IP policy
/// as the order webhook.
/// </summary>
[ApiController]
[Route("api/v1/plugins")]
[AllowAnonymous]
[EnableRateLimiting("WebhookIp")]
public class PluginUpdateController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly PluginUpdateService _updates;
    private readonly PluginLicenseService _licenses;
    private readonly PluginDownloadTokenService _tokens;
    private readonly IPluginPackageStorage _storage;
    private readonly PluginReplayGuard _replayGuard;
    private readonly PluginAuditService _audit;
    private readonly ILogger<PluginUpdateController> _logger;

    public PluginUpdateController(
        AppDbContext context,
        PluginUpdateService updates,
        PluginLicenseService licenses,
        PluginDownloadTokenService tokens,
        IPluginPackageStorage storage,
        PluginReplayGuard replayGuard,
        PluginAuditService audit,
        ILogger<PluginUpdateController> logger)
    {
        _context = context;
        _updates = updates;
        _licenses = licenses;
        _tokens = tokens;
        _storage = storage;
        _replayGuard = replayGuard;
        _audit = audit;
        _logger = logger;
    }

    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpPost("activate-license")]
    public async Task<IActionResult> Activate([FromBody] PluginActivateRequest req, CancellationToken ct)
    {
        if (_replayGuard.Check(req.Timestamp, req.Nonce) is { } replay)
            return Unauthorized(new { error = replay });

        var res = await _updates.ResolveLicenseAsync(req.Slug, req.LicenseKey, req.Domain, requireActive: false, ct);
        if (res.Error is not null || res.License is null)
            return Unauthorized(new { error = res.Error ?? "invalid_license" });

        var err = await _licenses.ActivateAsync(res.License, req.Domain, req.InstallKey,
            req.PluginVersion, req.WpVersion, req.WcVersion, req.PhpVersion, ClientIp, ct);
        if (err is not null) return Unauthorized(new { error = err });

        var flags = await _updates.GetFlagsAsync(res.License.PluginId, ct);
        return Ok(new { activated = true, domain = res.License.Domain, flags });
    }

    [HttpPost("deactivate-license")]
    public async Task<IActionResult> Deactivate([FromBody] PluginRequestBase req, CancellationToken ct)
    {
        if (_replayGuard.Check(req.Timestamp, req.Nonce) is { } replay)
            return Unauthorized(new { error = replay });

        var res = await _updates.ResolveLicenseAsync(req.Slug, req.LicenseKey, req.Domain, requireActive: false, ct);
        if (res.Error is not null || res.License is null)
            return Unauthorized(new { error = res.Error ?? "invalid_license" });

        var err = await _licenses.DeactivateAsync(res.License, req.Domain, ClientIp, ct);
        if (err is not null) return Unauthorized(new { error = err });
        return Ok(new { deactivated = true });
    }

    [HttpPost("check-update")]
    public async Task<IActionResult> CheckUpdate([FromBody] PluginCheckUpdateRequest req, CancellationToken ct)
    {
        if (_replayGuard.Check(req.Timestamp, req.Nonce) is { } replay)
            return Unauthorized(new { error = replay });

        var res = await _updates.ResolveLicenseAsync(req.Slug, req.LicenseKey, req.Domain, requireActive: true, ct);
        if (res.Error is not null || res.License is null)
            return Unauthorized(new { error = res.Error ?? "invalid_license" });

        // Keep the installation snapshot fresh on every check.
        await _licenses.UpsertInstallationAsync(res.License, req.InstallKey,
            PluginUpdateService.NormalizeDomain(req.Domain),
            req.PluginVersion, req.WpVersion, req.WcVersion, req.PhpVersion, null, null, ct);
        await _context.SaveChangesAsync(ct);

        var flags = await _updates.GetFlagsAsync(res.License.PluginId, ct);
        var release = await _updates.ResolveBestReleaseAsync(res.License, req.InstallKey, req.Domain,
            req.PluginVersion, req.WpVersion, req.WcVersion, req.PhpVersion, ct);

        if (release is null)
            return Ok(new PluginCheckUpdateResponse { UpdateAvailable = false, Flags = flags });

        var token = _tokens.Create(release.ReleaseId, res.License.LicenseId, req.InstallKey);
        return Ok(new PluginCheckUpdateResponse
        {
            UpdateAvailable = true,
            Version = release.Version,
            Changelog = release.Changelog,
            DownloadUrl = Url.ActionLink(nameof(Download), values: new { token })
                          ?? $"/api/v1/plugins/download?token={Uri.EscapeDataString(token)}",
            DownloadExpiresInSeconds = (int)PluginDownloadTokenService.TokenLifetime.TotalSeconds,
            Sha256 = release.Sha256,
            FileSizeBytes = release.FileSizeBytes,
            MinWpVersion = release.MinWpVersion,
            MinWcVersion = release.MinWcVersion,
            MinPhpVersion = release.MinPhpVersion,
            Flags = flags,
        });
    }

    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || !_tokens.TryValidate(token, out var releaseId, out var licenseId, out var installKey))
            return Unauthorized(new { error = "invalid_or_expired_token" });

        var release = await _context.PluginReleases.AsNoTracking()
            .Where(r => r.ReleaseId == releaseId && r.Status == PluginReleaseStatus.Published)
            .Select(r => new { r.ReleaseId, r.FileName, r.Sha256, r.Version, r.PluginId })
            .FirstOrDefaultAsync(ct);
        if (release is null) return NotFound(new { error = "release_not_available" });

        var license = await _context.PluginLicenses.AsNoTracking()
            .FirstOrDefaultAsync(l => l.LicenseId == licenseId, ct);
        if (license is null || license.Status != PluginLicenseStatus.Active)
            return Unauthorized(new { error = "license_not_active" });
        if (license.ExpiresOn is not null && license.ExpiresOn < DateTime.UtcNow)
            return Unauthorized(new { error = "license_expired" });
        // Entitlement: the license must belong to the same plugin as the
        // release, and the installKey baked into the token must be a known
        // installation of that license (check-update upserts it, so a
        // legitimately issued token always has one).
        if (license.PluginId != release.PluginId)
            return Unauthorized(new { error = "license_plugin_mismatch" });
        var installExists = await _context.PluginInstallations.AsNoTracking()
            .AnyAsync(i => i.LicenseId == licenseId && i.InstallKey == installKey, ct);
        if (!installExists)
            return Unauthorized(new { error = "unknown_installation" });

        var bytes = await _storage.ReadAsync(releaseId, ct);
        if (bytes is null || bytes.Length == 0) return NotFound(new { error = "package_missing" });

        // Server-side integrity check: the stored bytes must still match the
        // SHA256 recorded at upload time. A mismatch means corruption or
        // tampering — never serve it, and leave an audit trail.
        var actualSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actualSha),
                System.Text.Encoding.ASCII.GetBytes(release.Sha256.ToLowerInvariant())))
        {
            _logger.LogError("Plugin package integrity failure for release {ReleaseId}: stored hash {Expected}, actual {Actual}",
                releaseId, release.Sha256, actualSha);
            _audit.Append("download.integrity_failure", "system", "PluginRelease",
                $"{release.PluginId}:{release.Version}", new { releaseId, expected = release.Sha256, actual = actualSha }, ClientIp);
            await _context.SaveChangesAsync(ct);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "package_integrity_failure" });
        }

        _audit.Append("download.served", $"plugin:{license.Domain ?? "unknown"}", "PluginRelease",
            $"{release.PluginId}:{release.Version}", new { releaseId, licenseId, installKey }, ClientIp);
        await _context.SaveChangesAsync(ct);

        var fileName = string.IsNullOrEmpty(release.FileName) ? $"plugin-{release.Version}.zip" : release.FileName;
        return File(bytes, "application/zip", fileName);
    }

    [HttpGet("changelog")]
    public async Task<IActionResult> Changelog([FromQuery] string slug, [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        var releases = await _context.PluginReleases.AsNoTracking()
            .Where(r => r.Plugin.Slug == slug && r.Status == PluginReleaseStatus.Published)
            .OrderByDescending(r => r.CreatedOn)
            .Take(limit)
            .Select(r => new { version = r.Version, changelog = r.Changelog, released_on = r.CreatedOn })
            .ToListAsync(ct);
        return Ok(new { releases });
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] PluginHeartbeatRequest req, CancellationToken ct)
    {
        if (_replayGuard.Check(req.Timestamp, req.Nonce) is { } replay)
            return Unauthorized(new { error = replay });

        var res = await _updates.ResolveLicenseAsync(req.Slug, req.LicenseKey, req.Domain, requireActive: true, ct);
        if (res.Error is not null || res.License is null)
            return Unauthorized(new { error = res.Error ?? "invalid_license" });

        await _licenses.UpsertInstallationAsync(res.License, req.InstallKey,
            PluginUpdateService.NormalizeDomain(req.Domain),
            req.PluginVersion, req.WpVersion, req.WcVersion, req.PhpVersion,
            req.ApiLatencyMs, req.RecentErrors?.GetRawText(), ct);
        await _context.SaveChangesAsync(ct);

        var flags = await _updates.GetFlagsAsync(res.License.PluginId, ct);
        return Ok(new { ok = true, flags });
    }

    [HttpPost("telemetry")]
    public async Task<IActionResult> Telemetry([FromBody] PluginTelemetryRequest req, CancellationToken ct)
    {
        if (_replayGuard.Check(req.Timestamp, req.Nonce) is { } replay)
            return Unauthorized(new { error = replay });

        var res = await _updates.ResolveLicenseAsync(req.Slug, req.LicenseKey, req.Domain, requireActive: true, ct);
        if (res.Error is not null || res.License is null)
            return Unauthorized(new { error = res.Error ?? "invalid_license" });

        var installation = await _context.PluginInstallations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.LicenseId == res.License.LicenseId && i.InstallKey == req.InstallKey, ct);

        var now = DateTime.UtcNow;
        var accepted = 0;
        foreach (var e in req.Events.Take(100)) // hard cap per batch
        {
            if (string.IsNullOrWhiteSpace(e.EventType)) continue;
            DateTime occurred = now;
            if (!string.IsNullOrEmpty(e.OccurredAt) &&
                DateTime.TryParse(e.OccurredAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                occurred = parsed;
            }
            _context.PluginTelemetryEvents.Add(new PluginTelemetryEvent
            {
                InstallationId = installation?.InstallationId,
                PluginId = res.License.PluginId,
                EventType = e.EventType.Length > 100 ? e.EventType[..100] : e.EventType,
                PayloadJson = e.Payload?.GetRawText(),
                OccurredOn = occurred,
                ReceivedOn = now,
            });
            accepted++;
        }
        await _context.SaveChangesAsync(ct);
        return Accepted(new { accepted });
    }
}
