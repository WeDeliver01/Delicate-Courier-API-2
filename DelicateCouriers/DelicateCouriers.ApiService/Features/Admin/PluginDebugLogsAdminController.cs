using DelicateCouriers.ApiService.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DelicateCouriers.ApiService.Features.Admin;

/// <summary>
/// SuperAdmin-only read API for the platform-side mirror of plugin debug
/// logs. Backs the /super-admin/debug-log page.
///
/// Filters: store, tenant, level, context substring, source, date range,
/// free-text search across detail. Page size capped at 200.
/// </summary>
[ApiController]
[Route("api/admin/plugin-debug-logs")]
[Authorize(Roles = "SuperAdmin")]
public class PluginDebugLogsAdminController : ControllerBase
{
    private readonly AppDbContext _context;

    public PluginDebugLogsAdminController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<PluginDebugLogListResponse>> GetLogs(
        [FromQuery] int? storeId,
        [FromQuery] int? tenantId,
        [FromQuery] string? level,
        [FromQuery] string? context,
        [FromQuery] string? source,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 100;
        if (pageSize > 200) pageSize = 200;

        var q = _context.PluginDebugLogs.IgnoreQueryFilters().AsNoTracking().AsQueryable();
        if (storeId.HasValue) q = q.Where(x => x.StoreID == storeId.Value);
        if (tenantId.HasValue) q = q.Where(x => x.TenantID == tenantId.Value);
        if (!string.IsNullOrWhiteSpace(level)) q = q.Where(x => x.Level == level);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(x => x.Source == source);
        if (!string.IsNullOrWhiteSpace(context))
        {
            var c = context.Trim();
            q = q.Where(x => EF.Functions.ILike(x.Context, $"%{c}%"));
        }
        if (from.HasValue) q = q.Where(x => x.OccurredAt >= from.Value);
        if (to.HasValue) q = q.Where(x => x.OccurredAt <= to.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(x =>
                EF.Functions.ILike(x.Detail, $"%{s}%") ||
                EF.Functions.ILike(x.Context, $"%{s}%") ||
                (x.DataJson != null && EF.Functions.ILike(x.DataJson, $"%{s}%")));
        }

        var total = await q.LongCountAsync(ct);

        var rows = await q
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.PluginDebugLogID)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows.Select(x => new PluginDebugLogDto
        {
            PluginDebugLogId = x.PluginDebugLogID,
            StoreId = x.StoreID,
            TenantId = x.TenantID,
            OccurredAt = DateTime.SpecifyKind(x.OccurredAt, DateTimeKind.Utc),
            ReceivedAt = DateTime.SpecifyKind(x.ReceivedAt, DateTimeKind.Utc),
            Level = x.Level,
            Context = x.Context,
            Source = x.Source,
            Detail = x.Detail,
            PluginVersion = x.PluginVersion,
            Data = ParseJson(x.DataJson),
        }).ToList();

        return Ok(new PluginDebugLogListResponse
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>Distinct context tags currently in the log, for the filter dropdown.</summary>
    [HttpGet("contexts")]
    public async Task<ActionResult<List<string>>> GetContexts(CancellationToken ct)
    {
        var list = await _context.PluginDebugLogs.IgnoreQueryFilters()
            .AsNoTracking()
            .Select(x => x.Context)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);
        return Ok(list);
    }

    /// <summary>Distinct stores that have shipped any log entries (for the store-picker dropdown).</summary>
    [HttpGet("stores")]
    public async Task<ActionResult<List<PluginDebugLogStoreDto>>> GetStores(CancellationToken ct)
    {
        var rows = await (
            from log in _context.PluginDebugLogs.IgnoreQueryFilters().AsNoTracking()
            join store in _context.Stores.IgnoreQueryFilters().AsNoTracking()
                on log.StoreID equals store.StoreID
            group store by new { store.StoreID, store.TenantID, store.StoreName } into g
            select new PluginDebugLogStoreDto
            {
                StoreId = g.Key.StoreID,
                TenantId = g.Key.TenantID,
                StoreName = g.Key.StoreName,
                EntryCount = g.Count(),
            }).ToListAsync(ct);
        return Ok(rows.OrderBy(s => s.StoreName).ToList());
    }

    [HttpDelete("store/{storeId:int}")]
    public async Task<IActionResult> ClearStoreLogs(int storeId, CancellationToken ct)
    {
        var deleted = await _context.PluginDebugLogs
            .IgnoreQueryFilters()
            .Where(x => x.StoreID == storeId)
            .ExecuteDeleteAsync(ct);
        return Ok(new { deleted });
    }

    private static JsonElement? ParseJson(string? json)
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

public class PluginDebugLogDto
{
    public long PluginDebugLogId { get; set; }
    public int StoreId { get; set; }
    public int TenantId { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Level { get; set; } = string.Empty;
    public string Context { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string? PluginVersion { get; set; }
    public JsonElement? Data { get; set; }
}

public class PluginDebugLogListResponse
{
    public List<PluginDebugLogDto> Items { get; set; } = new();
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class PluginDebugLogStoreDto
{
    public int StoreId { get; set; }
    public int TenantId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public int EntryCount { get; set; }
}
