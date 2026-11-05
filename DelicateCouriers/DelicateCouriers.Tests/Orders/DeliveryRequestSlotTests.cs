using DelicateCouriers.ApiService.Features.Delivery;
using DelicateCouriers.ApiService.Features.Delivery.Shapes;
using Xunit;

namespace DelicateCouriers.Tests.Orders;

/// <summary>
/// The slot surviving the trip from a mapped shipment into a delivery request.
///
/// This is the step that was missing. OrderToShipmentMapper worked out the day and window
/// from the order, and the factory then built a request without them, so the Engine was asked
/// to book a Standard delivery with no slot and refused every single one.
/// </summary>
public class DeliveryRequestSlotTests
{
    private static CreateShipmentRequest Shipment(DateTime? date, string? after) => new()
    {
        CustomerReference = "WC-12056",
        ServiceLevelCode = "standard",
        CollectionAddress = new AddressDto { Street = "1 Bakery Rd", City = "Pretoria", PostalCode = "0181" },
        CollectionContact = new ContactDto { Name = "Baker", Mobile = "0821111111" },
        DeliveryAddress = new AddressDto { Street = "12 Oak St", City = "Pretoria", PostalCode = "0157" },
        DeliveryContact = new ContactDto { Name = "Jane", Mobile = "0821234567" },
        Parcels = new List<ParcelDto>(),
        DeliveryMinDate = date,
        DeliveryAfter = after
    };

    [Fact]
    public void CarriesTheDayAndTheStartOfTheWindow()
    {
        var request = DeliveryRequestFactory.From(
            Shipment(new DateTime(2026, 11, 6), "09:00"), "ASH001", "pkg-1");

        Assert.NotNull(request.Slot);
        Assert.Equal("2026-11-06", request.Slot!.Date);
        Assert.Equal("09:00", request.Slot.Time);
    }

    [Fact]
    public void SendsTheDayAloneWhenNoWindowWasSet()
    {
        // The Engine picks the window. Refusing an order we could deliver, because nobody
        // typed a time, would be the same mistake in a smaller hat.
        var request = DeliveryRequestFactory.From(
            Shipment(new DateTime(2026, 11, 6), null), "ASH001", "pkg-1");

        Assert.NotNull(request.Slot);
        Assert.Equal("2026-11-06", request.Slot!.Date);
        Assert.Null(request.Slot.Time);
    }

    [Fact]
    public void SendsNoSlotWhenNoDayIsKnown()
    {
        // On-demand needs none, and for a slotted level the Engine refuses in terms that name
        // the missing field. Both beat guessing a date and delivering on a day nobody chose.
        var request = DeliveryRequestFactory.From(Shipment(null, "09:00"), "ASH001", "pkg-1");

        Assert.Null(request.Slot);
    }
}
