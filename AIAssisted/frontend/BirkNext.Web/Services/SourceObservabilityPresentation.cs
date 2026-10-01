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
    public const string Boundary = "Source-derived only — correlation and telemetry at runtime are not verified.";
    public const string CardLimitation = "Source-derived only · runtime telemetry not verified";
    public static readonly IReadOnlyList<string> Provides = ["Correlation boundaries", "Tracing", "Logging quality", "Telemetry configuration"];

    public static readonly ObservabilityCategory[] CorrelationCategories = [ObservabilityCategory.Correlation, ObservabilityCategory.Tracing, ObservabilityCategory.Telemetry];
    public static readonly ObservabilityCategory[] LoggingCategories =
        Enum.GetValues<ObservabilityCategory>().Where(c => c is not (ObservabilityCategory.Correlation or ObservabilityCategory.Tracing or ObservabilityCategory.Telemetry)).ToArray();

    public static string StatusLabel(ArchitectureStatus status) => SourceAnalysisOverview.StatusLabel(status);

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
    {
        var changes = new List<ObservabilityChange>();
        var before = previous.Findings.Where(f => f.Kind != ObservabilityFindingKind.Limitation).ToDictionary(f => f.Id, StringComparer.Ordinal);
        var after = current.Findings.Where(f => f.Kind != ObservabilityFindingKind.Limitation).ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var f in after.Values.Where(f => !before.ContainsKey(f.Id))) changes.Add(new("Added", ObservabilitySnapshot.Label(f.Category), ComponentName(current, f.Component), f.Title));
        foreach (var f in before.Values.Where(f => !after.ContainsKey(f.Id))) changes.Add(new("No longer found", ObservabilitySnapshot.Label(f.Category), ComponentName(previous, f.Component), f.Title));
        foreach (var f in after.Values.Where(f => before.TryGetValue(f.Id, out var p) && p.Occurrences != f.Occurrences))
            changes.Add(new("Changed", ObservabilitySnapshot.Label(f.Category), ComponentName(current, f.Component), $"{f.Title}: {before[f.Id].Occurrences} → {f.Occurrences} occurrence(s)"));
        var oldBoundaries = previous.Boundaries.ToDictionary(b => b.Id, StringComparer.Ordinal);
        foreach (var b in current.Boundaries)
        {
            if (!oldBoundaries.TryGetValue(b.Id, out var old)) changes.Add(new("Added", "Correlation boundary", ComponentName(current, b.Component), $"{ObservabilitySnapshot.Label(b.Type)} {b.Transport} · {ObservabilitySnapshot.Label(b.Propagation)}"));
            else if (old.Propagation != b.Propagation)
                changes.Add(new("Changed", "Correlation boundary", ComponentName(current, b.Component), $"{ObservabilitySnapshot.Label(b.Type)} {b.Transport}: {ObservabilitySnapshot.Label(old.Propagation)} → {ObservabilitySnapshot.Label(b.Propagation)}"));
        }
        foreach (var b in previous.Boundaries.Where(b => current.Boundaries.All(x => x.Id != b.Id)))
            changes.Add(new("No longer found", "Correlation boundary", ComponentName(previous, b.Component), $"{ObservabilitySnapshot.Label(b.Type)} {b.Transport}"));
        return changes.OrderBy(c => c.Kind, StringComparer.Ordinal).ThenBy(c => c.Area, StringComparer.Ordinal).ThenBy(c => c.Subject, StringComparer.Ordinal).ToList();
    }
}
