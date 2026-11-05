using System.Text.Json.Serialization;

namespace DelicateCouriers.Integration.Engine;

/// <summary>
/// Booking, as the Engine's service surface takes it.
///
/// <c>IdempotencyKey</c> is the whole safety story and is required, not optional. It belongs to
/// the merchant's order rather than to any quote, so it survives re-pricing, and the Engine
/// holds a unique index on (account, key): two attempts become one booking inside a
/// transaction at the system of record, which is a stronger guarantee than anything this
/// service could arrange on its own.
/// </summary>
public sealed record EngineBookingRequest
{
    [JsonPropertyName("serviceLevelCode")] public string ServiceLevelCode { get; init; } = "standard";
    [JsonPropertyName("collection")] public EngineCollection Collection { get; init; } = new();
    [JsonPropertyName("drops")] public List<EngineDrop> Drops { get; init; } = new();
    [JsonPropertyName("options")] public Dictionary<string, object> Options { get; init; } = new();
    [JsonPropertyName("idempotencyKey")] public string IdempotencyKey { get; init; } = string.Empty;
    [JsonPropertyName("customerReference")] public string? CustomerReference { get; init; }

    /// <summary>
    /// Refuse rather than book above this. The shopper was shown a number at checkout and a
    /// booking nobody agreed the price of becomes an argument on an invoice weeks later.
    /// </summary>
    [JsonPropertyName("maxTotalCents")] public long? MaxTotalCents { get; init; }

    [JsonPropertyName("slot")] public EngineSlot? Slot { get; init; }
}

public sealed record EngineSlot
{
    [JsonPropertyName("date")] public string Date { get; init; } = string.Empty;

    /// <summary>
    /// The window the Engine should book, when we happen to know its key. A checkout does not:
    /// it knows the time the shopper picked, so <see cref="Time"/> is the usual one to send.
    /// Both are omitted when empty rather than serialised as null, because the Engine takes
    /// either and sending both empty says nothing.
    /// </summary>
    [JsonPropertyName("windowKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WindowKey { get; init; }

    /// <summary>
    /// `HH:MM`, 24-hour. The Engine resolves it to whichever of its delivery windows contains
    /// that time for the service level being booked, so we do not have to know its window
    /// names or re-learn them when it adds one.
    /// </summary>
    [JsonPropertyName("time")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Time { get; init; }
}

public sealed record EngineShipment
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("waybill")] public string Waybill { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
}

public sealed record EngineBooking
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("reference")] public string Reference { get; init; } = string.Empty;
    [JsonPropertyName("customerReference")] public string? CustomerReference { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("totalCents")] public long TotalCents { get; init; }
    [JsonPropertyName("shipments")] public List<EngineShipment> Shipments { get; init; } = new();
}

public sealed record EngineBookingResponse
{
    [JsonPropertyName("booking")] public EngineBooking Booking { get; init; } = new();
    [JsonPropertyName("quote")] public EngineQuote Quote { get; init; } = new();

    /// <summary>True when the Engine recognised this as an attempt it had already served.</summary>
    [JsonPropertyName("replayed")] public bool Replayed { get; init; }
}
