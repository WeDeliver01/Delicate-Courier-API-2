using System.Security.Cryptography;
using System.Text;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// Version comparison + deterministic rollout bucketing helpers.
/// Tolerant semver-ish parsing: "2.4.5", "2.4", "7.4.33", "6.4.1-beta"
/// (pre-release suffix after '-' is ignored for ordering purposes except
/// that a pre-release sorts BELOW the same plain version).
/// </summary>
public static class PluginVersionHelper
{
    /// <summary>Returns &lt;0 if a&lt;b, 0 if equal, &gt;0 if a&gt;b. Null/unparseable segments compare as 0.</summary>
    public static int Compare(string? a, string? b)
    {
        var (pa, preA) = Parse(a);
        var (pb, preB) = Parse(b);
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        // Same numeric core: a pre-release is lower than the plain version.
        if (preA && !preB) return -1;
        if (!preA && preB) return 1;
        return 0;
    }

    /// <summary>True when <paramref name="actual"/> satisfies the minimum. Null/empty min = no constraint; null actual with a min set = NOT compatible (we can't verify).</summary>
    public static bool MeetsMinimum(string? minimum, string? actual)
    {
        if (string.IsNullOrWhiteSpace(minimum)) return true;
        if (string.IsNullOrWhiteSpace(actual)) return false;
        return Compare(actual, minimum) >= 0;
    }

    /// <summary>
    /// Deterministic 0–99 bucket for percentage rollouts. Same install +
    /// release always lands in the same bucket; different releases reshuffle
    /// so the same 5% of sites aren't always the guinea pigs.
    /// </summary>
    public static int Bucket(string installKey, int releaseId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{installKey}|{releaseId}"));
        var value = BitConverter.ToUInt32(bytes, 0);
        return (int)(value % 100);
    }

    private static (int[] parts, bool preRelease) Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return (Array.Empty<int>(), false);
        var v = version.Trim();
        var pre = false;
        var dash = v.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0)
        {
            pre = v[dash] == '-';
            v = v[..dash];
        }
        var segments = v.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var digits = new string(segments[i].TakeWhile(char.IsDigit).ToArray());
            parts[i] = int.TryParse(digits, out var n) ? n : 0;
        }
        return (parts, pre);
    }
}
