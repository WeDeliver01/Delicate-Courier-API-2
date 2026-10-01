using System;
using System.Globalization;

namespace DelicateCouriers.ApiService.Infrastructure;

/// <summary>
/// Converts store-reported order timestamps to UTC for persistence.
///
/// WooCommerce (its REST `date_created` field and the value our checkout
/// plugins send, formatted `Y-m-d H:i:s`) reports order dates in the
/// WordPress SITE's configured timezone with no offset information — and that
/// setting differs per merchant (observed in production: some sites run on
/// SAST/UTC+2, others on UTC). Blindly stamping the wall-clock value as UTC
/// made some stores' order times render two hours ahead in the UI, while
/// assuming SAST for everyone would break the UTC-configured sites.
///
/// For plugin webhooks we can disambiguate reliably: the webhook arrives
/// within seconds of checkout, so we interpret a bare timestamp under each
/// plausible zone (UTC and Africa/Johannesburg) and keep the candidate whose
/// UTC value lies closest to the arrival time.
///
/// Timestamps with explicit offset information (e.g. Shopify's ISO-8601
/// `created_at` like 2026-07-13T12:14:00+02:00, or a trailing `Z`) are
/// honoured as-is.
/// </summary>
public static class StoreLocalTime
{
    // South Africa Standard Time is UTC+2 year-round with no daylight saving,
    // so a fixed-offset zone is exact. We still prefer the OS tzdata entry
    // when available, but production containers may ship without tzdata
    // (FindSystemTimeZoneById then throws a file-not-found deep inside the
    // runtime, which as a static-initializer failure would poison every
    // order webhook), so we must never hard-depend on it.
    private static readonly TimeZoneInfo SouthAfrica = CreateSouthAfricaZone();

    private static TimeZoneInfo CreateSouthAfricaZone()
    {
        foreach (var id in new[] { "Africa/Johannesburg", "South Africa Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
                // tzdata missing or id unknown on this platform — try next / fall back.
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "SAST", TimeSpan.FromHours(2),
            "South Africa Standard Time", "South Africa Standard Time");
    }

    /// <summary>
    /// Parses a store-reported timestamp string and returns it as UTC.
    /// Strings carrying offset information are converted from that offset.
    /// Bare timestamps are interpreted under both UTC and SAST and the
    /// candidate closest to <paramref name="receivedAtUtc"/> (the webhook
    /// arrival time) wins. Returns <paramref name="fallbackUtc"/> when the
    /// string is missing or invalid.
    /// </summary>
    public static DateTime ParseToUtc(string? value, DateTime receivedAtUtc, DateTime fallbackUtc)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallbackUtc;

        // RoundtripKind keeps bare timestamps as Kind=Unspecified, maps a
        // trailing Z to Kind=Utc, and converts explicit offsets to Kind=Local
        // (machine-zone adjusted, which ToUniversalTime undoes).
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
            return fallbackUtc;

        if (parsed.Kind == DateTimeKind.Utc)
            return parsed;
        if (parsed.Kind == DateTimeKind.Local)
            return parsed.ToUniversalTime();

        var asUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        var asSast = TimeZoneInfo.ConvertTimeToUtc(parsed, SouthAfrica);

        var utcDistance = (asUtc - receivedAtUtc).Duration();
        var sastDistance = (asSast - receivedAtUtc).Duration();

        // For stale events (e.g. an order.updated replaying an old order's
        // creation date) both candidates are far from `now` and the UTC
        // interpretation wins the comparison, which preserves the historical
        // behaviour rather than inventing an offset we cannot verify.
        return sastDistance < utcDistance ? asSast : asUtc;
    }
}
