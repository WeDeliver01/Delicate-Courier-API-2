using System.Text.Json;
using DelicateCouriers.Integration.Contract.Plugin;
using DelicateCouriers.Integration.Jobs;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Webhooks;

/// <summary>
/// Order webhooks from the Delicate Courier WooCommerce plugin.
///
/// The webhook's job ends at "signed, valid, ours". Whether the order becomes a delivery is
/// decided behind it, by the queued job: a shopper's checkout is waiting on this response, and
/// the Engine being slow for ten seconds must not become the merchant's problem.
/// </summary>
[ApiController]
[Route("api/webhooks/plugin")]
public class PluginWebhookController : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

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
        var request = await WebhookRequest.ReadAsync(Request, _merchants, _logger);
        if (request.Failure is not null) return request.Failure;

        PluginOrderPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PluginOrderPayload>(request.RawBody!, Json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Plugin webhook rejected: invalid JSON");
            return BadRequest(new { error = "Invalid JSON" });
        }
        if (payload is null) return BadRequest(new { error = "Empty payload" });

        _logger.LogInformation(
            "Plugin webhook: event={Event} store={StoreId} wooOrderId={WooOrderId} status={Status}",
            payload.Event, request.StoreId, payload.WooOrderId, payload.Status);

        // Queued with the exact bytes that were signed, so the job books what the merchant
        // sent rather than something re-serialised on the way through.
        _queue.Enqueue(request.StoreId, request.RawBody!);

        return Ok(new PluginWebhookResponse
        {
            Success = true,
            Message = "queued",
            OrderNumber = payload.OrderNumber
        });
    }
}
