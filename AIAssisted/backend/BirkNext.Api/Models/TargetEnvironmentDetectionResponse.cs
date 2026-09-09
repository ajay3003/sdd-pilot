using System.Text.Json.Serialization;

namespace BirkNext.Api.Models;

/// <summary>
/// Safe response model for target environment detection.
/// Contains only non-sensitive metadata suitable for configuration.
/// NEVER includes: passwords, tokens, cookies, codes, secrets, headers, query parameters.
/// </summary>
public sealed class TargetEnvironmentDetectionResponse
{
    [JsonPropertyName("manualAuthenticationVerificationRequired")]
    public bool ManualAuthenticationVerificationRequired { get; set; }
    [JsonPropertyName("manualAuthenticationVerificationStatus")]
    public ManualAuthenticationVerificationStatus ManualAuthenticationVerificationStatus { get; set; }

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

    [JsonPropertyName("detectedRedirectUrls")]
    public List<string> DetectedRedirectUrls { get; set; } = [];

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

    public string? FrameworkEvidence { get; set; }
    public DetectionConfidence FrameworkConfidence { get; set; } = DetectionConfidence.Low;

    [JsonPropertyName("state")]
    public TargetDetectionState State { get; set; } = TargetDetectionState.NotChecked;

    [JsonPropertyName("browserRuntimeInspectionRequired")]
    public bool BrowserRuntimeInspectionRequired { get; set; }

    [JsonPropertyName("isActivationReady")]
    public bool IsActivationReady { get; set; }

    // ─────────────────────────────────────────────────────────────────────────
    // Endpoint Discovery (REST, GraphQL, Swagger, Health)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detected REST API base URL (e.g., https://api.example.com or https://app.example.com/api).
    /// Discovered from structured config, explicit references, or safe probing.
    /// </summary>
    [JsonPropertyName("detectedRestBaseUrl")]
    public string? DetectedRestBaseUrl { get; set; }

    [JsonPropertyName("restConfidence")]
    public DetectionConfidence RestConfidence { get; set; } = DetectionConfidence.Low;

    /// <summary>
    /// Detected GraphQL endpoint (e.g., https://api.example.com/graphql).
    /// Discovered when explicit config or endpoint evidence exists.
    /// </summary>
    [JsonPropertyName("detectedGraphQlEndpoint")]
    public string? DetectedGraphQlEndpoint { get; set; }

    [JsonPropertyName("graphQlConfidence")]
    public DetectionConfidence GraphQlConfidence { get; set; } = DetectionConfidence.Low;

    /// <summary>
    /// Detected Swagger/OpenAPI specification URL (e.g., https://api.example.com/swagger/v1/swagger.json).
    /// Discovered when endpoint exists and responds with valid schema.
    /// </summary>
    [JsonPropertyName("detectedSwaggerUrl")]
    public string? DetectedSwaggerUrl { get; set; }

    [JsonPropertyName("swaggerConfidence")]
    public DetectionConfidence SwaggerConfidence { get; set; } = DetectionConfidence.Low;

    /// <summary>
    /// Detected health check endpoint (e.g., https://api.example.com/health).
    /// Discovered from explicit config or successful endpoint verification.
    /// </summary>
    [JsonPropertyName("detectedHealthEndpoint")]
    public string? DetectedHealthEndpoint { get; set; }

    [JsonPropertyName("healthConfidence")]
    public DetectionConfidence HealthConfidence { get; set; } = DetectionConfidence.Low;

    // ─────────────────────────────────────────────────────────────────────────
    // Integration Discovery
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Detected integration targets (Event Hub, Service Bus, Kafka, RabbitMQ, etc.).
    /// Proposals only - never auto-applied to configured integrations.
    /// Contains only safe, non-sensitive metadata (namespace, resource name, not credentials).
    /// </summary>
    [JsonPropertyName("detectedIntegrations")]
    public List<DiscoveredIntegration> DetectedIntegrations { get; set; } = [];

    // ─────────────────────────────────────────────────────────────────────────
    // Discovery Evidence & Provenance
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Evidence sources for all discovered endpoints and integrations.
    /// Provides transparency and traceability for every detection claim.
    /// </summary>
    [JsonPropertyName("discoveryEvidence")]
    public List<DiscoveryEvidence> DiscoveryEvidence { get; set; } = [];

    // ─────────────────────────────────────────────────────────────────────────
    // Stale Invalidation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fingerprint of the Frontend URL used for this detection.
    /// If Frontend URL changes, all endpoint/integration discoveries become stale.
    /// Prevents reuse of old discoveries against new targets.
    /// </summary>
    [JsonPropertyName("frontendUrlFingerprint")]
    public string? FrontendUrlFingerprint { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
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

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetectionConfidence
{
    Low,
    Medium,
    High,
    VeryHigh
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendEnvironmentType
{
    Development,
    QA,
    RC,
    Production,
    Local
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendAuthenticationType
{
    None,
    MicrosoftEntraId,
    OpenIdConnect,
    OAuth2,
    Unknown
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientFrameworkType
{
    BlazorWebAssembly,
    React,
    Angular,
    Vue,
    Other
}

/// <summary>
/// Discovered integration target (Event Hub, Service Bus, Kafka, RabbitMQ, etc.).
/// Contains only safe, non-sensitive public metadata.
/// NEVER contains: passwords, connection strings with secrets, tokens, API keys.
/// </summary>
public sealed class DiscoveredIntegration
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = ""; // REST, GraphQL, EventHub, ServiceBus, Kafka, RabbitMQ

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; set; } // URL or namespace (safe only, no secrets)

    [JsonPropertyName("resourceName")]
    public string? ResourceName { get; set; } // Topic, queue, hub name, etc.

    [JsonPropertyName("confidence")]
    public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Low;

    [JsonPropertyName("evidenceSource")]
    public string? EvidenceSource { get; set; } // Where discovered (appsettings.json, config, HTML, etc.)

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = []; // Supporting details
}

/// <summary>
/// Evidence/provenance for a discovered endpoint or integration.
/// Provides transparency on how and where each value was discovered.
/// </summary>
public sealed class DiscoveryEvidence
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EvidenceType
    {
        StructuredConfig,   // From config file (appsettings.json, .env, etc.)
        HtmlReference,      // From HTML (script tag, meta tag, config object)
        WasmAsset,          // From deployed WASM asset/bundle
        HintExtractor,      // From source project hints
        SafeProbe,          // From safe endpoint verification
        ConventionalCandidate
    }

    [JsonPropertyName("type")]
    public EvidenceType Type { get; set; }

    [JsonPropertyName("locationCategory")]
    public string LocationCategory { get; set; } = ""; // appsettings.json, HTML, /swagger/v1/swagger.json, etc.

    [JsonPropertyName("value")]
    public string? Value { get; set; } // Non-sensitive discovered value

    [JsonPropertyName("confidence")]
    public DetectionConfidence Confidence { get; set; }

    [JsonPropertyName("targetField")]
    public string? TargetField { get; set; } // Which field this evidence supports (RestBaseUrl, GraphQlEndpoint, etc.)

    public EndpointEvidenceStatus Status { get; set; } = EndpointEvidenceStatus.Candidate;
    public EndpointProbeStatus ProbeStatus { get; set; } = EndpointProbeStatus.NotPerformed;
    public int? HttpStatus { get; set; }
    public string? ContentType { get; set; }
    public OpenApiResourceKind OpenApiKind { get; set; } = OpenApiResourceKind.Unknown;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointEvidenceStatus { Candidate, Observed, Confirmed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointProbeStatus { NotPerformed, ResponseReceived, Blocked, Timeout, Failed, SizeLimitExceeded }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpenApiResourceKind { Unknown, SwaggerUi, OpenApiDocument }
