namespace DelicateCouriers.Features.Shiplogic;

/// <summary>
/// Configuration settings for Shiplogic API integration
/// </summary>
public class ShiplogicSettings
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "Shiplogic";

    /// <summary>
    /// Shiplogic API base URL
    /// Sandbox: https://sandbox.shiplogic.com
    /// Production: https://api.shiplogic.com
    /// </summary>
    public required string ApiBaseUrl { get; set; } = "https://api.shiplogic.com";

    /// <summary>
    /// Bearer token for API authentication
    /// </summary>
    public required string BearerToken { get; set; }

    /// <summary>
    /// HTTP client timeout in seconds
    /// Default: 30 seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum retry attempts for failed requests
    /// Default: 3
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Circuit breaker failure threshold before opening circuit
    /// Default: 5
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Circuit breaker duration in seconds before attempting to close
    /// Default: 60 seconds
    /// </summary>
    public int CircuitBreakerDurationSeconds { get; set; } = 60;

    /// <summary>
    /// Rate limit: Maximum requests per minute
    /// Default: 60 (Shiplogic's typical limit)
    /// </summary>
    public int RateLimitPerMinute { get; set; } = 60;
}