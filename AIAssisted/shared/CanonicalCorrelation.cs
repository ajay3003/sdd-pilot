using System.Text.Json.Serialization;

namespace BirkNext.Interpretation;

// Phase 2 of the canonical interpretation engine: the cross-domain correlation vocabulary. Work-item rules (cases A–F) stay in WorkItemInterpretation;
// this file adds canonical entity identity, typed relations, aliases, reviewed associations and the correlation rules for cases G–N. Identity is never
// a display name: every entity is found by (scope, type, identity key), where the identity key comes from a documented per-type rule over source
// identity (content hash, declared namespace, route, explicit id). Every enum persists as text.

/// <summary>What a canonical entity is. A type label, never a status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CanonicalEntityType
{
    Project, Package, Repository, Module, System, Service, Capability, Feature, UserStory, FunctionalGroup, Scenario, Task, Requirement,
    SourceSymbol, ApiOperation, Contract, ContractOccurrence, ContractVersion, MessagingChannel, DataEntity, Test, TestSuite,
    /// <summary>A shared library a package publishes (e.g. M2LB.Common.Health): distinct from the package that contains it and the services that use it.</summary>
    Library,
}

/// <summary>Typed relation between two canonical entities (direction: source → target).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CanonicalRelationType
{
    Contains, BelongsTo, Implements, References, DependsOn, Exposes, Consumes, Produces, Verifies, AliasOf, CandidateSameAs, SameAs, Supersedes, DerivedFrom,
    /// <summary>A source occurrence (file in one package of one snapshot) of a contract version (correlation/2; correlation/1 used BelongsTo).</summary>
    OccurrenceOf,
    /// <summary>A service uses a shared library (project or package reference stated in the source).</summary>
    Uses,
}

/// <summary>How a relation was established. Only <see cref="Explicit"/>, <see cref="Derived"/> and <see cref="Confirmed"/> count as established.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationResolution
{
    /// <summary>The source states it (an explicit id, a route attribute, a declared reference).</summary>
    Explicit,
    /// <summary>Follows deterministically from supported evidence (same content hash, declared ownership).</summary>
    Derived,
    /// <summary>Proposed by a heuristic; never treated as traceability until a human confirms it.</summary>
    Candidate,
    /// <summary>More than one target fits the evidence equally well.</summary>
    Ambiguous,
    /// <summary>A human confirmed it within the decision's scope.</summary>
    Confirmed,
    /// <summary>A human rejected it; heuristics can never bring it back.</summary>
    Rejected,
    /// <summary>The source refers to something that could not be resolved.</summary>
    Unresolved,
}

/// <summary>Strength of the evidence behind a relation (a provenance attribute shown with MetaBadge, never a status colour).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CorrelationConfidence { Explicit, High, Medium, Low }

/// <summary>Lifecycle of a canonical alias. Only Explicit and Confirmed aliases resolve to an entity; Proposed ones are offered as candidates.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AliasState { Proposed, Explicit, Confirmed, Rejected, Historical }

/// <summary>A human review outcome for a proposed relation or alias.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewDecisionOutcome { Confirmed, Rejected, Deferred }

/// <summary>What a review decision applies to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewTargetKind { Relation, Alias }

/// <summary>The reviewed state of a relation after decisions are applied.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationReviewState
{
    /// <summary>No human decision applies.</summary>
    NotReviewed,
    Confirmed,
    Rejected,
    Deferred,
    /// <summary>A decision exists but the evidence it was made on has changed: shown, never silently applied.</summary>
    RevalidationRequired,
    /// <summary>A decision contradicts explicit source evidence (e.g. a rejected link the source now states explicitly).</summary>
    Conflict,
}

/// <summary>One piece of evidence behind an entity or relation (a path, a line, an id; never a payload copy or a secret).</summary>
public sealed record CorrelationEvidence(string Kind, string Reference, string? Detail = null, string? SourceSnapshotId = null);

/// <summary>
/// A canonical entity proposed by ingestion. <see cref="IdentityKey"/> is the stable identity within the scope (the persisted id is generated once and
/// found again by this key); <see cref="DisplayName"/> may change freely; <see cref="SourceIdentity"/> is the identity as the source wrote it.
/// </summary>
public sealed record CanonicalEntityFact(
    CanonicalEntityType Type, string IdentityKey, string DisplayName, string SourceIdentity, string ResolutionMethod,
    IReadOnlyList<CorrelationEvidence> Evidence, string? Version = null, IReadOnlyDictionary<string, string>? Attributes = null)
{
    public string Key => CanonicalKeys.EntityKey(Type, IdentityKey);
    public string? Attribute(string name) => Attributes is not null && Attributes.TryGetValue(name, out var v) ? v : null;
}

/// <summary>A typed relation proposed by a rule, addressed by entity keys (resolved to persisted ids at ingestion).</summary>
public sealed record CanonicalRelationFact(
    string SourceKey, string TargetKey, CanonicalRelationType Type, RelationResolution Resolution, CorrelationConfidence Confidence, string RuleId,
    IReadOnlyList<CorrelationEvidence> Evidence, string Explanation)
{
    /// <summary>Stable relation identity: endpoints + type (the same relation found again on reimport, whatever rule finds it).</summary>
    public string Key => CanonicalKeys.RelationKey(SourceKey, Type, TargetKey);

    /// <summary>Order-independent fingerprint of the evidence; a review decision made on different evidence needs revalidation.</summary>
    public string EvidenceFingerprint => CanonicalKeys.Fingerprint(Evidence.Select(e => $"{e.Kind}|{e.Reference}|{e.Detail}"));

    public bool IsEstablished => Resolution is RelationResolution.Explicit or RelationResolution.Derived or RelationResolution.Confirmed;
}

/// <summary>An alias proposed by ingestion (from a declared profile = Explicit, from a heuristic = Proposed).</summary>
public sealed record CanonicalAliasFact(string EntityKey, string Alias, AliasState State, string Source, string RuleId);

/// <summary>A persisted human decision, as the correlation layer applies it.</summary>
public sealed record ReviewDecisionFact(
    Guid Id, ReviewTargetKind TargetKind, string TargetKey, ReviewDecisionOutcome Outcome, string EvidenceFingerprint, string Reviewer, DateTimeOffset DecidedAt,
    string? Notes, string Scope);

/// <summary>The result of one correlation pass over a scope: entities, relations and aliases with the rule set that produced them.</summary>
public sealed record CorrelationResult(
    IReadOnlyList<CanonicalEntityFact> Entities, IReadOnlyList<CanonicalRelationFact> Relations, IReadOnlyList<CanonicalAliasFact> Aliases,
    IReadOnlyList<string> Limitations, string RuleSetVersion);

/// <summary>Key and fingerprint helpers shared by the backend store and the browser.</summary>
public static class CanonicalKeys
{
    public static string EntityKey(CanonicalEntityType type, string identityKey) => $"{type}|{identityKey}";
    public static string RelationKey(string sourceKey, CanonicalRelationType type, string targetKey) => $"{sourceKey}->{type}->{targetKey}";

    /// <summary>Lower-case, whitespace-collapsed identity text for case-insensitive identity keys (never used on display names alone).</summary>
    public static string Normalize(string? text) =>
        string.Join(' ', (text ?? "").Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string Fingerprint(IEnumerable<string> parts)
    {
        var joined = string.Join("\n", parts.OrderBy(p => p, StringComparer.Ordinal));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Deterministic precedence between evidence and human decisions. A decision applies within its scope while the evidence it was made on is unchanged.
/// Changed evidence never silently keeps or drops a decision: a confirmation needs revalidation (unless the evidence itself now establishes the link),
/// a rejection stays a rejection against heuristics and becomes a visible Conflict when the source now states the link explicitly.
/// </summary>
public static class CorrelationPrecedence
{
    public static (RelationReviewState Review, RelationResolution Effective) Apply(RelationResolution evidence, string evidenceFingerprint, ReviewDecisionFact? decision)
    {
        if (decision is null) return (RelationReviewState.NotReviewed, evidence);
        var unchanged = string.Equals(decision.EvidenceFingerprint, evidenceFingerprint, StringComparison.Ordinal);
        var established = evidence is RelationResolution.Explicit or RelationResolution.Derived;
        return decision.Outcome switch
        {
            ReviewDecisionOutcome.Deferred => (RelationReviewState.Deferred, evidence),
            ReviewDecisionOutcome.Confirmed when unchanged || established => (RelationReviewState.Confirmed, RelationResolution.Confirmed),
            ReviewDecisionOutcome.Confirmed => (RelationReviewState.RevalidationRequired, evidence),
            ReviewDecisionOutcome.Rejected when unchanged => (RelationReviewState.Rejected, RelationResolution.Rejected),
            ReviewDecisionOutcome.Rejected when established => (RelationReviewState.Conflict, evidence),
            _ => (RelationReviewState.RevalidationRequired, RelationResolution.Rejected),
        };
    }

    /// <summary>True when the relation may be counted as traceability (explicit, derived or human-confirmed; never a candidate).</summary>
    public static bool Counts(RelationResolution effective) => effective is RelationResolution.Explicit or RelationResolution.Derived or RelationResolution.Confirmed;

    public static string AliasTargetKey(string entityKey, string alias) => $"{entityKey}~{CanonicalKeys.Normalize(alias)}";
}

// ---- API views (backend → browser) ----

public sealed record CanonicalEntityView(Guid Id, CanonicalEntityType Type, string IdentityKey, string DisplayName, string SourceIdentity, string ResolutionMethod,
    string? Version, IReadOnlyDictionary<string, string> Attributes, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public string Key => CanonicalKeys.EntityKey(Type, IdentityKey);
}

public sealed record CanonicalOccurrenceView(string SourceSnapshotId, string OccurrenceKey, string Kind, string? Detail, DateTimeOffset FirstSeenAt);

public sealed record CanonicalAliasView(Guid Id, Guid EntityId, string Alias, AliasState State, string Source, string RuleId, DateTimeOffset UpdatedAt);

public sealed record CanonicalRelationView(Guid Id, string RelationKey, CanonicalEntityView Source, CanonicalEntityView Target, CanonicalRelationType Type,
    RelationResolution Resolution, RelationResolution Effective, CorrelationConfidence Confidence, string RuleId, string RuleSetVersion,
    IReadOnlyList<CorrelationEvidence> Evidence, string EvidenceFingerprint, string Explanation, RelationReviewState ReviewState, Guid? ReviewDecisionId, bool Active)
{
    public bool Counts => CorrelationPrecedence.Counts(Effective);
    public bool NeedsReview => Active && (ReviewState is RelationReviewState.RevalidationRequired or RelationReviewState.Conflict
        || (ReviewState is RelationReviewState.NotReviewed or RelationReviewState.Deferred && Resolution is RelationResolution.Candidate or RelationResolution.Ambiguous));
}

public sealed record CanonicalEntityDetail(CanonicalEntityView Entity, IReadOnlyList<CanonicalAliasView> Aliases, IReadOnlyList<CanonicalOccurrenceView> Occurrences,
    IReadOnlyList<CanonicalRelationView> Outgoing, IReadOnlyList<CanonicalRelationView> Incoming);

public sealed record ReviewDecisionView(Guid Id, long Sequence, ReviewTargetKind TargetKind, string TargetKey, ReviewDecisionOutcome Outcome, string Reviewer,
    DateTimeOffset DecidedAt, string? Notes, string EvidenceFingerprint, string RuleSetVersion, Guid? SupersedesId, Guid? SupersededById, string Proposed);

public sealed record InterpretationSnapshotView(Guid Id, string Scope, DateTimeOffset CreatedAt, IReadOnlyList<string> RuleSetVersions,
    IReadOnlyList<BirkNext.Snapshots.SnapshotReference> SourceSnapshots, long DecisionSequence, int EntityCount, int RelationCount, string ContentFingerprint,
    IReadOnlyList<string> Limitations, string Trigger, Guid? PreviousId, bool Reused = false);

public sealed record InterpretationRelationChange(string RelationKey, CanonicalRelationType Type, string? Before, string? After);

public sealed record InterpretationComparison(Guid From, Guid To, IReadOnlyList<InterpretationRelationChange> Added, IReadOnlyList<InterpretationRelationChange> Removed,
    IReadOnlyList<InterpretationRelationChange> Changed, IReadOnlyList<string> RuleSetChanges);

public sealed record CorrelationChainView(CanonicalEntityView Start, IReadOnlyList<CanonicalEntityType> Path, IReadOnlyList<CorrelationChainStepView> Steps);

public sealed record CorrelationChainStepView(CanonicalEntityType Type, bool Missing, IReadOnlyList<CanonicalRelationView> Links);

public sealed record AliasResolution(string Name, IReadOnlyList<CanonicalEntityView> Resolved, IReadOnlyList<CanonicalAliasView> Candidates, IReadOnlyList<CanonicalAliasView> Rejected);

public sealed record InterpretRequest(IReadOnlyList<Guid> SourceSnapshotIds, Guid? DeliverySnapshotId = null, string? Trigger = null);

public sealed record ReviewDecisionRequest(ReviewTargetKind TargetKind, string TargetKey, ReviewDecisionOutcome Outcome, string? Reviewer = null, string? Notes = null);

/// <summary>
/// The scope's canonical counts, read from the latest interpretation snapshot's frozen membership (so every consumer shows the same numbers for the
/// same interpretation, and entities left behind by an older rule set are never counted). Each count names what it counts.
/// </summary>
public sealed record CanonicalSummaryView(string Scope, Guid? InterpretationId, DateTimeOffset? InterpretedAt, IReadOnlyList<string> RuleSetVersions,
    IReadOnlyDictionary<string, int> EntitiesByType, IReadOnlyDictionary<string, int> RelationsByType, int EstablishedRelations, int CandidateRelations,
    int NeedsReview, int Conflicts, CanonicalContractCounts Contracts, IReadOnlyList<CanonicalPackageSummary> Packages, IReadOnlyList<string> Limitations);

/// <summary>Logical contracts (declared identity), versions (content + version context), distinct contents (hashes) and source occurrences, kept apart.</summary>
public sealed record CanonicalContractCounts(int LogicalContracts, int Versions, int DistinctContents, int Occurrences, int VersionsWithoutLogicalIdentity);

/// <summary>A package with every evidence capability it provides (never one exclusive role) and the shared libraries it publishes.</summary>
public sealed record CanonicalPackageSummary(Guid Id, string Name, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Libraries);

/// <summary>One page of a scoped listing (the browser never loads the whole graph).</summary>
public sealed record CanonicalPage<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);
