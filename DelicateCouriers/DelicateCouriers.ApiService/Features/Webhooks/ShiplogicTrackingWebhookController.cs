using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shipments;
using DelicateCouriers.ApiService.Features.Webhooks.DTOs;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Webhooks;

// Shiplogic authenticates with its own "Auth key" header (delivered verbatim
// in `Authorization` on every callback). The controller validates that key
// itself; the JwtBearer pipeline is bypassed for this path in Program.cs so
// it never tries to parse the non-JWT key. [AllowAnonymous] makes the intent
// explicit and survives any future tightening of the default auth policy.
[ApiController]
[AllowAnonymous]
[Route("api/webhooks/shiplogic")]
[EnableRateLimiting("WebhookIp")]
public class ShiplogicTrackingWebhookController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<ShiplogicTrackingWebhookController> _logger;
    private readonly IConfiguration _configuration;
    private readonly ISystemEventLogger _systemEvents;
    private readonly IWooCommerceService _wooCommerceService;

    public ShiplogicTrackingWebhookController(
        AppDbContext context,
        ILogger<ShiplogicTrackingWebhookController> logger,
        IConfiguration configuration,
        ISystemEventLogger systemEvents,
        IWooCommerceService wooCommerceService)
    {
        _context = context;
        _logger = logger;
        _configuration = configuration;
        _systemEvents = systemEvents;
        _wooCommerceService = wooCommerceService;
    }

    [HttpPost("tracking")]
    public async Task<IActionResult> ReceiveTrackingUpdate([FromBody] ShiplogicTrackingWebhookDTO payload)
    {
        // Shared-secret check. If Shiplogic__WebhookSecret is configured we require
        // an exact match in the X-Webhook-Secret header. If not configured we log a
        // warning and accept the webhook (so existing integrations don't break) —
        // operators should set this env var as soon as Shiplogic is configured to
        // send it.
        // Shiplogic's webhook subscription UI exposes a single "Auth key (optional)"
        // field whose value is delivered back on every callback in the Authorization
        // header (verbatim, no "Bearer " prefix). We also accept X-Webhook-Secret
        // for backwards compatibility with any earlier setup.
        var configuredSecret = _configuration["Shiplogic:WebhookSecret"];
        if (!string.IsNullOrEmpty(configuredSecret))
        {
            var headerSecret = Request.Headers["X-Webhook-Secret"].ToString();
            var authHeader = Request.Headers["Authorization"].ToString();
            if (!string.IsNullOrEmpty(authHeader) &&
                authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                authHeader = authHeader.Substring("Bearer ".Length).Trim();
            }

            var presented = !string.IsNullOrEmpty(headerSecret) ? headerSecret : authHeader;
            if (string.IsNullOrEmpty(presented) ||
                !FixedTimeEquals(presented, configuredSecret))
            {
                _logger.LogWarning(
                    "Shiplogic webhook rejected: invalid or missing auth (expected X-Webhook-Secret or Authorization header to match Shiplogic:WebhookSecret). ShipmentID: {ShipmentId}",
                    payload?.ShipmentId);
                return Unauthorized(new { error = "Invalid webhook secret" });
            }
        }
        else
        {
            _logger.LogWarning(
                "Shiplogic:WebhookSecret is not configured — accepting webhook unauthenticated. " +
                "Set the Shiplogic__WebhookSecret env var to enforce authentication.");
        }

        if (payload == null)
        {
            return BadRequest(new { error = "Empty payload" });
        }

        _logger.LogInformation(
            "Received Shiplogic tracking webhook. ShipmentID: {ShipmentId}, Status: {Status}, TrackingRef: {TrackingRef}",
            payload.ShipmentId, payload.Status, payload.ShortTrackingReference);

        try
        {
            // Find the shipment by ConsignmentID (Shiplogic's shipment_id).
            // Webhook has no JWT; use IgnoreQueryFilters() so the tenant filter
            // doesn't hide the row even if a context ever has a TenantId set.
            // Include Order.Store so the WooCommerce pushback below has the
            // credentials it needs in a single round-trip.
            var shipment = await _context.Shipments
                .IgnoreQueryFilters()
                .Include(s => s.Order)
                    .ThenInclude(o => o!.Store)
                .FirstOrDefaultAsync(s => s.ConsignmentID == payload.ShipmentId.ToString());

            if (shipment == null)
            {
                shipment = await _context.Shipments
                    .IgnoreQueryFilters()
                    .Include(s => s.Order)
                        .ThenInclude(o => o!.Store)
                    .FirstOrDefaultAsync(s => s.TrackingNumber == payload.ShortTrackingReference);
            }

            if (shipment == null)
            {
                _logger.LogWarning("Shipment not found for Shiplogic webhook. ShipmentID: {ShipmentId}, TrackingRef: {TrackingRef}",
                                    payload.ShipmentId, payload.ShortTrackingReference);

                // Return 200 to acknowledge receipt (don't want Shiplogic to retry)
                return Ok(new { message = "Shipment not found, webhook acknowledged" });
            }

            // Update shipment status
            var previousStatus = shipment.ShipmentStatus;
            shipment.ShipmentStatus = payload.Status;
            shipment.ChangedOn = DateTime.UtcNow;
            shipment.ChangedBy = "ShiplogicWebhook";

            if (payload.ShipmentCollectedDate.HasValue)
            {
                shipment.ActualDeliveryDate = null; // Clear if re-collected
            }

            if (payload.ShipmentDeliveredDate.HasValue)
            {
                shipment.ActualDeliveryDate = payload.ShipmentDeliveredDate;
            }

            if (payload.ShipmentEstimatedDeliveryFrom.HasValue)
            {
                shipment.EstimatedDeliveryDate = payload.ShipmentEstimatedDeliveryFrom;
            }

            // Save tracking events — batch the existence check to avoid an N+1 round-trip.
            if (payload.TrackingEvents != null && payload.TrackingEvents.Any())
            {
                var incomingEventIds = payload.TrackingEvents
                    .Select(e => e.Id.ToString())
                    .Distinct()
                    .ToList();

                var existingEventIds = await _context.TrackingEvents
                    .IgnoreQueryFilters()
                    .Where(te => te.ShipmentID == shipment.ShipmentID
                                 && te.ExternalEventId != null
                                 && incomingEventIds.Contains(te.ExternalEventId))
                    .Select(te => te.ExternalEventId!)
                    .ToListAsync();

                var existingSet = new HashSet<string>(existingEventIds);

                foreach (var evt in payload.TrackingEvents)
                {
                    var externalId = evt.Id.ToString();
                    if (existingSet.Contains(externalId))
                    {
                        continue;
                    }

                    var trackingEvent = new TrackingEvent
                    {
                        ShipmentID = shipment.ShipmentID,
                        ExternalEventId = externalId,
                        EventType = evt.Status,
                        EventDescription = !string.IsNullOrEmpty(evt.Message) ? evt.Message : GetDefaultMessage(evt.Status),
                        EventLocation = evt.Location ?? string.Empty,
                        EventTimestamp = evt.Date,
                        Source = evt.Source,
                        CreatedOn = DateTime.UtcNow,
                        CreatedBy = "ShiplogicWebhook"
                    };

                    _context.TrackingEvents.Add(trackingEvent);
                    existingSet.Add(externalId); // protect against duplicates within payload

                    _logger.LogInformation("Added tracking event for Shipment {ShipmentId}: {Status} at {EventTime}",
                                            shipment.ShipmentID, evt.Status, evt.Date);
                }
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated shipment {ShipmentId} status: {PreviousStatus} -> {NewStatus}",
                                   shipment.ShipmentID, previousStatus, payload.Status);

            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "tracking.event_ingested",
                ActorKind = "Webhook",
                ActorLabel = "Shiplogic",
                TenantId = shipment.Order?.TenantID,
                EntityType = "Shipment",
                EntityRef = shipment.TrackingNumber,
                Message = $"Tracking update for {shipment.TrackingNumber}: {previousStatus} → {payload.Status}",
                Details = new
                {
                    shipmentId = shipment.ShipmentID,
                    trackingNumber = shipment.TrackingNumber,
                    consignmentId = shipment.ConsignmentID,
                    previousStatus,
                    newStatus = payload.Status,
                    eventCount = payload.TrackingEvents?.Count ?? 0,
                    deliveredAt = payload.ShipmentDeliveredDate,
                },
            });

            // Push status changes back to WooCommerce. Best-effort: any failure
            // is logged but does not affect the webhook ack — Shiplogic must
            // never get a non-2xx because our storefront sync failed.
            await TryPushStatusToStorefrontAsync(shipment, previousStatus, payload.Status);

            return Ok(new { message = "Tracking update processed successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Shiplogic tracking webhook");

            // Return 200 anyway to prevent retries for processing errors
            return Ok(new { message = "Webhook acknowledged with errors" });
        }
    }

    /// <summary>
    /// Reflect a Shiplogic status change back to the originating WooCommerce store.
    /// - Pushes the tracking number as a customer-visible order note the first
    ///   time we see a non-empty status for the shipment (covers the case where
    ///   the booking-time push never happened, e.g. a shipment created before
    ///   the pushback feature shipped, or one where WooCommerce was briefly down).
    /// - On delivery we move the WooCommerce order to "completed", but ONLY on
    ///   the transition, not on every replay of the same status. Shipment
    ///   cancellation never changes the order status — it only adds an
    ///   internal order note.
    /// All failures are swallowed and logged so the webhook still 200s.
    /// </summary>
    private async Task TryPushStatusToStorefrontAsync(Shipment shipment, string previousStatus, string newStatus)
    {
        try
        {
            var order = shipment.Order;
            var store = order?.Store;
            if (order == null || store == null)
            {
                return;
            }

            if (!string.Equals(store.Platform, "woocommerce", StringComparison.OrdinalIgnoreCase))
            {
                return; // Shopify path not wired yet.
            }

            if (string.IsNullOrWhiteSpace(store.WooCommerceURL) ||
                string.IsNullOrWhiteSpace(store.WooConsumerKey) ||
                string.IsNullOrWhiteSpace(store.WooConsumerSecret))
            {
                _logger.LogDebug("Skipping WooCommerce status push for Shipment {ShipmentId} — store credentials incomplete.",
                    shipment.ShipmentID);
                return;
            }

            if (!int.TryParse(order.WooOrderID, out var wooOrderId) || wooOrderId <= 0)
            {
                _logger.LogDebug("Skipping WooCommerce status push for Shipment {ShipmentId} — WooOrderID '{WooOrderId}' is not a valid int.",
                    shipment.ShipmentID, order.WooOrderID);
                return;
            }

            var statusChanged = !string.Equals(previousStatus, newStatus, StringComparison.OrdinalIgnoreCase);

            // Refresh the _dcp_* meta on every webhook so the merchant's
            // "Shipment & Tracking" metabox stays live as the parcel moves
            // (submitted → collected → in-transit → delivered). Cheap upsert
            // on the WooCommerce side and idempotent — same keys, new values.
            var metaOk = await _wooCommerceService.PushShipmentMetaAsync(
                store.StoreID, wooOrderId, ShipmentStorefrontMeta.Build(shipment));
            if (!metaOk)
            {
                _logger.LogWarning(
                    "Shipment meta refresh failed for Shipment {ShipmentId} (Order {OrderId}) — metabox will show stale status until next webhook.",
                    shipment.ShipmentID, order.OrderID);
            }

            // Tracking-number push: fallback for shipments where the booking-
            // time push never happened (e.g. booked before this feature
            // shipped, or WooCommerce was briefly unreachable during booking).
            // Gated by Shipment.TrackingPushedOn so the customer only ever
            // sees ONE "Your order has been shipped via …" note per shipment,
            // no matter how many status transitions Shiplogic streams.
            if (!shipment.TrackingPushedOn.HasValue && !string.IsNullOrWhiteSpace(shipment.TrackingNumber))
            {
                var courier = !string.IsNullOrWhiteSpace(shipment.CourierName) ? shipment.CourierName : "Shiplogic";
                var noteOk = await _wooCommerceService.AddTrackingInfoAsync(
                    store.StoreID, wooOrderId, shipment.TrackingNumber, courier);
                if (noteOk)
                {
                    shipment.TrackingPushedOn = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    _logger.LogInformation(
                        "Pushed tracking {Tracking} for Shipment {ShipmentId} to WooCommerce order {WooOrderId} via webhook fallback.",
                        shipment.TrackingNumber, shipment.ShipmentID, wooOrderId);
                }
                else
                {
                    _logger.LogWarning(
                        "WooCommerce rejected the tracking note for Shipment {ShipmentId} (Order {OrderId}, WooOrder {WooOrderId}).",
                        shipment.ShipmentID, order.OrderID, wooOrderId);
                }
            }

            // Terminal-state mapping. Only on transition.
            //
            // Deliberately ONLY delivered → completed. A cancelled shipment
            // must never cancel the customer's WooCommerce order — cancelling
            // a booking on Shiplogic just means the merchant will rebook
            // delivery; the order itself stays in whatever status it's in.
            // We add an internal order note instead so the merchant has
            // visibility.
            if (statusChanged)
            {
                if (string.Equals(newStatus, "cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    var noteText =
                        $"Delicate Courier: shipment {shipment.TrackingNumber} was cancelled on the courier side. " +
                        "The order status has NOT been changed — rebook delivery if the order still needs to ship.";
                    var cancelNoteOk = await _wooCommerceService.AddOrderNoteAsync(
                        store.StoreID, wooOrderId, noteText);
                    if (!cancelNoteOk)
                    {
                        _logger.LogWarning(
                            "WooCommerce rejected the shipment-cancelled note for Shipment {ShipmentId} (Order {OrderId}, WooOrder {WooOrderId}).",
                            shipment.ShipmentID, order.OrderID, wooOrderId);
                    }
                }

                string? wooTargetStatus = newStatus?.ToLowerInvariant() switch
                {
                    "delivered" => "completed",
                    _ => null,
                };

                if (wooTargetStatus != null)
                {
                    var statusOk = await _wooCommerceService.UpdateOrderStatusAsync(
                        store.StoreID, wooOrderId, wooTargetStatus);
                    if (!statusOk)
                    {
                        _logger.LogWarning(
                            "WooCommerce rejected status update to '{Target}' for Shipment {ShipmentId} (Order {OrderId}, WooOrder {WooOrderId}).",
                            wooTargetStatus, shipment.ShipmentID, order.OrderID, wooOrderId);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Set WooCommerce order {WooOrderId} (Store {StoreId}) to '{Target}' after Shiplogic '{NewStatus}'.",
                            wooOrderId, store.StoreID, wooTargetStatus, newStatus);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Best-effort WooCommerce pushback failed for Shipment {ShipmentId} — webhook ack is unaffected.",
                shipment.ShipmentID);
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }
        return diff == 0;
    }

    // Single source of truth for Shiplogic status → human label is
    // ShipmentStorefrontMeta.StatusLabel — the same helper that stamps
    // _dcp_shipment_status_label onto the WooCommerce order. Using it here
    // too means a TrackingEvent description, a SystemEvent message, and
    // the merchant metabox all read the same wording for the same status.
    private static string GetDefaultMessage(string status) => ShipmentStorefrontMeta.StatusLabel(status);
}
