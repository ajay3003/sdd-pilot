using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public enum FrontendQualitySeverity { Critical, High, Medium, Low, Info }

public enum FrontendQualityCategory
{
    Performance,
    Security,
    Accessibility,
    Standards,
    BlazorWasm,
    Readiness,
}

public sealed class FrontendQualityFinding
{
    [JsonPropertyName("id")]             public string                   Id             { get; init; } = "";
    [JsonPropertyName("title")]          public string                   Title          { get; init; } = "";
    [JsonPropertyName("severity")]       public FrontendQualitySeverity  Severity       { get; init; }
    [JsonPropertyName("category")]       public FrontendQualityCategory  Category       { get; init; }
    [JsonPropertyName("description")]    public string                   Description    { get; init; } = "";
    [JsonPropertyName("recommendation")] public string                   Recommendation { get; init; } = "";
    [JsonPropertyName("evidence")]       public List<string>             Evidence       { get; init; } = [];
    [JsonPropertyName("sourceSystem")]   public string?                  SourceSystem   { get; init; }
}

public sealed class FrontendQualityCategoryScore
{
    [JsonPropertyName("category")]     public FrontendQualityCategory Category     { get; init; }
    [JsonPropertyName("score")]        public int                     Score        { get; init; }
    [JsonPropertyName("findingCount")] public int                     FindingCount { get; init; }
    [JsonPropertyName("critical")]     public int                     Critical     { get; init; }
    [JsonPropertyName("high")]         public int                     High         { get; init; }
    [JsonPropertyName("assessed")]     public bool                    Assessed     { get; init; }
}

public sealed class FrontendQualityReviewReport
{
    [JsonPropertyName("targetUrl")]          public string                           TargetUrl          { get; init; } = "";
    [JsonPropertyName("generatedAt")]        public DateTime                         GeneratedAt        { get; init; }
    [JsonPropertyName("overallScore")]       public int                              OverallScore       { get; init; }
    [JsonPropertyName("performanceScore")]   public int                              PerformanceScore   { get; init; }
    [JsonPropertyName("securityScore")]      public int                              SecurityScore      { get; init; }
    [JsonPropertyName("accessibilityScore")] public int                              AccessibilityScore { get; init; }
    [JsonPropertyName("standardsScore")]     public int                              StandardsScore     { get; init; }
    [JsonPropertyName("wasmScore")]          public int                              WasmScore          { get; init; }
    [JsonPropertyName("readinessScore")]     public int                              ReadinessScore     { get; init; }
    [JsonPropertyName("findings")]           public List<FrontendQualityFinding>     Findings           { get; init; } = [];
    [JsonPropertyName("categoryScores")]     public List<FrontendQualityCategoryScore> CategoryScores   { get; init; } = [];
    [JsonPropertyName("recommendations")]    public List<string>                     Recommendations    { get; init; } = [];
    [JsonPropertyName("risks")]              public List<string>                     Risks              { get; init; } = [];
    [JsonPropertyName("limitations")]        public List<string>                     Limitations        { get; init; } = [];
    [JsonPropertyName("isBlazorWasm")]       public bool                             IsBlazorWasm       { get; init; }
    [JsonPropertyName("errorMessage")]       public string?                          ErrorMessage       { get; init; }
}
