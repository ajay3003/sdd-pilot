using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.ApiQuality;

public enum ApiQualitySeverity { Critical, High, Medium, Low, Info }

public enum ApiQualityCategory
{
    Connectivity,
    Performance,
    Security,
    Rest,
    GraphQL,
    OpenApi,
    Readiness
}

public sealed class ApiQualityReviewRequest
{
    [JsonPropertyName("frontendBaseUrl")]  public string? FrontendBaseUrl  { get; set; }
    [JsonPropertyName("restBaseUrl")]      public string? RestBaseUrl      { get; set; }
    [JsonPropertyName("healthEndpoint")]   public string? HealthEndpoint   { get; set; }
    [JsonPropertyName("swaggerUrl")]       public string? SwaggerUrl       { get; set; }
    [JsonPropertyName("graphQlEndpoint")]  public string? GraphQlEndpoint  { get; set; }
    [JsonPropertyName("timeoutSeconds")]   public int     TimeoutSeconds   { get; set; } = 30;
    [JsonPropertyName("retryCount")]       public int     RetryCount       { get; set; } = 3;
    [JsonPropertyName("environmentName")]  public string  EnvironmentName  { get; set; } = "";

    // Active Target Environment authenticated-testing identity. Lets the backend route API-backed checks through the memory-only
    // authenticated context when the environment uses the Local HTTPS proxy. Never carries a token.
    [JsonPropertyName("authenticatedTestingMethod")] public BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod AuthenticatedTestingMethod { get; set; } = BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.ManagedEdgeCdp;
    [JsonPropertyName("profileId")]          public string? ProfileId          { get; set; }
    [JsonPropertyName("contextFingerprint")] public string? ContextFingerprint { get; set; }
}

/// <summary>One authenticated API-backed check performed for the review through the Local HTTPS proxy context. No credential, headers or body.</summary>
public sealed class ApiQualityAuthenticatedCheck
{
    [JsonPropertyName("label")]         public string                       Label         { get; init; } = "";
    [JsonPropertyName("url")]           public string                       Url           { get; init; } = "";
    [JsonPropertyName("executionMode")] public BirkNext.LocalHttpsProxy.ReviewExecutionMode ExecutionMode { get; init; }
    [JsonPropertyName("status")]        public BirkNext.LocalHttpsProxy.AuthenticatedExecutionStatus Status { get; init; }
    [JsonPropertyName("statusCode")]    public int                          StatusCode    { get; init; }
    [JsonPropertyName("contentType")]   public string?                      ContentType   { get; init; }
    [JsonPropertyName("elapsedMs")]     public double                       ElapsedMs     { get; init; }
    [JsonPropertyName("outcome")]       public string                       Outcome       { get; init; } = "";
}

/// <summary>Authenticated API availability + provenance surfaced in the API Quality report. Capability flags come from the shared review model; no token.</summary>
public sealed class ApiQualityAuthenticationSummary
{
    [JsonPropertyName("capabilities")] public BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities Capabilities { get; init; } = new();
    [JsonPropertyName("checks")]       public List<ApiQualityAuthenticatedCheck> Checks { get; init; } = [];
}

public sealed class ApiQualityFinding
{
    [JsonPropertyName("id")]             public string               Id             { get; init; } = "";
    [JsonPropertyName("title")]          public string               Title          { get; init; } = "";
    [JsonPropertyName("description")]    public string               Description    { get; init; } = "";
    [JsonPropertyName("recommendation")] public string               Recommendation { get; init; } = "";
    [JsonPropertyName("severity")]       public ApiQualitySeverity   Severity       { get; init; }
    [JsonPropertyName("category")]       public ApiQualityCategory   Category       { get; init; }
    [JsonPropertyName("evidence")]       public List<string>         Evidence       { get; init; } = [];
}

public sealed class ApiQualityCategoryScore
{
    [JsonPropertyName("category")]     public ApiQualityCategory Category     { get; init; }
    [JsonPropertyName("score")]        public int                Score        { get; init; }
    [JsonPropertyName("findingCount")] public int                FindingCount { get; init; }
    [JsonPropertyName("assessed")]     public bool               Assessed     { get; init; }
}

public sealed class ApiQualityEndpointResult
{
    [JsonPropertyName("endpoint")]        public string                     Endpoint        { get; init; } = "";
    [JsonPropertyName("reachable")]       public bool                       Reachable       { get; init; }
    [JsonPropertyName("statusCode")]      public int                        StatusCode      { get; init; }
    [JsonPropertyName("responseTimeMs")]  public long                       ResponseTimeMs  { get; init; }
    [JsonPropertyName("isHttps")]         public bool                       IsHttps         { get; init; }
    [JsonPropertyName("responseHeaders")] public Dictionary<string, string> ResponseHeaders { get; init; } = new();
    [JsonPropertyName("redirectedTo")]    public string?                    RedirectedTo    { get; init; }
    [JsonPropertyName("error")]           public string?                    Error           { get; init; }
}

public sealed class ApiQualityReviewReport
{
    [JsonPropertyName("environmentName")]    public string  EnvironmentName    { get; init; } = "";
    [JsonPropertyName("generatedAt")]        public DateTime GeneratedAt       { get; init; }
    [JsonPropertyName("overallScore")]       public int     OverallScore       { get; init; }
    [JsonPropertyName("connectivityScore")]  public int     ConnectivityScore  { get; init; }
    [JsonPropertyName("performanceScore")]   public int     PerformanceScore   { get; init; }
    [JsonPropertyName("securityScore")]      public int     SecurityScore      { get; init; }
    [JsonPropertyName("restScore")]          public int     RestScore          { get; init; }
    [JsonPropertyName("graphQlScore")]       public int     GraphQlScore       { get; init; }
    [JsonPropertyName("openApiScore")]       public int     OpenApiScore       { get; init; }
    [JsonPropertyName("readinessScore")]     public int     ReadinessScore     { get; init; }
    [JsonPropertyName("isDeploymentReady")]  public bool    IsDeploymentReady  { get; init; }
    [JsonPropertyName("findings")]           public List<ApiQualityFinding>       Findings       { get; init; } = [];
    [JsonPropertyName("categoryScores")]     public List<ApiQualityCategoryScore> CategoryScores { get; init; } = [];
    [JsonPropertyName("recommendations")]    public List<string>                  Recommendations { get; init; } = [];
    [JsonPropertyName("limitations")]        public List<string>                  Limitations     { get; init; } = [];
    [JsonPropertyName("errorMessage")]       public string? ErrorMessage { get; init; }

    /// <summary>Authenticated API availability and provenance for this review. Null when the environment has no authenticated-testing identity.</summary>
    [JsonPropertyName("authentication")]     public ApiQualityAuthenticationSummary? Authentication { get; init; }

    // Endpoint probe results for Connectivity tab
    [JsonPropertyName("frontendResult")]  public ApiQualityEndpointResult? FrontendResult  { get; init; }
    [JsonPropertyName("restResult")]      public ApiQualityEndpointResult? RestResult      { get; init; }
    [JsonPropertyName("healthResult")]    public ApiQualityEndpointResult? HealthResult    { get; init; }
    [JsonPropertyName("swaggerResult")]   public ApiQualityEndpointResult? SwaggerResult   { get; init; }
    [JsonPropertyName("graphQlResult")]   public ApiQualityEndpointResult? GraphQlResult   { get; init; }
}
