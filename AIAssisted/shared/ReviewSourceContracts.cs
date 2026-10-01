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
    public string? ConsumerEvidenceNote { get; init; }
    /// <summary>A short consumer-written summary of what the snapshot holds for this review (e.g. "397 declared dependencies").</summary>
    public string? ConsumerSummary { get; init; }
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
