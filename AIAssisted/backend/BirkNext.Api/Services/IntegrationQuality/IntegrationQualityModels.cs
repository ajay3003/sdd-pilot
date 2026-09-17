using System.Text.Json.Serialization;

namespace BirkNext.Api.Services.IntegrationQuality;

public enum IntegrationType
{
    REST, GraphQL, EventHub, ServiceBus, Kafka, RabbitMQ, File, SOAP
}

public enum IntegrationAuthType
{
    None, ApiKey, BearerToken, BasicAuth, ManagedIdentity, ConnectionString, SasToken
}

public enum ContractSourceType
{
    Auto = 0,              // Auto-detect from integration config
    OpenApi = 1,           // Swagger/OpenAPI specification
    GraphQlSchema = 2,     // GraphQL schema/introspection
    Assembly = 3,          // .NET assembly reference
    SchemaFile = 4,        // Schema file (JSON/YAML/protobuf)
    Endpoint = 5,          // Contract endpoint/registry
    Manual = 6,            // Manually entered
    Unknown = 7            // Undetermined
}

public enum RelationshipSource
{
    Configured = 0,        // Explicitly configured producer/consumer
    DiscoveredTraffic = 1, // Inferred from runtime traffic observation
    MessagingMetadata = 2, // From message/event metadata
    Unknown = 3            // Relationship source not determined
}

public enum RuntimeEvidenceType
{
    HttpRequestObserved = 0,      // REST API request observed
    GraphQlOperationObserved = 1, // GraphQL query/mutation observed
    MessagePublished = 2,          // Message published (not currently observed)
    MessageConsumed = 3,           // Message consumed (not currently observed)
    Unknown = 4                    // Evidence type unknown
}

public enum RuntimeEvidenceSource
{
    EndpointDiscovery = 0,  // From Endpoint Discovery (Browser Companion traffic)
    AuthenticatedProxy = 1, // From Local HTTPS Proxy authenticated checks
    MessagingAdapter = 2,   // From messaging system telemetry (not yet implemented)
    BrowserCompanion = 3,   // From Browser Companion direct observation
    OtherAuthoritative = 4  // Other authoritative source
}

public enum RuntimeEvidenceDirection
{
    Outbound = 0,  // Outbound from this service
    Inbound = 1,   // Inbound to this service
    Unknown = 2    // Direction unknown
}

public enum RuntimeEvidenceOutcome
{
    Success = 0,  // Successful observation (2xx status or no error)
    Error = 1,    // Error observed (4xx/5xx or error state)
    Unknown = 2   // Outcome unknown or not determined
}

/// <summary>
/// Represents a runtime observation of integration activity (Phase 3, Checkpoint 2).
/// Distinct from reachability probes. Contains evidence that actual traffic flowed.
/// </summary>
public sealed class RuntimeIntegrationEvidence
{
    [JsonPropertyName("integrationId")]
    public string IntegrationId { get; init; } = "";

    [JsonPropertyName("evidenceType")]
    public RuntimeEvidenceType EvidenceType { get; init; }

    [JsonPropertyName("source")]
    public RuntimeEvidenceSource Source { get; init; }

    [JsonPropertyName("direction")]
    public RuntimeEvidenceDirection Direction { get; init; }

    [JsonPropertyName("outcome")]
    public RuntimeEvidenceOutcome Outcome { get; init; }

    [JsonPropertyName("observedAt")]
    public DateTime ObservedAt { get; init; }

    [JsonPropertyName("durationMs")]
    public double? DurationMs { get; init; }  // Optional; only if measured

    [JsonPropertyName("operationOrMessage")]
    public string? OperationOrMessage { get; set; }  // GraphQL operation name, message type, etc.

    [JsonPropertyName("protocol")]
    public IntegrationType? Protocol { get; set; }

    [JsonPropertyName("statusCodeOrOutcome")]
    public string? StatusCodeOrOutcome { get; set; }  // "200", "404", "error", etc.

    [JsonPropertyName("sampleCount")]
    public int SampleCount { get; set; } = 1;  // For aggregated evidence

    [JsonPropertyName("metadata")]
    public Dictionary<string, string> Metadata { get; init; } = new();  // Sanitized metadata only
}

/// <summary>
/// Summarizes runtime evidence state for an integration.
/// </summary>
public sealed class RuntimeEvidenceSummary
{
    [JsonPropertyName("hasRuntimeEvidence")]
    public bool HasRuntimeEvidence { get; init; }

    [JsonPropertyName("evidenceCount")]
    public int EvidenceCount { get; init; }

    [JsonPropertyName("sources")]
    public List<RuntimeEvidenceSource> Sources { get; init; } = new();

    [JsonPropertyName("lastObservedAt")]
    public DateTime? LastObservedAt { get; init; }

    [JsonPropertyName("evidenceTypes")]
    public List<RuntimeEvidenceType> EvidenceTypes { get; init; } = new();

    [JsonPropertyName("minDurationMs")]
    public double? MinDurationMs { get; init; }

    [JsonPropertyName("maxDurationMs")]
    public double? MaxDurationMs { get; init; }

    [JsonPropertyName("avgDurationMs")]
    public double? AvgDurationMs { get; init; }
}

public enum ContractMetadataReadiness
{
    NotConfigured = 0,     // No relationship metadata provided
    Partial = 1,           // Incomplete relationship metadata
    Ready = 2              // Complete relationship metadata
}

public enum IntegrationFindingSeverity { Critical, High, Medium, Low, Info }

public sealed class IntegrationConfigDto
{
    [JsonPropertyName("id")]            public string              Id          { get; set; } = "";
    [JsonPropertyName("name")]          public string              Name        { get; set; } = "";
    [JsonPropertyName("type")]          public IntegrationType     Type        { get; set; }
    [JsonPropertyName("endpoint")]      public string?             Endpoint    { get; set; }
    [JsonPropertyName("resource")]      public string?             Resource    { get; set; }
    [JsonPropertyName("consumer")]      public string?             Consumer    { get; set; }
    [JsonPropertyName("authType")]      public IntegrationAuthType AuthType    { get; set; }
    [JsonPropertyName("healthUrl")]     public string?             HealthUrl   { get; set; }
    [JsonPropertyName("workerUrl")]     public string?             WorkerUrl   { get; set; }
    [JsonPropertyName("monitoringUrl")] public string?             MonitoringUrl { get; set; }
    [JsonPropertyName("owner")]         public string?             Owner       { get; set; }
    [JsonPropertyName("enabled")]       public bool                Enabled     { get; set; } = true;

    // Contract Relationship Metadata (Phase 2)
    [JsonPropertyName("logicalProducerService")]
    public string? LogicalProducerService { get; set; }

    [JsonPropertyName("logicalConsumerService")]
    public string? LogicalConsumerService { get; set; }

    // Relationship source (Phase 3)
    [JsonPropertyName("producerConsumerSource")]
    public RelationshipSource ProducerConsumerSource { get; set; } = RelationshipSource.Unknown;

    [JsonPropertyName("contractName")]
    public string? ContractName { get; set; }

    [JsonPropertyName("contractSourceType")]
    public ContractSourceType ContractSourceType { get; set; } = ContractSourceType.Unknown;

    [JsonPropertyName("contractSourceLocation")]
    public string? ContractSourceLocation { get; set; }

    [JsonPropertyName("contractMetadataReadiness")]
    public ContractMetadataReadiness ContractMetadataReadiness { get; set; } = ContractMetadataReadiness.NotConfigured;

    // Phase 5: Independent producer/consumer contract sources for messaging analysis
    [JsonPropertyName("producerContractSourceType")]
    public ContractSourceType? ProducerContractSourceType { get; set; }

    [JsonPropertyName("producerContractSourceLocation")]
    public string? ProducerContractSourceLocation { get; set; }

    [JsonPropertyName("consumerContractSourceType")]
    public ContractSourceType? ConsumerContractSourceType { get; set; }

    [JsonPropertyName("consumerContractSourceLocation")]
    public string? ConsumerContractSourceLocation { get; set; }

    /// <summary>
    /// Computes the contract metadata readiness based on the current values.
    /// </summary>
    public void ComputeReadiness()
    {
        ContractMetadataReadiness = ComputeReadinessFor(Type);
    }

    private ContractMetadataReadiness ComputeReadinessFor(IntegrationType type)
    {
        var hasProducer = !string.IsNullOrWhiteSpace(LogicalProducerService);
        var hasConsumer = !string.IsNullOrWhiteSpace(LogicalConsumerService);
        var hasContract = !string.IsNullOrWhiteSpace(ContractName);
        var hasSourceType = ContractSourceType != ContractSourceType.Unknown;
        var hasSourceLocation = !string.IsNullOrWhiteSpace(ContractSourceLocation);

        // Phase 5: Check for independent producer/consumer sources
        var hasProducerSource = ProducerContractSourceType.HasValue && ProducerContractSourceType != ContractSourceType.Unknown
            && !string.IsNullOrWhiteSpace(ProducerContractSourceLocation);
        var hasConsumerSource = ConsumerContractSourceType.HasValue && ConsumerContractSourceType != ContractSourceType.Unknown
            && !string.IsNullOrWhiteSpace(ConsumerContractSourceLocation);

        if (!hasProducer && !hasConsumer && !hasContract && !hasSourceType && !hasSourceLocation && !hasProducerSource && !hasConsumerSource)
            return ContractMetadataReadiness.NotConfigured;

        return type switch
        {
            IntegrationType.REST =>
                (hasSourceType && hasSourceLocation)
                    ? ContractMetadataReadiness.Ready
                    : ContractMetadataReadiness.Partial,

            IntegrationType.GraphQL =>
                (hasSourceType && ContractSourceType == ContractSourceType.GraphQlSchema && hasConsumer)
                    ? ContractMetadataReadiness.Ready
                    : ContractMetadataReadiness.Partial,

            IntegrationType.EventHub or IntegrationType.ServiceBus =>
                // Support both old-style (single source) and new-style (independent producer/consumer sources)
                (hasProducer && hasConsumer && hasContract && (hasSourceType && hasSourceLocation || hasProducerSource && hasConsumerSource))
                    ? ContractMetadataReadiness.Ready
                    : ContractMetadataReadiness.Partial,

            IntegrationType.Kafka or IntegrationType.RabbitMQ =>
                // For Kafka/RabbitMQ: (producer or consumer) + contract is enough
                // Or with independent sources if using Phase 5 model
                ((hasProducer || hasConsumer) && hasContract || hasProducerSource && hasConsumerSource && hasContract)
                    ? ContractMetadataReadiness.Ready
                    : ContractMetadataReadiness.Partial,

            _ => ContractMetadataReadiness.Partial
        };
    }
}

public sealed class IntegrationQualityRequest
{
    [JsonPropertyName("environmentName")] public string                    EnvironmentName { get; set; } = "";
    [JsonPropertyName("integrations")]    public List<IntegrationConfigDto> Integrations   { get; set; } = [];
    [JsonPropertyName("timeoutSeconds")]  public int                       TimeoutSeconds  { get; set; } = 30;

    // Active Target Environment authenticated-testing identity (never a token). Enables authenticated runtime checks via the Local HTTPS proxy.
    [JsonPropertyName("authenticatedTestingMethod")] public BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod AuthenticatedTestingMethod { get; set; } = BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.ManagedEdgeCdp;
    [JsonPropertyName("profileId")]          public string? ProfileId          { get; set; }
    [JsonPropertyName("contextFingerprint")] public string? ContextFingerprint { get; set; }
}

/// <summary>One authenticated runtime check performed for an integration through the Local HTTPS proxy context. No credential, headers or body.</summary>
public sealed class IntegrationAuthenticatedCheck
{
    [JsonPropertyName("integrationId")] public string                       IntegrationId { get; init; } = "";
    [JsonPropertyName("label")]         public string                       Label         { get; init; } = "";
    [JsonPropertyName("url")]           public string                       Url           { get; init; } = "";
    [JsonPropertyName("executionMode")] public BirkNext.LocalHttpsProxy.ReviewExecutionMode ExecutionMode { get; init; }
    [JsonPropertyName("status")]        public BirkNext.LocalHttpsProxy.AuthenticatedExecutionStatus Status { get; init; }
    [JsonPropertyName("statusCode")]    public int                          StatusCode    { get; init; }
    [JsonPropertyName("elapsedMs")]     public double                       ElapsedMs     { get; init; }
    [JsonPropertyName("outcome")]       public string                       Outcome       { get; init; } = "";
}

/// <summary>Authenticated availability + provenance surfaced in the Integration Quality report. Capability flags come from the shared review model; no token.</summary>
public sealed class IntegrationAuthenticationSummary
{
    [JsonPropertyName("capabilities")] public BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities Capabilities { get; init; } = new();
    [JsonPropertyName("checks")]       public List<IntegrationAuthenticatedCheck> Checks { get; init; } = [];
}

public sealed class IntegrationFinding
{
    [JsonPropertyName("id")]               public string                     Id              { get; init; } = "";
    [JsonPropertyName("integrationId")]    public string                     IntegrationId   { get; init; } = "";
    [JsonPropertyName("integrationName")]  public string                     IntegrationName { get; init; } = "";
    [JsonPropertyName("title")]            public string                     Title           { get; init; } = "";
    [JsonPropertyName("description")]      public string                     Description     { get; init; } = "";
    [JsonPropertyName("recommendation")]   public string                     Recommendation  { get; init; } = "";
    [JsonPropertyName("severity")]         public IntegrationFindingSeverity Severity        { get; init; }
    [JsonPropertyName("evidence")]         public List<string>               Evidence        { get; init; } = [];
}

public sealed class IntegrationStatus
{
    [JsonPropertyName("integrationId")]    public string          IntegrationId    { get; init; } = "";
    [JsonPropertyName("name")]             public string          Name             { get; init; } = "";
    [JsonPropertyName("type")]             public IntegrationType Type             { get; init; }
    [JsonPropertyName("enabled")]          public bool            Enabled          { get; init; }
    [JsonPropertyName("hasRequiredFields")] public bool           HasRequiredFields { get; init; }

    // Reachability (separate from runtime evidence)
    [JsonPropertyName("healthReachable")]  public bool?           HealthReachable  { get; init; }
    [JsonPropertyName("workerReachable")]  public bool?           WorkerReachable  { get; init; }

    // Runtime evidence (Phase 3)
    [JsonPropertyName("runtimeEvidenceSummary")]
    public RuntimeEvidenceSummary? RuntimeEvidenceSummary { get; set; }

    // Contract compatibility (Phase 3, Checkpoint 4). Nullable throughout: a report written
    // before these fields existed must read as "not captured", never as Compatible/NoChange.
    // Both ContractCompatibilityStatus.Compatible and ContractDriftState.NoChange are 0, so a
    // non-nullable field would silently turn a missing value into a compatibility claim.
    [JsonPropertyName("compatibilityState")]
    public BirkNext.Api.Services.ContractAnalysis.ContractCompatibilityStatus? CompatibilityState { get; set; }

    [JsonPropertyName("compatibilityComparedAt")]
    public DateTime? CompatibilityComparedAt { get; set; }

    [JsonPropertyName("compatibilityDifferenceCount")]
    public int? CompatibilityDifferenceCount { get; set; }

    [JsonPropertyName("compatibilityBreakingCount")]
    public int? CompatibilityBreakingCount { get; set; }

    [JsonPropertyName("compatibilityDifferences")]
    public List<BirkNext.Api.Services.ContractAnalysis.ContractDifference> CompatibilityDifferences { get; set; } = [];

    [JsonPropertyName("compatibilityReason")]
    public string? CompatibilityReason { get; set; }

    [JsonPropertyName("producerService")]
    public string? ProducerService { get; set; }

    [JsonPropertyName("consumerService")]
    public string? ConsumerService { get; set; }

    [JsonPropertyName("producerContractSource")]
    public string? ProducerContractSource { get; set; }

    [JsonPropertyName("consumerContractSource")]
    public string? ConsumerContractSource { get; set; }

    // Contract drift (Phase 3, Checkpoint 4). Baseline persistence lands in Checkpoint 5.
    [JsonPropertyName("driftState")]
    public BirkNext.Api.Services.ContractAnalysis.ContractDriftState? DriftState { get; set; }

    [JsonPropertyName("driftDifferenceCount")]
    public int? DriftDifferenceCount { get; set; }

    [JsonPropertyName("driftBreakingCount")]
    public int? DriftBreakingCount { get; set; }

    [JsonPropertyName("driftDifferences")]
    public List<BirkNext.Api.Services.ContractAnalysis.ContractDifference> DriftDifferences { get; set; } = [];

    [JsonPropertyName("previousBaselineTimestamp")]
    public DateTime? PreviousBaselineTimestamp { get; set; }

    [JsonPropertyName("currentContractFingerprint")]
    public string? CurrentContractFingerprint { get; set; }

    [JsonPropertyName("previousContractFingerprint")]
    public string? PreviousContractFingerprint { get; set; }

    // History (Phase 3, Checkpoint 5).
    [JsonPropertyName("baselineKey")]
    public string? BaselineKey { get; set; }

    [JsonPropertyName("historicalChanges")]
    public List<IntegrationHistoricalChange> HistoricalChanges { get; set; } = [];

    [JsonPropertyName("score")]            public int             Score            { get; init; }
    [JsonPropertyName("missingFields")]    public List<string>    MissingFields    { get; init; } = [];
    [JsonPropertyName("contractCompatibility")] public object? ContractCompatibility { get; set; }
}

public sealed class IntegrationQualityReport
{
    [JsonPropertyName("environmentName")]    public string                   EnvironmentName   { get; init; } = "";
    [JsonPropertyName("generatedAt")]        public DateTime                 GeneratedAt       { get; init; }
    [JsonPropertyName("overallScore")]       public int                      OverallScore      { get; init; }
    [JsonPropertyName("integrationCount")]   public int                      IntegrationCount  { get; init; }
    [JsonPropertyName("enabledCount")]       public int                      EnabledCount      { get; init; }
    [JsonPropertyName("missingConfigCount")] public int                      MissingConfigCount { get; init; }
    [JsonPropertyName("isReadyForDeployment")] public bool                   IsReadyForDeployment { get; init; }
    [JsonPropertyName("findings")]           public List<IntegrationFinding> Findings          { get; init; } = [];
    [JsonPropertyName("statuses")]           public List<IntegrationStatus>  Statuses          { get; init; } = [];
    [JsonPropertyName("recommendations")]    public List<string>             Recommendations   { get; init; } = [];
    [JsonPropertyName("limitations")]        public List<string>             Limitations       { get; init; } = [];
    /// <summary>Authenticated availability and provenance for this review. Null when the environment has no authenticated-testing identity.</summary>
    [JsonPropertyName("authentication")]     public IntegrationAuthenticationSummary? Authentication { get; init; }

    // Snapshot history (Phase 3, Checkpoint 5). Nullable so a report written before history
    // existed reads as "not captured" rather than as an absent baseline.
    [JsonPropertyName("currentSnapshotId")]
    public Guid? CurrentSnapshotId { get; set; }

    [JsonPropertyName("previousSnapshotId")]
    public Guid? PreviousSnapshotId { get; set; }

    [JsonPropertyName("previousSnapshotCapturedAt")]
    public DateTimeOffset? PreviousSnapshotCapturedAt { get; set; }

    [JsonPropertyName("baselineAvailable")]
    public bool BaselineAvailable { get; set; }

    [JsonPropertyName("historicalChangeCount")]
    public int HistoricalChangeCount { get; set; }

    [JsonPropertyName("historicalChanges")]
    public List<IntegrationHistoricalChange> HistoricalChanges { get; set; } = [];

    [JsonPropertyName("snapshotPersistenceState")]
    public SnapshotPersistenceState SnapshotPersistenceState { get; set; } = SnapshotPersistenceState.NotAttempted;
}


/// <summary>
/// Outcome of persisting this review as a historical snapshot. A failure here must never be
/// reported as a successful save, and must never discard the completed review.
/// </summary>
public enum SnapshotPersistenceState
{
    NotAttempted = 0,
    Saved = 1,
    Failed = 2,
    SkippedIncompleteReview = 3
}
