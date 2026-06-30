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
}

public enum TraceabilityHealthLevel
{
    Good,
    Warning,
    Error,
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
    public TraceabilityStatus Status { get; init; }   // Missing | Orphaned
    public string Description    { get; init; } = string.Empty;
    public GapSeverity Severity  { get; init; }
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

    public TraceabilityStatus Status { get; init; }
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
}
