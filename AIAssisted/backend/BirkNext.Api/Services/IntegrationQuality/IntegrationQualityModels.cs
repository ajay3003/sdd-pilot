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
