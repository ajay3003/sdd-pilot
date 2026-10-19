namespace BirkNext.Web.Services;

/// <summary>The four separate status dimensions Source Analysis shows. They answer different questions, so they never share one look:
/// a Partial analysis, a Partial parser and a Needs-review observation are three different facts.</summary>
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
        SourceStatusDimension.Confidence => "Evidence confidence",
        SourceStatusDimension.Observation => "Observation",
        _ => "Parser coverage",
    };

    public static string DimensionHelp(SourceStatusDimension d) => d switch
    {
        SourceStatusDimension.Completeness => "How completely an analysis area ran over the selected snapshot. Partial means some supported sources could only be read in part — not that the project is incomplete.",
        SourceStatusDimension.Confidence => "How strongly the source supports one extracted fact. Confirmed: explicit declaration. Strongly supported: several consistent signals. Inferred: derived from conventions or names. Unresolved: the target could not be identified.",
        SourceStatusDimension.Observation => "What the analyzer found for one observability dimension or boundary. Needs review is a prompt to look, not a defect; Not found means no evidence in this source.",
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
            "Confirmed" => "positive",
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
        _ => label switch { "Supported" => "supported", "Partial" => "partial", _ => "neutral" },
    };

    /// <summary>Short meaning of one label, used as the badge tooltip.</summary>
    public static string Meaning(SourceStatusDimension dimension, string label) => (dimension, label) switch
    {
        (SourceStatusDimension.Completeness, "Partial") => "Analysis completeness: some supported sources were only partly readable.",
        (SourceStatusDimension.Completeness, "Needs review") => "Analysis completeness: the analysis ran, with unresolved evidence worth reviewing.",
        (SourceStatusDimension.Completeness, "Review recommended") => "Some items are candidates for review; nothing is a verdict.",
        (SourceStatusDimension.Confidence, "Strongly supported") => "Evidence confidence: several consistent source signals, no explicit declaration.",
        (SourceStatusDimension.Confidence, "Inferred") => "Evidence confidence: derived from conventions or names, not declared explicitly.",
        (SourceStatusDimension.Confidence, "Confirmed") => "Evidence confidence: explicitly declared in source.",
        (SourceStatusDimension.Confidence, "Unresolved") => "Evidence confidence: the source names it, but it could not be identified.",
        (SourceStatusDimension.ParserSupport, "Supported") => "Parser coverage: read completely by a structured reader. Not a quality verdict.",
        (SourceStatusDimension.ParserSupport, "Partial") => "Parser coverage: heuristic reading, or some constructs not resolved. Not a quality verdict.",
        (SourceStatusDimension.Observation, "Partial") => "Observation: found for part of the source only.",
        (SourceStatusDimension.Observation, "Needs review") => "Observation: worth a look — a review prompt, not a defect.",
        _ => $"{DimensionLabel(dimension)}: {label}",
    };
}
