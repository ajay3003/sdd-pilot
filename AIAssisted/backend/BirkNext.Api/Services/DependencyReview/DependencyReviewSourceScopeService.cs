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
            $"{e.Dependencies.Count} declared dependencies · {e.RenovateFiles.Count} Renovate configuration file(s)" + PipelineChecks(s));

    /// <summary>Dependency/security checks the snapshot's pipelines intend to run, read from Source Analysis CI/CD evidence (defined, not executed).
    /// Dependency Review keeps its own registry/security/license interpretation; this is context only.</summary>
    public static string PipelineChecks(IqrSourceSnapshot s) => SourceAnalysis.SourceEvidenceQueries.DependencyChecks(s) is { Available: true, Items.Count: > 0 } checks
        ? $" · {checks.Items.Count} pipeline dependency/security check(s) defined" : "";

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
