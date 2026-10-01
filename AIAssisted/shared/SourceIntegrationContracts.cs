using System.Text.Json.Serialization;
using BirkNext.SourceArchitecture;

namespace BirkNext.Integrations;

// ── Generic source integrations ──────────────────────────────────────────────────────────────────────────────────────
// Source-to-consumer integrations discovered from one immutable source snapshot. The pattern (Change Data Capture, messaging, API call …) is
// the durable concept; the capture technology (Debezium, a custom publisher, SQL Server CDC …) is an open, source-derived string — never the
// integration type. Detected ≠ Suggested ≠ Confirmed: discovery never confirms a consumer mapping and never writes the catalog; only a person
// does, through the existing confirmation. Source evidence ≠ runtime evidence.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationPattern { ChangeDataCapture, EventDriven, Messaging, ApiCall, ApiPull, ApiPush, Polling, FileTransfer, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationTransport { EventHub, ServiceBusTopic, ServiceBusQueue, ServiceBus, Kafka, Http, GraphQl, Blob, File, Unknown }

/// <summary>How a configured integration relates to the latest source discovery. Never a mapping state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceMatchState
{
    /// <summary>No source snapshot has been analyzed for the environment.</summary>
    NotAnalyzed,
    /// <summary>The source supports the configured channel and consumer (or there is no consumer to compare).</summary>
    Supported,
    /// <summary>The source names a consumer candidate for an integration without an assigned consumer.</summary>
    SourceSuggestion,
    /// <summary>The source names a different consumer than the configured one — review needed; nothing is overwritten.</summary>
    Conflict,
    /// <summary>The configured channel was not found in the analyzed source. Not evidence that the integration is absent.</summary>
    NotFoundInSource,
}

/// <summary>One piece of evidence for one discovered field. Never a configuration value that could be a secret.</summary>
public sealed record SourceFieldEvidence(string Field, string Value, ArchitectureEvidenceState State, ArchitectureEvidenceKind Kind, string File, int Line, string Symbol, string Extractor, string Explanation);

/// <summary>A source integration candidate discovered from one source snapshot. Every field carries its own evidence in <see cref="Evidence"/>.</summary>
public sealed record SourceIntegrationCandidate
{
    /// <summary>Stable within and across snapshots: transport + channel (+ consumer for fan-out), lowercase.</summary>
    public string Id { get; init; } = "";
    public Guid SourceSnapshotId { get; init; }
    public string? SourceSystem { get; init; }
    /// <summary>"Database", "Application component", "External system" …</summary>
    public string? SourceSystemType { get; init; }
    public string? SourceEntity { get; init; }
    /// <summary>"Table", "Component", "Endpoint" …</summary>
    public string? SourceEntityType { get; init; }
    public string? SourceSchema { get; init; }
    public string? SourceDatabase { get; init; }
    public IntegrationPattern Pattern { get; init; } = IntegrationPattern.Unknown;
    /// <summary>Open, source-derived: "Debezium", "Custom publisher", "SQL Server CDC", "Kafka Connect" — or null when not detected.</summary>
    public string? CaptureTechnology { get; init; }
    /// <summary>The producing/calling component in the analyzed source, or null when the producer lives outside it.</summary>
    public string? ProducerComponent { get; init; }
    /// <summary>Client/caller technology on the edge (e.g. "Strawberry Shake (GraphQL client)") — an implementation detail, never a node.</summary>
    public string? CallerTechnology { get; init; }
    /// <summary>Server technology of the target (e.g. "Hot Chocolate (GraphQL server)") — an implementation detail, never a node.</summary>
    public string? ServerTechnology { get; init; }
    public IntegrationTransport Transport { get; init; } = IntegrationTransport.Unknown;
    public string? ChannelName { get; init; }
    /// <summary>Other names the source declares for the same channel (per environment).</summary>
    public List<string> ChannelVariants { get; init; } = [];
    public string? NamespaceName { get; init; }
    public string? DestinationType { get; init; }
    /// <summary>Consumer candidate as the source names it. A candidate is at most a suggestion — never a confirmed mapping.</summary>
    public string? ConsumerCandidate { get; init; }
    public string? ConsumerComponentId { get; init; }
    public ArchitectureEvidenceState ConsumerEvidence { get; init; } = ArchitectureEvidenceState.Unresolved;
    /// <summary>Overall source evidence of the candidate (its weakest decisive field).</summary>
    public ArchitectureEvidenceState EvidenceState { get; init; } = ArchitectureEvidenceState.Inferred;
    public bool IsBusinessIntegration { get; init; } = true;
    public bool IsTechnicalPlatformChannel { get; init; }
    public string? TechnicalPurpose { get; init; }
    public List<SourceFieldEvidence> Evidence { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}

/// <summary>A platform/support channel that is excluded from business integration counts, with why.</summary>
public sealed record TechnicalIntegrationChannel(string Name, IntegrationTransport Transport, string Purpose, string Source, string ExclusionReason);

/// <summary>Source-only signal the discovery reads besides the architecture snapshot (persisted with the snapshot; no source text).</summary>
public sealed record SourceIntegrationSignal(string Kind, string? ComponentId, string Value, ArchitectureEvidence Evidence);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceDiscoveryStatus { Complete, Partial, NotAnalyzed }

/// <summary>Discovery result for exactly one source snapshot (fingerprint + analyzer + extractor versions). Recomputed, never mutated.</summary>
public sealed record SourceIntegrationDiscoveryResult
{
    public Guid SourceSnapshotId { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public string? ArchiveName { get; init; }
    public DateTimeOffset AnalyzedAt { get; init; }
    public int ArchitectureAnalyzerVersion { get; init; }
    public int EngineVersion { get; init; }
    public Dictionary<string, string> ExtractorVersions { get; init; } = [];
    public SourceDiscoveryStatus Status { get; init; }
    public List<SourceIntegrationCandidate> Candidates { get; init; } = [];
    public List<TechnicalIntegrationChannel> TechnicalChannels { get; init; } = [];
    /// <summary>Capture/implementation technologies the discovery found evidence for (open strings).</summary>
    public List<string> DetectedTechnologies { get; init; } = [];
    /// <summary>Source references that could not be turned into a candidate (computed names, unresolved targets).</summary>
    public List<string> Unresolved { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public List<string> Limitations { get; init; } = [];

    public const string Boundary = "Source discovery reads only the analyzed source snapshot. No Event Hub, Service Bus, database or external endpoint was contacted, and nothing was changed.";
}

/// <summary>How one configured integration stands against the latest discovery (and, when available, the previous one).</summary>
public sealed record SourceIntegrationMatch
{
    public string IntegrationId { get; init; } = "";
    public SourceMatchState State { get; init; }
    public string? CandidateId { get; init; }
    /// <summary>The consumer the source names (for SourceSuggestion and Conflict).</summary>
    public string? SourceConsumer { get; init; }
    public ArchitectureEvidenceState? SourceConsumerEvidence { get; init; }
    /// <summary>True when the configured consumer was confirmed against an older source snapshot than the latest.</summary>
    public bool StaleEvidence { get; init; }
    /// <summary>Field changes of the matched candidate since the previous snapshot ("Capture technology: Debezium → Custom publisher").</summary>
    public List<string> SourceChanges { get; init; } = [];
    public string Detail { get; init; } = "";
}

/// <summary>Everything the Source integrations section needs: the latest discovery and its reconciliation with the configured catalog.</summary>
public sealed record SourceIntegrationsReport
{
    public string EnvironmentId { get; init; } = "";
    public SourceDiscoveryStatus SourceAnalysis { get; init; } = SourceDiscoveryStatus.NotAnalyzed;
    public SourceIntegrationDiscoveryResult? Discovery { get; init; }
    public Guid? PreviousSnapshotId { get; init; }
    public List<SourceIntegrationMatch> Matches { get; init; } = [];
    /// <summary>Business candidates in source that no configured integration covers (never added automatically).</summary>
    public List<string> UnconfiguredCandidateIds { get; init; } = [];
    public DateTimeOffset GeneratedAt { get; init; }
    public string Boundary { get; init; } = SourceIntegrationDiscoveryResult.Boundary;
}

/// <summary>Display labels shared by the backend report and the UI. CDC is a pattern; Debezium is only ever a capture technology.</summary>
public static class SourceIntegrationLabels
{
    public static string Pattern(IntegrationPattern pattern) => pattern switch
    {
        IntegrationPattern.ChangeDataCapture => "Change Data Capture",
        IntegrationPattern.EventDriven => "Event-driven",
        IntegrationPattern.ApiCall => "API call",
        IntegrationPattern.ApiPull => "API pull",
        IntegrationPattern.ApiPush => "API push",
        IntegrationPattern.FileTransfer => "File transfer",
        IntegrationPattern.Unknown => "Unknown",
        _ => pattern.ToString(),
    };

    public static string PatternShort(IntegrationPattern pattern) => pattern switch
    {
        IntegrationPattern.ChangeDataCapture => "CDC",
        IntegrationPattern.ApiCall or IntegrationPattern.ApiPull or IntegrationPattern.ApiPush => "API",
        _ => Pattern(pattern),
    };

    /// <summary>Filter category of a pattern: CDC, Messaging, API or Other.</summary>
    public static string PatternGroup(IntegrationPattern? pattern) => pattern switch
    {
        IntegrationPattern.ChangeDataCapture => "CDC",
        IntegrationPattern.Messaging or IntegrationPattern.EventDriven => "Messaging",
        IntegrationPattern.ApiCall or IntegrationPattern.ApiPull or IntegrationPattern.ApiPush => "API",
        _ => "Other",
    };

    public static string Transport(IntegrationTransport transport) => transport switch
    {
        IntegrationTransport.EventHub => "Event Hub",
        IntegrationTransport.ServiceBusTopic => "Service Bus topic",
        IntegrationTransport.ServiceBusQueue => "Service Bus queue",
        IntegrationTransport.ServiceBus => "Service Bus",
        IntegrationTransport.Http => "HTTP",
        IntegrationTransport.GraphQl => "GraphQL",
        _ => transport.ToString(),
    };

    /// <summary>The transport a configured integration kind implies. Identity provisioning and "Other" carry no transport of their own.</summary>
    public static IntegrationTransport TransportOf(IntegrationKind kind) => kind switch
    {
        IntegrationKind.EventHub => IntegrationTransport.EventHub,
        IntegrationKind.ServiceBus => IntegrationTransport.ServiceBus,
        IntegrationKind.HttpApi => IntegrationTransport.Http,
        IntegrationKind.File => IntegrationTransport.File,
        _ => IntegrationTransport.Unknown,
    };

    public static string Evidence(ArchitectureEvidenceState state) => state switch
    {
        ArchitectureEvidenceState.StronglySupported => "Strongly supported",
        _ => state.ToString(),
    };

    public static string Match(SourceMatchState state) => state switch
    {
        SourceMatchState.NotAnalyzed => "Not analyzed",
        SourceMatchState.Supported => "Supported by source",
        SourceMatchState.SourceSuggestion => "Source suggestion",
        SourceMatchState.Conflict => "Mapping conflict",
        _ => "Not found in source",
    };
}
