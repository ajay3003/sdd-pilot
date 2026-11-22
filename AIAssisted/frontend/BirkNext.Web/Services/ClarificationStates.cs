using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>A clarification for testers. A documented answer is text in the specification; a resolution is recorded explicitly with an authoritative
/// reference. A documented answer is never promoted to a resolution automatically.</summary>
public enum ClarificationState { Unanswered, DocumentedAnswerFound, Resolved, Superseded }

public static class ClarificationStates
{
    /// <summary>The identity a clarification is recorded under in BirkNext: its own id, else a hash of the question.</summary>
    public static string Key(SemanticClarification item) => string.IsNullOrWhiteSpace(item.Id)
        ? "Q-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.Question)))[..12]
        : item.Id!;

    /// <summary>The state recorded in BirkNext for a clarification in the active requirement scope (scoped first, then unscoped).</summary>
    public static SddClarification? Recorded(SddLifecycleState lifecycle, SemanticClarification item)
    {
        var key = Key(item);
        return lifecycle.Questions.FirstOrDefault(x => x.Id == key && RequirementScopes.Matches(x.RequirementScope, lifecycle.ActiveRequirementScope))
            ?? lifecycle.Questions.FirstOrDefault(x => x.Id == key && x.RequirementScope is null && lifecycle.ActiveRequirementScope is null);
    }

    private static readonly System.Text.RegularExpressions.Regex NamedIds =
        new(@"\b(FR|US|SC)-?(\d{1,4})([a-z])?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The specification's clarifications for discovery: question, documented answer, the state recorded in BirkNext, and the ids they concern —
    /// those the parser linked from the answer plus those the question itself names (FR-012, US1, SC-003). Superseded clarifications are left out.
    /// </summary>
    public static List<BirkNext.TestCoverage.CoverageClarification> ForDiscovery(IEnumerable<SemanticClarification> items, SddLifecycleState lifecycle) =>
        [.. items.Where(i => !string.IsNullOrWhiteSpace(i.Question)).Select(i => (Item: i, Recorded: Recorded(lifecycle, i)))
            .Select(x => (x.Item, x.Recorded, State: Of(x.Item, x.Recorded)))
            .Where(x => x.State != ClarificationState.Superseded)
            .Select(x => new BirkNext.TestCoverage.CoverageClarification(Key(x.Item), x.Item.Question.Trim(), string.IsNullOrWhiteSpace(x.Item.Answer) ? null : x.Item.Answer.Trim(),
                x.State switch
                {
                    ClarificationState.Resolved => BirkNext.TestCoverage.CoverageClarification.Resolved,
                    ClarificationState.DocumentedAnswerFound => BirkNext.TestCoverage.CoverageClarification.Documented,
                    _ => BirkNext.TestCoverage.CoverageClarification.Open,
                },
                x.State == ClarificationState.Resolved ? x.Recorded?.Resolution : null,
                [.. x.Item.AffectedElements.Concat(NamedIds.Matches(x.Item.Question).Select(m => m.Groups[1].Value.ToUpperInvariant() == "FR"
                        ? BirkNext.Sdd.RequirementReferenceParser.Normalize($"FR-{m.Groups[2].Value}") + m.Groups[3].Value.ToUpperInvariant()
                        : m.Value.ToUpperInvariant()))
                    .Distinct(StringComparer.OrdinalIgnoreCase)]))];

    /// <summary>"Open", "Documented answer — confirm before testing", "Resolved" for a clarification shown on a test case.</summary>
    public static string CaseLabel(BirkNext.TestCoverage.CoverageClarification c) => c.State switch
    {
        BirkNext.TestCoverage.CoverageClarification.Resolved => "Resolved",
        BirkNext.TestCoverage.CoverageClarification.Documented => "Documented answer — confirm before testing",
        _ => "Open — the expected result may change",
    };

    public static ClarificationState Of(SemanticClarification item, SddClarification? recorded) => recorded?.Status switch
    {
        "Resolved" => ClarificationState.Resolved,
        "Superseded" => ClarificationState.Superseded,
        _ => string.IsNullOrWhiteSpace(item.Answer) ? ClarificationState.Unanswered : ClarificationState.DocumentedAnswerFound,
    };

    public static string Label(ClarificationState state) => state switch
    {
        ClarificationState.DocumentedAnswerFound => "Documented answer found — needs confirmation",
        ClarificationState.Resolved => "Resolved",
        ClarificationState.Superseded => "Superseded",
        _ => "Unanswered",
    };

    public static string? Help(ClarificationState state) => state switch
    {
        ClarificationState.DocumentedAnswerFound => "A documented answer was found, but no authoritative resolution has been recorded.",
        ClarificationState.Unanswered => "No answer is documented and no resolution has been recorded.",
        _ => null,
    };
}
