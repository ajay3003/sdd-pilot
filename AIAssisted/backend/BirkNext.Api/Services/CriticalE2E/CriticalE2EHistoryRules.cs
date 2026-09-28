using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.CriticalE2E;

/// <summary>
/// What "Archive old runs" and "Clear test history" may touch for one flow.
///
/// Archive is a view decision: it hides a run from the default history and changes nothing the release verdict reads —
/// coverage and release evidence still see archived runs like any other. Even so it never selects the flow's latest run
/// or the run the release verdict uses for the named build, because hiding those would make the page contradict itself.
///
/// Clear is destructive, so it refuses outright while any run in its scope is release evidence: a build-linked run of a
/// release-required critical flow. There is no override; archiving is the answer for that history.
///
/// Pure functions over their inputs, like <see cref="CriticalE2ECoverage"/>.
/// </summary>
public static class CriticalE2EHistoryRules
{
    public const string ClearBlockedReason =
        "This flow has build-linked release evidence, so its history cannot be cleared. Archive old runs instead.";

    /// <summary>Evidence for its build: a build-linked run of a release-required critical flow (as the flow is now, or as the run was recorded).</summary>
    public static bool IsReleaseEvidence(CriticalE2EFlowDefinition flow, CriticalE2ERunResult run) =>
        flow.Kind == CriticalE2EFlowKind.Critical && (flow.RequiredForRelease || run.RequiredForRelease) && !string.IsNullOrWhiteSpace(run.BuildId);

    /// <summary>The run the release verdict uses for the named build: the latest one on it, for an enabled, required, critical flow.</summary>
    public static CriticalE2ERunResult? CurrentEvidence(CriticalE2EFlowDefinition flow, IReadOnlyList<CriticalE2ERunResult> runs, string? buildId) =>
        flow.Enabled && flow.RequiredForRelease && flow.Kind == CriticalE2EFlowKind.Critical && !string.IsNullOrWhiteSpace(buildId)
            ? runs.OrderByDescending(r => r.StartedAt).FirstOrDefault(r => OnBuild(r, buildId))
            : null;

    public static bool OnBuild(CriticalE2ERunResult run, string? buildId) =>
        !string.IsNullOrWhiteSpace(buildId) && string.Equals(run.BuildId?.Trim(), buildId.Trim(), StringComparison.OrdinalIgnoreCase);

    public sealed record ArchiveSelection(List<CriticalE2ERunResult> Archive, List<string> Skipped);

    /// <summary>
    /// Active runs to archive. The latest run and the current evidence run are always kept, and said so in
    /// <see cref="ArchiveSelection.Skipped"/> when the user named them.
    /// </summary>
    public static ArchiveSelection SelectForArchive(CriticalE2EFlowDefinition flow, IReadOnlyList<CriticalE2ERunResult> runs,
        CriticalE2EArchiveSelection selection, IReadOnlyCollection<string> runIds, string? buildId)
    {
        var ordered = runs.OrderByDescending(r => r.StartedAt).ToList();
        var latest = ordered.FirstOrDefault();
        var evidence = CurrentEvidence(flow, ordered, buildId);
        var skipped = new List<string>();
        bool Kept(CriticalE2ERunResult run, bool named)
        {
            string? reason = run.RunId == latest?.RunId ? "the latest run is kept"
                : run.RunId == evidence?.RunId ? "it is the current release evidence for this build"
                : null;
            if (reason is not null && named) skipped.Add($"{run.RunId}: {reason}");
            return reason is not null;
        }

        var active = ordered.Where(r => !r.Archived);
        var archive = selection switch
        {
            CriticalE2EArchiveSelection.AllExceptLatest => active.Where(r => !Kept(r, named: false)).ToList(),
            // Without a named build every run is "not on the current build", which is AllExceptLatest under another name.
            CriticalE2EArchiveSelection.NotOnCurrentBuild when string.IsNullOrWhiteSpace(buildId) =>
                throw new ArgumentException("No build is selected, so there is no current build to compare runs against."),
            CriticalE2EArchiveSelection.NotOnCurrentBuild => active.Where(r => !OnBuild(r, buildId) && !Kept(r, named: false)).ToList(),
            _ => active.Where(r => runIds.Contains(r.RunId) && !Kept(r, named: true)).ToList(),
        };
        return new ArchiveSelection(archive, skipped);
    }

    public static List<CriticalE2ERunResult> ClearScope(IReadOnlyList<CriticalE2ERunResult> runs, bool includeArchived) =>
        runs.Where(r => includeArchived || !r.Archived).ToList();

    public static string? ClearBlocked(CriticalE2EFlowDefinition flow, IReadOnlyList<CriticalE2ERunResult> scope) =>
        scope.Any(r => IsReleaseEvidence(flow, r)) ? ClearBlockedReason : null;

    public static CriticalE2EHistoryPreview Preview(CriticalE2EFlowDefinition flow, IReadOnlyList<CriticalE2ERunResult> runs, string? buildId)
    {
        var ordered = runs.OrderByDescending(r => r.StartedAt).ToList();
        var blocked = ClearBlocked(flow, ordered);
        return new CriticalE2EHistoryPreview
        {
            FlowId = flow.Id,
            FlowName = flow.Name,
            Module = flow.Module,
            Kind = flow.Kind,
            ActiveRuns = ordered.Count(r => !r.Archived),
            ArchivedRuns = ordered.Count(r => r.Archived),
            BuildLinkedRuns = ordered.Count(r => !string.IsNullOrWhiteSpace(r.BuildId)),
            ReleaseEvidenceRuns = ordered.Count(r => IsReleaseEvidence(flow, r)),
            ActiveReleaseEvidenceRuns = ordered.Count(r => !r.Archived && IsReleaseEvidence(flow, r)),
            LatestRunId = ordered.FirstOrDefault()?.RunId,
            CurrentEvidenceRunId = CurrentEvidence(flow, ordered, buildId)?.RunId,
            ArchiveCandidates = SelectForArchive(flow, ordered, CriticalE2EArchiveSelection.AllExceptLatest, [], buildId).Archive.Count,
            ArchiveCandidatesNotOnCurrentBuild = string.IsNullOrWhiteSpace(buildId)
                ? null
                : SelectForArchive(flow, ordered, CriticalE2EArchiveSelection.NotOnCurrentBuild, [], buildId).Archive.Count,
            ClearBlocked = blocked is not null,
            ClearBlockedReason = blocked,
        };
    }
}
