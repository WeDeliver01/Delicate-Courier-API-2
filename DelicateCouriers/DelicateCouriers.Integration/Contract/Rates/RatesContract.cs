namespace DelicateCouriers.Integration.Contract.Rates;

/// <summary>
/// The checkout rates contract, reproduced exactly as the WooCommerce plugins already see it.
///
/// These shapes are not ours to improve. Three plugin lines in the field deserialize them, and
/// a merchant cannot be asked to upgrade a plugin because we rewrote a platform. Every field
/// name, type and default here is copied from the ApiService implementation this service
/// replaces; if a difference ever creeps in, the contract tests are what should catch it.
/// </summary>
public class GetRatesRequestDto
{
    /// <summary>Store making the request. Used to resolve collection address and account.</summary>
    public int StoreId { get; set; }

    public DeliveryAddressDto DeliveryAddress { get; set; } = new();

    public List<ParcelInfoDto> Parcels { get; set; } = new();
}

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

public class ParcelInfoDto
{
    public string? Description { get; set; }
    public decimal LengthCm { get; set; }
    public decimal WidthCm { get; set; }
    public decimal HeightCm { get; set; }
    public decimal WeightKg { get; set; }
}

public class GetRatesResponseDto
{
    public bool Success { get; set; }
    public List<RateOptionDto> Rates { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// One rate as the plugin renders it.
///
/// <c>ServiceLevelCode</c> must match the service level configured in the plugin (default
/// "STD") or the plugin's own filter drops the rate and the customer sees no delivery option
/// at all. It is therefore the merchant's configured code, not anything the Engine calls a
/// service level internally.
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
