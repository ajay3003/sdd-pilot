using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Dependencies;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.DependencyReview;

public interface IDependencyReviewSourceScopeService
{
    /// <summary>Source Analysis snapshots as Dependency Review sees them and, for a chosen scope, its candidates, newer snapshots and problems.</summary>
    Task<ReviewSourceOptions> OptionsAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default);
    /// <summary>Validates the scope and reviews exactly the selected immutable snapshots.</summary>
    Task<(DependencyReviewResult? Result, string? Error)> RunAsync(SourceDependencyReviewRequest request, CancellationToken ct = default);
    /// <summary>Checks the given snapshots' file inventories for supported SBOM/lock-file names. Read-only; never imports or validates content.</summary>
    Task<SbomCandidateScan> SbomCandidatesAsync(string environmentId, IReadOnlyList<Guid> snapshotIds, CancellationToken ct = default);
}

/// <summary>
/// Dependency Review's source snapshot mode. Snapshot access, scope rules and provenance come from the shared
/// <see cref="IReviewSourceEvidenceProvider"/> (no second snapshot store, no upload). Dependency Review owns what the snapshots mean: its
/// dependency evidence, and that every exact package/project reference match is a related-source suggestion for this review.
/// </summary>
public sealed class DependencyReviewSourceScopeService(IReviewSourceEvidenceProvider sources, IDependencyReviewService reviews) : IDependencyReviewSourceScopeService
{
    public const string NoEvidence = "Analyzed before dependency evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot.";

    public static SourceRepositoryIdentity IdentityOf(IqrSourceSnapshot s) => ReviewSourceEvidenceProvider.Identity(s);

    public static SourceScopeEntry Entry(IqrSourceSnapshot s) => ReviewSourceEvidenceProvider.Entry(s);

    public static string SourceStatus(SourceAnalysisStatus status) => ReviewSourceEvidenceProvider.SourceStatus(status);

    /// <summary>Whether a snapshot carries dependency evidence, and what it holds.</summary>
    public static ConsumerSourceEvidence Evidence(IqrSourceSnapshot s) => s.DependencyEvidence is not { } e
        ? new(false, NoEvidence)
        : new(true, e.Dependencies.Count == 0 && e.RenovateFiles.Count == 0 ? "No supported dependency declaration or Renovate configuration was found in this snapshot." : null,
            $"{e.Dependencies.Count} declared dependencies · {e.RenovateFiles.Count} Renovate configuration file(s)" + PipelineChecks(s) + IacAutomation(s));

    /// <summary>Dependency/security checks the snapshot's pipelines intend to run, read from Source Analysis CI/CD evidence (defined, not executed).
    /// Dependency Review keeps its own registry/security/license interpretation; this is context only.</summary>
    public static string PipelineChecks(IqrSourceSnapshot s) => SourceAnalysis.SourceEvidenceQueries.DependencyChecks(s) is { Available: true, Items.Count: > 0 } checks
        ? $" · {checks.Items.Count} pipeline dependency/security check(s) defined" : "";

    /// <summary>IaC automation dependencies, kept apart from package health: Terraform providers pinned in lock files, modules, and the Python
    /// imports and external tools infrastructure scripts reference. Static references only — nothing is installed, resolved or queried.</summary>
    public static string IacAutomation(IqrSourceSnapshot s)
    {
        if (s.EvidenceDomains?.Infrastructure is not { } i || (i.LockedProviders.Count == 0 && i.Scripts.Count == 0)) return "";
        var providers = i.LockedProviders.Select(p => p.Address).Distinct(StringComparer.Ordinal).Count();
        var imports = i.Scripts.SelectMany(x => x.Imports).Distinct(StringComparer.Ordinal).Count();
        var tools = i.Scripts.SelectMany(x => x.Commands).Where(c => c.ExternalTool).Select(c => c.Command).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return $" · IaC automation dependencies (separate from package health): {providers} locked Terraform provider(s), {i.Modules.Count(m => !m.Local)} external module(s), "
            + $"{imports} Python import(s), external tools {(tools.Count == 0 ? "none" : string.Join(", ", tools.Take(6)))}";
    }

    public static List<ReviewSourceSnapshot> Describe(IReadOnlyList<IqrSourceSnapshot> snapshots) => ReviewSourceEvidenceProvider.Describe(snapshots, Evidence);

    /// <summary>Dependency Review's related sources: every exact package-ID or external ProjectReference match (see the provider).</summary>
    public static List<RelatedSourceCandidate> Candidates(IqrSourceSnapshot primary, IReadOnlyList<IqrSourceSnapshot> all) => ReviewSourceEvidenceProvider.ReferenceCandidates(primary, all);

    private static string? Problem(IqrSourceSnapshot s) => s.DependencyEvidence is null ? NoEvidence : null;

    public static ReviewSourceScopeRequest Scope(SourceDependencyReviewRequest request) => new()
    {
        PrimarySnapshotId = request.PrimarySnapshotId, RelatedSnapshotIds = request.RelatedSnapshotIds, ExcludedSuggestions = request.ExcludedSuggestions,
    };

    /// <summary>The shared scope rules plus Dependency Review's own: every snapshot must carry dependency evidence.</summary>
    public static (List<IqrSourceSnapshot>? Selected, string? Error) Validate(SourceDependencyReviewRequest request, IReadOnlyList<IqrSourceSnapshot> snapshots, bool sourceAnalysisEnabled = true) =>
        ReviewSourceEvidenceProvider.Validate(Scope(request), snapshots, Problem, sourceAnalysisEnabled);

    public async Task<ReviewSourceOptions> OptionsAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default)
    {
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        return ReviewSourceEvidenceProvider.Options(sources.SourceAnalysisEnabled, snapshots, Evidence, scope, primary => Candidates(primary, snapshots), Problem);
    }

    /// <summary>SBOM candidates of exactly the given snapshots, by file name from the snapshot's file inventory.</summary>
    public static SbomCandidateScan ScanForSbom(IEnumerable<IqrSourceSnapshot> snapshots)
    {
        var checkedSnapshots = new List<SbomCheckedSnapshot>();
        var candidates = new List<SbomCandidate>();
        var limitations = new List<string>();
        foreach (var s in snapshots)
        {
            var repository = IdentityOf(s).DisplayName;
            var files = s.TargetIndex?.Files ?? [];
            checkedSnapshots.Add(new(s.Id, repository, s.TargetIndex?.FileInventoryComplete == true, files.Count));
            if (s.TargetIndex is null) limitations.Add($"{repository}: analyzed before Source Analysis kept a file inventory, so it could not be checked for SBOM files.");
            else if (!s.TargetIndex.FileInventoryComplete) limitations.Add($"{repository}: the snapshot's file inventory is incomplete; SBOM files outside it were not checked.");
            foreach (var file in files)
                if (SbomCandidates.Classify(file.RelativePath) is { } format) candidates.Add(new(s.Id, repository, file.RelativePath, format, s.AnalyzedAt));
        }
        return new SbomCandidateScan { Checked = checkedSnapshots, Candidates = [.. candidates.OrderBy(c => c.Repository, StringComparer.Ordinal).ThenBy(c => c.Path, StringComparer.Ordinal)], Limitations = limitations };
    }

    public async Task<SbomCandidateScan> SbomCandidatesAsync(string environmentId, IReadOnlyList<Guid> snapshotIds, CancellationToken ct = default)
    {
        if (!sources.SourceAnalysisEnabled || snapshotIds.Count == 0) return new SbomCandidateScan();
        var wanted = snapshotIds.ToHashSet();
        return ScanForSbom((await sources.ListAsync(environmentId, ct)).Where(s => wanted.Contains(s.Id)));
    }

    public async Task<(DependencyReviewResult? Result, string? Error)> RunAsync(SourceDependencyReviewRequest request, CancellationToken ct = default)
    {
        var snapshots = await sources.ListAsync(request.EnvironmentId, ct);
        var (selected, error) = Validate(request, snapshots, sources.SourceAnalysisEnabled);
        if (selected is null) return (null, error);
        var inputs = selected.Select(s =>
        {
            var name = IdentityOf(s).DisplayName;
            var over = request.ConfigOverrides.FirstOrDefault(o => string.Equals(o.Repository, name, StringComparison.OrdinalIgnoreCase));
            return (SourceDependencyEvidenceExtractor.Input(name, s.Archive.Sha256, s.DependencyEvidence!, over is null ? null : (over.FileName, over.Content)), s.DependencyEvidence!);
        }).ToList();
        var scope = ReviewSourceEvidenceProvider.Scope(selected, Candidates(selected[0], snapshots), request.ExcludedSuggestions);
        var label = string.IsNullOrWhiteSpace(request.Label) ? string.Join(" + ", selected.Select(s => IdentityOf(s).DisplayName)) : request.Label.Trim();
        return (await reviews.ReviewAsync(label, inputs, scope, ct), null);
    }
}
