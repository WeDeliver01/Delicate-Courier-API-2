namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a courier shipment created via Shiplogic API.
/// Each shipment is linked to one order.
/// Example: Order #12345 → Shipment with Aramex, tracking TR789
/// </summary>
public class Shipment
{
    /// <summary>
    /// Unique identifier for the shipment in our system
    /// </summary>
    public int ShipmentID { get; set; }

    /// <summary>
    /// Foreign key to the order this shipment is for
    /// </summary>
    public int OrderID { get; set; }

    /// <summary>
    /// Consignment ID from Shiplogic
    /// Example: "SL123456789"
    /// </summary>
    public string ConsignmentID { get; set; } = string.Empty;

    /// <summary>
    /// Tracking number provided to customer
    /// Example: "TR78912345"
    /// </summary>
    public string TrackingNumber { get; set; } = string.Empty;

    /// <summary>
    /// Name of the courier company
    /// Example: "Aramex", "The Courier Guy", "DHL"
    /// </summary>
    public string CourierName { get; set; } = string.Empty;

    /// <summary>
    /// Courier service type
    /// Example: "Express", "Economy", "Same Day"
    /// </summary>
    public string CourierService { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the shipment
    /// Values: "Created", "PickedUp", "InTransit", "OutForDelivery", "Delivered", "Failed", "Cancelled"
    /// </summary>
    public string ShipmentStatus { get; set; } = "Created";

    /// <summary>
    /// Cost of shipping this order
    /// </summary>
    public decimal ShippingCost { get; set; }

    /// <summary>
    /// Estimated delivery date from Shiplogic
    /// </summary>
    public DateTime? EstimatedDeliveryDate { get; set; }

    /// <summary>
    /// Actual delivery date (when package was delivered)
    /// </summary>
    public DateTime? ActualDeliveryDate { get; set; }

    /// <summary>
    /// When this shipment was created in our system
    /// </summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who/what created this shipment (usually "System")
    /// </summary>
    public string CreatedBy { get; set; } = "System";

    /// <summary>
    /// When this shipment was last modified
    /// </summary>
    /// <summary>
    /// Timestamp (UTC) of when this shipment's tracking number was first
    /// successfully pushed back to the originating storefront (WooCommerce
    /// order note today, future Shopify fulfilment later). Null means it has
    /// not been pushed yet. Used as a persisted idempotency marker so the
    /// booking path and the Shiplogic tracking-webhook path don't both spam
    /// the customer with duplicate "Your order has been shipped via …" notes.
    /// </summary>
    public DateTime? TrackingPushedOn { get; set; }

    public DateTime? ChangedOn { get; set; }

    /// <summary>
    /// Who last modified this shipment
    /// </summary>
    public string? ChangedBy { get; set; }

    // Navigation property
    public Order Order { get; set; } = null!;

    // Navigation property - the label for this shipment (one-to-one)
    public Label? Label { get; set; }

    // Navigation property - all tracking events for this shipment
    public ICollection<TrackingEvent> TrackingEvents { get; set; } = new List<TrackingEvent>();


}