namespace BirkNext.Web.Services.Engine;

/// <summary>
/// Represents a coverage gap detected by a rule pack.
/// Not every finding has an associated gap; rule packs emit gaps explicitly
/// when a finding represents missing coverage rather than a quality violation.
/// </summary>
public sealed class RuleGap
{
    public string  GapArea     { get; init; } = string.Empty;
    public string  Description { get; init; } = string.Empty;
    public string? ItemId      { get; init; }
    public string? ItemTitle   { get; init; }

    /// <summary>One of: Critical | High | Medium | Low</summary>
    public string  Severity    { get; init; } = "Medium";
}
