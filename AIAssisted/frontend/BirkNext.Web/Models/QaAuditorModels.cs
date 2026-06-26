namespace BirkNext.Web.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum QaSeverity
{
    Critical,
    High,
    Medium,
    Low,
    Info,
}

public enum QaCategory
{
    Constitution,
    Specification,
    Plan,
    Task,
    Traceability,
    Compliance,
    Testing,
    Architecture,
}

// ── Finding (one rule violation detected by an audit rule) ────────────────────

public sealed class QaFinding
{
    public string NodeId         { get; init; } = Guid.NewGuid().ToString()[..10];
    public string RuleCode       { get; init; } = string.Empty;   // e.g. "SPEC-001"
    public string Title          { get; init; } = string.Empty;
    public string Description    { get; init; } = string.Empty;
    public QaSeverity Severity   { get; init; }
    public QaCategory Category   { get; init; }
    public string? AffectedArtifact { get; init; }   // e.g. "FR-001", "PP-02", "ADR-01"
}

// ── Risk (high/critical findings presented as delivery risks) ─────────────────

public sealed class QaRisk
{
    public string NodeId      { get; init; } = Guid.NewGuid().ToString()[..10];
    public string Title       { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public QaSeverity Severity { get; init; }
    public QaCategory Category { get; init; }
    public string? Mitigation  { get; init; }
    public string? RuleCode    { get; init; }
}

// ── Coverage gap ──────────────────────────────────────────────────────────────

public sealed class QaGap
{
    public string NodeId      { get; init; } = Guid.NewGuid().ToString()[..10];
    public string GapArea     { get; init; } = string.Empty;   // e.g. "Missing Plan Coverage"
    public string Description { get; init; } = string.Empty;
    public string? ItemId     { get; init; }
    public string? ItemTitle  { get; init; }
    public QaSeverity Severity { get; init; }
}

// ── Recommendation ────────────────────────────────────────────────────────────

public sealed class QaRecommendation
{
    public string NodeId         { get; init; } = Guid.NewGuid().ToString()[..10];
    public string Text           { get; init; } = string.Empty;
    public QaCategory Category   { get; init; }
    public QaSeverity Priority   { get; init; }
    public string? AffectedArtifact { get; init; }
    public string? RuleCode      { get; init; }
}

// ── Audit health / scoring ────────────────────────────────────────────────────

public sealed class QaAuditHealth
{
    public int TotalFindings    { get; init; }
    public int CriticalCount    { get; init; }
    public int HighCount        { get; init; }
    public int MediumCount      { get; init; }
    public int LowCount         { get; init; }
    public int InfoCount        { get; init; }
    public int CoverageGapCount { get; init; }
    public int ViolationCount   { get; init; }
    public double AuditScore    { get; init; }   // 0–100 (100 = no findings)
}

// ── Report root ───────────────────────────────────────────────────────────────

public sealed class QaAuditReport
{
    public List<QaFinding>        Findings        { get; init; } = [];
    public List<QaRisk>           Risks           { get; init; } = [];
    public List<QaGap>            Gaps            { get; init; } = [];
    public List<QaRecommendation> Recommendations { get; init; } = [];
    public QaAuditHealth          Health          { get; init; } = new();

    public bool HasConstitution  { get; init; }
    public bool HasSpecification { get; init; }
    public bool HasPlan          { get; init; }
    public bool HasTasks         { get; init; }
}
