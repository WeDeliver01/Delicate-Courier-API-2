using System.Text.Json.Serialization;

namespace DelicateCouriers.Features.Shiplogic.DTOs;

/// <summary>
/// Address information for collection or delivery
/// </summary>
public class AddressDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "residential";

    [JsonPropertyName("company")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Company { get; set; }

    [JsonPropertyName("street_address")]
    public required string Street { get; set; }

    [JsonPropertyName("local_area")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Suburb { get; set; }

    [JsonPropertyName("city")]
    public required string City { get; set; }

    [JsonPropertyName("code")]
    public required string PostalCode { get; set; }

    [JsonPropertyName("zone")]
    public string? Province { get; set; }

    [JsonPropertyName("country")]
    public string Country { get; set; } = "ZA";

    // Omit lat/lng entirely when not set — Shiplogic rejects payloads that
    // include `"lat": null, "lng": null` (same bug the WordPress plugin had).
    [JsonPropertyName("lat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Latitude { get; set; }

    [JsonPropertyName("lng")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Longitude { get; set; }

    // Shiplogic's /v2/rates endpoint expects contact details nested INSIDE
    // the address object (not as a separate collection_contact/delivery_contact
    // sibling). The working v1 WooCommerce plugin proved this shape; sending
    // contact as a sibling causes Shiplogic to return rates:null because the
    // address fails its internal validation for providers that require
    // contact details. The CreateShipment endpoint uses sibling contacts
    // (see CreateShipmentRequest) and does not populate AddressDto.Contact,
    // so the rates and shipment payloads stay shaped correctly.
    [JsonPropertyName("contact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContactDto? Contact { get; set; }

    [JsonIgnore]
    public string? BuildingDetails { get; set; }

    // These are used internally for GetRates but should NOT be serialized in shipment requests
    [JsonIgnore]
    public int AccountId { get; set; }

    [JsonIgnore]
    public int ProviderId { get; set; }
}