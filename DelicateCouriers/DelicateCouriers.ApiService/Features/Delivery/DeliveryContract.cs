using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.Delivery;

/// <summary>
/// A delivery, as this portal describes one.
///
/// Mirrors what the integration service accepts, which was in turn shaped after the ShipLogic
/// request it replaced: two ends, each an address and a contact, plus parcels. Keeping that
/// shape is why swapping couriers did not mean rewriting every screen that books one.
/// </summary>
public sealed record DeliveryRequest
{
    /// <summary>The store or tenant's own identifier, which the Engine resolves to an account.</summary>
    [JsonPropertyName("accountRef")] public string AccountRef { get; init; } = string.Empty;

    /// <summary>
    /// This portal's reference for the job. Also the idempotency key: sent twice, the booking
    /// already made comes back rather than a second collection being arranged.
    /// </summary>
    [JsonPropertyName("customerReference")] public string CustomerReference { get; init; } = string.Empty;

    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = "standard";
    [JsonPropertyName("collection")] public DeliveryStop Collection { get; init; } = new();
    [JsonPropertyName("delivery")] public DeliveryStop Delivery { get; init; } = new();
    [JsonPropertyName("parcels")] public List<DeliveryParcel> Parcels { get; init; } = new();

    /// <summary>
    /// Refuse rather than book above this, in cents. Null when an operator has already agreed
    /// the price by hand; set when a customer was quoted one and is owed it.
    /// </summary>
    [JsonPropertyName("maxTotalCents")] public long? MaxTotalCents { get; init; }
}

public sealed record DeliveryStop
{
    [JsonPropertyName("formatted")] public string Formatted { get; init; } = string.Empty;
    [JsonPropertyName("line1")] public string? Line1 { get; init; }
    [JsonPropertyName("suburb")] public string? Suburb { get; init; }
    [JsonPropertyName("city")] public string? City { get; init; }
    [JsonPropertyName("postalCode")] public string? PostalCode { get; init; }
    [JsonPropertyName("country")] public string Country { get; init; } = "ZA";

    /// <summary>
    /// Send these when they are known — a store's own premises, stored once. Leaving them null
    /// has the integration service geocode, which costs a network call and can disagree with
    /// itself between a quote and a booking.
    /// </summary>
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }

    [JsonPropertyName("contactName")] public string ContactName { get; init; } = string.Empty;
    [JsonPropertyName("contactPhone")] public string ContactPhone { get; init; } = string.Empty;
    [JsonPropertyName("contactEmail")] public string? ContactEmail { get; init; }
    [JsonPropertyName("instructions")] public string? Instructions { get; init; }
}

public sealed record DeliveryParcel
{
    /// <summary>
    /// The Engine catalogue entry this is priced as. Required: nothing in a weight and a
    /// length says whether a box of cupcakes is handled like a box of books.
    /// </summary>
    [JsonPropertyName("packageTypeId")] public string PackageTypeId { get; init; } = string.Empty;

    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;
    [JsonPropertyName("weightKg")] public decimal? WeightKg { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

/// <summary>
/// A booking, named for the columns it lands in.
///
/// <see cref="ConsignmentId"/> and <see cref="TrackingNumber"/> kept their ShipLogic-era
/// names: the database has held them since, they are already text, and renaming a column
/// across a live schema buys nothing a comment cannot.
/// </summary>
public sealed record DeliveryBooking
{
    [JsonPropertyName("consignmentId")] public string ConsignmentId { get; init; } = string.Empty;
    [JsonPropertyName("trackingNumber")] public string TrackingNumber { get; init; } = string.Empty;
    [JsonPropertyName("reference")] public string Reference { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("totalCents")] public long TotalCents { get; init; }

    /// <summary>
    /// Who is carrying it. Always us now: there is no third party to name since the
    /// decoupling, and the column used to hold the reseller's collecting branch.
    /// </summary>
    [JsonPropertyName("courierName")] public string CourierName { get; init; } = "Delicate Courier";

    /// <summary>The service actually booked. Stored as CourierService.</summary>
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = string.Empty;

    /// <summary>True when this was already booked and the Engine returned what it had.</summary>
    [JsonPropertyName("replayed")] public bool Replayed { get; init; }

    /// <summary>What the portal stores as a money column, from the Engine's cents.</summary>
    public decimal TotalAmount => TotalCents / 100m;
}

public sealed record DeliveryQuote
{
    [JsonPropertyName("totalCents")] public long TotalCents { get; init; }
    [JsonPropertyName("currency")] public string Currency { get; init; } = "ZAR";
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = string.Empty;

    public decimal TotalAmount => TotalCents / 100m;
}
