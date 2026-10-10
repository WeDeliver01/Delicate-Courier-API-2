using DelicateCouriers.Integration.Bookings;
using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Merchants;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// The rules that decide whether an order becomes a delivery.
///
/// Each of these exists because an order was once booked that should not have been. They are
/// cheap to assert and expensive to get wrong, so they are asserted one at a time.
/// </summary>
public class BookingRulesTests
{
    private static Merchant Store(params string[] triggerStatuses) => new()
    {
        StoreId = 1,
        Name = "Honey Bee Baker",
        EngineAccountRef = "store-1",
        BookingTriggerStatuses = triggerStatuses.ToList()
    };

    private static PluginOrderPayload Order(string status = "processing", string? fulfillment = null) => new()
    {
        WooOrderId = 49905,
        OrderNumber = "49905",
        Status = status,
        FulfillmentType = fulfillment
    };

    [Fact]
    public void Books_an_ordinary_delivery()
    {
        Assert.True(BookingRules.Evaluate(Store(), Order()).ShouldBook);
    }

    [Fact]
    public void Never_books_a_collect_order()
    {
        // The customer walks in and takes it. A driver sent for this one finds nothing.
        var decision = BookingRules.Evaluate(Store(), Order(fulfillment: "collect"));
        Assert.False(decision.ShouldBook);
        Assert.Equal("collect", decision.Reason);
    }

    [Fact]
    public void Collect_wins_even_on_a_status_that_would_otherwise_book()
    {
        Assert.False(BookingRules.Evaluate(Store("processing"), Order("processing", "collect")).ShouldBook);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("canceled")] // WooCommerce and its plugins disagree on the spelling
    [InlineData("refunded")]
    [InlineData("failed")]
    [InlineData("trash")]
    [InlineData("CANCELLED")]
    [InlineData("  Refunded  ")]
    public void Never_books_a_terminal_order(string status)
    {
        var decision = BookingRules.Evaluate(Store(), Order(status));
        Assert.False(decision.ShouldBook);
        Assert.StartsWith("terminal_status:", decision.Reason);
    }

    [Fact]
    public void Honours_a_merchants_own_trigger_statuses()
    {
        var merchant = Store("completed");
        Assert.False(BookingRules.Evaluate(merchant, Order("processing")).ShouldBook);
        Assert.True(BookingRules.Evaluate(merchant, Order("completed")).ShouldBook);
    }

    [Fact]
    public void A_status_outside_the_trigger_list_is_not_yet_rather_than_no()
    {
        // The plugin fires on every transition, so the same order arrives again later and
        // books then. The reason names the status so a support question has an answer.
        var decision = BookingRules.Evaluate(Store("completed"), Order("on-hold"));
        Assert.Equal("status_not_a_trigger:on-hold", decision.Reason);
    }

    [Fact]
    public void A_merchant_without_a_trigger_list_books_on_any_non_terminal_status()
    {
        Assert.True(BookingRules.Evaluate(Store(), Order("on-hold")).ShouldBook);
        Assert.True(BookingRules.Evaluate(Store(), Order("pending")).ShouldBook);
    }

    [Fact]
    public void A_null_fulfillment_type_is_an_older_plugin_doing_a_delivery()
    {
        // Older plugin versions never send the field. Treating absent as anything other than
        // "delivery" would change how every existing installation behaves.
        Assert.True(BookingRules.Evaluate(Store(), Order(fulfillment: null)).ShouldBook);
    }

    [Fact]
    public void The_customer_reference_is_the_woo_order_id_and_nothing_else()
    {
        // Both systems find a booking by this string. It is computed, never stored, so it
        // cannot drift from the order it names.
        Assert.Equal("WC-49905", BookingRules.CustomerReferenceFor(49905));
    }
}
