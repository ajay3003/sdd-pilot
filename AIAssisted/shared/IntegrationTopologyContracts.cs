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

public sealed record TopologyComponent
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
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
    public TopologyStatus Status { get; init; }
    public List<TopologyProvenance> Provenance { get; init; } = [];
    public List<string> Notes { get; init; } = [];
}

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
