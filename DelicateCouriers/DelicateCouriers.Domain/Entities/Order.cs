namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents an order received from a WooCommerce store.
/// Orders are created when WooCommerce sends webhooks to our platform.
/// Example: Customer orders a shirt, WooCommerce sends order data, we create this record.
/// </summary>
public class Order
{
    /// <summary>
    /// Unique identifier for the order in our system
    /// </summary>
    public int OrderID { get; set; }

    /// <summary>
    /// Foreign key to the tenant who owns this order
    /// </summary>
    public int TenantID { get; set; }

    /// <summary>
    /// Foreign key to the store this order came from
    /// </summary>
    public int StoreID { get; set; }

    /// <summary>
    /// Order ID from WooCommerce (their internal ID)
    /// Example: "12345"
    /// </summary>
    public string WooOrderID { get; set; } = string.Empty;

    /// <summary>
    /// Order number displayed to customer in WooCommerce
    /// Example: "ORD-2025-001"
    /// </summary>
    public string WooOrderNumber { get; set; } = string.Empty;

    /// <summary>
    /// Customer's full name
    /// Example: "John Doe"
    /// </summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>
    /// Customer's email address
    /// </summary>
    public string CustomerEmail { get; set; } = string.Empty;

    /// <summary>
    /// Customer's phone number
    /// </summary>
    public string CustomerPhone { get; set; } = string.Empty;

    /// <summary>
    /// Shipping address line 1
    /// Example: "123 Main Street"
    /// </summary>
    public string ShippingAddressLine1 { get; set; } = string.Empty;

    /// <summary>
    /// Shipping address line 2 (apartment, suite, etc.)
    /// Example: "Apt 4B"
    /// </summary>
    public string? ShippingAddressLine2 { get; set; }

    /// <summary>
    /// Shipping suburb / local area. Required by Shiplogic for accurate
    /// pickup and delivery scheduling. WooCommerce doesn't have a native
    /// suburb field on its address — the plugin sources this from billing/
    /// shipping address_2 (the conventional ZA placement).
    /// Example: "Sandhurst"
    /// </summary>
    public string? ShippingSuburb { get; set; }

    /// <summary>
    /// Shipping city
    /// Example: "Johannesburg"
    /// </summary>
    public string ShippingCity { get; set; } = string.Empty;

    /// <summary>
    /// Shipping province/state
    /// Example: "Gauteng"
    /// </summary>
    public string ShippingProvince { get; set; } = string.Empty;

    /// <summary>
    /// Shipping postal code
    /// Example: "2000"
    /// </summary>
    public string ShippingPostalCode { get; set; } = string.Empty;

    /// <summary>
    /// Shipping country
    /// Example: "South Africa"
    /// </summary>
    public string ShippingCountry { get; set; } = string.Empty;

    /// <summary>
    /// Total order amount (including shipping, tax)
    /// </summary>
    public decimal OrderTotal { get; set; }

    /// <summary>
    /// Current status of the order
    /// Values: "Pending", "Processing", "Completed", "Cancelled"
    /// </summary>
    public string OrderStatus { get; set; } = "Pending";

    /// <summary>
    /// When the customer placed this order in WooCommerce
    /// </summary>
    public DateTime OrderDate { get; set; }

    /// <summary>
    /// When we received this order in our system
    /// </summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who/what created this order (usually "System" from webhook)
    /// </summary>
    public string CreatedBy { get; set; } = "System";

    /// <summary>
    /// When this order was last modified
    /// </summary>
    public DateTime? ChangedOn { get; set; }

    /// <summary>
    /// Who last modified this order
    /// </summary>
    public string? ChangedBy { get; set; }

    public DateTime? RequestedDeliveryDate { get; set; }

    public string? RequestedDeliveryTime { get; set; }

    public DateTime? RequestedCollectionDate { get; set; }

    public string? RequestedCollectionTime { get; set; }

    public string? Occasion { get; set; }

    /// <summary>
    /// How the customer wants this order fulfilled. Sourced from the
    /// WooCommerce plugin payload (`fulfillment_type`).
    ///
    /// Known values:
    ///   "delivery"     — standard courier shipment (default)
    ///   "collect"      — customer collects in person at the merchant.
    ///                    The platform MUST NOT book a Shiplogic shipment
    ///                    for these orders. Enforced as a defensive guard
    ///                    at the top of CreateShipmentForOrderAsync so it
    ///                    cannot be bypassed by any future trigger path
    ///                    (webhook, manual admin button, retry queue).
    ///   "special_trip" — collection-flow on the merchant side but still
    ///                    booked through Shiplogic as an ad-hoc job.
    ///   null           — older plugin versions that don't send the field;
    ///                    treated as "delivery" so existing installations
    ///                    are unaffected.
    /// </summary>
    public string? FulfillmentType { get; set; }

    /// <summary>
    /// Woo shipping method id(s) the customer chose at checkout (e.g.
    /// "flat_rate", "local_pickup"), verified against the store's Woo REST
    /// API on intake. Persisted for auditability and so the pre-booking
    /// guard can re-apply the collection-keyword check without another
    /// REST round-trip. Null for orders that were never verified
    /// (Shopify, stores without Woo REST credentials, pre-existing rows).
    /// </summary>
    public string? ShippingMethodId { get; set; }

    /// <summary>
    /// Merchant-configured shipping method title(s) the customer chose at
    /// checkout (e.g. "Collect from 1 Clifford road, Irene…"). Some stores
    /// build paid collection points as `flat_rate` methods whose ONLY
    /// collection signal is this label — which is exactly why it must be
    /// checked and persisted.
    /// </summary>
    public string? ShippingMethodTitle { get; set; }

    /// <summary>
    /// Amount quoted to the customer for a "special_trip" order by the
    /// WooCommerce plugin (driving distance × merchant R/km). Used as the
    /// declared value on the Shiplogic SPX booking. Null for non-special-trip
    /// orders and for orders from plugin versions older than v2.7.0.
    /// </summary>
    public decimal? SpecialTripQuotedAmount { get; set; }

    /// <summary>
    /// Driving distance (km, store → customer) the special-trip quote was
    /// computed from. Audit/diagnostic value.
    /// </summary>
    public decimal? SpecialTripDistanceKm { get; set; }

    /// <summary>
    /// Customer latitude geocoded by the plugin at checkout for the
    /// special-trip quote. May be null even for special trips (geocode
    /// failure — the Distance Matrix lookup can still succeed by address).
    /// </summary>
    public double? SpecialTripCustomerLat { get; set; }

    /// <summary>
    /// Customer longitude geocoded by the plugin at checkout. See
    /// <see cref="SpecialTripCustomerLat"/>.
    /// </summary>
    public double? SpecialTripCustomerLng { get; set; }

    // Navigation properties
    public Tenant Tenant { get; set; } = null!;

    public Store Store { get; set; } = null!;

    // Navigation property - the shipment for this order (one-to-one)
    public Shipment? Shipment { get; set; }

    // Navigation property - all line items (products) in this order
    public ICollection<OrderLineItem> LineItems { get; set; } = new List<OrderLineItem>();
}