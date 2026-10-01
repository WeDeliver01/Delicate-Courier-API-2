using DelicateCouriers.ApiService.Data;
using DelicateCouriers.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Infrastructure.Services;

/// <summary>
/// Append a row to the SystemEvent log. Implementations must NEVER throw —
/// observability is best-effort and must not break the caller's main flow.
/// Internally swallows and logs any persistence failures.
/// </summary>
public interface ISystemEventLogger
{
    Task LogAsync(SystemEventEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Plain-old data describing a single system event. Built by the caller
/// and handed to the logger.
/// </summary>
public class SystemEventEntry
{
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public int? TenantId { get; init; }
    public int? ActorUserId { get; init; }
    public string ActorKind { get; init; } = "System";
    public string? ActorLabel { get; init; }
    public string? EntityType { get; init; }
    public string? EntityRef { get; init; }
    public object? Details { get; init; }

    /// <summary>
    /// Short tag identifying the subsystem that produced the event
    /// (e.g. "daa", "shiplogic", "woocommerce"). Optional — used by
    /// the SuperAdmin events page filter and by idempotency keys
    /// (paired with RequestId).
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Optional idempotency key. When set, the database has a partial
    /// unique index on this column so a concurrent retry that races past
    /// the controller-level "have we seen this" check will be caught by
    /// Postgres with a 23505 instead of double-inserting.
    /// </summary>
    public Guid? RequestId { get; init; }
}

/// <summary>
/// Fire-and-forget SystemEvent persister. Uses its own scoped DbContext per
/// call so it's safe to invoke from anywhere (controllers, background jobs,
/// Hangfire filters) regardless of whether the caller is mid-transaction.
/// Failures are logged but swallowed — the log is operational metadata, not
/// part of the business flow.
/// </summary>
public class SystemEventLogger : ISystemEventLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemEventLogger> _logger;

    private static readonly JsonSerializerOptions DetailsJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public SystemEventLogger(IServiceScopeFactory scopeFactory, ILogger<SystemEventLogger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogAsync(SystemEventEntry entry, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var row = new SystemEvent
            {
                OccurredAt = DateTime.UtcNow,
                EventType = entry.EventType,
                TenantID = entry.TenantId,
                ActorUserID = entry.ActorUserId,
                ActorKind = entry.ActorKind,
                ActorLabel = Truncate(entry.ActorLabel, 200),
                EntityType = Truncate(entry.EntityType, 50),
                EntityRef = Truncate(entry.EntityRef, 200),
                Message = Truncate(entry.Message, 1000) ?? string.Empty,
                DetailsJson = entry.Details is null
                    ? null
                    : JsonSerializer.Serialize(entry.Details, DetailsJsonOptions),
                Source = Truncate(entry.Source, 50),
                RequestId = entry.RequestId,
            };

            db.SystemEvents.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Observability must never break the caller. Log and move on.
            _logger.LogWarning(ex,
                "Failed to persist SystemEvent {EventType} for {Entity} {Ref}",
                entry.EventType, entry.EntityType, entry.EntityRef);
        }
    }

    private static string? Truncate(string? s, int max)
    {
        if (s == null) return null;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
