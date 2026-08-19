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
            var evidence = ExtractPositiveEvidence(lines, term);
            if (evidence is not null)
                return new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = rule.RuleId,
                    Category       = rule.Category,
                    Title          = rule.Title,
                    Description    = rule.Description,
                    Severity       = rule.Severity,
                    Status         = "Passed",
                    Evidence       = evidence,
                    Recommendation = string.Empty,
                };
        }
        foreach (var term in rule.OptionalKeywords)
        {
            var evidence = ExtractPositiveEvidence(lines, term);
            if (evidence is not null)
                return new RuleFinding
                {
                    RulePackId     = RulePackId,
                    RuleId         = rule.RuleId,
                    Category       = rule.Category,
                    Title          = rule.Title,
                    Description    = rule.Description,
                    Severity       = rule.Severity,
                    Status         = "Warning",
                    Evidence       = evidence,
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

    private static string? ExtractPositiveEvidence(string[] lines, string term)
    {
        var lower = term.ToLowerInvariant();

        // Find the first line containing the keyword that is not explicitly negated
        foreach (var line in lines)
        {
            var lineLower = line.ToLowerInvariant();
            if (lineLower.Contains(lower) && !IsNegatedContext(line, term))
            {
                var trimmed = line.Trim().TrimStart('#').Trim();
                return trimmed.Length > 120 ? trimmed[..120] + "…" : trimmed;
            }
        }

        return null;
    }

    private static bool IsNegatedContext(string line, string keyword)
    {
        var lineLower = line.ToLowerInvariant();
        var keywordLower = keyword.ToLowerInvariant();

        // Negation patterns: look for negation markers before the keyword
        var negationMarkers = new[]
        {
            "no " + keywordLower,
            "no explicit " + keywordLower,
            "not " + keywordLower,
            "does not " + keywordLower,
            "do not " + keywordLower,
            "doesn't " + keywordLower,
            "don't " + keywordLower,
            "does not provide " + keywordLower,
            "does not include " + keywordLower,
            "does not support " + keywordLower,
            "does not exist",
            "is not " + keywordLower,
            "is not available",
            "has not been " + keywordLower,
            "have not been " + keywordLower,
            "without " + keywordLower,
            "lacking " + keywordLower,
            "lacks " + keywordLower,
        };

        // Check for direct negation patterns
        foreach (var marker in negationMarkers)
        {
            if (lineLower.Contains(marker))
            {
                // Verify the keyword actually appears in the negated phrase
                var keywordIndex = lineLower.IndexOf(keywordLower);
                var markerIndex = lineLower.IndexOf(marker);
                if (keywordIndex >= markerIndex && keywordIndex < markerIndex + marker.Length)
                    return true;
            }
        }

        // Check for "does not ... <keyword>" pattern
        if (lineLower.Contains("does not") && lineLower.Contains(keywordLower))
        {
            var notIndex = lineLower.IndexOf("does not");
            var keywordIndex = lineLower.IndexOf(keywordLower);
            if (keywordIndex > notIndex && keywordIndex < notIndex + 100)
                return true;
        }

        // Check for "... is not <keyword>" pattern
        if (lineLower.Contains(" is not ") && lineLower.Contains(keywordLower))
        {
            var isNotIndex = lineLower.IndexOf(" is not ");
            var keywordIndex = lineLower.IndexOf(keywordLower);
            if (keywordIndex > isNotIndex && keywordIndex < isNotIndex + 50)
                return true;
        }

        return false;
    }
}
