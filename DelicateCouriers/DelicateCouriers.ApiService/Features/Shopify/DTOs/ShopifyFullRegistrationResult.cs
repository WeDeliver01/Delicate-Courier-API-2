namespace DelicateCouriers.ApiService.Features.Shopify.DTOs
{
    /// <summary>
    /// Outcome of running the full Shopify store setup in one call: verify the
    /// connection, register the carrier service (live checkout rates), and
    /// subscribe the order webhooks. Each step's own result is preserved so the
    /// caller can see exactly what succeeded or failed. Steps run in order and a
    /// failed connection short-circuits the rest (carrier service / webhooks
    /// would fail anyway with a bad token).
    /// </summary>
    public class ShopifyFullRegistrationResult
    {
        /// <summary>True only when every attempted step succeeded.</summary>
        public bool Success { get; set; }

        public string Message { get; set; } = string.Empty;

        /// <summary>Connection test result (always attempted first).</summary>
        public ShopifyConnectionResponse Connection { get; set; } = new();

        /// <summary>
        /// Carrier-service registration result. Null if it was skipped because
        /// the connection test failed.
        /// </summary>
        public ShopifyCarrierServiceResult? CarrierService { get; set; }

        /// <summary>
        /// Order-webhook subscription result. Null if it was skipped because the
        /// connection test failed.
        /// </summary>
        public ShopifyWebhookSubscriptionResult? Webhooks { get; set; }
    }
}
