using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public enum IntegrationFindingSeverity { Critical, High, Medium, Low, Info }

public sealed class IntegrationQualityRequest
{
    [JsonPropertyName("environmentName")] public string                  EnvironmentName { get; set; } = "";
    [JsonPropertyName("integrations")]    public List<IntegrationConfig> Integrations    { get; set; } = [];
    [JsonPropertyName("timeoutSeconds")]  public int                     TimeoutSeconds  { get; set; } = 30;

    [JsonPropertyName("authenticatedTestingMethod")] public BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod AuthenticatedTestingMethod { get; set; } = BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.ManagedEdgeCdp;
    [JsonPropertyName("profileId")]          public string? ProfileId          { get; set; }
    [JsonPropertyName("contextFingerprint")] public string? ContextFingerprint { get; set; }
}

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

public sealed class IntegrationAuthenticationSummary
{
    [JsonPropertyName("capabilities")] public BirkNext.LocalHttpsProxy.AuthenticatedReviewCapabilities Capabilities { get; init; } = new();
    [JsonPropertyName("checks")]       public List<IntegrationAuthenticatedCheck> Checks { get; init; } = [];
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
    [JsonPropertyName("authentication")]        public IntegrationAuthenticationSummary? Authentication { get; init; }
}
