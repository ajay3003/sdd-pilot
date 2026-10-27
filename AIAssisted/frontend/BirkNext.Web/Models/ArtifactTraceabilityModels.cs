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

/// <summary>
/// The one category of a reference observation. Only a broken reference, an ambiguous reference or a policy-required trace that is not
/// established is a traceability issue; "no explicit reference" is informational evidence and never a defect.
/// </summary>
public enum TraceabilityObservationCategory
{
    NoExplicitReference,
    /// <summary>An explicit reference to an id that does not exist in the selected document (e.g. a task citing FR-999).</summary>
    BrokenReference,
    /// <summary>An explicit reference that resolves to several deterministic candidates; no candidate is chosen automatically.</summary>
    AmbiguousReference,
    /// <summary>An explicit project/governance policy requires the relation and no explicit reference establishes it.</summary>
    PolicyRequiredTraceNotEstablished,
}

/// <summary>One document transition and whether it was assessed. There is no combined traceability score across transitions.</summary>
public sealed record TraceabilityTransition(string Key, string Name, bool Assessed, string Detail);

/// <summary>Reviewer-facing evidence labels. Absence of an explicit reference is never shown as "missing coverage" or "orphaned".</summary>
public static class TraceabilityLabels
{
    public const string Semantics = "This review shows explicit authored-document references and supported deterministic relationships. Absence of an explicit reference is informational unless project policy requires that relationship. This review does not verify implementation or test execution.";
    public const string PlanToTaskNotAssessed = "Plan phases contain descriptive action text but no stable Task IDs. Task-to-Requirement and Task-to-Success-Criterion references are reported separately.";
    public const string ImplicitApplicability = "Implicit applicability is not assessed. A Constitution rule may apply globally even when no explicit Specification reference exists.";
    public const string MatrixScope = "Matrix shows authored-document evidence only. Plan nodes are authored Plan locations, not source-code or implementation evidence. Plan→Task links are not assessed when stable Task IDs are unavailable.";
    public const string NoReferenceCell = "No explicit reference represented";

    public static string Evidence(TraceabilityStatus status, ArtifactType chain) => (status, chain) switch
    {
        (TraceabilityStatus.Covered, ArtifactType.Specification) => "Explicitly referenced",
        (TraceabilityStatus.Covered, ArtifactType.Task) => "Explicitly referenced",
        (TraceabilityStatus.Covered, _) => "Explicitly linked",
        (TraceabilityStatus.Partial, _) => "Partially linked",
        (TraceabilityStatus.Missing or TraceabilityStatus.Orphaned, _) => NoReference(chain),
        _ => status.ToString(),
    };

    /// <summary>"No explicit Specification reference" / "No explicit Plan reference" / "No explicit Requirement/SC reference".</summary>
    public static string NoReference(ArtifactType chain) => chain switch
    {
        ArtifactType.Constitution => "No explicit Specification reference",
        ArtifactType.Specification => "No explicit Plan reference",
        ArtifactType.Task => "No explicit Requirement/SC reference",
        _ => "No explicit reference",
    };

    /// <summary>The observation's category; observations stored before categories existed read as policy-required or no explicit reference.</summary>
    public static TraceabilityObservationCategory CategoryOf(TraceabilityGap gap) =>
        gap.Category ?? (gap.PolicyRequired ? TraceabilityObservationCategory.PolicyRequiredTraceNotEstablished : TraceabilityObservationCategory.NoExplicitReference);

    public static string Category(TraceabilityGap gap) => CategoryOf(gap) switch
    {
        TraceabilityObservationCategory.BrokenReference => "Broken reference",
        TraceabilityObservationCategory.AmbiguousReference => "Ambiguous reference",
        TraceabilityObservationCategory.PolicyRequiredTraceNotEstablished => "Policy-required trace not established",
        _ => NoReference(gap.GapIn),
    };

    /// <summary>State, separate from category: informational unless the reference is invalid, ambiguous or required by policy.</summary>
    public static string Significance(TraceabilityGap gap) => CategoryOf(gap) switch
    {
        TraceabilityObservationCategory.BrokenReference => "Invalid reference target",
        TraceabilityObservationCategory.AmbiguousReference => "Review required",
        TraceabilityObservationCategory.PolicyRequiredTraceNotEstablished => "Required trace not established",
        _ => "Informational",
    };

    /// <summary>Visual token: info (blue-grey) for no explicit reference, warning for ambiguous/policy-required, error only for a broken reference.</summary>
    public static string Tone(TraceabilityGap gap) => CategoryOf(gap) switch
    {
        TraceabilityObservationCategory.BrokenReference => "error",
        TraceabilityObservationCategory.AmbiguousReference or TraceabilityObservationCategory.PolicyRequiredTraceNotEstablished => "warning",
        _ => "info",
    };

    /// <summary>Readable matrix edge evidence; abbreviations are never the only label.</summary>
    public static string EdgeTone(string? edge) => edge switch
    {
        "Explicit" => "explicit",
        "Derived" => "explicit",
        "Not assessed" => "notassessed",
        _ => "info",
    };
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
    /// <summary>Observation category; null on observations stored before categories existed (read through <see cref="TraceabilityLabels.CategoryOf"/>).</summary>
    public TraceabilityObservationCategory? Category { get; init; }
    /// <summary>Provenance of a policy-required trace ("Required by: …").</summary>
    public string? RequiredBy    { get; init; }
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
    /// <summary>Policy-required traces not established — separate from the informational "no explicit reference" observations.</summary>
    public int PolicyRequiredGapCount => Gaps.Count(g => TraceabilityLabels.CategoryOf(g) == TraceabilityObservationCategory.PolicyRequiredTraceNotEstablished);

    /// <summary>Informational observations: items with no explicit document reference. Never a defect, readiness or quality count.</summary>
    public int NoExplicitReferenceCount => Gaps.Count(g => TraceabilityLabels.CategoryOf(g) == TraceabilityObservationCategory.NoExplicitReference);

    /// <summary>Traceability issues that may need review attention: broken, ambiguous and policy-required observations only.</summary>
    public int ReferenceIssueCount => Gaps.Count - NoExplicitReferenceCount;

    /// <summary>The four document transitions with their assessment state (Plan → Tasks fails safe as Not assessed).</summary>
    public IReadOnlyList<TraceabilityTransition> Transitions =>
    [
        Transition("const-spec", "Constitution → Specification", ConstitutionCoverage, HasConstitution && HasSpecification),
        Transition("spec-plan", "Specification → Plan", SpecificationCoverage, HasSpecification && HasPlan),
        PlanCoverage.HasApplicablePopulation
            ? Transition("plan-task", "Plan → Tasks", PlanCoverage, HasPlan && HasTasks)
            : new("plan-task", "Plan → Tasks", false, "No stable Plan→Task identity available"),
        Transition("task-ref", "Tasks → Requirement/SC", TaskCoverage, HasTasks && HasSpecification),
    ];

    public int AssessedTransitionCount => Transitions.Count(t => t.Assessed);

    private static TraceabilityTransition Transition(string key, string name, TraceabilityCoverageStats stats, bool available) =>
        stats.HasApplicablePopulation
            ? new(key, name, true, $"{stats.CoveredItems + stats.PartialItems} / {stats.TotalItems} explicitly referenced · {stats.CoveragePercentage:0.#}% explicit reference coverage")
            : new(key, name, false, available ? "No applicable items" : "Documents not loaded");
}
