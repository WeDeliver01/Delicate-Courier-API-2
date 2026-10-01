using System;
using System.Collections.Generic;

namespace DelicateCouriers.ApiService.Infrastructure;

public static class CountryNormalizer
{
    private static readonly Dictionary<string, string> CountryMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "South Africa", "ZA" }, { "ZA", "ZA" }, { "RSA", "ZA" },
            { "Namibia", "NA" }, { "NA", "NA" },
            { "Botswana", "BW" }, { "BW", "BW" },
            { "Zimbabwe", "ZW" }, { "ZW", "ZW" },
            { "Mozambique", "MZ" }, { "MZ", "MZ" },
            { "Lesotho", "LS" }, { "LS", "LS" },
            { "Swaziland", "SZ" }, { "Eswatini", "SZ" }, { "SZ", "SZ" }
        };

    public static string ToIsoCode(string? countryName)
    {
        if (string.IsNullOrWhiteSpace(countryName))
        {
            return "ZA";
        }

        return CountryMap.TryGetValue(countryName.Trim(), out var iso) ? iso : "ZA";
    }
}
