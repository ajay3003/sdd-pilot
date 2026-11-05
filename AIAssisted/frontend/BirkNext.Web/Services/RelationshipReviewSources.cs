using BirkNext.Integrations;
using BirkNext.Snapshots;
using BirkNext.Web.Services.ProjectEvidence;

namespace BirkNext.Web.Services;

/// <summary>One stored Source Analysis snapshot as a Relationship Review input: its real analysis outcome over the areas it contains (the shared
/// <see cref="SourceSetupOutcome.Overall"/> rule the Snapshot Registry uses) and whether Project Evidence associates it with the Target Application.</summary>
public sealed record RelationshipSource(Guid Id, string Label, DateTimeOffset AnalyzedAt, SnapshotProcessingStatus Outcome, string? Detail, bool Associated)
{
    public string OutcomeLabel => RelationshipReviewSources.Label(Outcome);
    /// <summary>A failed analysis holds nothing to relate; it is listed, never selected by default.</summary>
    public bool Usable => Outcome is not SnapshotProcessingStatus.Failed;
}

/// <summary>
/// Pure rules for the Relationship Review source summary. The outcome is read from the snapshot's own per-area results when the backend reports
/// them, else from the code analyzer's status; it is never "analyzed" merely because the snapshot exists. The default selection is the sources
/// Project Evidence associates with the Target Application; without associations, every usable source.
/// </summary>
public static class RelationshipReviewSources
{
    public static IReadOnlyList<RelationshipSource> Of(IReadOnlyList<IqrSourceSnapshotSummary> stored, SourceSetupView? setup, ProjectEvidenceView? evidence)
    {
        var associated = evidence?.Associations.Where(a => a.State == EvidenceAssociationState.Associated && a.Package.SnapshotId is not null)
            .Select(a => a.Package.SnapshotId!.Value).ToHashSet() ?? [];
        return [.. stored.OrderByDescending(s => s.AnalyzedAt).Select(s =>
        {
            var areas = setup?.Snapshots.FirstOrDefault(x => x.SnapshotId == s.Id)?.Areas ?? [];
            var outcome = areas.Count > 0 ? SourceSetupOutcome.Overall(areas) : s.Status switch
            {
                SourceAnalysisStatus.Ready => SnapshotProcessingStatus.Completed,
                SourceAnalysisStatus.Partial => SnapshotProcessingStatus.Partial,
                _ => SnapshotProcessingStatus.Failed,
            };
            return new RelationshipSource(s.Id, s.Repository is { Length: > 0 } repo ? $"{repo} · {s.Archive.FileName}" : s.Archive.FileName, s.AnalyzedAt, outcome,
                Detail(areas), associated.Contains(s.Id));
        })];
    }

    public static IReadOnlyList<Guid> DefaultSelection(IReadOnlyList<RelationshipSource> sources) =>
        sources.Any(s => s.Associated && s.Usable) ? [.. sources.Where(s => s.Associated && s.Usable).Select(s => s.Id)] : [.. sources.Where(s => s.Usable).Select(s => s.Id)];

    public static string Label(SnapshotProcessingStatus outcome) => outcome switch
    {
        SnapshotProcessingStatus.Completed => "Analyzed",
        SnapshotProcessingStatus.Partial => "Partially analyzed",
        SnapshotProcessingStatus.Failed => "Analysis failed",
        SnapshotProcessingStatus.Running => "Analysis running",
        _ => "Nothing analyzed",
    };

    /// <summary>The present areas that are not complete ("Application code: Failed · Architecture: Partial"); null when every present area is complete.</summary>
    private static string? Detail(IReadOnlyList<SourceSetupArea> areas)
    {
        var incomplete = areas.Where(a => a.Status is not ("Complete" or "NotDetected" or "NotAnalyzed")).Select(a => $"{a.Area}: {a.Status}").ToList();
        return incomplete.Count == 0 ? null : string.Join(" · ", incomplete);
    }
}
