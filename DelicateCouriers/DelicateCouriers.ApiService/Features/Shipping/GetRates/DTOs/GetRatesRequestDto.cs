using DelicateCouriers.ApiService.Features.Shipping.GetRates.DTOs;

namespace DelicateCouriers.Features.Shipping.GetRates.DTOs;

/// <summary>
/// Request to get shipping rates for a customer's delivery address
/// Called by WooCommerce during checkout
/// </summary>
public class GetRatesRequestDto
{
    /// <summary>
    /// Store ID making the request
    /// Used to look up tenant and collection address
    /// </summary>
    public int StoreId { get; set; }

    /// <summary>
    /// Customer's delivery address
    /// </summary>
    public DeliveryAddressDto DeliveryAddress { get; set; } = new();

    /// <summary>
    /// List of parcels/packages being shipped
    /// </summary>
    public List<ParcelInfoDto> Parcels { get; set; } = new();
}