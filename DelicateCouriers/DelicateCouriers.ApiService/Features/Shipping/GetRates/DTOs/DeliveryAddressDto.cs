namespace DelicateCouriers.Features.Shipping.GetRates.DTOs;

/// <summary>
/// Customer delivery address details
/// Received from WooCommerce checkout
/// </summary>
public class DeliveryAddressDto
{
    public string? Company { get; set; }
    public string StreetAddress { get; set; } = string.Empty;
    public string? LocalArea { get; set; }
    public string City { get; set; } = string.Empty;
    public string? Zone { get; set; }
    public string? Country { get; set; }
    public string Code { get; set; } = string.Empty;
    public string ContactName { get; set; } = string.Empty;
    public string? ContactPhone { get; set; }
    public string? ContactEmail { get; set; }
}