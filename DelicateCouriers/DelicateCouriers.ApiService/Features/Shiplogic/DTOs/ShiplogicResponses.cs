using System.Text.Json.Serialization;

namespace DelicateCouriers.Features.Shiplogic.DTOs;

/// <summary>
/// Response from Shiplogic after creating a shipment
/// POST /shipments
/// Based on actual Shiplogic API response structure
/// </summary>
public class CreateShipmentResponse
{
    /// <summary>
    /// Unique shipment ID from Shiplogic
    /// </summary>
    [JsonPropertyName("id")]
    public int ShipmentId { get; set; }

    /// <summary>
    /// Custom tracking reference (full)
    /// </summary>
    [JsonPropertyName("custom_tracking_reference")]
    public string? CustomTrackingReference { get; set; }

    /// <summary>
    /// Short tracking reference (last 4 chars)
    /// </summary>
    [JsonPropertyName("short_tracking_reference")]
    public string? ShortTrackingReference { get; set; }

    /// <summary>
    /// Current shipment status
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Shipment cost/rate
    /// </summary>
    [JsonPropertyName("rate")]
    public decimal? Rate { get; set; }

    /// <summary>
    /// Service level code used (e.g., "ECO", "ONX")
    /// </summary>
    [JsonPropertyName("service_level_code")]
    public string? ServiceLevelCode { get; set; }

    /// <summary>
    /// Service level name (e.g., "Economy", "Overnight")
    /// </summary>
    [JsonPropertyName("service_level_name")]
    public string? ServiceLevelName { get; set; }

    /// <summary>
    /// Estimated collection date/time
    /// </summary>
    [JsonPropertyName("estimated_collection")]
    public DateTime? EstimatedCollection { get; set; }

    /// <summary>
    /// Estimated delivery from date/time
    /// </summary>
    [JsonPropertyName("estimated_delivery_from")]
    public DateTime? EstimatedDeliveryFrom { get; set; }

    /// <summary>
    /// Estimated delivery to date/time
    /// </summary>
    [JsonPropertyName("estimated_delivery_to")]
    public DateTime? EstimatedDeliveryTo { get; set; }

    /// <summary>
    /// Date shipment was created
    /// </summary>
    [JsonPropertyName("time_created")]
    public DateTime? TimeCreated { get; set; }

    /// <summary>
    /// Collection branch ID
    /// </summary>
    [JsonPropertyName("collection_branch_id")]
    public int? CollectionBranchId { get; set; }

    /// <summary>
    /// Collection branch name
    /// </summary>
    [JsonPropertyName("collection_branch_name")]
    public string? CollectionBranchName { get; set; }

    /// <summary>
    /// Delivery branch ID
    /// </summary>
    [JsonPropertyName("delivery_branch_id")]
    public int? DeliveryBranchId { get; set; }

    /// <summary>
    /// Delivery branch name
    /// </summary>
    [JsonPropertyName("delivery_branch_name")]
    public string? DeliveryBranchName { get; set; }

    // HELPER PROPERTIES for backward compatibility
    [JsonIgnore]
    public string ConsignmentId => ShipmentId.ToString();

    [JsonIgnore]
    public string TrackingNumber => CustomTrackingReference ?? ShortTrackingReference ?? ShipmentId.ToString();

    [JsonIgnore]
    public DateTime? EstimatedDeliveryDate => EstimatedDeliveryFrom;
}

/// <summary>
/// Response from tracking shipments
/// GET /tracking/shipments
/// </summary>
public class TrackingResponse
{
    /// <summary>
    /// List of shipments with tracking info
    /// </summary>
    [JsonPropertyName("shipments")]
    public List<TrackingShipment> Shipments { get; set; } = new();
}

public class TrackingShipment
{
    /// <summary>
    /// Shipment ID
    /// </summary>
    [JsonPropertyName("id")]
    public int ShipmentId { get; set; }

    /// <summary>
    /// Custom tracking reference
    /// </summary>
    [JsonPropertyName("custom_tracking_reference")]
    public string? CustomTrackingReference { get; set; }

    /// <summary>
    /// Short tracking reference
    /// </summary>
    [JsonPropertyName("short_tracking_reference")]
    public string? ShortTrackingReference { get; set; }

    /// <summary>
    /// Current status
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// List of tracking events
    /// </summary>
    [JsonPropertyName("tracking_events")]
    public List<TrackingEventDto>? TrackingEvents { get; set; }

    /// <summary>
    /// Collection date
    /// </summary>
    [JsonPropertyName("shipment_collected_date")]
    public DateTime? CollectedDate { get; set; }

    /// <summary>
    /// Delivery date
    /// </summary>
    [JsonPropertyName("shipment_delivered_date")]
    public DateTime? DeliveredDate { get; set; }

    /// <summary>
    /// Estimated collection
    /// </summary>
    [JsonPropertyName("shipment_estimated_collection")]
    public DateTime? EstimatedCollection { get; set; }

    /// <summary>
    /// Estimated delivery from
    /// </summary>
    [JsonPropertyName("shipment_estimated_delivery_from")]
    public DateTime? EstimatedDeliveryFrom { get; set; }

    /// <summary>
    /// Estimated delivery to
    /// </summary>
    [JsonPropertyName("shipment_estimated_delivery_to")]
    public DateTime? EstimatedDeliveryTo { get; set; }

    /// <summary>
    /// Time created
    /// </summary>
    [JsonPropertyName("shipment_time_created")]
    public DateTime? TimeCreated { get; set; }

    /// <summary>
    /// Time modified
    /// </summary>
    [JsonPropertyName("shipment_time_modified")]
    public DateTime? TimeModified { get; set; }

    // HELPER PROPERTIES for backward compatibility
    [JsonIgnore]
    public string ConsignmentId => ShipmentId.ToString();

    [JsonIgnore]
    public string TrackingNumber => CustomTrackingReference ?? ShortTrackingReference ?? ShipmentId.ToString();

    [JsonIgnore]
    public DateTime? EstimatedDeliveryDate => EstimatedDeliveryFrom;

    [JsonIgnore]
    public DateTime? DeliveredAt => DeliveredDate;

    [JsonIgnore]
    public List<TrackingEventDto> Events => TrackingEvents ?? new List<TrackingEventDto>();
}

public class TrackingEventDto
{
    /// <summary>
    /// Event ID
    /// </summary>
    [JsonPropertyName("id")]
    public int EventId { get; set; }

    /// <summary>
    /// Event timestamp
    /// </summary>
    [JsonPropertyName("date")]
    public DateTime Date { get; set; }

    /// <summary>
    /// Event status code
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Event message/description
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Location where event occurred
    /// </summary>
    [JsonPropertyName("location")]
    public string? Location { get; set; }

    /// <summary>
    /// Source of the event (e.g., "driver", "system")
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Parcel ID if event is parcel-specific
    /// </summary>
    [JsonPropertyName("parcel_id")]
    public int? ParcelId { get; set; }

    // HELPER PROPERTIES for backward compatibility
    [JsonIgnore]
    public DateTime Timestamp => Date;

    [JsonIgnore]
    public string Description => Message ?? Status ?? "";
}

/// <summary>
/// Response from getting quotes
/// GET /quotes
/// </summary>
/// 

//public class QuoteResponse
//{
//    /// <summary>
//    /// List of available quotes
//    /// </summary>
//    [JsonPropertyName("quotes")]
//    public List<ShippingQuote> Quotes { get; set; } = new();
//}

//public class ShippingQuote
//{
//    /// <summary>
//    /// Service level ID
//    /// </summary>
//    [JsonPropertyName("service_level_id")]
//    public int ServiceLevelId { get; set; }

//    /// <summary>
//    /// Service level code (e.g., "ECO", "ONX")
//    /// </summary>
//    [JsonPropertyName("service_level_code")]
//    public string? ServiceLevelCode { get; set; }

//    /// <summary>
//    /// Service level name (e.g., "Economy", "Overnight")
//    /// </summary>
//    [JsonPropertyName("service_level_name")]
//    public string? ServiceLevelName { get; set; }

//    /// <summary>
//    /// Shipping rate/cost
//    /// </summary>
//    [JsonPropertyName("rate")]
//    public decimal Rate { get; set; }

//    /// <summary>
//    /// Estimated delivery days
//    /// </summary>
//    [JsonPropertyName("estimated_delivery_days")]
//    public int? EstimatedDeliveryDays { get; set; }

//    /// <summary>
//    /// Collection branch ID
//    /// </summary>
//    [JsonPropertyName("collection_branch_id")]
//    public int? CollectionBranchId { get; set; }

//    /// <summary>
//    /// Collection branch name
//    /// </summary>
//    [JsonPropertyName("collection_branch_name")]
//    public string? CollectionBranchName { get; set; }

//    /// <summary>
//    /// Delivery branch ID
//    /// </summary>
//    [JsonPropertyName("delivery_branch_id")]
//    public int? DeliveryBranchId { get; set; }

//    /// <summary>
//    /// Delivery branch name
//    /// </summary>
//    [JsonPropertyName("delivery_branch_name")]
//    public string? DeliveryBranchName { get; set; }

//    // HELPER PROPERTIES for backward compatibility
//    [JsonIgnore]
//    public string Courier => CollectionBranchName ?? "Unknown";

//    [JsonIgnore]
//    public DateTime? EstimatedDeliveryDate => EstimatedDeliveryDays.HasValue ? DateTime.UtcNow.AddDays(EstimatedDeliveryDays.Value) : null;

//    [JsonIgnore]
//    public int? DeliveryDays => EstimatedDeliveryDays;
//}