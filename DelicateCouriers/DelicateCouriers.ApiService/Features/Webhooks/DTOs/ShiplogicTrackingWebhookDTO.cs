using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.Webhooks.DTOs
{
    // DTOs for the webhook payload
    public class ShiplogicTrackingWebhookDTO
    {
        [JsonPropertyName("shipment_id")]
        public long ShipmentId { get; set; }

        [JsonPropertyName("short_tracking_reference")]
        public string? ShortTrackingReference { get; set; }

        [JsonPropertyName("custom_tracking_reference")]
        public string? CustomTrackingReference { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("update_type")]
        public string? UpdateType { get; set; }

        [JsonPropertyName("provider_id")]
        public int ProviderId { get; set; }

        [JsonPropertyName("service_level_code")]
        public string? ServiceLevelCode { get; set; }

        [JsonPropertyName("collection_hub")]
        public string? CollectionHub { get; set; }

        [JsonPropertyName("delivery_hub")]
        public string? DeliveryHub { get; set; }

        [JsonPropertyName("shipment_collected_date")]
        public DateTime? ShipmentCollectedDate { get; set; }

        [JsonPropertyName("shipment_delivered_date")]
        public DateTime? ShipmentDeliveredDate { get; set; }

        [JsonPropertyName("shipment_estimated_collection")]
        public DateTime? ShipmentEstimatedCollection { get; set; }

        [JsonPropertyName("shipment_estimated_delivery_from")]
        public DateTime? ShipmentEstimatedDeliveryFrom { get; set; }

        [JsonPropertyName("shipment_estimated_delivery_to")]
        public DateTime? ShipmentEstimatedDeliveryTo { get; set; }

        [JsonPropertyName("shipment_time_created")]
        public DateTime? ShipmentTimeCreated { get; set; }

        [JsonPropertyName("shipment_time_modified")]
        public DateTime? ShipmentTimeModified { get; set; }

        [JsonPropertyName("tracking_events")]
        public List<ShiplogicTrackingEvent>? TrackingEvents { get; set; }

        [JsonPropertyName("parcel_tracking_references")]
        public List<string>? ParcelTrackingReferences { get; set; }
    }

    public class ShiplogicTrackingEvent
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("date")]
        public DateTime Date { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("parcel_id")]
        public int ParcelId { get; set; }
    }
}
