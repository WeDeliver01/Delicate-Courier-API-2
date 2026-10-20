using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.Integration.Merchants;

/// <summary>
/// Merchants, read from the database and held briefly in memory.
///
/// Every checkout rate request and every status callback needs a merchant, so this is one of
/// the hottest reads in the service — a database round trip per lookup would put a query in
/// front of a shopper waiting at a checkout. The whole set is small (tens of rows, not
/// thousands), so it is cached wholesale and refreshed on a short interval.
///
/// The cache is cleared the moment anything writes, so a change made in the admin screen is
/// live immediately rather than up to the refresh interval later. The interval only matters
/// for a second instance that did not serve the write — which is why it is seconds rather
/// than minutes.
/// </summary>
public sealed class DbMerchantDirectory : IMerchantDirectory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DbMerchantDirectory> _logger;
    private readonly object _gate = new();

    private Dictionary<int, Merchant> _byStoreId = new();
    private Dictionary<string, Merchant> _byAccountId = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public DbMerchantDirectory(IServiceScopeFactory scopes, ILogger<DbMerchantDirectory> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public Merchant? Find(int storeId)
    {
        EnsureLoaded();
        lock (_gate) return _byStoreId.TryGetValue(storeId, out var m) ? m : null;
    }

    public Merchant? FindByAccountId(string? engineAccountId)
    {
        if (string.IsNullOrWhiteSpace(engineAccountId)) return null;
        EnsureLoaded();
        lock (_gate) return _byAccountId.TryGetValue(engineAccountId, out var m) ? m : null;
    }

    /// <summary>Called after any write, so an edit takes effect on the next request.</summary>
    public void Invalidate()
    {
        lock (_gate) _loadedAt = DateTimeOffset.MinValue;
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _loadedAt < CacheFor) return;
        }

        List<MerchantRecord> rows;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MerchantDbContext>();
            rows = db.Merchants.AsNoTracking().Where(m => m.IsActive).ToList();
        }
        catch (Exception ex)
        {
            // Serve the previous set rather than failing every request while the database is
            // briefly away. A stale merchant list books slightly out-of-date addresses; an
            // empty one tells every shopper there is no delivery available.
            _logger.LogError(ex, "Could not refresh merchants; serving the previous set");
            return;
        }

        var merchants = rows.Select(r => r.ToMerchant()).ToList();
        lock (_gate)
        {
            _byStoreId = merchants.ToDictionary(m => m.StoreId);
            _byAccountId = merchants
                .Where(m => !string.IsNullOrWhiteSpace(m.EngineAccountId))
                .ToDictionary(m => m.EngineAccountId!, m => m, StringComparer.OrdinalIgnoreCase);
            _loadedAt = DateTimeOffset.UtcNow;
        }
    }
}
