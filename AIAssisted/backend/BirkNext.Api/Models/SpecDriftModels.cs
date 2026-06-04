namespace BirkNext.Api.Models;

/// <summary>
/// A requirement that shows signs of spec drift — either uncovered or partially covered.
/// </summary>
public sealed class DriftRequirement
{
    public Scenario Requirement { get; init; } = null!;
    /// <summary>Reuses the same thresholds as ImpactAnalysisService (0=High, 1=Medium, 2+=Low).</summary>
    public RiskLevel DriftRisk { get; init; }
    public int LinkedTestCount { get; init; }
    public string DriftReason { get; init; } = string.Empty;
}

/// <summary>A single finding produced by one of the deterministic drift rules.</summary>
public sealed class DriftFinding
{
    /// <summary>
    /// Rule category: CoverageGap | PartialCoverage | OrphanTest | LowCoverage.
    /// String (not enum) for extensibility — new rules can be added without a schema change.
    /// </summary>
    public string Category { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    /// <summary>Reuses RiskLevel for consistent severity vocabulary across the feature set.</summary>
    public RiskLevel Severity { get; init; }
}

/// <summary>
/// Full spec drift report for a project. Computed on demand — not persisted.
/// Reuses data from ImpactAnalysisService; adds orphan test detection and
/// drift-specific findings + recommendations.
/// </summary>
public sealed class SpecDriftReport
{
    public RiskLevel OverallDriftRisk { get; init; }
    public int TotalRequirements { get; init; }
    public int RequirementsAtRisk { get; init; }
    /// <summary>Requirements with 0 linked tests (High Risk).</summary>
    public int CoverageGaps { get; init; }
    /// <summary>Tests not linked to any requirement.</summary>
    public int OrphanTestCount { get; init; }
    /// <summary>Percentage of requirements with at least one linked test.</summary>
    public double CoveragePercent { get; init; }
    public IReadOnlyList<DriftRequirement> RequirementsAtRiskList { get; init; } = [];
    public IReadOnlyList<Scenario> OrphanTests { get; init; } = [];
    public IReadOnlyList<DriftFinding> Findings { get; init; } = [];
    public IReadOnlyList<string> RecommendedActions { get; init; } = [];
}
