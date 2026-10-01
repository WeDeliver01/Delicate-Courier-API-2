namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// Represents a WooCommerce store connected to the platform.
/// Each tenant can have multiple stores (e.g., different countries, brands).
/// Example: "ABC Fashion SA", "ABC Fashion UK"
/// </summary>
public class Store
{
    /// <summary>
    /// Unique identifier for the store
    /// </summary>
    public int StoreID { get; set; }

    /// <summary>
    /// Foreign key to the owning tenant
    /// </summary>
    public int TenantID { get; set; }

    /// <summary>
    /// Storefront platform this store belongs to.
    /// Examples: "woocommerce", "shopify".
    /// </summary>
    public string Platform { get; set; } = "woocommerce";

    /// <summary>
    /// Shopify store URL (e.g., "my-shop.myshopify.com").
    /// Null when Platform != "shopify".
    /// </summary>
    public string? ShopifyStoreUrl { get; set; }

    /// <summary>
    /// Shopify Admin API access token (will be encrypted in database).
    /// Null when Platform != "shopify".
    /// </summary>
    public string? ShopifyAccessToken { get; set; }

    /// <summary>
    /// Secret used to validate Shopify webhook HMAC signatures.
    /// Null when Platform != "shopify".
    /// </summary>
    public string? ShopifyWebhookSecret { get; set; }

    /// <summary>
    /// Display name of the store
    /// Example: "ABC Fashion South Africa Store"
    /// </summary>
    public string StoreName { get; set; } = string.Empty;

    /// <summary>
    /// WooCommerce store URL
    /// Example: "https://abcfashion.co.za"
    /// </summary>
    public string? WooCommerceURL { get; set; }

    /// <summary>
    /// WooCommerce REST API Consumer Key (will be encrypted in database)
    /// Used to authenticate with WooCommerce API
    /// </summary>
    public string? WooConsumerKey { get; set; }

    /// <summary>
    /// WooCommerce REST API Consumer Secret (will be encrypted in database)
    /// Used to authenticate with WooCommerce API
    /// </summary>
    public string? WooConsumerSecret { get; set; }

    /// <summary>
    /// Secret key for validating WooCommerce webhook signatures
    /// Used to verify that webhook requests are actually from WooCommerce
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Whether this store is currently active
    /// Inactive stores don't process orders
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional per-store override of which WooCommerce order statuses may
    /// trigger an automatic shipment booking. Comma-separated, lowercase
    /// (e.g. "completed" or "processing,completed").
    ///
    /// NULL/empty = platform default: book on any non-terminal status.
    /// When set, ONLY the listed statuses enqueue a booking — everything
    /// else (pending, on-hold, processing, unknown/blank) is saved as an
    /// order but never booked until a later webhook arrives with a listed
    /// status. Terminal statuses (cancelled/refunded/failed/trash) are
    /// never booked regardless of this setting.
    /// </summary>
    public string? BookingTriggerStatuses { get; set; }

    /// <summary>
    /// When this store was added to the system
    /// </summary>
    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User who added this store (email or username)
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// When this store was last modified (null if never modified)
    /// </summary>
    public DateTime? ChangedOn { get; set; }

    /// <summary>
    /// User who last modified this store (null if never modified)
    /// </summary>
    public string? ChangedBy { get; set; }

    #region Collection Address Configuration
    // Where shipments are picked up from (warehouse/store location)

    /// <summary>
    /// Collection address line 1 (street address of warehouse/store)
    /// Example: "123 Industrial Road"
    /// </summary>
    public string? CollectionAddressLine1 { get; set; }

    /// <summary>
    /// Collection address line 2 (unit, building, etc.)
    /// Example: "Unit 5B"
    /// </summary>
    public string? CollectionAddressLine2 { get; set; }

    /// <summary>
    /// Collection city
    /// Example: "Pretoria"
    /// </summary>
    public string? CollectionCity { get; set; }

    /// <summary>
    /// Collection province/state
    /// Example: "Gauteng"
    /// </summary>
    public string? CollectionProvince { get; set; }

    /// <summary>
    /// Collection postal code
    /// Example: "0001"
    /// </summary>
    public string? CollectionPostalCode { get; set; }

    /// <summary>
    /// Collection country (ISO code)
    /// Example: "ZA" for South Africa
    /// </summary>
    public string CollectionCountry { get; set; } = "ZA";

    // Collection Contact Configuration
    // Who the courier should contact for pickup

    /// <summary>
    /// Collection contact person name
    /// Example: "John Warehouse Manager"
    /// </summary>
    public string? CollectionContactName { get; set; }

    /// <summary>
    /// Collection contact phone number
    /// Example: "+27123456789"
    /// </summary>
    public string? CollectionContactPhone { get; set; }

    /// <summary>
    /// Collection contact email address
    /// Example: "warehouse@honeybee.co.za"
    /// </summary>
    public string? CollectionContactEmail { get; set; }

    /// <summary>
    /// Collection company/business name
    /// Example: "Honey Bee Bakery - Pretoria Branch"
    /// </summary>
    public string? CollectionCompanyName { get; set; }

    // Shipping Configuration
    /// <summary>
    /// Default Shiplogic service level code for this store
    /// Examples: "ECO" (Economy), "ONX" (Overnight Express), "EXSM" (Express Same Day)
    /// Can be overridden per order if needed
    /// </summary>
    /// 

    public double? CollectionLatitude { get; set; }

    public double? CollectionLongitude { get; set; }

    public int ShiplogicProviderId { get; set; } //= 35;

    public int ShiplogicAccountId { get; set; } //= 624164;

    //Service level - Required field for Shiplogic shipment creation
    public string DefaultServiceLevel { get; set; } = "STD";

    // Collection address suburb (local_area for Shiplogic)
    public string? CollectionSuburb { get; set; }

    // Collection time window
    public string? CollectionTimeFrom { get; set; }  // e.g., "08:00"
    public string? CollectionTimeTo { get; set; }    // e.g., "16:00"

    // Delivery time window  
    public string? DeliveryTimeFrom { get; set; }    // e.g., "08:00"
    public string? DeliveryTimeTo { get; set; }      // e.g., "17:00"

    // ── Special-trip fallback rate (Shopify carrier callback) ──
    // When Shiplogic returns no rate for a delivery address, offer a
    // "Special Trip" rate priced by one-way driving distance:
    //   price = max(SpecialTripMinFee, distanceKm × SpecialTripCostPerKm)
    // Enabled only when SpecialTripCostPerKm is set (> 0). Distance beyond
    // SpecialTripMaxKm (when set) offers nothing.
    public decimal? SpecialTripCostPerKm { get; set; }   // e.g. 9.00 (ZAR/km)
    public decimal? SpecialTripMinFee { get; set; }      // e.g. 150.00
    public decimal? SpecialTripMaxKm { get; set; }       // e.g. 200

    /// <summary>
    /// The merchant's OWN Google Maps API key (must have Distance Matrix
    /// API enabled). Special-trip distance lookups bill to the merchant,
    /// not the platform. Null/empty disables the special-trip fallback.
    /// </summary>
    public string? GoogleMapsApiKey { get; set; }

    /// <summary>
    /// Optional merchant-facing checkout rate label (e.g. "Standard baked
    /// goods delivery"). When set, WooCommerce checkout rates use this name
    /// instead of the Shiplogic service-level name, and the special-trip
    /// fallback is labeled "{CheckoutRateLabel} (X km)" instead of
    /// "Special Trip Request (X km)". Order intake detects special trips by
    /// this label + the km suffix, so both must stay in sync.
    /// </summary>
    public string? CheckoutRateLabel { get; set; }
    #endregion

    // Navigation property - the tenant that owns this store
    public Tenant Tenant { get; set; } = null!;

    // Navigation property - all orders from this store
    public ICollection<Order> Orders { get; set; } = new List<Order>();

    // Navigation property - package types configured for this store
    public ICollection<PackageType> PackageTypes { get; set; } = new List<PackageType>();
}