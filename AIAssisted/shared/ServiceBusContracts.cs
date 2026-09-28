using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// Azure Service Bus transport evidence — a transport of its own, never an Event Hub (no consumer group, no checkpoint, no lag) and never
// Wolverine application messaging. Three layers stay apart: CONFIGURED topology (expected entities and properties, with provenance),
// RUNTIME metadata (what the namespace reports through Azure Resource Manager, read-only), and APPLICATION routes (Wolverine / Azure SDK
// references from analyzed source). No message is sent, received, peeked or settled, and no entity is created, changed or deleted.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceBusEntityType { Queue, Topic, Subscription }

/// <summary>One comparison or observation. Pass only for a bounded expected-vs-observed comparison; existence is Observed, never Pass.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceBusCheckState { Configured, Matched, Mismatch, Pass, Observed, NotFound, NotReferenced, NotAssessed, NotConfigured, NotAuthorized, Unavailable, Error, Stale }

/// <summary>Overall state of a Service Bus evidence test. Never a single "Service Bus works".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceBusEvidenceState { Consistent, Partial, IssueDetected, NotTestable }

/// <summary>An expected entity with the properties the configured source states. A null property is Unknown and is never compared.</summary>
public sealed record ServiceBusEntityExpectation
{
    public ServiceBusEntityType EntityType { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Owning topic of a subscription.</summary>
    public string? Topic { get; init; }
    /// <summary>Applications the audited infrastructure grants send (publishers) or receive (consumers) on this entity.</summary>
    public List<string> Publishers { get; init; } = [];
    public List<string> Consumers { get; init; } = [];
    public int? MaxDeliveryCount { get; init; }
    public string? LockDuration { get; init; }
    public string? DefaultMessageTimeToLive { get; init; }
    public bool? RequiresSession { get; init; }
    public bool? DeadLetteringOnMessageExpiration { get; init; }
    public bool? DeadLetteringOnFilterEvaluationExceptions { get; init; }
    public bool? RequiresDuplicateDetection { get; init; }
    public int? MaxSizeInMegabytes { get; init; }
    public string? Note { get; init; }
    [JsonIgnore] public string Path => EntityType == ServiceBusEntityType.Subscription ? $"{Topic}/{Name}" : Name;
}

/// <summary>The configured, expected Service Bus topology of a platform. Persisted in BirkNext; Terraform is never read at runtime.</summary>
public sealed record ServiceBusTopology
{
    /// <summary>Where the expectation comes from, e.g. a developer-side Terraform audit that seeded these values.</summary>
    public string Source { get; init; } = "";
    public DateTimeOffset? AuditedAt { get; init; }
    public List<ServiceBusEntityExpectation> Entities { get; init; } = [];
    /// <summary>Applications the audited infrastructure grants receive on the whole namespace (every queue and subscription).</summary>
    public List<string> NamespaceReceivers { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    [JsonIgnore] public IEnumerable<ServiceBusEntityExpectation> Queues => Entities.Where(e => e.EntityType == ServiceBusEntityType.Queue);
    [JsonIgnore] public IEnumerable<ServiceBusEntityExpectation> Topics => Entities.Where(e => e.EntityType == ServiceBusEntityType.Topic);
    [JsonIgnore] public IEnumerable<ServiceBusEntityExpectation> Subscriptions => Entities.Where(e => e.EntityType == ServiceBusEntityType.Subscription);
}

/// <summary>What Azure Resource Manager reports for one entity (management metadata). Counts are a point-in-time snapshot; null = not reported.</summary>
public sealed record ServiceBusEntityObservation
{
    public ServiceBusEntityType EntityType { get; init; }
    public string Name { get; init; } = "";
    public string? Topic { get; init; }
    public string? Status { get; init; }
    public Dictionary<string, string?> Properties { get; init; } = [];
    public long? ActiveMessageCount { get; init; }
    public long? DeadLetterMessageCount { get; init; }
    public long? ScheduledMessageCount { get; init; }
    public long? TransferMessageCount { get; init; }
    public long? TransferDeadLetterMessageCount { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? AccessedAt { get; init; }
    [JsonIgnore] public string Path => EntityType == ServiceBusEntityType.Subscription ? $"{Topic}/{Name}" : Name;
}

/// <summary>A runtime metadata read of one namespace. State is typed (NotConfigured, NotAuthorized, NotFound, Unavailable, Error…), never zeros.</summary>
public sealed record ServiceBusRuntimeEvidence
{
    public string PlatformId { get; init; } = "";
    public string? Namespace { get; init; }
    public IntegrationEvidenceState State { get; init; }
    public string Reason { get; init; } = "";
    public IntegrationEvidenceSource Source { get; init; } = IntegrationEvidenceSource.AzureResourceManager;
    public DateTimeOffset CapturedAt { get; init; }
    public string? NamespaceStatus { get; init; }
    public string? Sku { get; init; }
    /// <summary>Entities listed per type; a type whose list could not be read keeps its own failure in <see cref="ListFailures"/>.</summary>
    public List<ServiceBusEntityObservation> Entities { get; init; } = [];
    public Dictionary<string, string> ListFailures { get; init; } = [];
}

public sealed record ServiceBusComparison
{
    public string CheckId { get; init; } = "";
    public ServiceBusEntityType? EntityType { get; init; }
    public string? Entity { get; init; }
    public string Title { get; init; } = "";
    public string? Expected { get; init; }
    public string? Observed { get; init; }
    public ServiceBusCheckState State { get; init; }
    public string Detail { get; init; } = "";
    public IntegrationEvidenceSource Provenance { get; init; }
}

/// <summary>Application route ↔ transport entity. Matched routing is infrastructure, never proof that a message was processed.</summary>
public sealed record ServiceBusRouteCorrelation
{
    public string Application { get; init; } = "";
    /// <summary>"Wolverine" or "Azure SDK".</summary>
    public string Technology { get; init; } = "";
    public string Direction { get; init; } = "";
    public string? MessageType { get; init; }
    public string? Entity { get; init; }
    public ServiceBusEntityType? EntityType { get; init; }
    public string EntitySource { get; init; } = "";
    public ServiceBusCheckState Configuration { get; init; }
    public ServiceBusCheckState Runtime { get; init; } = ServiceBusCheckState.NotAssessed;
    public ServiceBusCheckState Access { get; init; } = ServiceBusCheckState.NotAssessed;
    public string Detail { get; init; } = "";
    public SourceLocation? Location { get; init; }
}

/// <summary>Result of the read-only "Test Service Bus" action and of a review's Service Bus part (snapshotted with the result).</summary>
public sealed record ServiceBusEvidenceCheck
{
    public string PlatformId { get; init; } = "";
    public string? Namespace { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public ServiceBusEvidenceState OverallState { get; init; }
    public int Queues { get; init; }
    public int Topics { get; init; }
    public int Subscriptions { get; init; }
    public List<ServiceBusComparison> Configuration { get; init; } = [];
    public ServiceBusRuntimeEvidence? Runtime { get; init; }
    public List<ServiceBusComparison> RuntimeChecks { get; init; } = [];
    public List<ServiceBusRouteCorrelation> Routes { get; init; } = [];
    public List<string> Missing { get; init; } = [];
    public List<string> Findings { get; init; } = [];
    public int WindowHours { get; init; }
}

/// <summary>Pre-run summary of a Service Bus platform (configuration only; nothing is contacted before a run).</summary>
public sealed record ServiceBusReadiness
{
    public string PlatformId { get; init; } = "";
    public string PlatformName { get; init; } = "";
    public int Queues { get; init; }
    public int Topics { get; init; }
    public int Subscriptions { get; init; }
    public int RoutesMatched { get; init; }
    public int RoutesMismatched { get; init; }
    public int RoutesTotal { get; init; }
    public IntegrationEvidenceState RuntimeState { get; init; }
    public string RuntimeReason { get; init; } = "";
}

public static class ServiceBusLabels
{
    public static string State(ServiceBusCheckState state) => state switch
    {
        ServiceBusCheckState.NotFound => "Not found",
        ServiceBusCheckState.NotReferenced => "Not referenced",
        ServiceBusCheckState.NotAssessed => "Not assessed",
        ServiceBusCheckState.NotConfigured => "Not configured",
        ServiceBusCheckState.NotAuthorized => "Not authorized",
        _ => state.ToString(),
    };

    public static string Overall(ServiceBusEvidenceState state) => state switch
    {
        ServiceBusEvidenceState.IssueDetected => "Issue detected",
        ServiceBusEvidenceState.NotTestable => "Not testable",
        _ => state.ToString(),
    };

    public static ServiceBusCheckState FromEvidence(IntegrationEvidenceState state) => state switch
    {
        IntegrationEvidenceState.Available => ServiceBusCheckState.Observed,
        IntegrationEvidenceState.NotFound => ServiceBusCheckState.NotFound,
        IntegrationEvidenceState.NotAuthorized => ServiceBusCheckState.NotAuthorized,
        IntegrationEvidenceState.NotConfigured => ServiceBusCheckState.NotConfigured,
        IntegrationEvidenceState.Stale => ServiceBusCheckState.Stale,
        IntegrationEvidenceState.Error => ServiceBusCheckState.Error,
        _ => ServiceBusCheckState.Unavailable,
    };
}
