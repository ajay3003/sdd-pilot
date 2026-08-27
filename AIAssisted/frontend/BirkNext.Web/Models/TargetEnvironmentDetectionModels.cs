using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>
/// Safe metadata detected from target URL preflight inspection.
/// Contains only non-sensitive information suitable for configuration draft.
/// NEVER includes: passwords, tokens, cookies, authorization codes, secrets.
/// </summary>
public sealed class TargetEnvironmentDetectionResult
{
    /// <summary>
    /// Original URL provided by user (unchanged).
    /// </summary>
    [JsonPropertyName("originalUrl")]
    public string OriginalUrl { get; set; } = "";

    /// <summary>
    /// Normalized target URL after preflight inspection.
    /// If redirect occurred, this is the final reachable origin.
    /// </summary>
    [JsonPropertyName("normalizedTargetUrl")]
    public string? NormalizedTargetUrl { get; set; }

    /// <summary>
    /// Whether target is reachable without authentication.
    /// </summary>
    [JsonPropertyName("reachability")]
    public TargetReachability Reachability { get; set; }

    /// <summary>
    /// Whether authentication is required to reach intended application.
    /// </summary>
    [JsonPropertyName("authenticationRequired")]
    public bool AuthenticationRequired { get; set; }

    /// <summary>
    /// Type of authentication if detected.
    /// Example: MicrosoftEntraId, OpenIdConnect, OAuth2, etc.
    /// </summary>
    [JsonPropertyName("detectedAuthenticationType")]
    public FrontendAuthenticationType DetectedAuthenticationType { get; set; }

    /// <summary>
    /// Authority/IdP URL if authentication is required.
    /// Example: https://login.microsoftonline.com
    /// Sanitized of sensitive query parameters.
    /// </summary>
    [JsonPropertyName("detectedAuthority")]
    public string? DetectedAuthority { get; set; }

    /// <summary>
    /// Tenant identifier if safely detected from auth redirect.
    /// Only populated if concrete GUID or explicit tenant is detected.
    /// For 'common' or 'organizations', set to null and use TenantMode instead.
    /// </summary>
    [JsonPropertyName("detectedTenantId")]
    public string? DetectedTenantId { get; set; }

    /// <summary>
    /// Tenant mode if concrete tenant not detected.
    /// Examples: "common", "organizations", "consumers"
    /// </summary>
    [JsonPropertyName("tenantMode")]
    public string? TenantMode { get; set; }

    /// <summary>
    /// Client/Application ID if detected from auth redirect.
    /// Sanitized of context. Not a secret - metadata for configuration.
    /// </summary>
    [JsonPropertyName("detectedClientId")]
    public string? DetectedClientId { get; set; }

    /// <summary>
    /// Suggested environment type based on hostname/URL patterns.
    /// Only if confident. User must confirm.
    /// </summary>
    [JsonPropertyName("suggestedEnvironmentType")]
    public FrontendEnvironmentType? SuggestedEnvironmentType { get; set; }

    /// <summary>
    /// Suggested profile name derived from hostname.
    /// User can edit before saving.
    /// </summary>
    [JsonPropertyName("suggestedProfileName")]
    public string? SuggestedProfileName { get; set; }

    /// <summary>
    /// Number of redirects followed during detection.
    /// Alert if excessive.
    /// </summary>
    [JsonPropertyName("redirectCount")]
    public int RedirectCount { get; set; }

    /// <summary>
    /// Non-sensitive warnings or observations about the target.
    /// Examples: "excessive redirects", "untrusted host detected", etc.
    /// </summary>
    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// Overall confidence level of detection.
    /// Values: Low, Medium, High, VeryHigh
    /// </summary>
    [JsonPropertyName("confidence")]
    public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Medium;

    /// <summary>
    /// User-friendly status message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Whether detection succeeded or an error occurred.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Machine-readable error code if detection failed.
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }
}

public enum TargetReachability
{
    Reachable,
    AuthenticationRequired,
    Unreachable,
    Timeout,
    TlsError,
    DnsError,
    TooManyRedirects,
    UntrustedRedirect,
    Unknown
}

public enum DetectionConfidence
{
    Low,
    Medium,
    High,
    VeryHigh
}

/// <summary>
/// Individual field detection with provenance.
/// Tracks where each value came from for UI presentation.
/// </summary>
public sealed class DetectedFieldValue<T>
{
    public T? Value { get; set; }
    public FieldValueSource Source { get; set; }
    public bool UserCanEdit { get; set; } = true;
    public string? Reason { get; set; }
}

public enum FieldValueSource
{
    /// <summary>Detected from target inspection/redirect chain</summary>
    Detected,

    /// <summary>Inferred/suggested based on heuristics</summary>
    Suggested,

    /// <summary>User already configured - unchanged</summary>
    UserConfigured,

    /// <summary>Derived from detection</summary>
    Derived
}
