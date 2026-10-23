using DelicateCouriers.Integration.Merchants;
using DelicateCouriers.Integration.Storefront;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// Matching a shopper's choice back to a merchant's service level.
///
/// A merchant offering next-day and same-day has two prices at checkout, and the shopper picks.
/// Getting this wrong is not a crash — it is a customer who paid for same-day being quietly
/// booked as next-day, which nobody notices until the parcel is late.
///
/// The link between the two systems is a string: the plugin names each rate
/// <c>delicate_courier_platform_&lt;code&gt;</c> in lowercase, and WooCommerce hands that id back
/// on the order. These tests pin that convention from both ends.
/// </summary>
public class ServiceLevelTests
{
    private static Merchant WithLevels(params (string Code, string Engine)[] levels) => new()
    {
        StoreId = 1,
        Name = "Honey Bee Baker",
        EngineAccountRef = "store-1",
        ServiceLevels = levels.Select(l => new MerchantServiceLevel
        {
            CheckoutCode = l.Code,
            CheckoutLabel = l.Code,
            EngineCode = l.Engine,
            EstimatedDeliveryDays = 1,
        }).ToList(),
    };

    [Fact]
    public void The_chosen_level_is_recognised_from_the_woo_method_id()
    {
        var merchant = WithLevels(("STD", "standard"), ("EXP", "express"));

        var level = Fulfillment.MatchServiceLevel(
            merchant, new ChosenShippingMethod("delicate_courier_platform_exp", "Express"));

        Assert.NotNull(level);
        Assert.Equal("express", level!.EngineCode);
    }

    [Fact]
    public void Case_does_not_decide_what_the_customer_gets()
    {
        // The plugin lowercases the code when it builds the id; the merchant types it however
        // they like. A customer's delivery must not turn on that difference.
        var merchant = WithLevels(("Std", "standard"));

        var level = Fulfillment.MatchServiceLevel(
            merchant, new ChosenShippingMethod("DELICATE_COURIER_PLATFORM_STD", "Standard"));

        Assert.NotNull(level);
        Assert.Equal("standard", level!.EngineCode);
    }

    [Fact]
    public void A_longer_code_is_not_matched_by_a_shorter_one()
    {
        // "STD" must not claim an order that chose "STDPLUS". Both are real options a merchant
        // might list, and the prefix would silently downgrade the dearer one.
        var merchant = WithLevels(("STD", "standard"), ("STDPLUS", "standard-plus"));

        var level = Fulfillment.MatchServiceLevel(
            merchant, new ChosenShippingMethod("delicate_courier_platform_stdplus", "Standard Plus"));

        Assert.NotNull(level);
        Assert.Equal("standard-plus", level!.EngineCode);
    }

    [Fact]
    public void An_unknown_method_matches_nothing_rather_than_guessing()
    {
        // The caller decides what to do about it. Returning the first level from here would
        // hide the fact that nothing actually matched.
        var merchant = WithLevels(("STD", "standard"));

        Assert.Null(Fulfillment.MatchServiceLevel(
            merchant, new ChosenShippingMethod("flat_rate:3", "Flat rate")));
    }

    [Fact]
    public void A_storefront_that_could_not_be_asked_matches_nothing()
    {
        var merchant = WithLevels(("STD", "standard"));

        Assert.Null(Fulfillment.MatchServiceLevel(merchant, null));
        Assert.Null(Fulfillment.MatchServiceLevel(merchant, new ChosenShippingMethod(null, null)));
    }

    [Fact]
    public void A_level_with_no_code_never_matches()
    {
        // An empty code would otherwise match every id that ends in an underscore, which is
        // all of them.
        var merchant = WithLevels(("", "standard"));

        Assert.Null(Fulfillment.MatchServiceLevel(
            merchant, new ChosenShippingMethod("delicate_courier_platform_std", "Standard")));
    }
}
