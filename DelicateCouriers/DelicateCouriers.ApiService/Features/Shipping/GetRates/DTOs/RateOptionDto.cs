namespace DelicateCouriers.Features.Shipping.GetRates.DTOs;

/// <summary>
/// A single shipping rate option returned to WooCommerce
/// </summary>
public class RateOptionDto
{
    public int ServiceLevelId { get; set; }
    public string ServiceLevelCode { get; set; } = string.Empty;
    public string ServiceLevelName { get; set; } = string.Empty;
    public decimal Cost { get; set; }
    public string Currency { get; set; } = "ZAR";
    public int EstimatedDeliveryDays { get; set; }
}