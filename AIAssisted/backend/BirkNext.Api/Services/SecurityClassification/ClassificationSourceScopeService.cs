using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.SecurityClassification;

public interface IClassificationSourceScopeService
{
    /// <summary>Source Analysis snapshots as this review sees them, related-source candidates of a primary and the combined evidence of a scope. Read-only.</summary>
    Task<ClassificationScopeOptions> OptionsAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default);
    /// <summary>Validates a scope and combines the evidence of exactly those snapshots (no substitution, no latest rebind).</summary>
    Task<(ClassificationSourceEvidence? Evidence, string? Error)> ResolveAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default);
}

/// <summary>
/// Security Classification's view of Source Analysis, through the shared <see cref="IReviewSourceEvidenceProvider"/> (no second snapshot store, no upload,
/// no Azure, Event Hub, database, HTTP or GraphQL call, no mutation) and binds a review to the exact snapshot IDs and fingerprints chosen.
/// Related sources are suggested only when actual source evidence ties them to this review: a classification type the primary uses but does
/// not declare and the candidate declares, or a package/project reference to a source whose own snapshot carries classification or
/// authorization evidence. Nothing is included without an explicit choice and nothing is merged.
/// </summary>
public sealed class ClassificationSourceScopeService(IReviewSourceEvidenceProvider sources) : IClassificationSourceScopeService
{
    public const string NoEvidence = "Analyzed before security classification evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot.";

    /// <summary>Areas whose evidence makes a source relevant to this review (privacy, caching and grant defaults are reported for any source and do not count).</summary>
    private static readonly ClassificationArea[] SecurityAreas =
    [
        ClassificationArea.Model, ClassificationArea.Pipeline, ClassificationArea.Guard, ClassificationArea.DirectAccess, ClassificationArea.Search,
        ClassificationArea.GraphQL, ClassificationArea.AuditAccess, ClassificationArea.ChildAccess,
    ];

    private static SourceRepositoryIdentity Identity(IqrSourceSnapshot s) => ReviewSourceEvidenceProvider.Identity(s);

    public static ReviewSourceProvenance Ref(IqrSourceSnapshot s) => ReviewSourceEvidenceProvider.Provenance(s);

    /// <summary>The areas in which a snapshot's own evidence shows classification or authorization behaviour (empty = not relevant here).</summary>
    public static List<ClassificationArea> RelevantAreas(IqrSourceSnapshot s) => s.SecurityClassificationEvidence is { Unavailable: null } e
        ? e.Facts.Where(f => SecurityAreas.Contains(f.Area) && f.State is not (ClassificationState.NotFound or ClassificationState.NotApplicable)).Select(f => f.Area)
            .Concat(e.Levels.Count > 0 ? [ClassificationArea.Model] : []).Distinct().OrderBy(a => a).ToList()
        : [];

    private static string? EvidenceNote(IqrSourceSnapshot s) => s.SecurityClassificationEvidence switch
    {
        null => NoEvidence,
        { Unavailable: { } why } => why,
        { DeclaredNamespaces.Count: > 0 } e when RelevantAreas(s).Count == 0 => $"No classification model or access path here; declares {string.Join(", ", e.DeclaredNamespaces.Take(3))}.",
        _ when RelevantAreas(s).Count == 0 => "No classification or authorization evidence was found in this snapshot.",
        _ => null,
    };

    /// <summary>Whether a snapshot carries this review's security classification observations.</summary>
    public static ConsumerSourceEvidence Evidence(IqrSourceSnapshot s) => new(s.SecurityClassificationEvidence is { Unavailable: null }, EvidenceNote(s), SourceSecurityContext(s));

    /// <summary>Security-relevant Source Analysis evidence beside this review's own observations: identity/security configuration, IaC access
    /// assignments and contract fields named like a classification. Context only — a declared role or a classification field is not enforcement;
    /// Security Classification keeps its own semantics.</summary>
    public static string? SourceSecurityContext(IqrSourceSnapshot s)
    {
        if (s.EvidenceDomains is null) return null;
        var config = SourceAnalysis.SourceEvidenceQueries.SecurityConfiguration(s).Items.Count(e => e.Category is BirkNext.SourceDomains.ConfigurationCategory.Authentication or BirkNext.SourceDomains.ConfigurationCategory.Authorization or BirkNext.SourceDomains.ConfigurationCategory.Security);
        var access = SourceAnalysis.SourceEvidenceQueries.IdentityInfrastructure(s).Items.Count;
        var fields = SourceAnalysis.SourceEvidenceQueries.Contracts(s).Items.SelectMany(c => c.Types.SelectMany(t => t.Fields)).Count(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, "(?i)(classification|sensitivity|confidential|securitylevel|security_level)"));
        return config + access + fields == 0 ? null : $"Source context: {config} auth/security configuration entr(ies) · {access} IaC access assignment(s) · {fields} classification-named contract field(s)";
    }

    public static List<ReviewSourceSnapshot> Describe(IReadOnlyList<IqrSourceSnapshot> snapshots) => ReviewSourceEvidenceProvider.Describe(snapshots, Evidence);

    private static string? Problem(IqrSourceSnapshot s) => s.SecurityClassificationEvidence is { Unavailable: null } ? null : s.SecurityClassificationEvidence?.Unavailable ?? NoEvidence;

    /// <summary>
    /// Related-source candidates of a primary snapshot, from explicit evidence only:
    /// (1) a classification type the primary uses but does not declare, declared by another repository's snapshot (exact type name);
    /// (2) a package/project reference (Dependency Review's exact matching) to a repository whose snapshot carries classification or
    /// authorization evidence. A referenced source without classification evidence is not suggested; one whose snapshots predate the evidence
    /// is Needs review (relevance unknown); a reference no snapshot matches is shown only when the primary uses undeclared classification types.
    /// Several repositories matching → Needs review. Never auto-included.
    /// </summary>
    public static List<RelatedSourceCandidate> Candidates(IqrSourceSnapshot primary, IReadOnlyList<IqrSourceSnapshot> all)
    {
        var evidence = primary.SecurityClassificationEvidence;
        if (evidence is null) return [];
        var primaryKey = Identity(primary).Key;
        var others = all.Where(s => Identity(s).Key != primaryKey).ToList();
        var undeclared = evidence.ReferencedTypes.Where(t => !evidence.DeclaredTypes.Contains(t)).ToList();
        var candidates = new Dictionary<string, (string Repository, List<RelatedSourceEvidence> Evidence, HashSet<Guid> Snapshots, bool Ambiguous, string? Unknown)>(StringComparer.Ordinal);
        void Add(string key, string repository, RelatedSourceEvidence item, IEnumerable<Guid> snapshots, bool ambiguous, string? unknown = null)
        {
            if (!candidates.TryGetValue(key, out var entry)) entry = (repository, [], [], false, null);
            entry.Evidence.Add(item);
            entry.Snapshots.UnionWith(snapshots);
            candidates[key] = (entry.Repository, entry.Evidence, entry.Snapshots, entry.Ambiguous || ambiguous, entry.Unknown ?? unknown);
        }
        string Areas(IEnumerable<IqrSourceSnapshot> snapshots) => string.Join(", ", snapshots.SelectMany(RelevantAreas).Distinct().Order().Select(ClassificationLabels.Area));

        // (1) Classification types the primary uses but does not declare.
        foreach (var type in undeclared)
        {
            var declaring = others.Where(s => s.SecurityClassificationEvidence?.DeclaredTypes.Contains(type) == true).GroupBy(s => Identity(s).Key).ToList();
            foreach (var repo in declaring)
                Add(repo.Key, Identity(repo.First()).DisplayName, new("Type reference", $"Declares {type}, which the primary source's classification path constructs but does not declare (exact type name).", 0, []),
                    repo.OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id), ambiguous: declaring.Count > 1);
        }

        // (1b) Security namespaces the primary imports but does not declare (e.g. a shared authorization package), declared by another source.
        var imported = evidence.ReferencedNamespaces.ToDictionary(n => n.Name, StringComparer.Ordinal);
        foreach (var repo in others.Where(s => s.SecurityClassificationEvidence?.DeclaredNamespaces.Any(imported.ContainsKey) == true).GroupBy(s => Identity(s).Key))
        {
            var names = repo.SelectMany(s => s.SecurityClassificationEvidence!.DeclaredNamespaces).Where(imported.ContainsKey).Distinct().Order(StringComparer.Ordinal).ToList();
            Add(repo.Key, Identity(repo.First()).DisplayName, new("Namespace reference",
                    $"Declares {string.Join(", ", names)}, which the primary source imports in {names.Sum(n => imported[n].Files)} file(s) (exact namespace).", 0, names.SelectMany(n => imported[n].Examples).Distinct().Take(5).ToList()),
                repo.Where(s => s.SecurityClassificationEvidence!.DeclaredNamespaces.Any(imported.ContainsKey)).OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id), ambiguous: false);
        }

        // (2) Package / project references, kept only when the referenced source shows classification or authorization evidence. Relevance is
        // judged on the snapshots that carry the evidence: one assessed and not relevant is not suggested; only all-unassessed is "unknown".
        foreach (var reference in ReviewSourceEvidenceProvider.ReferenceCandidates(primary, all))
        {
            var matching = reference.MatchingSnapshotIds.Select(id => all.First(s => s.Id == id)).ToList();
            var relevant = matching.Where(s => RelevantAreas(s).Count > 0 || s.SecurityClassificationEvidence?.DeclaredNamespaces.Any(imported.ContainsKey) == true).ToList();
            var unknown = matching.All(s => s.SecurityClassificationEvidence is null) ? matching : [];
            var areas = Areas(relevant) is { Length: > 0 } a ? a : "security namespace";
            if (reference.State == RelatedSourceState.SnapshotUnavailable)
            {
                if (undeclared.Count == 0) continue;
                foreach (var e in reference.Evidence)
                    Add(reference.RepositoryKey, reference.Repository, e with { Detail = $"{e.Detail} The primary uses classification type(s) it does not declare ({string.Join(", ", undeclared)})." }, [], ambiguous: false);
            }
            else if (relevant.Count > 0)
                foreach (var e in reference.Evidence)
                    Add(reference.RepositoryKey, reference.Repository, e with { Detail = $"{e.Detail} Its snapshot shows {areas} evidence." }, relevant.Select(s => s.Id), ambiguous: reference.State == RelatedSourceState.NeedsReview);
            else if (unknown.Count > 0)
                foreach (var e in reference.Evidence)
                    Add(reference.RepositoryKey, reference.Repository, e, [], ambiguous: true,
                        unknown: "Referenced from source, but its snapshot(s) were analyzed before security classification evidence was captured — analyze it again in Source Analysis to tell whether it is relevant.");
        }

        return candidates.Select(c => new RelatedSourceCandidate
        {
            RepositoryKey = c.Key, Repository = c.Value.Repository, Evidence = c.Value.Evidence,
            State = c.Value.Ambiguous ? RelatedSourceState.NeedsReview : c.Value.Snapshots.Count == 0 ? RelatedSourceState.SnapshotUnavailable : RelatedSourceState.SnapshotAvailable,
            Reason = c.Value.Unknown is { } why && c.Value.Snapshots.Count == 0 ? why
                : c.Value.Ambiguous ? "The match is not unique — review before including."
                : c.Value.Snapshots.Count == 0 ? "Referenced from source, but no analyzed snapshot of this source is available."
                : c.Value.Evidence.Any(e => e.Kind == "Type reference") ? "Declares a classification type the primary source uses."
                : "Referenced by the primary source and carries classification or authorization evidence of its own.",
            MatchingSnapshotIds = [.. all.Where(s => c.Value.Snapshots.Contains(s.Id)).OrderByDescending(s => s.AnalyzedAt).Select(s => s.Id)],
        }).OrderBy(c => c.Repository, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The shared scope rules plus this review's own: every snapshot must carry security classification observations.</summary>
    public static (List<IqrSourceSnapshot>? Selected, string? Error) Validate(ReviewSourceScopeRequest request, IReadOnlyList<IqrSourceSnapshot> snapshots, bool sourceAnalysisEnabled = true) =>
        ReviewSourceEvidenceProvider.Validate(request, snapshots, Problem, sourceAnalysisEnabled);

    /// <summary>The immutable scope descriptor, including suggested sources the review continues without.</summary>
    public static ReviewSourceScope Scope(List<IqrSourceSnapshot> selected, IReadOnlyList<IqrSourceSnapshot> all, IEnumerable<string> excluded) =>
        ReviewSourceEvidenceProvider.Scope(selected, Candidates(selected[0], all), excluded);

    /// <summary>Each selected snapshot's own observations, combined at the review layer with per-snapshot provenance.</summary>
    public static ClassificationSourceEvidence Combine(string environmentId, List<IqrSourceSnapshot> selected, IReadOnlyList<IqrSourceSnapshot> all, IEnumerable<string> excluded, DateTimeOffset now)
    {
        var scope = Scope(selected, all, excluded);
        var evidence = ClassificationSourceAnalyzer.Combine(environmentId, selected.Select(s => ((ReviewSourceProvenance?)Ref(s), s.SecurityClassificationEvidence!)).ToList(), now);
        return evidence with { Scope = scope, Limitations = [.. evidence.Limitations, .. scope.Limitations] };
    }

    public async Task<(ClassificationSourceEvidence? Evidence, string? Error)> ResolveAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default)
    {
        var snapshots = await sources.ListAsync(environmentId, ct);
        var (selected, error) = Validate(scope, snapshots, sources.SourceAnalysisEnabled);
        return selected is null ? (null, error) : (Combine(environmentId, selected, snapshots, scope.ExcludedSuggestions, DateTimeOffset.UtcNow), null);
    }

    public async Task<ClassificationScopeOptions> OptionsAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default)
    {
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        var shared = ReviewSourceEvidenceProvider.Options(sources.SourceAnalysisEnabled, snapshots, Evidence, scope, primary => Candidates(primary, snapshots), Problem);
        var options = new ClassificationScopeOptions
        {
            SourceAnalysisEnabled = shared.SourceAnalysisEnabled, Snapshots = shared.Snapshots, Candidates = shared.Candidates, Scope = shared.Scope, Newer = shared.Newer, Error = shared.Error,
            Coverage = ClassificationSourceCoverage.Rows(null),
        };
        if (shared.Scope is null || scope is null) return options;
        var (selected, _) = Validate(scope, snapshots);
        var evidence = Combine(environmentId, selected!, snapshots, scope.ExcludedSuggestions, DateTimeOffset.UtcNow);
        return options with { Evidence = evidence, Coverage = ClassificationSourceCoverage.Rows(evidence) };
    }
}
