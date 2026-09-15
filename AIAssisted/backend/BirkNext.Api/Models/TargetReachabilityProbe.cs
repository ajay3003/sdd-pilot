using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BirkNext.Api.Models;

/// <summary>
/// Request for a server-side reachability probe of a configured frontend target. The probe runs from the BirkNext backend
/// (the same network path the Static Security and Passive Performance engines use), so browser CORS policy and in-browser
/// protection never influence the result. Do NOT send credentials, tokens, cookies or authorization headers here.
/// </summary>
public sealed class TargetReachabilityRequest
{
    [Required(ErrorMessage = "Target URL is required")]
    [Url(ErrorMessage = "Target URL must be a valid HTTP/HTTPS URL")]
    [MaxLength(2048, ErrorMessage = "Target URL must not exceed 2048 characters")]
    [RegularExpression(@"^https?://.+$", ErrorMessage = "Only HTTP and HTTPS schemes are supported")]
    public string TargetUrl { get; set; } = "";
}

/// <summary>
/// Safe result of a server-side reachability probe. Distinguishes a real HTTP status (including 401/403/404/500) from a network
/// failure, a real timeout and a sign-in redirect, so a review never reports a generic timeout for a different problem.
/// Contains no headers, no body, no credentials and no query strings.
/// </summary>
public sealed class TargetReachabilityProbeResult
{
    /// <summary>Requested target without query string or fragment.</summary>
    [JsonPropertyName("targetUrl")] public string TargetUrl { get; set; } = "";

    [JsonPropertyName("reachability")] public TargetReachability Reachability { get; set; } = TargetReachability.Unknown;

    /// <summary>Final HTTP status code observed after validated redirects, or null when no HTTP response was received.</summary>
    [JsonPropertyName("statusCode")] public int? StatusCode { get; set; }

    /// <summary>Final URL after validated redirects, without query string or fragment. Null when no response was received.</summary>
    [JsonPropertyName("finalUrl")] public string? FinalUrl { get; set; }

    /// <summary>True when the frontend host itself demands authentication (401/403 or a redirect to a sign-in page).</summary>
    [JsonPropertyName("authenticationRequired")] public bool AuthenticationRequired { get; set; }

    [JsonPropertyName("redirectCount")] public int RedirectCount { get; set; }

    /// <summary>Time from first request to final response (or failure) in milliseconds.</summary>
    [JsonPropertyName("elapsedMs")] public double ElapsedMs { get; set; }

    /// <summary>Non-secret, user-facing explanation. "Target did not respond within timeout period." is used only for a real timeout.</summary>
    [JsonPropertyName("message")] public string Message { get; set; } = "";

    /// <summary>Set when SSRF/target policy blocked the probe before any request was made.</summary>
    [JsonPropertyName("blockReason")] public string? BlockReason { get; set; }

    /// <summary>True when the probe issued at least one request and received an HTTP response.</summary>
    [JsonPropertyName("responseReceived")] public bool ResponseReceived => StatusCode.HasValue;
}
