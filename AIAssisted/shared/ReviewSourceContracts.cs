using System.Text.Json.Serialization;

namespace BirkNext.SourceEvidence;

// ── Source Analysis as a shared source-evidence provider ─────────────────────────────────────────────────────────────────────
// Source Analysis owns source ingestion: ZIP upload, immutable snapshots, fingerprints and history. Review features CONSUME snapshots
// through one scope model (one primary + explicitly included related snapshots, never merged) and keep their own interpretation:
// Source Analysis says "this source contains X", a review says "for my review, X means Y". Runtime evidence never depends on it.
// These types are metadata and provenance only — raw source content never reaches the browser.

/// <summary>Which repository a source snapshot belongs to — derived from the archive (root solution file, else archive name). Never merged.</summary>
public sealed record SourceRepositoryIdentity(string Key, string DisplayName, string Basis);

/// <summary>Whether a review needs source for some of its checks or only uses it as optional enrichment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewSourceRequirement { RequiredForSourceChecks, OptionalEnrichment }

/// <summary>The source-evidence gate of a direct source consumer. Guidance only — never a review result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewSourceGateState
{
    SourceAnalysisDisabled, NoSnapshotAvailable, SnapshotAvailableNotSelected, Selected, NewerSnapshotAvailable, SelectedSnapshotUnavailable,
    RelatedSourceSuggested, RequiredRelatedSourceUnavailable,
}

/// <summary>Source state of an optional consumer: it never blocks the review.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewSourceOptionalState { NotSelected, Available, Selected, Unavailable }

/// <summary>One immutable Source Analysis snapshot as a review sees it (metadata only). The consumer fields say whether THIS review
/// can use the snapshot (e.g. it predates the consumer's evidence) — snapshot exists is not the same as evidence assessable.</summary>
public sealed record ReviewSourceSnapshot
{
    public Guid SnapshotId { get; init; }
    public string RepositoryKey { get; init; } = "";
    public string Repository { get; init; } = "";
    public string IdentityBasis { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    /// <summary>The Source Analysis extraction status of the snapshot — never the consumer's review result.</summary>
    public string SourceStatus { get; init; } = "";
    /// <summary>Newest snapshot of its repository in Source Analysis.</summary>
    public bool Latest { get; init; }
    public bool HasConsumerEvidence { get; init; } = true;
    /// <summary>
    /// The snapshot's own analysis holds the review's subject (e.g. a classification model for Security Classification), not merely an extraction
    /// result that may be empty. Null when the review does not distinguish; a role-based recommendation then falls back to <see cref="HasConsumerEvidence"/>.
    /// </summary>
    public bool? HasConsumerSubject { get; init; }
    public string? ConsumerEvidenceNote { get; init; }
    /// <summary>A short consumer-written summary of what the snapshot holds for this review (e.g. "397 declared dependencies").</summary>
    public string? ConsumerSummary { get; init; }
}

// ── Baseline eligibility (shared by every change/drift review) ──────────────────────────────────────────────────────────────
// A baseline is comparable when it has the current snapshot's repository identity. A snapshot of another identity is never matched by a similar
// name (M2LB vs M2LB-language) and never hidden: it is listed with the reason, and it can be compared only after the reviewer explicitly declares
// both are versions of the same repository. That declaration is recorded with the result as such — declared, not detected.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BaselineRelationKind { SameRepositoryIdentity, DeclaredByReviewer }

/// <summary>How the baseline of a stored result relates to its current snapshot (provenance of the comparison).</summary>
public sealed record BaselineRelation(BaselineRelationKind Kind, string Explanation);

/// <summary>
/// A reviewer's remembered declaration that two repository identities are versions of the same repository (e.g. a feature-branch ZIP and the main
/// ZIP). Never detected and never inferred from names: it exists only because a person declared it, with who and when. Revoking it ends it for
/// future comparisons; results recorded with it keep their relation.
/// </summary>
public sealed record SourceRepositoryAssociation(Guid Id, string RepositoryKeyA, string RepositoryA, string RepositoryKeyB, string RepositoryB, string DeclaredBy,
    DateTimeOffset DeclaredAt, string? Note)
{
    public bool Links(string keyA, string keyB) =>
        (string.Equals(RepositoryKeyA, keyA, StringComparison.Ordinal) && string.Equals(RepositoryKeyB, keyB, StringComparison.Ordinal))
        || (string.Equals(RepositoryKeyA, keyB, StringComparison.Ordinal) && string.Equals(RepositoryKeyB, keyA, StringComparison.Ordinal));

    public string Explanation => $"Declared by {DeclaredBy} on {DeclaredAt.UtcDateTime:yyyy-MM-dd}: {RepositoryA} and {RepositoryB} are versions of the same repository (remembered; not detected — their repository identities differ).";
}

public sealed record DeclareRepositoryAssociationRequest(string RepositoryKeyA, string RepositoryA, string RepositoryKeyB, string RepositoryB, string? Note = null);

/// <summary>One possible baseline for the current snapshot: eligible as is (same identity, or a remembered declaration), or only after the reviewer
/// confirms the repositories are the same.</summary>
public sealed record BaselineCandidate(ReviewSourceSnapshot Snapshot, bool Eligible, string Reason)
{
    public bool NeedsConfirmation => !Eligible;
    /// <summary>The remembered declaration that makes a different identity eligible (null for the same identity or an unconfirmed one).</summary>
    public SourceRepositoryAssociation? Association { get; init; }
}

public static class BaselineEligibility
{
    /// <summary>Every other snapshot as a baseline candidate: same repository identity first (newest first), then the others with the reason they need
    /// confirmation. Nothing is filtered out silently.</summary>
    public static IReadOnlyList<BaselineCandidate> For(IReadOnlyList<ReviewSourceSnapshot> snapshots, ReviewSourceSnapshot? current,
        IReadOnlyList<SourceRepositoryAssociation>? associations = null)
    {
        if (current is null) return [];
        return [.. snapshots.Where(s => s.SnapshotId != current.SnapshotId)
            .Select(s => s.RepositoryKey == current.RepositoryKey
                ? new BaselineCandidate(s, true, $"Same repository identity ({s.Repository}; {s.IdentityBasis.ToLowerInvariant()}).")
                : (associations ?? []).FirstOrDefault(a => a.Links(s.RepositoryKey, current.RepositoryKey)) is { } remembered
                    ? new BaselineCandidate(s, true, remembered.Explanation) { Association = remembered }
                    : new BaselineCandidate(s, false, $"Different repository identity: {s.Repository} ({s.IdentityBasis.ToLowerInvariant()}) vs {current.Repository}. Compare only if both are versions of the same repository — you must confirm that explicitly; similar names are never treated as the same repository."))
            .OrderByDescending(c => c.Eligible && c.Association is null).ThenByDescending(c => c.Eligible).ThenByDescending(c => c.Snapshot.AnalyzedAt)];
    }

    /// <summary>Why no baseline can be chosen at all (null when at least one candidate exists).</summary>
    public static string? Unavailable(IReadOnlyList<ReviewSourceSnapshot> snapshots, ReviewSourceSnapshot? current) =>
        current is null ? "Choose the current snapshot first."
        : snapshots.All(s => s.SnapshotId == current.SnapshotId) ? "No other analyzed snapshot exists. Analyze another revision (for example a feature branch or the main branch) in Source Analysis to compare against it."
        : null;

    /// <summary>The relation of a requested baseline to the current snapshot, or the reason it is refused. A different identity is accepted only with the
    /// reviewer's explicit confirmation, and is then recorded as declared.</summary>
    public static (BaselineRelation? Relation, string? Error) Validate(SourceRepositoryIdentity current, SourceRepositoryIdentity baseline, bool confirmedSameRepository,
        SourceRepositoryAssociation? remembered = null) =>
        baseline.Key == current.Key
            ? (new(BaselineRelationKind.SameRepositoryIdentity, $"Same repository identity ({current.DisplayName}; {current.Basis.ToLowerInvariant()})."), null)
            : remembered is not null && remembered.Links(current.Key, baseline.Key)
                ? (new(BaselineRelationKind.DeclaredByReviewer, remembered.Explanation), null)
            : confirmedSameRepository
                ? (new(BaselineRelationKind.DeclaredByReviewer,
                    $"Declared by the reviewer: {baseline.DisplayName} and {current.DisplayName} are versions of the same repository. Not detected — their repository identities differ."), null)
                : (null, $"The baseline belongs to another repository identity ({baseline.DisplayName} vs {current.DisplayName}). Confirm that both are versions of the same repository to compare them.");
}

/// <summary>An exact snapshot in a review scope, as it was at run time.</summary>
public sealed record SourceScopeEntry
{
    public Guid SnapshotId { get; init; }
    public string RepositoryKey { get; init; } = "";
    public string Repository { get; init; } = "";
    public string ArchiveName { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    public string SourceStatus { get; init; } = "";
}

/// <summary>The source scope a review asks for: one primary snapshot plus explicitly included related snapshots.</summary>
public sealed record ReviewSourceScopeRequest
{
    public Guid PrimarySnapshotId { get; init; }
    public List<Guid> RelatedSnapshotIds { get; init; } = [];
    /// <summary>Suggested related sources the user chose to continue without; recorded as a scope limitation.</summary>
    public List<string> ExcludedSuggestions { get; init; } = [];
}

/// <summary>The immutable source scope a review ran on: exact snapshots and fingerprints. Two snapshots stay two entries — never merged.</summary>
public sealed record ReviewSourceScope
{
    public SourceScopeEntry Primary { get; init; } = new();
    public List<SourceScopeEntry> Related { get; init; } = [];
    public List<string> ExcludedSuggestions { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

/// <summary>Where a piece of source evidence came from: one exact snapshot, with the locations in it.</summary>
public sealed record ReviewSourceProvenance
{
    public Guid SnapshotId { get; init; }
    public string Repository { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    /// <summary>File:line locations inside that snapshot (empty when the evidence is snapshot-wide).</summary>
    public List<BirkNext.Integrations.SourceLocation> Locations { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelatedSourceState { SnapshotAvailable, SnapshotUnavailable, NeedsReview }

public sealed record RelatedSourceEvidence(string Kind, string Detail, int Projects, List<string> Examples);

/// <summary>A source the primary snapshot appears to depend on. Suggested only — never included without an explicit choice. Which
/// references count is decided by each review (its relevance rules), not by Source Analysis.</summary>
public sealed record RelatedSourceCandidate
{
    public string RepositoryKey { get; init; } = "";
    public string Repository { get; init; } = "";
    public RelatedSourceState State { get; init; }
    public string Confidence { get; init; } = "Suggested";
    public string Reason { get; init; } = "";
    public List<RelatedSourceEvidence> Evidence { get; init; } = [];
    /// <summary>Snapshots of the candidate repository that carry the matched identity, newest first.</summary>
    public List<Guid> MatchingSnapshotIds { get; init; } = [];
}

/// <summary>What a source-aware review page needs to show its source scope: Source Analysis snapshots, related-source candidates of
/// the chosen primary, the resolved scope, newer snapshots of the scope's repositories and a scope problem. Read-only.</summary>
public record ReviewSourceOptions
{
    public bool SourceAnalysisEnabled { get; init; } = true;
    public List<ReviewSourceSnapshot> Snapshots { get; init; } = [];
    public List<RelatedSourceCandidate> Candidates { get; init; } = [];
    public ReviewSourceScope? Scope { get; init; }
    /// <summary>Remembered repository declarations (active only) — what makes a different identity an eligible baseline.</summary>
    public List<SourceRepositoryAssociation> RepositoryAssociations { get; init; } = [];
    /// <summary>Newer snapshots of the scope's repositories. Shown, never switched to automatically.</summary>
    public List<ReviewSourceSnapshot> Newer { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>Pure source-scope rules shared by the backend provider and the frontend components.</summary>
public static class ReviewSourceScopes
{
    public const string OpenSourceAnalysis = "Open Source Analysis";
    public const string SourceAnalysisRoute = "source-analysis";

    public static string Short(string fingerprint) => fingerprint.Length > 8 ? fingerprint[..8] + "…" : fingerprint;

    public static string Label(SourceScopeEntry e) => $"{e.Repository} · {Short(e.Fingerprint)}";

    public static string Label(ReviewSourceScope scope) => string.Join(" + ", new[] { scope.Primary }.Concat(scope.Related).Select(Label));

    /// <summary>For each repository in the scope, its newest snapshot when that is newer than the scope's own (never substituted).</summary>
    public static List<ReviewSourceSnapshot> Newer(IReadOnlyList<ReviewSourceSnapshot> snapshots, IEnumerable<Guid> scopeIds)
    {
        var ids = scopeIds.ToHashSet();
        return snapshots.Where(s => ids.Contains(s.SnapshotId)).Select(s => (Current: s, Latest: snapshots.Where(x => x.RepositoryKey == s.RepositoryKey).MaxBy(x => x.AnalyzedAt)!))
            .Where(x => x.Latest.SnapshotId != x.Current.SnapshotId && x.Latest.AnalyzedAt > x.Current.AnalyzedAt && !ids.Contains(x.Latest.SnapshotId))
            .Select(x => x.Latest).DistinctBy(s => s.SnapshotId).ToList();
    }

    /// <summary>The gate state of a direct source consumer, in priority order (disabled, nothing to choose, nothing chosen, chosen but broken,
    /// then what the user may want to act on). Required related sources are only those the consumer says it truly needs.</summary>
    public static ReviewSourceGateState Gate(bool sourceAnalysisEnabled, int snapshots, bool primarySelected, bool selectionUnavailable, int newer, int suggestionsNotIncluded,
        bool requiredRelatedUnavailable = false) =>
        !sourceAnalysisEnabled ? ReviewSourceGateState.SourceAnalysisDisabled
        : primarySelected && selectionUnavailable ? ReviewSourceGateState.SelectedSnapshotUnavailable
        : snapshots == 0 ? ReviewSourceGateState.NoSnapshotAvailable
        : !primarySelected ? ReviewSourceGateState.SnapshotAvailableNotSelected
        : requiredRelatedUnavailable ? ReviewSourceGateState.RequiredRelatedSourceUnavailable
        : newer > 0 ? ReviewSourceGateState.NewerSnapshotAvailable
        : suggestionsNotIncluded > 0 ? ReviewSourceGateState.RelatedSourceSuggested
        : ReviewSourceGateState.Selected;

    /// <summary>An optional consumer's source state: absent source never blocks it.</summary>
    public static ReviewSourceOptionalState Optional(bool sourceAnalysisEnabled, int snapshots, bool selected) =>
        !sourceAnalysisEnabled ? ReviewSourceOptionalState.Unavailable : selected ? ReviewSourceOptionalState.Selected
        : snapshots > 0 ? ReviewSourceOptionalState.Available : ReviewSourceOptionalState.NotSelected;

    public static string GateLabel(ReviewSourceGateState state) => state switch
    {
        ReviewSourceGateState.SourceAnalysisDisabled => "Source Analysis disabled",
        ReviewSourceGateState.NoSnapshotAvailable => "No snapshot available",
        ReviewSourceGateState.SnapshotAvailableNotSelected => "Not selected",
        ReviewSourceGateState.Selected => "Selected",
        ReviewSourceGateState.NewerSnapshotAvailable => "Newer snapshot available",
        ReviewSourceGateState.SelectedSnapshotUnavailable => "Needs repair",
        ReviewSourceGateState.RelatedSourceSuggested => "Related source suggested",
        _ => "Required related source unavailable",
    };

    public static string OptionalLabel(ReviewSourceOptionalState state) => state switch
    {
        ReviewSourceOptionalState.Selected => "Selected",
        ReviewSourceOptionalState.Available => "Available",
        ReviewSourceOptionalState.Unavailable => "Unavailable",
        _ => "Not selected",
    };
}
