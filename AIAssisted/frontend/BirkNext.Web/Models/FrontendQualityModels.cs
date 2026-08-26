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

public enum CheckExecutionStatus
{
    NotAssessed,
    Skipped,
    Passed,
    Failed,
    EngineError,
    NotApplicable,
}

public enum AssessmentCompleteness
{
    Full,
    Partial,
    Failed,
}

public enum PreflightStatus
{
    Ready,
    ReadyWithWarnings,
    AuthenticationRequired,
    Unreachable,
    InvalidTarget,
    ScannerUnavailable,
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
    [JsonPropertyName("status")]         public CheckExecutionStatus     Status         { get; init; } = CheckExecutionStatus.Passed;
}

public sealed class FrontendQualityCategoryScore
{
    [JsonPropertyName("category")]     public FrontendQualityCategory Category     { get; init; }
    [JsonPropertyName("score")]        public int?                    Score        { get; init; }
    [JsonPropertyName("findingCount")] public int                     FindingCount { get; init; }
    [JsonPropertyName("critical")]     public int                     Critical     { get; init; }
    [JsonPropertyName("high")]         public int                     High         { get; init; }
    [JsonPropertyName("assessed")]     public bool                    Assessed     { get; init; }
    [JsonPropertyName("notAssessedReason")] public string?            NotAssessedReason { get; init; }
}

public sealed class FrontendQualityReviewReport
{
    [JsonPropertyName("targetUrl")]          public string                           TargetUrl          { get; init; } = "";
    [JsonPropertyName("finalUrl")]           public string?                          FinalUrl           { get; init; }
    [JsonPropertyName("generatedAt")]        public DateTime                         GeneratedAt        { get; init; }
    [JsonPropertyName("completedAt")]        public DateTime?                        CompletedAt        { get; init; }
    [JsonPropertyName("durationMs")]         public long?                            DurationMs         { get; init; }
    [JsonPropertyName("overallScore")]       public int?                             OverallScore       { get; init; }
    [JsonPropertyName("performanceScore")]   public int?                             PerformanceScore   { get; init; }
    [JsonPropertyName("securityScore")]      public int?                             SecurityScore      { get; init; }
    [JsonPropertyName("accessibilityScore")] public int?                             AccessibilityScore { get; init; }
    [JsonPropertyName("standardsScore")]     public int?                             StandardsScore     { get; init; }
    [JsonPropertyName("wasmScore")]          public int?                             WasmScore          { get; init; }
    [JsonPropertyName("readinessScore")]     public int?                             ReadinessScore     { get; init; }
    [JsonPropertyName("findings")]           public List<FrontendQualityFinding>     Findings           { get; init; } = [];
    [JsonPropertyName("categoryScores")]     public List<FrontendQualityCategoryScore> CategoryScores   { get; init; } = [];
    [JsonPropertyName("recommendations")]    public List<string>                     Recommendations    { get; init; } = [];
    [JsonPropertyName("risks")]              public List<string>                     Risks              { get; init; } = [];
    [JsonPropertyName("limitations")]        public List<string>                     Limitations        { get; init; } = [];
    [JsonPropertyName("isBlazorWasm")]       public bool                             IsBlazorWasm       { get; init; }
    [JsonPropertyName("errorMessage")]       public string?                          ErrorMessage       { get; init; }
    [JsonPropertyName("completeness")]       public AssessmentCompleteness?          Completeness       { get; init; }
    [JsonPropertyName("preflightStatus")]    public PreflightStatus?                 PreflightStatus    { get; init; }
    [JsonPropertyName("preflightMessage")]   public string?                          PreflightMessage   { get; init; }
    [JsonPropertyName("redirectOccurred")]   public bool                             RedirectOccurred   { get; init; }
    [JsonPropertyName("assessedEngines")]    public List<string>                     AssessedEngines    { get; init; } = [];
    [JsonPropertyName("failedEngines")]      public List<string>                     FailedEngines      { get; init; } = [];
    [JsonPropertyName("skippedEngines")]     public List<string>                     SkippedEngines     { get; init; } = [];
    [JsonPropertyName("accessibilityReport")] public AccessibilityResultDto?          AccessibilityReport { get; init; }
    [JsonPropertyName("lighthouseReport")]    public LighthouseResultDto?              LighthouseReport { get; init; }
    [JsonPropertyName("passiveSecurityReport")] public PassiveSecurityResultDto?       PassiveSecurityReport { get; init; }
}

public enum LighthouseExecutionStatusDto { NotAssessed, Assessed, EngineError, Skipped, AuthenticationRequired, TimedOut }
public enum LighthouseMetricStatusDto { Measured, Good, NeedsImprovement, Poor, NotAvailable, FieldDataRequired }
public sealed record LighthouseMetricDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("observedValue")] double? ObservedValue = null,
    [property: JsonPropertyName("unit")] string? Unit = null,
    [property: JsonPropertyName("status")] LighthouseMetricStatusDto Status = LighthouseMetricStatusDto.NotAvailable,
    [property: JsonPropertyName("source")] string Source = "Lighthouse",
    [property: JsonPropertyName("measurementType")] string MeasurementType = "Lab",
    [property: JsonPropertyName("auditId")] string? AuditId = null,
    [property: JsonPropertyName("threshold")] double? Threshold = null,
    [property: JsonPropertyName("thresholdSource")] string? ThresholdSource = null);
public sealed record LighthouseAuditFindingDto(
    [property: JsonPropertyName("auditId")] string AuditId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("score")] double? Score = null,
    [property: JsonPropertyName("displayValue")] string? DisplayValue = null,
    [property: JsonPropertyName("sources")] List<string>? Sources = null);
public sealed record LighthouseResultDto(
    [property: JsonPropertyName("executionStatus")] LighthouseExecutionStatusDto ExecutionStatus = LighthouseExecutionStatusDto.NotAssessed,
    [property: JsonPropertyName("engineName")] string EngineName = "Lighthouse Lab Performance",
    [property: JsonPropertyName("measurementType")] string MeasurementType = "Lab",
    [property: JsonPropertyName("fieldDataAvailable")] bool FieldDataAvailable = false,
    [property: JsonPropertyName("lighthouseVersion")] string? LighthouseVersion = null,
    [property: JsonPropertyName("nodeVersion")] string? NodeVersion = null,
    [property: JsonPropertyName("browserName")] string? BrowserName = null,
    [property: JsonPropertyName("browserVersion")] string? BrowserVersion = null,
    [property: JsonPropertyName("requestedUrl")] string? RequestedUrl = null,
    [property: JsonPropertyName("finalUrl")] string? FinalUrl = null,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null,
    [property: JsonPropertyName("performanceScore")] int? PerformanceScore = null,
    [property: JsonPropertyName("metrics")] List<LighthouseMetricDto>? Metrics = null,
    [property: JsonPropertyName("audits")] List<LighthouseAuditFindingDto>? Audits = null,
    [property: JsonPropertyName("limitations")] List<string>? Limitations = null,
    [property: JsonPropertyName("engineError")] string? EngineError = null);

public enum AccessibilityExecutionStatusDto { NotAssessed, Assessed, EngineError, Skipped, AuthenticationRequired }
public enum AccessibilityFindingKindDto { Violation, NeedsManualReview }

public sealed record AccessibilityFindingDto(
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("kind")] AccessibilityFindingKindDto Kind,
    [property: JsonPropertyName("severity")] FrontendQualitySeverity Severity,
    [property: JsonPropertyName("impact")] string? Impact,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("wcagTags")] List<string> WcagTags,
    [property: JsonPropertyName("affectedNodeCount")] int AffectedNodeCount,
    [property: JsonPropertyName("selectors")] List<string> Selectors,
    [property: JsonPropertyName("htmlSnippets")] List<string> HtmlSnippets,
    [property: JsonPropertyName("failureSummaries")] List<string> FailureSummaries,
    [property: JsonPropertyName("helpUrl")] string? HelpUrl,
    [property: JsonPropertyName("recommendation")] string Recommendation);

public sealed record AccessibilityResultDto(
    [property: JsonPropertyName("executionStatus")] AccessibilityExecutionStatusDto ExecutionStatus = AccessibilityExecutionStatusDto.NotAssessed,
    [property: JsonPropertyName("engineName")] string EngineName = "Accessibility (axe-core)",
    [property: JsonPropertyName("axeVersion")] string? AxeVersion = null,
    [property: JsonPropertyName("browserName")] string? BrowserName = null,
    [property: JsonPropertyName("browserVersion")] string? BrowserVersion = null,
    [property: JsonPropertyName("requestedUrl")] string? RequestedUrl = null,
    [property: JsonPropertyName("finalUrl")] string? FinalUrl = null,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt = default,
    [property: JsonPropertyName("completedAt")] DateTime? CompletedAt = null,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null,
    [property: JsonPropertyName("ruleTags")] List<string>? RuleTags = null,
    [property: JsonPropertyName("violationCount")] int ViolationCount = 0,
    [property: JsonPropertyName("incompleteCount")] int IncompleteCount = 0,
    [property: JsonPropertyName("passCount")] int PassCount = 0,
    [property: JsonPropertyName("inapplicableCount")] int InapplicableCount = 0,
    [property: JsonPropertyName("findings")] List<AccessibilityFindingDto>? Findings = null,
    [property: JsonPropertyName("limitations")] List<string>? Limitations = null,
    [property: JsonPropertyName("engineError")] string? EngineError = null);

// ── Browser Runtime DTOs ────────────────────────────────────────────
public enum BrowserRuntimeEngineStatusDto
{
    NotAssessed,
    Assessed,
    EngineError,
    Skipped,
    NotApplicable,
}

public enum BrowserStartupStateDto
{
    Started,
    StartedWithErrors,
    Failed,
    TimedOut,
    NotApplicable,
}

public enum BrowserRuntimeFindingSeverityDto
{
    Critical,
    High,
    Medium,
    Low,
    Info,
}

public sealed record BrowserRuntimeFindingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("severity")] BrowserRuntimeFindingSeverityDto Severity,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("recommendation")] string Recommendation,
    [property: JsonPropertyName("evidence")] List<string> Evidence = default!);

public sealed record BrowserRuntimeResultDto(
    [property: JsonPropertyName("status")] BrowserRuntimeEngineStatusDto Status = BrowserRuntimeEngineStatusDto.NotAssessed,
    [property: JsonPropertyName("engineName")] string EngineName = "Browser Runtime",
    [property: JsonPropertyName("browserName")] string? BrowserName = null,
    [property: JsonPropertyName("browserVersion")] string? BrowserVersion = null,
    [property: JsonPropertyName("requestedUrl")] string? RequestedUrl = null,
    [property: JsonPropertyName("finalUrl")] string? FinalUrl = null,
    [property: JsonPropertyName("startedAt")] DateTime StartedAt = default,
    [property: JsonPropertyName("completedAt")] DateTime? CompletedAt = null,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null,
    [property: JsonPropertyName("startupState")] BrowserStartupStateDto StartupState = BrowserStartupStateDto.NotApplicable,
    [property: JsonPropertyName("consoleErrorCount")] int ConsoleErrorCount = 0,
    [property: JsonPropertyName("pageErrorCount")] int PageErrorCount = 0,
    [property: JsonPropertyName("criticalResourceFailureCount")] int CriticalResourceFailureCount = 0,
    [property: JsonPropertyName("findings")] List<BrowserRuntimeFindingDto>? Findings = null,
    [property: JsonPropertyName("engineError")] string? EngineError = null,
    [property: JsonPropertyName("limitations")] List<string>? Limitations = null);
