using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>A clarification for testers. A documented answer is text in the specification; a resolution is recorded explicitly with an authoritative
/// reference. A documented answer is never promoted to a resolution automatically.</summary>
public enum ClarificationState { Unanswered, DocumentedAnswerFound, Resolved, Superseded }

public static class ClarificationStates
{
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
