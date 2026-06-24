namespace BirkNext.Web.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum ReadinessState
{
    Ready,
    MostlyReady,
    NotReady,
    Blocked,
}

public enum GateSeverity
{
    Critical,
    High,
    Medium,
    Low,
}

// ── Blocker (active issue preventing gate progression) ────────────────────────

public sealed class ReadinessBlocker
{
    public string NodeId      { get; init; } = Guid.NewGuid().ToString()[..10];
    public string Title       { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public GateSeverity Severity { get; init; }
    public string Category    { get; init; } = string.Empty;
    public string? Phase      { get; init; }    // "Development" | "Testing" | "Release" | null = all
    public string? RuleCode   { get; init; }    // source rule if applicable
}

// ── Gate decision (detailed per-gate assessment) ──────────────────────────────

public sealed class DeliveryGate
{
    public string Phase             { get; init; } = string.Empty;
    public ReadinessState State     { get; init; }
    public double Score             { get; init; }    // 0–100
    public List<string> PassedChecks { get; init; } = [];
    public List<string> FailedChecks { get; init; } = [];
    public List<ReadinessBlocker> Blockers { get; init; } = [];
}

// ── Simplified per-phase decision (summary view) ──────────────────────────────

public sealed class ReadinessDecision
{
    public string Name          { get; init; } = string.Empty;
    public ReadinessState State { get; init; }
    public double Score         { get; init; }
    public string? Summary      { get; init; }
}

// ── Recommendation ────────────────────────────────────────────────────────────

public sealed class DeliveryRecommendation
{
    public string NodeId      { get; init; } = Guid.NewGuid().ToString()[..10];
    public string Text        { get; init; } = string.Empty;
    public string Category    { get; init; } = string.Empty;
    public GateSeverity Priority { get; init; }
    public string? Phase      { get; init; }    // "Development" | "Testing" | "Release"
}

// ── Health snapshot ───────────────────────────────────────────────────────────

public sealed class DeliveryReadinessHealth
{
    public double DevelopmentScore      { get; init; }
    public double TestingScore          { get; init; }
    public double ReleaseScore          { get; init; }
    public double OverallReadinessScore { get; init; }
}

// ── Report root ───────────────────────────────────────────────────────────────

public sealed class DeliveryReadinessReport
{
    // Per-gate detail
    public DeliveryGate DevelopmentGate { get; init; } = new();
    public DeliveryGate TestingGate     { get; init; } = new();
    public DeliveryGate ReleaseGate     { get; init; } = new();

    // Simplified decisions for the overview
    public ReadinessDecision DevelopmentDecision { get; init; } = new();
    public ReadinessDecision TestingDecision      { get; init; } = new();
    public ReadinessDecision ReleaseDecision      { get; init; } = new();

    // Consolidated lists (de-duped, sorted by severity)
    public List<ReadinessBlocker>      Blockers        { get; init; } = [];
    public List<DeliveryRecommendation> Recommendations { get; init; } = [];

    public DeliveryReadinessHealth Health { get; init; } = new();

    public bool HasConstitution  { get; init; }
    public bool HasSpecification { get; init; }
    public bool HasPlan          { get; init; }
    public bool HasTasks         { get; init; }
}
