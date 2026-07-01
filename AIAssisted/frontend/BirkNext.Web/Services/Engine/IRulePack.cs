namespace BirkNext.Web.Services.Engine;

/// <summary>
/// A self-contained, pluggable set of deterministic rules.
/// Each rule pack is independent; the <see cref="RuleEngine"/> orchestrates them.
///
/// To add a new standard or quality dimension:
///   1. Implement this interface.
///   2. Add the instance to the list passed to <see cref="RuleEngine.Run"/>.
///   3. Done — no changes to existing pages or services.
/// </summary>
public interface IRulePack
{
    /// <summary>Stable identifier, e.g. "WCAG22", "qa-specification", "constitution-coverage".</summary>
    string RulePackId   { get; }

    /// <summary>Human-readable name shown in logs and error messages.</summary>
    string RulePackName { get; }

    /// <summary>
    /// Execute all rules in this pack against the provided context.
    /// Must not throw; return an error-flagged result instead.
    /// </summary>
    RulePackResult Execute(RuleContext context);
}
