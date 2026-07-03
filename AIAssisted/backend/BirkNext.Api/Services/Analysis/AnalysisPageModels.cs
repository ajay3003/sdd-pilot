namespace BirkNext.Api.Services.Analysis;

/// <summary>
/// Status of an analysis page or result.
/// Ready = prerequisites satisfied and runnable
/// Blocked = required workspace artifacts/config missing
/// Warning = degraded but runnable
/// Fail = actual analysis/runtime error only (not missing inputs)
/// Empty = no results yet, not failure
/// </summary>
public enum AnalysisStatus
{
    Ready = 0,
    Blocked = 1,
    Warning = 2,
    Fail = 3,
    Empty = 4
}

/// <summary>
/// Severity level for analysis findings.
/// </summary>
public enum AnalysisSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>
/// Structured model for an analysis page (Spec Drift, Impact Analysis, etc.).
/// All pages must follow this contract.
/// </summary>
public class AnalysisPageModel
{
    /// <summary>Page title (e.g., "Spec Drift")</summary>
    public required string Title { get; set; }

    /// <summary>Page description/subtitle</summary>
    public required string Description { get; set; }

    /// <summary>Overall readiness status of the page</summary>
    public required AnalysisStatus ReadinessStatus { get; set; }

    /// <summary>Artifacts/inputs required to run this analysis (e.g., "Specification", "Change Input")</summary>
    public required List<string> RequiredInputs { get; set; }

    /// <summary>Which required inputs are missing (explains why Blocked)</summary>
    public required List<string> MissingInputs { get; set; }

    /// <summary>Optional: Analysis sections/tabs (e.g., Overview, Details, etc.)</summary>
    public List<AnalysisSection> Sections { get; set; } = [];

    /// <summary>Analysis results/findings</summary>
    public List<AnalysisResult> Results { get; set; } = [];

    /// <summary>Recommended actions (e.g., "Upload specification", "Configure target environment")</summary>
    public List<AnalysisAction> Actions { get; set; } = [];

    /// <summary>Overall summary and statistics</summary>
    public required AnalysisSummary Summary { get; set; }
}

/// <summary>
/// A section within an analysis page (e.g., Overview, Details).
/// </summary>
public class AnalysisSection
{
    public required string Name { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public List<string> Items { get; set; } = [];
}

/// <summary>
/// A single result or finding from analysis.
/// </summary>
public class AnalysisResult
{
    /// <summary>Result identifier/name</summary>
    public required string Name { get; set; }

    /// <summary>Category (e.g., "CoverageGap", "Orphan", "Drift", "Impact", "Unmapped")</summary>
    public required string Category { get; set; }

    /// <summary>Status of this result</summary>
    public required AnalysisStatus Status { get; set; }

    /// <summary>Severity level</summary>
    public required AnalysisSeverity Severity { get; set; }

    /// <summary>Brief summary</summary>
    public required string Summary { get; set; }

    /// <summary>Detailed explanation</summary>
    public string? Details { get; set; }

    /// <summary>Recommended action or remediation</summary>
    public string? Recommendation { get; set; }

    /// <summary>Artifacts related to this result (requirement IDs, task IDs, etc.)</summary>
    public List<string> RelatedArtifacts { get; set; } = [];
}

/// <summary>
/// Recommended action from the analysis page.
/// </summary>
public class AnalysisAction
{
    public required string Label { get; set; }
    public required string Description { get; set; }
    public string? NavigationUrl { get; set; }
    public bool IsPrimary { get; set; }
}

/// <summary>
/// Overall summary and statistics for the analysis.
/// </summary>
public class AnalysisSummary
{
    /// <summary>Can the analysis run right now?</summary>
    public required bool CanRun { get; set; }

    /// <summary>Human-readable message explaining current state</summary>
    public required string ReadinessMessage { get; set; }

    /// <summary>Total number of results found</summary>
    public int TotalResults { get; set; }

    /// <summary>Results by severity</summary>
    public int CriticalCount { get; set; }
    public int HighCount { get; set; }
    public int MediumCount { get; set; }
    public int LowCount { get; set; }

    /// <summary>Health percentage (0-100)</summary>
    public int HealthPercent { get; set; }
}
