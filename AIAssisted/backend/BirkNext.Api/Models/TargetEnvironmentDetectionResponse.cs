using System.Text.Json.Serialization;

namespace BirkNext.Api.Models;

/// <summary>
/// Safe response model for target environment detection.
/// Contains only non-sensitive metadata suitable for configuration.
/// NEVER includes: passwords, tokens, cookies, codes, secrets, headers, query parameters.
/// </summary>
public sealed class TargetEnvironmentDetectionResponse
{
    [JsonPropertyName("originalUrl")]
    public string OriginalUrl { get; set; } = "";

    [JsonPropertyName("normalizedTargetUrl")]
    public string? NormalizedTargetUrl { get; set; }

    [JsonPropertyName("reachability")]
    public TargetReachability Reachability { get; set; }

    [JsonPropertyName("authenticationRequired")]
    public bool AuthenticationRequired { get; set; }

    [JsonPropertyName("detectedAuthenticationType")]
    public FrontendAuthenticationType DetectedAuthenticationType { get; set; }

    [JsonPropertyName("detectedAuthority")]
    public string? DetectedAuthority { get; set; }

    [JsonPropertyName("detectedTenantId")]
    public string? DetectedTenantId { get; set; }

    [JsonPropertyName("tenantMode")]
    public string? TenantMode { get; set; }

    [JsonPropertyName("detectedClientId")]
    public string? DetectedClientId { get; set; }

    [JsonPropertyName("suggestedEnvironmentType")]
    public FrontendEnvironmentType? SuggestedEnvironmentType { get; set; }

    [JsonPropertyName("suggestedProfileName")]
    public string? SuggestedProfileName { get; set; }

    [JsonPropertyName("redirectCount")]
    public int RedirectCount { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];

    [JsonPropertyName("confidence")]
    public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Medium;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Detected client-side framework type (Blazor WASM, React, Angular, etc.).
    /// Only populated if positive framework indicators are found in the response body.
    /// </summary>
    [JsonPropertyName("detectedClientFramework")]
    public ClientFrameworkType? DetectedClientFramework { get; set; }
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

public enum FrontendEnvironmentType
{
    Development,
    QA,
    RC,
    Production,
    Local
}

public enum FrontendAuthenticationType
{
    None,
    MicrosoftEntraId,
    OpenIdConnect,
    OAuth2,
    Unknown
}

public enum ClientFrameworkType
{
    BlazorWebAssembly,
    React,
    Angular,
    Vue,
    Other
}
