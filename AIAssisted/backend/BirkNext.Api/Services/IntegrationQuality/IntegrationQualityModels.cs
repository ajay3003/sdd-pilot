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
    [JsonPropertyName("healthReachable")]  public bool?           HealthReachable  { get; init; }
    [JsonPropertyName("workerReachable")]  public bool?           WorkerReachable  { get; init; }
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
}
