using Path = System.IO.Path;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.SourceAnalysis;

/// <summary>What one consumer can use from a snapshot: whether its own evidence was captured, why not, and a short summary.</summary>
public sealed record ConsumerSourceEvidence(bool Available, string? Note = null, string? Summary = null);

/// <summary>
/// The one way review features consume Source Analysis. Source Analysis owns ingestion and the immutable snapshots (<see cref="IqrSourceStore"/>);
/// this provider only READS them: it lists and resolves exact snapshots, describes them as metadata, validates a source scope (one primary
/// plus explicitly included related snapshots — never merged, never substituted), reports newer snapshots without switching to them,
/// supplies exact reference matching for related-source suggestions and builds the immutable scope and provenance a run keeps.
/// It interprets nothing: which evidence counts, which related source is relevant and what a finding means stay with each review.
/// Full snapshots (with captured evidence) are returned to backend analyzers only; the browser receives <see cref="ReviewSourceSnapshot"/> metadata.
/// </summary>
public interface IReviewSourceEvidenceProvider
{
    /// <summary>False when Source Analysis is disabled in Feature Visibility: source evidence cannot be selected (no data is deleted).</summary>
    bool SourceAnalysisEnabled { get; }
    /// <summary>The Source Analysis snapshots of the Target Environment, newest first (deterministic).</summary>
    Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Exactly this snapshot, or null — never the latest one instead.</summary>
    Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default);
}

public sealed class ReviewSourceEvidenceProvider(IqrSourceStore store, bool sourceAnalysisEnabled = true) : IReviewSourceEvidenceProvider
{
    public const string Disabled = "Source Analysis is disabled in Feature Visibility, so source evidence cannot be selected. Stored snapshots and review results are kept.";

    public static ReviewSourceEvidenceProvider FromConfiguration(IqrSourceStore store, IConfiguration configuration) =>
        new(store, configuration.GetSection("FeatureVisibility").GetValue("SourceAnalysis", true));

    public bool SourceAnalysisEnabled => sourceAnalysisEnabled;

    public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => store.ListSourceAnalysisAsync(environmentId, ct: ct);

    public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => store.FindSourceAnalysisAsync(environmentId, snapshotId, ct);

    // ── Metadata and provenance ────────────────────────────────────────────────────────────────────────────────────────────

    public static SourceRepositoryIdentity Identity(IqrSourceSnapshot s) => s.Repository
        ?? new SourceRepositoryIdentity(SourceDependencyEvidenceExtractor.Key(SourceDependencyEvidenceExtractor.ArchiveRepositoryName(s.Archive.FileName)),
            SourceDependencyEvidenceExtractor.ArchiveRepositoryName(s.Archive.FileName), "Archive file name");

    public static string SourceStatus(SourceAnalysisStatus status) => status switch { SourceAnalysisStatus.Ready => "Ready", SourceAnalysisStatus.Partial => "Partial", _ => "No production project found" };

    public static string ShortFingerprint(IqrSourceSnapshot s) => s.Archive.Sha256[..Math.Min(8, s.Archive.Sha256.Length)] + "…";

    /// <summary>The snapshots as metadata, grouped by repository (newest first inside each), with the consumer's own evidence state.</summary>
    public static List<ReviewSourceSnapshot> Describe(IReadOnlyList<IqrSourceSnapshot> snapshots, Func<IqrSourceSnapshot, ConsumerSourceEvidence> consumer)
    {
        var latest = snapshots.GroupBy(s => Identity(s).Key).ToDictionary(g => g.Key, g => g.MaxBy(s => s.AnalyzedAt)!.Id);
        return snapshots.OrderBy(s => Identity(s).DisplayName, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.AnalyzedAt).ThenBy(s => s.Id)
            .Select(s => Describe(s, latest[Identity(s).Key] == s.Id, consumer(s))).ToList();
    }

    public static ReviewSourceSnapshot Describe(IqrSourceSnapshot s, bool latest, ConsumerSourceEvidence evidence)
    {
        var identity = Identity(s);
        return new ReviewSourceSnapshot
        {
            SnapshotId = s.Id, RepositoryKey = identity.Key, Repository = identity.DisplayName, IdentityBasis = identity.Basis, ArchiveName = s.Archive.FileName,
            Fingerprint = s.Archive.Sha256, AnalyzedAt = s.AnalyzedAt, SourceStatus = SourceStatus(s.Status), Latest = latest,
            HasConsumerEvidence = evidence.Available, ConsumerEvidenceNote = evidence.Note, ConsumerSummary = evidence.Summary,
        };
    }

    public static SourceScopeEntry Entry(IqrSourceSnapshot s) => new()
    {
        SnapshotId = s.Id, RepositoryKey = Identity(s).Key, Repository = Identity(s).DisplayName, ArchiveName = s.Archive.FileName, Fingerprint = s.Archive.Sha256,
        AnalyzedAt = s.AnalyzedAt, SourceStatus = SourceStatus(s.Status),
    };

    public static ReviewSourceProvenance Provenance(IqrSourceSnapshot s) => new() { SnapshotId = s.Id, Repository = Identity(s).DisplayName, Fingerprint = s.Archive.Sha256 };

    // ── Scope rules ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shared scope rules: Source Analysis enabled; a primary is required; every id resolves exactly (nothing substituted); no duplicates;
    /// one snapshot per repository; then the consumer's own requirement per snapshot (e.g. its evidence must have been captured).
    /// </summary>
    public static (List<IqrSourceSnapshot>? Selected, string? Error) Validate(ReviewSourceScopeRequest request, IReadOnlyList<IqrSourceSnapshot> snapshots,
        Func<IqrSourceSnapshot, string?>? consumerProblem = null, bool sourceAnalysisEnabled = true)
    {
        if (!sourceAnalysisEnabled) return (null, Disabled);
        if (request.PrimarySnapshotId == Guid.Empty) return (null, "Choose a primary source snapshot.");
        var ids = new[] { request.PrimarySnapshotId }.Concat(request.RelatedSnapshotIds).ToList();
        if (ids.Distinct().Count() != ids.Count) return (null, "A source snapshot is selected more than once.");
        var selected = new List<IqrSourceSnapshot>();
        foreach (var id in ids)
        {
            if (snapshots.FirstOrDefault(s => s.Id == id) is not { } snapshot) return (null, $"Source snapshot {id} is unavailable in Source Analysis. Repair the source scope; nothing is substituted.");
            if (consumerProblem?.Invoke(snapshot) is { } problem) return (null, $"{Identity(snapshot).DisplayName} ({ShortFingerprint(snapshot)}): {problem}");
            selected.Add(snapshot);
        }
        var repeated = selected.GroupBy(s => Identity(s).Key).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null) return (null, $"{Identity(repeated.First()).DisplayName} is selected more than once; a scope holds one snapshot per repository.");
        return (selected, null);
    }

    /// <summary>Newer snapshots of the selected snapshots' repositories — offered, never substituted.</summary>
    public static List<IqrSourceSnapshot> Newer(IReadOnlyList<IqrSourceSnapshot> selected, IReadOnlyList<IqrSourceSnapshot> all)
    {
        var latest = all.GroupBy(s => Identity(s).Key).ToDictionary(g => g.Key, g => g.MaxBy(s => s.AnalyzedAt)!);
        return selected.Select(s => (Current: s, Latest: latest[Identity(s).Key]))
            .Where(x => x.Latest.Id != x.Current.Id && x.Latest.AnalyzedAt > x.Current.AnalyzedAt && selected.All(s => s.Id != x.Latest.Id))
            .Select(x => x.Latest).DistinctBy(s => s.Id).ToList();
    }

    /// <summary>The immutable scope descriptor of a run. Suggested sources left out are recorded as limitations, never hidden.</summary>
    public static ReviewSourceScope Scope(List<IqrSourceSnapshot> selected, IReadOnlyList<RelatedSourceCandidate> candidates, IEnumerable<string> excluded)
    {
        var notIncluded = candidates.Where(c => !selected.Skip(1).Any(s => Identity(s).Key == c.RepositoryKey)).ToList();
        return new ReviewSourceScope
        {
            Primary = Entry(selected[0]), Related = selected.Skip(1).Select(Entry).ToList(),
            ExcludedSuggestions = excluded.Concat(notIncluded.Select(c => c.Repository)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Limitations = notIncluded.Select(c => $"Related source detected but not included in this review scope: {c.Repository} ({c.State switch { RelatedSourceState.SnapshotAvailable => "snapshot available", RelatedSourceState.SnapshotUnavailable => "no analyzed snapshot", _ => "needs review" }}).").ToList(),
        };
    }

    /// <summary>The options a page shows: metadata, candidates, the resolved scope, newer snapshots and a scope problem. Read-only.</summary>
    public static ReviewSourceOptions Options(bool sourceAnalysisEnabled, IReadOnlyList<IqrSourceSnapshot> snapshots, Func<IqrSourceSnapshot, ConsumerSourceEvidence> consumer,
        ReviewSourceScopeRequest? request, Func<IqrSourceSnapshot, List<RelatedSourceCandidate>> candidates, Func<IqrSourceSnapshot, string?>? consumerProblem = null)
    {
        if (!sourceAnalysisEnabled) return new ReviewSourceOptions { SourceAnalysisEnabled = false, Error = request is null ? null : Disabled };
        var options = new ReviewSourceOptions { Snapshots = Describe(snapshots, consumer) };
        if (request is null || request.PrimarySnapshotId == Guid.Empty) return options;
        var primary = snapshots.FirstOrDefault(s => s.Id == request.PrimarySnapshotId);
        var related = primary is null ? [] : candidates(primary);
        var (selected, error) = Validate(request, snapshots, consumerProblem);
        if (selected is null) return options with { Candidates = related, Error = error };
        var newer = Newer(selected, snapshots).Select(n => Describe(n, true, consumer(n))).ToList();
        return options with { Candidates = related, Scope = Scope(selected, related, request.ExcludedSuggestions), Newer = newer };
    }

    // ── Related-source plumbing (exact references only; each review decides relevance) ──────────────────────────────────────

    /// <summary>
    /// Reference candidates of a primary snapshot from Source Analysis' captured dependency evidence, by exact identity only:
    /// (1) a NuGet package the primary declares whose exact ID another repository's snapshot declares it produces (csproj PackageId);
    /// (2) a ProjectReference that leaves the primary archive, matched by exact project file name to another repository's snapshot.
    /// Several repositories producing the same identity → Needs review (no guess). A repository named like the package whose snapshots do not
    /// produce it → Needs review. An external reference nothing matches → Snapshot unavailable. Whether a match MATTERS is the consumer's call.
    /// </summary>
    public static List<RelatedSourceCandidate> ReferenceCandidates(IqrSourceSnapshot primary, IReadOnlyList<IqrSourceSnapshot> all)
    {
        var evidence = primary.DependencyEvidence;
        if (evidence is null) return [];
        var primaryKey = Identity(primary).Key;
        var others = all.Where(s => Identity(s).Key != primaryKey).ToList();
        var candidates = new Dictionary<string, (string Repository, List<RelatedSourceEvidence> Evidence, HashSet<Guid> Snapshots, bool Ambiguous)>(StringComparer.Ordinal);
        void Add(string key, string repository, RelatedSourceEvidence item, IEnumerable<Guid> snapshots, bool ambiguous)
        {
            if (!candidates.TryGetValue(key, out var entry)) entry = (repository, [], [], false);
            entry.Evidence.Add(item);
            entry.Snapshots.UnionWith(snapshots);
            candidates[key] = (entry.Repository, entry.Evidence, entry.Snapshots, entry.Ambiguous || ambiguous);
        }

        foreach (var package in evidence.Dependencies.Where(d => d.Manager == "nuget").GroupBy(d => d.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            var producers = others.Where(s => s.DependencyEvidence?.PublishedPackages.Any(p => string.Equals(p.PackageId, package.Key, StringComparison.OrdinalIgnoreCase)) == true).ToList();
            var projects = package.SelectMany(d => d.ReferencedBy.Count > 0 ? d.ReferencedBy : [d.OwnerFile]).Distinct().ToList();
            var versions = string.Join(", ", package.Select(d => d.CurrentValue ?? "(no version)").Distinct().Order(StringComparer.Ordinal));
            var repos = producers.GroupBy(s => Identity(s).Key).ToList();
            if (repos.Count == 0)
            {
                // A repository known to Source Analysis carries the package's name, but none of its snapshots declares the package: ambiguous.
                var named = others.Where(s => Identity(s).Key == SourceDependencyEvidenceExtractor.Key(package.Key)).ToList();
                if (named.Count == 0) continue;
                Add(Identity(named[0]).Key, Identity(named[0]).DisplayName, new("PackageReference", $"Package {package.Key} {versions}: a repository with this name exists, but none of its snapshots declares that it produces this package.", projects.Count, projects.Take(5).ToList()), [], ambiguous: true);
                continue;
            }
            foreach (var repo in repos)
                Add(repo.Key, Identity(repo.First()).DisplayName, new("PackageReference", $"Package {package.Key} {versions}, which this repository's snapshot declares it produces (exact package ID).", projects.Count, projects.Take(5).ToList()),
                    repo.OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id), ambiguous: repos.Count > 1);
        }

        foreach (var reference in evidence.ExternalProjectReferences.GroupBy(r => r.ProjectFileName, StringComparer.OrdinalIgnoreCase))
        {
            var holders = others.Where(s => s.DependencyEvidence?.Dependencies.Any(d => Path.GetFileName(d.OwnerFile).Equals(reference.Key, StringComparison.OrdinalIgnoreCase)) == true
                || s.DependencyEvidence?.Managers.Any(m => m.Files.Any(f => Path.GetFileName(f).Equals(reference.Key, StringComparison.OrdinalIgnoreCase))) == true
                || s.DependencyEvidence?.PublishedPackages.Any(p => Path.GetFileName(p.Project).Equals(reference.Key, StringComparison.OrdinalIgnoreCase)) == true).ToList();
            var detail = $"ProjectReference to {reference.Key} outside this archive ({reference.First().Reference}).";
            var from = reference.Select(r => r.FromProject).Distinct().ToList();
            var repos = holders.GroupBy(s => Identity(s).Key).ToList();
            if (repos.Count == 0) Add("external:" + SourceDependencyEvidenceExtractor.Key(Path.GetFileNameWithoutExtension(reference.Key)), Path.GetFileNameWithoutExtension(reference.Key), new("ProjectReference", detail, from.Count, from.Take(5).ToList()), [], ambiguous: false);
            foreach (var repo in repos) Add(repo.Key, Identity(repo.First()).DisplayName, new("ProjectReference", detail, from.Count, from.Take(5).ToList()), repo.OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id), ambiguous: repos.Count > 1);
        }

        return candidates.Select(c => new RelatedSourceCandidate
        {
            RepositoryKey = c.Key, Repository = c.Value.Repository, Evidence = c.Value.Evidence,
            State = c.Value.Ambiguous ? RelatedSourceState.NeedsReview : c.Value.Snapshots.Count == 0 ? RelatedSourceState.SnapshotUnavailable : RelatedSourceState.SnapshotAvailable,
            Reason = c.Value.Ambiguous ? "The match is not unique or not confirmed by the candidate's own snapshots — review before including."
                : c.Value.Snapshots.Count == 0 ? "Referenced from source, but no analyzed snapshot of this source is available."
                : $"Referenced by {c.Value.Evidence.Max(e => e.Projects)} project(s) in the primary source.",
            MatchingSnapshotIds = [.. all.Where(s => c.Value.Snapshots.Contains(s.Id)).OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id)],
        }).OrderBy(c => c.Repository, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
