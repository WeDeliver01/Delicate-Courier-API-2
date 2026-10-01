using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.PluginPlatform;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.PluginPlatform;

/// <summary>
/// Unit tests for the Plugin Platform module: version comparison, rollout
/// gating (stages + domain rules + deterministic bucketing), license
/// resolution/activation, and best-release selection.
/// </summary>
public class PluginPlatformTests
{
    // ---------------------------------------------------------- versions

    [Theory]
    [InlineData("2.4.5", "2.4.4", 1)]
    [InlineData("2.4.5", "2.4.5", 0)]
    [InlineData("2.4.5", "2.4.6", -1)]
    [InlineData("2.10.0", "2.9.9", 1)]
    [InlineData("3.0", "2.99.99", 1)]
    [InlineData("2.4", "2.4.0", 0)]
    [InlineData("2.5.0-beta", "2.5.0", -1)]
    public void Compare_orders_versions(string a, string b, int expectedSign)
    {
        var result = PluginVersionHelper.Compare(a, b);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData(null, "6.0", true)]        // no constraint
    [InlineData("6.4", "6.5.1", true)]
    [InlineData("6.4", "6.3", false)]
    [InlineData("7.4", null, false)]       // can't verify => not compatible
    [InlineData("8.1", "8.1.27", true)]
    public void MeetsMinimum_enforces_constraints(string? min, string? actual, bool expected)
    {
        Assert.Equal(expected, PluginVersionHelper.MeetsMinimum(min, actual));
    }

    [Fact]
    public void Bucket_is_deterministic_and_in_range()
    {
        var a = PluginVersionHelper.Bucket("install-abc", 7);
        Assert.Equal(a, PluginVersionHelper.Bucket("install-abc", 7));
        Assert.InRange(a, 0, 99);
        // Different release id reshuffles (statistically certain to differ for some key).
        var reshuffled = Enumerable.Range(0, 50)
            .Any(i => PluginVersionHelper.Bucket($"key-{i}", 1) != PluginVersionHelper.Bucket($"key-{i}", 2));
        Assert.True(reshuffled);
    }

    // ----------------------------------------------------------- rollout

    private static PluginRelease Release(string stage, params PluginRolloutRule[] rules) => new()
    {
        ReleaseId = 42,
        Version = "2.0.0",
        RolloutStage = stage,
        Status = PluginReleaseStatus.Published,
        RolloutRules = rules.ToList(),
    };

    private static PluginLicense License(bool @internal = false, bool beta = false) => new()
    {
        LicenseId = 1,
        Status = PluginLicenseStatus.Active,
        IsInternal = @internal,
        IsBeta = beta,
    };

    [Fact]
    public void Rollout_internal_stage_only_internal_licenses()
    {
        var release = Release(PluginRolloutStage.Internal);
        Assert.True(PluginUpdateService.PassesRollout(release, License(@internal: true), "k", "a.com"));
        Assert.False(PluginUpdateService.PassesRollout(release, License(beta: true), "k", "a.com"));
        Assert.False(PluginUpdateService.PassesRollout(release, License(), "k", "a.com"));
    }

    [Fact]
    public void Rollout_beta_stage_includes_internal_and_beta()
    {
        var release = Release(PluginRolloutStage.Beta);
        Assert.True(PluginUpdateService.PassesRollout(release, License(@internal: true), "k", "a.com"));
        Assert.True(PluginUpdateService.PassesRollout(release, License(beta: true), "k", "a.com"));
        Assert.False(PluginUpdateService.PassesRollout(release, License(), "k", "a.com"));
    }

    [Fact]
    public void Rollout_pct100_includes_everyone()
    {
        Assert.True(PluginUpdateService.PassesRollout(Release(PluginRolloutStage.Percent100), License(), "k", "a.com"));
    }

    [Fact]
    public void Rollout_pct5_gates_by_bucket()
    {
        var release = Release(PluginRolloutStage.Percent5);
        var license = License();
        // Find keys on both sides of the 5% line and assert the gate matches the bucket.
        var inKey = Enumerable.Range(0, 500).Select(i => $"key-{i}")
            .First(k => PluginVersionHelper.Bucket(k, release.ReleaseId) < 5);
        var outKey = Enumerable.Range(0, 500).Select(i => $"key-{i}")
            .First(k => PluginVersionHelper.Bucket(k, release.ReleaseId) >= 5);
        Assert.True(PluginUpdateService.PassesRollout(release, license, inKey, "a.com"));
        Assert.False(PluginUpdateService.PassesRollout(release, license, outKey, "a.com"));
        // Internal/beta bypass the percentage gate.
        Assert.True(PluginUpdateService.PassesRollout(release, License(@internal: true), outKey, "a.com"));
    }

    [Fact]
    public void Rollout_domain_rules_override_stage()
    {
        var deny = Release(PluginRolloutStage.Percent100,
            new PluginRolloutRule { RuleType = "deny_domain", Value = "bad.com" });
        Assert.False(PluginUpdateService.PassesRollout(deny, License(@internal: true), "k", "bad.com"));

        var allow = Release(PluginRolloutStage.Internal,
            new PluginRolloutRule { RuleType = "allow_domain", Value = "vip.com" });
        Assert.True(PluginUpdateService.PassesRollout(allow, License(), "k", "vip.com"));
        Assert.False(PluginUpdateService.PassesRollout(allow, License(), "k", "other.com"));
    }

    // ------------------------------------------------------------ domain

    [Theory]
    [InlineData("https://www.Example.com/shop", "example.com")]
    [InlineData("example.com:8080", "example.com")]
    [InlineData("http://sub.example.com/", "sub.example.com")]
    [InlineData("  Example.COM  ", "example.com")]
    public void NormalizeDomain_strips_noise(string input, string expected)
    {
        Assert.Equal(expected, PluginUpdateService.NormalizeDomain(input));
    }

    [Fact]
    public void GenerateLicenseKey_has_expected_shape_and_uniqueness()
    {
        var key = PluginLicenseService.GenerateLicenseKey();
        Assert.Matches(@"^DCP-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", key);
        Assert.NotEqual(key, PluginLicenseService.GenerateLicenseKey());
    }

    // ------------------------------------------------------- replay guard

    [Fact]
    public void ReplayGuard_rejects_malformed_timestamps_without_throwing()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var guard = new PluginReplayGuard(cache);

        Assert.Equal("timestamp_out_of_window", guard.Check(long.MaxValue, "n1"));
        Assert.Equal("timestamp_out_of_window", guard.Check(long.MinValue, "n2"));
        Assert.Equal("timestamp_out_of_window", guard.Check(0, "n3")); // 1970 — far outside window
    }

    [Fact]
    public void ReplayGuard_accepts_fresh_then_rejects_replayed_nonce()
    {
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var guard = new PluginReplayGuard(cache);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Null(guard.Check(now, "nonce-x"));
        Assert.Equal("nonce_replayed", guard.Check(now, "nonce-x"));
        Assert.Null(guard.Check(null, null)); // legacy client without nonce
        Assert.Equal("timestamp_and_nonce_required_together", guard.Check(now, null));
    }

    // -------------------------------------------------- service (in-mem DB)

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"plugin-platform-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options, httpContextAccessor: null);
    }

    private static async Task<(AppDbContext ctx, PluginProduct plugin, PluginLicense license)> SeedAsync(
        bool activated = true, string? boundDomain = "shop.com")
    {
        var ctx = CreateContext();
        var plugin = new PluginProduct { Slug = "delicate-courier-platform", Name = "DCP", CreatedBy = "test" };
        ctx.PluginProducts.Add(plugin);
        await ctx.SaveChangesAsync();
        var license = new PluginLicense
        {
            PluginId = plugin.PluginId,
            LicenseKey = "DCP-TEST-TEST-TEST-TEST",
            Domain = boundDomain,
            Status = activated ? PluginLicenseStatus.Active : PluginLicenseStatus.Inactive,
            CreatedBy = "test",
        };
        ctx.PluginLicenses.Add(license);
        await ctx.SaveChangesAsync();
        return (ctx, plugin, license);
    }

    [Fact]
    public async Task ResolveLicense_rejects_wrong_key_revoked_expired_and_domain_mismatch()
    {
        var (ctx, _, license) = await SeedAsync();
        var svc = new PluginUpdateService(ctx);

        Assert.Equal("invalid_license", (await svc.ResolveLicenseAsync("delicate-courier-platform", "DCP-XXXX-XXXX-XXXX-XXXX", "shop.com", true, default)).Error);
        Assert.Equal("unknown_plugin", (await svc.ResolveLicenseAsync("nope", license.LicenseKey, "shop.com", true, default)).Error);
        Assert.Equal("domain_mismatch", (await svc.ResolveLicenseAsync("delicate-courier-platform", license.LicenseKey, "other.com", true, default)).Error);
        Assert.Null((await svc.ResolveLicenseAsync("delicate-courier-platform", license.LicenseKey, "https://www.shop.com/", true, default)).Error);

        license.ExpiresOn = DateTime.UtcNow.AddDays(-1);
        await ctx.SaveChangesAsync();
        Assert.Equal("license_expired", (await svc.ResolveLicenseAsync("delicate-courier-platform", license.LicenseKey, "shop.com", true, default)).Error);

        license.ExpiresOn = null;
        license.Status = PluginLicenseStatus.Revoked;
        await ctx.SaveChangesAsync();
        Assert.Equal("license_revoked", (await svc.ResolveLicenseAsync("delicate-courier-platform", license.LicenseKey, "shop.com", true, default)).Error);
    }

    [Fact]
    public async Task Activate_binds_domain_and_creates_installation()
    {
        var (ctx, _, license) = await SeedAsync(activated: false, boundDomain: null);
        var audit = new PluginAuditService(ctx, NullLogger<PluginAuditService>.Instance);
        var svc = new PluginLicenseService(ctx, audit);

        var err = await svc.ActivateAsync(license, "https://www.Shop.com", "install-1", "2.4.5", "6.5", "9.0", "8.2", null, default);
        Assert.Null(err);
        Assert.Equal("shop.com", license.Domain);
        Assert.Equal(PluginLicenseStatus.Active, license.Status);
        var install = Assert.Single(ctx.PluginInstallations);
        Assert.Equal("shop.com", install.Domain);
        Assert.Equal("2.4.5", install.PluginVersion);

        // Second activation from another domain is rejected.
        Assert.Equal("domain_mismatch", await svc.ActivateAsync(license, "evil.com", "install-2", null, null, null, null, null, default));

        // Re-activation from the same domain upserts (no duplicate installation).
        Assert.Null(await svc.ActivateAsync(license, "shop.com", "install-1", "2.4.6", null, null, null, null, default));
        Assert.Single(ctx.PluginInstallations);
        Assert.Equal("2.4.6", ctx.PluginInstallations.Single().PluginVersion);
    }

    [Fact]
    public async Task ResolveBestRelease_picks_highest_compatible_published_release()
    {
        var (ctx, plugin, license) = await SeedAsync();
        ctx.PluginReleases.AddRange(
            NewRelease(plugin.PluginId, "1.9.0", PluginRolloutStage.Percent100),
            NewRelease(plugin.PluginId, "2.1.0", PluginRolloutStage.Percent100),
            NewRelease(plugin.PluginId, "2.2.0", PluginRolloutStage.Percent100, status: PluginReleaseStatus.Withdrawn),
            NewRelease(plugin.PluginId, "2.3.0", PluginRolloutStage.Percent100, minPhp: "9.9"),
            NewRelease(plugin.PluginId, "3.0.0", PluginRolloutStage.Internal));
        await ctx.SaveChangesAsync();

        var svc = new PluginUpdateService(ctx);
        var best = await svc.ResolveBestReleaseAsync(license, "install-1", "shop.com", "2.0.0", "6.5", "9.0", "8.2", default);

        // 1.9 too old, 2.2 withdrawn, 2.3 needs php 9.9, 3.0 internal-only → 2.1.0 wins.
        Assert.NotNull(best);
        Assert.Equal("2.1.0", best!.Version);

        // Internal license sees 3.0.0.
        license.IsInternal = true;
        var bestInternal = await svc.ResolveBestReleaseAsync(license, "install-1", "shop.com", "2.0.0", "6.5", "9.0", "8.2", default);
        Assert.Equal("3.0.0", bestInternal!.Version);

        // Already up to date → null.
        license.IsInternal = false;
        Assert.Null(await svc.ResolveBestReleaseAsync(license, "install-1", "shop.com", "2.1.0", "6.5", "9.0", "8.2", default));
    }

    [Fact]
    public async Task Download_refuses_to_serve_package_whose_bytes_no_longer_match_stored_sha256()
    {
        var (ctx, plugin, license) = await SeedAsync();
        var release = NewRelease(plugin.PluginId, "2.5.0", PluginRolloutStage.Percent100);
        // Sha256 stays 'aaaa…' but the actual bytes hash to something else.
        ctx.PluginReleases.Add(release);
        ctx.PluginInstallations.Add(new PluginInstallation
        {
            LicenseId = license.LicenseId,
            InstallKey = "install-1",
            Domain = "shop.com",
        });
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx, out var tokens);
        var token = tokens.Create(release.ReleaseId, license.LicenseId, "install-1");

        var result = await controller.Download(token, default);

        var obj = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(result);
        Assert.Equal(503, obj.StatusCode);
        Assert.Contains(ctx.PluginAuditLogs, a => a.Action == "download.integrity_failure");
    }

    [Fact]
    public async Task Download_serves_package_when_bytes_match_stored_sha256()
    {
        var (ctx, plugin, license) = await SeedAsync();
        var release = NewRelease(plugin.PluginId, "2.5.0", PluginRolloutStage.Percent100);
        release.Sha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(release.PackageData!)).ToLowerInvariant();
        ctx.PluginReleases.Add(release);
        ctx.PluginInstallations.Add(new PluginInstallation
        {
            LicenseId = license.LicenseId,
            InstallKey = "install-1",
            Domain = "shop.com",
        });
        await ctx.SaveChangesAsync();

        var controller = CreateController(ctx, out var tokens);
        var token = tokens.Create(release.ReleaseId, license.LicenseId, "install-1");

        var result = await controller.Download(token, default);

        var file = Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(result);
        Assert.Equal(release.PackageData, file.FileContents);
    }

    [Fact]
    public async Task AuditLogs_pluginId_filter_scopes_entries_server_side()
    {
        var (ctx, plugin, license) = await SeedAsync();
        var other = new PluginProduct { Slug = "other-plugin", Name = "Other", CreatedBy = "test" };
        ctx.PluginProducts.Add(other);
        await ctx.SaveChangesAsync();
        var otherLicense = new PluginLicense
        {
            PluginId = other.PluginId,
            LicenseKey = "OTH-XXXX-XXXX-XXXX-XXXX",
            Status = PluginLicenseStatus.Active,
            CreatedBy = "test",
        };
        ctx.PluginLicenses.Add(otherLicense);

        ctx.PluginAuditLogs.AddRange(
            new PluginAuditLog { Action = "plugin.created", Actor = "a", EntityType = "PluginProduct", EntityRef = plugin.Slug },
            new PluginAuditLog { Action = "download.served", Actor = "a", EntityType = "PluginRelease", EntityRef = $"{plugin.PluginId}:2.5.0" },
            new PluginAuditLog { Action = "flag.upserted", Actor = "a", EntityType = "PluginFeatureFlag", EntityRef = $"{plugin.Slug}:my-flag" },
            new PluginAuditLog { Action = "license.activated", Actor = "a", EntityType = "PluginLicense", EntityRef = license.LicenseKey },
            // Entries for the other plugin — must be excluded.
            new PluginAuditLog { Action = "plugin.created", Actor = "a", EntityType = "PluginProduct", EntityRef = other.Slug },
            new PluginAuditLog { Action = "download.served", Actor = "a", EntityType = "PluginRelease", EntityRef = $"{other.PluginId}:1.0.0" },
            new PluginAuditLog { Action = "license.activated", Actor = "a", EntityType = "PluginLicense", EntityRef = otherLicense.LicenseKey },
            // Unattributable entry — excluded from per-plugin views.
            new PluginAuditLog { Action = "rollout.updated", Actor = "a", EntityType = "PluginRolloutRule", EntityRef = "17" });
        await ctx.SaveChangesAsync();

        var controller = new ApiService.Features.PluginPlatform.PluginPlatformAdminController(
            ctx, new DbPluginPackageStorage(ctx),
            new PluginAuditService(ctx, NullLogger<PluginAuditService>.Instance));

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            await controller.ListAuditLogs(action: null, pluginId: plugin.PluginId));
        var logs = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<PluginAuditLog>>(ok.Value).ToList();
        Assert.Equal(4, logs.Count);
        Assert.DoesNotContain(logs, l => l.EntityRef == other.Slug || l.EntityRef == otherLicense.LicenseKey
            || l.EntityRef == $"{other.PluginId}:1.0.0" || l.EntityType == "PluginRolloutRule");

        // Combined with an action filter.
        var okDownloads = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            await controller.ListAuditLogs(action: "download.served", pluginId: plugin.PluginId));
        var downloadLogs = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<PluginAuditLog>>(okDownloads.Value).ToList();
        Assert.Single(downloadLogs);
        Assert.Equal($"{plugin.PluginId}:2.5.0", downloadLogs[0].EntityRef);

        // Unknown plugin id → 404.
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>(
            await controller.ListAuditLogs(action: null, pluginId: 99999));
    }

    [Fact]
    public async Task Telemetry_pluginId_filter_keeps_own_and_unattributed_events()
    {
        var (ctx, plugin, _) = await SeedAsync();
        ctx.PluginTelemetryEvents.AddRange(
            new PluginTelemetryEvent { PluginId = plugin.PluginId, EventType = "booking.success" },
            new PluginTelemetryEvent { PluginId = null, EventType = "activation.error" },
            new PluginTelemetryEvent { PluginId = plugin.PluginId + 1, EventType = "other.event" });
        await ctx.SaveChangesAsync();

        var controller = new ApiService.Features.PluginPlatform.PluginPlatformAdminController(
            ctx, new DbPluginPackageStorage(ctx),
            new PluginAuditService(ctx, NullLogger<PluginAuditService>.Instance));

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
            await controller.ListTelemetry(eventType: null, pluginId: plugin.PluginId));
        var events = Assert.IsAssignableFrom<System.Collections.Generic.IEnumerable<PluginTelemetryEvent>>(ok.Value).ToList();
        Assert.Equal(2, events.Count);
        Assert.DoesNotContain(events, e => e.EventType == "other.event");
    }

    private static ApiService.Features.PluginPlatform.PluginUpdateController CreateController(
        AppDbContext ctx, out PluginDownloadTokenService tokens)
    {
        var provider = Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create("plugin-tests");
        tokens = new PluginDownloadTokenService(provider);
        var audit = new PluginAuditService(ctx, NullLogger<PluginAuditService>.Instance);
        var controller = new ApiService.Features.PluginPlatform.PluginUpdateController(
            ctx,
            new PluginUpdateService(ctx),
            new PluginLicenseService(ctx, audit),
            tokens,
            new DbPluginPackageStorage(ctx),
            new PluginReplayGuard(new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())),
            audit,
            NullLogger<ApiService.Features.PluginPlatform.PluginUpdateController>.Instance)
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            },
        };
        return controller;
    }

    private static PluginRelease NewRelease(int pluginId, string version, string stage,
        string status = PluginReleaseStatus.Published, string? minPhp = null) => new()
    {
        PluginId = pluginId,
        Version = version,
        Changelog = "test",
        Sha256 = new string('a', 64),
        StorageKey = "db:test",
        PackageData = new byte[] { 0x50, 0x4B, 0x03, 0x04 },
        FileSizeBytes = 4,
        FileName = $"dcp-{version}.zip",
        RolloutStage = stage,
        Status = status,
        MinPhpVersion = minPhp,
        CreatedBy = "test",
    };
}
