using System.Text.Json.Serialization;

namespace BirkNext.Scopes;

// ── Shared Scope Registry: one project-scoped scope identity that every module can reference ─────────────────────────────
// A scope is a reusable, versioned selection (systems, scenarios, requirements, contracts, test data …) referenced by typed stable ids.
// It owns membership only: contracts, test data, evidence and environment configuration stay in their own stores. A revision is immutable
// once approved; editing an approved scope starts a new draft revision. Approval pins membership — it never means tests passed or that
// runtime execution is authorized. Selecting scopes is a filter, never an authorization boundary.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopePurpose { FunctionalQuality, Test, IntegrationE2E, ReleaseDelivery, Other }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeState { Draft, InReview, Approved, Archived }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeRevisionState { Draft, InReview, Approved, Superseded }

/// <summary>What a member references, by the owner's stable id (never a display name). Append only: stored JSON uses the names.
/// <see cref="CriticalE2EFlow"/> = a Critical E2E flow definition id (CriticalE2EFlowDefinition.Id) in one Target Environment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeMemberKind { System, Relationship, Scenario, Requirement, Feature, UserStory, AcceptanceCriterion, Journey, Contract, TestSuite, TestCase, TestDataAsset, CriticalE2EFlow }

/// <summary>Confirmed by a person; Candidate = proposed from evidence, not yet a member; Excluded = explicitly kept out.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeMemberInclusion { Confirmed, Candidate, Excluded }

/// <summary>One typed member reference of a scope revision.</summary>
public sealed record ScopeMember
{
    public ScopeMemberKind Kind { get; init; }
    /// <summary>The owner's stable id (system/entity id, scenario id, requirement id, contract key, TestDataAssetId …).</summary>
    public string TargetId { get; init; } = "";
    /// <summary>Display label at the time of selection (informational; identity is <see cref="TargetId"/>).</summary>
    public string Label { get; init; } = "";
    public ScopeMemberInclusion Inclusion { get; init; } = ScopeMemberInclusion.Confirmed;
    /// <summary>Where the reference came from ("Selected by a person", "Integration Topology candidate", …).</summary>
    public string Provenance { get; init; } = "";
    public string? Rationale { get; init; }
    /// <summary>The catalog context the id was resolved in (the canonical interpretation scope for systems, requirements, scenarios, contracts;
    /// the environment for journeys; null for the global Test Data catalog). Kept so a member is never re-read in another project's catalog.
    /// Not part of the membership fingerprint.</summary>
    public string? CatalogScope { get; init; }
    [JsonIgnore] public string Key => $"{Kind}:{TargetId}";
}

public sealed record ScopeRevisionView
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public ScopeRevisionState State { get; init; }
    public List<ScopeMember> Members { get; init; } = [];
    public string? ChangeSummary { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = "";
    public DateTimeOffset? ApprovedAt { get; init; }
    public string? ApprovedBy { get; init; }
    public string? ApprovalRationale { get; init; }
    public string ContentFingerprint { get; init; } = "";
}

public sealed record ScopeSummary
{
    public Guid Id { get; init; }
    public string ProjectKey { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ScopePurpose Purpose { get; init; }
    public string? Owner { get; init; }
    public ScopeState State { get; init; }
    /// <summary>The newest revision (draft or approved) and the newest approved revision, if any.</summary>
    public int CurrentRevision { get; init; }
    public int? ApprovedRevision { get; init; }
    public int MemberCount { get; init; }
    public Dictionary<ScopeMemberKind, int> MembersByKind { get; init; } = [];
    /// <summary>Optimistic-concurrency token: a save must send the version it was based on.</summary>
    public int Version { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ScopeDetail
{
    public ScopeSummary Scope { get; init; } = new();
    public List<ScopeRevisionView> Revisions { get; init; } = [];
}

/// <param name="CatalogScope">The catalog context new members were chosen from (the active Target Environment's interpretation scope); every new
/// member is checked against its catalog before anything is saved.</param>
public sealed record CreateScopeRequest(string ProjectKey, string Name, string? Description, ScopePurpose Purpose, string? Owner, List<ScopeMember> Members,
    string? CatalogScope = null);

/// <summary>Edits a scope. When its newest revision is approved, the edit starts a new draft revision (the approved one never changes).</summary>
public sealed record SaveScopeDraftRequest(int ExpectedVersion, string Name, string? Description, ScopePurpose Purpose, string? Owner, List<ScopeMember> Members, string? ChangeSummary,
    string? CatalogScope = null);

public sealed record ScopeDecisionRequest(int ExpectedVersion, int Revision, string? Rationale);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeMemberChange { Added, Removed, InclusionChanged, Unchanged }

public sealed record ScopeMemberDifference(ScopeMemberChange Change, ScopeMember? Before, ScopeMember? After);

public sealed record ScopeComparison(Guid ScopeId, int FromRevision, int ToRevision, List<ScopeMemberDifference> Differences)
{
    [JsonIgnore] public int Added => Differences.Count(d => d.Change == ScopeMemberChange.Added);
    [JsonIgnore] public int Removed => Differences.Count(d => d.Change == ScopeMemberChange.Removed);
}

/// <summary>One member of a multi-scope selection: listed once, with every scope (and pinned revision) that contributed it.</summary>
public sealed record ScopeSelectionMember(ScopeMember Member, List<ScopeContribution> ContributedBy);

public sealed record ScopeContribution(Guid ScopeId, string ScopeName, int Revision);

/// <summary>
/// The union of one or several scopes' confirmed members (approved revision by default), de-duplicated by typed id. Per-scope counts and the
/// combined unique count are both kept so overlapping scopes never double-count. Candidates and exclusions are not members.
/// </summary>
public sealed record ScopeSelection
{
    public string ProjectKey { get; init; } = "";
    public List<ScopeContribution> Scopes { get; init; } = [];
    public List<ScopeSelectionMember> Members { get; init; } = [];
    public Dictionary<Guid, int> PerScopeCounts { get; init; } = [];
    /// <summary>Scopes named in the request that are not in this project or have no approved revision (when approved was required).</summary>
    public List<string> Notices { get; init; } = [];
    [JsonIgnore] public int UniqueCount => Members.Count;
}

// ── Member catalogs: what can be added to a scope, read from the owning stores (never copied) ────────────────────────────────────────────

/// <summary>One thing a person can add to a scope: the owner's stable id, a readable name and where it comes from. Values are never included.</summary>
public sealed record ScopeCatalogItem
{
    public ScopeMemberKind Kind { get; init; }
    public string TargetId { get; init; } = "";
    public string Label { get; init; } = "";
    /// <summary>Short readable description (requirement text, scenario source, asset kind …); never a value.</summary>
    public string? Description { get; init; }
    /// <summary>Owning module ("Canonical interpretation (Source Analysis)", "Test Data", "Integration Journeys").</summary>
    public string SourceModule { get; init; } = "";
    /// <summary>The owner's own state wording (approved/unapproved, declared, candidate …): guidance, not a scope decision.</summary>
    public string? State { get; init; }
    /// <summary>A finer type than the member kind (Service under System, Feature module …).</summary>
    public string? SubKind { get; init; }
    public string? CatalogScope { get; init; }
}

public sealed record ScopeCatalogPage(ScopeMemberKind Kind, List<ScopeCatalogItem> Items, int Total, int Skip, int Take)
{
    /// <summary>Why the catalog is empty or partial (no interpretation, no active environment …); empty when the list is complete.</summary>
    public List<string> Notices { get; init; } = [];
    /// <summary>False when no catalog can verify ids of this kind: such members cannot be added (no fabricated ids).</summary>
    public bool Available { get; init; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeMemberCheck
{
    /// <summary>The id exists in its catalog now.</summary>
    Current,
    /// <summary>The catalog was read and the id is not in it (removed, renamed, or never existed): needs review, never silently replaced.</summary>
    Missing,
    /// <summary>The id was chosen in another project's or environment's catalog.</summary>
    OtherContext,
    /// <summary>No catalog can confirm this kind here (no interpretation yet, no active environment, or no catalog for the kind).</summary>
    NotCheckable,
}

public sealed record ScopeMemberStatus(string Key, ScopeMemberCheck Check, string Detail, string? CurrentLabel = null);

public sealed record ScopeMemberCheckRequest(List<ScopeMember> Members, string? CatalogScope);

/// <summary>The confirmed members of one kind in each scope's newest revision (one call for a whole project, no request per scope).</summary>
public sealed record ScopeMembershipSummary(Guid ScopeId, string Name, ScopeState State, int Revision, List<string> TargetIds);

/// <summary>A scenario linked to a requirement of a scope by an established canonical Verifies relation (explicit, derived or confirmed by a
/// person). Candidates never appear: a requirement only filters scenarios through relations someone can stand behind.</summary>
public sealed record ScopeScenarioLink(string RequirementKey, string ScenarioKey, string Basis);

public sealed record ScopeScenarioLinkRequest(string CatalogScope, List<string> RequirementKeys);

// ── Systems overview: systems, services and their connections for one interpretation scope, with evidence states ────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SystemConnectionState
{
    /// <summary>Proposed by a heuristic or partial evidence; not a connection until confirmed.</summary>
    Candidate,
    /// <summary>Strong or conflicting evidence that a person still has to decide on.</summary>
    NeedsReview,
    /// <summary>Stated by the source (explicit or deterministically derived). Not observed at runtime.</summary>
    FromSourceEvidence,
    ConfirmedByPerson,
    /// <summary>Only with runtime evidence that saw the connection.</summary>
    ObservedAtRuntime,
    Rejected,
}

public sealed record SystemAliasInfo(string Alias, BirkNext.Interpretation.AliasState State, string Source);

/// <summary>A system or service of the latest interpretation, with the services its declared aliases name (never by name similarity).</summary>
public sealed record SystemOverviewItem
{
    public Guid Id { get; init; }
    /// <summary>The canonical entity key ("System|altinn"): the scope member id.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public BirkNext.Interpretation.CanonicalEntityType Type { get; init; }
    /// <summary>Where the identity comes from ("declared system name", "messaging endpoint", "operation owner" …).</summary>
    public string Basis { get; init; } = "";
    public string? Declaration { get; init; }
    public List<SystemAliasInfo> Aliases { get; init; } = [];
    /// <summary>The system whose declared (explicit or confirmed) alias names this service; null when no declaration says so.</summary>
    public string? ParentKey { get; init; }
    public string? ParentBasis { get; init; }
    public List<string> ServiceKeys { get; init; } = [];
    public int Outgoing { get; init; }
    public int Incoming { get; init; }
    /// <summary>Identity questions (proposed aliases, same-as candidates) and connections touching it that a person must decide.</summary>
    public int NeedsReview { get; init; }
    /// <summary>The configured location of the matching Target Environment component (configuration only — not a verified connection).</summary>
    public string? ConfiguredLocation { get; init; }
}

/// <summary>A connection between two systems/services, with the evidence that states it and its review state.</summary>
public sealed record SystemConnectionView
{
    public string Id { get; init; } = "";
    public string SourceKey { get; init; } = "";
    public string SourceName { get; init; } = "";
    public string TargetKey { get; init; } = "";
    public string TargetName { get; init; } = "";
    /// <summary>What the evidence says the connection is ("sends messages via person.barn", "depends on", "provides REST API to").</summary>
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    public SystemConnectionState State { get; init; }
    /// <summary>"Canonical interpretation" or "Integration Topology".</summary>
    public string Origin { get; init; } = "";
    public string? Via { get; init; }
    public List<string> Evidence { get; init; } = [];
    public string Explanation { get; init; } = "";
    /// <summary>The canonical relation key(s) a review decision applies to (empty for topology connections, which are reviewed there).</summary>
    public List<string> RelationKeys { get; init; } = [];
    /// <summary>What this connection does not prove.</summary>
    public string Unverified { get; init; } = "";
}

/// <summary>An identity question about systems (a same-as candidate or a proposed alias), decided in the shared review flow.</summary>
public sealed record SystemIdentityQuestion(string SubjectKey, string Question, BirkNext.Interpretation.ReviewTargetKind TargetKind, string TargetKey, string? OtherKey);

public sealed record SystemsOverviewView
{
    public string Scope { get; init; } = "";
    public Guid? InterpretationId { get; init; }
    public DateTimeOffset? InterpretedAt { get; init; }
    public List<string> SourceLabels { get; init; } = [];
    public List<SystemOverviewItem> Systems { get; init; } = [];
    public List<SystemConnectionView> Connections { get; init; } = [];
    public List<SystemIdentityQuestion> IdentityQuestions { get; init; } = [];
    /// <summary>Set while an analysis of this Target Environment is running in this BirkNext instance (the list shows the previous result).</summary>
    public DateTimeOffset? AnalysisRunningSince { get; init; }
    public string? AnalysisRunningTrigger { get; init; }
    /// <summary>Which sources were read ("Integration Topology: used" …) and what is missing, so an empty list is never read as "nothing exists".</summary>
    public List<string> Sources { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}
