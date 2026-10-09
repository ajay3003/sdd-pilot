using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

/// <summary>Transport-neutral event concepts used by Active Event Testing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventOperation { Create, Update, Delete, ReadSnapshot, Tombstone, Custom }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventReplayKind { None, ExactReplay, NaturalKeyDuplicate, ControlAfterInvalid }

/// <summary>
/// Evidence stages, each recorded on its own: generated ≠ sent ≠ accepted by the transport ≠ consumer activity ≠ consumer continuity ≠
/// downstream verified. Continuity (a checkpoint moving past the event) is never downstream success.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventEvidenceStage
{
    Generated, SendAttempted, TransportAccepted, ConsumerActivityObserved, ConsumerContinuityObserved, DownstreamVerified
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventEvidenceStatus { NotAttempted, Observed, NotObserved, NotVerified, Unavailable, Ambiguous, Failed, SafetyBlocked, UnexpectedResult }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventRunStatus { Running, Completed, CompletedWithLimitedEvidence, Failed, SafetyBlocked, Inconclusive, Cancelled }

/// <summary>
/// Ready, Optional, Unknown and Partial do not block a run (Partial: the run can execute, but its best result is limited evidence).
/// Blocked, NotConfigured and NotAvailable block it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventReadinessState { Ready, Blocked, Unknown, Optional, NotConfigured, NotAvailable, Partial }

/// <summary>Which prerequisite a readiness check belongs to, so every provider's checks group the same way in the shared panel.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventReadinessCategory
{
    Execution, TrustedEnvironment, Integration, Destination, Transport, SourceContract, CdcCapture, SyntheticData, Scenario,
    Authentication, Authorization, DownstreamVerification,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventCorrelationQuality { Exact, Strong, Moderate, Weak, AggregateOnly, Unavailable }

/// <summary>Supported scenarios can run once ready; NotAssessed scenarios are declared (so the gap is visible) but never execute.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventScenarioSupport { Supported, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActiveEventDownstreamOutcome { Verified, NotVerified, Unavailable, UnexpectedResult, Ambiguous }

public static class ActiveEventReadinessRules
{
    public static bool Blocks(ActiveEventReadinessState state) =>
        state is ActiveEventReadinessState.Blocked or ActiveEventReadinessState.NotConfigured or ActiveEventReadinessState.NotAvailable;
}

/// <summary>Project extension declaration. Domain-specific requirements live in extension providers, not the execution engine.</summary>
public sealed record ActiveEventScenarioDescriptor
{
    public string ExtensionId { get; init; } = "";
    public string ExtensionVersion { get; init; } = "";
    public string ProviderDisplayName { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public string ScenarioVersion { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    /// <summary>The source resource the scenario's events describe (for CDC: database.schema.table), as shown to the user.</summary>
    public string ResourceLabel { get; init; } = "";
    public string RequiredIntegrationType { get; init; } = "";
    public string RequiredTransportType { get; init; } = "";
    public IReadOnlyList<ActiveEventOperation> SupportedOperations { get; init; } = [];
    public int ExpectedEventCount { get; init; } = 1;
    public bool SyntheticDataRequired { get; init; } = true;
    /// <summary>Sending may change downstream state (records may be created and remain in the target environment).</summary>
    public bool StateChanging { get; init; } = true;
    /// <summary>A Completed result requires a downstream verifier to observe the expected state.</summary>
    public bool RequiresDownstreamVerification { get; init; } = true;
    /// <summary>A Completed result requires consumer continuity (progression past the run's events) to be observed.</summary>
    public bool RequiresContinuity { get; init; }
    public ActiveEventReplayKind ReplayKind { get; init; }
    public bool SupportsReplay => ReplayKind is ActiveEventReplayKind.ExactReplay;
    public ActiveEventScenarioSupport Support { get; init; } = ActiveEventScenarioSupport.Supported;
    /// <summary>Why a NotAssessed scenario does not run (shown as-is).</summary>
    public string SupportDetail { get; init; } = "";
    public string SafetyClassification { get; init; } = "Synthetic test data, DEV/QA only";
    public IReadOnlyList<string> RequiredSourceEvidence { get; init; } = [];
    public IReadOnlyList<string> RequiredSourceResources { get; init; } = [];
    public IReadOnlyList<string> VerificationCapabilities { get; init; } = [];
    /// <summary>What a Completed result means and, more importantly, what it does not.</summary>
    public string ResultMeaning { get; init; } = "";
    public string ResultDoesNotMean { get; init; } = "";
}

public sealed record ActiveEventReadinessCheck(string Key, string Label, ActiveEventReadinessState State, string Detail,
    ActiveEventReadinessCategory Category = ActiveEventReadinessCategory.Execution);

public sealed record ActiveEventReadiness
{
    public string TargetEnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public ActiveEventScenarioDescriptor Scenario { get; init; } = new();
    public ActiveEventTrustedTarget? Target { get; init; }
    public ActiveEventSourceContractReference SourceContract { get; init; } = new();
    public IReadOnlyList<ActiveEventReadinessCheck> Checks { get; init; } = [];
    public bool CanRun { get; init; }
    public int SendTimeoutSeconds { get; init; }
    public int ObservationTimeoutSeconds { get; init; }
    /// <summary>Safe preview of what would be generated (identity ranges, fields, operations) — never a payload.</summary>
    public IReadOnlyDictionary<string, string> SyntheticSummary { get; init; } = new Dictionary<string, string>();
}

public sealed record ActiveEventRunRequest
{
    public string TargetEnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public string ExtensionId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public Guid? SourceSnapshotId { get; init; }
    public bool Confirmed { get; init; }
    /// <summary>The destination resource the user confirmed. A check only: the backend resolves the destination itself.</summary>
    public string? ConfirmedDestination { get; init; }
    /// <summary>The source-contract fingerprint shown when the user reviewed readiness. A changed contract blocks the run (Needs review).</summary>
    public string? ReviewedContractFingerprint { get; init; }
}

/// <summary>Correlation metadata safe to persist; raw event bodies and secrets are never part of this contract.</summary>
public sealed record ActiveEventCorrelation
{
    public Guid RunId { get; init; }
    public string EventId { get; init; } = "";
    public string EventFingerprint { get; init; } = "";
    public string SyntheticMarker { get; init; } = "";
    /// <summary>The synthetic source key value(s) of the event (for CDC: the reserved synthetic primary key).</summary>
    public string? SafeSourceIdentity { get; init; }
    /// <summary>The identity the downstream system is expected to derive (when the provider knows the derivation), used for downstream verification.</summary>
    public string? ExpectedResourceIdentity { get; init; }
    public IReadOnlyDictionary<string, string> ExtensionValues { get; init; } = new Dictionary<string, string>();
    public string? ReplaysEventId { get; init; }
    public ActiveEventReplayKind ReplayKind { get; init; }
}

/// <summary>
/// Opaque extension-generated bytes plus safe metadata. The core must not inspect Body. Body is empty only for an explicit
/// <see cref="ActiveEventOperation.Tombstone"/>, which must then carry an <see cref="EventKey"/>.
/// </summary>
public sealed record GeneratedActiveEvent
{
    public string EventId { get; init; } = "";
    public string ExtensionId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public int SequenceIndex { get; init; }
    public ActiveEventOperation Operation { get; init; }
    public byte[] Body { get; init; } = [];
    public string BodySha256 { get; init; } = "";
    public int BodyBytes { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    /// <summary>Safe synthetic record key (e.g. a key JSON with synthetic values). Required for tombstones.</summary>
    public string? EventKey { get; init; }
    /// <summary>Optional partition key chosen by the provider (domain key selection); the transport maps it to its own concept.</summary>
    public string? PartitionKey { get; init; }
    public ActiveEventCorrelation Correlation { get; init; } = new();
    public IReadOnlyDictionary<string, string> SafeDisplayMetadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> TransportProperties { get; init; } = new Dictionary<string, string>();
}

/// <summary>Neutral downstream verification result. Identities are synthetic/derived values only; no domain data or PII.</summary>
public sealed record ActiveEventDownstreamResult
{
    public ActiveEventDownstreamOutcome Outcome { get; init; } = ActiveEventDownstreamOutcome.NotVerified;
    public string? ExpectedIdentity { get; init; }
    public string? ObservedIdentity { get; init; }
    public string Reason { get; init; } = "";
    public string EvidenceSummary { get; init; } = "";
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed record ActiveEventStageEvidence
{
    public ActiveEventEvidenceStage Stage { get; init; }
    public ActiveEventEvidenceStatus Status { get; init; } = ActiveEventEvidenceStatus.NotVerified;
    public ActiveEventCorrelationQuality CorrelationQuality { get; init; } = ActiveEventCorrelationQuality.Unavailable;
    public string Detail { get; init; } = "";
    public string EvidenceSource { get; init; } = "";
    public DateTimeOffset? CapturedAt { get; init; }
    /// <summary>The event the stage refers to; null for run-level evidence (e.g. continuity over the whole run).</summary>
    public string? EventId { get; init; }
    public ActiveEventDownstreamResult? Downstream { get; init; }
}

public sealed record ActiveEventTrustedTarget
{
    public string TargetEnvironmentId { get; init; } = "";
    public string EnvironmentDisplayName { get; init; } = "";
    public string EnvironmentType { get; init; } = "Unknown";
    /// <summary>The backend-owned target application URL of the trusted environment (production-host checks run on it server-side).</summary>
    public string? TargetUrl { get; init; }
    public string IntegrationId { get; init; } = "";
    public string IntegrationDisplayName { get; init; } = "";
    public string IntegrationType { get; init; } = "";
    public string TransportType { get; init; } = "";
    public string? Endpoint { get; init; }
    public string? Resource { get; init; }
    public string? Consumer { get; init; }
    public bool ConsumerAssumed { get; init; }
    public string? ConsumerRole { get; init; }
    public string? SourceResource { get; init; }
    public string AuthenticationMode { get; init; } = "Instance identity";
    public IReadOnlyDictionary<string, string> SafeMetadata { get; init; } = new Dictionary<string, string>();
}

public sealed record ActiveEventSourceContractReference
{
    public Guid? SourceSnapshotId { get; init; }
    public string? ContractFingerprint { get; init; }
    public string? SourceResource { get; init; }
    public string ContractStatus { get; init; } = "";
    public string ExtensionVersion { get; init; } = "";
    public string ScenarioVersion { get; init; } = "";
}

/// <summary>Safe event summary for durable history. Event bodies are intentionally excluded.</summary>
public sealed record ActiveEventGeneratedSummary
{
    public string EventId { get; init; } = "";
    public string ExtensionId { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public int SequenceIndex { get; init; }
    public ActiveEventOperation Operation { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string BodySha256 { get; init; } = "";
    public int BodyBytes { get; init; }
    public string? EventKey { get; init; }
    public string? PartitionKey { get; init; }
    public ActiveEventCorrelation Correlation { get; init; } = new();
    public IReadOnlyDictionary<string, string> SafeMetadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>Transport-neutral immutable run/history contract. This is the lifecycle result shared by every scenario extension.</summary>
public sealed record ActiveEventRunResult
{
    public Guid RunId { get; init; }
    public ActiveEventScenarioDescriptor Scenario { get; init; } = new();
    public ActiveEventTrustedTarget Target { get; init; } = new();
    public ActiveEventSourceContractReference SourceContract { get; init; } = new();
    public ActiveEventRunStatus Status { get; init; } = ActiveEventRunStatus.Running;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<ActiveEventGeneratedSummary> Events { get; init; } = [];
    public IReadOnlyList<ActiveEventStageEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
    /// <summary>True for a run recorded by the retired Active CDC runner and projected read-only into this contract.</summary>
    public bool Legacy { get; init; }
    public bool Completed => Status != ActiveEventRunStatus.Running;
    public bool SendAttempted => Evidence.Any(item => item.Stage == ActiveEventEvidenceStage.SendAttempted);
}

/// <summary>One history row. Mixed providers share this shape; domain details stay in the run itself.</summary>
public sealed record ActiveEventRunSummary
{
    public Guid RunId { get; init; }
    public string ExtensionId { get; init; } = "";
    public string ProviderDisplayName { get; init; } = "";
    public string ScenarioId { get; init; } = "";
    public string ScenarioName { get; init; } = "";
    public string TargetEnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public ActiveEventRunStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public bool SendAttempted { get; init; }
    public int EventCount { get; init; }
    public bool Legacy { get; init; }
}

/// <summary>A registered scenario provider as the shared panel lists it, with the integrations it applies to in one environment.</summary>
public sealed record ActiveEventProviderSummary
{
    public string ExtensionId { get; init; } = "";
    public string ExtensionVersion { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Resources { get; init; } = [];
    /// <summary>Every scenario the provider declares, independent of configuration (for display only).</summary>
    public IReadOnlyList<ActiveEventScenarioDescriptor> Scenarios { get; init; } = [];
    public IReadOnlyList<string> ApplicableIntegrationIds { get; init; } = [];
    /// <summary>Why the provider has no applicable integration in this environment (empty when it has one).</summary>
    public string NotApplicableReason { get; init; } = "";
}

/// <summary>The backend's view of whether an environment may execute active events. Client labels are never authoritative.</summary>
public sealed record ActiveEventEnvironmentTrust
{
    public string EnvironmentId { get; init; } = "";
    public bool Trusted { get; init; }
    public string DisplayName { get; init; } = "";
    public string EnvironmentType { get; init; } = "Unknown";
    public bool ExecutionAllowed { get; init; }
    public IReadOnlyList<string> IntegrationIds { get; init; } = [];
    public string Detail { get; init; } = "";
}
