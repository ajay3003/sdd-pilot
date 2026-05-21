using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

internal interface IExtractionRuleEngine
{
    /// <summary>
    /// Evaluates a single text block against the rule set.
    /// For the filter pass (Stage 4), pass <c>string.Empty</c> as <paramref name="strippedText"/>.
    /// For the classification pass (Stage 6), pass the markdown-stripped text from Stage 5.
    /// </summary>
    RuleEvaluationResult Evaluate(TextBlock block, string strippedText);

    /// <summary>
    /// All rule names in the engine: filter rules first, classification rules second,
    /// each group in priority-descending order.
    /// </summary>
    IReadOnlyList<string> RuleNames { get; }

    /// <summary>
    /// Plain-text prefixes whose matching content items are discarded at Stage 5.5 (US4).
    /// Empty when no ignore-prefix configuration is present.
    /// </summary>
    IReadOnlyList<string> IgnorePrefixes { get; }
}
