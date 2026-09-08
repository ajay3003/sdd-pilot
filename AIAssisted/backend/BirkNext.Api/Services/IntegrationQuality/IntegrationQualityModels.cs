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

        if (!hasProducer && !hasConsumer && !hasContract && !hasSourceType && !hasSourceLocation)
            return ContractMetadataReadiness.NotConfigured;

        return type switch
        {
            IntegrationType.REST =>
                (hasSourceType && (hasSourceLocation || ContractSourceType == ContractSourceType.Auto))
                    ? ContractMetadataReadiness.Ready
                    : ContractMetadataReadiness.Partial,

            IntegrationType.GraphQL =>
                (hasSourceType && ContractSourceType == ContractSourceType.GraphQlSchema && hasConsumer)
                    ? ContractMetadataReadiness.Ready
                    : ContractMetadataReadiness.Partial,

            IntegrationType.EventHub or IntegrationType.ServiceBus =>
                ((hasProducer || hasConsumer) && (hasContract || hasSourceLocation))
                    ? ContractMetadataReadiness.Ready
                    : ContractMetadataReadiness.Partial,

            IntegrationType.Kafka or IntegrationType.RabbitMQ =>
                ((hasProducer || hasConsumer) && (hasContract || hasSourceLocation))
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
}
