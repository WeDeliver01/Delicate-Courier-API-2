using DelicateCouriers.ApiService.Features.Orders;
using DelicateCouriers.ApiService.Features.Packaging;
using DelicateCouriers.ApiService.Infrastructure.Geocoding;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.ApiService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DelicateCouriers.Tests.Orders;

/// <summary>
/// Locks the special-trip (plugin v2.7.0+) booking rules in
/// OrderToShipmentMapper:
/// - FulfillmentType "special_trip" books as Shiplogic service level SPX
/// - the customer-quoted amount rides along as declared_value
/// - plugin-geocoded customer coordinates are attached to the delivery
///   address (and the platform-side geocoder is skipped)
/// - normal delivery orders are unaffected (no declared value, store
///   default service level)
/// </summary>
public class SpecialTripMappingTests
{
    private sealed class NullGeocoder : IGeocoder
    {
        public string Name => "null";
        public int Calls { get; private set; }

        public Task<GeocodeResult?> GeocodeAsync(GeocodeQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<GeocodeResult?>(null);
        }
    }

    private static (OrderToShipmentMapper Mapper, NullGeocoder Geocoder) CreateMapper()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"special-trip-{Guid.NewGuid():N}")
            .Options;
        var ctx = new AppDbContext(options, httpContextAccessor: null);
        var geocoder = new NullGeocoder();
        var mapper = new OrderToShipmentMapper(
            new PackageMappingService(ctx, NullLogger<PackageMappingService>.Instance),
            geocoder,
            NullLogger<OrderToShipmentMapper>.Instance);
        return (mapper, geocoder);
    }

    private static Store CreateStore() => new()
    {
        StoreID = 1,
        TenantID = 1,
        StoreName = "Test Store",
        DefaultServiceLevel = "ECO",
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

    private static Order CreateOrder() => new()
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
        LineItems = new List<OrderLineItem>
        {
            new() { OrderLineItemID = 1, OrderID = 1, ProductName = "Cake", Quantity = 1 },
        },
    };

    [Fact]
    public async Task SpecialTrip_Books_As_SPX_With_QuotedAmount_As_DeclaredValue()
    {
        var order = CreateOrder();
        order.FulfillmentType = "special_trip";
        order.SpecialTripQuotedAmount = 486.20m;
        order.SpecialTripDistanceKm = 44.2m;

        var (mapper, _) = CreateMapper();
        var request = await mapper.MapOrderToShipmentAsync(order, CreateStore());

        Assert.Equal("SPX", request.ServiceLevelCode);
        Assert.Equal(486.20m, request.DeclaredValue);
    }

    [Fact]
    public async Task SpecialTrip_Uses_Plugin_Coordinates_And_Skips_Platform_Geocode()
    {
        var order = CreateOrder();
        order.FulfillmentType = "special_trip";
        order.SpecialTripQuotedAmount = 100m;
        order.SpecialTripCustomerLat = -26.1076;
        order.SpecialTripCustomerLng = 28.0567;

        var (mapper, geocoder) = CreateMapper();
        var request = await mapper.MapOrderToShipmentAsync(order, CreateStore());

        Assert.Equal(-26.1076, request.DeliveryAddress!.Latitude);
        Assert.Equal(28.0567, request.DeliveryAddress.Longitude);
        Assert.Equal(0, geocoder.Calls);
    }

    [Fact]
    public async Task SpecialTrip_Without_Coordinates_Falls_Back_To_Platform_Geocoder()
    {
        var order = CreateOrder();
        order.FulfillmentType = "special_trip";
        order.SpecialTripQuotedAmount = 100m;

        var (mapper, geocoder) = CreateMapper();
        var request = await mapper.MapOrderToShipmentAsync(order, CreateStore());

        Assert.Equal("SPX", request.ServiceLevelCode);
        Assert.Equal(1, geocoder.Calls);
    }

    [Fact]
    public async Task Delivery_Order_Without_Postal_Code_Fails_Fast_Before_Shiplogic()
    {
        // Shiplogic rejects bookings whose delivery address has no postal
        // code ("delivery postal_code is required") — and then the job
        // retries forever. The mapper must throw a clear error instead.
        var order = CreateOrder();
        order.FulfillmentType = "delivery";
        order.ShippingPostalCode = "";

        var (mapper, geocoder) = CreateMapper();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mapper.MapOrderToShipmentAsync(order, CreateStore()));

        Assert.Contains("postal code", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(order.WooOrderNumber!, ex.Message);
        // Fail fast: nothing downstream (like geocoding) should have run.
        Assert.Equal(0, geocoder.Calls);
    }

    [Fact]
    public async Task Delivery_Order_With_Whitespace_Postal_Code_Also_Fails_Fast()
    {
        var order = CreateOrder();
        order.ShippingPostalCode = "   ";

        var (mapper, _) = CreateMapper();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mapper.MapOrderToShipmentAsync(order, CreateStore()));
    }

    [Fact]
    public async Task SpecialTrip_Without_Postal_Code_Still_Maps_As_SPX()
    {
        // Special trips book as SPX ad-hoc trips routed by the customer
        // lat/lng captured at checkout — a postal code is not required.
        var order = CreateOrder();
        order.FulfillmentType = "special_trip";
        order.SpecialTripQuotedAmount = 250m;
        order.SpecialTripCustomerLat = -26.1076;
        order.SpecialTripCustomerLng = 28.0567;
        order.ShippingPostalCode = "";

        var (mapper, _) = CreateMapper();
        var request = await mapper.MapOrderToShipmentAsync(order, CreateStore());

        Assert.Equal("SPX", request.ServiceLevelCode);
        Assert.Equal(250m, request.DeclaredValue);
    }

    [Fact]
    public async Task Normal_Delivery_Order_Is_Unaffected()
    {
        var order = CreateOrder();
        order.FulfillmentType = "delivery";

        var (mapper, geocoder) = CreateMapper();
        var request = await mapper.MapOrderToShipmentAsync(order, CreateStore());

        Assert.NotEqual("SPX", request.ServiceLevelCode);
        Assert.Null(request.DeclaredValue);
        Assert.Equal(1, geocoder.Calls);
    }
}
