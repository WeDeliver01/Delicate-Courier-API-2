using System.Text;
using System.Text.Json;
using DelicateCouriers.ApiService.Data;
using DelicateCouriers.ApiService.Infrastructure.Services;
using DelicateCouriers.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace DelicateCouriers.ApiService.Features.Admin;

/// <summary>
/// Ingest endpoint for the WooCommerce plugin's debug log entries.
///
/// The plugin already keeps a 200-entry ring buffer in WP's `options`
/// table for its own admin panel. Every entry is ALSO fire-and-forget
/// POSTed here so SuperAdmins can see all merchants' debug streams in
/// one place inside the platform (under /super-admin/debug-log).
///
/// Auth: same HMAC-SHA256 scheme as the order webhook —
///   X-Plugin-Signature: base64(HMAC-SHA256(rawBody, store.WebhookSecret))
///   X-Store-ID:         numeric store id (also acceptable inside the body).
///
/// Rate limited via the existing "WebhookIp" policy so a misbehaving
/// plugin can't flood the platform. The endpoint is intentionally lenient:
/// it returns 202 Accepted on success and returns 200 for soft-failures
/// (e.g. unknown store) so the plugin never retries — a debug log is not
/// worth jamming the queue over.
///
/// Retention: rolling cap of 10,000 entries per store, enforced by a
/// probabilistic prune that runs on roughly 1% of inserts. This keeps
/// the table bounded without hammering it on every write.
/// </summary>
[ApiController]
[Route("api/webhooks/plugin/debug-log")]
[EnableRateLimiting("WebhookIp")]
public class PluginDebugLogIngestController : ControllerBase
{
    private const int RetentionPerStore = 10_000;
    private const int PruneCheckPercent = 1; // 1% of inserts run the prune

    private readonly AppDbContext _context;
    private readonly WebhookSignatureValidator _signatureValidator;
    private readonly ILogger<PluginDebugLogIngestController> _logger;
    private static readonly Random _rng = new();

    public PluginDebugLogIngestController(
        AppDbContext context,
        WebhookSignatureValidator signatureValidator,
        ILogger<PluginDebugLogIngestController> logger)
    {
        _context = context;
        _signatureValidator = signatureValidator;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Ingest(CancellationToken ct)
    {
        // Read raw body BEFORE model binding so HMAC sees the exact bytes the
        // plugin signed. Identical pattern to PluginWebhookController.
        string rawBody;
        Request.EnableBuffering();
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            rawBody = await reader.ReadToEndAsync(ct);
            Request.Body.Position = 0;
        }

        if (string.IsNullOrEmpty(rawBody))
        {
            return BadRequest(new { error = "Empty payload" });
        }

        PluginDebugLogIngestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PluginDebugLogIngestPayload>(rawBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Plugin debug log rejected: invalid JSON");
            return BadRequest(new { error = "Invalid JSON" });
        }

        if (payload == null)
        {
            return BadRequest(new { error = "Empty payload" });
        }

        var storeIdHeader = Request.Headers["X-Store-ID"].ToString();
        var storeIdRaw = !string.IsNullOrEmpty(storeIdHeader) ? storeIdHeader : payload.StoreId;
        if (string.IsNullOrEmpty(storeIdRaw) || !int.TryParse(storeIdRaw, out var storeId))
        {
            return BadRequest(new { error = "Missing or invalid Store ID" });
        }

        // Webhooks are unauthenticated → bypass tenant filter.
        var store = await _context.Stores
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.StoreID == storeId && s.IsActive, ct);

        if (store == null)
        {
            _logger.LogWarning("Plugin debug log rejected: store {StoreId} unknown", storeId);
            // 200 (not 404) so the plugin treats it as "processed" and doesn't retry
            // — there's nothing the plugin can do about a missing store.
            return Ok(new { accepted = false, reason = "unknown_store" });
        }

        if (string.IsNullOrEmpty(store.WebhookSecret))
        {
            _logger.LogError(
                "Plugin debug log rejected: store {StoreId} has no WebhookSecret", storeId);
            return Ok(new { accepted = false, reason = "no_secret_configured" });
        }

        var signature = Request.Headers["X-Plugin-Signature"].ToString();
        if (string.IsNullOrEmpty(signature) ||
            !_signatureValidator.ValidateSignature(rawBody, signature, store.WebhookSecret))
        {
            _logger.LogWarning("Plugin debug log rejected: bad signature for store {StoreId}", storeId);
            return Unauthorized(new { error = "Invalid signature" });
        }

        // ----- Persist (or skip if obviously empty) -------------------------
        var entries = payload.Entries ?? new List<PluginDebugLogEntryDto>();
        // Single-entry mode: plugin sends one entry per POST in real-time mode,
        // but for forward-compat the payload also supports a batch under
        // `entries`. If neither shape is present treat as no-op.
        if (entries.Count == 0 && !string.IsNullOrEmpty(payload.Context))
        {
            entries.Add(new PluginDebugLogEntryDto
            {
                Time = payload.Time,
                Level = payload.Level,
                Context = payload.Context,
                Detail = payload.Detail,
                Source = payload.Source,
                Data = payload.Data,
            });
        }

        if (entries.Count == 0)
        {
            return Ok(new { accepted = true, written = 0 });
        }

        var nowUtc = DateTime.UtcNow;
        var rows = new List<PluginDebugLog>(entries.Count);
        // Prefer the plugin's UTC timestamp (`time_utc`, ISO8601 with offset)
        // when present — that's unambiguous across merchant timezones. Fall
        // back to `time` only for older plugin builds; if THAT is also a
        // bare WP local-time string we have no offset to apply, so just use
        // server's receive time as a safer default than silently mis-tagging.
        foreach (var e in entries)
        {
            rows.Add(new PluginDebugLog
            {
                StoreID = store.StoreID,
                TenantID = store.TenantID,
                OccurredAt = ParseOccurredAt(payload.TimeUtc ?? e.TimeUtc, e.Time, nowUtc),
                ReceivedAt = nowUtc,
                Level = NormalizeLevel(e.Level),
                Context = Truncate(e.Context ?? "uncategorised", 100),
                Source = NormalizeSource(e.Source),
                Detail = e.Detail ?? string.Empty,
                DataJson = e.Data?.GetRawText(),
                PluginVersion = Truncate(payload.PluginVersion, 20),
            });
        }

        _context.PluginDebugLogs.AddRange(rows);
        await _context.SaveChangesAsync(ct);

        // ----- Rolling cap: probabilistic prune -----------------------------
        // Running this on every insert would be expensive; running it never
        // would let pathological stores grow unbounded. 1% sample rate means
        // for a store doing 100 entries/min we prune ~once/min, which is
        // plenty for an upper bound of 10k.
        try
        {
            if (_rng.Next(100) < PruneCheckPercent)
            {
                await PruneStoreAsync(store.StoreID, ct);
            }
        }
        catch (Exception ex)
        {
            // Pruning failure must not break ingest. The next prune attempt
            // will catch up.
            _logger.LogWarning(ex, "Plugin debug log prune failed for store {StoreId}", store.StoreID);
        }

        return Accepted(new { accepted = true, written = rows.Count });
    }

    private async Task PruneStoreAsync(int storeId, CancellationToken ct)
    {
        var count = await _context.PluginDebugLogs
            .Where(x => x.StoreID == storeId)
            .LongCountAsync(ct);
        if (count <= RetentionPerStore) return;

        var excess = (int)(count - RetentionPerStore);
        // Find the cut-off ID — anything with a smaller PK is older and gets dropped.
        // Order by PK desc, skip retention, take 1, then delete <= that ID.
        var cutoff = await _context.PluginDebugLogs
            .Where(x => x.StoreID == storeId)
            .OrderByDescending(x => x.PluginDebugLogID)
            .Skip(RetentionPerStore)
            .Select(x => x.PluginDebugLogID)
            .FirstOrDefaultAsync(ct);
        if (cutoff == 0) return;

        var deleted = await _context.PluginDebugLogs
            .Where(x => x.StoreID == storeId && x.PluginDebugLogID <= cutoff)
            .ExecuteDeleteAsync(ct);
        _logger.LogInformation(
            "Plugin debug log prune: deleted {Deleted} rows for store {StoreId} (was {Was}, retention {Retention})",
            deleted, storeId, count, RetentionPerStore);
    }

    /// <summary>
    /// Resolve OccurredAt with strict UTC semantics.
    ///   1. If <paramref name="utcIso"/> parses as an offset-aware ISO8601
    ///      timestamp, use it (correctly normalised to UTC).
    ///   2. Otherwise fall back to <paramref name="fallbackUtc"/> (server
    ///      receive time). We deliberately do NOT parse <paramref name="local"/>
    ///      as UTC because the plugin sends it from `current_time('mysql')`,
    ///      which is the WP site's local wall clock with no timezone — treating
    ///      that as UTC would skew chronology by the merchant's offset.
    /// </summary>
    private static DateTime ParseOccurredAt(string? utcIso, string? local, DateTime fallbackUtc)
    {
        if (!string.IsNullOrWhiteSpace(utcIso) &&
            DateTime.TryParse(utcIso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var utc))
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        }
        _ = local; // intentionally unused; see XML doc
        return fallbackUtc;
    }

    private static string NormalizeLevel(string? level)
    {
        var l = (level ?? "info").Trim().ToLowerInvariant();
        return l switch
        {
            "info" or "warning" or "error" or "debug" => l,
            "warn" => "warning",
            "err" => "error",
            _ => "info",
        };
    }

    private static string NormalizeSource(string? source)
    {
        var s = (source ?? "server").Trim().ToLowerInvariant();
        return s switch
        {
            "server" or "browser" or "checkout" => s,
            _ => "server",
        };
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}

/// <summary>
/// Wire format from the plugin. Supports both single-entry (real-time mode)
/// and batched (future shutdown-mode) shapes. The plugin currently sends
/// single entries.
/// </summary>
public class PluginDebugLogIngestPayload
{
    public string? StoreId { get; set; }
    public string? PluginVersion { get; set; }

    // ----- Single-entry mode -----
    public string? Time { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("time_utc")]
    public string? TimeUtc { get; set; }
    public string? Level { get; set; }
    public string? Context { get; set; }
    public string? Detail { get; set; }
    public string? Source { get; set; }
    public JsonElement? Data { get; set; }

    // ----- Batch mode (forward-compat) -----
    public List<PluginDebugLogEntryDto>? Entries { get; set; }
}

public class PluginDebugLogEntryDto
{
    public string? Time { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("time_utc")]
    public string? TimeUtc { get; set; }
    public string? Level { get; set; }
    public string? Context { get; set; }
    public string? Detail { get; set; }
    public string? Source { get; set; }
    public JsonElement? Data { get; set; }
}
