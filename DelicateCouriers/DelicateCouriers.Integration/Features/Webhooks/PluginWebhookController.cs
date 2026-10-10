using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Jobs;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Webhooks;

/// <summary>
/// Order webhooks from the Delicate Courier WooCommerce plugin.
///
/// Authentication is HMAC-SHA256 over the exact bytes received, keyed on the merchant's
/// webhook secret, compared against <c>X-Plugin-Signature</c>. Over the raw body rather than a
/// re-serialised object, because a round trip through a deserializer changes whitespace and
/// key order and the signature would never match.
/// </summary>
[ApiController]
[Route("api/webhooks/plugin")]
public class PluginWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IMerchantDirectory _merchants;
    private readonly IOrderQueue _queue;
    private readonly ILogger<PluginWebhookController> _logger;

    public PluginWebhookController(
        IMerchantDirectory merchants,
        IOrderQueue queue,
        ILogger<PluginWebhookController> logger)
    {
        _merchants = merchants;
        _queue = queue;
        _logger = logger;
    }

    [HttpPost("order")]
    public async Task<IActionResult> ReceiveOrder()
    {
        var rawBody = await ReadRawBodyAsync();
        if (string.IsNullOrEmpty(rawBody)) return BadRequest(new { error = "Empty payload" });

        PluginOrderPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PluginOrderPayload>(rawBody, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Plugin webhook rejected: invalid JSON");
            return BadRequest(new { error = "Invalid JSON" });
        }
        if (payload is null) return BadRequest(new { error = "Empty payload" });

        // The header wins over the body: the body is attacker-controlled until the signature
        // has been checked, and the signature is checked against the store the header names.
        var storeIdText = Request.Headers["X-Store-ID"].ToString();
        if (string.IsNullOrEmpty(storeIdText)) storeIdText = payload.StoreId;
        if (string.IsNullOrEmpty(storeIdText)) return BadRequest(new { error = "Missing Store ID" });
        if (!int.TryParse(storeIdText, out var storeId))
        {
            return BadRequest(new { error = "Invalid Store ID format" });
        }

        var merchant = _merchants.Find(storeId);
        if (merchant is null)
        {
            _logger.LogWarning("Plugin webhook rejected: store {StoreId} not found", storeId);
            return NotFound(new { error = "Store not found" });
        }

        if (string.IsNullOrEmpty(merchant.WebhookSecret))
        {
            // A misconfiguration, not an attack. Loud, and never treated as "allow".
            _logger.LogError("Plugin webhook rejected: store {StoreId} has no webhook secret", storeId);
            return StatusCode(500, new { error = "Store webhook secret not configured" });
        }

        var signature = Request.Headers["X-Plugin-Signature"].ToString();
        if (string.IsNullOrEmpty(signature))
        {
            return BadRequest(new { error = "Missing signature header" });
        }
        if (!SignatureIsValid(rawBody, signature, merchant.WebhookSecret))
        {
            _logger.LogWarning("Plugin webhook rejected: invalid signature for store {StoreId}", storeId);
            return Unauthorized(new { error = "Invalid signature" });
        }

        _logger.LogInformation(
            "Plugin webhook: event={Event} store={StoreId} wooOrderId={WooOrderId} status={Status}",
            payload.Event, storeId, payload.WooOrderId, payload.Status);

        // Hand it to the queue and answer. Whether this order becomes a delivery is decided
        // behind the webhook: the shopper's checkout is waiting on this response, and the
        // Engine being slow for ten seconds must not become the merchant's problem.
        _queue.Enqueue(storeId, rawBody);

        return Ok(new PluginWebhookResponse
        {
            Success = true,
            Message = "queued",
            OrderNumber = payload.OrderNumber
        });
    }

    private async Task<string> ReadRawBodyAsync()
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        Request.Body.Position = 0;
        return body;
    }

    /// <summary>
    /// Constant-time comparison of the base64 HMAC. A byte-by-byte compare that returns early
    /// leaks, one request at a time, how much of a guess was right.
    /// </summary>
    private static bool SignatureIsValid(string rawBody, string provided, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
