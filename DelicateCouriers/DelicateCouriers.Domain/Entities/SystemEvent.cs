namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// A timestamped record of a significant platform activity (order received,
/// shipment booked, tracking event ingested, user provisioned, webhook
/// received, background-job failures, etc).
///
/// Read by the SuperAdmin "System Events" page so a single operator can
/// reconcile anything that looks off across all tenants. The log is
/// append-only and starts empty from the deploy that introduces it —
/// historical events are not backfilled.
/// </summary>
public class SystemEvent
{
    public long SystemEventID { get; set; }

    /// <summary>
    /// UTC timestamp when the event occurred. Indexed for chronological queries.
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Short machine-readable event type, e.g. "order.received",
    /// "shipment.booked", "shipment.book_failed", "tracking.event_ingested",
    /// "user.created", "user.role_changed", "user.status_changed",
    /// "auth.login", "webhook.received", "job.failed".
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Tenant the event belongs to. Null for cross-tenant / system events
    /// (e.g. background job failures before tenant context is known).
    /// </summary>
    public int? TenantID { get; set; }

    /// <summary>
    /// Local user that triggered the action, when applicable.
    /// </summary>
    public int? ActorUserID { get; set; }

    /// <summary>
    /// One of: User, System, Webhook, Job.
    /// </summary>
    public string ActorKind { get; set; } = "System";

    /// <summary>
    /// Display label for the actor (email, "Shiplogic webhook", "Hangfire", etc).
    /// </summary>
    public string? ActorLabel { get; set; }

    /// <summary>
    /// Target entity type, e.g. "Order", "Shipment", "TrackingEvent", "User".
    /// </summary>
    public string? EntityType { get; set; }

    /// <summary>
    /// Human-readable reference for the entity (Order #11980, Tracking XJ347V, etc).
    /// </summary>
    public string? EntityRef { get; set; }

    /// <summary>
    /// Short human-readable message summarising what happened.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON blob with extra context for the row's details pane.
    /// </summary>
    public string? DetailsJson { get; set; }

    /// <summary>
    /// Namespacing tag for the event's origin subsystem, e.g. "daa", "core",
    /// "shiplogic", "woocommerce". Lets the SuperAdmin UI filter to just the
    /// DAA-monitor stream without string-matching prefixes on EventType.
    /// Null for legacy rows written before the column was introduced.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Idempotency key for events carried into the platform from an outside
    /// system that may retry (currently the DAA push channel). When set, a
    /// duplicate ingest with the same RequestId is silently dropped without
    /// inserting a second row. Null for events generated inside the platform.
    /// Indexed unique-where-not-null in Postgres.
    /// </summary>
    public Guid? RequestId { get; set; }
}
