using DelicateCouriers.ApiService.Features.Orders;
using DelicateCouriers.ApiService.Features.Packaging;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Orders;

/// <summary>
/// Locks the collection/delivery window math in OrderToShipmentMapper:
/// - single chosen delivery time becomes a 30-min slot ENDING at that time
/// - default collection window runs 120→90 minutes before the delivery end
/// - early-morning underflow falls back to [00:00, delivery start]
/// </summary>
public class OrderToShipmentMapperTimingTests
{
    private sealed class NullGeocoder : IGeocoder
    {
        public string Name => "null";

        public Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
            => Task.FromResult<GeocodeResult?>(null);
    }

    private static OrderToShipmentMapper CreateMapper()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"mapper-timing-{Guid.NewGuid():N}")
            .Options;
        var ctx = new AppDbContext(options, httpContextAccessor: null);
        return new OrderToShipmentMapper(
            new PackageMappingService(ctx, NullLogger<PackageMappingService>.Instance),
            new NullGeocoder(),
            NullLogger<OrderToShipmentMapper>.Instance);
    }

    private static Store CreateStore() => new()
    {
        StoreID = 1,
        TenantID = 1,
        StoreName = "Test Store",
        CollectionAddressLine1 = "1 Main Rd",
        CollectionCity = "Pretoria",
        CollectionPostalCode = "0181",
        CollectionCountry = "ZA",
        CollectionContactName = "Store Owner",
        CollectionContactPhone = "+27110000000",
        CollectionLatitude = -25.75,
        CollectionLongitude = 28.19,
        ShiplogicProviderId = 35,
        ShiplogicAccountId = 624164,
    };

    private static Order CreateOrder(string? deliveryTime) => new()
    {
        OrderID = 1,
        TenantID = 1,
        StoreID = 1,
        WooOrderID = "1001",
        WooOrderNumber = "1001",
        CustomerName = "Test Customer",
        CustomerPhone = "+27820000000",
        ShippingAddressLine1 = "2 Other St",
        ShippingCity = "Johannesburg",
        ShippingPostalCode = "2000",
        ShippingCountry = "ZA",
        RequestedDeliveryDate = DateTime.UtcNow.Date.AddDays(2),
        RequestedDeliveryTime = deliveryTime,
        LineItems = new List<OrderLineItem>
        {
            new() { OrderLineItemID = 1, OrderID = 1, ProductName = "Cake", Quantity = 1 },
        },
    };

    [Fact]
    public async Task ChosenTime_1630_Yields_Delivery_1600_1630_And_Collection_1430_1500()
    {
        var request = await CreateMapper().MapOrderToShipmentAsync(CreateOrder("16:30"), CreateStore());

        Assert.Equal("16:00", request.DeliveryAfter);
        Assert.Equal("16:30", request.DeliveryBefore);
        Assert.Equal("14:30", request.CollectionAfter);
        Assert.Equal("15:00", request.CollectionBefore);
    }

    [Fact]
    public async Task NoDeliveryTime_UsesStoreDefaultWindow_CollectionKeysOffWindowEnd()
    {
        var store = CreateStore();
        store.DeliveryTimeFrom = "08:00";
        store.DeliveryTimeTo = "17:00";

        var request = await CreateMapper().MapOrderToShipmentAsync(CreateOrder(null), store);

        Assert.Equal("15:00", request.CollectionAfter);
        Assert.Equal("15:30", request.CollectionBefore);
    }

    [Fact]
    public async Task EarlyMorningDeliveryEnd_UnderflowFallsBackTo_Midnight_To_DeliveryStart()
    {
        var request = await CreateMapper().MapOrderToShipmentAsync(CreateOrder("01:20"), CreateStore());

        Assert.Equal("00:50", request.DeliveryAfter);
        Assert.Equal("01:20", request.DeliveryBefore);
        Assert.Equal("00:00", request.CollectionAfter);
        Assert.Equal("00:50", request.CollectionBefore);
    }
}
