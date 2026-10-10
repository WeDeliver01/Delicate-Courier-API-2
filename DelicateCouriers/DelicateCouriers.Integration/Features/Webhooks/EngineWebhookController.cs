using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Merchants;
using DelicateCouriers.Integration.Status;
using DelicateCouriers.Integration.Storefront;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Webhooks;

/// <summary>
/// The Engine telling us a shipment moved.
///
/// This is the half that closes the loop, and it replaces the ShipLogic tracking webhook
/// rather than migrating it. Without something at this address a parcel is collected,
/// delivered and signed for while the merchant's order sits exactly where it was, and their
/// customer finds out before they do.
///
/// Signed with the subscription's own secret, not the service credential: whoever operates
/// this URL needs to verify what arrives without being handed the key that books deliveries.
/// </summary>
[ApiController]
[Route("api/webhooks/engine")]
public class EngineWebhookController : ControllerBase
{
    public const string SignatureHeader = "X-Delicate-Signature";
    public const string DeliveryHeader = "X-Delicate-Delivery";

    private readonly IMerchantDirectory _merchants;
    private readonly IEngineClient _engine;
    private readonly IStorefront _storefront;
    private readonly EngineOptions _options;
    private readonly ILogger<EngineWebhookController> _logger;

    public EngineWebhookController(
        IMerchantDirectory merchants,
        IEngineClient engine,
        IStorefront storefront,
        EngineOptions options,
        ILogger<EngineWebhookController> logger)
    {
        _merchants = merchants;
        _engine = engine;
        _storefront = storefront;
        _options = options;
        _logger = logger;
    }

    [HttpPost("status")]
    public async Task<IActionResult> ShipmentStatusChanged(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            // Never an implicit allow: an unverified status update can move a merchant's order
            // to completed, which fires their fulfilment emails and their accounting.
            _logger.LogError("Engine webhook rejected: no subscription secret configured");
            return StatusCode(500, new { error = "Webhook secret not configured" });
        }

        Request.EnableBuffering();
        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
            Request.Body.Position = 0;
        }
        if (string.IsNullOrEmpty(rawBody)) return BadRequest(new { error = "Empty payload" });

        var signature = Request.Headers[SignatureHeader].ToString();
        if (!SignatureIsValid(rawBody, signature, _options.WebhookSecret))
        {
            _logger.LogWarning("Engine webhook rejected: invalid signature");
            return Unauthorized(new { error = "Invalid signature" });
        }

        EngineStatusDelivery? delivery;
        try
        {
            delivery = JsonSerializer.Deserialize<EngineStatusDelivery>(
                rawBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON" });
        }
        if (delivery?.Data is null) return BadRequest(new { error = "Empty payload" });

        var waybill = delivery.Data.Waybill ?? "(unknown)";
        _logger.LogInformation(
            "Engine status: delivery={DeliveryId} waybill={Waybill} {From} -> {To}",
            delivery.Id, waybill, delivery.Data.From, delivery.Data.To);

        var merchantStatus = ShipmentStatusMap.ToMerchantVisible(delivery.Data.To ?? string.Empty);
        if (merchantStatus is null)
        {
            // Accepted and ignored. Answering anything but 200 would make the Engine retry
            // something we have decided not to act on.
            _logger.LogInformation(
                "No merchant-visible equivalent for Engine status {Status}; nothing pushed",
                delivery.Data.To);
            return Ok(new { received = true, applied = false });
        }

        var applied = await PushToStorefrontAsync(delivery, merchantStatus, waybill, cancellationToken);
        return Ok(new { received = true, applied });
    }

    /// <summary>
    /// Find the order this shipment belongs to and tell its shop.
    ///
    /// The Engine knows the booking's customer reference — `WC-{wooOrderId}` — which is the
    /// only thing tying a waybill back to a merchant's order, so it is fetched rather than
    /// guessed from the waybill format.
    /// </summary>
    private async Task<bool> PushToStorefrontAsync(
        EngineStatusDelivery delivery,
        string merchantStatus,
        string waybill,
        CancellationToken cancellationToken)
    {
        var merchant = _merchants.FindByAccountId(delivery.AccountId);
        if (merchant is null)
        {
            _logger.LogWarning(
                "Engine status for account {AccountId} has no merchant mapped; nothing pushed",
                delivery.AccountId);
            return false;
        }

        long wooOrderId;
        try
        {
            var booking = await _engine.GetBookingAsync(
                merchant.EngineAccountRef, delivery.Data!.BookingId!, cancellationToken);
            if (!TryParseWooOrderId(booking?.CustomerReference, out wooOrderId))
            {
                _logger.LogInformation(
                    "Booking {BookingId} has no WooCommerce reference; nothing pushed",
                    delivery.Data.BookingId);
                return false;
            }
        }
        catch (EngineCallException ex)
        {
            _logger.LogWarning("Could not resolve booking for {Waybill}: {Code}", waybill, ex.Code);
            return false;
        }

        var update = new StorefrontOrderUpdate(
            Status: ShipmentStatusMap.ToWooOrderStatus(delivery.Data!.To!),
            CustomerNote: ShipmentStatusMap.CustomerNote(delivery.Data.To!, waybill),
            ShipmentStatus: merchantStatus,
            Waybill: waybill);

        try
        {
            await _storefront.UpdateOrderAsync(merchant, wooOrderId, update, cancellationToken);
            return true;
        }
        catch (StorefrontLookupException ex)
        {
            // Answer 200 anyway. The Engine retrying the whole delivery would re-push to every
            // other subscriber too, and a missed note is a smaller problem than a storm.
            _logger.LogError(ex, "Could not push status to store {StoreId}", merchant.StoreId);
            return false;
        }
    }

    private static bool TryParseWooOrderId(string? customerReference, out long wooOrderId)
    {
        wooOrderId = 0;
        return customerReference is not null
               && customerReference.StartsWith("WC-", StringComparison.OrdinalIgnoreCase)
               && long.TryParse(customerReference[3..], out wooOrderId);
    }

    private static bool SignatureIsValid(string rawBody, string provided, string secret)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(provided);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public sealed record EngineStatusDelivery
{
    public string? Id { get; init; }
    public string? Type { get; init; }
    public string? AccountId { get; init; }
    public EngineStatusData? Data { get; init; }
}

public sealed record EngineStatusData
{
    public string? ShipmentId { get; init; }
    public string? BookingId { get; init; }
    public string? Waybill { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? Note { get; init; }
}
