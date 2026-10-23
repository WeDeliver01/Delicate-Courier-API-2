using DelicateCouriers.Integration.Engine;
using DelicateCouriers.Integration.Features.Admin;
using DelicateCouriers.Integration.Geo;
using DelicateCouriers.Integration.Labels;
using Microsoft.AspNetCore.Mvc;

namespace DelicateCouriers.Integration.Features.Portal;

/// <summary>
/// What the new portal books through, now that it books through the Engine rather than
/// ShipLogic.
///
/// The portal does not hold an Engine credential. One process holds it, which means one place
/// to rotate it, one grant list to audit, and one answer to "what can act on the Engine as
/// us". It is the same reason the plugin does not hold one either.
///
/// The contract here is the portal's, not the Engine's — see <see cref="PortalDeliveryRequest"/>.
/// Passing the Engine's own shapes through would have meant the portal knowing how the Engine
/// models a job, which is exactly what routing through this service was meant to avoid, and
/// would leave two codebases to change the next time the Engine does.
/// </summary>
[ApiController]
[Route("api/portal")]
[RequiresAdminKey]
public class PortalController : ControllerBase
{
    private readonly IEngineClient _engine;
    private readonly IEngineGeocoder _geocoder;
    private readonly ILabelRenderer _labels;
    private readonly ILogger<PortalController> _logger;

    public PortalController(
        IEngineClient engine,
        IEngineGeocoder geocoder,
        ILabelRenderer labels,
        ILogger<PortalController> logger)
    {
        _engine = engine;
        _geocoder = geocoder;
        _labels = labels;
        _logger = logger;
    }

    /// <summary>Price without committing. What the portal shows before anyone agrees.</summary>
    [HttpPost("quotes")]
    public async Task<ActionResult<PortalQuoteResult>> Quote(
        [FromBody] PortalDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        if (Invalid(request) is { } problem) return BadRequest(new { error = problem });

        try
        {
            var (collection, delivery) = await ResolveEnds(request, cancellationToken);
            var quote = await _engine.CreateQuoteAsync(
                request.AccountRef,
                new EngineQuoteRequest
                {
                    ServiceLevelCode = request.ServiceLevelCode,
                    Collection = collection,
                    Drops = new List<EngineDrop> { delivery },
                },
                cancellationToken);

            return Ok(new PortalQuoteResult
            {
                TotalCents = quote.Breakdown.TotalCents,
                Currency = "ZAR",
                ServiceLevelCode = request.ServiceLevelCode,
            });
        }
        catch (AddressUnresolvedException ex)
        {
            return BadRequest(new { error = ex.Message, code = "address_unresolvable" });
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, request.AccountRef);
        }
    }

    /// <summary>
    /// Book it.
    ///
    /// Idempotent on the caller's reference: sent twice, the Engine returns the booking it
    /// already made. That is what closes the window between a booking succeeding and the
    /// caller recording it — the alternative is a second driver at the door.
    /// </summary>
    [HttpPost("bookings")]
    public async Task<ActionResult<PortalDeliveryResult>> Book(
        [FromBody] PortalDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        if (Invalid(request) is { } problem) return BadRequest(new { error = problem });

        try
        {
            var (collection, delivery) = await ResolveEnds(request, cancellationToken);

            var result = await _engine.CreateBookingAsync(
                request.AccountRef,
                new EngineBookingRequest
                {
                    ServiceLevelCode = request.ServiceLevelCode,
                    Collection = collection,
                    Drops = new List<EngineDrop> { delivery },
                    IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                        ? request.CustomerReference
                        : request.IdempotencyKey,
                    CustomerReference = request.CustomerReference,
                    MaxTotalCents = request.MaxTotalCents,
                },
                cancellationToken);

            return Ok(ToResult(result, request.ServiceLevelCode));
        }
        catch (AddressUnresolvedException ex)
        {
            return BadRequest(new { error = ex.Message, code = "address_unresolvable" });
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, request.AccountRef);
        }
    }

    /// <summary>The booking already made against this reference, or 404.</summary>
    [HttpGet("bookings/by-reference")]
    public async Task<ActionResult<PortalDeliveryResult>> ByReference(
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

            return booking is null
                ? NotFound(new { error = "No booking for that reference" })
                : Ok(ToResult(booking, replayed: true));
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, accountRef);
        }
    }

    /// <summary>One booking, for a status refresh.</summary>
    [HttpGet("bookings/{bookingId}")]
    public async Task<ActionResult<PortalDeliveryResult>> Get(
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
            return booking is null
                ? NotFound(new { error = "Booking not found" })
                : Ok(ToResult(booking, replayed: true));
        }
        catch (EngineCallException ex)
        {
            return Failure(ex, accountRef);
        }
    }

    /// <summary>
    /// The waybill, as a PDF.
    ///
    /// The Engine returns the facts of a shipment, not a document. Laying them out happens
    /// here, with the same renderer the plugin's download uses, so a waybill looks the same
    /// whichever door it came through.
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

    private static string? Invalid(PortalDeliveryRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.AccountRef)) return "accountRef is required";
        if (string.IsNullOrWhiteSpace(r.CustomerReference)) return "customerReference is required";
        if (r.Parcels.Count == 0) return "At least one parcel is required";
        if (r.Parcels.Any(p => string.IsNullOrWhiteSpace(p.PackageTypeId)))
        {
            // Not derivable from dimensions, and guessing one silently changes the price.
            return "Every parcel needs a packageTypeId";
        }
        if (string.IsNullOrWhiteSpace(r.Collection.Formatted) && !r.Collection.HasCoordinates)
        {
            return "The collection address needs either a formatted address or coordinates";
        }
        if (string.IsNullOrWhiteSpace(r.Delivery.Formatted) && !r.Delivery.HasCoordinates)
        {
            return "The delivery address needs either a formatted address or coordinates";
        }
        return null;
    }

    /// <summary>
    /// Both ends as the Engine wants them, geocoding only what has to be geocoded.
    ///
    /// A caller that already knows where a place is — a merchant's own premises, stored once —
    /// should not pay for a lookup on every booking, and should not risk a geocoder disagreeing
    /// with itself between the quote and the booking.
    /// </summary>
    private async Task<(EngineCollection, EngineDrop)> ResolveEnds(
        PortalDeliveryRequest request, CancellationToken cancellationToken)
    {
        var collection = await ToAddress(request.Collection, "collection", cancellationToken);
        var delivery = await ToAddress(request.Delivery, "delivery", cancellationToken);

        return (
            new EngineCollection
            {
                Address = collection,
                Contact = ToContact(request.Collection),
                Instructions = request.Collection.Instructions,
            },
            new EngineDrop
            {
                Address = delivery,
                Recipient = ToContact(request.Delivery),
                Instructions = request.Delivery.Instructions,
                Parcels = request.Parcels.Select(p => new EngineParcel
                {
                    PackageTypeId = p.PackageTypeId,
                    Quantity = p.Quantity <= 0 ? 1 : p.Quantity,
                    WeightKg = p.WeightKg,
                    Description = p.Description,
                }).ToList(),
            });
    }

    private async Task<EngineAddress> ToAddress(
        PortalStop stop, string which, CancellationToken cancellationToken)
    {
        if (stop.HasCoordinates)
        {
            return new EngineAddress
            {
                Formatted = stop.Formatted,
                Line1 = stop.Line1,
                Suburb = stop.Suburb,
                City = stop.City,
                PostalCode = stop.PostalCode,
                Country = stop.Country,
                Location = new EngineLatLng(stop.Latitude!.Value, stop.Longitude!.Value),
            };
        }

        var place = await _geocoder.ResolveAsync(stop.Formatted, cancellationToken);
        if (place is null)
        {
            // Permanent as far as retrying goes: the same words will not resolve in an hour.
            // Someone has to look at the address, so say so rather than retrying for a day.
            throw new AddressUnresolvedException(
                $"The {which} address could not be resolved: {stop.Formatted}");
        }

        return new EngineAddress
        {
            Formatted = string.IsNullOrWhiteSpace(place.Formatted) ? stop.Formatted : place.Formatted,
            Line1 = stop.Line1,
            Suburb = stop.Suburb,
            City = stop.City,
            PostalCode = stop.PostalCode,
            Country = stop.Country,
            Location = new EngineLatLng(place.Latitude, place.Longitude),
        };
    }

    private static EngineContact ToContact(PortalStop stop) => new()
    {
        Name = stop.ContactName,
        Phone = stop.ContactPhone,
        Email = stop.ContactEmail,
    };

    private static PortalDeliveryResult ToResult(EngineBookingResponse response, string serviceLevelCode) =>
        ToResult(response.Booking, response.Replayed, serviceLevelCode);

    private static PortalDeliveryResult ToResult(
        EngineBooking booking, bool replayed, string serviceLevelCode = "")
    {
        var shipment = booking.Shipments.FirstOrDefault();
        return new PortalDeliveryResult
        {
            // The portal's columns are named for a ShipLogic consignment. They hold an Engine
            // shipment id now, which fits: the column has always been text.
            ConsignmentId = shipment?.Id ?? booking.Id,
            TrackingNumber = shipment?.Waybill ?? booking.Reference,
            Reference = booking.Reference,
            Status = shipment?.Status ?? booking.Status,
            TotalCents = booking.TotalCents,
            ServiceLevelCode = serviceLevelCode,
            Replayed = replayed,
        };
    }

    /// <summary>
    /// Pass the Engine's refusal through rather than flattening it.
    ///
    /// The caller is the portal, not a shopper, so there is nobody here to withhold detail
    /// from — and an operator told "this credential may not act for that account" can act on
    /// it, where one told "something went wrong" cannot. The status travels too, so a
    /// transient 502 is retried and a 403 is not.
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

/// <summary>An address nobody can deliver to, which is a caller problem rather than a fault.</summary>
public sealed class AddressUnresolvedException : Exception
{
    public AddressUnresolvedException(string message) : base(message) { }
}
