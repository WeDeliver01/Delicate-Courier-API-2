using System.Text.Json;
using DelicateCouriers.Integration.Bookings;
using DelicateCouriers.Integration.Engine;
using Xunit;

namespace DelicateCouriers.Integration.Tests;

/// <summary>
/// The date and time a shopper picked, on their way to becoming a delivery slot.
///
/// Every one of these is a real shape some checkout plugin writes. The reason the parsing is
/// forgiving rather than strict is that the alternative, demonstrated in production for two
/// weeks, is an order that is silently never booked.
/// </summary>
public class DeliverySlotMapperTests
{
    [Theory]
    [InlineData("2026-11-06", "2026-11-06")]
    [InlineData("2026/11/06", "2026-11-06")]
    [InlineData("06/11/2026", "2026-11-06")]
    [InlineData("6/11/2026", "2026-11-06")]
    [InlineData("06-11-2026", "2026-11-06")]
    [InlineData("6 November 2026", "2026-11-06")]
    public void ReadsTheDateShapesCheckoutsActuallyWrite(string input, string expected)
    {
        Assert.Equal(expected, DeliverySlotMapper.ParseDate(input));
    }

    [Fact]
    public void ReadsAnAmbiguousDateDayFirst()
    {
        // The whole business is in South Africa: 06/11 is the sixth of November, not the
        // eleventh of June. Reading it the other way delivers a cake five months late, and
        // trusting the machine's locale is how that happens on somebody else's laptop.
        Assert.Equal("2026-11-06", DeliverySlotMapper.ParseDate("06/11/2026"));
        Assert.Equal("2026-06-11", DeliverySlotMapper.ParseDate("11/06/2026"));
    }

    [Theory]
    [InlineData("09:00 - 11:00", "09:00")]
    [InlineData("09:00-11:00", "09:00")]
    [InlineData("09:00 – 11:00", "09:00")]
    [InlineData("9am to 11am", "09:00")]
    [InlineData("9:30 AM - 11:30 AM", "09:30")]
    [InlineData("14:30", "14:30")]
    [InlineData("2:30 PM", "14:30")]
    public void TakesTheStartOfTheWindowTheShopperWasPromised(string input, string expected)
    {
        Assert.Equal(expected, DeliverySlotMapper.ParseStartTime(input));
    }

    [Fact]
    public void BuildsTheSlotAnOrderCarries()
    {
        var slot = DeliverySlotMapper.FromOrder("06/11/2026", "09:00 - 11:00");

        Assert.NotNull(slot);
        Assert.Equal("2026-11-06", slot!.Date);
        Assert.Equal("09:00", slot.Time);
        // We do not know the Engine's window names and should not pretend to: it resolves the
        // time itself, so adding a window there does not break us.
        Assert.Null(slot.WindowKey);
    }

    [Fact]
    public void StillBooksWhenOnlyTheDateIsKnown()
    {
        // A date and no time is not a reason to refuse an order we could deliver. The Engine
        // picks the window.
        var slot = DeliverySlotMapper.FromOrder("2026-11-06", null);

        Assert.NotNull(slot);
        Assert.Equal("2026-11-06", slot!.Date);
        Assert.Null(slot.Time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ask me later")]
    public void SendsNoSlotRatherThanInventingOne(string? date)
    {
        // On-demand needs no slot, and a Standard booking without one is refused by the Engine
        // in terms that name the missing field. Both are better than guessing a date and
        // delivering somebody's order on a day nobody chose.
        Assert.Null(DeliverySlotMapper.FromOrder(date, "09:00 - 11:00"));
    }

    [Fact]
    public void SerialisesTheShapeTheEngineAccepts()
    {
        // The whole fault was a wire format, so assert the wire format. `slot: null` is what
        // the Engine was receiving for every order; an empty `windowKey` would be just as
        // wrong, because the Engine reads it as a window name it does not have.
        var json = JsonSerializer.Serialize(DeliverySlotMapper.FromOrder("06/11/2026", "09:00 - 11:00"));

        Assert.Contains("\"date\":\"2026-11-06\"", json);
        Assert.Contains("\"time\":\"09:00\"", json);
        Assert.DoesNotContain("windowKey", json);
    }

    [Fact]
    public void IgnoresATimeItCannotRead()
    {
        // The date is the valuable half. An unreadable time loses the window, not the order.
        var slot = DeliverySlotMapper.FromOrder("2026-11-06", "whenever suits");

        Assert.NotNull(slot);
        Assert.Equal("2026-11-06", slot!.Date);
        Assert.Null(slot.Time);
    }
}
