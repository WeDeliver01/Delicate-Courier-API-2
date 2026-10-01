using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce.DTOs;
using DelicateCouriers.ApiService.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Receives webhooks from the Delicate Courier WooCommerce plugin.
    ///
    /// Authentication: HMAC-SHA256(rawBody, store.WebhookSecret) — base64 — must
    /// match the X-Plugin-Signature header. Without this any caller who knew or
    /// guessed a StoreID could inject orders for that tenant. The store must
    /// have a WebhookSecret configured; if it doesn't, the request is rejected
    /// with 500 (configuration error) so it's obvious to the operator.
    /// </summary>
    [ApiController]
    [Route("api/webhooks/plugin")]
    [EnableRateLimiting("WebhookIp")]
    public class PluginWebhookController : ControllerBase
    {
        private readonly PluginWebhookService _pluginWebhookService;
        private readonly WebhookSignatureValidator _signatureValidator;
        private readonly AppDbContext _context;
        private readonly ILogger<PluginWebhookController> _logger;
        private readonly ISystemEventLogger _systemEvents;

        public PluginWebhookController(
            PluginWebhookService pluginWebhookService,
            WebhookSignatureValidator signatureValidator,
            AppDbContext context,
            ILogger<PluginWebhookController> logger,
            ISystemEventLogger systemEvents)
        {
            _pluginWebhookService = pluginWebhookService;
            _signatureValidator = signatureValidator;
            _context = context;
            _logger = logger;
            _systemEvents = systemEvents;
        }

        [HttpPost("order")]
        public async Task<IActionResult> ReceivePluginOrder()
        {
            try
            {
                // Read raw body for signature validation BEFORE model binding,
                // since signing is over the exact bytes the client sent.
                string rawBody;
                Request.EnableBuffering();
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
                {
                    rawBody = await reader.ReadToEndAsync();
                    Request.Body.Position = 0;
                }

                if (string.IsNullOrEmpty(rawBody))
                {
                    return BadRequest(new { error = "Empty payload" });
                }

                PluginWebhookPayload? payload;
                try
                {
                    payload = JsonSerializer.Deserialize<PluginWebhookPayload>(rawBody, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Plugin webhook rejected: invalid JSON");
                    return BadRequest(new { error = "Invalid JSON" });
                }

                if (payload == null)
                {
                    return BadRequest(new { error = "Empty payload" });
                }

                var storeIdHeader = Request.Headers["X-Store-ID"].ToString();
                var storeIdString = !string.IsNullOrEmpty(storeIdHeader) ? storeIdHeader : payload.StoreId;
                if (string.IsNullOrEmpty(storeIdString))
                {
                    _logger.LogWarning("Plugin webhook received without Store ID");
                    return BadRequest(new { error = "Missing Store ID" });
                }
                if (!int.TryParse(storeIdString, out var storeId))
                {
                    _logger.LogWarning("Plugin webhook received with invalid Store ID: {StoreId}", storeIdString);
                    return BadRequest(new { error = "Invalid Store ID format" });
                }

                // Look up the store (webhooks are unauthenticated — bypass tenant filter).
                var store = await _context.Stores
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.StoreID == storeId && s.IsActive);

                if (store == null)
                {
                    _logger.LogWarning("Plugin webhook rejected: Store {StoreId} not found or inactive", storeId);
                    return NotFound(new { error = "Store not found" });
                }

                if (string.IsNullOrEmpty(store.WebhookSecret))
                {
                    _logger.LogError(
                        "Plugin webhook rejected: Store {StoreId} has no WebhookSecret configured", storeId);
                    return StatusCode(500, new { error = "Store webhook secret not configured" });
                }

                var signature = Request.Headers["X-Plugin-Signature"].ToString();
                if (string.IsNullOrEmpty(signature))
                {
                    _logger.LogWarning(
                        "Plugin webhook rejected: missing X-Plugin-Signature header for Store {StoreId}", storeId);
                    return BadRequest(new { error = "Missing signature header" });
                }

                if (!_signatureValidator.ValidateSignature(rawBody, signature, store.WebhookSecret))
                {
                    // Emit body + secret fingerprints (NOT the secret itself) so we can
                    // tell whether the mismatch is caused by the body bytes differing
                    // in flight vs the plugin and backend using a different secret.
                    var bodyBytes = Encoding.UTF8.GetBytes(rawBody);
                    var secretBytes = Encoding.UTF8.GetBytes(store.WebhookSecret);
                    string Sha(byte[] b) => Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant();
                    _logger.LogWarning(
                        "Plugin webhook rejected: invalid signature for Store {StoreId}. " +
                        "BodyLen={BodyLen} BodySha256={BodySha} SecretLen={SecretLen} SecretSha256={SecretSha}",
                        storeId, bodyBytes.Length, Sha(bodyBytes), secretBytes.Length, Sha(secretBytes));
                    return Unauthorized(new { error = "Invalid signature" });
                }

                _logger.LogInformation(
                    "Received plugin webhook. Event: {Event}, StoreID: {StoreId}, WooOrderID: {WooOrderId}, Status: {Status}",
                    payload.Event, storeId, payload.WooOrderId, payload.Status);

                var result = await _pluginWebhookService.ProcessPluginOrderAsync(storeId, payload);

                if (result.Success)
                {
                    _logger.LogInformation(
                        "Plugin webhook processed successfully. OrderID: {OrderId}, OrderNumber: {OrderNumber}",
                        result.OrderId, result.OrderNumber);

                    await _systemEvents.LogAsync(new SystemEventEntry
                    {
                        EventType = "order.received",
                        ActorKind = "Webhook",
                        ActorLabel = $"DC Plugin {store.StoreName}",
                        TenantId = store.TenantID,
                        EntityType = "Order",
                        EntityRef = result.OrderNumber != null ? $"#{result.OrderNumber}" : null,
                        Message = $"Order #{result.OrderNumber} received from plugin ({payload.Status})",
                        Details = new
                        {
                            orderId = result.OrderId,
                            wooOrderId = payload.WooOrderId,
                            storeId = store.StoreID,
                            status = payload.Status,
                            evt = payload.Event,
                        },
                    });

                    return Ok(result);
                }

                _logger.LogWarning("Plugin webhook processing failed: {Message}", result.Message);
                return BadRequest(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception processing plugin webhook");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        /// <summary>
        /// Signed waybill (label PDF) download for the WooCommerce plugin.
        /// POST body: {"wc_order_id": 12345}. Auth: same HMAC scheme as the
        /// order webhook — X-Store-ID + X-Plugin-Signature over the raw body.
        /// Returns the label PDF for the latest shipment on that order.
        /// </summary>
        [HttpPost("label")]
        public async Task<IActionResult> DownloadLabel(
            [FromServices] DelicateCouriers.Features.Shiplogic.LabelService labelService,
            [FromServices] DelicateCouriers.Features.Shiplogic.IShiplogicService shiplogicService)
        {
            try
            {
                string rawBody;
                Request.EnableBuffering();
                using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
                {
                    rawBody = await reader.ReadToEndAsync();
                    Request.Body.Position = 0;
                }
                if (string.IsNullOrEmpty(rawBody))
                    return BadRequest(new { error = "Empty payload" });

                int wcOrderId;
                try
                {
                    using var doc = JsonDocument.Parse(rawBody);
                    if (!doc.RootElement.TryGetProperty("wc_order_id", out var idEl)
                        || !idEl.TryGetInt32(out wcOrderId) || wcOrderId <= 0)
                        return BadRequest(new { error = "wc_order_id (int) is required" });
                }
                catch (JsonException)
                {
                    return BadRequest(new { error = "Invalid JSON" });
                }

                var storeIdHeader = Request.Headers["X-Store-ID"].ToString();
                if (!int.TryParse(storeIdHeader, out var storeId))
                    return BadRequest(new { error = "Missing or invalid X-Store-ID header" });

                var store = await _context.Stores
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.StoreID == storeId && s.IsActive);
                if (store == null)
                    return NotFound(new { error = "Store not found" });
                if (string.IsNullOrEmpty(store.WebhookSecret))
                    return StatusCode(500, new { error = "Store webhook secret not configured" });

                var signature = Request.Headers["X-Plugin-Signature"].ToString();
                if (string.IsNullOrEmpty(signature)
                    || !_signatureValidator.ValidateSignature(rawBody, signature, store.WebhookSecret))
                {
                    _logger.LogWarning("Label download rejected: invalid signature for Store {StoreId}", storeId);
                    return Unauthorized(new { error = "Invalid signature" });
                }

                var wcOrderIdString = wcOrderId.ToString();
                var order = await _context.Orders
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.StoreID == storeId && o.WooOrderID == wcOrderIdString);
                if (order == null)
                    return NotFound(new { error = "Order not found on platform" });

                var shipment = await _context.Shipments
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(s => s.OrderID == order.OrderID)
                    .OrderByDescending(s => s.ShipmentID)
                    .FirstOrDefaultAsync();
                if (shipment == null)
                    return NotFound(new { error = "No shipment exists for this order yet" });

                var pdfBytes = await labelService.GetLabelBytesAsync(shipment.ShipmentID);

                // Not cached yet — fetch on demand from Shiplogic using the
                // tenant's bearer token, then persist for future downloads.
                if ((pdfBytes == null || pdfBytes.Length == 0)
                    && !string.IsNullOrWhiteSpace(shipment.ConsignmentID))
                {
                    var tenant = await _context.Tenants
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(t => t.TenantID == store.TenantID);
                    if (tenant != null && !string.IsNullOrWhiteSpace(tenant.ShiplogicBearerToken))
                    {
                        try
                        {
                            pdfBytes = await shiplogicService.GetLabelAsync(
                                tenant.ShiplogicBearerToken, shipment.ConsignmentID);
                            if (pdfBytes is { Length: > 0 }
                                && int.TryParse(shipment.ConsignmentID, out var shiplogicId))
                            {
                                await labelService.SaveLabelAsync(shipment.ShipmentID, pdfBytes, shiplogicId);
                            }
                        }
                        catch (Exception fetchEx)
                        {
                            _logger.LogWarning(fetchEx,
                                "On-demand Shiplogic label fetch failed for Shipment {ShipmentId} (Consignment {ConsignmentId})",
                                shipment.ShipmentID, shipment.ConsignmentID);
                        }
                    }
                }

                if (pdfBytes == null || pdfBytes.Length == 0)
                    return NotFound(new { error = "Waybill PDF not available for this shipment yet" });

                var fileName = !string.IsNullOrWhiteSpace(shipment.TrackingNumber)
                    ? $"waybill_{shipment.TrackingNumber}.pdf"
                    : $"waybill_shipment_{shipment.ShipmentID}.pdf";

                _logger.LogInformation(
                    "Plugin label download: Store {StoreId}, WC order {WcOrderId}, Shipment {ShipmentId}, {Size} bytes",
                    storeId, wcOrderId, shipment.ShipmentID, pdfBytes.Length);

                return File(pdfBytes, "application/pdf", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception serving plugin label download");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            return Ok(new
            {
                status = "healthy",
                service = "Delicate Couriers Plugin Webhook Receiver",
                timestamp = DateTime.UtcNow
            });
        }
    }
}
