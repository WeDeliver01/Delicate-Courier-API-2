using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Packaging;

/// <summary>
/// Global catalogue of default packaging types that every store inherits.
///
/// On store creation we copy every template into the store's PackageTypes
/// table. On application startup we back-fill any existing store that is
/// missing one of these templates (matched by case-insensitive Name).
///
/// The seed is intentionally additive and idempotent: it never updates or
/// removes a store's existing rows, so stores can freely customise / delete
/// individual entries without them coming back.
/// </summary>
public static class DefaultPackageTypes
{
    public sealed record Template(
        string Name,
        int LengthCm,
        int WidthCm,
        int HeightCm,
        decimal DefaultWeightKg,
        int SortOrder);

    public static readonly IReadOnlyList<Template> All = new List<Template>
    {
        new("Xsmall Cake Box",       20, 20, 28,  1m,  1),
        new("Custom Dessert Box",    30, 30, 20, 20m,  2),
        new("Small Cake Box",        25, 25, 45,  2m,  3),
        new("Medium Cake Box",       30, 30, 47,  3m,  4),
        new("Medium Wedding Cake",   35, 35, 40,  3m,  5),
        new("Deli Cake Box",         23, 23, 15,  1m,  6),
        new("Medium Cheesecake",     20, 20,  8,  2m,  7),
        new("Large Cheesecake Box",  25, 25, 10,  3m,  8),
        new("Cookie Box of 6",       24, 16, 11,  2m,  9),
        new("Cookie Box of 12",      24, 24, 12,  4m, 10),
        new("Cake Tasting Box",      16, 12,  3,  1m, 11),
        new("Small Tall Cake Box",   28, 44, 26,  5m, 12),
        new("Med Tall Cake Box",     32, 33, 46, 10m, 13),
        new("Flattery",              14, 34, 26,  8m, 14),
        new("xSmall",                24, 26, 21,  1m, 15),
        new("Lunchie",                9, 15, 16,  1m, 16),
        new("Baton Box",             57, 37, 15,  5m, 17),
        new("Cookie Cake",           24, 30, 12,  2m, 18),
        new("12 Cupcakes",           24, 33,  8,  2m, 19),
        new("6 Cupcakes",            24, 17,  8,  1m, 20),
        new("Small Cheesecake",      20, 20,  8,  2m, 21),
        new("Deli Cake",             23, 23, 10,  1m, 22),
        new("Macarons",              23, 23,  5,  1m, 23),
        new("4 Cupcakes",            16, 17,  6,  1m, 24),
        new("Small Wedding Cake",    27, 27, 25,  5m, 25),
        new("Med Wedding Cake",      37, 35, 35, 10m, 26),
        new("Large Wedding Cake",    45, 45, 45, 18m, 27),
        new("Small White Cake Box",  32, 26, 26,  5m, 28),
        new("Med White Cake Box",    32, 30, 33, 10m, 29),
    };

    /// <summary>
    /// Adds any missing default templates (matched case-insensitively by
    /// Name) to the given store. Returns the number of rows inserted.
    /// Caller is responsible for SaveChangesAsync.
    /// </summary>
    public static async Task<int> EnsureForStoreAsync(
        AppDbContext db,
        int storeId,
        string createdBy = "system",
        CancellationToken ct = default)
    {
        var existing = await db.PackageTypes
            .IgnoreQueryFilters()
            .Where(p => p.StoreID == storeId)
            .Select(p => p.Name)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        var inserted = 0;
        foreach (var t in All)
        {
            if (existingSet.Contains(t.Name)) continue;

            db.PackageTypes.Add(new PackageType
            {
                StoreID = storeId,
                Name = t.Name,
                Description = null,
                LengthCm = t.LengthCm,
                WidthCm = t.WidthCm,
                HeightCm = t.HeightCm,
                DefaultWeightKg = t.DefaultWeightKg,
                MaxWeightKg = null,
                IsDefault = false,
                IsActive = true,
                SortOrder = t.SortOrder,
                CreatedOn = DateTime.UtcNow,
                CreatedBy = createdBy,
            });
            inserted++;
        }

        return inserted;
    }
}
