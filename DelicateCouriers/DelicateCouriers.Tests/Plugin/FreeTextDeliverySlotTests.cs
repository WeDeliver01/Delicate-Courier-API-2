using DelicateCouriers.ApiService.Features.Shipments;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;

namespace DelicateCouriers.Tests.Plugin;

/// <summary>
/// Free-text delivery-slot parsing (Baked By Nataleen product addon):
/// merchants capture the slot as a typed sentence on a line item, e.g.
/// "Thursday 20th August 11am delivery". The orchestration fallback must
/// extract date + time from such text so Shiplogic bookings carry the
/// customer's real requested delivery window instead of next-day defaults.
/// </summary>
public class FreeTextDeliverySlotTests
{
    private static readonly DateTime OrderDate = new(2026, 8, 14);

    private static WooCommerceOrder OrderWithLineItemText(string text) => new()
    {
        LineItems = new List<WooCommerceLineItem>
        {
            new()
            {
                MetaData = new List<WooCommerceMetaData>
                {
                    new() { Key = "pa_platter-servings", Value = System.Text.Json.JsonSerializer.SerializeToElement("platter-for-18") },
                    new() { Key = "Please indicate date, day and TIME of Pick up or delivery", Value = System.Text.Json.JsonSerializer.SerializeToElement(text) },
                },
            },
        },
    };

    [Theory]
    [InlineData("Thursday 20th August 11am delivery", "2026-08-20", "11:00")]
    [InlineData("Saturday 15th August Delivery 9.30am", "2026-08-15", "09:30")]
    [InlineData("delivery 22 aug 4:30 pm", "2026-08-22", "16:30")]
    [InlineData("Friday 21st August 17h00", "2026-08-21", "17:00")]
    [InlineData("2nd January delivery 10am", "2027-01-02", "10:00")] // rolls to next year
    [InlineData("20th August 2026", "2026-08-20", null)] // date only
    public void Parses_date_and_time_from_free_text(string text, string expectedDate, string? expectedTime)
    {
        var ok = ShipmentOrchestrationService.TryParseFreeTextDeliverySlot(
            OrderWithLineItemText(text), OrderDate, out var date, out var time);

        Assert.True(ok);
        Assert.Equal(DateTime.Parse(expectedDate), date.Date);
        Assert.Equal(expectedTime, time);
    }

    [Theory]
    [InlineData("Delivery, anytime, before 17h00")] // no date — must NOT invent one
    [InlineData("not spicy")]
    [InlineData("White is good. gold pearls ")]
    [InlineData("31st February 10am")] // impossible date
    public void Rejects_text_without_a_real_date(string text)
    {
        var ok = ShipmentOrchestrationService.TryParseFreeTextDeliverySlot(
            OrderWithLineItemText(text), OrderDate, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Ignores_internal_underscore_meta()
    {
        var order = new WooCommerceOrder
        {
            MetaData = new List<WooCommerceOrderMeta>
            {
                new() { Key = "_internal_note", Value = System.Text.Json.JsonSerializer.SerializeToElement("20th August 11am") },
            },
        };

        Assert.False(ShipmentOrchestrationService.TryParseFreeTextDeliverySlot(order, OrderDate, out _, out _));
    }
}
