using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.Features.Shiplogic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Shipments;

[ApiController]
[Route("api/[controller]")]
[Authorize] // Require authentication
public class ShipmentsController : ControllerBase
{
    private readonly ShipmentOrchestrationService _orchestrationService;
    private readonly LabelService _labelService;
    private readonly ILogger<ShipmentsController> _logger;
    private readonly ShipmentService _shipmentService;
    private readonly IShiplogicService _shiplogicService;
    private readonly IWooCommerceService _wooCommerceService;
    private readonly AppDbContext _context;

    public ShipmentsController(
        ShipmentOrchestrationService orchestrationService,
        LabelService labelService,
        ILogger<ShipmentsController> logger,
        ShipmentService shipmentService,
        IShiplogicService shiplogicService,
        IWooCommerceService wooCommerceService,
        AppDbContext context)
    {
        _orchestrationService = orchestrationService;
        _labelService = labelService;
        _logger = logger;
        _shipmentService = shipmentService;
        _shiplogicService = shiplogicService;
        _wooCommerceService = wooCommerceService;
        _context = context;
    }

    /// <summary>
    /// Create a shipment for an order.
    /// POST /api/shipments/create/{orderRef}
    ///
    /// Accepts ANY of three references the user might have:
    ///   1. The internal OrderID primary key (e.g. "3")
    ///   2. The WooCommerce numeric order id      (e.g. "11980")
    ///   3. The WooCommerce visible order number  (e.g. "11980" or "DC-11980")
    ///
    /// Users in the UI see "Order #11980" everywhere, so they naturally type
    /// 11980 into the manual-create box — not the internal database id (3).
    /// We resolve whichever reference they pass to the internal OrderID
    /// before handing it to the orchestration pipeline, which is still
    /// strictly keyed on OrderID downstream.
    /// </summary>
    [HttpPost("create/{orderRef}")]
    public async Task<IActionResult> CreateShipment(string orderRef)
    {
        _logger.LogInformation("Received request to create shipment for reference: {OrderRef}", orderRef);

        var orderId = await ResolveOrderIdAsync(orderRef);
        if (orderId == null)
        {
            _logger.LogWarning(
                "Could not resolve order reference '{OrderRef}' to any internal OrderID, WooOrderID, or WooOrderNumber.",
                orderRef);

            return NotFound(new
            {
                success = false,
                message = $"No order found for reference '{orderRef}'. Try the WooCommerce order number you see on the order list.",
            });
        }

        _logger.LogInformation("Resolved reference '{OrderRef}' -> internal OrderID {OrderId}", orderRef, orderId.Value);

        var result = await _orchestrationService.CreateShipmentForOrderAsync(orderId.Value);

        if (result.IsSuccess)
        {
            return Ok(new
            {
                success = true,
                message = result.Message,
                shipmentId = result.ShipmentID,
                trackingNumber = result.TrackingNumber
            });
        }

        // Determine appropriate HTTP status code based on error
        var statusCode = result.Message.Contains("not found") ? 404 :
                         result.Message.Contains("already has") ? 409 :
                         result.Message.Contains("configuration") ? 400 :
                         result.Message.Contains("token not configured") ? 400 :
                         500;

        return StatusCode(statusCode, new
        {
            success = false,
            message = result.Message,
            shipmentId = result.ShipmentID
        });
    }

    /// <summary>
    /// Resolve a user-supplied "order reference" (could be any of: internal
    /// OrderID, WooOrderID, WooOrderNumber) to the internal OrderID primary
    /// key. Returns null if no order matches. Bypasses the tenant query
    /// filter for the lookup only — the orchestration service downstream
    /// still does its own tenant/store validation before booking.
    /// </summary>
    private async Task<int?> ResolveOrderIdAsync(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;

        reference = reference.Trim();

        // Tenant-scope the lookup so a non-SuperAdmin caller can't probe
        // for orders in other tenants by comparing 404 vs 200 responses.
        // SuperAdmins (and any caller without a TenantId claim) see all
        // orders; everyone else is restricted to their own tenant. Mirrors
        // the gating pattern already used in GetTracking below.
        var callerRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                         ?? User.FindFirst("Role")?.Value;
        var callerTenantClaim = User.FindFirst("TenantId")?.Value;
        int? tenantScope = null;
        if (callerRole != "SuperAdmin"
            && int.TryParse(callerTenantClaim, out var callerTenantId))
        {
            tenantScope = callerTenantId;
        }

        IQueryable<Domain.Entities.Order> baseQuery = _context.Orders.AsNoTracking();
        if (tenantScope.HasValue)
        {
            baseQuery = baseQuery.Where(o => o.TenantID == tenantScope.Value);
        }

        // Try the internal numeric OrderID first — that's the canonical id
        // the dashboard's "Retry" button passes and what Hangfire enqueues.
        if (int.TryParse(reference, out var numericId))
        {
            var byPk = await baseQuery
                .Where(o => o.OrderID == numericId)
                .Select(o => (int?)o.OrderID)
                .FirstOrDefaultAsync();

            if (byPk != null)
                return byPk;
        }

        // Fall back to the WooCommerce identifiers the user can SEE in their
        // store admin: woo_order_id (numeric) and woo_order_number (display
        // string, sometimes prefixed e.g. "DC-11980").
        var byWoo = await baseQuery
            .Where(o => o.WooOrderID == reference || o.WooOrderNumber == reference)
            .Select(o => (int?)o.OrderID)
            .FirstOrDefaultAsync();

        return byWoo;
    }

    /// <summary>
    /// Get shipment by ID with full details
    /// GET /api/shipments/{shipmentId}
    /// </summary>
    [HttpGet("{shipmentId}")]
    public async Task<IActionResult> GetShipment(int shipmentId)
    {
        var shipment = await _shipmentService.GetShipmentByIdAsync(shipmentId);

        if (shipment == null)
        {
            return NotFound(new { message = $"Shipment {shipmentId} not found" });
        }

        return Ok(shipment);
    }

    /// <summary>
    /// Force a re-push of the `_dcp_*` order meta to the storefront's
    /// WooCommerce order, so the merchant's "Shipment &amp; Tracking" metabox
    /// is backfilled without waiting for the next Shiplogic webhook. Useful
    /// for shipments booked under a backend version that did not yet support
    /// the meta write-back, or any case where the meta got out of sync.
    /// POST /api/shipments/{shipmentId}/resync-storefront
    /// </summary>
    [HttpPost("{shipmentId}/resync-storefront")]
    public async Task<IActionResult> ResyncStorefront(int shipmentId)
    {
        var shipment = await _context.Shipments
            .Include(s => s.Order).ThenInclude(o => o.Store)
            .FirstOrDefaultAsync(s => s.ShipmentID == shipmentId);

        if (shipment == null)
        {
            return NotFound(new { message = $"Shipment {shipmentId} not found" });
        }

        // Tenant ownership check — mirror of GetTracking above.
        var callerRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                         ?? User.FindFirst("Role")?.Value;
        var callerTenantClaim = User.FindFirst("TenantId")?.Value;
        if (callerRole != "SuperAdmin"
            && int.TryParse(callerTenantClaim, out var callerTenantId)
            && shipment.Order?.Store?.TenantID != callerTenantId)
        {
            return NotFound(new { message = $"Shipment {shipmentId} not found" });
        }

        var order = shipment.Order;
        var store = order?.Store;
        if (order == null || store == null)
        {
            return BadRequest(new { message = "Shipment is not linked to an order/store." });
        }
        if (!string.Equals(store.Platform, "woocommerce", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = $"Store platform '{store.Platform}' is not WooCommerce — nothing to resync." });
        }
        if (string.IsNullOrWhiteSpace(store.WooCommerceURL)
            || string.IsNullOrWhiteSpace(store.WooConsumerKey)
            || string.IsNullOrWhiteSpace(store.WooConsumerSecret))
        {
            return BadRequest(new { message = "Store is missing WooCommerce REST API credentials (URL / Consumer Key / Consumer Secret)." });
        }
        if (!int.TryParse(order.WooOrderID, out var wooOrderId) || wooOrderId <= 0)
        {
            return BadRequest(new { message = $"Order WooOrderID '{order.WooOrderID}' is not a valid integer." });
        }

        var meta = ShipmentStorefrontMeta.Build(shipment);

        // Build an INTERNAL (merchant-only) order note so the merchant
        // sees the tracking info land in the order timeline alongside
        // the metabox. We only attach a note when there's actually a
        // tracking number — re-syncing a half-populated record
        // shouldn't spam the timeline with "Tracking: (none)".
        //
        // noteIsCustomerNote is intentionally false: a manual resync
        // must NEVER trigger WooCommerce's "a note has been added to
        // your order" email to the buyer. The tracking number/URL/
        // courier are written to the order's `_dcp_*` meta in the same
        // call and surface to the buyer via the merchant's standard
        // processing/completed order emails.
        string? note = null;
        if (!string.IsNullOrWhiteSpace(shipment.TrackingNumber))
        {
            var trackingUrl = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                ShipmentStorefrontMeta.ShiplogicTrackingUrlFormat,
                Uri.EscapeDataString(shipment.TrackingNumber));
            note = $"Delicate Courier: shipment booked. Tracking number {shipment.TrackingNumber} ({trackingUrl}). Status: {ShipmentStorefrontMeta.StatusLabel(shipment.ShipmentStatus)}.";
        }

        var result = await _wooCommerceService.PushShipmentMetaDetailedAsync(
            store.StoreID, wooOrderId, meta, note, noteIsCustomerNote: false);

        _logger.LogInformation(
            "Manual resync for Shipment {ShipmentId} (WooOrderID {WooOrderId}): path={Path} success={Success} sent={Sent} updated={Updated} skipped={Skipped} preserved={Preserved} note={Note} http={Http}",
            shipmentId, wooOrderId, result.Path, result.Success, result.KeysSent, result.KeysUpdated,
            result.SkippedKeys?.Count ?? 0, result.PreservedKeys?.Count ?? 0, result.NoteAdded, result.HttpStatus);

        if (!result.Success)
        {
            return StatusCode(502, new
            {
                message = result.Summary,
                path = result.Path,
                httpStatus = result.HttpStatus,
                responseBody = result.ResponseBody,
                wooOrderId,
                keysSent = result.KeysSent,
            });
        }

        // Frontend reads `path` + `keysUpdated` to tell the operator whether
        // the plugin route actually engaged (path == "plugin" with a real
        // updated count) or we fell back to WC REST (which returns 200 even
        // when WC silently drops protected `_dcp_*` meta). The skipped /
        // preserved arrays let the UI pin-point which specific failure
        // mode is in play (key-name mismatch vs blank-value protection).
        return Ok(new
        {
            message = result.Summary,
            path = result.Path,
            wooOrderId,
            keysSent = result.KeysSent,
            keysUpdated = result.KeysUpdated,
            acceptedKeys = result.AcceptedKeys,
            skippedKeys = result.SkippedKeys,
            preservedKeys = result.PreservedKeys,
            noteAdded = result.NoteAdded,
            httpStatus = result.HttpStatus,
            keyCount = result.KeysSent, // backwards-compat for existing frontend
        });
    }

    /// <summary>
    /// Get tracking updates for a shipment
    /// GET /api/shipments/{shipmentId}/tracking
    /// </summary>
    [HttpGet("{shipmentId}/tracking")]
    public async Task<IActionResult> GetTracking(int shipmentId)
    {
        var shipment = await _context.Shipments
            .Include(s => s.Order).ThenInclude(o => o.Store).ThenInclude(st => st.Tenant)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ShipmentID == shipmentId);

        if (shipment == null)
        {
            return NotFound(new { message = $"Shipment {shipmentId} not found" });
        }

        // Tenant ownership check: a non-SuperAdmin caller may only see shipments
        // belonging to their own tenant. Prevents cross-tenant ID enumeration.
        var callerRole = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                         ?? User.FindFirst("Role")?.Value;
        var callerTenantClaim = User.FindFirst("TenantId")?.Value;
        if (callerRole != "SuperAdmin"
            && int.TryParse(callerTenantClaim, out var callerTenantId)
            && shipment.Order?.Store?.TenantID != callerTenantId)
        {
            return NotFound(new { message = $"Shipment {shipmentId} not found" });
        }

        if (string.IsNullOrEmpty(shipment.ConsignmentID))
        {
            return BadRequest(new { message = "Shipment has no consignment id yet" });
        }

        var bearerToken = shipment.Order?.Store?.Tenant?.ShiplogicBearerToken;
        if (string.IsNullOrEmpty(bearerToken))
        {
            return BadRequest(new { message = "Tenant Shiplogic bearer token not configured" });
        }

        try
        {
            var tracking = await _shiplogicService.GetTrackingUpdatesAsync(
                bearerToken,
                new List<string> { shipment.ConsignmentID });

            return Ok(tracking);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching tracking for ShipmentID: {ShipmentId}", shipmentId);
            return StatusCode(502, new { message = "Failed to fetch tracking from Shiplogic" });
        }
    }

    /// <summary>
    /// Download shipping label PDF for a shipment
    /// GET /api/Shipments/{shipmentId}/label
    /// </summary>
    [HttpGet("{shipmentId}/label")]
    public async Task<IActionResult> GetShipmentLabel(int shipmentId)
    {
        try
        {
            _logger.LogInformation("Label download requested for ShipmentID: {ShipmentId}", shipmentId);

            // Get label bytes (LabelService handles authorization internally)
            var pdfBytes = await _labelService.GetLabelBytesAsync(shipmentId);

            if (pdfBytes == null)
            {
                _logger.LogWarning("Label not found for ShipmentID: {ShipmentId}", shipmentId);

                return NotFound(new { message = "Label not found for this shipment" });
            }

            // Get label info for filename
            var label = await _labelService.GetLabelByShipmentIdAsync(shipmentId);
            var fileName = label?.BlobFileName ?? $"label_{shipmentId}.pdf";

            _logger.LogInformation("Returning label for ShipmentID: {ShipmentId}, Size: {Size} bytes", shipmentId, pdfBytes.Length);

            // Return PDF file
            return File(pdfBytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving label for ShipmentID: {ShipmentId}", shipmentId);

            return StatusCode(500, new { message = "Failed to retrieve label" });
        }
    }

    /// <summary>
    /// Get all shipments with optional filtering
    /// GET /api/shipments?days=7&status=Failed
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetShipments([FromQuery] int days = 30, [FromQuery] string? status = null)
    {
        var result = await _shipmentService.GetShipmentsAsync(days, status);

        return Ok(result);
    }

    /// <summary>
    /// Get dashboard statistics
    /// GET /api/shipments/stats?days=7
    /// </summary>
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats([FromQuery] int days = 7)
    {
        var result = await _shipmentService.GetStatsAsync(days);

        return Ok(result);
    }

    /// <summary>
    /// Get orders without shipments (ready for manual creation)
    /// GET /api/shipments/orders/unshipped
    /// </summary>
    [HttpGet("orders/unshipped")]
    public async Task<IActionResult> GetUnshippedOrders([FromQuery] int limit = 100)
    {
        var orders = await _shipmentService.GetUnshippedOrdersAsync(limit);
        return Ok(orders);
    }
}