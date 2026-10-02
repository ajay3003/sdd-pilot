using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation for Pipeline Review: severity and confidence stay separate pills (both with text), gate cells carry a symbol AND a word,
/// and `backtick` spans from the deterministic text become code. "Needs attention" — never a pipeline score.
/// </summary>
public static class PipelineReviewPresentation
{
    public static readonly PipelineFindingSeverity[] SeverityOrder = [PipelineFindingSeverity.High, PipelineFindingSeverity.Medium, PipelineFindingSeverity.Low, PipelineFindingSeverity.Info];

    public static string SeverityCss(PipelineFindingSeverity s) => s switch
    {
        PipelineFindingSeverity.High => "sd-pill sd-pill-attention",
        PipelineFindingSeverity.Medium => "sd-pill sd-pill-partial",
        _ => "sd-pill sd-pill-muted",
    };

    public static string StateCss(ArchitectureEvidenceState s) => s switch
    {
        ArchitectureEvidenceState.Confirmed => "sd-pill sd-pill-complete",
        ArchitectureEvidenceState.StronglySupported => "sd-pill sd-pill-complete",
        ArchitectureEvidenceState.Inferred => "sd-pill sd-pill-partial",
        _ => "sd-pill sd-pill-muted",
    };

    public static string Headline(PipelineReviewResult r)
    {
        var actionable = PipelineReviewScoring.QualityFindings(r.Findings).Where(f => f.Severity != PipelineFindingSeverity.Info).ToList();
        return actionable.Count == 0 ? "No delivery gap detected from source" : "Needs attention";
    }

    /// <summary>Quality findings by severity, then assessment gaps apart: what BirkNext could not establish is not a pipeline defect.</summary>
    public static string Counts(PipelineReviewResult r)
    {
        var quality = PipelineReviewScoring.QualityFindings(r.Findings).ToList();
        var gaps = PipelineReviewScoring.AssessmentGaps(r.Findings).Count();
        var text = string.Join(" · ", SeverityOrder.Select(s => (s, n: quality.Count(f => f.Severity == s))).Where(x => x.n > 0).Select(x => $"{x.n} {x.s}"));
        if (text.Length == 0) text = "No findings";
        return gaps == 0 ? text : $"{text} · {gaps} not assessable";
    }

    /// <summary>Symbol + word for a matrix cell (never colour alone).</summary>
    public static (string Symbol, string Text) Cell(GateState? state) => state switch
    {
        GateState.Gates => ("✓", "Gates"),
        GateState.Inherited => ("✓*", "Gates via promoted artifact"),
        GateState.Conditional => ("✓?", "Conditional"),
        GateState.SoftGate => ("~", "May not block"),
        GateState.After => ("→", "Runs after"),
        GateState.NotGating => ("○", "Runs, does not gate"),
        GateState.Unknown => ("?", "Not assessable"),
        _ => ("–", "Not detected"),
    };

    public static string GateCss(GateState state) => state switch
    {
        GateState.Gates or GateState.Inherited => "sd-pill sd-pill-complete",
        GateState.Conditional or GateState.SoftGate => "sd-pill sd-pill-partial",
        GateState.NotGating => "sd-pill sd-pill-attention",
        _ => "sd-pill sd-pill-muted",
    };

    public static string Section(FlowSection s) => s switch
    {
        FlowSection.PostDeployment => "Post-deployment",
        _ => s.ToString(),
    };

    /// <summary>Splits deterministic text into plain and `code` segments.</summary>
    public static IEnumerable<(string Text, bool Code)> Segments(string text)
    {
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) yield return (parts[i], i % 2 == 1);
    }

    public static string Location(PipelineEvidenceRef e) => e.Line > 0 ? $"{e.File}:{e.Line}" : e.File;

    public static string Utc(DateTimeOffset at) => $"{at.UtcDateTime:yyyy-MM-dd HH:mm} UTC";

    public static IEnumerable<(PipelineFindingSeverity Severity, List<PipelineReviewFinding> Findings)> BySeverity(IEnumerable<PipelineReviewFinding> findings, PipelineFindingCategory? category) =>
        SeverityOrder.Select(s => (s, findings.Where(f => f.Severity == s && (category is null || f.Category == category)).ToList())).Where(x => x.Item2.Count > 0);

    public static string Coverage(TriggerCoverage c) => c switch
    {
        TriggerCoverage.Yes => "Starts",
        TriggerCoverage.Partial => "Partly",
        TriggerCoverage.No => "Does not start",
        _ => "Unknown",
    };
}
