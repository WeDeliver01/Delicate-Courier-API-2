using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Features.Shipments;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.WooCommerce
{
    /// <summary>
    /// Platform-side safety net for stores whose installed plugin cannot be
    /// updated (see order #50829): older plugin builds sync each order ONCE
    /// and never re-send status changes, so a store with an explicit
    /// <see cref="DelicateCouriers.Domain.Entities.Store.BookingTriggerStatuses"/>
    /// list (e.g. "completed") never learns the order reached its trigger
    /// status and never books the shipment.
    ///
    /// Runs as a Hangfire recurring job. For every unshipped, non-terminal
    /// WooCommerce order belonging to a store WITH a trigger-status override,
    /// it fetches the current status from the store's Woo REST API, persists
    /// any change, and enqueues the (idempotent) booking job once the status
    /// passes <see cref="BookingStatusGate"/>. Stores without an override are
    /// untouched — they book at first sync, so there is nothing to reconcile.
    /// </summary>
    public class WooStatusReconciliationService
    {
        private const int LookbackDays = 7;
        private const int MaxOrdersPerRun = 50;

        /// <summary>
        /// Orders created before this date are NEVER reconciled. Without
        /// this floor the first sweep would have picked up ~80 historical
        /// unshipped orders (many already fulfilled manually) and booked
        /// real courier shipments for them. Only orders created after the
        /// feature shipped are eligible.
        /// </summary>
        private static readonly DateTime MinCreatedOnUtc = new(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc);

        private static readonly string[] TerminalStatuses = { "Cancelled", "Canceled", "Refunded", "Failed", "Trash" };

        private readonly AppDbContext _context;
        private readonly IWooCommerceService _wooCommerceService;
        private readonly IBackgroundJobClient _backgroundJobClient;
        private readonly ILogger<WooStatusReconciliationService> _logger;

        public WooStatusReconciliationService(
            AppDbContext context,
            IWooCommerceService wooCommerceService,
            IBackgroundJobClient backgroundJobClient,
            ILogger<WooStatusReconciliationService> logger)
        {
            _context = context;
            _wooCommerceService = wooCommerceService;
            _backgroundJobClient = backgroundJobClient;
            _logger = logger;
        }

        [AutomaticRetry(Attempts = 0)] // recurring — next run is the retry
        [DisableConcurrentExecution(timeoutInSeconds: 60)] // never overlap two sweeps
        public async Task RunAsync()
        {
            var started = DateTime.UtcNow;
            var rolling = DateTime.UtcNow.AddDays(-LookbackDays);
            var cutoff = rolling > MinCreatedOnUtc ? rolling : MinCreatedOnUtc;

            var candidates = await _context.Orders
                .IgnoreQueryFilters()
                .Include(o => o.Store)
                .Include(o => o.Shipment)
                .Where(o => o.Shipment == null
                            && o.Store.IsActive
                            && o.Store.Platform == "woocommerce"
                            && o.Store.BookingTriggerStatuses != null
                            && o.Store.BookingTriggerStatuses != ""
                            && !TerminalStatuses.Contains(o.OrderStatus)
                            && o.CreatedOn >= cutoff)
                .OrderByDescending(o => o.CreatedOn) // newest first — they are the likeliest to need booking, so a backlog can't starve them
                .Take(MaxOrdersPerRun)
                .ToListAsync();

            if (candidates.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Woo status reconciliation: checking {Count} unshipped order(s) across trigger-status stores.", candidates.Count);

            foreach (var order in candidates)
            {
                // Hard time budget: a slow/dead merchant site must not let a
                // single sweep occupy a Hangfire worker indefinitely. Anything
                // left over is picked up by the next run (newest-first order).
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(4))
                {
                    _logger.LogWarning("Woo status reconciliation: time budget exhausted — deferring remaining orders to the next run.");
                    break;
                }

                try
                {
                    await ReconcileOrderAsync(order);
                }
                catch (Exception ex)
                {
                    // One store being down must not block the rest of the sweep,
                    // and a failed save must not keep re-flushing this entity on
                    // later orders' SaveChangesAsync calls — detach it.
                    _logger.LogError(ex, "Woo status reconciliation failed for Order {OrderId} (Woo #{WooNumber}, Store {StoreId}).",
                        order.OrderID, order.WooOrderNumber, order.StoreID);
                    _context.Entry(order).State = EntityState.Detached;
                }
            }
        }

        private async Task ReconcileOrderAsync(Domain.Entities.Order order)
        {
            if (!int.TryParse(order.WooOrderID, out var wooOrderId))
            {
                _logger.LogWarning("Order {OrderId} has non-numeric WooOrderID '{WooOrderId}' — skipping reconciliation.",
                    order.OrderID, order.WooOrderID);
                return;
            }

            var wooOrder = await _wooCommerceService.GetOrderAsync(order.StoreID, wooOrderId);
            if (wooOrder == null)
            {
                _logger.LogWarning("Woo status reconciliation: could not fetch Woo order {WooOrderId} for Store {StoreId} (Order {OrderId}).",
                    wooOrderId, order.StoreID, order.OrderID);
                return;
            }

            var normalised = wooOrder.Status?.Trim().ToLowerInvariant();
            var mapped = MapWooStatus(normalised);

            if (!string.Equals(order.OrderStatus, mapped, StringComparison.Ordinal))
            {
                _logger.LogInformation("Woo status reconciliation: Order {OrderId} (Woo #{WooNumber}) status {Old} -> {New} (woo '{Raw}').",
                    order.OrderID, order.WooOrderNumber, order.OrderStatus, mapped, wooOrder.Status);
                order.OrderStatus = mapped;
                order.ChangedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            // Never book collect orders from the sweep; the orchestration
            // job re-verifies this (plus the collection-keyword and
            // special-trip guards) before any Shiplogic call.
            if (string.Equals(order.FulfillmentType, "collect", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (TerminalStatuses.Contains(mapped))
            {
                return;
            }

            if (!BookingStatusGate.IsAllowed(order.Store, normalised, out _))
            {
                return; // not at a trigger status yet — check again next run
            }

            _logger.LogInformation("Woo status reconciliation: Order {OrderId} (Woo #{WooNumber}, Store {StoreId}) reached trigger status '{Status}' with no shipment — enqueueing booking.",
                order.OrderID, order.WooOrderNumber, order.StoreID, wooOrder.Status);

            _backgroundJobClient.Enqueue<ShipmentOrchestrationService>(s => s.CreateShipmentForOrderAsync(order.OrderID));
        }

        private static string MapWooStatus(string? normalised)
        {
            return normalised switch
            {
                "pending" => "Pending",
                "on-hold" => "Pending",
                "processing" => "Processing",
                "completed" => "Completed",
                "cancelled" => "Cancelled",
                "refunded" => "Cancelled",
                "failed" => "Cancelled",
                "trash" => "Cancelled",
                _ => "Pending"
            };
        }
    }
}
