using System.Text.Json.Serialization;
using DelicateCouriers.Integration.Engine;

namespace DelicateCouriers.Integration.Labels;

/// <summary>
/// The waybill as the Engine serves it: data, not a rendered page.
///
/// Handing over the fields rather than a layout is the right way round. This service knows
/// what the plugin and the drivers need on a label and can change it without a deploy of the
/// Engine, and the Engine's own design never gets frozen into somebody else's integration.
/// </summary>
public sealed record WaybillDocument
{
    [JsonPropertyName("waybill")] public string Waybill { get; init; } = string.Empty;
    [JsonPropertyName("bookingReference")] public string BookingReference { get; init; } = string.Empty;
    [JsonPropertyName("customerReference")] public string? CustomerReference { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("issuedAt")] public string IssuedAt { get; init; } = string.Empty;
    [JsonPropertyName("carrier")] public WaybillCarrier Carrier { get; init; } = new();
    [JsonPropertyName("sender")] public WaybillParty Sender { get; init; } = new();
    [JsonPropertyName("recipient")] public WaybillParty Recipient { get; init; } = new();
    [JsonPropertyName("service")] public WaybillService Service { get; init; } = new();
    [JsonPropertyName("parcelCount")] public int ParcelCount { get; init; }
    [JsonPropertyName("terms")] public string Terms { get; init; } = string.Empty;
}

public sealed record WaybillCarrier
{
    [JsonPropertyName("legalName")] public string? LegalName { get; init; }
    [JsonPropertyName("tradingName")] public string? TradingName { get; init; }
    [JsonPropertyName("phone")] public string? Phone { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
}

public sealed record WaybillParty
{
    [JsonPropertyName("accountName")] public string? AccountName { get; init; }
    [JsonPropertyName("contact")] public EngineContact? Contact { get; init; }
    [JsonPropertyName("address")] public EngineAddress? Address { get; init; }
    [JsonPropertyName("instructions")] public string? Instructions { get; init; }
}

public sealed record WaybillService
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("slotDate")] public string? SlotDate { get; init; }
    [JsonPropertyName("slotWindow")] public string? SlotWindow { get; init; }
}
