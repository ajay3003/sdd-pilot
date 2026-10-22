namespace BirkNext.Web.Services;

/// <summary>The separate status dimensions Source Analysis shows. They answer different questions, so they never share one look: a Partial
/// analysis, a Partial parser, an Inferred fact, an Unresolved target and a Needs-confirmation item are different facts. Source Analysis
/// describes what the source proves; runtime/deployment verification is its own dimension and is never assessed here.</summary>
public enum SourceStatusDimension
{
    /// <summary>How completely a whole analysis area ran over the snapshot (Complete, Partial, Not detected, Unsupported…).</summary>
    Completeness,
    /// <summary>How strongly the source evidence supports one extracted fact (Confirmed, Strongly supported, Inferred, Unresolved, Conflict).</summary>
    Confidence,
    /// <summary>What the analyzer observed for one dimension or boundary (Detected, Partial, Needs review, Not found…).</summary>
    Observation,
    /// <summary>How far an analyzer can read a technology or file (Supported, Partial, Unsupported).</summary>
    ParserSupport,
    /// <summary>Whether a detected relationship/reference reached its target (Resolved, Unresolved, Ambiguous, Conflict).</summary>
    Resolution,
    /// <summary>What a person should do with an item (No action, Needs review, Needs confirmation) — a prompt, never a defect.</summary>
    ReviewAction,
    /// <summary>Runtime/deployment verification (Not assessed here) — outside Source Analysis.</summary>
    Runtime,
}

/// <summary>
/// Presentation-only normalization of Source Analysis statuses. The domain models keep their own enums and labels; this maps the label a
/// page already shows to its dimension's tone and help text. Nothing here changes a state, and no state becomes Pass/Fail or a score.
/// </summary>
public static class SourceStatusPresentation
{
    public static string DimensionLabel(SourceStatusDimension d) => d switch
    {
        SourceStatusDimension.Completeness => "Analysis completeness",
        SourceStatusDimension.Confidence => "Evidence state",
        SourceStatusDimension.Observation => "Evidence observed",
        SourceStatusDimension.Resolution => "Resolution",
        SourceStatusDimension.ReviewAction => "Review action",
        SourceStatusDimension.Runtime => "Runtime verification",
        _ => "Parser coverage",
    };

    public static string DimensionHelp(SourceStatusDimension d) => d switch
    {
        SourceStatusDimension.Completeness => "Complete: the supported analysis ran over this source scope. Partial: useful evidence was produced, but some files, constructs or capabilities could not be fully analyzed — not a failure. Not assessed: outside analyzer capability or not enough evidence.",
        SourceStatusDimension.Confidence => "Confirmed / Detected in source: direct source evidence exists. Strongly supported: several consistent signals. Inferred: derived indirectly — not direct source proof. Not found in source: no supporting evidence in this snapshot, which does not prove absence at runtime.",
        SourceStatusDimension.Observation => "What the analyzer found for one observability dimension or boundary. Needs review is a prompt to look, not a defect; Not found means no evidence in this source — not that the capability is absent at runtime.",
        SourceStatusDimension.Resolution => "Resolved: the target was identified in source. Unresolved: the relationship was detected, but its endpoint, target or reference could not be identified. Ambiguous or Conflict: several candidates — none chosen.",
        SourceStatusDimension.ReviewAction => "Needs review: evidence exists and a human look is useful. Needs confirmation: a candidate or inference that should not be treated as authoritative until confirmed. Needs classification: the analyzer could not categorize it. None of these is an application defect.",
        SourceStatusDimension.Runtime => "Runtime and deployment state (deployed resources and databases, live messaging, runtime health or authorization) is not assessed by Source Analysis.",
        _ => "How far the analyzer could read a technology or file. Supported: read by a structured reader. Partial: heuristic reading, or some constructs (references, fragments) not resolved. Not a quality or compatibility verdict.",
    };

    /// <summary>The tone suffix (CSS) for one label in one dimension. Tones are per dimension, so the same word may look different across dimensions.</summary>
    public static string Tone(SourceStatusDimension dimension, string label) => dimension switch
    {
        SourceStatusDimension.Completeness => label switch
        {
            "Complete" or "Ready" or "Detected" => "positive",
            "Partial" or "Needs review" or "Review recommended" => "caution",
            "Analysis failed" or "Structural failure" => "critical",
            _ => "neutral",
        },
        SourceStatusDimension.Confidence => label switch
        {
            "Confirmed" or "Confirmed in source" or "Detected in source" => "positive",
            "Strongly supported" => "strong",
            "Inferred" => "inferred",
            "Conflict" or "Conflicts" => "critical",
            _ => "neutral",
        },
        SourceStatusDimension.Observation => label switch
        {
            "Detected" or "Strongly supported" or "Framework instrumentation detected" or "Propagation configured" or "Propagation explicit" => "positive",
            "Partial" or "Propagation inferred" => "partial",
            "Needs review" or "Conflicting" or "Unresolved" => "review",
            _ => "neutral",
        },
        SourceStatusDimension.Resolution => label switch
        {
            "Resolved" => "positive",
            "Unresolved" or "Ambiguous" => "review",
            "Conflict" => "critical",
            _ => "neutral",
        },
        SourceStatusDimension.ReviewAction => label switch
        {
            "Needs review" or "Needs confirmation" or "Needs classification" => "review",
            _ => "neutral",
        },
        SourceStatusDimension.Runtime => "neutral",
        _ => label switch { "Supported" or "Structured" => "supported", "Partial" or "Heuristic" => "partial", _ => "neutral" },
    };

    /// <summary>Short meaning of one label, used as the badge tooltip.</summary>
    public static string Meaning(SourceStatusDimension dimension, string label) => (dimension, label) switch
    {
        (SourceStatusDimension.Completeness, "Partial") => "Analysis completeness: some supported sources were only partly readable.",
        (SourceStatusDimension.Completeness, "Needs review") => "Analysis completeness: the analysis ran, with unresolved evidence worth reviewing.",
        (SourceStatusDimension.Completeness, "Review recommended") => "Some items are candidates for review; nothing is a verdict.",
        (SourceStatusDimension.Confidence, "Strongly supported") => "Evidence state: several consistent source signals, no explicit declaration.",
        (SourceStatusDimension.Confidence, "Inferred") => "Evidence state: derived indirectly from conventions or names — not direct source proof.",
        (SourceStatusDimension.Confidence, "Confirmed" or "Confirmed in source") => "Evidence state: explicitly declared in source. Deployment and runtime are not verified.",
        (SourceStatusDimension.Confidence, "Detected in source") => "Evidence state: direct source evidence exists. Deployment and runtime are not verified.",
        (SourceStatusDimension.Confidence, "Not found in source") => "Evidence state: no supporting evidence in this snapshot — not proof of absence at runtime.",
        (SourceStatusDimension.Resolution, "Unresolved") => "Resolution: the relationship was detected, but its target could not be identified in this source.",
        (SourceStatusDimension.ReviewAction, "Needs confirmation") => "Review action: a candidate or inference — confirm it before treating it as authoritative.",
        (SourceStatusDimension.ReviewAction, "Needs classification") => "Review action: the analyzer could not categorize this item — a classification gap, not a defect.",
        (SourceStatusDimension.Runtime, _) => "Runtime verification: not assessed by Source Analysis.",
        (SourceStatusDimension.ParserSupport, "Heuristic") => "Parsing method: interpreted heuristically, not by a structured reader. Not a validity verdict.",
        (SourceStatusDimension.ParserSupport, "Structured") => "Parsing method: read by a supported structured reader. Not a runtime validation.",
        (SourceStatusDimension.Confidence, "Unresolved") => "Evidence state: the source names it, but its target could not be identified.",
        (SourceStatusDimension.ParserSupport, "Supported") => "Parser coverage: read completely by a structured reader. Not a quality verdict.",
        (SourceStatusDimension.ParserSupport, "Partial") => "Parser coverage: heuristic reading, or some constructs not resolved. Not a quality verdict.",
        (SourceStatusDimension.Observation, "Partial") => "Observation: found for part of the source only.",
        (SourceStatusDimension.Observation, "Needs review") => "Observation: worth a look — a review prompt, not a defect.",
        _ => $"{DimensionLabel(dimension)}: {label}",
    };
}
