using System.Security.Cryptography;
using System.Text;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Webhooks;

/// <summary>
/// Reading and authenticating a signed request from the plugin.
///
/// One implementation for every plugin endpoint, deliberately. Signature checking that exists
/// in two places is signature checking that will differ in two places, and the weaker copy is
/// the one an attacker finds.
/// </summary>
public sealed class WebhookRequest
{
    private WebhookRequest(IActionResult? failure, Merchant? merchant, string? rawBody, int storeId)
    {
        Failure = failure;
        Merchant = merchant;
        RawBody = rawBody;
        StoreId = storeId;
    }

    /// <summary>Set when the request must be refused; return it unchanged.</summary>
    public IActionResult? Failure { get; }

    public Merchant? Merchant { get; }
    public string? RawBody { get; }
    public int StoreId { get; }

    public static async Task<WebhookRequest> ReadAsync(
        HttpRequest request, IMerchantDirectory merchants, ILogger logger)
    {
        // The raw bytes, before any model binding: the signature covers exactly what was sent,
        // and a round trip through a deserializer changes whitespace and key order.
        request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync();
            request.Body.Position = 0;
        }

        if (string.IsNullOrEmpty(rawBody))
        {
            return Refuse(new BadRequestObjectResult(new { error = "Empty payload" }));
        }

        var storeIdText = request.Headers["X-Store-ID"].ToString();
        if (string.IsNullOrEmpty(storeIdText))
        {
            return Refuse(new BadRequestObjectResult(new { error = "Missing or invalid X-Store-ID header" }));
        }
        if (!int.TryParse(storeIdText, out var storeId))
        {
            return Refuse(new BadRequestObjectResult(new { error = "Missing or invalid X-Store-ID header" }));
        }

        var merchant = merchants.Find(storeId);
        if (merchant is null)
        {
            logger.LogWarning("Plugin request rejected: store {StoreId} not found", storeId);
            return Refuse(new NotFoundObjectResult(new { error = "Store not found" }));
        }

        if (string.IsNullOrEmpty(merchant.WebhookSecret))
        {
            // A misconfiguration, never an implicit allow.
            logger.LogError("Plugin request rejected: store {StoreId} has no webhook secret", storeId);
            return Refuse(new ObjectResult(new { error = "Store webhook secret not configured" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            });
        }

        var signature = request.Headers["X-Plugin-Signature"].ToString();
        if (string.IsNullOrEmpty(signature))
        {
            return Refuse(new BadRequestObjectResult(new { error = "Missing signature header" }));
        }
        if (!SignatureIsValid(rawBody, signature, merchant.WebhookSecret))
        {
            logger.LogWarning("Plugin request rejected: invalid signature for store {StoreId}", storeId);
            return Refuse(new UnauthorizedObjectResult(new { error = "Invalid signature" }));
        }

        return new WebhookRequest(null, merchant, rawBody, storeId);
    }

    private static WebhookRequest Refuse(IActionResult failure) => new(failure, null, null, 0);

    /// <summary>
    /// Constant-time comparison. A compare that returns on the first wrong byte leaks, one
    /// request at a time, how much of a guess was right.
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
