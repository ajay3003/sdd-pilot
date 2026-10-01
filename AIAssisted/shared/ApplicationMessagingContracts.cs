using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// Application messaging evidence (Wolverine) — a layer beside transport evidence (Event Hub / Service Bus), never merged into it.
// Source/build facts say what is CONFIGURED; only runtime telemetry can say what was OBSERVED. Detected is not configured correctly,
// configured is not executed, and a missing runtime source is Not assessed — never zero and never Pass.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessagingTechnology { Wolverine }

/// <summary>How strongly source/build evidence shows the technology. Transport alone (Service Bus, Event Hub) never raises it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessagingDetection { Confirmed, Likely, NotDetected }

/// <summary>State of one source/build fact. Configured is configuration, not behaviour.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessagingFactState { Detected, Configured, Available, NotFound, NotAssessable, NotApplicable }

/// <summary>Reference: source names a transport entity without a provable direction (e.g. a configuration default for an SDK client).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessagingRouteDirection { Publish, Listen, Reference }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessagingEndpointKind { Queue, Topic, Subscription }

/// <summary>A file and line inside an analyzed archive. The source text itself is never stored.</summary>
public sealed record SourceLocation(string File, int Line);

public sealed record MessagingFact
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public MessagingFactState State { get; init; }
    public string Detail { get; init; } = "";
    public IntegrationEvidenceSource Source { get; init; } = IntegrationEvidenceSource.SourceCode;
    public List<SourceLocation> Locations { get; init; } = [];
}

/// <summary>A configured Wolverine endpoint route. Handler mapping is only Available when source names exactly one handler for an exactly known message type.</summary>
public sealed record MessagingRoute
{
    public MessagingRouteDirection Direction { get; init; }
    public string? MessageType { get; init; }
    public MessagingEndpointKind EndpointKind { get; init; }
    public string Endpoint { get; init; } = "";
    /// <summary>Concrete entity name when source (literal, configuration default or base appsettings.json value) establishes one; null otherwise.</summary>
    public string? EntityName { get; init; }
    public string? EntityNameSource { get; init; }
    public string? Topic { get; init; }
    public string? TopicName { get; init; }
    /// <summary>"Wolverine" or "Azure SDK" (Azure.Messaging.ServiceBus used directly).</summary>
    public string Technology { get; init; } = "Wolverine";
    /// <summary>Configuration guard around the route (e.g. "unless AzureServiceBus:Disabled"), when source shows one.</summary>
    public string? Condition { get; init; }
    public List<string> Options { get; init; } = [];
    public MessagingFactState HandlerMapping { get; init; } = MessagingFactState.NotApplicable;
    public List<string> Handlers { get; init; } = [];
    public string? MappingReason { get; init; }
    /// <summary>Publish routes: classes whose source constructs the message and sends it through the Wolverine message bus.</summary>
    public List<string> Senders { get; init; } = [];
    /// <summary>Publish routes: source call sites in catch blocks that reach a sender — the application's failure path to this endpoint.</summary>
    public List<string> FailurePath { get; init; } = [];
    public SourceLocation? Location { get; init; }
}

public sealed record MessagingHandler
{
    public string Type { get; init; } = "";
    public string Method { get; init; } = "";
    public string MessageType { get; init; } = "";
    public string Project { get; init; } = "";
    /// <summary>True when source puts the handler's assembly in Wolverine discovery; null when the discovery scope is not established.</summary>
    public bool? InDiscoveryScope { get; init; }
    public string DiscoveryReason { get; init; } = "";
    public SourceLocation? Location { get; init; }
}

/// <summary>An explicit Wolverine failure rule (OnException …). Configured policy only — never evidence that a retry happened.</summary>
public sealed record MessagingFailureRule
{
    public string ExceptionType { get; init; } = "";
    public string? Condition { get; init; }
    public List<string> Actions { get; init; } = [];
    public List<string> Delays { get; init; } = [];
    public SourceLocation? Location { get; init; }
}

/// <summary>Application messaging evidence of one deployable application (a project with a Program entry point).</summary>
public sealed record ApplicationMessagingEvidence
{
    public string ApplicationId { get; init; } = "";
    public string? ServiceName { get; init; }
    /// <summary>OpenTelemetry service name from source (the Application Insights role), if source sets one.</summary>
    public string? TelemetryRoleName { get; init; }
    public MessagingTechnology Technology { get; init; } = MessagingTechnology.Wolverine;
    public MessagingDetection Detection { get; init; }
    public string DetectionReason { get; init; } = "";
    public MessagingFactState HandlerMapping { get; init; }
    public MessagingFactState RetryPolicy { get; init; }
    public MessagingFactState Outbox { get; init; }
    public MessagingFactState ErrorHandling { get; init; }
    public List<MessagingFact> Facts { get; init; } = [];
    public List<MessagingRoute> Routes { get; init; } = [];
    public List<MessagingHandler> Handlers { get; init; } = [];
    public List<MessagingFailureRule> FailureRules { get; init; } = [];
    /// <summary>Service Bus entities the application's own Azure SDK code references (outside Wolverine) — senders, processors, configured defaults.</summary>
    public List<MessagingRoute> SdkRoutes { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    /// <summary>The Integrations consumer this application is explicitly bound to by a person; never inferred from names.</summary>
    public string? BoundConsumer { get; init; }
    public MessagingFact? Fact(string id) => Facts.FirstOrDefault(f => f.Id == id);
}

public sealed record SourceArchive(string FileName, string Sha256, int FilesAnalyzed);

/// <summary>What one analysis of uploaded source archives established for an environment. Replaced as a whole on re-analysis.</summary>
public sealed record ApplicationMessagingEvidenceSet
{
    public string EnvironmentId { get; init; } = "";
    public DateTimeOffset AnalyzedAt { get; init; }
    /// <summary>Version of the analysis that produced this set. 0 = before entity names were resolved (re-analysis needed for Service Bus comparison).</summary>
    public int AnalyzerVersion { get; init; }
    public List<SourceArchive> Archives { get; init; } = [];
    public List<ApplicationMessagingEvidence> Applications { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    /// <summary>The Source Analysis snapshots this set was built from (null when it was uploaded directly by an earlier version).</summary>
    public BirkNext.SourceEvidence.ReviewSourceScope? SourceScope { get; init; }
}

/// <summary>Runtime application-messaging telemetry of one application in one run. Aggregates only — no message body, no payload field.</summary>
public sealed record ApplicationMessagingRuntime
{
    public string ApplicationId { get; init; } = "";
    public IntegrationEvidenceState State { get; init; }
    public string Reason { get; init; } = "";
    public IntegrationEvidenceSource Source { get; init; } = IntegrationEvidenceSource.ApplicationInsights;
    public DateTimeOffset CapturedAt { get; init; }
    public IntegrationEvidenceItemFreshness Freshness { get; init; } = IntegrationEvidenceItemFreshness.Unknown;
    /// <summary>Log records written by the source-listed handler classes, by level. Null when not measured — never zero for "unknown".</summary>
    public long? HandlerInformationLogs { get; init; }
    public long? HandlerWarningLogs { get; init; }
    public long? HandlerErrorLogs { get; init; }
    public DateTimeOffset? LastHandlerLog { get; init; }
    public int WindowHours { get; init; }
}

/// <summary>Pre-run summary line of one application (configuration evidence plus whether runtime evidence can be read).</summary>
public sealed record ApplicationMessagingSummary
{
    public string ApplicationId { get; init; } = "";
    public string? ServiceName { get; init; }
    public MessagingTechnology Technology { get; init; } = MessagingTechnology.Wolverine;
    public MessagingDetection Detection { get; init; }
    public MessagingFactState HandlerMapping { get; init; }
    public MessagingFactState RetryPolicy { get; init; }
    public MessagingFactState Outbox { get; init; }
    public MessagingFactState ErrorHandling { get; init; }
    public string? BoundConsumer { get; init; }
    public int BoundTopics { get; init; }
    public IntegrationEvidenceState RuntimeState { get; init; }
    public string RuntimeReason { get; init; } = "";
}

public static class ApplicationMessagingLabels
{
    public static string Detection(MessagingDetection detection) => detection switch
    {
        MessagingDetection.NotDetected => "Not detected",
        _ => detection.ToString(),
    };

    public static string Fact(MessagingFactState state) => state switch
    {
        MessagingFactState.NotFound => "Not found",
        MessagingFactState.NotAssessable => "Not assessable",
        MessagingFactState.NotApplicable => "Not applicable",
        _ => state.ToString(),
    };

    /// <summary>Runtime processing wording: only Available telemetry is "Observed"; everything else is Not assessed with its reason.</summary>
    public static string Runtime(IntegrationEvidenceState state) => state == IntegrationEvidenceState.Available ? "Observed" : "Not assessed";
}
