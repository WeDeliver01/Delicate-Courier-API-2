using DelicateCouriers.ApiService.Features.WooCommerce;

namespace DelicateCouriers.Tests;

/// <summary>
/// Where a Woo REST call actually goes.
///
/// These exist because the failure is silent. A store in a subfolder had its REST calls sent
/// to the site root: nothing threw, the wrong WordPress answered, and the order the platform
/// asked about looked as though it did not exist — so a real order sat unbooked, flagged for
/// manual review, with the log saying only "Woo REST returned no order".
/// </summary>
public class WooUrlTests
{
    [Fact]
    public void A_store_in_a_subfolder_keeps_its_subfolder()
    {
        var url = WooUrl.Resolve("https://honeybeebaker.co.za/staging", "/wp-json/wc/v3/orders/42");
        Assert.Equal("https://honeybeebaker.co.za/staging/wp-json/wc/v3/orders/42", url.ToString());
    }

    [Fact]
    public void A_trailing_slash_on_the_store_url_changes_nothing()
    {
        Assert.Equal(
            WooUrl.Resolve("https://honeybeebaker.co.za/staging", "/wp-json/wc/v3/orders/42"),
            WooUrl.Resolve("https://honeybeebaker.co.za/staging/", "/wp-json/wc/v3/orders/42"));
    }

    [Fact]
    public void A_store_at_the_root_is_unaffected()
    {
        var url = WooUrl.Resolve("https://example.co.za", "/wp-json/wc/v3/orders/42");
        Assert.Equal("https://example.co.za/wp-json/wc/v3/orders/42", url.ToString());
    }

    [Fact]
    public void A_path_without_a_leading_slash_resolves_the_same_way()
    {
        Assert.Equal(
            WooUrl.Resolve("https://honeybeebaker.co.za/staging", "/wp-json/wc/v3/orders/42"),
            WooUrl.Resolve("https://honeybeebaker.co.za/staging", "wp-json/wc/v3/orders/42"));
    }

    [Fact]
    public void A_query_string_survives()
    {
        var url = WooUrl.Resolve("https://honeybeebaker.co.za/staging", "/wp-json/wc/v3/orders?per_page=1");
        Assert.Equal(
            "https://honeybeebaker.co.za/staging/wp-json/wc/v3/orders?per_page=1", url.ToString());
    }

    [Fact]
    public void A_deeper_subfolder_survives_too()
    {
        var url = WooUrl.Resolve("https://example.co.za/shops/bakery", "/wp-json/wc/v3/system_status");
        Assert.Equal("https://example.co.za/shops/bakery/wp-json/wc/v3/system_status", url.ToString());
    }
}
