using System.Text.Json.Serialization;

namespace DelicateCouriers.Features.Shiplogic.DTOs;

public class CreateShipmentRequest
{
    // ===== Optional: Account identification (only sent if > 0) =====
    [JsonPropertyName("account_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int AccountId { get; set; }

    [JsonPropertyName("provider_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ProviderId { get; set; }

    // ===== Addresses and Contacts =====
    [JsonPropertyName("collection_address")]
    public required AddressDto CollectionAddress { get; set; }

    [JsonPropertyName("collection_contact")]
    public required ContactDto CollectionContact { get; set; }

    [JsonPropertyName("delivery_address")]
    public required AddressDto DeliveryAddress { get; set; }

    [JsonPropertyName("delivery_contact")]
    public required ContactDto DeliveryContact { get; set; }

    [JsonPropertyName("parcels")]
    public required List<ParcelDto> Parcels { get; set; }

    [JsonPropertyName("service_level_code")]
    public string? ServiceLevelCode { get; set; }

    [JsonPropertyName("service_level_id")]
    public int? ServiceLevelId { get; set; }

    [JsonPropertyName("mute_notifications")]
    public bool MuteNotifications { get; set; } = false;

    [JsonPropertyName("customer_reference")]
    public string? CustomerReference { get; set; }

    [JsonPropertyName("declared_value")]
    public decimal? DeclaredValue { get; set; }

    [JsonPropertyName("special_instructions_collection")]
    public string? SpecialInstructionsCollection { get; set; }

    [JsonPropertyName("special_instructions_delivery")]
    public string? SpecialInstructionsDelivery { get; set; }

    [JsonPropertyName("collection_min_date")]
    public DateTime? CollectionMinDate { get; set; }

    [JsonPropertyName("collection_after")]
    public string? CollectionAfter { get; set; }

    [JsonPropertyName("collection_before")]
    public string? CollectionBefore { get; set; }

    [JsonPropertyName("delivery_min_date")]
    public DateTime? DeliveryMinDate { get; set; }

    [JsonPropertyName("delivery_after")]
    public string? DeliveryAfter { get; set; }

    [JsonPropertyName("delivery_before")]
    public string? DeliveryBefore { get; set; }

    [JsonPropertyName("custom_tracking_reference")]
    public string? CustomTrackingReference { get; set; }

    [JsonIgnore]
    public string? SpecialInstructions
    {
        get => SpecialInstructionsDelivery;
        set => SpecialInstructionsDelivery = value;
    }
}