using BirkNext.Web.Services.ProjectEvidence;

namespace BirkNext.Web.Services;

/// <summary>How far a source's contained evidence was analyzed (from Project Evidence capabilities, never the snapshot's code-analyzer outcome).</summary>
public enum SourceAnalysisBucket { Analyzed, Partial, NotAnalyzed, NotImported }

/// <summary>One analysis dimension of a source as a chip: "Docs · Analyzed". The tone is never the only signal — the text says the state.</summary>
public sealed record SourceAnalysisChip(string Dimension, PackageCapabilityStatus Status)
{
    public string StatusText => Status switch { PackageCapabilityStatus.Ready => "Analyzed", PackageCapabilityStatus.Partial => "Partial", _ => "Failed" };
    public string Tone => Status switch { PackageCapabilityStatus.Ready => "ok", PackageCapabilityStatus.Partial => "partial", _ => "failed" };
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

/// <summary>Summary over the sources. <see cref="Applications"/> counts distinct applications (several sources can serve one application).</summary>
public sealed record SourceSummary(int Total, int Zip, int Git, int NeedsReview, int Analyzed, int Partial, int NotAnalyzed, int Applications);

public static class SourceManagementPresentation
{
    public static SourceAnalysisBucket Bucket(bool imported, IReadOnlyList<SourceAnalysisChip> analysis) =>
        !imported ? SourceAnalysisBucket.NotImported
        : analysis.Count == 0 ? SourceAnalysisBucket.NotAnalyzed
        : analysis.All(a => a.Status == PackageCapabilityStatus.Ready) ? SourceAnalysisBucket.Analyzed
        : SourceAnalysisBucket.Partial;

    public static string BucketLabel(SourceAnalysisBucket bucket) => bucket switch
    {
        SourceAnalysisBucket.Analyzed => "Analyzed",
        SourceAnalysisBucket.Partial => "Partly analyzed",
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
        rows.Count(r => r.Bucket is SourceAnalysisBucket.NotAnalyzed or SourceAnalysisBucket.NotImported),
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
                || filter.Analysis == nameof(SourceAnalysisBucket.NotAnalyzed) && r.Bucket == SourceAnalysisBucket.NotImported)
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
