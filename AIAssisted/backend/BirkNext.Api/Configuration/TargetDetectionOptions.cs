namespace BirkNext.Api.Configuration;

/// <summary>
/// Configuration options for Target Environment Detection service.
/// SECURITY: Controls authorization, HTTPS enforcement, loopback allowance, and rate limiting.
/// </summary>
public sealed class TargetDetectionOptions
{
    public const string SectionName = "TargetDetection";

    /// <summary>
    /// Whether target detection is enabled globally.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether loopback addresses (127.x, ::1, localhost) are allowed for detection.
    /// Should be false in production.
    /// Default: false
    /// </summary>
    public bool AllowLoopback { get; set; } = false;

    /// <summary>
    /// Maximum number of redirects to follow during target inspection.
    /// Default: 5
    /// </summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>
    /// HTTP timeout for target inspection requests (seconds).
    /// Default: 10
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of detection requests per minute per IP/user.
    /// Default: 10
    /// </summary>
    public int RateLimitPerMinute { get; set; } = 10;

    /// <summary>
    /// Whether HTTPS is required for detection endpoint.
    /// Default: true (should always be true in production)
    /// </summary>
    public bool RequireHttps { get; set; } = true;
}
