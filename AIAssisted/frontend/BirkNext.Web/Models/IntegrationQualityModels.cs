using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public enum IntegrationFindingSeverity { Critical, High, Medium, Low, Info }

public sealed class IntegrationQualityRequest
{
    [JsonPropertyName("environmentName")] public string                  EnvironmentName { get; set; } = "";
    [JsonPropertyName("integrations")]    public List<IntegrationConfig> Integrations    { get; set; } = [];
    [JsonPropertyName("timeoutSeconds")]  public int                     TimeoutSeconds  { get; set; } = 30;
}

public sealed class IntegrationFinding
{
    [JsonPropertyName("id")]              public string                     Id              { get; init; } = "";
    [JsonPropertyName("integrationId")]   public string                     IntegrationId   { get; init; } = "";
    [JsonPropertyName("integrationName")] public string                     IntegrationName { get; init; } = "";
    [JsonPropertyName("title")]           public string                     Title           { get; init; } = "";
    [JsonPropertyName("description")]     public string                     Description     { get; init; } = "";
    [JsonPropertyName("recommendation")]  public string                     Recommendation  { get; init; } = "";
    [JsonPropertyName("severity")]        public IntegrationFindingSeverity Severity        { get; init; }
    [JsonPropertyName("evidence")]        public List<string>               Evidence        { get; init; } = [];
}

public sealed class IntegrationStatus
{
    [JsonPropertyName("integrationId")]     public string          IntegrationId     { get; init; } = "";
    [JsonPropertyName("name")]              public string          Name              { get; init; } = "";
    [JsonPropertyName("type")]              public IntegrationType Type              { get; init; }
    [JsonPropertyName("enabled")]           public bool            Enabled           { get; init; }
    [JsonPropertyName("hasRequiredFields")] public bool            HasRequiredFields { get; init; }
    [JsonPropertyName("healthReachable")]   public bool?           HealthReachable   { get; init; }
    [JsonPropertyName("workerReachable")]   public bool?           WorkerReachable   { get; init; }
    [JsonPropertyName("score")]             public int             Score             { get; init; }
    [JsonPropertyName("missingFields")]     public List<string>    MissingFields     { get; init; } = [];
}

public sealed class IntegrationQualityReport
{
    [JsonPropertyName("environmentName")]       public string                   EnvironmentName      { get; init; } = "";
    [JsonPropertyName("generatedAt")]           public DateTime                 GeneratedAt          { get; init; }
    [JsonPropertyName("overallScore")]          public int                      OverallScore         { get; init; }
    [JsonPropertyName("integrationCount")]      public int                      IntegrationCount     { get; init; }
    [JsonPropertyName("enabledCount")]          public int                      EnabledCount         { get; init; }
    [JsonPropertyName("missingConfigCount")]    public int                      MissingConfigCount   { get; init; }
    [JsonPropertyName("isReadyForDeployment")]  public bool                     IsReadyForDeployment { get; init; }
    [JsonPropertyName("findings")]              public List<IntegrationFinding> Findings             { get; init; } = [];
    [JsonPropertyName("statuses")]              public List<IntegrationStatus>  Statuses             { get; init; } = [];
    [JsonPropertyName("recommendations")]       public List<string>             Recommendations      { get; init; } = [];
    [JsonPropertyName("limitations")]           public List<string>             Limitations          { get; init; } = [];
}
