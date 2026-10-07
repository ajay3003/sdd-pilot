using BirkNext.Web.GraphQL;

namespace BirkNext.Web.Models;

/// <summary>Logical entity types of a Specification. Each type is counted on its own; a Test or Clarification
/// related to a Requirement never becomes a Requirement.</summary>
public enum SpecEntityKind { Requirement, Test, Clarification, UserStory, SuccessCriterion }

/// <summary>Relation semantics, always read from <c>Source</c> to <c>Target</c>: a Test Verifies a Requirement,
/// a Clarification Clarifies a Requirement/User Story/Success Criterion, and a Requirement or Test BelongsTo a User Story.</summary>
public enum SpecRelationType { Verifies, Clarifies, BelongsTo }

/// <summary>Why a relation exists. Both are deterministic document evidence; there is no similarity or AI provenance.</summary>
public enum SpecRelationProvenance
{
    /// <summary>The source item sits inside the target's section in the document.</summary>
    StructuralNesting,
    /// <summary>The source item's own text names the target's identifier (for example "FR-004").</summary>
    ExplicitReference,
}

/// <summary>One logical Specification entity with a stable identity. <see cref="Key"/> is the identity used by relations
/// and counts: the normalized document identifier (FR-004) when the document has one, otherwise a deterministic
/// position-based identity following the parser's document order.</summary>
public sealed record SpecLogicalEntity(
    string Key,
    SpecEntityKind Kind,
    string DisplayId,
    bool HasDocumentId,
    string Text,
    string? Given = null,
    string? When = null,
    string? Then = null,
    string? Answer = null)
{
    public bool HasStructuredScenario => Given is not null || When is not null || Then is not null;
}

/// <summary>A many-to-many traceability relation between two logical entities.</summary>
public sealed record SpecRelation(string SourceKey, string TargetKey, SpecRelationType Type, SpecRelationProvenance Provenance);

/// <summary>The single tree-context parent of an entity. It is chosen for navigation only and never replaces the relations.</summary>
public sealed record SpecPrimaryParent(string ParentKey, SpecRelationType Type, SpecRelationProvenance Provenance);

/// <summary>The same document identifier defined more than once. The occurrences are counted as one entity and reported here.</summary>
public sealed record SpecDuplicateIdentity(SpecEntityKind Kind, string DisplayId, int Occurrences);

/// <summary>Relationship metrics read from document structure and explicit references. They are not a coverage verdict:
/// Traceability &amp; Coverage belongs to the shared Requirements Traceability projection.</summary>
public sealed record SpecRelationshipSummary(
    int TestsLinkedToRequirements,
    int TestsWithMultipleRequirementLinks,
    int TestsWithUserStoryContext,
    int ClarificationsLinkedToRequirements,
    int SpecificationLevelClarifications);

/// <summary>Logical entities, relations and primary tree context of one Specification text.</summary>
public sealed class SpecificationEntityProjection
{
    public static readonly SpecificationEntityProjection Empty = new([], [], new Dictionary<string, SpecPrimaryParent>(), []);

    public SpecificationEntityProjection(
        IReadOnlyList<SpecLogicalEntity> entities,
        IReadOnlyList<SpecRelation> relations,
        IReadOnlyDictionary<string, SpecPrimaryParent> primaryParents,
        IReadOnlyList<SpecDuplicateIdentity> duplicates)
    {
        Entities = entities;
        Relations = relations;
        PrimaryParents = primaryParents;
        Duplicates = duplicates;
        _byKey = entities.ToDictionary(e => e.Key, StringComparer.Ordinal);
    }

    private readonly Dictionary<string, SpecLogicalEntity> _byKey;

    public IReadOnlyList<SpecLogicalEntity> Entities { get; }
    public IReadOnlyList<SpecRelation> Relations { get; }
    public IReadOnlyDictionary<string, SpecPrimaryParent> PrimaryParents { get; }
    public IReadOnlyList<SpecDuplicateIdentity> Duplicates { get; }

    public int Count(SpecEntityKind kind) => Entities.Count(e => e.Kind == kind);
    public IEnumerable<SpecLogicalEntity> OfKind(SpecEntityKind kind) => Entities.Where(e => e.Kind == kind);
    public SpecLogicalEntity? Find(string key) => _byKey.GetValueOrDefault(key);
    public SpecPrimaryParent? PrimaryParentOf(string key) => PrimaryParents.GetValueOrDefault(key);

    public IEnumerable<SpecRelation> RelationsFrom(string key) => Relations.Where(r => r.SourceKey == key);

    /// <summary>Distinct target entities of <paramref name="key"/>'s relations of <paramref name="type"/> whose target is <paramref name="targetKind"/>.</summary>
    public IReadOnlyList<SpecLogicalEntity> Targets(string key, SpecRelationType type, SpecEntityKind targetKind) =>
        RelationsFrom(key).Where(r => r.Type == type)
            .Select(r => Find(r.TargetKey)).OfType<SpecLogicalEntity>()
            .Where(e => e.Kind == targetKind)
            .DistinctBy(e => e.Key).ToList();

    /// <summary>Entities whose primary tree parent is <paramref name="parentKey"/>.</summary>
    public IEnumerable<SpecLogicalEntity> PrimaryChildrenOf(string parentKey) =>
        Entities.Where(e => PrimaryParentOf(e.Key)?.ParentKey == parentKey);

    public SpecRelationshipSummary Summary()
    {
        var tests = OfKind(SpecEntityKind.Test).ToList();
        var clarifications = OfKind(SpecEntityKind.Clarification).ToList();
        return new(
            tests.Count(t => Targets(t.Key, SpecRelationType.Verifies, SpecEntityKind.Requirement).Count > 0),
            tests.Count(t => Targets(t.Key, SpecRelationType.Verifies, SpecEntityKind.Requirement).Count > 1),
            tests.Count(t => Targets(t.Key, SpecRelationType.BelongsTo, SpecEntityKind.UserStory).Count > 0),
            clarifications.Count(c => Targets(c.Key, SpecRelationType.Clarifies, SpecEntityKind.Requirement).Count > 0),
            clarifications.Count(c => PrimaryParentOf(c.Key) is null));
    }
}

/// <summary>Review candidate type, owned by the projection so Razor never infers it from label text.</summary>
public enum ReviewCandidateType { Requirement, Test, Clarification }

/// <summary>One analyzer review candidate: an item suggested for human review. It is not a finding, defect or failure.
/// <see cref="EntityKey"/> references the logical entity the candidate is about when the document identifies it deterministically.</summary>
public sealed record SpecReviewCandidate(
    Guid CandidateId,
    ReviewCandidateType Type,
    string Text,
    string? ContextHeading,
    SpecLogicalEntity? Entity);

/// <summary>Counts shown in Specification Review Analysis. Entity counts are unique logical entities of the analyzed
/// Specification; candidate counts are the analyzer's candidate records and are never used as entity counts.</summary>
public sealed record SpecificationReviewSummary(
    int Requirements,
    int Tests,
    int Clarifications,
    int Candidates,
    int RequirementCandidates,
    int TestCandidates,
    int ClarificationCandidates);

public static class ReviewCandidateTypes
{
    public static ReviewCandidateType From(ScenarioKind kind) => kind switch
    {
        ScenarioKind.Requirement => ReviewCandidateType.Requirement,
        ScenarioKind.Test => ReviewCandidateType.Test,
        _ => ReviewCandidateType.Clarification,
    };

    public static string Label(ReviewCandidateType type) => type switch
    {
        ReviewCandidateType.Requirement => "Requirement",
        ReviewCandidateType.Test => "Test",
        _ => "Clarification",
    };
}
