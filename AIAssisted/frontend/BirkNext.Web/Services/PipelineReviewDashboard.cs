using System.Text.RegularExpressions;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>Deterministic stage/job purpose, from the validation and deployment evidence the backend attached — never from a name alone.</summary>
public enum FlowNodeType { Build, Test, Security, Package, Deploy, Other }

public sealed record SummaryCard(string Key, string Label, string Value, string? Note, string Role);

public sealed record ReadinessItem(string Label, CapabilityStatus Status, string? Detail);

/// <summary>
/// Pure presentation of the Pipeline Review dashboard (status, source context, summary cards, readiness, flow node types, readable conditions).
/// Everything comes from the backend review and the selected Source Analysis snapshot; nothing here reads YAML or invents counts.
///   Needs refresh (outdated CI/CD evidence) ≠ N/A (no pipeline definition) ≠ Unsupported (provider not readable) ≠ Analysis failed (error).
/// </summary>
public static class PipelineReviewDashboard
{
    public const string NeedsRefresh = "NeedsReanalysis";
    public const string NoPipelines = "NoPipelines";
    public const string Reviewed = "Reviewed";

    /// <summary>Page status for the selected snapshot. Red (Danger) only when the CI/CD analysis itself failed.</summary>
    public static CapabilityStatus PageStatus(PipelineReviewResult? r, PipelineReviewSourceOption? source)
    {
        if (source?.CiCdStatus == nameof(SourceDomainStatus.FailedAnalysis)) return new("Analysis failed", "✕", CapabilityTone.Danger);
        if (r is null) return new("No evidence", "○", CapabilityTone.Neutral);
        return r.State switch
        {
            NeedsRefresh => new("Needs refresh", "↻", CapabilityTone.Caution),
            NoPipelines when source?.CiCdStatus == nameof(SourceDomainStatus.Unsupported) => new("Unsupported", "⊘", CapabilityTone.Neutral),
            NoPipelines => new("N/A", "–", CapabilityTone.Neutral),
            _ when IsPartial(r, source) => new("Partial", "◐", CapabilityTone.Caution),
            _ => new("Ready", "✓", CapabilityTone.Positive),
        };
    }

    /// <summary>Partial: reviewed, but part of the pipelines could not be read fully (pattern-based provider, unresolved templates,
    /// assessment gaps). Coverage, not quality. The CI/CD domain's own "Partial" is not used: it is the static-reader disclaimer every
    /// pipeline carries (expressions are never evaluated), already stated in the limitations.</summary>
    public static bool IsPartial(PipelineReviewResult r, PipelineReviewSourceOption? source) =>
        r.Pipelines.Any(p => !p.IsTemplate && p.Platform != PipelinePlatform.AzurePipelines)
        || r.UnresolvedTemplates.Count > 0
        || PipelineReviewScoring.AssessmentGaps(r.Findings).Any();

    /// <summary>Project/source name for the main UI: the repository display name, else the archive name without its extension.
    /// The raw archive name stays in Technical details.</summary>
    public static string SourceName(PipelineReviewSourceOption s) =>
        !string.IsNullOrWhiteSpace(s.Repository) ? s.Repository! : Path.GetFileNameWithoutExtension(s.ArchiveName) is { Length: > 0 } n ? n : s.ArchiveName;

    public static bool EvidenceCurrent(PipelineReviewSourceOption s) => s.CiCdAnalyzerVersion >= PipelineReviewText.RequiredCiCdVersion;

    public static string EvidenceLabel(PipelineReviewSourceOption s) => s.CiCdAnalyzerVersion <= 0 ? "No CI/CD evidence" : $"CI/CD evidence v{s.CiCdAnalyzerVersion}";

    /// <summary>One readable line per snapshot option: name · date · CI/CD evidence · latest.</summary>
    public static string OptionText(PipelineReviewSourceOption s, bool latest) =>
        string.Join(" · ", new[] { SourceName(s), PipelineReviewPresentation.Utc(s.AnalyzedAt), s.Status, EvidenceCurrent(s) ? null : "needs refresh", latest ? "latest" : null }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

    public static CapabilityStatus SourceStatus(string? status) => status switch
    {
        nameof(BirkNext.Integrations.SourceAnalysisStatus.Ready) => new("Ready", "✓", CapabilityTone.Positive),
        nameof(BirkNext.Integrations.SourceAnalysisStatus.Partial) => new("Partial", "◐", CapabilityTone.Caution),
        nameof(BirkNext.Integrations.SourceAnalysisStatus.Failed) => new("Failed", "✕", CapabilityTone.Danger),
        _ => new("Unknown", "○", CapabilityTone.Neutral),
    };

    public static CapabilityStatus EvidenceStatus(PipelineReviewSourceOption s) => EvidenceCurrent(s)
        ? new("Current", "✓", CapabilityTone.Positive)
        : new("Needs refresh", "↻", CapabilityTone.Caution);

    /// <summary>Optional Azure DevOps enrichment: never a prerequisite for the source review.</summary>
    public static CapabilityStatus MetadataStatus(bool requested, PipelineMetadataSummary? m) => !requested || m is null
        ? new("Not included", "○", CapabilityTone.Neutral)
        : m.State switch
        {
            "Available" => new("Included", "✓", CapabilityTone.Positive),
            "NotAuthorized" => new("Not authorized", "!", CapabilityTone.Caution),
            "Failed" => new("Unavailable", "○", CapabilityTone.Neutral),
            _ => new("Not configured", "○", CapabilityTone.Neutral),
        };

    public static IReadOnlyList<string> Providers(PipelineReviewResult r) =>
        r.Pipelines.Where(p => !p.IsTemplate).Select(p => SourceDomainText.Label(p.Platform)).Distinct().Order(StringComparer.Ordinal).ToList();

    /// <summary>Summary cards. Only a reviewed result has current counts; otherwise values are "—" (never 0), and the snapshot's own
    /// pipeline-file count is shown as historical when the older evidence has one.</summary>
    public static IReadOnlyList<SummaryCard> Cards(PipelineReviewResult? r, PipelineReviewSourceOption? source)
    {
        if (r is { State: Reviewed })
        {
            var runnable = r.Pipelines.Where(p => !p.IsTemplate).ToList();
            var providers = Providers(r);
            var gating = r.Tests.Count(t => t.Gates.Count > 0);
            var gaps = PipelineReviewScoring.QualityFindings(r.Findings).Count(f => f.Severity is not PipelineFindingSeverity.Info);
            return
            [
                new("provider", "Provider", providers.Count == 0 ? "—" : string.Join(", ", providers), runnable.Count == 0 ? null : $"{runnable.Count} pipeline{(runnable.Count == 1 ? "" : "s")}", "provider"),
                new("structure", "Stages · jobs", $"{runnable.Sum(p => p.Stages)} · {runnable.Sum(p => p.Jobs)}", $"{r.Pipelines.Count(p => p.IsTemplate)} templates", "structure"),
                new("gates", "Tests & checks", r.Tests.Count.ToString(), $"{gating} gate a deployment · defined, not executed", "gates"),
                new("artifacts", "Artifacts", r.Pipelines.SelectMany(p => p.Artifacts).Distinct(StringComparer.Ordinal).Count().ToString(), "named in definitions", "artifacts"),
                new("environments", "Environments", r.Environments.Count.ToString(), $"{r.Deployments.Count} deployments", "environments"),
                new("gaps", "Gaps", gaps.ToString(), gaps == 0 ? "none detected from source" : "need review", gaps == 0 ? "neutral" : "gaps"),
            ];
        }
        var historical = source is { Pipelines: > 0 } && r is { State: NeedsRefresh } ? $"{source.Pipelines}" : "—";
        return
        [
            new("provider", "Provider", "—", r?.State == NeedsRefresh ? "after refresh" : null, "provider"),
            new("structure", "Pipeline files", historical, historical == "—" ? null : $"historical · {EvidenceLabel(source!)}", "structure"),
            new("gates", "Tests & checks", "—", null, "gates"),
            new("artifacts", "Artifacts", "—", null, "artifacts"),
            new("environments", "Environments", "—", null, "environments"),
            new("gaps", "Gaps", "—", null, "neutral"),
        ];
    }

    /// <summary>Readiness checklist (status always has text). The overall line is <see cref="PageStatus"/>.</summary>
    public static IReadOnlyList<ReadinessItem> Readiness(PipelineReviewResult? r, PipelineReviewSourceOption? source, bool metadataRequested)
    {
        var ok = new CapabilityStatus("Yes", "✓", CapabilityTone.Positive);
        var items = new List<ReadinessItem>
        {
            new("Source snapshot", source is null ? new("Missing", "○", CapabilityTone.Neutral) : ok, source is null ? null : SourceName(source)),
        };
        if (source is null) return items;
        items.Add(new("Pipeline definitions detected",
            source.Pipelines > 0 ? ok : r?.State == NoPipelines ? new("None", "–", CapabilityTone.Neutral) : new("Unknown", "○", CapabilityTone.Neutral),
            source.Pipelines > 0 ? $"{source.Pipelines} pipeline file{(source.Pipelines == 1 ? "" : "s")}" : null));
        items.Add(new("CI/CD evidence current", EvidenceStatus(source),
            EvidenceCurrent(source) ? EvidenceLabel(source) : $"{EvidenceLabel(source)} · v{PipelineReviewText.RequiredCiCdVersion} required"));
        items.Add(new("Provider identified",
            r is { State: Reviewed } && Providers(r).Count > 0 ? ok : new("After refresh", "○", CapabilityTone.Neutral),
            r is { State: Reviewed } ? string.Join(", ", Providers(r)) : null));
        items.Add(new("Azure DevOps metadata (optional)", MetadataStatus(metadataRequested, r?.Metadata), null));
        return items;
    }

    /// <summary>Stage purpose from attached evidence: deployments, then validation categories, then published artifacts.</summary>
    public static FlowNodeType StageType(PipelineReviewResult r, PipelineGraphNode stage)
    {
        bool Same(string? name) => string.Equals(name, stage.Label, StringComparison.Ordinal);
        if (r.Deployments.Any(d => d.Pipeline == stage.PipelineId && Same(d.Stage))
            || r.Edges.Any(e => e.Kind == PipelineEdgeKind.DeploysTo && e.FromId == stage.Id)) return FlowNodeType.Deploy;
        var categories = r.Tests.Where(t => t.Pipeline == stage.PipelineId && Same(t.Stage)).Select(t => t.Category).ToHashSet();
        var tests = categories.Where(c => c is not (ValidationCategory.Build or ValidationCategory.InfrastructurePlan)).ToList();
        if (tests.Count > 0)
            return tests.All(c => c is ValidationCategory.Security or ValidationCategory.DependencyScan or ValidationCategory.StaticAnalysis) ? FlowNodeType.Security : FlowNodeType.Test;
        if (categories.Contains(ValidationCategory.Build)) return FlowNodeType.Build;
        if (r.Edges.Any(e => e.Kind == PipelineEdgeKind.Publishes && e.FromId == stage.Id)) return FlowNodeType.Package;
        return FlowNodeType.Other;
    }

    /// <summary>One validation step of a single-job pipeline; the backend lists a step once per category it matches, so they are merged.</summary>
    public sealed record ValidationStep(string Name, List<ValidationCategory> Categories, string? Condition, bool ContinueOnError, int Line);

    /// <summary>Validation steps of one pipeline in definition order, one entry per step (file + line + name), categories merged.</summary>
    public static List<ValidationStep> Steps(PipelineReviewResult r, string pipelineId) =>
        r.Tests.Where(t => t.Pipeline == pipelineId)
            .GroupBy(t => (t.Evidence.File, t.Evidence.Line, t.Name))
            .Select(g => new ValidationStep(g.Key.Name, g.Select(t => t.Category).Distinct().ToList(), g.First().Condition, g.Any(t => t.ContinueOnError), g.Key.Line))
            .OrderBy(s => s.Line).ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Type of a validation step from its categories: Build or Security only when every category is one; otherwise Test.</summary>
    public static FlowNodeType StepType(IReadOnlyCollection<ValidationCategory> categories) =>
        categories.Count > 0 && categories.All(c => c is ValidationCategory.Build or ValidationCategory.InfrastructurePlan) ? FlowNodeType.Build
        : categories.Count > 0 && categories.All(c => c is ValidationCategory.Security or ValidationCategory.DependencyScan or ValidationCategory.StaticAnalysis) ? FlowNodeType.Security
        : FlowNodeType.Test;

    public static string TypeLabel(FlowNodeType t) => t switch
    {
        FlowNodeType.Deploy => "Deploy",
        FlowNodeType.Security => "Security",
        FlowNodeType.Test => "Test",
        FlowNodeType.Build => "Build",
        FlowNodeType.Package => "Package",
        _ => "Other",
    };

    private static readonly Regex Variable = new(@"^variables(?:\[\s*'([^']+)'\s*\]|\.([A-Za-z0-9_.]+))$", RegexOptions.Compiled);

    /// <summary>
    /// Plain-language reading of a common Azure Pipelines condition ("succeeded()", "and(...)", "eq(variables['X'], 'true')"). Null when the
    /// expression uses anything else — the raw expression is then shown as is (never guessed).
    /// </summary>
    public static string? ReadableCondition(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return null;
        try
        {
            var pos = 0;
            var text = Parse(condition.Trim(), ref pos);
            return pos == condition.Trim().Length ? text : null;
        }
        catch (FormatException) { return null; }
    }

    private static string Parse(string s, ref int pos)
    {
        Skip(s, ref pos);
        var start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] is '_' or '.' or '[' or ']' or '\'' && Peek(s, start, pos))) pos++;
        var token = s[start..pos];
        Skip(s, ref pos);
        if (token.StartsWith('\'')) throw new FormatException();
        if (pos < s.Length && s[pos] == '(')
        {
            pos++;
            var args = new List<string>();
            Skip(s, ref pos);
            while (pos < s.Length && s[pos] != ')')
            {
                args.Add(s[pos] == '\'' ? Literal(s, ref pos) : Parse(s, ref pos));
                Skip(s, ref pos);
                if (pos < s.Length && s[pos] == ',') pos++;
                Skip(s, ref pos);
            }
            if (pos >= s.Length) throw new FormatException();
            pos++;
            return Function(token.ToLowerInvariant(), args);
        }
        if (Variable.Match(token) is { Success: true } m) return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (token.Equals("true", StringComparison.OrdinalIgnoreCase) || token.Equals("false", StringComparison.OrdinalIgnoreCase)) return token.ToLowerInvariant();
        throw new FormatException();
    }

    private static bool Peek(string s, int start, int pos) => s.AsSpan(start, pos - start).StartsWith("variables", StringComparison.Ordinal);

    private static string Literal(string s, ref int pos)
    {
        var end = s.IndexOf('\'', pos + 1);
        if (end < 0) throw new FormatException();
        var value = s[(pos + 1)..end];
        pos = end + 1;
        return $"'{value}'";
    }

    private static void Skip(string s, ref int pos) { while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++; }

    private static string Function(string name, List<string> a) => name switch
    {
        "succeeded" when a.Count == 0 => "previous steps succeeded",
        "failed" when a.Count == 0 => "a previous step failed",
        "succeededorfailed" when a.Count == 0 => "previous steps finished (even after a failure, unless cancelled)",
        "always" when a.Count == 0 => "always (even after a failure or cancellation)",
        "canceled" or "cancelled" when a.Count == 0 => "the run was cancelled",
        "and" when a.Count >= 2 => string.Join(" AND ", a),
        "or" when a.Count >= 2 => "(" + string.Join(" OR ", a.Select(x => x.Contains(" AND ", StringComparison.Ordinal) ? $"({x})" : x)) + ")",
        "not" when a.Count == 1 => $"NOT {a[0]}",
        "eq" when a.Count == 2 => $"{a[0]} is {a[1]}",
        "ne" when a.Count == 2 => $"{a[0]} is not {a[1]}",
        _ => throw new FormatException(),
    };
}
