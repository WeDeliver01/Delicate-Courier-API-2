namespace DelicateCouriers.Domain.Entities;

/// <summary>
/// One row per debug-log entry shipped from the WooCommerce plugin.
///
/// The plugin already keeps a 200-entry ring buffer in WP's options
/// table for its own admin "Debug log" panel (visible to the merchant
/// on their WordPress dashboard). This entity is the platform-side
/// mirror — every entry the plugin generates is also fire-and-forget
/// POSTed to the platform so SuperAdmins can see exactly what every
/// merchant's plugin is doing without having to log into 30 different
/// WordPress sites.
///
/// Retention: rolling cap of 10,000 entries per store. The ingest
/// endpoint runs a probabilistic prune (1% of inserts) that deletes
/// oldest excess rows for the store the new entry belongs to.
///
/// Visibility: SuperAdmin only. The log can contain PII (customer
/// addresses, emails, signed-request bodies) because the merchant's
/// own debug panel does — we deliberately do not redact, because the
/// whole point of the log is to reproduce/diagnose merchant-side
/// issues end-to-end. The read endpoint is gated by
/// [Authorize(Roles = "SuperAdmin")] and the ingest endpoint is
/// HMAC-signed with the same per-store secret as order webhooks.
/// </summary>
public class PluginDebugLog
{
    public long PluginDebugLogID { get; set; }

    /// <summary>Store the log entry came from. Indexed for per-store queries.</summary>
    public int StoreID { get; set; }

    /// <summary>Tenant that owns the store, denormalised so SuperAdmin filters work without a join.</summary>
    public int TenantID { get; set; }

    /// <summary>
    /// When the event happened on the merchant's WordPress site (their wall-clock,
    /// since `dcp_log` stamps it from `current_time('mysql')`). Stored as UTC if
    /// the plugin sent a Z-suffixed value, otherwise stored as-sent.
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>When the platform received the entry. UTC.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Severity. One of: info, warning, error, debug.</summary>
    public string Level { get; set; } = "info";

    /// <summary>
    /// Short machine-readable context tag from the plugin, e.g.
    /// "platform.order", "platform.sign", "rates.added",
    /// "browser.checkout.fulfillment_toggle", "browser.console.error".
    /// </summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>
    /// Where the entry originated:
    ///   "server"   — server-side dcp_log()/dcp_log_http() call in PHP.
    ///   "browser"  — JS console events captured on the customer-facing page.
    ///   "checkout" — JS event captured on the WooCommerce checkout page
    ///                (shipping method toggles, fulfillment changes, etc).
    /// </summary>
    public string Source { get; set; } = "server";

    /// <summary>Human-readable one-line detail.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>
    /// Optional JSON blob with the full HTTP request/response or browser
    /// event data. Same shape the plugin's `dcp_log_sanitize_data` produces.
    /// Stored verbatim — no further redaction.
    /// </summary>
    public string? DataJson { get; set; }

    /// <summary>Plugin version reported by the sender. Useful for cross-merchant debugging.</summary>
    public string? PluginVersion { get; set; }
}
