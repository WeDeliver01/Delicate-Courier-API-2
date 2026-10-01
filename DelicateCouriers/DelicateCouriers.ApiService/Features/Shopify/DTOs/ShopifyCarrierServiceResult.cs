namespace DelicateCouriers.ApiService.Features.Shopify.DTOs
{
    /// <summary>
    /// Outcome of registering (or updating) the Shopify Carrier Service that
    /// powers live checkout rates for a store.
    /// </summary>
    public class ShopifyCarrierServiceResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The callback URL we told Shopify to hit for live rates. Useful for
        /// the SuperAdmin UI to display / verify.
        /// </summary>
        public string? CallbackUrl { get; set; }
    }
}
