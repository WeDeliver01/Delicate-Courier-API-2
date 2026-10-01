namespace DelicateCouriers.ApiService.Features.Shopify
{
    /// <summary>
    /// Single source of truth for the Shopify Admin API version used across
    /// the platform (ShopifyService, the tracking write-back job, and the
    /// carrier-service registration call). Keeping this in one place avoids
    /// the scaffold's drift where different files targeted different versions.
    /// </summary>
    public static class ShopifyApiConstants
    {
        public const string ApiVersion = "2024-04";
    }
}
