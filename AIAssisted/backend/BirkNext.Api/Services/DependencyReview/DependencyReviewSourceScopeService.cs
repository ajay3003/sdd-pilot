using Path = System.IO.Path;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Dependencies;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.DependencyReview;

public interface IDependencyReviewSourceScopeService
{
    /// <summary>Source Analysis snapshots of the environment as Dependency Review sees them, plus related-source candidates of a primary.</summary>
    Task<SourceScopeOptions> OptionsAsync(string environmentId, Guid? primarySnapshotId, CancellationToken ct = default);
    /// <summary>Validates the scope and reviews exactly the selected immutable snapshots.</summary>
    Task<(DependencyReviewResult? Result, string? Error)> RunAsync(SourceDependencyReviewRequest request, CancellationToken ct = default);
}

/// <summary>
/// Dependency Review's view of Source Analysis: it READS snapshots from the Source Analysis store (no second snapshot store, no upload,
/// no mutation) and binds every run to the exact snapshot IDs and fingerprints chosen. Related sources are only suggested, from explicit
/// evidence; nothing is included without an explicit choice and nothing is re-bound to a newer snapshot.
/// </summary>
public sealed class DependencyReviewSourceScopeService(IqrSourceStore store, IDependencyReviewService reviews) : IDependencyReviewSourceScopeService
{
    public const string NoEvidence = "Analyzed before dependency evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot.";

    public static SourceRepositoryIdentity IdentityOf(IqrSourceSnapshot s) => s.Repository
        ?? new SourceRepositoryIdentity(SourceDependencyEvidenceExtractor.Key(SourceDependencyEvidenceExtractor.ArchiveRepositoryName(s.Archive.FileName)),
            SourceDependencyEvidenceExtractor.ArchiveRepositoryName(s.Archive.FileName), "Archive file name");

    public static string SourceStatus(SourceAnalysisStatus status) => status switch { SourceAnalysisStatus.Ready => "Ready", SourceAnalysisStatus.Partial => "Partial", _ => "No production project found" };

    public static List<SourceScopeSnapshot> Describe(IReadOnlyList<IqrSourceSnapshot> snapshots)
    {
        var latest = snapshots.GroupBy(s => IdentityOf(s).Key).ToDictionary(g => g.Key, g => g.MaxBy(s => s.AnalyzedAt)!.Id);
        return snapshots.OrderBy(s => IdentityOf(s).DisplayName, StringComparer.OrdinalIgnoreCase).ThenByDescending(s => s.AnalyzedAt).Select(s =>
        {
            var identity = IdentityOf(s);
            return new SourceScopeSnapshot
            {
                SnapshotId = s.Id, RepositoryKey = identity.Key, Repository = identity.DisplayName, IdentityBasis = identity.Basis, ArchiveName = s.Archive.FileName,
                Fingerprint = s.Archive.Sha256, AnalyzedAt = s.AnalyzedAt, SourceStatus = SourceStatus(s.Status),
                HasDependencyEvidence = s.DependencyEvidence is not null, DependencyEvidenceNote = s.DependencyEvidence is null ? NoEvidence
                    : s.DependencyEvidence.Dependencies.Count == 0 && s.DependencyEvidence.RenovateFiles.Count == 0 ? "No supported dependency declaration or Renovate configuration was found in this snapshot." : null,
                DeclaredDependencies = s.DependencyEvidence?.Dependencies.Count ?? 0, RenovateFiles = s.DependencyEvidence?.RenovateFiles.Count ?? 0, Latest = latest[identity.Key] == s.Id,
            };
        }).ToList();
    }

    /// <summary>
    /// Related-source candidates of a primary snapshot, from explicit evidence only:
    /// (1) a NuGet package the primary declares whose exact ID another repository's snapshot declares it produces (csproj PackageId);
    /// (2) a ProjectReference that leaves the primary archive, matched by exact project file name to another repository's snapshot.
    /// Several repositories producing the same identity → Needs review (no guess). A repository named like the package whose snapshots do not
    /// produce it → Needs review. An external reference nothing matches → Snapshot unavailable. Never auto-included.
    /// </summary>
    public static List<RelatedSourceCandidate> Candidates(IqrSourceSnapshot primary, IReadOnlyList<IqrSourceSnapshot> all)
    {
        var evidence = primary.DependencyEvidence;
        if (evidence is null) return [];
        var primaryKey = IdentityOf(primary).Key;
        var others = all.Where(s => IdentityOf(s).Key != primaryKey).ToList();
        var candidates = new Dictionary<string, (string Repository, List<RelatedSourceEvidence> Evidence, HashSet<Guid> Snapshots, bool Ambiguous, bool Unmatched)>(StringComparer.Ordinal);

        foreach (var package in evidence.Dependencies.Where(d => d.Manager == "nuget").GroupBy(d => d.PackageName, StringComparer.OrdinalIgnoreCase))
        {
            var producers = others.Where(s => s.DependencyEvidence?.PublishedPackages.Any(p => string.Equals(p.PackageId, package.Key, StringComparison.OrdinalIgnoreCase)) == true).ToList();
            var projects = package.SelectMany(d => d.ReferencedBy.Count > 0 ? d.ReferencedBy : [d.OwnerFile]).Distinct().ToList();
            var versions = string.Join(", ", package.Select(d => d.CurrentValue ?? "(no version)").Distinct().Order(StringComparer.Ordinal));
            var repos = producers.GroupBy(s => IdentityOf(s).Key).ToList();
            if (repos.Count == 0)
            {
                // A repository known to Source Analysis carries the package's name, but none of its snapshots declares the package: ambiguous.
                var named = others.Where(s => IdentityOf(s).Key == SourceDependencyEvidenceExtractor.Key(package.Key)).ToList();
                if (named.Count == 0) continue;
                Add(IdentityOf(named[0]).Key, IdentityOf(named[0]).DisplayName, new("PackageReference", $"Package {package.Key} {versions}: a repository with this name exists, but none of its snapshots declares that it produces this package.", projects.Count, projects.Take(5).ToList()), [], ambiguous: true);
                continue;
            }
            foreach (var repo in repos)
                Add(repo.Key, IdentityOf(repo.First()).DisplayName, new("PackageReference", $"Package {package.Key} {versions}, which this repository's snapshot declares it produces (exact package ID).", projects.Count, projects.Take(5).ToList()),
                    repo.OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id), ambiguous: repos.Count > 1);
        }

        foreach (var reference in evidence.ExternalProjectReferences.GroupBy(r => r.ProjectFileName, StringComparer.OrdinalIgnoreCase))
        {
            var holders = others.Where(s => s.DependencyEvidence?.Dependencies.Any(d => Path.GetFileName(d.OwnerFile).Equals(reference.Key, StringComparison.OrdinalIgnoreCase)) == true
                || s.DependencyEvidence?.Managers.Any(m => m.Files.Any(f => Path.GetFileName(f).Equals(reference.Key, StringComparison.OrdinalIgnoreCase))) == true
                || s.DependencyEvidence?.PublishedPackages.Any(p => Path.GetFileName(p.Project).Equals(reference.Key, StringComparison.OrdinalIgnoreCase)) == true).ToList();
            var detail = $"ProjectReference to {reference.Key} outside this archive ({reference.First().Reference}).";
            var from = reference.Select(r => r.FromProject).Distinct().ToList();
            var repos = holders.GroupBy(s => IdentityOf(s).Key).ToList();
            if (repos.Count == 0) Add("external:" + SourceDependencyEvidenceExtractor.Key(Path.GetFileNameWithoutExtension(reference.Key)), Path.GetFileNameWithoutExtension(reference.Key), new("ProjectReference", detail, from.Count, from.Take(5).ToList()), [], ambiguous: false, unmatched: true);
            foreach (var repo in repos) Add(repo.Key, IdentityOf(repo.First()).DisplayName, new("ProjectReference", detail, from.Count, from.Take(5).ToList()), repo.OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id), ambiguous: repos.Count > 1);
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

        void Add(string key, string repository, RelatedSourceEvidence item, IEnumerable<Guid> snapshots, bool ambiguous, bool unmatched = false)
        {
            if (!candidates.TryGetValue(key, out var entry)) entry = (repository, [], [], false, false);
            entry.Evidence.Add(item);
            entry.Snapshots.UnionWith(snapshots);
            candidates[key] = (entry.Repository, entry.Evidence, entry.Snapshots, entry.Ambiguous || ambiguous, entry.Unmatched || unmatched);
        }
    }

    public async Task<SourceScopeOptions> OptionsAsync(string environmentId, Guid? primarySnapshotId, CancellationToken ct = default)
    {
        var snapshots = await store.ListAsync(environmentId, ct);
        var primary = primarySnapshotId is { } id ? snapshots.FirstOrDefault(s => s.Id == id) : null;
        return new SourceScopeOptions { Snapshots = Describe(snapshots), Candidates = primary is null ? [] : Candidates(primary, snapshots) };
    }

    public static SourceScopeEntry Entry(IqrSourceSnapshot s) => new()
    {
        SnapshotId = s.Id, RepositoryKey = IdentityOf(s).Key, Repository = IdentityOf(s).DisplayName, ArchiveName = s.Archive.FileName, Fingerprint = s.Archive.Sha256,
        AnalyzedAt = s.AnalyzedAt, SourceStatus = SourceStatus(s.Status),
    };

    /// <summary>The scope rules: a primary is required; every ID resolves; no duplicates; one snapshot per repository; each has dependency evidence.</summary>
    public static (List<IqrSourceSnapshot>? Selected, string? Error) Validate(SourceDependencyReviewRequest request, IReadOnlyList<IqrSourceSnapshot> snapshots)
    {
        if (request.PrimarySnapshotId == Guid.Empty) return (null, "Choose a primary source snapshot.");
        var ids = new[] { request.PrimarySnapshotId }.Concat(request.RelatedSnapshotIds).ToList();
        if (ids.Distinct().Count() != ids.Count) return (null, "A source snapshot is selected more than once.");
        var selected = new List<IqrSourceSnapshot>();
        foreach (var id in ids)
        {
            if (snapshots.FirstOrDefault(s => s.Id == id) is not { } snapshot) return (null, $"Source snapshot {id} is unavailable in Source Analysis. Repair the source scope; nothing is substituted.");
            if (snapshot.DependencyEvidence is null) return (null, $"{IdentityOf(snapshot).DisplayName} ({snapshot.Archive.Sha256[..Math.Min(8, snapshot.Archive.Sha256.Length)]}…): {NoEvidence}");
            selected.Add(snapshot);
        }
        var repeated = selected.GroupBy(s => IdentityOf(s).Key).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null) return (null, $"{IdentityOf(repeated.First()).DisplayName} is selected more than once; a scope holds one snapshot per repository.");
        return (selected, null);
    }

    public async Task<(DependencyReviewResult? Result, string? Error)> RunAsync(SourceDependencyReviewRequest request, CancellationToken ct = default)
    {
        var snapshots = await store.ListAsync(request.EnvironmentId, ct);
        var (selected, error) = Validate(request, snapshots);
        if (selected is null) return (null, error);
        var sources = selected.Select(s =>
        {
            var name = IdentityOf(s).DisplayName;
            var over = request.ConfigOverrides.FirstOrDefault(o => string.Equals(o.Repository, name, StringComparison.OrdinalIgnoreCase));
            return (SourceDependencyEvidenceExtractor.Input(name, s.Archive.Sha256, s.DependencyEvidence!, over is null ? null : (over.FileName, over.Content)), s.DependencyEvidence!);
        }).ToList();
        var notIncluded = Candidates(selected[0], snapshots).Where(c => !selected.Skip(1).Any(s => IdentityOf(s).Key == c.RepositoryKey)).ToList();
        var scope = new DependencyReviewSourceScope
        {
            Primary = Entry(selected[0]), Related = selected.Skip(1).Select(Entry).ToList(),
            ExcludedSuggestions = request.ExcludedSuggestions.Concat(notIncluded.Select(c => c.Repository)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Limitations = notIncluded.Select(c => $"Related source detected but not included in this review scope: {c.Repository} ({c.State switch { RelatedSourceState.SnapshotAvailable => "snapshot available", RelatedSourceState.SnapshotUnavailable => "no analyzed snapshot", _ => "needs review" }}).").ToList(),
        };
        var label = string.IsNullOrWhiteSpace(request.Label) ? string.Join(" + ", selected.Select(s => IdentityOf(s).DisplayName)) : request.Label.Trim();
        return (await reviews.ReviewAsync(label, sources, scope, ct), null);
    }
}
