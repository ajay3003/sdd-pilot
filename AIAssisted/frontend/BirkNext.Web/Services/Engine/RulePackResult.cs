namespace BirkNext.Web.Services.Engine;

/// <summary>
/// The complete output of running one <see cref="IRulePack"/> against a <see cref="RuleContext"/>.
/// Contains all findings and coverage gaps produced by that pack.
/// </summary>
public sealed class RulePackResult
{
    public string            RulePackId   { get; init; } = string.Empty;
    public string            RulePackName { get; init; } = string.Empty;
    public List<RuleFinding> Findings     { get; init; } = [];
    public List<RuleGap>     Gaps         { get; init; } = [];

    /// <summary>
    /// Coverage score 0–100 computed by the rule pack.
    /// Uses the coverage formula for keyword-based packs (Standards).
    /// QA packs do not populate this — the QA service uses its own deduction model.
    /// </summary>
    public double            Score        { get; init; }

    /// <summary>Set when the rule pack failed to execute. Findings and Gaps will be empty.</summary>
    public string?           Error        { get; init; }
}
