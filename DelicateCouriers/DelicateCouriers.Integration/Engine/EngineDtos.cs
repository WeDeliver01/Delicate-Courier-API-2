using System.Text.Json.Serialization;

namespace DelicateCouriers.Integration.Engine;

/// <summary>
/// The Delicate Engine's own vocabulary, as its `@delicate/contracts` package defines it.
///
/// Kept deliberately separate from the checkout contract. The two speak different languages —
/// the Engine prices a loop from the depot in integer cents, the plugin wants a decimal rand
/// amount for one leg — and the whole value of this service is that the translation happens in
/// one place instead of leaking in both directions.
/// </summary>
public sealed record EngineLatLng(
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lng")] double Lng);

public sealed record EngineAddress
{
    [JsonPropertyName("formatted")] public string Formatted { get; init; } = string.Empty;
    [JsonPropertyName("line1")] public string? Line1 { get; init; }
    [JsonPropertyName("suburb")] public string? Suburb { get; init; }
    [JsonPropertyName("city")] public string? City { get; init; }
    [JsonPropertyName("postalCode")] public string? PostalCode { get; init; }
    [JsonPropertyName("country")] public string Country { get; init; } = "ZA";
    [JsonPropertyName("location")] public EngineLatLng Location { get; init; } = new(0, 0);
    [JsonPropertyName("placeId")] public string? PlaceId { get; init; }
}

public sealed record EngineContact
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("phone")] public string Phone { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string? Email { get; init; }
}

public sealed record EngineParcel
{
    [JsonPropertyName("packageTypeId")] public string PackageTypeId { get; init; } = string.Empty;
    [JsonPropertyName("quantity")] public int Quantity { get; init; } = 1;
    [JsonPropertyName("weightKg")] public decimal? WeightKg { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

public sealed record EngineCollection
{
    [JsonPropertyName("address")] public EngineAddress Address { get; init; } = new();
    [JsonPropertyName("contact")] public EngineContact? Contact { get; init; }
    [JsonPropertyName("instructions")] public string? Instructions { get; init; }
}

public sealed record EngineDrop
{
    [JsonPropertyName("address")] public EngineAddress Address { get; init; } = new();
    [JsonPropertyName("recipient")] public EngineContact Recipient { get; init; } = new();
    [JsonPropertyName("instructions")] public string? Instructions { get; init; }
    [JsonPropertyName("parcels")] public List<EngineParcel> Parcels { get; init; } = new();
}

public sealed record EngineQuoteRequest
{
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = "standard";
    [JsonPropertyName("collection")] public EngineCollection Collection { get; init; } = new();
    [JsonPropertyName("drops")] public List<EngineDrop> Drops { get; init; } = new();
    [JsonPropertyName("options")] public Dictionary<string, object> Options { get; init; } = new();
}

public sealed record EngineQuoteLine
{
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("amountCents")] public long AmountCents { get; init; }
}

/// <summary>Money is integer cents everywhere in the Engine. It never sends a float.</summary>
public sealed record EngineQuoteBreakdown
{
    [JsonPropertyName("distanceKm")] public double DistanceKm { get; init; }
    [JsonPropertyName("lines")] public List<EngineQuoteLine> Lines { get; init; } = new();
    [JsonPropertyName("subtotalCents")] public long SubtotalCents { get; init; }
    [JsonPropertyName("vatCents")] public long VatCents { get; init; }
    [JsonPropertyName("totalCents")] public long TotalCents { get; init; }
}

public sealed record EngineQuote
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("reference")] public string? Reference { get; init; }
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = string.Empty;
    [JsonPropertyName("breakdown")] public EngineQuoteBreakdown Breakdown { get; init; } = new();
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>The Engine's standard error body: a stable `code` plus a human message.</summary>
public sealed record EngineError
{
    [JsonPropertyName("statusCode")] public int StatusCode { get; init; }
    [JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }
}
