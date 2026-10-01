using System.Security.Cryptography;
using System.Text;

namespace DelicateCouriers.ApiService.Infrastructure.Services
{
    /// <summary>
    /// Validates webhook signatures from WooCommerce
    /// Ensures webhooks are legitimate and not from attackers
    /// Critical for security - prevents fake orders being created
    /// </summary>
    public class WebhookSignatureValidator
    {
        private readonly ILogger<WebhookSignatureValidator> _logger;

        public WebhookSignatureValidator(ILogger<WebhookSignatureValidator> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Validate that a webhook request is actually from WooCommerce
        /// </summary>
        /// <param name="payload">The raw JSON body from the webhook</param>
        /// <param name="signature">The X-WC-Webhook-Signature header value</param>
        /// <param name="secret">The webhook secret configured in WooCommerce</param>
        /// <returns>True if signature is valid, false if potentially malicious</returns>
        public bool ValidateSignature(string payload, string signature, string secret)
        {
            try
            {
                if (string.IsNullOrEmpty(payload))
                {
                    _logger.LogWarning("Webhook validation failed: Empty payload");

                    return false;
                }

                if (string.IsNullOrEmpty(signature))
                {
                    _logger.LogWarning("Webhook validation failed: Missing signature header");

                    return false;
                }

                if (string.IsNullOrEmpty(secret))
                {
                    _logger.LogWarning("Webhook validation failed: No webhook secret configured");

                    return false;
                }

                // WooCommerce uses HMAC-SHA256 to sign webhooks
                var computedSignature = ComputeSignature(payload, secret);

                // Use constant-time comparison to prevent timing attacks
                var isValid = ConstantTimeEquals(signature, computedSignature);

                if (!isValid)
                {
                    _logger.LogWarning("Webhook validation failed: Signature mismatch. Expected: {Expected}, Got: {Actual}", computedSignature, signature);
                }
                else
                {
                    _logger.LogInformation("Webhook signature validated successfully");
                }

                return isValid;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception during webhook signature validation");

                return false;
            }
        }

        /// <summary>
        /// Compute HMAC-SHA256 signature for webhook payload
        /// This is how WooCommerce signs their webhooks
        /// </summary>
        private string ComputeSignature(string payload, string secret)
        {
            var encoding = new UTF8Encoding();
            var keyBytes = encoding.GetBytes(secret);
            var payloadBytes = encoding.GetBytes(payload);

            using var hmac = new HMACSHA256(keyBytes);
            var hashBytes = hmac.ComputeHash(payloadBytes);

            // Convert to base64 string (WooCommerce format)
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// Constant-time string comparison to prevent timing attacks
        /// Regular string comparison (==) can leak information about the secret
        /// </summary>
        private bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            if (a.Length != b.Length)
            {
                return false;
            }

            var result = 0;
            for (var i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }

            return result == 0;
        }

        /// <summary>
        /// Validate webhook and extract store information
        /// Returns store ID if valid, null if invalid
        /// </summary>
        public async Task<int?> ValidateAndGetStoreIdAsync(string payload, string signature,string storeUrl, Func<string, Task<(int storeId, string webhookSecret)?>> getStoreByUrl)
        {
            try
            {
                // Get store information by URL
                var storeInfo = await getStoreByUrl(storeUrl);

                if (storeInfo == null)
                {
                    _logger.LogWarning("Webhook validation failed: Store not found for URL {StoreUrl}", storeUrl);

                    return null;
                }

                var (storeId, webhookSecret) = storeInfo.Value;

                // Validate signature
                if (!ValidateSignature(payload, signature, webhookSecret))
                {
                    _logger.LogWarning("Webhook validation failed for StoreID {StoreId}", storeId);

                    return null;
                }

                _logger.LogInformation("Webhook validated successfully for StoreID {StoreId}", storeId);

                return storeId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception validating webhook for store URL {StoreUrl}", storeUrl);

                return null;
            }
        }
    }
}