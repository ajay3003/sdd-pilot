using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

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
    [JsonPropertyName("findings")]           public List<ApiQualityFinding>       Findings        { get; init; } = [];
    [JsonPropertyName("categoryScores")]     public List<ApiQualityCategoryScore> CategoryScores  { get; init; } = [];
    [JsonPropertyName("recommendations")]    public List<string>                  Recommendations { get; init; } = [];
    [JsonPropertyName("limitations")]        public List<string>                  Limitations     { get; init; } = [];
    [JsonPropertyName("errorMessage")]       public string? ErrorMessage { get; init; }

    [JsonPropertyName("frontendResult")]  public ApiQualityEndpointResult? FrontendResult  { get; init; }
    [JsonPropertyName("restResult")]      public ApiQualityEndpointResult? RestResult      { get; init; }
    [JsonPropertyName("healthResult")]    public ApiQualityEndpointResult? HealthResult    { get; init; }
    [JsonPropertyName("swaggerResult")]   public ApiQualityEndpointResult? SwaggerResult   { get; init; }
    [JsonPropertyName("graphQlResult")]   public ApiQualityEndpointResult? GraphQlResult   { get; init; }
}
