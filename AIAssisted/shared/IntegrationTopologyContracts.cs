using System.Text.Json.Serialization;

namespace BirkNext.IntegrationTopology;

// Integration Topology: the components of an environment, each component's interface inventory (REST, GraphQL, messaging, file/storage,
// external applications …) and the connections between them, every element with the provenance of every source that reported it.
// Status is derived from evidence only: configured ≠ discovered ≠ observed, and nothing here means healthy or compatible.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyInterfaceType { Rest, GraphQl, Messaging, File, Storage, ExternalApplication, Database, Other }

/// <summary>What the component does with the interface: provides (serves) it, calls it, produces to it or consumes from it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyDirection { Provides, Calls, Produces, Consumes, Unknown }

/// <summary>
/// Evidence level, in increasing strength: <see cref="Expected"/> only a domain profile says so; <see cref="Configured"/> configuration
/// declares it; <see cref="Discovered"/> source or a contract proves it exists; <see cref="Observed"/> runtime evidence saw it. Conflict and
/// Needs review are reported beside the level; Not evaluated means no source said anything.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyStatus { NotEvaluated, Expected, Configured, Discovered, Observed, NeedsReview, Conflict }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologySource { TargetEnvironment, OpenApi, GraphQlSchema, SourceAnalysis, IntegrationCatalog, ExternalApplicationMetadata, BrowserObservation, RuntimeEvidence, DomainProfile }

/// <summary>One source's statement about an element: what it is, where it comes from (a safe reference) and when it was captured.</summary>
public sealed record TopologyProvenance(TopologySource Source, string Reference, string Detail, DateTimeOffset? CapturedAt = null);

public sealed record TopologyInterface
{
    public string Id { get; init; } = "";
    public string ComponentId { get; init; } = "";
    public TopologyInterfaceType Type { get; init; }
    public string Protocol { get; init; } = "";
    public TopologyDirection Direction { get; init; }
    /// <summary>Route, topic/queue, container or application reference — never a URL with credentials or a query string.</summary>
    public string Address { get; init; } = "";
    /// <summary>HTTP method, GraphQL operation, or empty.</summary>
    public string? Operation { get; init; }
    public string? ContractReference { get; init; }
    public TopologyStatus Status { get; init; }
    public List<TopologyProvenance> Provenance { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

/// <summary>What a node is: an application, an intermediate resource (queue, topic, event hub, captured table, CDC connector) or an external
/// application. Resource nodes keep a chain resource-centred (producer → resource → consumer) instead of flattening it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyComponentKind { Application, Resource, External }

public sealed record TopologyComponent
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public TopologyComponentKind Kind { get; init; }
    public string? ComponentType { get; init; }
    /// <summary>Where it runs (base address or host) when a source states it; null otherwise.</summary>
    public string? Location { get; init; }
    public TopologyStatus Status { get; init; }
    public List<TopologyProvenance> Provenance { get; init; } = [];
    public List<TopologyInterface> Interfaces { get; init; } = [];
}

public sealed record TopologyConnection
{
    public string Id { get; init; } = "";
    public string ProducerComponentId { get; init; } = "";
    public string? ProducerInterfaceId { get; init; }
    public string ConsumerComponentId { get; init; } = "";
    public string? ConsumerInterfaceId { get; init; }
    public TopologyInterfaceType Type { get; init; }
    public string Protocol { get; init; } = "";
    public string? Address { get; init; }
    public string? ContractReference { get; init; }
    /// <summary>Edge annotation for <see cref="ContractReference"/>: where the contract statement comes from and how far the relationship is
    /// reviewed ("Configured contract reference · relationship review required"). A contract annotates an existing connection — it never creates one.</summary>
    public string? ContractStatus { get; init; }
    public TopologyStatus Status { get; init; }
    public List<TopologyProvenance> Provenance { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    /// <summary>Why BirkNext believes this connection: one entry per independent piece of evidence, each with its strength.</summary>
    public List<TopologyEvidence> Evidence { get; init; } = [];
    /// <summary>The relationship state the evidence supports (never Expected but unresolved: an expectation alone creates no connection).</summary>
    public TopologyRelationshipState Relationship { get; init; }
    /// <summary>What the evidence does not prove (always present: a resolved connection is not observed, healthy or compatible).</summary>
    public string Unverified { get; init; } = "";
}

/// <summary>
/// How much one piece of evidence proves. Very strong alone resolves a connection (configured integration with explicit ends, direct module
/// address injection, explicit client target + matching target host, explicit producer/consumer of a named resource); two strong pieces from
/// independent sources resolve it; supporting evidence never does; weak evidence (role assignment, same namespace or project, similar names)
/// never creates a connection at all.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyEvidenceStrength { Weak, Supporting, Strong, VeryStrong }

/// <summary>
/// Relationship states in increasing certainty: Expected but unresolved (an expectation only) → Candidate (one side or partial evidence) →
/// Strongly supported (several strong pieces of evidence, canonical resolution not yet established) → Resolved (the producer/consumer identity
/// is established by evidence) → Observed (resolved and seen at runtime). Resolved never means observed, healthy, contract-compatible or tested;
/// runtime observation is a separate dimension the UI shows beside the confidence.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyRelationshipState { ExpectedUnresolved, Candidate, StronglySupported, Resolved, Observed }

/// <summary>One piece of relationship evidence: its family (<see cref="Basis"/>, e.g. "http-client-target"), strength, the source family it
/// comes from ("Application source", "Infrastructure as Code" …) and the provenance reference (an archive name is provenance only).</summary>
public sealed record TopologyEvidence(string Basis, TopologyEvidenceStrength Strength, string Source, string Detail, string? Reference = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologySourceState { Used, NotAvailable, NotRequested }

/// <summary>Which sources contributed, which were not available and why — so a missing source is never read as "nothing exists".</summary>
public sealed record TopologySourceSummary(TopologySource Source, TopologySourceState State, string Detail, DateTimeOffset? CapturedAt = null);

public sealed record IntegrationTopologyView
{
    public string EnvironmentId { get; init; } = "";
    public string? EnvironmentName { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public List<TopologyComponent> Components { get; init; } = [];
    public List<TopologyConnection> Connections { get; init; } = [];
    public List<TopologySourceSummary> Sources { get; init; } = [];
    public List<string> Profiles { get; init; } = [];
    /// <summary>Version of the relationship resolver that produced this projection. Projections are rebuilt from stored source facts on every read;
    /// source snapshots are never rewritten, so a resolver improvement applies to existing snapshots without re-import.</summary>
    public int ResolverVersion { get; init; }
    /// <summary>What "Discover interfaces" would read, computed without any network call.</summary>
    public TopologyDiscoveryPlan Discovery { get; init; } = new([], []);
}

/// <summary>OpenAPI interface discovery before it runs (computed offline, no network call): the components that declare an OpenAPI document and
/// the hosts that would be queried (one GET each, own host, no redirects), deduplicated by scheme, host, port and document path. Ineligible
/// components say why. Zero eligible targets means nothing to read over the network — never that no interface evidence exists.</summary>
public sealed record TopologyDiscoveryPlan(List<TopologyDiscoveryTarget> Eligible, List<TopologyDiscoveryTarget> Ineligible)
{
    public List<string> Hosts => [.. Eligible.Select(item => item.Host).Where(host => host.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
}

public sealed record TopologyDiscoveryTarget(string Component, string Host, string Path, string? Reason = null);

// ── Source coverage: which logical sources a Test Pack's expected topology needs, and which expected relationships the evidence resolves ──

/// <summary>Required: needed for core topology. Recommended: needed for full infrastructure topology. Optional: adds evidence, never needed.
/// Conditional: needed only to prove specific relationships. Built-in reference: shipped with BirkNext, no import.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologySourceRequirement { Required, Recommended, Optional, Conditional, BuiltInReference }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologySourceRoleState { Available, PartlyAvailable, NeedsAnalysis, NotProvided, ReferenceAvailable, ReferenceUnavailable, AvailableNeedsBinding, Bound, Ambiguous }

/// <summary>What resolves a source-role or relationship gap, chosen from the actual evidence state (never "import" when the source already
/// exists): import an absent source, bind an existing one in IQR, review several plausible candidates, look at the missing evidence in a
/// bound/available source, or configure the integration that would state the relationship.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyGapAction { None, ImportSource, BindSource, ReviewCandidates, ReanalyzeSource, ViewMissingEvidence, ConfigureIntegration, ViewEvidence }

/// <summary>One logical source role and how the current sources cover it. Matching sources are archive names as provenance; the role is
/// recognized by content (components, configuration, infrastructure), never by file name. Presence and binding are separate: a recognized
/// application source is Bound only through the canonical IQR source-evidence binding (consumed, never stored here); otherwise it is
/// Available — needs binding, or Ambiguous when several distinct snapshots are plausible (the newest is never chosen).</summary>
public sealed record TopologySourceRoleCoverage
{
    public string RoleId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public TopologySourceRequirement Requirement { get; init; }
    public TopologySourceRoleState State { get; init; }
    public List<string> MatchingSources { get; init; } = [];
    public List<string> CapabilitiesProvided { get; init; } = [];
    public List<string> CapabilitiesFound { get; init; } = [];
    /// <summary>Expected parts of the role not found in any matching source (PartlyAvailable).</summary>
    public List<string> MissingParts { get; init; } = [];
    public string Guidance { get; init; } = "";
    /// <summary>What remains unresolved without this source ("the application side of a CDC flow may remain unresolved").</summary>
    public string Consequence { get; init; } = "";
    public string Detail { get; init; } = "";
    /// <summary>Archives (of matching sources) that a canonical IQR binding of this environment points to.</summary>
    public List<string> BoundSources { get; init; } = [];
    /// <summary>When the newest matching snapshot was analyzed (freshness), or null.</summary>
    public DateTimeOffset? AnalyzedAt { get; init; }
    public TopologyGapAction Action { get; init; }
}

/// <summary>A Test Pack's expected relationship, resolved against the evidence. Expected alone never resolves; partial evidence is a candidate.</summary>
public sealed record TopologyRelationshipResult
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Transport { get; init; } = "";
    public string Dimension { get; init; } = "";
    public TopologyRelationshipState State { get; init; }
    public List<string> ConnectionIds { get; init; } = [];
    /// <summary>The resource or route the connection uses, when known.</summary>
    public string? Route { get; init; }
    public List<TopologyEvidence> Evidence { get; init; } = [];
    public string Why { get; init; } = "";
    public string? AvailableEvidence { get; init; }
    public string? MissingEvidence { get; init; }
    public string? SourceRoleId { get; init; }
    public string? SourceRoleName { get; init; }
    public TopologySourceRoleState? SourceRoleState { get; init; }
    public List<string> MatchingSources { get; init; } = [];
    /// <summary>What resolves the gap, from the evidence state of the source role (import / bind / review candidates / view missing evidence /
    /// configure integration), and the feature that owns that fix.</summary>
    public TopologyGapAction Action { get; init; }
    public string? Owner { get; init; }
    public string Unverified { get; init; } = "";
    /// <summary>Runtime observation of the resolved route — a separate dimension from the confidence state.</summary>
    public bool RuntimeObserved { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyCompletenessState { Complete, Partial, NotAvailable, Observed, NotObserved }

/// <summary>Completeness of one topology dimension (Application, Infrastructure, Messaging, Contract, External consumer, Runtime observation) —
/// categorical, never a percentage.</summary>
public sealed record TopologyCompleteness(string Dimension, TopologyCompletenessState State, string Detail);

public sealed record TopologyCoverageView
{
    public string ProfileId { get; init; } = "";
    public string ProfileLabel { get; init; } = "";
    public int ResolverVersion { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public List<TopologySourceRoleCoverage> Roles { get; init; } = [];
    public List<TopologyRelationshipResult> Relationships { get; init; } = [];
    public List<TopologyCompleteness> Completeness { get; init; } = [];
    /// <summary>All Required roles available (core topology sources complete).</summary>
    public bool CoreSourcesComplete { get; init; }
    public string Summary { get; init; } = "";
}

/// <summary>
/// One runtime observation the browser holds (from the existing Endpoint Discovery evidence): method, host and path only — no query, header,
/// cookie or body. Sent to merge as Observed evidence; the backend stores nothing.
/// </summary>
public sealed record TopologyObservation(string Category, string Method, string Host, string Path, string? OperationName, DateTimeOffset LastObservedAt);

public sealed record TopologyRequest(List<TopologyObservation>? Observations, bool DiscoverContracts = false);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TopologyPathState { Resolved, Unresolved, Conflict }

/// <summary>One expected hop of a profile's flow and how the topology resolves it.</summary>
public sealed record TopologyPathStep(string Label, string From, string To, TopologyPathState State, TopologyStatus Evidence, bool Observed, string? ConnectionId, string Detail);

public sealed record TopologyPathResult(string ProfileId, string PathId, string Label, List<TopologyPathStep> Steps)
{
    public bool Resolved => Steps.All(step => step.State == TopologyPathState.Resolved);
}
