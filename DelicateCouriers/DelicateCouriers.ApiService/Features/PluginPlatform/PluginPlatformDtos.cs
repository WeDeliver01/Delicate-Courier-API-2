using System.Text.Json.Serialization;

namespace DelicateCouriers.ApiService.Features.PluginPlatform;

/// <summary>
/// Wire DTOs for the plugin-facing endpoints. snake_case via explicit
/// JsonPropertyName — same convention as the existing order-push webhook.
/// </summary>

public class PluginRequestBase
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = string.Empty;
    [JsonPropertyName("install_key")] public string InstallKey { get; set; } = string.Empty;
    [JsonPropertyName("domain")] public string Domain { get; set; } = string.Empty;
    // Replay protection (optional for legacy clients; enforced together).
    [JsonPropertyName("timestamp")] public long? Timestamp { get; set; }
    [JsonPropertyName("nonce")] public string? Nonce { get; set; }
}

public class PluginCheckUpdateRequest : PluginRequestBase
{
    [JsonPropertyName("plugin_version")] public string? PluginVersion { get; set; }
    [JsonPropertyName("wp_version")] public string? WpVersion { get; set; }
    [JsonPropertyName("wc_version")] public string? WcVersion { get; set; }
    [JsonPropertyName("php_version")] public string? PhpVersion { get; set; }
}

public class PluginActivateRequest : PluginCheckUpdateRequest
{
}

public class PluginHeartbeatRequest : PluginCheckUpdateRequest
{
    [JsonPropertyName("api_latency_ms")] public int? ApiLatencyMs { get; set; }
    [JsonPropertyName("recent_errors")] public System.Text.Json.JsonElement? RecentErrors { get; set; }
}

public class PluginTelemetryRequest : PluginRequestBase
{
    [JsonPropertyName("events")] public List<PluginTelemetryEventDto> Events { get; set; } = new();
}

public class PluginTelemetryEventDto
{
    [JsonPropertyName("event_type")] public string EventType { get; set; } = string.Empty;
    [JsonPropertyName("occurred_at")] public string? OccurredAt { get; set; }
    [JsonPropertyName("payload")] public System.Text.Json.JsonElement? Payload { get; set; }
}

public class PluginCheckUpdateResponse
{
    [JsonPropertyName("update_available")] public bool UpdateAvailable { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("changelog")] public string? Changelog { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("download_expires_in_seconds")] public int? DownloadExpiresInSeconds { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("file_size_bytes")] public long? FileSizeBytes { get; set; }
    [JsonPropertyName("min_wp_version")] public string? MinWpVersion { get; set; }
    [JsonPropertyName("min_wc_version")] public string? MinWcVersion { get; set; }
    [JsonPropertyName("min_php_version")] public string? MinPhpVersion { get; set; }
    [JsonPropertyName("flags")] public Dictionary<string, object?> Flags { get; set; } = new();
}
