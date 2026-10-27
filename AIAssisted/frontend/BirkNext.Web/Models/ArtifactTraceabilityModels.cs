namespace BirkNext.Web.Models;

// ── Enums ────────────────────────────────────────────────────────────────────

public enum ArtifactType
{
    Constitution,
    Specification,
    Plan,
    Task,
    DataModel,
}

public enum TraceabilityStatus
{
    Covered,
    Partial,
    Missing,
    Orphaned,
}

public enum GapSeverity
{
    High,
    Medium,
    Low,
    /// <summary>A missing explicit reference without a policy that requires it: evidence, not a defect.</summary>
    Informational,
}

public enum TraceabilityHealthLevel
{
    Good,
    Warning,
    Error,
    /// <summary>Neutral evidence statement (no policy requires the relation).</summary>
    Info,
}

/// <summary>
/// Which explicit authored references a project requires. None is configured by default: missing explicit references are informational
/// evidence, never a quality, readiness or release failure. Only an explicit policy turns a missing reference into a required gap.
/// </summary>
public sealed record TraceabilityPolicy(bool RequireConstitutionToSpec = false, bool RequireSpecToPlan = false, bool RequireTaskReferences = false)
{
    public static TraceabilityPolicy None { get; } = new();
    public bool Configured => RequireConstitutionToSpec || RequireSpecToPlan || RequireTaskReferences;
    public string Description => Configured
        ? "Project policy requires explicit " + string.Join(", ", new[] { RequireConstitutionToSpec ? "Constitution→Specification" : null, RequireSpecToPlan ? "Specification→Plan" : null, RequireTaskReferences ? "Task→Requirement/SC" : null }.Where(x => x is not null)) + " traceability."
        : "No explicit traceability policy configured.";
}

/// <summary>Reviewer-facing evidence labels. Absence of an explicit reference is never shown as "missing coverage" or "orphaned".</summary>
public static class TraceabilityLabels
{
    public static string Evidence(TraceabilityStatus status, ArtifactType chain) => (status, chain) switch
    {
        (TraceabilityStatus.Covered, ArtifactType.Specification) => "Explicitly referenced",
        (TraceabilityStatus.Covered, ArtifactType.Task) => "Explicit Requirement/SC reference",
        (TraceabilityStatus.Covered, _) => "Explicitly linked",
        (TraceabilityStatus.Partial, _) => "Partially linked",
        (TraceabilityStatus.Missing, ArtifactType.Specification) => "No explicit plan reference",
        (TraceabilityStatus.Missing, _) => "No explicit link",
        (TraceabilityStatus.Orphaned, _) => "No explicit Requirement/SC reference",
        _ => status.ToString(),
    };

    public static string Significance(TraceabilityGap gap) => gap.PolicyRequired ? "Required link missing (policy)" : "Informational";
}

// ── Links ────────────────────────────────────────────────────────────────────

public sealed class TraceabilityLink
{
    public string SourceId    { get; init; } = string.Empty;
    public ArtifactType SourceType { get; init; }
    public string TargetId    { get; init; } = string.Empty;
    public ArtifactType TargetType { get; init; }
    public string? SourceTitle { get; init; }
    public string? TargetTitle { get; init; }
}

// ── Coverage stats ────────────────────────────────────────────────────────────

public sealed class TraceabilityCoverageStats
{
    public int TotalItems    { get; init; }
    public int CoveredItems  { get; init; }
    public int PartialItems  { get; init; }
    public int MissingItems  { get; init; }
    public int OrphanedItems { get; init; }
    public bool HasApplicablePopulation => TotalItems > 0;

    public double CoveragePercentage =>
        TotalItems > 0
            ? Math.Round((double)(CoveredItems + PartialItems) / TotalItems * 100.0, 1)
            : 0.0;
}

// ── Per-chain item (one row in Constitution→Spec, Spec→Plan, Plan→Task tabs) ──

public sealed class ChainCoverage
{
    public string ItemId    { get; init; } = string.Empty;
    public string ItemTitle { get; init; } = string.Empty;
    public ArtifactType ItemType { get; init; }
    public string? ItemSubType { get; init; }   // e.g., rule type label or spec node type
    public TraceabilityStatus Status { get; init; }
    public List<TraceabilityLink> Links { get; init; } = [];
}

// ── Gaps ─────────────────────────────────────────────────────────────────────

public sealed class TraceabilityGap
{
    public string NodeId { get; init; } = Guid.NewGuid().ToString()[..10];
    public ArtifactType GapIn    { get; init; }
    public string ItemId         { get; init; } = string.Empty;
    public string ItemTitle      { get; init; } = string.Empty;
    public TraceabilityStatus Status { get; init; }   // Missing | Orphaned (display via TraceabilityLabels)
    public string Description    { get; init; } = string.Empty;
    public GapSeverity Severity  { get; init; }
    /// <summary>True only when an explicit project policy requires this relation. Evidence state stays "no explicit reference".</summary>
    public bool PolicyRequired   { get; init; }
}

// ── Matrix ───────────────────────────────────────────────────────────────────

public sealed class TraceabilityMatrixRow
{
    public string NodeId { get; init; } = Guid.NewGuid().ToString()[..10];

    // Column values — null means "not present / not loaded"
    public string  ConstitutionRuleId    { get; init; } = string.Empty;
    public string  ConstitutionRuleTitle { get; init; } = string.Empty;
    public string? SpecRequirementId     { get; init; }
    public string? SpecRequirementTitle  { get; init; }
    public string? PlanItemId            { get; init; }
    public string? PlanItemTitle         { get; init; }
    public string? TaskId                { get; init; }
    public string? TaskTitle             { get; init; }
    /// <summary>Constitution→Specification edge only (Covered = explicitly linked, Missing = no explicit link). No global chain status.</summary>
    public TraceabilityStatus Status { get; init; }
    /// <summary>Typed Plan node: "Plan phase", "Plan section", "Architecture decision" or "Supporting plan document". Never source evidence.</summary>
    public string? PlanItemKind          { get; init; }
    /// <summary>Edge evidence: "Explicit", "Derived", "No explicit reference" or "Not assessed".</summary>
    public string SpecEdge               { get; init; } = "No explicit reference";
    public string? PlanEdge              { get; init; }
    public string TaskEdge               { get; init; } = "Not assessed";
}

// ── Health ───────────────────────────────────────────────────────────────────

public sealed class TraceabilityHealthIndicator
{
    public string Icon    { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public TraceabilityHealthLevel Level { get; init; }
}

public sealed class TraceabilityHealth
{
    public int TotalRules        { get; init; }
    public int TotalRequirements { get; init; }
    public int TotalPlanItems    { get; init; }
    public int TotalTasks        { get; init; }

    public int CoveredCount  { get; init; }
    public int PartialCount  { get; init; }
    public int MissingCount  { get; init; }
    public int OrphanCount   { get; init; }

    public double CoveragePercentage { get; init; }
    public int CoverageDenominator { get; init; }
    public bool HasAssessedCoverage => CoverageDenominator > 0;
    public int GapCount { get; init; }

    public List<TraceabilityHealthIndicator> Indicators { get; init; } = [];
}

// ── Report root ───────────────────────────────────────────────────────────────

public sealed class ArtifactTraceabilityReport
{
    // Per-chain coverage stats
    public TraceabilityCoverageStats ConstitutionCoverage  { get; init; } = new();
    public TraceabilityCoverageStats SpecificationCoverage { get; init; } = new();
    public TraceabilityCoverageStats PlanCoverage          { get; init; } = new();
    public TraceabilityCoverageStats TaskCoverage          { get; init; } = new();

    // Per-chain drill-down (used by the chain tabs)
    public List<ChainCoverage> ConstitutionToSpec { get; init; } = [];
    public List<ChainCoverage> SpecToPlan         { get; init; } = [];
    public List<ChainCoverage> PlanToTask         { get; init; } = [];

    // Consolidated gap list (used by the Gaps tab)
    public List<TraceabilityGap> Gaps { get; init; } = [];

    // Full end-to-end matrix (used by the Matrix tab)
    public List<TraceabilityMatrixRow> Matrix { get; init; } = [];

    // Overall health
    public TraceabilityHealth Health { get; init; } = new();

    // Which artifacts were loaded (for empty-state rendering)
    public bool HasConstitution  { get; init; }
    public bool HasSpecification { get; init; }
    public bool HasPlan          { get; init; }
    public bool HasTasks         { get; init; }
    public int ReferenceOnlyConstitutionEntries { get; init; }
    /// <summary>The traceability policy the report was evaluated against (None unless explicitly configured).</summary>
    public TraceabilityPolicy Policy { get; init; } = TraceabilityPolicy.None;
    /// <summary>Missing references a configured policy requires — separate from the informational missing explicit references.</summary>
    public int PolicyRequiredGapCount => Gaps.Count(g => g.PolicyRequired);
}
