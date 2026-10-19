using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;

namespace BirkNext.Web.Services;

public sealed record ObservabilityChange(string Kind, string Area, string Subject, string Detail);

/// <summary>
/// Presentation helpers for Source Analysis → Observability, computed from the stored snapshot only (nothing is re-analyzed). Labels are
/// the model's own source states — never Pass/Fail, never a percentage or score.
/// </summary>
public static class SourceObservabilityPresentation
{
    public const string Boundary = "Runtime telemetry not assessed — states describe correlation, logging and telemetry evidence in source.";
    public const string CardLimitation = "Runtime telemetry not assessed";
    public static readonly IReadOnlyList<string> Provides = ["Correlation boundaries", "Tracing", "Logging quality", "Telemetry configuration"];

    public static readonly ObservabilityCategory[] CorrelationCategories = [ObservabilityCategory.Correlation, ObservabilityCategory.Tracing, ObservabilityCategory.Telemetry];
    public static readonly ObservabilityCategory[] LoggingCategories =
        Enum.GetValues<ObservabilityCategory>().Where(c => c is not (ObservabilityCategory.Correlation or ObservabilityCategory.Tracing or ObservabilityCategory.Telemetry)).ToArray();

    public static string StatusLabel(ArchitectureStatus status) => SourceAnalysisOverview.StatusLabel(status);

    /// <summary>One concept group of observability dimensions (presentation only: the analyzer's dimensions and states are unchanged).</summary>
    public sealed record DimensionGroup(string Key, string Title, string Description, IReadOnlyList<ObservabilityDimension> Dimensions);

    // Each analyzer dimension id belongs to exactly one group. Developer tests exercise correlation, tracing AND logging, so they are their own
    // group rather than being filed under telemetry configuration; an id the analyzer adds later lands in "Other" instead of disappearing.
    private static readonly (string Key, string Title, string Description, string[] Ids)[] GroupDefinitions =
    [
        ("tracing", "Tracing", "Tracing registration, correlation propagation across boundaries, service name / role and sampling.", ["tracing", "propagation", "service-name", "sampling"]),
        ("logging", "Logging", "How log calls are written, whether they carry correlation context, and logging in message handlers.", ["structured", "context", "handlers"]),
        ("errors", "Error observability", "Whether failures stay visible: exceptions passed to logs, swallowed exceptions, retry and dead-letter paths.", ["exceptions", "swallow", "retry"]),
        ("data-safety", "Data safety", "Sensitive-named values or payloads passed to logs, and redaction or masking support.", ["sensitive", "redaction"]),
        ("telemetry", "Telemetry", "Telemetry exporters and log sinks registered in source (configured ≠ delivered).", ["exporters"]),
        ("tests", "Developer tests", "Test files that exercise correlation, tracing or logging. They support every group above; their results are not known here.", ["tests"]),
    ];

    public static IReadOnlyList<DimensionGroup> Groups(IReadOnlyList<ObservabilityDimension> dimensions)
    {
        var groups = GroupDefinitions.Select(g => new DimensionGroup(g.Key, g.Title, g.Description, dimensions.Where(d => g.Ids.Contains(d.Id)).ToList()))
            .Where(g => g.Dimensions.Count > 0).ToList();
        var known = GroupDefinitions.SelectMany(g => g.Ids).ToHashSet(StringComparer.Ordinal);
        var other = dimensions.Where(d => !known.Contains(d.Id)).ToList();
        if (other.Count > 0) groups.Add(new("other", "Other", "Further dimensions reported by the analyzer.", other));
        return groups;
    }

    /// <summary>State counts over some dimensions, in a fixed reading order; zero counts are left out.</summary>
    public static IReadOnlyList<(ObservabilityDimensionState State, int Count)> StateCounts(IEnumerable<ObservabilityDimension> dimensions)
    {
        var list = dimensions.ToList();
        return new[] { ObservabilityDimensionState.Detected, ObservabilityDimensionState.StronglySupported, ObservabilityDimensionState.Partial, ObservabilityDimensionState.NeedsReview,
                ObservabilityDimensionState.NotFound, ObservabilityDimensionState.NotAssessed }
            .Select(s => (s, list.Count(d => d.State == s))).Where(x => x.Item2 > 0).ToList();
    }

    /// <summary>"2 detected · 1 partial · 1 not found" for a group heading.</summary>
    public static string StateSummary(IEnumerable<ObservabilityDimension> dimensions) =>
        string.Join(" · ", StateCounts(dimensions).Select(x => $"{x.Count} {ObservabilitySnapshot.Label(x.State).ToLowerInvariant()}"));

    public static string ComponentName(SourceObservabilitySnapshot s, string? id) => id is null ? "Snapshot-wide"
        : s.Components.FirstOrDefault(c => c.ComponentId == id)?.Name ?? (id.IndexOf(':') is var i and >= 0 ? id[(i + 1)..] : id);

    /// <summary>Findings that ask for attention (Needs review or higher; observations and limitations excluded).</summary>
    public static int NeedsReview(SourceObservabilitySnapshot s) =>
        s.Findings.Where(f => f.Kind is ObservabilityFindingKind.Finding or ObservabilityFindingKind.Unresolved && f.Severity >= ObservabilitySeverity.NeedsReview).Sum(f => f.Occurrences);

    public static int LogCalls(SourceObservabilitySnapshot s) => s.Logging.StructuredCalls + s.Logging.InterpolatedCalls + s.Logging.ConcatenatedCalls;

    public static int Inferred(SourceObservabilitySnapshot s) => s.Boundaries.Count(b => b.Propagation == PropagationState.PropagationInferred);

    public static string KindLabel(ObservabilityFindingKind kind) => kind switch
    {
        ObservabilityFindingKind.Observation => "Source observation",
        ObservabilityFindingKind.Finding => "Source finding",
        ObservabilityFindingKind.Limitation => "Limitation",
        _ => "Unresolved evidence",
    };

    public static string Tone(ObservabilityDimensionState state) => state switch
    {
        ObservabilityDimensionState.StronglySupported or ObservabilityDimensionState.Detected => "complete",
        ObservabilityDimensionState.Partial or ObservabilityDimensionState.NeedsReview => "partial",
        _ => "muted",
    };

    public static string Tone(PropagationState state) => state switch
    {
        PropagationState.FrameworkInstrumentation or PropagationState.PropagationConfigured or PropagationState.PropagationExplicit => "complete",
        PropagationState.PropagationInferred => "muted",
        _ => "partial",
    };

    public static string Tone(ObservabilitySeverity severity) => severity switch
    {
        ObservabilitySeverity.HighPriority or ObservabilitySeverity.Warning => "attention",
        ObservabilitySeverity.NeedsReview => "partial",
        _ => "muted",
    };

    public static IEnumerable<ObservabilityFinding> Filter(IEnumerable<ObservabilityFinding> findings, string category, string severity, string kind, string component, string search) =>
        findings.Where(f => (category == "" || f.Category.ToString() == category) && (severity == "" || f.Severity.ToString() == severity) && (kind == "" || f.Kind.ToString() == kind)
            && (component == "" || (f.Component ?? "") == component)
            && (search == "" || $"{f.Title} {f.Detail} {f.Technology} {string.Join(' ', f.Evidence.Select(e => $"{e.File} {e.Symbol} {e.Pattern}"))}".Contains(search, StringComparison.OrdinalIgnoreCase)));

    public static IEnumerable<CorrelationBoundary> Filter(IEnumerable<CorrelationBoundary> boundaries, string type, string propagation, string component) =>
        boundaries.Where(b => (type == "" || b.Type.ToString() == type) && (propagation == "" || b.Propagation.ToString() == propagation) && (component == "" || b.Component == component));

    /// <summary>Source comparison between two Observability analyses: findings that appear/disappear and boundaries whose propagation state changed.</summary>
    public static List<ObservabilityChange> Compare(SourceObservabilitySnapshot previous, SourceObservabilitySnapshot current)
        => SourceObservabilityComparison.Compare(previous, current).Select(c => new ObservabilityChange(c.Kind, c.Area, c.Subject, c.Detail)).ToList();
}
