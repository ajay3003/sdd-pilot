using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// A source snapshot's analysis outcome — the shared <see cref="SourceSetupOutcome.Overall"/> over the areas the package contains, the same rule
/// the Snapshot Registry, Relationship Review and every snapshot picker use (never a page-local definition, never the code analyzer's own
/// status). <see cref="NotAnalyzed"/>: no snapshot (or its areas could not be read); <see cref="NothingAnalyzable"/>: no area present.
/// </summary>
public enum SourceAnalysisBucket { Analyzed, Partial, Failed, NothingAnalyzable, NotAnalyzed, NotImported }

/// <summary>One analysis area of a snapshot as a chip: "CI/CD · Partial". The tone is never the only signal — the text says the state. An
/// Unsupported area is a BirkNext analyzer limitation, not a defect of the source.</summary>
public sealed record SourceAnalysisChip(string Dimension, string Status)
{
    public string StatusText => Status switch { "Complete" => "Analyzed", "Partial" => "Partial", "Failed" => "Failed", "Unsupported" => "Not supported", var s => s };
    public string Tone => Status switch { "Complete" => "ok", "Partial" => "partial", "Failed" => "failed", _ => "unsupported" };

    public string Meaning => Status switch
    {
        "Complete" => "Analyzed completely.",
        "Partial" => "Analyzed in part — some files or constructs could not be read; Source Analysis lists them.",
        "Failed" => "Present but could not be analyzed.",
        "Unsupported" => "Present, but BirkNext cannot analyze this technology yet (a BirkNext limitation, not a defect of the source).",
        _ => Status,
    };
}

/// <summary>
/// The facts one Source Management row shows, read once from the shared records (import record, Git link, Project Evidence, Target
/// Environments). Filtering, sorting and the summary are computed from these, so the summary always counts what the list shows.
/// </summary>
public sealed record SourceRowFacts(
    string Key, string Name, string Origin, bool Imported, IReadOnlyList<string> Capabilities, IReadOnlyList<SourceAnalysisChip> Analysis,
    SourceAnalysisBucket Bucket, string? AnalysisNote, bool NeedsReview, string? ReviewReason, IReadOnlyList<string> ConfirmedRoles,
    IReadOnlyList<(string Application, string Environment)> Applications, DateTimeOffset? UpdatedAt, Guid? SnapshotId, string? Commit);

/// <summary>The list's filters. Empty strings mean "all".</summary>
public sealed record SourceListFilter(string Search = "", string Origin = "", string Capability = "", string Analysis = "", bool ReviewOnly = false, string Sort = "name")
{
    public bool IsActive => Search.Length > 0 || Origin.Length > 0 || Capability.Length > 0 || Analysis.Length > 0 || ReviewOnly;
}

/// <summary>Summary over the sources. <see cref="Applications"/> counts distinct applications (several sources can serve one application).
/// <see cref="NotAnalyzed"/> includes sources with nothing analyzable and linked repositories not imported yet.</summary>
public sealed record SourceSummary(int Total, int Zip, int Git, int NeedsReview, int Analyzed, int Partial, int Failed, int NotAnalyzed, int Applications);

public static class SourceManagementPresentation
{
    /// <summary>The areas present in the snapshot (not detected / not run are not shown).</summary>
    public static IReadOnlyList<SourceAnalysisChip> Chips(IReadOnlyList<SourceSetupArea>? areas) =>
        areas is null ? [] : [.. areas.Where(a => a.Status is not ("NotDetected" or "NotAnalyzed")).Select(a => new SourceAnalysisChip(a.Area, a.Status))];

    public static SourceAnalysisBucket Bucket(bool imported, IReadOnlyList<SourceSetupArea>? areas) =>
        !imported ? SourceAnalysisBucket.NotImported
        : areas is null ? SourceAnalysisBucket.NotAnalyzed
        : SourceSetupOutcome.Overall(areas) switch
        {
            BirkNext.Snapshots.SnapshotProcessingStatus.Completed => SourceAnalysisBucket.Analyzed,
            BirkNext.Snapshots.SnapshotProcessingStatus.Failed => SourceAnalysisBucket.Failed,
            BirkNext.Snapshots.SnapshotProcessingStatus.Partial => SourceAnalysisBucket.Partial,
            _ => SourceAnalysisBucket.NothingAnalyzable,
        };

    public static string BucketLabel(SourceAnalysisBucket bucket) => bucket switch
    {
        SourceAnalysisBucket.Analyzed => "Analyzed",
        SourceAnalysisBucket.Partial => "Partly analyzed",
        SourceAnalysisBucket.Failed => "Analysis failed",
        SourceAnalysisBucket.NothingAnalyzable => "Nothing analyzable",
        SourceAnalysisBucket.NotAnalyzed => "Not analyzed",
        _ => "Not imported",
    };

    public static SourceSummary Summary(IReadOnlyList<SourceRowFacts> rows) => new(
        rows.Count,
        rows.Count(r => r.Origin == "ZIP"),
        rows.Count(r => r.Origin == "Git"),
        rows.Count(r => r.NeedsReview),
        rows.Count(r => r.Bucket == SourceAnalysisBucket.Analyzed),
        rows.Count(r => r.Bucket == SourceAnalysisBucket.Partial),
        rows.Count(r => r.Bucket == SourceAnalysisBucket.Failed),
        rows.Count(r => r.Bucket is SourceAnalysisBucket.NotAnalyzed or SourceAnalysisBucket.NotImported or SourceAnalysisBucket.NothingAnalyzable),
        rows.SelectMany(r => r.Applications.Select(a => a.Application)).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    /// <summary>Capabilities present on any row (the capability filter offers only these).</summary>
    public static IReadOnlyList<string> CapabilityOptions(IReadOnlyList<SourceRowFacts> rows) =>
        [.. rows.SelectMany(r => r.Capabilities).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    public static IReadOnlyList<SourceRowFacts> Apply(IReadOnlyList<SourceRowFacts> rows, SourceListFilter filter)
    {
        var search = filter.Search.Trim();
        var shown = rows.Where(r =>
            (search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            && (filter.Origin.Length == 0 || r.Origin == filter.Origin)
            && (filter.Capability.Length == 0 || r.Capabilities.Contains(filter.Capability, StringComparer.Ordinal))
            && (filter.Analysis.Length == 0 || r.Bucket.ToString() == filter.Analysis
                || filter.Analysis == nameof(SourceAnalysisBucket.NotAnalyzed) && r.Bucket is SourceAnalysisBucket.NotImported or SourceAnalysisBucket.NothingAnalyzable)
            && (!filter.ReviewOnly || r.NeedsReview));
        // Review-required rows first within every sort: an open decision is what the person has to act on.
        return filter.Sort switch
        {
            "updated" => [.. shown.OrderByDescending(r => r.NeedsReview).ThenByDescending(r => r.UpdatedAt ?? DateTimeOffset.MinValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)],
            "analysis" => [.. shown.OrderByDescending(r => r.NeedsReview).ThenByDescending(r => r.Bucket).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)],
            _ => [.. shown.OrderByDescending(r => r.NeedsReview).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)],
        };
    }

    /// <summary>"Updated 2026-11-05 14:02 UTC" or "Not imported yet" — invariant, never the browser culture.</summary>
    public static string Updated(DateTimeOffset? at) =>
        at is { } value ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC" : "Not imported yet";
}
