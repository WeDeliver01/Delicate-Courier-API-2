using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Orders;
using DelicateCouriers.ApiService.Features.WooCommerce;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using DelicateCouriers.Features.Shiplogic;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace DelicateCouriers.ApiService.Features.Shipments;

/// <summary>
/// Orchestrates the complete shipment creation workflow
/// Coordinates between Order, Store, Shiplogic API, and database
/// </summary>
public class ShipmentOrchestrationService
{
    private readonly AppDbContext _context;
    private readonly IShiplogicService _shiplogicService;
    private readonly ILogger<ShipmentOrchestrationService> _logger;
    private readonly LabelService _labelService;
    private readonly OrderToShipmentMapper _orderToShipmentMapper;
    private readonly ISystemEventLogger _systemEvents;
    private readonly IWooCommerceService _wooCommerceService;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IWooFulfillmentVerifier _fulfillmentVerifier;
    private readonly DelicateCouriers.ApiService.Features.Notifications.IAdminEmailSender _adminEmailSender;

    public ShipmentOrchestrationService(AppDbContext context, IShiplogicService shiplogicService, ILogger<ShipmentOrchestrationService> logger, LabelService labelService, OrderToShipmentMapper orderToShipmentMapper, ISystemEventLogger systemEvents, IWooCommerceService wooCommerceService, IBackgroundJobClient backgroundJobClient, IWooFulfillmentVerifier fulfillmentVerifier, DelicateCouriers.ApiService.Features.Notifications.IAdminEmailSender adminEmailSender)
    {
        _adminEmailSender = adminEmailSender;
        _context = context;
        _shiplogicService = shiplogicService;
        _logger = logger;
        _labelService = labelService;
        _orderToShipmentMapper = orderToShipmentMapper;
        _systemEvents = systemEvents;
        _wooCommerceService = wooCommerceService;
        _backgroundJobClient = backgroundJobClient;
        _fulfillmentVerifier = fulfillmentVerifier;
    }

    /// <summary>
    /// Create a shipment for an order.
    /// Complete workflow: Order → Shiplogic → Save shipment → Retrieve label.
    ///
    /// IMPORTANT — Hangfire retry behaviour:
    ///  * Transient errors (Shiplogic 5xx, network failures, DB outages,
    ///    timeouts, anything unexpected) are RE-THROWN so Hangfire retries
    ///    them with exponential backoff. The job is configured to retry up
    ///    to 10 times (~24h total), after which it ends up in the Failed
    ///    queue and can be retried manually from the Hangfire dashboard.
    ///  * Permanent errors (order missing, store config incomplete, no
    ///    tenant Shiplogic token) RETURN a failure result without throwing,
    ///    so Hangfire does not retry them — retrying won't help, an admin
    ///    has to fix the configuration. We log loudly so they're visible.
    ///  * "Order already has a shipment" is idempotent success — handles
    ///    the case where the backend was restarted mid-execution and the
    ///    job is being replayed.
    /// </summary>
    [AutomaticRetry(Attempts = 10, OnAttemptsExceeded = AttemptsExceededAction.Fail, DelaysInSeconds = new[] { 30, 60, 120, 300, 600, 1200, 1800, 3600, 7200, 14400 })]
    public async Task<ShipmentCreationResult> CreateShipmentForOrderAsync(int orderId)
    {
        _logger.LogInformation("Starting shipment creation for OrderID: {OrderId}", orderId);

        // === Idempotent guards =================================================
        // Load order with all related data.
        //
        // NOTE: the four LogInformation calls in this block are intentionally
        // verbose — production was silently hanging between LoadOrderWithDetailsAsync
        // and the first mapper log line, with no exception and no further output.
        // These breadcrumbs make it possible to see EXACTLY which guard the
        // call dies on in the next deploy.
        _logger.LogInformation("Loading order details for OrderID: {OrderId}", orderId);
        var order = await LoadOrderWithDetailsAsync(orderId);
        _logger.LogInformation(
            "Order load complete for OrderID: {OrderId}. Found={Found}, HasStore={HasStore}, HasShipment={HasShipment}, LineItemCount={LineItems}",
            orderId,
            order != null,
            order?.Store != null,
            order?.Shipment != null,
            order?.LineItems?.Count ?? 0);
        if (order == null)
        {
            // Genuinely permanent — the order row is gone, no amount of
            // retrying will bring it back. Throw so the job is visible in
            // the Hangfire Failed queue but exhaust retries quickly via
            // the standing retry schedule.
            var msg = $"Order {orderId} not found — cannot create shipment.";
            _logger.LogError(msg);
            throw new InvalidOperationException(msg);
        }

        // Already booked — treat replay as success so a restart mid-execution
        // does not create duplicate Shiplogic consignments.
        if (order.Shipment != null)
        {
            _logger.LogInformation("Order {OrderId} already has Shipment {ShipmentId} — idempotent skip.", orderId, order.Shipment.ShipmentID);
            return ShipmentCreationResult.Success(order.Shipment.ShipmentID, order.Shipment.TrackingNumber ?? string.Empty);
        }

        // ===== LAYER 2: collect-order defensive guard ==========================
        // The webhook-intake gate (PluginWebhookService.ShouldAutoCreateShipment)
        // already refuses to enqueue collect orders, but a collect order recently
        // slipped through and got booked anyway. The root cause was probably a
        // SECOND trigger path that bypassed the intake gate — the manual
        // "create shipment" admin button (ShipmentsController.CreateShipment),
        // a Hangfire retry that was enqueued before the v2.4.2 plugin push,
        // or an order-status-change hook that re-fires booking after the
        // status moves from pending → processing.
        //
        // Whichever it was, the fix is the same: refuse to call Shiplogic for
        // any order whose persisted FulfillmentType is "collect", at the
        // single chokepoint that ALL booking paths funnel through. New
        // trigger paths added in future are automatically covered.
        //
        // Returns Failure (not throw) so Hangfire does NOT retry — re-running
        // won't change the order's fulfillment choice. The job ends cleanly
        // and the merchant sees a single explanatory log line instead of a
        // failed-queue entry.
        if (string.Equals(order.FulfillmentType, "collect", StringComparison.OrdinalIgnoreCase))
        {
            var skipMsg = $"Order {orderId} (WooOrderID {order.WooOrderID}) is a customer-collect order — refusing to book a Shiplogic shipment.";
            _logger.LogInformation(
                "Layer-2 collect guard hit for OrderID {OrderId} (WooOrderID {WooOrderId}). " +
                "No Shiplogic call made, no address read, no charge to merchant.",
                orderId, order.WooOrderID);
            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "shipment.skipped_collect",
                ActorKind = "System",
                ActorLabel = "ShipmentOrchestrationService",
                TenantId = order.TenantID,
                EntityType = "Order",
                EntityRef = $"#{order.WooOrderNumber}",
                Message = $"Skipped Shiplogic booking for Order #{order.WooOrderNumber}: customer is collecting in person.",
                Details = new
                {
                    orderId,
                    wooOrderId = order.WooOrderID,
                    wooOrderNumber = order.WooOrderNumber,
                    fulfillmentType = order.FulfillmentType,
                    layer = "CreateShipmentForOrderAsync",
                },
            });
            return ShipmentCreationResult.Failure(skipMsg);
        }

        // ===== Terminal-status guard ===========================================
        // A booking can be enqueued while the order is eligible and executed
        // later (Hangfire delay/retry, status-reconciliation sweep). If the
        // order was cancelled/refunded in the meantime, refuse at this single
        // chokepoint rather than shipping a cancelled order. Failure (not
        // throw) — retrying will not un-cancel the order.
        var terminalStatuses = new[] { "Cancelled", "Canceled", "Refunded", "Failed", "Trash" };
        if (terminalStatuses.Contains(order.OrderStatus, StringComparer.OrdinalIgnoreCase))
        {
            var terminalMsg = $"Order {orderId} (#{order.WooOrderNumber}) is in terminal status '{order.OrderStatus}' — refusing to book a shipment.";
            _logger.LogInformation("Terminal-status guard hit for OrderID {OrderId}: status '{Status}'. No Shiplogic call made.",
                orderId, order.OrderStatus);
            return ShipmentCreationResult.Failure(terminalMsg);
        }

        // ===== Special-trip manual-booking guard ===============================
        // Checked BEFORE the collection-keyword guard: an order only becomes
        // special_trip after intake verified its shipping line via Woo REST
        // (label-based reclassification), so it is delivery by definition —
        // re-running the collection check would just burn a Woo REST call.
        // Shiplogic's API refuses SPX (special trip) creation on this account
        // ("You do not have permission to create rates for special trips",
        // verified live 2026-08-13) and that permission cannot be enabled.
        // Out-of-coverage orders therefore CANNOT be auto-booked. Instead of
        // burning 10 Hangfire retries on a guaranteed 400, we:
        //   1. leave the order unbooked (no Shipment row),
        //   2. write a note + _dcp_special_trip meta back to the Woo order,
        //   3. email the platform admin with everything needed to book the
        //      trip manually in the Shiplogic dashboard,
        //   4. emit a system event (also the idempotency marker so webhook
        //      retries / re-enqueues don't send duplicate emails).
        // Returns Failure (not throw) so Hangfire does NOT retry.
        if (string.Equals(order.FulfillmentType, "special_trip", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleSpecialTripManualBookingAsync(order);
        }
        // =======================================================================

        // ===== LAYER 3: shipping-method keyword guard ==========================
        // Some stores sell paid collection points as WooCommerce `flat_rate`
        // methods whose ONLY collection signal is the merchant-written label
        // ("Collect from 1 Clifford road… R95.00" — Baked By Nataleen,
        // store #7). Such orders arrive tagged `delivery` from the plugin, so
        // the Layer-2 guard above doesn't catch them. Intake now verifies the
        // shipping lines via Woo REST and persists them on the order; here we
        // re-apply the same keyword check (persisted data first, Woo REST
        // re-fetch as fallback for orders created before this guard existed)
        // so a retried or manually triggered booking for a collection order
        // is refused no matter which path enqueued it.
        var collectGate = await CheckCollectionShippingMethodAsync(order);
        if (collectGate != null)
        {
            return collectGate;
        }
        // =======================================================================

        // === Operational failures (THROW so they retry AND are visible) ========
        // Store config and tenant Shiplogic token are "fixable operationally":
        // an admin can correct them and the next retry will succeed. We THROW
        // (not silently return Failure) so the job lives in Hangfire and is
        // visible in the dashboard until the admin fixes the underlying data.
        // The standing [AutomaticRetry] schedule gives them ~24h to react
        // before the job moves to Failed.
        // If the plugin webhook didn't carry a delivery slot (older plugin
        // versions don't recognise every checkout plugin's meta keys), try
        // to backfill it straight from the WooCommerce order meta before
        // mapping — otherwise the booking silently defaults to "tomorrow".
        await TryBackfillRequestedDeliveryFromWooAsync(order);

        DelicateCouriers.Features.Shiplogic.DTOs.CreateShipmentRequest shipmentRequest;
        try
        {
            _logger.LogInformation("Mapping Order {OrderId} -> Shiplogic request (StoreID={StoreId}).", orderId, order.StoreID);
            shipmentRequest = await _orderToShipmentMapper.MapOrderToShipmentAsync(order, order.Store);
            _logger.LogInformation(
                "Mapper complete for OrderID: {OrderId}. Parcels={Parcels}, Service={Service}, CustomerRef={Ref}",
                orderId, shipmentRequest.Parcels?.Count ?? 0, shipmentRequest.ServiceLevelCode, shipmentRequest.CustomerReference);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Store/order configuration prevents shipment creation for OrderID: {OrderId}. Hangfire will retry until config is fixed.", orderId);
            throw;
        }
        catch (Exception ex)
        {
            // Previously this catch only handled InvalidOperationException, so a
            // NullReferenceException (e.g. null Store reference from a tenant
            // query filter) would bubble up unobserved. Now we log everything
            // before rethrowing so silent hangs become visible exceptions.
            _logger.LogError(ex, "Unexpected mapper failure for OrderID: {OrderId} ({ExceptionType}).", orderId, ex.GetType().FullName);
            throw;
        }

        _logger.LogInformation("Looking up tenant {TenantId} for OrderID: {OrderId}", order.TenantID, orderId);
        var tenant = await _context.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.TenantID == order.TenantID);
        if (tenant == null || string.IsNullOrWhiteSpace(tenant.ShiplogicBearerToken))
        {
            var msg = $"Tenant {order.TenantID} has no Shiplogic bearer token — cannot book Order {orderId}. Configure the tenant token and the next retry will succeed.";
            _logger.LogError(msg);
            throw new InvalidOperationException(msg);
        }

        // === Transient failures (let exceptions propagate so Hangfire retries) =
        // The whole "acquire advisory lock → check Shiplogic → create shipment
        // → save row → commit" block must run inside ONE transaction so the
        // pg_advisory_xact_lock survives until the row is durable. But we
        // also configure EF Core with EnableRetryOnFailure (see Program.cs),
        // which activates NpgsqlRetryingExecutionStrategy. That strategy
        // refuses manually-started transactions unless they're wrapped in
        // its ExecuteAsync, otherwise it throws
        //   "The configured execution strategy 'NpgsqlRetryingExecutionStrategy'
        //    does not support user-initiated transactions"
        // — which is exactly what was silently failing every Hangfire shipment
        // job in production. Wrap accordingly. Re-running the lambda is safe
        // because (a) the advisory-lock re-check below catches a sibling that
        // committed a Shipment, and (b) the Shiplogic FindShipmentByCustomerReference
        // guard below makes the Shiplogic-create idempotent.
        // These are populated by the strategy delegate below. They live OUTSIDE
        // the lambda so the post-transaction label fetch can see them, but they
        // MUST be reset at the top of each attempt — NpgsqlRetryingExecutionStrategy
        // may re-invoke the delegate on a transient DB failure, and stale state
        // from a failed previous attempt would corrupt the sibling-skip decision.
        Shipment? createdShipment = null;
        DelicateCouriers.Features.Shiplogic.DTOs.CreateShipmentResponse? shiplogicResponse = null;
        bool siblingWon = false;
        try
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                // Reset per-attempt state — see comment above.
                createdShipment = null;
                shiplogicResponse = null;
                siblingWon = false;

                // PER-ORDER SERIALIZATION (concurrent-job race guard)
                // ---------------------------------------------------
                // Two Hangfire workers could enqueue + pick up CreateShipmentForOrderAsync
                // for the same orderId at the same time (duplicate webhook deliveries,
                // manual re-trigger after stuck job, etc). Without serialization they
                // would BOTH see no local shipment, BOTH miss on the Shiplogic lookup,
                // and BOTH create a consignment.
                //
                // We use a Postgres transaction-scoped advisory lock keyed on orderId.
                // The lock is released automatically when the transaction commits or
                // rolls back. The lock is acquired blocking — concurrent jobs queue
                // up here and the second one will see the shipment created by the
                // first via the re-check below.
                await using var lockTx = await _context.Database.BeginTransactionAsync();
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({(long)orderId})");

                // Re-check INSIDE the lock — a sibling job may have just finished.
                var existingShipment = await _context.Shipments
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.OrderID == orderId);
                if (existingShipment != null)
                {
                    _logger.LogInformation(
                        "Sibling job won the race for Order {OrderId} — Shipment {ShipmentId} already exists. Idempotent skip.",
                        orderId, existingShipment.ShipmentID);
                    await lockTx.CommitAsync();
                    createdShipment = existingShipment;
                    siblingWon = true;
                    return;
                }

            // CROSS-SYSTEM IDEMPOTENCY GUARD
            // ------------------------------
            // Before asking Shiplogic to CREATE a shipment, ask Shiplogic
            // whether one already exists for this customer reference. This
            // closes the narrow crash window between a successful Shiplogic
            // create on a prior attempt and the local DB save that follows
            // it. Without this guard, a crash in that window would cause
            // the next Hangfire retry to create a SECOND consignment in
            // Shiplogic for the same WooCommerce order.
            //
            // The lookup is best-effort: if Shiplogic doesn't support the
            // query or the call fails for any reason, FindShipmentByCustomerReferenceAsync
            // returns null and we fall through to create (i.e. the worst
            // case is exactly the pre-guard behaviour).
                if (!string.IsNullOrWhiteSpace(shipmentRequest.CustomerReference))
                {
                    shiplogicResponse = await _shiplogicService.FindShipmentByCustomerReferenceAsync(
                        tenant.ShiplogicBearerToken,
                        shipmentRequest.CustomerReference);

                    if (shiplogicResponse != null)
                    {
                        _logger.LogWarning(
                            "Shiplogic already has a shipment for Order {OrderId} (Reference {Reference}, ShipmentId {ShipmentId}). " +
                            "Reusing instead of creating a duplicate — this is the crash-window idempotency guard.",
                            orderId, shipmentRequest.CustomerReference, shiplogicResponse.ShipmentId);
                    }
                }

                if (shiplogicResponse == null)
                {
                    _logger.LogInformation("Creating shipment in Shiplogic for Order {OrderId}. Weight: {Weight}kg, Service: {Service}",
                                            orderId,
                                            shipmentRequest.Parcels.Sum(p => p.Weight),
                                            shipmentRequest.ServiceLevelCode
                    );

                    shiplogicResponse = await _shiplogicService.CreateShipmentAsync(tenant.ShiplogicBearerToken, shipmentRequest);

                    _logger.LogInformation("Shipment created in Shiplogic. ConsignmentId: {ConsignmentId}, Tracking: {TrackingNumber}",
                        shiplogicResponse.ConsignmentId, shiplogicResponse.TrackingNumber);
                }

                // Step 7: Save shipment to database
                var shipment = new Shipment
                {
                    OrderID = order.OrderID,
                    ConsignmentID = shiplogicResponse.ConsignmentId,
                    TrackingNumber = shiplogicResponse.TrackingNumber,
                    CourierName = shiplogicResponse.CollectionBranchName ?? "Unknown",
                    CourierService = shiplogicResponse.ServiceLevelCode ?? order.Store.DefaultServiceLevel,
                    ShipmentStatus = shiplogicResponse.Status ?? "Created",
                    ShippingCost = shiplogicResponse.Rate ?? 0m,
                    EstimatedDeliveryDate = shiplogicResponse.EstimatedDeliveryDate,
                    CreatedOn = DateTime.UtcNow,
                    CreatedBy = "ShipmentOrchestration"
                };

                _context.Shipments.Add(shipment);

                await _context.SaveChangesAsync();

                // Commit the lock transaction now — the shipment row is durable
                // and the per-order advisory lock can be released so a sibling
                // worker that's blocked waiting can wake up and see this row via
                // the inside-the-lock re-check. We deliberately commit BEFORE
                // the (slow, non-critical) label fetch below so the lock isn't
                // held any longer than necessary.
                await lockTx.CommitAsync();

                _logger.LogInformation("Shipment saved to database. ShipmentID: {ShipmentId}, OrderID: {OrderId}", shipment.ShipmentID, orderId);
                createdShipment = shipment;
            });

            // If a sibling job had already created the shipment, short-circuit
            // — no Shiplogic create happened on this attempt, so there's nothing
            // to label. Use the explicit flag rather than inferring from
            // (shiplogicResponse == null) — a previous failed retry attempt
            // could have populated shiplogicResponse before throwing, and the
            // reset at the top of the delegate only nulls it on the NEXT
            // successful entry, not on a path that exits via sibling-won.
            if (siblingWon)
            {
                return ShipmentCreationResult.Success(createdShipment!.ShipmentID, createdShipment.TrackingNumber ?? string.Empty);
            }

            // ==================== STEP 8: AUTO-FETCH LABEL ====================
            // Deliberately OUTSIDE the execution-strategy/transaction block so a
            // transient label-fetch failure doesn't replay the shipment create.
            try
            {
                _logger.LogInformation("Fetching label for Shiplogic shipment ID: {ShiplogicShipmentId}",
                    shiplogicResponse!.ShipmentId);

                var labelBytes = await _shiplogicService.GetShipmentLabelAsync(
                    tenant.ShiplogicBearerToken,
                    shiplogicResponse.ShipmentId);

                if (labelBytes != null && labelBytes.Length > 0)
                {
                    await _labelService.SaveLabelAsync(
                        createdShipment!.ShipmentID,
                        labelBytes,
                        shiplogicResponse.ShipmentId);

                    _logger.LogInformation("Label fetched and saved for ShipmentID: {ShipmentId}. Size: {Size} bytes",
                        createdShipment.ShipmentID, labelBytes.Length);
                }
                else
                {
                    _logger.LogWarning("Label not available yet for Shiplogic shipment ID: {ShiplogicShipmentId}. " +
                        "Label can be fetched manually later.", shiplogicResponse.ShipmentId);
                }
            }
            catch (Exception labelEx)
            {
                // Don't fail the entire shipment if label fetch fails
                // Label can be fetched manually later via GET /api/Shipments/{id}/label endpoint
                _logger.LogError(labelEx, "Failed to fetch label for ShipmentID: {ShipmentId}. " +
                    "Shipment created successfully but label not saved. Label can be fetched manually later.", createdShipment!.ShipmentID);
            }
            // ==================== END AUTO-FETCH LABEL ====================

            // Emit a SystemEvent so SuperAdmin sees the successful booking
            // in the audit trail alongside the corresponding order.received.
            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "shipment.booked",
                ActorKind = "System",
                ActorLabel = "ShipmentOrchestration",
                TenantId = order.TenantID,
                EntityType = "Shipment",
                EntityRef = createdShipment!.TrackingNumber,
                Message = $"Shipment booked for Order #{order.WooOrderNumber} (tracking {createdShipment.TrackingNumber})",
                Details = new
                {
                    orderId = order.OrderID,
                    wooOrderNumber = order.WooOrderNumber,
                    shipmentId = createdShipment.ShipmentID,
                    consignmentId = createdShipment.ConsignmentID,
                    trackingNumber = createdShipment.TrackingNumber,
                    courier = createdShipment.CourierName,
                    cost = createdShipment.ShippingCost,
                },
            });

            // ==================== PUSH TRACKING BACK TO STOREFRONT ====================
            // Best-effort: tell WooCommerce (or future Shopify) about the tracking
            // number so the merchant sees it on the order page immediately. Any
            // failure here is logged but does NOT fail the shipment job — the
            // shipment is already booked and the tracking webhook handler will
            // retry the push when Shiplogic reports the first status change.
            await TryPushTrackingToStorefrontAsync(order, createdShipment!);
            // ==================== END PUSH TRACKING ====================

            return ShipmentCreationResult.Success(createdShipment!.ShipmentID, shiplogicResponse!.TrackingNumber);
        }
        catch (Exception ex)
        {
            // Log and RE-THROW so Hangfire retries the job. Anything that
            // gets here is transient or unexpected — Shiplogic 5xx, network
            // blip, DB transient — exactly the class of failure that should
            // be retried with backoff instead of silently dropped.
            _logger.LogError(ex, "Transient failure creating shipment for OrderID: {OrderId} — Hangfire will retry.", orderId);

            // Emit a SystemEvent on every failed attempt — this is the class
            // of error that previously hung silently for 24h while Hangfire
            // retried (the NpgsqlRetryingExecutionStrategy bug). With the
            // log in place a SuperAdmin sees the failure immediately.
            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = "shipment.book_failed",
                ActorKind = "System",
                ActorLabel = "ShipmentOrchestration",
                TenantId = order.TenantID,
                EntityType = "Order",
                EntityRef = $"#{order.WooOrderNumber}",
                Message = $"Shipment booking failed for Order #{order.WooOrderNumber}: {ex.Message}",
                Details = new
                {
                    orderId = order.OrderID,
                    wooOrderNumber = order.WooOrderNumber,
                    exceptionType = ex.GetType().FullName,
                    exceptionMessage = ex.Message,
                },
            });

            throw;
        }
    }

    /// <summary>
    /// Push the freshly-booked tracking number back to the originating storefront
    /// (WooCommerce today, Shopify later). Best-effort and isolated from the
    /// shipment-creation result: any failure is logged but never thrown.
    /// </summary>
    private async Task TryPushTrackingToStorefrontAsync(Order order, Shipment shipment)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(shipment.TrackingNumber))
            {
                return;
            }

            var store = order.Store;
            if (store == null)
            {
                _logger.LogWarning("Cannot push tracking for Order {OrderId} — Store navigation not loaded.", order.OrderID);
                return;
            }

            // Shopify storefronts push tracking back as a Shopify fulfillment.
            // This is done on a Hangfire job (not inline) so the booking job
            // returns promptly and Shopify API hiccups get their own retry
            // schedule, independent of the shipment-creation job.
            if (string.Equals(store.Platform, "shopify", StringComparison.OrdinalIgnoreCase))
            {
                _backgroundJobClient.Enqueue<DelicateCouriers.ApiService.Features.Shopify.ShopifyTrackingWriteback>(
                    w => w.WriteTrackingAsync(order.OrderID));
                _logger.LogInformation(
                    "Enqueued Shopify tracking write-back for Order {OrderId} (Shipment {ShipmentId}).",
                    order.OrderID, shipment.ShipmentID);
                return;
            }

            // Only WooCommerce push is wired beyond this point.
            if (!string.Equals(store.Platform, "woocommerce", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping tracking push for Order {OrderId} — store Platform '{Platform}' is not WooCommerce or Shopify.",
                    order.OrderID, store.Platform);
                return;
            }

            if (string.IsNullOrWhiteSpace(store.WooCommerceURL) ||
                string.IsNullOrWhiteSpace(store.WooConsumerKey) ||
                string.IsNullOrWhiteSpace(store.WooConsumerSecret))
            {
                _logger.LogWarning(
                    "Cannot push tracking for Order {OrderId} — Store {StoreId} is missing WooCommerce REST API credentials (URL/key/secret).",
                    order.OrderID, store.StoreID);
                return;
            }

            if (!int.TryParse(order.WooOrderID, out var wooOrderId) || wooOrderId <= 0)
            {
                _logger.LogWarning(
                    "Cannot push tracking for Order {OrderId} — WooOrderID '{WooOrderId}' is not a valid integer.",
                    order.OrderID, order.WooOrderID);
                return;
            }

            // Push the _dcp_* meta UNCONDITIONALLY (not gated by
            // TrackingPushedOn). meta_data is an upsert on the WooCommerce
            // side — re-running is cheap and keeps the metabox in sync if a
            // booking is retried, partially re-processed, or replayed after
            // the customer-visible note was already posted. Only the note
            // POST below is idempotency-gated, because that note is visible
            // to the customer and we never want to duplicate it.
            var metaOk = await _wooCommerceService.PushShipmentMetaAsync(
                store.StoreID, wooOrderId, ShipmentStorefrontMeta.Build(shipment));
            if (!metaOk)
            {
                _logger.LogWarning(
                    "Shipment meta push to WooCommerce returned false for Shipment {ShipmentId} (Order {OrderId}).",
                    shipment.ShipmentID, order.OrderID);
            }

            // Persisted idempotency: if a previous attempt already pushed the
            // tracking note (visible to the customer), don't post a duplicate.
            if (shipment.TrackingPushedOn.HasValue)
            {
                _logger.LogDebug("Tracking note already pushed for Shipment {ShipmentId} at {PushedOn} — skipping duplicate note (meta was still upserted above).",
                    shipment.ShipmentID, shipment.TrackingPushedOn);
                return;
            }

            var courier = !string.IsNullOrWhiteSpace(shipment.CourierName) ? shipment.CourierName : "Shiplogic";
            var ok = await _wooCommerceService.AddTrackingInfoAsync(
                store.StoreID, wooOrderId, shipment.TrackingNumber, courier);

            if (ok)
            {
                // Stamp the marker so the Shiplogic-webhook pushback path
                // (and any future re-run of this booking job) won't post the
                // same note again. The Shipment was added to _context above,
                // so this is part of the current change-tracker session.
                shipment.TrackingPushedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                _logger.LogInformation(
                    "Pushed tracking {Tracking} for Shipment {ShipmentId} to WooCommerce order {WooOrderId} (Store {StoreId}).",
                    shipment.TrackingNumber, shipment.ShipmentID, wooOrderId, store.StoreID);

                await _systemEvents.LogAsync(new SystemEventEntry
                {
                    EventType = "shipment.tracking_pushed",
                    ActorKind = "System",
                    ActorLabel = "ShipmentOrchestration",
                    TenantId = order.TenantID,
                    EntityType = "Shipment",
                    EntityRef = shipment.TrackingNumber,
                    Message = $"Tracking {shipment.TrackingNumber} pushed to WooCommerce order #{order.WooOrderNumber}.",
                    Details = new
                    {
                        orderId = order.OrderID,
                        wooOrderId,
                        storeId = store.StoreID,
                        trackingNumber = shipment.TrackingNumber,
                        courier,
                    },
                });
            }
            else
            {
                _logger.LogWarning(
                    "WooCommerce rejected the tracking-info note for Shipment {ShipmentId} (Order {OrderId}, WooOrder {WooOrderId}).",
                    shipment.ShipmentID, order.OrderID, wooOrderId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Best-effort tracking pushback failed for Shipment {ShipmentId} (Order {OrderId}) — shipment booking is unaffected.",
                shipment.ShipmentID, order.OrderID);
        }
    }

    /// <summary>
    /// Layer-3 pre-booking guard: refuse to book any order whose chosen
    /// WooCommerce shipping method looks like in-person collection
    /// ("collect"/"pickup" in the method id or title), even when the
    /// order's FulfillmentType says "delivery".
    ///
    /// Returns a <see cref="ShipmentCreationResult"/> when booking must be
    /// refused, or null when booking may proceed.
    ///
    /// Decision order:
    ///  1. Persisted ShippingMethodId/Title (stamped by intake verification)
    ///     — no REST round-trip needed.
    ///  2. Nothing persisted and it's a REST-reachable Woo store → re-fetch
    ///     the shipping lines from Woo (covers orders created before this
    ///     guard existed, and manual/retried bookings).
    ///  3. Re-fetch failed → THROW. Hangfire retries with backoff, the job
    ///     stays visible, and we never book while unverified — fail safe,
    ///     but a transient Woo blip doesn't permanently block a genuine
    ///     delivery.
    ///  4. Unverifiable (no Woo creds / Shopify store) → proceed; the
    ///     Shopify pipeline has its own gating and non-REST Woo stores keep
    ///     legacy behaviour.
    /// </summary>
    /// <summary>
    /// Special-trip orders cannot be auto-booked (Shiplogic denies SPX
    /// creation on this account). Notify the admin so the trip is booked
    /// manually in the Shiplogic dashboard, write a marker note + meta back
    /// to the Woo order, and emit a system event. Idempotent: the system
    /// event doubles as the "already notified" marker, so webhook retries
    /// and re-enqueues do not send duplicate emails or notes.
    /// </summary>
    private async Task<ShipmentCreationResult> HandleSpecialTripManualBookingAsync(Order order)
    {
        const string eventType = "shipment.special_trip_manual_booking";
        var resultMsg = $"Order {order.OrderID} (Woo #{order.WooOrderNumber}) is a special trip — Shiplogic cannot auto-book it; admin notified for manual booking.";

        // Stable idempotency key: WooOrderNumber can in principle be blank, and
        // a blank "#" ref would make every later special trip look "handled".
        var entityRef = string.IsNullOrWhiteSpace(order.WooOrderNumber)
            ? $"order:{order.OrderID}"
            : $"#{order.WooOrderNumber}";

        // Serialize concurrent booking jobs for the same order (webhook retry +
        // manual endpoint racing) with the same transaction-scoped advisory
        // lock used for normal booking, so exactly one run sends the email /
        // Woo note. NpgsqlRetryingExecutionStrategy rejects user-initiated
        // transactions, so the transaction must run inside an execution
        // strategy. Skipped on non-relational (in-memory test) providers.
        if (!_context.Database.IsRelational())
        {
            return await RunSpecialTripCoreAsync(order, eventType, entityRef, resultMsg);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var lockTx = await _context.Database.BeginTransactionAsync();
            await _context.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({(long)order.OrderID})");
            var result = await RunSpecialTripCoreAsync(order, eventType, entityRef, resultMsg);
            await lockTx.CommitAsync(); // releases the advisory lock
            return result;
        });
    }

    private async Task<ShipmentCreationResult> RunSpecialTripCoreAsync(
        Order order, string eventType, string entityRef, string resultMsg)
    {
        var alreadyHandled = await _context.SystemEvents
            .IgnoreQueryFilters()
            .AnyAsync(e => e.EventType == eventType
                        && e.TenantID == order.TenantID
                        && e.EntityType == "Order"
                        && e.EntityRef == entityRef);
        if (alreadyHandled)
        {
            _logger.LogInformation(
                "Special-trip manual-booking notification already sent for Order {OrderId} — idempotent skip.",
                order.OrderID);
            return ShipmentCreationResult.Failure(resultMsg);
        }

        var km = order.SpecialTripDistanceKm?.ToString("0.#") ?? "?";
        var amount = order.SpecialTripQuotedAmount?.ToString("0.00") ?? "?";
        var deliveryDate = order.RequestedDeliveryDate?.ToString("ddd, dd MMM yyyy") ?? "not specified";
        var deliveryTime = string.IsNullOrWhiteSpace(order.RequestedDeliveryTime) ? "" : $" {order.RequestedDeliveryTime}";
        var address = string.Join(", ", new[]
        {
            order.ShippingAddressLine1, order.ShippingAddressLine2, order.ShippingSuburb,
            order.ShippingCity, order.ShippingProvince, order.ShippingPostalCode,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // 1) Woo write-back: internal order note + _dcp_ meta so the merchant
        //    sees the special-trip flag right on the order screen.
        if (!string.IsNullOrWhiteSpace(order.WooOrderID) && int.TryParse(order.WooOrderID, out var wooId))
        {
            var note = $"SPECIAL TRIP (out of courier coverage) — R{amount} for {km} km. " +
                       $"Automatic booking is not possible; Delicate Couriers will book this trip manually. " +
                       $"Requested delivery: {deliveryDate}{deliveryTime}.";
            var meta = new Dictionary<string, string?>
            {
                ["_dcp_special_trip"] = "yes",
                ["_dcp_special_trip_distance_km"] = order.SpecialTripDistanceKm?.ToString("0.#"),
                ["_dcp_special_trip_amount"] = order.SpecialTripQuotedAmount?.ToString("0.00"),
                ["_dcp_special_trip_booking"] = "manual",
            };
            try
            {
                await _wooCommerceService.PushShipmentMetaDetailedAsync(order.StoreID, wooId, meta, note);
            }
            catch (Exception ex)
            {
                // Never let Woo write-back failure block the admin email.
                _logger.LogError(ex, "Failed writing special-trip note/meta to Woo order {WooOrderId} (Order {OrderId}).", wooId, order.OrderID);
            }
        }

        // 2) Admin email with everything needed to book manually.
        var subject = $"Special trip to book manually — {order.Store?.StoreName ?? $"Store {order.StoreID}"} order #{order.WooOrderNumber} (R{amount})";
        var html =
            $"<h2>Special trip requires manual booking</h2>" +
            $"<p>Shiplogic cannot auto-book this out-of-coverage delivery. Please create the trip manually in the Shiplogic dashboard.</p>" +
            $"<table cellpadding=\"4\" style=\"border-collapse:collapse\">" +
            $"<tr><td><b>Store</b></td><td>{WebUtility.HtmlEncode(order.Store?.StoreName ?? order.StoreID.ToString())}</td></tr>" +
            $"<tr><td><b>Order #</b></td><td>{WebUtility.HtmlEncode(order.WooOrderNumber ?? "?")}</td></tr>" +
            $"<tr><td><b>Customer reference</b></td><td>WC-{WebUtility.HtmlEncode(order.WooOrderID ?? order.WooOrderNumber ?? "?")}</td></tr>" +
            $"<tr><td><b>Rate charged to customer</b></td><td>R{amount} ({km} km)</td></tr>" +
            $"<tr><td><b>Customer</b></td><td>{WebUtility.HtmlEncode(order.CustomerName)}</td></tr>" +
            $"<tr><td><b>Phone</b></td><td>{WebUtility.HtmlEncode(order.CustomerPhone)}</td></tr>" +
            $"<tr><td><b>Email</b></td><td>{WebUtility.HtmlEncode(order.CustomerEmail)}</td></tr>" +
            $"<tr><td><b>Delivery address</b></td><td>{WebUtility.HtmlEncode(address)}</td></tr>" +
            $"<tr><td><b>Requested delivery</b></td><td>{WebUtility.HtmlEncode(deliveryDate + deliveryTime)}</td></tr>" +
            $"<tr><td><b>Order total</b></td><td>R{order.OrderTotal:0.00}</td></tr>" +
            $"</table>";

        var emailSent = await _adminEmailSender.SendAsync(subject, html);

        // 3) System event — audit + idempotency marker. Logged AFTER the
        //    email attempt so a hard crash before this point lets the next
        //    webhook retry try the email again.
        await _systemEvents.LogAsync(new SystemEventEntry
        {
            EventType = eventType,
            ActorKind = "System",
            ActorLabel = "ShipmentOrchestrationService",
            TenantId = order.TenantID,
            EntityType = "Order",
            EntityRef = entityRef,
            Message = $"NEEDS MANUAL BOOKING: special trip for Order #{order.WooOrderNumber} — R{amount} / {km} km to {address}. " +
                      (emailSent ? $"Admin notified at {_adminEmailSender.RecipientAddress}." : "ADMIN EMAIL FAILED — check email provider configuration."),
            Details = new
            {
                orderId = order.OrderID,
                wooOrderId = order.WooOrderID,
                storeId = order.StoreID,
                distanceKm = order.SpecialTripDistanceKm,
                quotedAmount = order.SpecialTripQuotedAmount,
                requestedDeliveryDate = order.RequestedDeliveryDate,
                requestedDeliveryTime = order.RequestedDeliveryTime,
                emailSent,
                emailRecipient = _adminEmailSender.RecipientAddress,
            },
        });

        _logger.LogInformation(
            "Special trip Order {OrderId}: manual-booking flow complete. EmailSent={EmailSent}, Recipient={Recipient}",
            order.OrderID, emailSent, _adminEmailSender.RecipientAddress);

        // Failure (not throw): retrying cannot change the outcome.
        return ShipmentCreationResult.Failure(resultMsg);
    }

    private async Task<ShipmentCreationResult?> CheckCollectionShippingMethodAsync(Order order)
    {
        string? methodId = order.ShippingMethodId;
        string? methodTitle = order.ShippingMethodTitle;

        var hasPersisted = !string.IsNullOrWhiteSpace(methodId) || !string.IsNullOrWhiteSpace(methodTitle);
        if (!hasPersisted)
        {
            var verification = await _fulfillmentVerifier.VerifyAsync(order.Store, order.WooOrderID);
            switch (verification.Outcome)
            {
                case FulfillmentVerificationOutcome.Unverifiable:
                    return null; // not a REST-reachable Woo store — nothing to check

                case FulfillmentVerificationOutcome.LookupFailed:
                    var failMsg =
                        $"Cannot verify shipping method for Order {order.OrderID} (WooOrderID {order.WooOrderID}) — " +
                        "Woo REST lookup failed. Refusing to book until the shipping method can be verified " +
                        "(the customer may have chosen a collection point). Hangfire will retry.";
                    _logger.LogError(failMsg);
                    await _systemEvents.LogAsync(new SystemEventEntry
                    {
                        EventType = "order.fulfillment_verification_failed",
                        ActorKind = "System",
                        ActorLabel = "ShipmentOrchestrationService",
                        TenantId = order.TenantID,
                        EntityType = "Order",
                        EntityRef = $"#{order.WooOrderNumber}",
                        Message = $"NEEDS REVIEW: shipment booking for Order #{order.WooOrderNumber} withheld — could not verify the chosen shipping method via Woo REST.",
                        Details = new
                        {
                            orderId = order.OrderID,
                            wooOrderId = order.WooOrderID,
                            storeId = order.StoreID,
                            layer = "CheckCollectionShippingMethodAsync",
                        },
                    });
                    throw new InvalidOperationException(failMsg);

                default:
                    // ConfirmedDelivery or Collection — persist what we learned
                    // so future retries skip the REST round-trip.
                    methodId = verification.ShippingMethodId;
                    methodTitle = verification.ShippingMethodTitle;
                    order.ShippingMethodId = methodId;
                    order.ShippingMethodTitle = methodTitle;
                    await _context.SaveChangesAsync();
                    break;
            }
        }

        if (!_fulfillmentVerifier.IsCollectionMethod(methodId, methodTitle))
        {
            return null; // genuine delivery — proceed with booking
        }

        // Collection method detected — reclassify and refuse. Return Failure
        // (not throw) so Hangfire does NOT retry: re-running won't change the
        // customer's shipping choice.
        order.FulfillmentType = "collect";
        order.ChangedOn = DateTime.UtcNow;
        order.ChangedBy = "FulfillmentVerification";
        await _context.SaveChangesAsync();

        var skip =
            $"Order {order.OrderID} (WooOrderID {order.WooOrderID}) chose collection shipping method " +
            $"\"{methodTitle ?? methodId}\" — refusing to book a Shiplogic shipment.";
        _logger.LogWarning(
            "Layer-3 collection-method guard hit for OrderID {OrderId} (WooOrderID {WooOrderId}). " +
            "Method id(s): '{MethodIds}', title(s): '{MethodTitles}'. No Shiplogic call made.",
            order.OrderID, order.WooOrderID, methodId ?? "(none)", methodTitle ?? "(none)");

        await _systemEvents.LogAsync(new SystemEventEntry
        {
            EventType = "shipment.skipped_collect",
            ActorKind = "System",
            ActorLabel = "ShipmentOrchestrationService",
            TenantId = order.TenantID,
            EntityType = "Order",
            EntityRef = $"#{order.WooOrderNumber}",
            Message = $"Skipped Shiplogic booking for Order #{order.WooOrderNumber}: customer chose collection shipping method \"{methodTitle ?? methodId}\".",
            Details = new
            {
                orderId = order.OrderID,
                wooOrderId = order.WooOrderID,
                wooOrderNumber = order.WooOrderNumber,
                shippingMethodId = methodId,
                shippingMethodTitle = methodTitle,
                layer = "CheckCollectionShippingMethodAsync",
            },
        });

        return ShipmentCreationResult.Failure(skip);
    }

    /// <summary>
    /// Load order with all required related data
    /// </summary>
    private async Task<Order?> LoadOrderWithDetailsAsync(int orderId)
    {
        return await _context.Orders.Include(o => o.Store) // Need store for collection address
                                    .Include(o => o.LineItems) // Need line items for weight calculation
                                    .Include(o => o.Shipment) // Check if shipment already exists
                                    .FirstOrDefaultAsync(o => o.OrderID == orderId);
    }

    // Meta keys checked (in priority order) when backfilling a missing
    // delivery slot straight from the WooCommerce order. Covers the DTSC
    // plugin plus generic checkout plugins that write plain order meta
    // (e.g. Crumble GF's `delivery_date` / `delivery_time`).
    private static readonly string[] DeliveryDateMetaKeys = { "_dtsc_delivery_date", "delivery_date", "_delivery_date", "date_picker", "_date_picker" };
    private static readonly string[] DeliveryTimeMetaKeys = { "_dtsc_delivery_time", "delivery_time", "_delivery_time", "time_picker", "_time_picker" };

    /// <summary>
    /// Parse a delivery-date meta value. Tries unambiguous formats first
    /// (ISO yyyy-MM-dd), then explicit DAY-FIRST formats (dd/MM/yyyy —
    /// the format ThemeHigh checkout date pickers emit, e.g. "09/07/2026"
    /// = 9 July), and only then falls back to a generic invariant parse.
    /// Never lets "09/07/2026" be read month-first as September 7.
    /// </summary>
    private static bool TryParseDeliveryDate(string raw, out DateTime date)
    {
        raw = raw.Trim();
        string[] exactFormats =
        {
            "yyyy-MM-dd", "yyyy/MM/dd",
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy"
        };
        if (DateTime.TryParseExact(raw, exactFormats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date))
            return true;

        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out date);
    }

    /// <summary>
    /// Normalise a delivery-time meta value to 24h "HH:mm" when possible
    /// (e.g. "05:00 PM" → "17:00"); returns the raw value untouched if it
    /// isn't a single clock time (ranges like "12:00 - 13:00" pass through
    /// and are handled downstream by the shipment mapper).
    /// </summary>
    private static string NormalizeDeliveryTime(string raw)
    {
        raw = raw.Trim();
        string[] formats = { "hh:mm tt", "h:mm tt", "hh:mmtt", "h:mmtt", "HH:mm", "H:mm" };
        if (DateTime.TryParseExact(raw, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var t))
            return t.ToString("HH:mm");
        return raw;
    }

    /// <summary>
    /// Fallback slot source: product-addon FREE-TEXT fields on line items.
    /// Some stores (Baked By Nataleen) capture the delivery slot as a typed
    /// sentence on the product ("Please indicate date, day and TIME…" →
    /// "Thursday 20th August 11am delivery", "Saturday 15th August Delivery
    /// 9.30am"). Scans every non-internal line-item meta value (and order
    /// meta values) for a "&lt;day&gt;th &lt;month&gt;" date plus an optional clock
    /// time. The year is inferred: the next occurrence of that day/month on
    /// or after the order date (orders are never for the past).
    /// </summary>
    public static bool TryParseFreeTextDeliverySlot(
        WooCommerce.DTOs.WooCommerceOrder wooOrder, DateTime orderDate, out DateTime date, out string? time)
    {
        date = default;
        time = null;

        var texts = new List<string>();
        foreach (var li in wooOrder.LineItems ?? new())
            foreach (var m in li.MetaData ?? new())
                if (!string.IsNullOrWhiteSpace(m.Key) && !m.Key.StartsWith("_") && !string.IsNullOrWhiteSpace(m.StringValue))
                    texts.Add(m.StringValue!);
        foreach (var m in wooOrder.MetaData ?? new())
            if (!string.IsNullOrWhiteSpace(m.Key) && !m.Key.StartsWith("_") && !string.IsNullOrWhiteSpace(m.StringValue))
                texts.Add(m.StringValue!);

        var dateRe = new System.Text.RegularExpressions.Regex(
            @"\b(\d{1,2})\s*(?:st|nd|rd|th)?\s+(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s*(\d{4})?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // "9.30am", "11am", "09:30", "17h00", "4:30 pm"
        var timeRe = new System.Text.RegularExpressions.Regex(
            @"\b(\d{1,2})(?:[.:h](\d{2}))?\s*(am|pm)\b|\b(\d{1,2})[.:h](\d{2})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        string[] months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        foreach (var text in texts)
        {
            var dm = dateRe.Match(text);
            if (!dm.Success)
                continue;

            var day = int.Parse(dm.Groups[1].Value);
            var month = Array.IndexOf(months, dm.Groups[2].Value.ToLowerInvariant()) + 1;
            if (day < 1 || day > 31 || month < 1)
                continue;

            int year;
            if (dm.Groups[3].Success)
            {
                year = int.Parse(dm.Groups[3].Value);
            }
            else
            {
                // Next occurrence on/after the order date (small grace window
                // for same-day orders stamped slightly after midnight UTC).
                year = orderDate.Year;
                if (DateTime.DaysInMonth(year, month) < day) year++;
                if (DateTime.DaysInMonth(year, month) >= day
                    && new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month))) < orderDate.Date.AddDays(-1))
                    year++;
            }
            if (DateTime.DaysInMonth(year, month) < day)
                continue; // e.g. "31st Feb" — not a real date

            date = new DateTime(year, month, day);

            // Time: search the text AFTER the date match first (usual order),
            // fall back to the whole text.
            var tail = text.Substring(dm.Index + dm.Length);
            var tm = timeRe.Match(tail);
            if (!tm.Success) tm = timeRe.Match(text);
            if (tm.Success)
            {
                int hour; int minute = 0;
                if (tm.Groups[1].Success)
                {
                    hour = int.Parse(tm.Groups[1].Value);
                    if (tm.Groups[2].Success) minute = int.Parse(tm.Groups[2].Value);
                    var ampm = tm.Groups[3].Value.ToLowerInvariant();
                    if (ampm == "pm" && hour < 12) hour += 12;
                    if (ampm == "am" && hour == 12) hour = 0;
                }
                else
                {
                    hour = int.Parse(tm.Groups[4].Value);
                    minute = int.Parse(tm.Groups[5].Value);
                }
                if (hour <= 23 && minute <= 59)
                    time = $"{hour:00}:{minute:00}";
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Best-effort: when the order has no RequestedDeliveryDate (the plugin
    /// webhook didn't recognise the merchant checkout's meta keys), fetch
    /// the order from the WooCommerce REST API and pull the delivery slot
    /// from its raw meta. Never throws — booking proceeds with defaults if
    /// the lookup fails.
    /// </summary>
    private async Task TryBackfillRequestedDeliveryFromWooAsync(Order order)
    {
        if (order.RequestedDeliveryDate != null)
            return; // already have a slot — nothing to do

        if (!int.TryParse(order.WooOrderID, out var wooOrderId) || wooOrderId <= 0)
            return;

        if (order.Store == null || string.IsNullOrWhiteSpace(order.Store.WooCommerceURL)
            || string.IsNullOrWhiteSpace(order.Store.WooConsumerKey))
            return; // not a REST-reachable WooCommerce store

        try
        {
            var wooOrder = await _wooCommerceService.GetOrderAsync(order.StoreID, wooOrderId);
            var meta = wooOrder?.MetaData;
            if (meta == null || meta.Count == 0)
                return;

            string? FindMeta(string[] keys) => keys
                .Select(k => meta.FirstOrDefault(m => string.Equals(m.Key, k, StringComparison.OrdinalIgnoreCase))?.StringValue)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var dateRaw = FindMeta(DeliveryDateMetaKeys);
            string? timeRaw = null;
            DateTime parsedDate;

            if (!string.IsNullOrWhiteSpace(dateRaw) && TryParseDeliveryDate(dateRaw, out parsedDate))
            {
                timeRaw = FindMeta(DeliveryTimeMetaKeys);
                if (!string.IsNullOrWhiteSpace(timeRaw))
                    timeRaw = NormalizeDeliveryTime(timeRaw);
            }
            else if (!TryParseFreeTextDeliverySlot(wooOrder, order.OrderDate, out parsedDate, out timeRaw))
            {
                return; // no recognisable slot anywhere on the order
            }

            order.RequestedDeliveryDate = DateTime.SpecifyKind(parsedDate.Date, DateTimeKind.Utc);
            if (!string.IsNullOrWhiteSpace(timeRaw))
                order.RequestedDeliveryTime = timeRaw;

            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Backfilled delivery slot for OrderID {OrderId} from Woo order meta: {Date} {Time}",
                order.OrderID, order.RequestedDeliveryDate, order.RequestedDeliveryTime ?? "(no time)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not backfill delivery slot from WooCommerce for OrderID {OrderId}; proceeding with defaults.",
                order.OrderID);
        }
    }
}

/// <summary>
/// Result of shipment creation attempt
/// </summary>
public class ShipmentCreationResult
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? ShipmentID { get; set; }
    public string? TrackingNumber { get; set; }

    public static ShipmentCreationResult Success(int shipmentId, string trackingNumber)
    {
        return new ShipmentCreationResult
        {
            IsSuccess = true,
            Message = "Shipment created successfully",
            ShipmentID = shipmentId,
            TrackingNumber = trackingNumber
        };
    }

    public static ShipmentCreationResult Failure(string message, int? existingShipmentId = null)
    {
        return new ShipmentCreationResult
        {
            IsSuccess = false,
            Message = message,
            ShipmentID = existingShipmentId
        };
    }
}