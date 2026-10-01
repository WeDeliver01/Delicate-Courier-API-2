using DelicateCouriers.ApiService.Infrastructure;
using Xunit;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Woo plugins send order timestamps as bare wall-clock time in the WordPress
/// site's configured timezone, which differs per merchant (some UTC, some
/// SAST). Stamping the raw value as UTC rendered some stores' order times two
/// hours ahead in the UI. These tests pin the arrival-time-based inference.
/// </summary>
public class StoreLocalTimeTests
{
    private static readonly DateTime Arrival = new(2026, 7, 13, 11, 14, 30, DateTimeKind.Utc);
    private static readonly DateTime Fallback = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Bare_timestamp_from_sast_site_is_shifted_back_to_utc()
    {
        // Site clock reads 13:14 SAST; webhook arrives at 11:14:30 UTC.
        var utc = StoreLocalTime.ParseToUtc("2026-07-13 13:14:00", Arrival, Fallback);

        Assert.Equal(new DateTime(2026, 7, 13, 11, 14, 0, DateTimeKind.Utc), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void Bare_timestamp_from_utc_site_is_kept_as_utc()
    {
        // Site clock reads 11:14 (already UTC); webhook arrives at 11:14:30 UTC.
        var utc = StoreLocalTime.ParseToUtc("2026-07-13 11:14:00", Arrival, Fallback);

        Assert.Equal(new DateTime(2026, 7, 13, 11, 14, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Stale_bare_timestamp_defaults_to_utc_interpretation()
    {
        // An order.updated replaying a creation date from days ago — no
        // reliable inference; historical treat-as-UTC behaviour is preserved.
        var utc = StoreLocalTime.ParseToUtc("2026-07-01 09:00:00", Arrival, Fallback);

        Assert.Equal(new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Delayed_webhook_from_sast_site_still_picks_sast_within_an_hour()
    {
        // Retry delivered 50 minutes after checkout: SAST candidate is 50min
        // from arrival, UTC candidate is 70min — SAST still wins.
        var arrival = new DateTime(2026, 7, 13, 12, 4, 0, DateTimeKind.Utc);

        var utc = StoreLocalTime.ParseToUtc("2026-07-13 13:14:00", arrival, Fallback);

        Assert.Equal(new DateTime(2026, 7, 13, 11, 14, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Webhook_delayed_beyond_an_hour_falls_back_to_utc_interpretation()
    {
        // Documented limitation: if delivery is delayed >1h, the UTC
        // candidate is closer to arrival and wins, reintroducing the skew
        // for that one order. Acceptable for outage-length retry delays.
        var arrival = new DateTime(2026, 7, 13, 12, 30, 0, DateTimeKind.Utc);

        var utc = StoreLocalTime.ParseToUtc("2026-07-13 13:14:00", arrival, Fallback);

        Assert.Equal(new DateTime(2026, 7, 13, 13, 14, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Iso_timestamp_with_explicit_offset_is_honoured()
    {
        // Shopify format: ISO-8601 with offset — no inference needed.
        var utc = StoreLocalTime.ParseToUtc("2026-07-13T13:14:00+02:00", Arrival, Fallback);

        Assert.Equal(new DateTime(2026, 7, 13, 11, 14, 0, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void Utc_timestamp_with_z_suffix_is_unchanged()
    {
        var utc = StoreLocalTime.ParseToUtc("2026-07-13T11:14:00Z", Arrival, Fallback);

        Assert.Equal(new DateTime(2026, 7, 13, 11, 14, 0, DateTimeKind.Utc), utc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Missing_or_invalid_input_returns_fallback(string? input)
    {
        Assert.Equal(Fallback, StoreLocalTime.ParseToUtc(input, Arrival, Fallback));
    }
}
