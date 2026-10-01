using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Infrastructure.Geocoding;

/// <summary>
/// Persistent cache of geocoding lookups, backed by the GeocodeCache table.
/// Stores both successful hits AND negative results so we don't hammer the
/// upstream geocoder for the same un-resolvable address on every checkout.
/// </summary>
public class GeocodeCacheStore
{
    private readonly AppDbContext _db;

    public GeocodeCacheStore(AppDbContext db)
    {
        _db = db;
    }

    public static string BuildKey(GeocodeQuery q)
    {
        // Normalize: trim, lowercase, collapse whitespace, drop empties.
        string Norm(string? s) => string.IsNullOrWhiteSpace(s)
            ? ""
            : System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
        return string.Join("|", new[] { q.Street, q.Suburb, q.City, q.Province, q.PostalCode, q.Country }.Select(Norm));
    }

    public async Task<(bool Found, GeocodeResult? Result)> TryGetAsync(string key, CancellationToken ct)
    {
        await using var cmd = _db.Database.GetDbConnection().CreateCommand();
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(ct);
        }
        cmd.CommandText = @"SELECT ""Latitude"", ""Longitude"", ""Provider"", ""Hit"" FROM ""GeocodeCache"" WHERE ""AddressKey"" = @k LIMIT 1";
        var p = cmd.CreateParameter();
        p.ParameterName = "@k";
        p.Value = key;
        cmd.Parameters.Add(p);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (false, null);

        var hit = reader.GetBoolean(3);
        if (!hit) return (true, null);
        return (true, new GeocodeResult(reader.GetDouble(0), reader.GetDouble(1), reader.GetString(2)));
    }

    public async Task SaveAsync(string key, GeocodeResult? result, string provider, CancellationToken ct)
    {
        await using var cmd = _db.Database.GetDbConnection().CreateCommand();
        if (cmd.Connection!.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(ct);
        }
        cmd.CommandText = @"
            INSERT INTO ""GeocodeCache"" (""AddressKey"", ""Latitude"", ""Longitude"", ""Provider"", ""Hit"", ""CreatedAt"")
            VALUES (@k, @lat, @lng, @prov, @hit, NOW())
            ON CONFLICT (""AddressKey"") DO UPDATE SET
                ""Latitude"" = EXCLUDED.""Latitude"",
                ""Longitude"" = EXCLUDED.""Longitude"",
                ""Provider"" = EXCLUDED.""Provider"",
                ""Hit"" = EXCLUDED.""Hit"",
                ""CreatedAt"" = NOW()";
        void Add(string n, object? v)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        Add("@k", key);
        Add("@lat", result?.Latitude);
        Add("@lng", result?.Longitude);
        Add("@prov", result?.Provider ?? provider);
        Add("@hit", result != null);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Idempotent create. Called once from Program.cs after EF migrations run.
    /// </summary>
    public static async Task EnsureTableAsync(AppDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""GeocodeCache"" (
                ""AddressKey"" TEXT PRIMARY KEY,
                ""Latitude"" DOUBLE PRECISION NULL,
                ""Longitude"" DOUBLE PRECISION NULL,
                ""Provider"" TEXT NOT NULL,
                ""Hit"" BOOLEAN NOT NULL,
                ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );", ct);
    }
}
