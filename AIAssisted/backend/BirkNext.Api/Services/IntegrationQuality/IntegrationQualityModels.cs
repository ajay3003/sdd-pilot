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

/// <summary>
/// How an integration's configuration came to exist. Distinct from RelationshipSource, which
/// records where producer/consumer came from: an integration can be entered manually while its
/// relationship is derived from messaging metadata, and both facts are worth keeping.
///
/// Deliberately excluded from the baseline key: promoting a suggestion to a manual entry, or
/// having discovery confirm a manual one, must not orphan that integration's history.
/// </summary>
public enum IntegrationConfigurationSource
{
    /// <summary>Provenance not recorded. The safe default for configuration written before this existed.</summary>
    Unknown = 0,

    /// <summary>Proposed from traffic the Local HTTPS Proxy actually observed.</summary>
    EndpointDiscovery = 1,

    /// <summary>Pre-filled from a value proven in the target application's own source. Editable, and never claimed as runtime-verified.</summary>
    CodeSuggested = 2,

    /// <summary>Entered or edited by a person. Highest configuration authority.</summary>
    Manual = 3
}

/// <summary>
/// The transport entity an integration addresses. The generic Endpoint/Resource pair cannot
/// distinguish a Service Bus queue from a topic or a subscription, which changes both what the
/// configuration means and which fields are required.
/// </summary>
public enum IntegrationResourceKind
{
    Unknown = 0,
    RestEndpoint = 1,
    GraphQlEndpoint = 2,
    EventHub = 3,
    ServiceBusQueue = 4,
    ServiceBusTopic = 5,
    ServiceBusSubscription = 6,
    KafkaTopic = 7,
    RabbitExchange = 8,
    RabbitQueue = 9
}

public enum RelationshipSource
{
    Configured = 0,        // Explicitly configured producer/consumer
    DiscoveredTraffic = 1, // Inferred from runtime traffic observation
    MessagingMetadata = 2, // From message/event metadata
    Unknown = 3            // Relationship source not determined
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

    /// <summary>
    /// Where this configuration came from. Absent in legacy configuration, which reads as
    /// Unknown rather than claiming a provenance it never had.
    /// </summary>
    [JsonPropertyName("configurationSource")]
    public IntegrationConfigurationSource ConfigurationSource { get; set; } = IntegrationConfigurationSource.Unknown;

    /// <summary>
    /// Which transport entity Resource names. Legacy configuration reads as Unknown, so nothing
    /// is inferred about entities recorded before the distinction existed.
    /// </summary>
    [JsonPropertyName("resourceKind")]
    public IntegrationResourceKind ResourceKind { get; set; } = IntegrationResourceKind.Unknown;

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
