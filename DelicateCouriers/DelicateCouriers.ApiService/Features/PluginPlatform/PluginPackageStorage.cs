using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// Storage abstraction for release ZIPs. The current implementation keeps
/// bytes in the plugin_releases.PackageData bytea column (plugin ZIPs are
/// a few hundred KB — well within Postgres comfort). Swapping to object
/// storage later only requires a new implementation of this interface and
/// a different StorageKey scheme; nothing else changes.
/// </summary>
public interface IPluginPackageStorage
{
    /// <summary>Attach the package bytes to a (not yet saved) release and return the storage key.</summary>
    string Attach(PluginReleaseWriteModel release, byte[] bytes);

    /// <summary>Stream the package bytes for a release, or null if missing.</summary>
    Task<byte[]?> ReadAsync(int releaseId, CancellationToken ct);
}

/// <summary>Minimal write-side view so the storage impl doesn't need the full entity.</summary>
public class PluginReleaseWriteModel
{
    public required Domain.Entities.PluginRelease Release { get; init; }
}

public class DbPluginPackageStorage : IPluginPackageStorage
{
    private readonly AppDbContext _context;

    public DbPluginPackageStorage(AppDbContext context) => _context = context;

    public string Attach(PluginReleaseWriteModel model, byte[] bytes)
    {
        model.Release.PackageData = bytes;
        model.Release.FileSizeBytes = bytes.LongLength;
        // Key is symbolic for the DB backend — the row itself is the blob.
        return "db:plugin_releases.PackageData";
    }

    public async Task<byte[]?> ReadAsync(int releaseId, CancellationToken ct)
    {
        return await _context.PluginReleases
            .Where(r => r.ReleaseId == releaseId)
            .Select(r => r.PackageData)
            .FirstOrDefaultAsync(ct);
    }
}
