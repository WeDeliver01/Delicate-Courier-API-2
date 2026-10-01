namespace DelicateCouriers.ApiService.Features.Shopify.DTOs
{
    /// <summary>
    /// Outcome of listing the carrier services registered on a Shopify store
    /// (via GET /admin/api/{version}/carrier_services.json). Read-only — useful
    /// for verifying that the Delicate Couriers carrier service is present and
    /// pointing at the correct live-rate callback.
    /// </summary>
    public class ShopifyCarrierServiceListResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        public List<ShopifyCarrierServiceItem> CarrierServices { get; set; } = new();
    }

    public class ShopifyCarrierServiceItem
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? CallbackUrl { get; set; }
        public bool Active { get; set; }
    }
}
