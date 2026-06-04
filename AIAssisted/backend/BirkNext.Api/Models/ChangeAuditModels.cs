using BirkNext.Api.Services;

namespace BirkNext.Api.Models;

/// <summary>
/// Input for a change audit request.
/// In v1 only ChangeDescription is used.
/// Extension points for future input types are documented below.
/// </summary>
public sealed class ChangeAuditRequest
{
    public string ProjectId { get; init; } = string.Empty;
    public string ChangeDescription { get; init; } = string.Empty;

    // ── Future extension points (not implemented in v1) ───────────────────────
    // public string? GitCommitHash { get; init; }
    // public string? PullRequestUrl { get; init; }
    // public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    // public string? AiSessionContext { get; init; }   // AI coding session transcript
    // public string? SpecDriftContext { get; init; }   // Spec Drift Detection payload
}

/// <summary>
/// Full change audit report produced by the AI Change Auditor.
/// Combines Claude's semantic analysis with formal risk data from ImpactAnalysisService.
/// </summary>
public sealed class ChangeAuditReport
{
    public string ChangeDescription { get; init; } = string.Empty;
    public RiskLevel OverallRiskLevel { get; init; }
    public string AiReasoning { get; init; } = string.Empty;
    public string RegressionScope { get; init; } = string.Empty;
    public IReadOnlyList<AuditAffectedRequirement> AffectedRequirements { get; init; } = [];
    public IReadOnlyList<AuditAffectedTest> AffectedTests { get; init; } = [];
    public IReadOnlyList<string> CoverageGaps { get; init; } = [];
    public IReadOnlyList<RegressionItem> RecommendedRegressionTests { get; init; } = [];
}

/// <summary>A requirement identified as potentially affected, enriched with formal impact data.</summary>
public sealed class AuditAffectedRequirement
{
    public Scenario Requirement { get; init; } = null!;
    public RiskLevel RiskLevel { get; init; }
    public int LinkedTestCount { get; init; }
    public string AiRelevanceReason { get; init; } = string.Empty;
}

/// <summary>A test scenario identified as potentially affected by the change.</summary>
public sealed class AuditAffectedTest
{
    public Scenario Test { get; init; } = null!;
    public string AiRelevanceReason { get; init; } = string.Empty;
}
