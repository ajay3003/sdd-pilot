namespace BirkNext.Web.Services;

// Plain-string prefix match condition. No regex — no ReDoS surface.
// Evaluation is dispatched by ExtractionRuleEngine.EvaluateClassificationCondition (type switch).
public sealed record PrefixMatchCondition : ClassificationCondition
{
    public string Prefix { get; }

    public PrefixMatchCondition(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            throw new ArgumentException("Prefix must not be null or empty.", nameof(prefix));
        Prefix = prefix;
    }
}
