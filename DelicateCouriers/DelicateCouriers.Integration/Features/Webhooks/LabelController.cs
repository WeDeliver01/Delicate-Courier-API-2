using System.Text.Json;
using DelicateCouriers.Integration.Bookings;
using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Labels;
using DelicateCouriers.Integration.Merchants;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Webhooks;

/// <summary>
/// The label a merchant prints and sticks on the box.
///
/// Signed the same way as the order webhook, because it answers with a customer's name and
/// address: an unsigned endpoint here would hand anyone who could guess an order number the
/// delivery details that go with it.
/// </summary>
[ApiController]
[Route("api/webhooks/plugin")]
public class LabelController : ControllerBase
{
    private readonly IMerchantDirectory _merchants;
    private readonly IEngineClient _engine;
    private readonly ILabelRenderer _renderer;
    private readonly ILogger<LabelController> _logger;

    public LabelController(
        IMerchantDirectory merchants,
        IEngineClient engine,
        ILabelRenderer renderer,
        ILogger<LabelController> logger)
    {
        _merchants = merchants;
        _engine = engine;
        _renderer = renderer;
        _logger = logger;
    }

    [HttpPost("label")]
    public async Task<IActionResult> DownloadLabel(CancellationToken cancellationToken)
    {
        var request = await WebhookRequest.ReadAsync(Request, _merchants, _logger);
        if (request.Failure is not null) return request.Failure;

        var merchant = request.Merchant!;

        long wcOrderId;
        try
        {
            using var body = JsonDocument.Parse(request.RawBody!);
            if (!body.RootElement.TryGetProperty("wc_order_id", out var idElement)
                || !idElement.TryGetInt64(out wcOrderId))
            {
                return BadRequest(new { error = "wc_order_id (int) is required" });
            }
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON" });
        }

        var reference = BookingRules.CustomerReferenceFor(wcOrderId);

        try
        {
            var booking = await _engine.FindBookingByCustomerReferenceAsync(
                merchant.EngineAccountRef, reference, cancellationToken);
            if (booking is null)
            {
                // Ordinary, not an error: the booking job may not have run yet. The plugin
                // shows this to a merchant, so it says which of the two it is.
                return NotFound(new { error = "No shipment exists for this order yet" });
            }

            var shipment = booking.Shipments.FirstOrDefault();
            if (shipment is null)
            {
                return NotFound(new { error = "No shipment exists for this order yet" });
            }

            var waybill = await _engine.GetWaybillAsync(
                merchant.EngineAccountRef, shipment.Id, cancellationToken);
            var pdf = _renderer.Render(waybill);

            _logger.LogInformation(
                "Label served: store {StoreId}, order {WcOrderId}, waybill {Waybill}, {Size} bytes",
                merchant.StoreId, wcOrderId, waybill.Waybill, pdf.Length);

            // Same content type and filename convention the plugin already handles.
            return File(pdf, "application/pdf", $"waybill_{waybill.Waybill}.pdf");
        }
        catch (EngineCallException ex)
        {
            _logger.LogError(
                "Label fetch failed for order {WcOrderId}: {Code} {Message}",
                wcOrderId, ex.Code, ex.Message);
            return StatusCode(502, new { error = "Waybill not available for this shipment yet" });
        }
    }
}
