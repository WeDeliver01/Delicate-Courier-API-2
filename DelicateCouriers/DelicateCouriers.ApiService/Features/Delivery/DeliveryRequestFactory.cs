using DelicateCouriers.ApiService.Features.Delivery.Shapes;

namespace DelicateCouriers.ApiService.Features.Delivery;

/// <summary>
/// Turns what <c>OrderToShipmentMapper</c> assembles into a delivery request.
///
/// That mapper is six hundred lines of hard-won rules about this business — which address a
/// special trip collects from, how a cart becomes parcels, what counts as a missing field —
/// and none of that changed when the courier did. Rewriting it to build a different shape
/// would have risked all of it to gain nothing, so the shape is translated here instead and
/// the rules are left alone.
///
/// The types it produces are still named for ShipLogic. They are now just a local description
/// of a job: nothing in this process calls ShipLogic any more. Renaming them is worth doing
/// and is not worth doing in the same change as moving every booking onto a new courier.
/// </summary>
public static class DeliveryRequestFactory
{
    public static DeliveryRequest From(
        CreateShipmentRequest request,
        string accountRef,
        string defaultPackageTypeId)
    {
        return new DeliveryRequest
        {
            AccountRef = accountRef,
            CustomerReference = request.CustomerReference ?? string.Empty,
            ServiceLevelCode = string.IsNullOrWhiteSpace(request.ServiceLevelCode)
                ? "standard"
                : request.ServiceLevelCode,
            Collection = ToStop(request.CollectionAddress, request.CollectionContact),
            Delivery = ToStop(request.DeliveryAddress, request.DeliveryContact),
            Parcels = request.Parcels.Select(p => ToParcel(p, defaultPackageTypeId)).ToList(),

            // A declared value is what the customer was told it would cost on a special trip.
            // Carrying it as a ceiling means the Engine refuses rather than quietly booking
            // above a number somebody already agreed to.
            MaxTotalCents = request.DeclaredValue is > 0
                ? (long)Math.Round(request.DeclaredValue.Value * 100m, MidpointRounding.AwayFromZero)
                : null,
        };
    }

    /// <summary>
    /// One end of a job, for callers that hang the contact off the address.
    ///
    /// The rate controllers build it that way because ShipLogic's payload nested it; the
    /// booking path keeps them separate. Both arrive at the same place rather than each
    /// growing its own translation.
    /// </summary>
    public static DeliveryStop StopFrom(AddressDto address, ContactDto? contact = null) =>
        ToStop(address, contact ?? address.Contact ?? new ContactDto { Name = string.Empty });

    private static DeliveryStop ToStop(AddressDto address, ContactDto contact) => new()
    {
        // One line for the geocoder, built from the parts rather than stored separately, so
        // there is no second version of the address to drift from this one.
        Formatted = Describe(address),
        Line1 = address.Street,
        Suburb = address.Suburb,
        City = address.City,
        PostalCode = address.PostalCode,
        Country = string.IsNullOrWhiteSpace(address.Country) ? "ZA" : address.Country,

        // Sent only when the store actually holds them. A null pair has the integration
        // service geocode; a wrong pair prices silently, which is worse.
        Latitude = address.Latitude,
        Longitude = address.Longitude,

        ContactName = contact.Name,
        ContactPhone = contact.Mobile ?? string.Empty,
        ContactEmail = contact.Email,
        Instructions = address.BuildingDetails,
    };

    private static DeliveryParcel ToParcel(ParcelDto parcel, string defaultPackageTypeId) => new()
    {
        // The Engine prices by catalogued type and nothing in a length and a weight implies
        // one, so the store's configured default stands in. Dimensions still travel as the
        // description, because a driver loading a van needs them even when pricing does not.
        PackageTypeId = defaultPackageTypeId,
        Quantity = parcel.ParcelCount <= 0 ? 1 : parcel.ParcelCount,
        WeightKg = parcel.Weight,
        Description = parcel.Description ?? parcel.Packaging,
    };

    private static string Describe(AddressDto address) => string.Join(", ", new[]
    {
        address.Street,
        address.Suburb,
        address.City,
        address.Province,
        address.PostalCode,
        address.Country,
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
