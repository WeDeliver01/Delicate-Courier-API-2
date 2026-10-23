using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Features.Admin;
using DelicateCouriers.Integration.Labels;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Portal;

/// <summary>
/// What the new portal books through.
///
/// The portal replaced ShipLogic with the Engine, and reaches it here rather than holding its
/// own Engine credential. One process holds that credential, which means one place to rotate
/// it, one grant list to audit, and one answer to "what can reach the Engine as us".
///
/// Deliberately thin. It is a pass-through to <see cref="IEngineClient"/>, using the Engine's
/// own request and response shapes rather than translating them into a third vocabulary.
/// A mapping layer here would be a second contract to keep in step with the first, and the
/// Engine's shapes are already the ones the plugin path is tested against.
///
/// Unlike the plugin endpoints, nothing here is keyed on a store. A portal operator books on
/// behalf of an account, which may be a merchant who has no WooCommerce at all — a shipment
/// someone raised over the phone still has to go somewhere. So the account travels explicitly
/// in every call rather than being looked up from a store id.
/// </summary>
[ApiController]
[Route("api/portal")]
[RequiresAdminKey]
public class PortalController : ControllerBase
{
    private readonly IEngineClient _engine;
    private readonly ILabelRenderer _labels;
    private readonly ILogger<PortalController> _logger;

    public PortalController(
        IEngineClient engine,
        ILabelRenderer labels,
        ILogger<PortalController> logger)
    {
        _engine = engine;
        _labels = labels;
        _logger = logger;
    }

    /// <summary>Price without booking. What the portal shows before anyone commits.</summary>
    [HttpPost("quotes")]
    public Task<ActionResult<EngineQuote>> Quote(
        [FromQuery] string accountRef,
        [FromBody] EngineQuoteRequest request,
        CancellationToken cancellationToken) =>
        Call(accountRef, ct => _engine.CreateQuoteAsync(accountRef, request, ct), cancellationToken);

    /// <summary>
    /// Book it.
    ///
    /// The caller supplies the idempotency key, because only the caller knows what "the same
    /// booking" means to it. Sent twice with one key, the Engine returns the booking it already
    /// made rather than a second driver at the door.
    /// </summary>
    [HttpPost("bookings")]
    public Task<ActionResult<EngineBookingResponse>> Book(
        [FromQuery] string accountRef,
        [FromBody] EngineBookingRequest request,
        CancellationToken cancellationToken) =>
        Call(accountRef, ct => _engine.CreateBookingAsync(accountRef, request, ct), cancellationToken);

    /// <summary>
    /// The booking already made against this reference, or 404.
    ///
    /// This is what closes the window between a booking succeeding at the Engine and the
    /// caller recording it: ask before booking again, rather than discovering it afterwards.
    /// </summary>
    [HttpGet("bookings/by-reference")]
    public async Task<ActionResult<EngineBooking>> ByReference(
        [FromQuery] string accountRef,
        [FromQuery] string customerReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountRef) || string.IsNullOrWhiteSpace(customerReference))
        {
            return BadRequest(new { error = "accountRef and customerReference are both required" });
        }

        try
        {
            var booking = await _engine.FindBookingByCustomerReferenceAsync(
                accountRef, customerReference, cancellationToken);

            // Nothing booked yet is the ordinary case on this path, not a failure, but the
            // caller still needs to tell it apart from a booking that exists.
            return booking is null
                ? NotFound(new { error = "No booking for that reference" })
                : Ok(booking);
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, accountRef);
        }
    }

    [HttpGet("bookings/{bookingId}")]
    public async Task<ActionResult<EngineBooking>> Get(
        string bookingId,
        [FromQuery] string accountRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountRef))
        {
            return BadRequest(new { error = "accountRef is required" });
        }

        try
        {
            var booking = await _engine.GetBookingAsync(accountRef, bookingId, cancellationToken);
            return booking is null ? NotFound(new { error = "Booking not found" }) : Ok(booking);
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, accountRef);
        }
    }

    /// <summary>
    /// The waybill, rendered here as a PDF.
    ///
    /// The Engine returns the facts of the shipment, not a document; laying them out is this
    /// service's job and already happens for the plugin's download. The portal gets the same
    /// renderer, so a waybill looks the same whichever door it came through.
    /// </summary>
    [HttpGet("shipments/{shipmentId}/waybill")]
    public async Task<IActionResult> Waybill(
        string shipmentId,
        [FromQuery] string accountRef,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountRef))
        {
            return BadRequest(new { error = "accountRef is required" });
        }

        try
        {
            var waybill = await _engine.GetWaybillAsync(accountRef, shipmentId, cancellationToken);
            var pdf = _labels.Render(waybill);

            _logger.LogInformation(
                "Portal waybill served: account {AccountRef}, shipment {ShipmentId}, {Size} bytes",
                accountRef, shipmentId, pdf.Length);

            return File(pdf, "application/pdf", $"waybill-{waybill.Waybill}.pdf");
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, accountRef);
        }
    }

    private async Task<ActionResult<T>> Call<T>(
        string accountRef,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountRef))
        {
            return BadRequest(new { error = "accountRef is required" });
        }

        try
        {
            return Ok(await work(cancellationToken));
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, accountRef);
        }
    }

    /// <summary>
    /// Pass the Engine's refusal through rather than flattening it to a 500.
    ///
    /// The caller is the portal, not a shopper, so there is nobody here to protect from the
    /// detail — and an operator told "the Engine refused: this credential may not act for that
    /// account" can act on it, where one told "something went wrong" cannot. The status is
    /// kept too, so a transient 502 is retried and a 403 is not.
    /// </summary>
    private ObjectResult Failure(EngineCallException ex, string accountRef)
    {
        _logger.LogWarning(
            "Portal call failed for account {AccountRef}: {Status} {Code} {Message}",
            accountRef, (int)ex.Status, ex.Code, ex.Message);

        return StatusCode((int)ex.Status, new
        {
            error = ex.Message,
            code = ex.Code,
            requestId = ex.RequestId,
            transient = ex.IsTransient,
        });
    }
}
