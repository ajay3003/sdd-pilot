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

/// <summary>What a member references, by the owner's stable id (never a display name).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScopeMemberKind { System, Relationship, Scenario, Requirement, Feature, UserStory, AcceptanceCriterion, Journey, Contract, TestSuite, TestCase, TestDataAsset }

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

public sealed record CreateScopeRequest(string ProjectKey, string Name, string? Description, ScopePurpose Purpose, string? Owner, List<ScopeMember> Members);

/// <summary>Edits a scope. When its newest revision is approved, the edit starts a new draft revision (the approved one never changes).</summary>
public sealed record SaveScopeDraftRequest(int ExpectedVersion, string Name, string? Description, ScopePurpose Purpose, string? Owner, List<ScopeMember> Members, string? ChangeSummary);

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
