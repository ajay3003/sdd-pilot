using BirkNext.Interpretation;

namespace BirkNext.Web.Services;

/// <summary>
/// How canonical relations read in the UI, through the shared status vocabulary (no local status words or colours). The relation's resolution
/// is a status (Confirmed / Inferred / Needs review / Conflict …) qualified with the precise term; its type and confidence are MetaBadges.
/// A candidate reads as Inferred ("Candidate"), never as confirmed; a rejected link reads as Removed ("Rejected"), never as a failure.
/// </summary>
public static class CorrelationPresentation
{
    /// <summary>A package evidence capability in tester wording (a type label, never a status).</summary>
    public static string CapabilityLabel(string capability) => capability switch
    {
        "ApplicationSource" => "Application source",
        "SharedSource" => "Shared libraries",
        _ => capability,
    };

    public static (SemanticStatus Status, string Label, string Explanation) Status(CanonicalRelationView r) => r.ReviewState switch
    {
        RelationReviewState.Conflict => (SemanticStatus.Conflict, "Conflict",
            "A reviewer rejected this link, but the source now states it explicitly. Review again: neither side is applied silently."),
        RelationReviewState.RevalidationRequired => (SemanticStatus.NeedsReview, "Revalidation required",
            "A reviewer decided on different evidence. The old decision is not applied until someone reviews the new evidence."),
        RelationReviewState.Rejected => (SemanticStatus.Removed, "Rejected",
            "A reviewer rejected this link for this project. Heuristics never bring it back."),
        RelationReviewState.Confirmed => (SemanticStatus.Confirmed, "Confirmed by reviewer", "A reviewer confirmed this link for this project."),
        _ => r.Resolution switch
        {
            RelationResolution.Explicit => (SemanticStatus.Confirmed, "Explicit", "The source states this link (an explicit id, a route attribute, a declared reference)."),
            RelationResolution.Derived => (SemanticStatus.Confirmed, "Derived", "Follows deterministically from supported evidence (same content hash, declared ownership)."),
            RelationResolution.Candidate => (SemanticStatus.Inferred, r.ReviewState == RelationReviewState.Deferred ? "Candidate · deferred" : "Candidate",
                "Proposed by a heuristic. It is not traceability until a reviewer confirms it."),
            RelationResolution.Ambiguous => (SemanticStatus.NeedsReview, "Ambiguous", "More than one interpretation fits the evidence equally well."),
            RelationResolution.Unresolved => (SemanticStatus.Unknown, "Unresolved", "The source refers to something that could not be resolved."),
            _ => (SemanticStatus.Unknown, r.Resolution.ToString(), ""),
        },
    };

    public static (SemanticStatus Status, string Label) AliasStatus(AliasState state) => state switch
    {
        AliasState.Explicit => (SemanticStatus.Confirmed, "Declared"),
        AliasState.Confirmed => (SemanticStatus.Confirmed, "Confirmed by reviewer"),
        AliasState.Proposed => (SemanticStatus.Inferred, "Proposed"),
        AliasState.Rejected => (SemanticStatus.Removed, "Rejected"),
        _ => (SemanticStatus.Historical, "Historical"),
    };

    public static string TypeLabel(CanonicalEntityType type) => type switch
    {
        CanonicalEntityType.UserStory => "User story",
        CanonicalEntityType.FunctionalGroup => "Functional group",
        CanonicalEntityType.SourceSymbol => "Source symbol",
        CanonicalEntityType.ApiOperation => "API operation",
        CanonicalEntityType.ContractOccurrence => "Contract occurrence",
        CanonicalEntityType.ContractVersion => "Contract version",
        CanonicalEntityType.MessagingChannel => "Messaging channel",
        CanonicalEntityType.DataEntity => "Data entity",
        CanonicalEntityType.TestSuite => "Test suite",
        _ => type.ToString(),
    };

    public static string RelationLabel(CanonicalRelationType type) => type switch
    {
        CanonicalRelationType.BelongsTo => "belongs to",
        CanonicalRelationType.DependsOn => "depends on",
        CanonicalRelationType.AliasOf => "alias of",
        CanonicalRelationType.CandidateSameAs => "may be the same as",
        CanonicalRelationType.SameAs => "same as",
        CanonicalRelationType.DerivedFrom => "derived from",
        _ => type.ToString().ToLowerInvariant(),
    };

    /// <summary>Which relations a consumer panel lists first: those that need a decision, then established ones, then the rest.</summary>
    public static IEnumerable<CanonicalRelationView> Ordered(IEnumerable<CanonicalRelationView> relations) =>
        relations.OrderByDescending(r => r.NeedsReview).ThenByDescending(r => r.Counts).ThenBy(r => r.Type).ThenBy(r => r.RelationKey, StringComparer.Ordinal);

    /// <summary>The scope every canonical call uses: the active Target Environment profile id (null when no project is active).</summary>
    public static string? Scope(string? profileId) => string.IsNullOrWhiteSpace(profileId) ? null : profileId;
}
