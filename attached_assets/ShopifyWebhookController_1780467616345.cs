// Features/Shopify/ShopifyWebhookController.cs
//
// REPLACEMENT for the existing controller. The differences from the
// current production version:
//
//   1. After signature verification, we now dispatch the payload through
//      ShopifyOrderIngestionService (instead of just logging that we
//      received it).
//
//   2. Topic-based routing: orders/create, orders/updated, orders/cancelled
//      all run through the same ingestion path. Other topics are accepted
//      (200 OK) but ignored — Shopify can be configured to send many
//      topics, and we don't want to 4xx on ones we don't care about
//      because Shopify will retry indefinitely.
//
//   3. The route is unchanged: POST /api/webhooks/shopify/order — your
//      existing wired-up tests and merchant-facing docs still work.
//
// MIGRATION NOTE: keep the original file's NormaliseHost helper. I've
// reproduced it here for completeness. Diff this against the current
// production controller before merging — the only functional change is
// the ingestion-service call near the end.

using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace DelicateCouriers.ApiService.Features.Shopify;

[ApiController]
[Route("api/webhooks/shopify")]
[EnableRateLimiting("WebhookIp")]
public class ShopifyWebhookController : ControllerBase
{
    private readonly WebhookSignatureValidator _signatureValidator;
    private readonly AppDbContext _context;
    private readonly ShopifyOrderIngestionService _ingestionService;
    private readonly ILogger<ShopifyWebhookController> _logger;

    private static readonly HashSet<string> ProcessableTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        "orders/create",
        "orders/updated",
        "orders/cancelled"
    };

    public ShopifyWebhookController(
        WebhookSignatureValidator signatureValidator,
        AppDbContext context,
        ShopifyOrderIngestionService ingestionService,
        ILogger<ShopifyWebhookController> logger)
    {
        _signatureValidator = signatureValidator;
        _context = context;
        _ingestionService = ingestionService;
        _logger = logger;
    }

    [HttpPost("order")]
    public async Task<IActionResult> ReceiveShopifyOrder()
    {
        try
        {
            // 1. Read the raw body BEFORE model binding.
            Request.EnableBuffering();
            string rawBody;
            using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
            {
                rawBody = await reader.ReadToEndAsync();
                Request.Body.Position = 0;
            }

            if (string.IsNullOrEmpty(rawBody))
            {
                _logger.LogWarning("Shopify webhook rejected: empty payload");
                return BadRequest(new { error = "Empty payload" });
            }

            // 2. Headers
            var signature = Request.Headers["X-Shopify-Hmac-Sha256"].ToString();
            var shopDomain = Request.Headers["X-Shopify-Shop-Domain"].ToString();
            var topic = Request.Headers["X-Shopify-Topic"].ToString();

            if (string.IsNullOrEmpty(shopDomain))
            {
                return BadRequest(new { error = "Missing X-Shopify-Shop-Domain header" });
            }
            if (string.IsNullOrEmpty(signature))
            {
                return BadRequest(new { error = "Missing signature header" });
            }

            // 3. Resolve store
            var normalisedShopHost = NormaliseHost(shopDomain);
            var candidateStores = await _context.Stores
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.IsActive && s.Platform == "shopify" && s.ShopifyStoreUrl != null)
                .Select(s => new { s.StoreID, s.ShopifyStoreUrl, s.ShopifyWebhookSecret })
                .ToListAsync();

            var store = candidateStores.FirstOrDefault(s =>
                string.Equals(NormaliseHost(s.ShopifyStoreUrl!), normalisedShopHost, StringComparison.OrdinalIgnoreCase));

            if (store == null)
            {
                _logger.LogWarning("Shopify webhook rejected: no active store for domain {Shop}", shopDomain);
                return NotFound(new { error = "Store not found" });
            }

            if (string.IsNullOrEmpty(store.ShopifyWebhookSecret))
            {
                _logger.LogError("Shopify webhook rejected: Store {StoreId} has no ShopifyWebhookSecret", store.StoreID);
                return StatusCode(500, new { error = "Store webhook secret not configured" });
            }

            // 4. Verify signature
            if (!_signatureValidator.ValidateSignature(rawBody, signature, store.ShopifyWebhookSecret))
            {
                _logger.LogWarning(
                    "Shopify webhook rejected: invalid signature for Store {StoreId} (Topic: {Topic})",
                    store.StoreID, topic);
                return Unauthorized(new { error = "Invalid signature" });
            }

            // 5. Route by topic
            if (!ProcessableTopics.Contains(topic))
            {
                // We accept (but don't process) topics we don't recognise.
                // Returning 200 prevents Shopify from retrying — if a merchant
                // accidentally subscribes us to "orders/paid" or similar, we
                // don't want to keep getting hammered with retries.
                _logger.LogInformation(
                    "Shopify webhook accepted but not processed (unhandled topic). Topic: {Topic}, Store: {StoreId}",
                    topic, store.StoreID);
                return Ok(new { received = true, processed = false, reason = "topic not handled" });
            }

            // 6. Dispatch to ingestion
            var result = await _ingestionService.IngestOrderAsync(store.StoreID, topic, rawBody);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Shopify webhook ingested. Topic: {Topic}, Store: {StoreId}, OrderId: {OrderId}",
                    topic, store.StoreID, result.OrderId);
                return Ok(new { received = true, processed = true, orderId = result.OrderId });
            }

            // Soft failure (validation, etc.) — 200 with details, no Shopify retry.
            _logger.LogWarning(
                "Shopify webhook accepted but ingestion failed. Topic: {Topic}, Store: {StoreId}, Reason: {Reason}",
                topic, store.StoreID, result.Message);
            return Ok(new { received = true, processed = false, reason = result.Message });
        }
        catch (Exception ex)
        {
            // Unexpected error: return 500 so Shopify retries the webhook.
            // Shopify's retry schedule (~19 attempts over 48 hours) gives
            // us a recovery window if e.g. the DB is briefly down.
            _logger.LogError(ex, "Exception processing Shopify webhook");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpGet("health")]
    public IActionResult HealthCheck() => Ok(new
    {
        status = "healthy",
        service = "Delicate Couriers Shopify Webhook Receiver",
        timestamp = DateTime.UtcNow
    });

    private static string NormaliseHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }
        var withoutScheme = trimmed
            .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
        var slashIndex = withoutScheme.IndexOf('/');
        if (slashIndex >= 0) withoutScheme = withoutScheme.Substring(0, slashIndex);
        return withoutScheme.TrimEnd('/').ToLowerInvariant();
    }
}
