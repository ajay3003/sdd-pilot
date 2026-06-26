namespace BirkNext.Web.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum ReadinessStatus
{
    NotReady,       // 0–39
    NeedsWork,      // 40–64
    MostlyReady,    // 65–84
    Ready,          // 85–100
}

// ── Per-category score ────────────────────────────────────────────────────────

public sealed class ReadinessScore
{
    public string Category   { get; init; } = string.Empty;
    public double Score      { get; init; }    // 0–100
    public ReadinessStatus Status { get; init; }
    public bool IsAssessed   { get; init; }    // false when no artifact loaded for this category

    public List<string> Signals    { get; init; } = [];   // what is good
    public List<string> Weaknesses { get; init; } = [];   // what needs work
}

// ── Readiness gap (surface to user) ──────────────────────────────────────────

public sealed class ReadinessGap
{
    public string Category    { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ViolationSeverity Severity { get; init; }
}

// ── Prioritized recommendation ────────────────────────────────────────────────

public sealed class ReadinessRecommendation
{
    public string Category   { get; init; } = string.Empty;
    public string Text       { get; init; } = string.Empty;
    public ViolationSeverity Priority { get; init; }
    public ArtifactType TargetArtifact { get; init; }
}

// ── Readiness gate (binary pass/fail question) ────────────────────────────────

public sealed class ReadinessGate
{
    public string Name        { get; init; } = string.Empty;   // e.g. "Ready for Implementation"
    public string Question    { get; init; } = string.Empty;   // short question label
    public bool IsReady       { get; init; }
    public ReadinessStatus Status { get; init; }
    public string? BlockReason { get; init; }   // why it is blocked, if not ready
}

// ── Health snapshot (used by the health section) ──────────────────────────────

public sealed class ReadinessHealth
{
    public double SpecificationScore { get; init; }
    public double PlanScore          { get; init; }
    public double TaskScore          { get; init; }
    public double TraceabilityScore  { get; init; }
    public double ComplianceScore    { get; init; }
    public double OverallScore       { get; init; }
    public ReadinessStatus OverallStatus { get; init; }
}

// ── Report root ───────────────────────────────────────────────────────────────

public sealed class QAReadinessReport
{
    public double OverallScore         { get; init; }
    public ReadinessStatus OverallStatus { get; init; }

    public List<ReadinessScore>          Scores          { get; init; } = [];
    public List<ReadinessGap>            Gaps            { get; init; } = [];
    public List<ReadinessRecommendation> Recommendations { get; init; } = [];
    public List<ReadinessGate>           Gates           { get; init; } = [];
    public ReadinessHealth               Health          { get; init; } = new();

    // Which source artifacts were provided
    public bool HasConstitution  { get; init; }
    public bool HasSpecification { get; init; }
    public bool HasPlan          { get; init; }
    public bool HasTasks         { get; init; }
}
