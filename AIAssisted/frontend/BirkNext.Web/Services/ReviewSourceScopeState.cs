namespace BirkNext.Web.Services;

/// <summary>
/// The source scope a review page is editing: one primary Source Analysis snapshot plus explicitly included related snapshots, the related
/// sources the user continues without, and newer snapshots the user chose to keep ignoring. Shared by every source-aware review page (the
/// shared source components mutate it; the page reloads its options with <see cref="Request"/>). Nothing here is ever chosen automatically:
/// the current snapshot is only offered, a newer snapshot only shown, a detected related source only suggested.
/// </summary>
public sealed class ReviewSourceScopeState
{
    public Guid? PrimaryId { get; private set; }
    /// <summary>Included related sources: repository key → exact snapshot (explicit choices only).</summary>
    public Dictionary<string, Guid> Related { get; } = new(StringComparer.Ordinal);
    /// <summary>Which snapshot of a suggested repository the user picked before including it.</summary>
    public Dictionary<string, Guid> RelatedChoice { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Excluded { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<Guid> KeptNewer { get; } = [];
    public ReviewSourceOptions? Options { get; set; }

    public IReadOnlyList<ReviewSourceSnapshot> Snapshots => Options?.Snapshots ?? [];
    public ReviewSourceSnapshot? Primary => PrimaryId is { } id ? Snapshots.FirstOrDefault(s => s.SnapshotId == id) : null;
    /// <summary>Source Analysis' newest snapshot this review can use — offered as "Use this snapshot", never selected silently.</summary>
    public ReviewSourceSnapshot? Current => ProjectBoundSnapshot ?? (RecommendByEvidenceOnly
        ? EvidenceRepositories.Count == 1 ? Snapshots.Where(HoldsSubject).MaxBy(s => s.AnalyzedAt) : null
        : Snapshots.Where(s => s.HasConsumerEvidence).MaxBy(s => s.AnalyzedAt) ?? Snapshots.MaxBy(s => s.AnalyzedAt));

    /// <summary>
    /// Exact snapshots bound by the active Target Environment's Project (Target Environment → Project). An explicit project binding outranks
    /// discovery: when exactly one bound snapshot is usable here it is the one offered (still never selected silently by this state).
    /// </summary>
    public IReadOnlyCollection<Guid> ProjectBound { get; set; } = [];
    /// <summary>"M2LB (Target Environment Development)": names where <see cref="ProjectBound"/> comes from.</summary>
    public string? ProjectBoundLabel { get; set; }

    /// <summary>The one bound snapshot this review can use (holding its evidence when the review recommends by evidence), else null.</summary>
    public ReviewSourceSnapshot? ProjectBoundSnapshot => ProjectBound.Count == 0 ? null
        : Snapshots.Where(s => ProjectBound.Contains(s.SnapshotId) && (!RecommendByEvidenceOnly || HoldsSubject(s))).ToList() is { Count: 1 } bound ? bound[0] : null;
    /// <summary>
    /// Opt-in (a review whose evidence has a content-defined source role): only a snapshot whose own analysis holds this review's evidence is
    /// recommended, and only when exactly one repository has it — never the newest snapshot overall (e.g. an infrastructure-only current project).
    /// </summary>
    public bool RecommendByEvidenceOnly { get; init; }
    /// <summary>Repositories whose snapshots hold this review's evidence (several = a decision for the user, never guessed).</summary>
    public IReadOnlyList<string> EvidenceRepositories => [.. Snapshots.Where(HoldsSubject).Select(s => s.Repository).Distinct(StringComparer.OrdinalIgnoreCase)];
    /// <summary>The snapshot's analysis holds the review's subject (falls back to "has evidence" for reviews that do not distinguish).</summary>
    private static bool HoldsSubject(ReviewSourceSnapshot s) => s.HasConsumerSubject ?? s.HasConsumerEvidence;
    public List<RelatedSourceCandidate> NotIncluded => Options?.Candidates.Where(c => !Related.ContainsKey(c.RepositoryKey)).ToList() ?? [];
    public List<ReviewSourceSnapshot> Newer => (Options?.Newer ?? []).Where(n => !KeptNewer.Contains(n.SnapshotId)).ToList();
    /// <summary>A selection the backend resolved exactly (bound to a run only when it is run).</summary>
    public bool Resolved => Options is { Scope: not null, Error: null };

    public ReviewSourceScopeRequest? Request => PrimaryId is { } primary
        ? new ReviewSourceScopeRequest { PrimarySnapshotId = primary, RelatedSnapshotIds = [.. Related.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value)], ExcludedSuggestions = [.. Excluded.Order(StringComparer.OrdinalIgnoreCase)] }
        : null;

    /// <summary>The gate state of a direct source consumer (shared rule).</summary>
    public ReviewSourceGateState Gate(bool sourceAnalysisEnabled, bool requiredRelatedUnavailable = false) => ReviewSourceScopes.Gate(sourceAnalysisEnabled && Options?.SourceAnalysisEnabled != false,
        Snapshots.Count, PrimaryId is not null, PrimaryId is not null && Options?.Error is not null, Newer.Count, NotIncluded.Count(c => !Excluded.Contains(c.Repository)), requiredRelatedUnavailable);

    /// <summary>Choose (or clear) the primary: related choices are reset and re-detected for it.</summary>
    public void PickPrimary(Guid? id)
    {
        PrimaryId = id;
        Related.Clear();
        RelatedChoice.Clear();
        Excluded.Clear();
    }

    public bool Include(string repositoryKey, Guid snapshotId)
    {
        if (Primary is { } p && Snapshots.FirstOrDefault(s => s.SnapshotId == snapshotId)?.RepositoryKey == p.RepositoryKey) return false; // the primary's repository is not also related
        Related[repositoryKey] = snapshotId;
        return true;
    }

    public void Remove(string repositoryKey) => Related.Remove(repositoryKey);

    public void ContinueWithout() { foreach (var c in NotIncluded) Excluded.Add(c.Repository); }

    /// <summary>Review change: the newer snapshot replaces the scope's snapshot of the same repository (only on this explicit action).</summary>
    public void UseNewer(ReviewSourceSnapshot newer)
    {
        if (Primary?.RepositoryKey == newer.RepositoryKey) PrimaryId = newer.SnapshotId;
        else Related[newer.RepositoryKey] = newer.SnapshotId;
    }

    /// <summary>Proposes a stored run's exact scope again (never the latest snapshot).</summary>
    public void Restore(ReviewSourceScope? scope)
    {
        if (scope is null) return;
        PickPrimary(scope.Primary.SnapshotId);
        foreach (var related in scope.Related) Related[related.RepositoryKey] = related.SnapshotId;
        foreach (var excluded in scope.ExcludedSuggestions) Excluded.Add(excluded);
    }

    public string Label => Options?.Scope is { } scope ? ReviewSourceScopes.Label(scope) : "";

    public static string Option(ReviewSourceSnapshot s, string? missingEvidence = null) =>
        $"{s.ArchiveName} · {ReviewSourceScopes.Short(s.Fingerprint)} · {Utc(s.AnalyzedAt)} · Source Analysis: {s.SourceStatus}{(s.Latest ? " · latest" : " · older")}{(s.HasConsumerEvidence ? "" : $" · {missingEvidence ?? "no evidence for this review"}")}";

    public static string Utc(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " UTC";
}
