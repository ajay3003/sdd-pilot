namespace BirkNext.Web.Services.Engine;

/// <summary>
/// A single finding produced by a rule pack.
/// Uses string severity/status so the engine stays decoupled from any
/// specific domain enum. Services map these strings to their own enums.
/// </summary>
public sealed class RuleFinding
{
    public string  RulePackId     { get; init; } = string.Empty;
    public string  RuleId         { get; init; } = string.Empty;
    public string  Category       { get; init; } = string.Empty;
    public string  Title          { get; init; } = string.Empty;
    public string  Description    { get; init; } = string.Empty;

    /// <summary>One of: Critical | High | Medium | Low | Info</summary>
    public string  Severity       { get; init; } = "Medium";

    /// <summary>One of: Passed | Warning | Failed | NotApplicable</summary>
    public string  Status         { get; init; } = "Failed";

    public string? Evidence       { get; init; }
    public string  Recommendation { get; init; } = string.Empty;

    /// <summary>
    /// Optional reference to a specific item this finding concerns,
    /// e.g. a rule ID ("PP-01"), ADR identifier ("ADR-01"), or artifact path.
    /// </summary>
    public string? AffectedItem   { get; init; }
}
