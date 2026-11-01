namespace DelicateCouriers.ApiService.Features.WooCommerce;

/// <summary>
/// Joining a store's base URL to a WooCommerce REST path.
///
/// Its own type because getting it wrong is silent. A store living in a subfolder —
/// honeybeebaker.co.za/staging — had its REST calls sent to honeybeebaker.co.za, because a
/// request path beginning "/" is an absolute path and replaces the base's path entirely, and
/// a base without a trailing slash loses its last segment anyway. Neither throws. The call
/// simply asks a different WordPress install, which answers plausibly, and the order it was
/// asked about appears not to exist.
/// </summary>
public static class WooUrl
{
    /// <summary>The store's base, in a shape a relative path can be resolved against.</summary>
    public static Uri Base(string storeUrl) => new(storeUrl.TrimEnd('/') + "/");

    /// <summary>A REST path with any leading slash removed, so the base's own path survives.</summary>
    public static string Relative(string path) => path.TrimStart('/');

    /// <summary>Where a REST call against this store actually goes.</summary>
    public static Uri Resolve(string storeUrl, string path) => new(Base(storeUrl), Relative(path));
}
