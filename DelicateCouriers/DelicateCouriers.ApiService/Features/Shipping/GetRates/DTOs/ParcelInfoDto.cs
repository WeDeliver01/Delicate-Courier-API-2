namespace DelicateCouriers.Features.Shipping.GetRates.DTOs;

/// <summary>
/// Parcel dimensions and weight information
/// Used to calculate shipping rates
/// </summary>
public class ParcelInfoDto
{
    public string? Description { get; set; }
    public decimal LengthCm { get; set; }
    public decimal WidthCm { get; set; }
    public decimal HeightCm { get; set; }
    public decimal WeightKg { get; set; }
}