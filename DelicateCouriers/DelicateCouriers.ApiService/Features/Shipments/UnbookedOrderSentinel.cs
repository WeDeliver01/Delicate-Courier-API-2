using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Shipments;

/// <summary>
/// Platform-side safety net for the "#50829 completed but never booked"
/// failure mode: an order reaches WooCommerce status "completed" but no
/// shipment was ever created on the platform (missed webhook, Hangfire
/// enqueue blip, plugin outbox stuck, store gating misconfigured, ...).
///
/// Runs as a Hangfire recurring job (see Program.cs). For every order that is
///   * Completed,
///   * NOT a collection order (`fulfillment_type == "collect"` — nothing to book),
///   * has NO shipment row, and
///   * has been in that state for longer than the grace period
///     (so in-flight bookings are not flagged),
/// it emits ONE `order.completed_without_booking` system event (deduplicated
/// against the SystemEvents table) and logs at Error level so the condition
/// is loud in both the admin events feed and the server logs. It deliberately
/// does NOT auto-book: a human must decide whether booking is still wanted —
/// silently booking hours later can be worse than not booking at all.
/// </summary>
public class UnbookedOrderSentinel
{
    public const string EventType = "order.completed_without_booking";

    /// <summary>How long a completed order may sit unbooked before it is flagged.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _context;
    private readonly ISystemEventLogger _systemEvents;
    private readonly ILogger<UnbookedOrderSentinel> _logger;

    public UnbookedOrderSentinel(
        AppDbContext context,
        ISystemEventLogger systemEvents,
        ILogger<UnbookedOrderSentinel> logger)
    {
        _context = context;
        _systemEvents = systemEvents;
        _logger = logger;
    }

    /// <summary>Hangfire entry point.</summary>
    public Task RunAsync() => RunAsync(DateTime.UtcNow);

    /// <summary>Testable overload with an injectable clock.</summary>
    public async Task<int> RunAsync(DateTime nowUtc)
    {
        var cutoff = nowUtc - GracePeriod;

        // Completed + no shipment + not a collection order + older than the
        // grace period. ChangedOn (the completed-status webhook write) is the
        // best available "went completed at" timestamp; fall back to CreatedOn
        // for rows that were created already-completed and never touched.
        var candidates = await _context.Orders
            .AsNoTracking()
            .Where(o => o.OrderStatus == "Completed"
                        && o.Shipment == null
                        && (o.FulfillmentType == null || o.FulfillmentType != "collect")
                        && ((o.ChangedOn ?? o.CreatedOn) <= cutoff))
            .Select(o => new
            {
                o.OrderID,
                o.TenantID,
                o.StoreID,
                o.WooOrderID,
                o.WooOrderNumber,
                o.FulfillmentType,
                o.ChangedOn,
                o.CreatedOn,
            })
            .ToListAsync();

        if (candidates.Count == 0)
        {
            return 0;
        }

        // Dedup: flag each order at most once. EntityRef uses the platform
        // OrderID (unique across stores, unlike WooOrderNumber).
        var refs = candidates.Select(c => $"order:{c.OrderID}").ToList();
        var alreadyFlagged = await _context.SystemEvents
            .AsNoTracking()
            .Where(e => e.EventType == EventType && e.EntityRef != null && refs.Contains(e.EntityRef))
            .Select(e => e.EntityRef!)
            .ToListAsync();
        var flaggedSet = alreadyFlagged.ToHashSet(StringComparer.Ordinal);

        var flagged = 0;
        foreach (var order in candidates)
        {
            var entityRef = $"order:{order.OrderID}";
            if (flaggedSet.Contains(entityRef))
            {
                continue;
            }

            var sinceUtc = order.ChangedOn ?? order.CreatedOn;
            _logger.LogError(
                "SENTINEL: Order {OrderId} (Woo #{WooOrderNumber}, store {StoreId}) is COMPLETED but has NO shipment booked since {SinceUtc:u}. Review and book manually.",
                order.OrderID, order.WooOrderNumber, order.StoreID, sinceUtc);

            await _systemEvents.LogAsync(new SystemEventEntry
            {
                EventType = EventType,
                ActorKind = "System",
                ActorLabel = nameof(UnbookedOrderSentinel),
                Source = "sentinel",
                TenantId = order.TenantID,
                EntityType = "Order",
                EntityRef = entityRef,
                Message = $"Order #{order.WooOrderNumber} is completed on the store but no shipment has been booked (unbooked for {(int)(nowUtc - sinceUtc).TotalMinutes} min). Review and book manually.",
                Details = new
                {
                    orderId = order.OrderID,
                    wooOrderId = order.WooOrderID,
                    wooOrderNumber = order.WooOrderNumber,
                    storeId = order.StoreID,
                    fulfillmentType = order.FulfillmentType,
                    completedSinceUtc = sinceUtc,
                },
            });
            flagged++;
        }

        if (flagged > 0)
        {
            _logger.LogWarning("SENTINEL: flagged {Count} completed-but-unbooked order(s).", flagged);
        }

        return flagged;
    }
}
