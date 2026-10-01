namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a tracking event for a shipment.
/// Events are retrieved from Shiplogic and stored for history/audit trail.
/// Example: "Package picked up at warehouse - Johannesburg - 2025-01-15 10:00 AM"
/// </summary>
public class TrackingEvent
{
    /// <summary>
    /// Unique identifier for the tracking event
    /// </summary>
    public int TrackingEventID { get; set; }

    /// <summary>
    /// Foreign key to the shipment this event belongs to
    /// </summary>
    public int ShipmentID { get; set; }

    /// <summary>
    /// Shiplogic's event ID - used to prevent duplicate events
    /// </summary>
    public string? ExternalEventId { get; set; }

    /// <summary>
    /// Type of tracking event
    /// Values: "Created", "PickedUp", "InTransit", "OutForDelivery", "Delivered", "Exception", "Returned"
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the event
    /// Example: "Package picked up from sender"
    /// </summary>
    public string EventDescription { get; set; } = string.Empty;

    /// <summary>
    /// Location where the event occurred
    /// Example: "Johannesburg Distribution Center"
    /// </summary>
    public string EventLocation { get; set; } = string.Empty;

    /// <summary>
    /// When the event actually happened (timestamp from courier)
    /// </summary>
    public DateTime EventTimestamp { get; set; }

    /// <summary>
    /// Source of the event (e.g., "corneldriver", "system", "mariskaadmin")
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// When we received and stored this event in our system
    /// </summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who/what created this event record (usually "System" from background job)
    /// </summary>
    public string CreatedBy { get; set; } = "System";

    /// <summary>
    /// When this event was last modified
    /// </summary>
    public DateTime? ChangedOn { get; set; }

    /// <summary>
    /// Who last modified this event
    /// </summary>
    public string? ChangedBy { get; set; }

    // Navigation property
    public Shipment Shipment { get; set; } = null!;
}