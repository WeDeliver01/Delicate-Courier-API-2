using Microsoft.AspNetCore.DataProtection;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// Issues and validates short-lived signed download tokens. Built on
/// ASP.NET Data Protection (key ring persisted in Postgres), using the
/// time-limited protector so expiry is enforced cryptographically — no
/// token table, nothing to clean up. Lifetime is 60 seconds: the plugin
/// requests the URL and immediately downloads.
/// </summary>
public class PluginDownloadTokenService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);
    private const string Purpose = "PluginPlatform.DownloadToken.v1";

    private readonly ITimeLimitedDataProtector _protector;

    public PluginDownloadTokenService(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    }

    public string Create(int releaseId, int licenseId, string installKey)
        => _protector.Protect($"{releaseId}|{licenseId}|{installKey}", TokenLifetime);

    public bool TryValidate(string token, out int releaseId, out int licenseId, out string installKey)
    {
        releaseId = 0;
        licenseId = 0;
        installKey = string.Empty;
        try
        {
            var payload = _protector.Unprotect(token);
            var parts = payload.Split('|', 3);
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out releaseId)) return false;
            if (!int.TryParse(parts[1], out licenseId)) return false;
            installKey = parts[2];
            return true;
        }
        catch
        {
            // Expired, tampered, or issued under a rotated-away key.
            return false;
        }
    }
}
