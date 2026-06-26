namespace BirkNext.Web.Models;

// ── Enums ────────────────────────────────────────────────────────────────────

public enum ComplianceStatus
{
    Compliant,
    Partial,
    Missing,
    Violation,
    Unknown,
}

public enum ViolationSeverity
{
    Critical,
    High,
    Medium,
    Low,
}

// ── Per-rule result ───────────────────────────────────────────────────────────

public sealed class ComplianceResult
{
    public string NodeId { get; init; } = Guid.NewGuid().ToString()[..10];
    public string RuleId    { get; init; } = string.Empty;
    public string RuleTitle { get; init; } = string.Empty;
    public ConstitutionRuleType RuleType { get; init; }
    public ComplianceStatus Status { get; init; }

    // Evidence per artifact
    public bool HasSpecCoverage  { get; init; }
    public bool HasPlanCoverage  { get; init; }
    public bool HasTaskCoverage  { get; init; }

    // What mentioned this rule
    public List<string> SpecReferences  { get; init; } = [];
    public List<string> PlanReferences  { get; init; } = [];
    public List<string> TaskReferences  { get; init; } = [];
}

// ── Violation ─────────────────────────────────────────────────────────────────

public sealed class ComplianceViolation
{
    public string NodeId { get; init; } = Guid.NewGuid().ToString()[..10];
    public string RuleId    { get; init; } = string.Empty;
    public string RuleTitle { get; init; } = string.Empty;
    public ArtifactType Artifact  { get; init; }
    public string Issue           { get; init; } = string.Empty;
    public ViolationSeverity Severity { get; init; }
    public string? Evidence       { get; init; }
}

// ── Gap (rule with no coverage in one or more artifacts) ────────────────────

public sealed class ComplianceGap
{
    public string NodeId { get; init; } = Guid.NewGuid().ToString()[..10];
    public string RuleId       { get; init; } = string.Empty;
    public string RuleTitle    { get; init; } = string.Empty;
    public ConstitutionRuleType RuleType { get; init; }
    public bool MissingInSpec  { get; init; }
    public bool MissingInPlan  { get; init; }
    public bool MissingInTasks { get; init; }
    public ViolationSeverity Severity { get; init; }

    public string MissingSummary
    {
        get
        {
            var parts = new List<string>();
            if (MissingInSpec)  parts.Add("Specification");
            if (MissingInPlan)  parts.Add("Plan");
            if (MissingInTasks) parts.Add("Tasks");
            return parts.Count > 0 ? string.Join(", ", parts) : "None";
        }
    }
}

// ── Recommendation ───────────────────────────────────────────────────────────

public sealed class ComplianceRecommendation
{
    public string NodeId { get; init; } = Guid.NewGuid().ToString()[..10];
    public string RuleId   { get; init; } = string.Empty;
    public string Text     { get; init; } = string.Empty;
    public ArtifactType TargetArtifact { get; init; }
    public ViolationSeverity Priority  { get; init; }
}

// ── Coverage stats ────────────────────────────────────────────────────────────

public sealed class ComplianceCoverage
{
    public int TotalItems     { get; init; }
    public int CompliantItems { get; init; }
    public int PartialItems   { get; init; }
    public int MissingItems   { get; init; }
    public int ViolationItems { get; init; }

    public double CompliancePercentage =>
        TotalItems > 0
            ? Math.Round((double)(CompliantItems + PartialItems) / TotalItems * 100.0, 1)
            : 0.0;
}

// ── Health ────────────────────────────────────────────────────────────────────

public sealed class ComplianceHealthIndicator
{
    public string Icon    { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ComplianceHealthLevel Level { get; init; }
}

public enum ComplianceHealthLevel { Good, Warning, Error }

public sealed class ComplianceHealth
{
    public int TotalRules      { get; init; }
    public int CoveredRules    { get; init; }
    public int PartialRules    { get; init; }
    public int MissingRules    { get; init; }
    public int ViolationCount  { get; init; }
    public double CompliancePercentage { get; init; }
    public List<ComplianceHealthIndicator> Indicators { get; init; } = [];
}

// ── Report root ───────────────────────────────────────────────────────────────

public sealed class ConstitutionComplianceReport
{
    // One ComplianceResult per constitution rule
    public List<ComplianceResult>        Results         { get; init; } = [];
    public List<ComplianceViolation>     Violations      { get; init; } = [];
    public List<ComplianceGap>           Gaps            { get; init; } = [];
    public List<ComplianceRecommendation> Recommendations { get; init; } = [];
    public ComplianceCoverage            Coverage        { get; init; } = new();
    public ComplianceHealth              Health          { get; init; } = new();

    // Which artifacts were provided
    public bool HasConstitution  { get; init; }
    public bool HasSpecification { get; init; }
    public bool HasPlan          { get; init; }
    public bool HasTasks         { get; init; }
}
