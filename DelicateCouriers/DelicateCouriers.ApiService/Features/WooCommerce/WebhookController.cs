using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using System.Text;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Controller for receiving webhooks from WooCommerce.
    /// This endpoint does NOT require authentication — WooCommerce can't send JWT tokens.
    /// Security is handled via mandatory webhook HMAC signature validation.
    /// </summary>
    [ApiController]
    [Route("api/webhooks/woocommerce")]
    [EnableRateLimiting("WebhookIp")]
    public class WebhookController : ControllerBase
    {
        private readonly WebhookService _webhookService;
        private readonly ILogger<WebhookController> _logger;

        public WebhookController(WebhookService webhookService, ILogger<WebhookController> logger)
        {
            _webhookService = webhookService;
            _logger = logger;
        }

        /// <summary>
        /// Receive order webhooks from WooCommerce.
        /// </summary>
        [HttpPost("order")]
        public async Task<IActionResult> ReceiveOrderWebhook()
        {
            try
            {
                // Step 1: Read the raw body (JSON payload)
                string payload;
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
                {
                    payload = await reader.ReadToEndAsync();
                }

                if (string.IsNullOrEmpty(payload))
                {
                    _logger.LogWarning("Received empty webhook payload");
                    return BadRequest(new { error = "Empty payload" });
                }

                // Step 2: Extract WooCommerce headers
                var signature = Request.Headers["X-WC-Webhook-Signature"].ToString();
                var topic = Request.Headers["X-WC-Webhook-Topic"].ToString();
                var source = Request.Headers["X-WC-Webhook-Source"].ToString();

                _logger.LogInformation("Received WooCommerce webhook. Topic: {Topic}, Source: {Source}", topic, source);

                // Step 3: Mandatory headers — both signature and source must be present.
                // Anything else is rejected; signature absence used to be tolerated for
                // testing but that allowed forged orders to be created.
                if (string.IsNullOrEmpty(signature))
                {
                    _logger.LogWarning("Webhook rejected: missing X-WC-Webhook-Signature header");
                    return BadRequest(new { error = "Missing signature header" });
                }

                if (string.IsNullOrEmpty(source))
                {
                    _logger.LogWarning("Webhook rejected: missing X-WC-Webhook-Source header");
                    return BadRequest(new { error = "Missing source header" });
                }

                // Step 4: Process the webhook (signature is verified inside the service).
                var result = await _webhookService.ProcessOrderWebhookAsync(payload, signature, source, topic);

                if (result.Success)
                {
                    _logger.LogInformation("Webhook processed successfully. OrderID: {OrderId}", result.OrderId);
                    return Ok(result);
                }
                else
                {
                    _logger.LogWarning("Webhook processing failed: {Message}", result.Message);
                    return BadRequest(result);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception processing webhook");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Health check endpoint for WooCommerce webhook configuration.
        /// </summary>
        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            return Ok(new
            {
                status = "healthy",
                service = "Delicate Couriers Webhook Receiver",
                timestamp = DateTime.UtcNow
            });
        }
    }
}
