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
    /// <summary>Tasks grouped by [USn] in a scenario-based spec (case B): not a user story.</summary>
    ScenarioGroup,
    /// <summary>Shared and foundational tasks of a feature (case E): not a missing user story.</summary>
    SharedFoundationGroup,
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
public enum ReviewTargetKind
{
    Relation, Alias,
    /// <summary>An unscoped requirement id (source-only analysis) confirmed as, or rejected as, one feature-pack-scoped requirement (identity/1).</summary>
    Identity,
}

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
    /// <param name="prior">The latest non-deferred decision a Deferred one postponed (null when there is none). Deferring postpones a question; it
    /// never answers one: a conflict or a stale confirmation the prior decision left stays visible (and keeps blocking where it blocks).</param>
    public static (RelationReviewState Review, RelationResolution Effective) Apply(RelationResolution evidence, string evidenceFingerprint, ReviewDecisionFact? decision,
        ReviewDecisionFact? prior = null)
    {
        if (decision is null) return (RelationReviewState.NotReviewed, evidence);
        if (decision.Outcome == ReviewDecisionOutcome.Deferred && prior is { Outcome: not ReviewDecisionOutcome.Deferred }
            && Apply(evidence, evidenceFingerprint, prior) is { Review: RelationReviewState.Conflict or RelationReviewState.RevalidationRequired } held)
            return held;
        // A decision applies unchanged only while the evidence it was made on is still the current evidence (the shared freshness rule).
        var unchanged = BirkNext.Evidence.EvidenceFreshnessRules.CanSupportPositive(BirkNext.Evidence.EvidenceFreshnessRules.Of(decision.EvidenceFingerprint, evidenceFingerprint));
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
    int NeedsReview, int Conflicts, CanonicalContractCounts Contracts, IReadOnlyList<CanonicalPackageSummary> Packages, IReadOnlyList<string> Limitations,
    int Decisions = 0, int Suggestions = 0)
{
    /// <summary>Relations neither established nor a candidate: ambiguous alternatives, unresolved references and rejected relations. Established +
    /// candidates + not counted = every relation of the interpretation (mutually exclusive buckets).</summary>
    public int NotCounted { get; init; }
}

/// <summary>Logical contracts (declared identity), versions (content + version context), distinct contents (hashes) and source occurrences, kept apart.</summary>
public sealed record CanonicalContractCounts(int LogicalContracts, int Versions, int DistinctContents, int Occurrences, int VersionsWithoutLogicalIdentity);

/// <summary>A package with every evidence capability it provides (never one exclusive role) and the shared libraries it publishes.</summary>
public sealed record CanonicalPackageSummary(Guid Id, string Name, IReadOnlyList<string> Capabilities, IReadOnlyList<string> Libraries);

/// <summary>One page of a scoped listing (the browser never loads the whole graph).</summary>
public sealed record CanonicalPage<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

// ---- Requirement identity (identity/1): unscoped source-only requirement ids → reviewed feature-pack-scoped identities ----

/// <summary>Where one unscoped requirement id stands. Candidate/Ambiguous/Unresolved are proposals from the analyzed Delivery snapshot; only Confirmed resolves.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RequirementIdentityState
{
    /// <summary>Exactly one feature pack of the analyzed Delivery snapshot defines the id: a proposal until a person confirms it.</summary>
    Candidate,
    /// <summary>Several feature packs define the id: a person must choose the one it is (there is no default).</summary>
    Ambiguous,
    /// <summary>No feature pack of the analyzed Delivery snapshot defines the id (or no Delivery snapshot was analyzed).</summary>
    Unresolved,
    /// <summary>A person confirmed one scoped identity: every scope consumer resolves the id to it.</summary>
    Confirmed,
    /// <summary>Every proposed feature pack was rejected: the id stays unscoped.</summary>
    Rejected,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdentityTargetState { Candidate, Confirmed, Rejected }

/// <summary>One feature-pack-scoped requirement an unscoped id may be: where the Delivery snapshot defines it and the current decision on it.</summary>
public sealed record RequirementIdentityTarget(string TargetKey, string Module, string Feature, string RequirementId, string DisplayName,
    IReadOnlyList<CorrelationEvidence> Evidence, IdentityTargetState State, string DecisionKey, ReviewDecisionView? Decision);

/// <summary>One unscoped requirement id (of the latest analysis, or of an earlier source-only one when the latest proposes a pack for it), where the
/// source names it, and the scoped identities it may be.</summary>
public sealed record RequirementIdentityRow(string UnscopedKey, string RequirementId, string DisplayName, RequirementIdentityState State,
    IReadOnlyList<CorrelationEvidence> FoundIn, IReadOnlyList<RequirementIdentityTarget> Targets, string? ConfirmedKey, string Explanation, bool InLatestAnalysis = true);

/// <summary>The requirement identity review of one project's latest analysis (empty rows when every requirement is already scoped).</summary>
public sealed record RequirementIdentityView(string Scope, Guid? InterpretationId, IReadOnlyList<BirkNext.Snapshots.SnapshotReference> Sources, bool DeliveryIncluded,
    IReadOnlyList<RequirementIdentityRow> Rows, IReadOnlyList<string> Notices)
{
    public int Count(RequirementIdentityState state) => Rows.Count(r => r.State == state);
}

/// <summary>
/// Pure rules of requirement identity (identity/1). An unscoped id is matched only to scoped requirements a Delivery snapshot of the same analysis
/// defines with the same normalized id — never by name, text or module guessing — and only a person's confirmation of one specific target resolves it.
/// </summary>
public static class RequirementIdentity
{
    public const string RuleSetVersion = "identity/1";
    const string Prefix = "identity:", Arrow = "=>";

    /// <summary>The decision target key of one (unscoped → scoped) pair: each target is decided on its own, so a rejection never confirms another.</summary>
    public static string DecisionKey(string unscopedKey, string scopedKey) => $"{Prefix}{unscopedKey}{Arrow}{scopedKey}";

    public static (string Unscoped, string Scoped)? ParseDecisionKey(string? key)
    {
        if (key is null || !key.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var body = key[Prefix.Length..];
        var arrow = body.IndexOf(Arrow, StringComparison.Ordinal);
        return arrow <= 0 || arrow + Arrow.Length >= body.Length ? null : (body[..arrow], body[(arrow + Arrow.Length)..]);
    }

    /// <summary>A requirement identity without module/feature (a source-only id such as "FR-012").</summary>
    public static bool IsUnscoped(string entityType, string identityKey) =>
        entityType == nameof(CanonicalEntityType.Requirement) && !identityKey.Contains('/');

    /// <summary>The row state from how many feature packs define the id and the current decision on each of them.</summary>
    public static RequirementIdentityState StateOf(IReadOnlyCollection<IdentityTargetState> targets) =>
        targets.Count(t => t == IdentityTargetState.Confirmed) == 1 ? RequirementIdentityState.Confirmed
        : targets.Count == 0 ? RequirementIdentityState.Unresolved
        : targets.All(t => t == IdentityTargetState.Rejected) ? RequirementIdentityState.Rejected
        : targets.Count == 1 ? RequirementIdentityState.Candidate
        : RequirementIdentityState.Ambiguous;
}
