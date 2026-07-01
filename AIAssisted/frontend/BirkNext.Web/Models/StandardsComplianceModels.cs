namespace BirkNext.Web.Models;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum CheckStatus
{
    Passed,
    Warning,
    Failed,
    NotApplicable,
}

public enum CheckSeverity
{
    High,
    Medium,
    Low,
}

// ── Rule-pack JSON models ─────────────────────────────────────────────────────

public sealed class RulePackIndexEntry
{
    public string StandardId  { get; set; } = string.Empty;
    public string Label       { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Path        { get; set; } = string.Empty;
}

public sealed class StandardRulePack
{
    public string             StandardId      { get; set; } = string.Empty;
    public string             StandardName    { get; set; } = string.Empty;
    public string             StandardVersion { get; set; } = string.Empty;
    public string             RulePackVersion { get; set; } = string.Empty;
    public string             LastUpdated     { get; set; } = string.Empty;
    public string             Description     { get; set; } = string.Empty;
    public List<StandardRule> Rules           { get; set; } = [];
}

public sealed class StandardRule
{
    public string       RuleId           { get; set; } = string.Empty;
    public string       Category         { get; set; } = string.Empty;
    public string       Title            { get; set; } = string.Empty;
    public string       Description      { get; set; } = string.Empty;
    public string       Severity         { get; set; } = "Medium";
    public List<string> RequiredSections { get; set; } = [];
    public List<string> RequiredKeywords { get; set; } = [];
    public List<string> OptionalKeywords { get; set; } = [];
    public string       EvidenceHint     { get; set; } = string.Empty;
    public string       Recommendation   { get; set; } = string.Empty;
}

// ── Load result ───────────────────────────────────────────────────────────────

public sealed record RulePackLoadResult(
    string            StandardId,
    string            PackPath,
    StandardRulePack? Pack,
    string?           Error);

// ── Per-rule result ───────────────────────────────────────────────────────────

public sealed class StandardCheckResult
{
    public string        RuleId         { get; init; } = string.Empty;
    public string        StandardId     { get; init; } = string.Empty;
    public string        Category       { get; init; } = string.Empty;
    public string        Title          { get; init; } = string.Empty;
    public string        Description    { get; init; } = string.Empty;
    public CheckSeverity Severity       { get; init; }
    public CheckStatus   Status         { get; init; }
    public string?       Evidence       { get; init; }
    public string        Recommendation { get; init; } = string.Empty;
}

// ── Per-standard summary ──────────────────────────────────────────────────────

public sealed class StandardsComplianceSummary
{
    public string  StandardId      { get; init; } = string.Empty;
    public string  StandardName    { get; init; } = string.Empty;
    public string  StandardVersion { get; init; } = string.Empty;
    public string  RulePackVersion { get; init; } = string.Empty;
    public string  LastUpdated     { get; init; } = string.Empty;
    public int     TotalChecks     { get; init; }
    public int     Passed          { get; init; }
    public int     Warnings        { get; init; }
    public int     Failed          { get; init; }
    public double  Score           { get; init; }
}

// ── Report root ───────────────────────────────────────────────────────────────

public sealed class StandardsComplianceReport
{
    public List<StandardCheckResult>        Results          { get; init; } = [];
    public List<StandardsComplianceSummary> Summaries        { get; init; } = [];
    public double                           OverallScore     { get; init; }
    public bool                             HasSpecification { get; init; }
    public bool                             HasConstitution  { get; init; }
    public bool                             HasPlan          { get; init; }
    public bool                             HasTasks         { get; init; }
    public DateTimeOffset                   CheckedAt        { get; init; }
}
