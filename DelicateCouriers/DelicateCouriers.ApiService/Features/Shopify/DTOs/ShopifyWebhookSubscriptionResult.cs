namespace DelicateCouriers.ApiService.Features.Shopify.DTOs
{
    /// <summary>
    /// Outcome of subscribing a Shopify store to the order webhooks
    /// (orders/create, orders/updated, orders/cancelled) that feed the
    /// platform's order-ingestion pipeline. Mirrors the shape/style of
    /// <see cref="ShopifyCarrierServiceResult"/>.
    /// </summary>
    public class ShopifyWebhookSubscriptionResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The address we told Shopify to POST order webhooks to. Useful for
        /// the SuperAdmin UI to display / verify.
        /// </summary>
        public string? CallbackUrl { get; set; }

        /// <summary>Topics we newly subscribed on this run.</summary>
        public List<string> Created { get; set; } = new();

        /// <summary>Topics that were already subscribed at our address (no-op).</summary>
        public List<string> AlreadyPresent { get; set; } = new();

        /// <summary>Topics whose existing subscription pointed elsewhere and we re-pointed.</summary>
        public List<string> Updated { get; set; } = new();

        /// <summary>Topics Shopify refused to subscribe (e.g. missing scope).</summary>
        public List<string> Failed { get; set; } = new();

        /// <summary>
        /// Whether the store has a webhook secret on file. Webhooks created via
        /// the Admin API are HMAC-signed by Shopify with the store's own
        /// custom-app API secret; the receiver verifies against the store's
        /// webhook secret. If this is false, every delivery is guaranteed to be
        /// rejected with 401, so <see cref="Success"/> is forced to false even
        /// when all topics were subscribed — setup is not complete until the
        /// secret is saved. (A present-but-wrong secret cannot be detected here:
        /// the signing secret lives in the store's Shopify custom app and is
        /// never exposed over the Admin API, so there is nothing to compare to.)
        /// </summary>
        public bool WebhookSecretConfigured { get; set; }
    }
}
