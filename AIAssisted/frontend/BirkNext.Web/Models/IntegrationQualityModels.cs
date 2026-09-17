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
    // Contract compatibility and drift (Phase 3, Checkpoint 4). Nullable throughout so a report
    // produced before these fields existed reads as "not captured" rather than as a
    // compatibility claim: Compatible and NoChange are both 0.
    [JsonPropertyName("compatibilityState")]           public ContractCompatibilityStatus? CompatibilityState { get; init; }
    [JsonPropertyName("compatibilityComparedAt")]      public DateTime?          CompatibilityComparedAt      { get; init; }
    [JsonPropertyName("compatibilityDifferenceCount")] public int?               CompatibilityDifferenceCount { get; init; }
    [JsonPropertyName("compatibilityBreakingCount")]   public int?               CompatibilityBreakingCount   { get; init; }
    [JsonPropertyName("compatibilityDifferences")]     public List<ContractDifference> CompatibilityDifferences { get; init; } = [];
    [JsonPropertyName("compatibilityReason")]          public string?            CompatibilityReason          { get; init; }
    [JsonPropertyName("producerService")]              public string?            ProducerService              { get; init; }
    [JsonPropertyName("consumerService")]              public string?            ConsumerService              { get; init; }
    [JsonPropertyName("producerContractSource")]       public string?            ProducerContractSource       { get; init; }
    [JsonPropertyName("consumerContractSource")]       public string?            ConsumerContractSource       { get; init; }

    [JsonPropertyName("driftState")]                   public ContractDriftState? DriftState                  { get; init; }
    [JsonPropertyName("driftDifferenceCount")]         public int?               DriftDifferenceCount         { get; init; }
    [JsonPropertyName("driftBreakingCount")]           public int?               DriftBreakingCount           { get; init; }
    [JsonPropertyName("driftDifferences")]             public List<ContractDifference> DriftDifferences       { get; init; } = [];
    [JsonPropertyName("previousBaselineTimestamp")]    public DateTime?          PreviousBaselineTimestamp    { get; init; }
    [JsonPropertyName("currentContractFingerprint")]   public string?            CurrentContractFingerprint   { get; init; }
    [JsonPropertyName("previousContractFingerprint")]  public string?            PreviousContractFingerprint  { get; init; }

    // History (Phase 3, Checkpoint 5). The baseline key is diagnostic only and is not surfaced
    // as primary UI content.
    [JsonPropertyName("baselineKey")]        public string? BaselineKey { get; init; }
    [JsonPropertyName("historicalChanges")]  public List<IntegrationHistoricalChange> HistoricalChanges { get; init; } = [];

    [JsonPropertyName("score")]             public int             Score             { get; init; }
    [JsonPropertyName("missingFields")]     public List<string>    MissingFields     { get; init; } = [];
}

public enum IntegrationHistoricalChangeType
{
    IntegrationAdded = 0,
    IntegrationRemoved = 1,
    ProducerChanged = 2,
    ConsumerChanged = 3,
    RelationshipSourceChanged = 4,
    AuthenticationRequiredChanged = 5,
    AuthenticatedCapabilityChanged = 6,
    RuntimeEvidenceStateChanged = 7
}

public enum SnapshotPersistenceState
{
    NotAttempted = 0,
    Saved = 1,
    Failed = 2,
    SkippedIncompleteReview = 3
}

public sealed class IntegrationHistoricalChange
{
    [JsonPropertyName("type")]            public IntegrationHistoricalChangeType Type { get; init; }
    [JsonPropertyName("baselineKey")]     public string  BaselineKey     { get; init; } = "";
    [JsonPropertyName("integrationName")] public string  IntegrationName { get; init; } = "";
    [JsonPropertyName("oldValue")]        public string? OldValue        { get; init; }
    [JsonPropertyName("newValue")]        public string? NewValue        { get; init; }
    [JsonPropertyName("description")]     public string  Description     { get; init; } = "";
}

// Numeric values mirror the backend contract enums exactly; enums cross the wire as numbers.
public enum ContractCompatibilityStatus
{
    Compatible = 0,
    Warning = 1,
    Breaking = 2,
    Unsupported = 3,
    NotReady = 4,
    Error = 5,
    NotComparable = 6
}

public enum ContractDriftState
{
    NoChange = 0,
    NonBreakingChange = 1,
    BreakingChange = 2,
    BaselineUnavailable = 3,
    NotComparable = 4
}

public enum ContractDifferenceSeverity { Info = 0, Warning = 1, Breaking = 2 }

public sealed class ContractDifference
{
    [JsonPropertyName("type")]           public int    Type          { get; init; }
    [JsonPropertyName("path")]           public string Path          { get; init; } = "";
    [JsonPropertyName("operation")]      public string? Operation    { get; init; }
    [JsonPropertyName("property")]       public string? Property     { get; init; }
    [JsonPropertyName("producer_value")] public string? ProducerValue { get; init; }
    [JsonPropertyName("consumer_value")] public string? ConsumerValue { get; init; }
    [JsonPropertyName("severity")]       public ContractDifferenceSeverity Severity { get; init; }
    [JsonPropertyName("explanation")]    public string Explanation   { get; init; } = "";
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

    // Snapshot history (Phase 3, Checkpoint 5).
    [JsonPropertyName("currentSnapshotId")]          public Guid?           CurrentSnapshotId          { get; init; }
    [JsonPropertyName("previousSnapshotId")]         public Guid?           PreviousSnapshotId         { get; init; }
    [JsonPropertyName("previousSnapshotCapturedAt")] public DateTimeOffset? PreviousSnapshotCapturedAt { get; init; }
    [JsonPropertyName("baselineAvailable")]          public bool            BaselineAvailable          { get; init; }
    [JsonPropertyName("historicalChangeCount")]      public int             HistoricalChangeCount      { get; init; }
    [JsonPropertyName("historicalChanges")]          public List<IntegrationHistoricalChange> HistoricalChanges { get; init; } = [];
    [JsonPropertyName("snapshotPersistenceState")]   public SnapshotPersistenceState SnapshotPersistenceState { get; init; }
}
