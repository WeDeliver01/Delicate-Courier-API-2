using DelicateCouriers.ApiService.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.Admin;

/// <summary>
/// Read-only access to the SystemEvent log. SuperAdmin-only — the log spans
/// every tenant so it must not leak across tenant boundaries.
/// </summary>
[ApiController]
[Route("api/admin/system-events")]
[Authorize(Roles = "SuperAdmin")]
public class SystemEventsController : ControllerBase
{
    private readonly AppDbContext _context;

    public SystemEventsController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// List system events, newest first. Supports date range, event type,
    /// tenant, actor and free-text filters plus simple page/pageSize
    /// pagination. Page size is capped at 200.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<SystemEventListResponse>> GetEvents(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? eventType,
        [FromQuery] int? tenantId,
        [FromQuery] string? actorKind,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 50;
        if (pageSize > 200) pageSize = 200;

        // SystemEvents has no global tenant filter so this query naturally
        // sees every tenant — exactly what a SuperAdmin needs.
        var q = _context.SystemEvents.AsNoTracking().AsQueryable();

        if (from.HasValue) q = q.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue) q = q.Where(e => e.OccurredAt <= to.Value);
        if (!string.IsNullOrWhiteSpace(eventType)) q = q.Where(e => e.EventType == eventType);
        if (tenantId.HasValue) q = q.Where(e => e.TenantID == tenantId.Value);
        if (!string.IsNullOrWhiteSpace(actorKind)) q = q.Where(e => e.ActorKind == actorKind);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            // EF.Functions.ILike is Postgres-specific case-insensitive search.
            q = q.Where(e =>
                EF.Functions.ILike(e.Message, $"%{s}%") ||
                (e.EntityRef != null && EF.Functions.ILike(e.EntityRef, $"%{s}%")) ||
                (e.ActorLabel != null && EF.Functions.ILike(e.ActorLabel, $"%{s}%")));
        }

        var total = await q.LongCountAsync(ct);

        var rows = await q
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.SystemEventID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(e => new SystemEventDto
        {
            SystemEventId = e.SystemEventID,
            OccurredAt = DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc),
            EventType = e.EventType,
            TenantId = e.TenantID,
            ActorUserId = e.ActorUserID,
            ActorKind = e.ActorKind,
            ActorLabel = e.ActorLabel,
            EntityType = e.EntityType,
            EntityRef = e.EntityRef,
            Message = e.Message,
            Details = ParseDetails(e.DetailsJson),
        }).ToList();

        return Ok(new SystemEventListResponse
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// Distinct event-type list, useful for populating a filter dropdown
    /// in the UI without hard-coding the catalogue.
    /// </summary>
    [HttpGet("event-types")]
    public async Task<ActionResult<List<string>>> GetEventTypes(CancellationToken ct)
    {
        var types = await _context.SystemEvents
            .AsNoTracking()
            .Select(e => e.EventType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);
        return Ok(types);
    }

    private static JsonElement? ParseDetails(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }
}

public class SystemEventDto
{
    public long SystemEventId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int? TenantId { get; set; }
    public int? ActorUserId { get; set; }
    public string ActorKind { get; set; } = string.Empty;
    public string? ActorLabel { get; set; }
    public string? EntityType { get; set; }
    public string? EntityRef { get; set; }
    public string Message { get; set; } = string.Empty;
    public JsonElement? Details { get; set; }
}

public class SystemEventListResponse
{
    public List<SystemEventDto> Items { get; set; } = new();
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
