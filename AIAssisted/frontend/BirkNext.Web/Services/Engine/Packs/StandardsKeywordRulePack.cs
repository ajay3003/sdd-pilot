using BirkNext.Web.Models;

namespace BirkNext.Web.Services.Engine.Packs;

/// <summary>
/// Wraps a JSON-loaded <see cref="StandardRulePack"/> and runs keyword-based
/// documentation coverage checks against <see cref="RuleContext.CombinedText"/>.
///
/// Scoring (per <see cref="RuleEngine.ComputeCoverageScore"/>):
///   RequiredKeywords match → Passed (strong evidence)
///   OptionalKeywords match → Warning (weak/incidental mention)
///   No match              → Failed (no documentation found)
///
/// One instance per standard (WCAG, OWASP, GDPR, ISO 25010).
/// Additional standards require only a new JSON rule pack + entry in index.json.
/// </summary>
public sealed class StandardsKeywordRulePack : IRulePack
{
    private readonly StandardRulePack _pack;

    public string RulePackId   => _pack.StandardId;
    public string RulePackName => _pack.StandardName;

    public StandardsKeywordRulePack(StandardRulePack pack)
    {
        _pack = pack;
    }

    public RulePackResult Execute(RuleContext context)
    {
        var text  = context.CombinedText;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lower = text.ToLowerInvariant();

        var findings = _pack.Rules
            .Select(rule => EvaluateRule(rule, lower, lines))
            .ToList();

        return new RulePackResult
        {
            RulePackId   = RulePackId,
            RulePackName = RulePackName,
            Findings     = findings,
            Score        = RuleEngine.ComputeCoverageScore(findings),
        };
    }

    private RuleFinding EvaluateRule(StandardRule rule, string lower, string[] lines)
    {
        foreach (var term in rule.RequiredKeywords)
        {
            if (lower.Contains(term.ToLowerInvariant()))
                return new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = rule.RuleId,
                    Category       = rule.Category,
                    Title          = rule.Title,
                    Description    = rule.Description,
                    Severity       = rule.Severity,
                    Status         = "Passed",
                    Evidence       = ExtractEvidence(lines, term),
                    Recommendation = string.Empty,
                };
        }
        foreach (var term in rule.OptionalKeywords)
        {
            if (lower.Contains(term.ToLowerInvariant()))
                return new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = rule.RuleId,
                    Category       = rule.Category,
                    Title          = rule.Title,
                    Description    = rule.Description,
                    Severity       = rule.Severity,
                    Status         = "Warning",
                    Evidence       = ExtractEvidence(lines, term),
                    Recommendation = rule.Recommendation,
                };
        }
        return new RuleFinding
        {
            RulePackId     = RulePackId,
            RuleId         = rule.RuleId,
            Category       = rule.Category,
            Title          = rule.Title,
            Description    = rule.Description,
            Severity       = rule.Severity,
            Status         = "Failed",
            Recommendation = rule.Recommendation,
        };
    }

    private static string? ExtractEvidence(string[] lines, string term)
    {
        var lower = term.ToLowerInvariant();
        var match = lines.FirstOrDefault(l => l.ToLowerInvariant().Contains(lower));
        if (match is null) return null;
        var trimmed = match.Trim().TrimStart('#').Trim();
        return trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
    }
}
