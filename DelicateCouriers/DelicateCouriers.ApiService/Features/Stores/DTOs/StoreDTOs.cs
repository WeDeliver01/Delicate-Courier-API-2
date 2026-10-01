namespace DelicateCouriers.ApiService.Features.Stores.DTOs
{
    // Request to create a new store
    public class CreateStoreRequest
    {
        public int TenantID { get; set; }
        public string StoreName { get; set; } = string.Empty;

        // Storefront platform: "woocommerce" (default) or "shopify"
        public string Platform { get; set; } = "woocommerce";

        // WooCommerce credentials (required when Platform == "woocommerce")
        public string? WooCommerceURL { get; set; }
        public string? WooConsumerKey { get; set; }
        public string? WooConsumerSecret { get; set; }
        public string? WebhookSecret { get; set; }

        // Shopify credentials (required when Platform == "shopify")
        public string? ShopifyStoreUrl { get; set; }
        public string? ShopifyAccessToken { get; set; }
        public string? ShopifyWebhookSecret { get; set; }

        // Collection Address
        public string? CollectionAddressLine1 { get; set; }
        public string? CollectionAddressLine2 { get; set; }
        public string? CollectionCity { get; set; }
        public string? CollectionProvince { get; set; }
        public string? CollectionPostalCode { get; set; }
        public string CollectionCountry { get; set; } = "ZA";

        // Collection Contact
        public string? CollectionContactName { get; set; }
        public string? CollectionContactPhone { get; set; }
        public string? CollectionContactEmail { get; set; }
        public string? CollectionCompanyName { get; set; }

        // Shiplogic Configuration
        public int ShiplogicProviderId { get; set; }
        public int ShiplogicAccountId { get; set; }
        public string DefaultServiceLevel { get; set; } = "STD";

        // Special Trip fallback (distance-priced rate when Shiplogic has no rate)
        public decimal? SpecialTripCostPerKm { get; set; }
        public decimal? SpecialTripMinFee { get; set; }
        public decimal? SpecialTripMaxKm { get; set; }
        // Merchant's own Google Maps API key (Distance Matrix billing is theirs)
        public string? GoogleMapsApiKey { get; set; }
        // Optional customer-facing checkout rate label (replaces "Standard" / "Special Trip Request")
        public string? CheckoutRateLabel { get; set; }
    }

    // Request to update an existing store
    public class UpdateStoreRequest
    {
        public string? StoreName { get; set; }

        // Storefront platform: "woocommerce" or "shopify". Null = leave unchanged.
        public string? Platform { get; set; }

        public string? WooCommerceURL { get; set; }
        public string? WooConsumerKey { get; set; }
        public string? WooConsumerSecret { get; set; }
        public string? WebhookSecret { get; set; }

        // Shopify credentials
        public string? ShopifyStoreUrl { get; set; }
        public string? ShopifyAccessToken { get; set; }
        public string? ShopifyWebhookSecret { get; set; }

        public bool? IsActive { get; set; }

        // Collection Address
        public string? CollectionAddressLine1 { get; set; }
        public string? CollectionAddressLine2 { get; set; }
        public string? CollectionCity { get; set; }
        public string? CollectionProvince { get; set; }
        public string? CollectionPostalCode { get; set; }
        public string? CollectionCountry { get; set; }

        // Collection Contact
        public string? CollectionContactName { get; set; }
        public string? CollectionContactPhone { get; set; }
        public string? CollectionContactEmail { get; set; }
        public string? CollectionCompanyName { get; set; }

        // Shiplogic Configuration
        public int? ShiplogicProviderId { get; set; }
        public int? ShiplogicAccountId { get; set; }
        public string? DefaultServiceLevel { get; set; }

        // Special Trip fallback. Null = leave unchanged; 0 CostPerKm disables.
        public decimal? SpecialTripCostPerKm { get; set; }
        public decimal? SpecialTripMinFee { get; set; }
        public decimal? SpecialTripMaxKm { get; set; }
        // Merchant's own Google Maps API key. Null = unchanged; "" = clear.
        public string? GoogleMapsApiKey { get; set; }
        // Customer-facing checkout rate label. Null = unchanged; "" = clear (back to defaults).
        public string? CheckoutRateLabel { get; set; }
    }

    // Response when returning store data
    public class StoreResponse
    {
        public int StoreID { get; set; }
        public int TenantID { get; set; }
        public string TenantName { get; set; } = string.Empty;
        public string StoreName { get; set; } = string.Empty;
        public string WooCommerceURL { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime CreatedOn { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime? ChangedOn { get; set; }
        public string? ChangedBy { get; set; }

        // Credential status flags
        public bool HasWooConsumerKey { get; set; }
        public bool HasWooConsumerSecret { get; set; }
        public bool HasWebhookSecret { get; set; }

        // Actual credentials (for editing)
        public string? WooConsumerKey { get; set; }
        public string? WooConsumerSecret { get; set; }
        public string? WebhookSecret { get; set; }

        // Collection Address
        public string? CollectionAddressLine1 { get; set; }
        public string? CollectionAddressLine2 { get; set; }
        public string? CollectionCity { get; set; }
        public string? CollectionProvince { get; set; }
        public string? CollectionPostalCode { get; set; }
        public string CollectionCountry { get; set; } = "ZA";

        // Collection Contact
        public string? CollectionContactName { get; set; }
        public string? CollectionContactPhone { get; set; }
        public string? CollectionContactEmail { get; set; }
        public string? CollectionCompanyName { get; set; }

        // Shiplogic Configuration
        public int ShiplogicProviderId { get; set; }
        public int ShiplogicAccountId { get; set; }
        public string DefaultServiceLevel { get; set; } = "STD";

        // Special Trip fallback configuration
        public decimal? SpecialTripCostPerKm { get; set; }
        public decimal? SpecialTripMinFee { get; set; }
        public decimal? SpecialTripMaxKm { get; set; }
        public bool HasGoogleMapsApiKey { get; set; }
        public string? CheckoutRateLabel { get; set; }
    }

    // Response with validation result
    public class StoreConnectionTestResponse
    {
        public bool IsConnected { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? WooCommerceVersion { get; set; }
    }
}