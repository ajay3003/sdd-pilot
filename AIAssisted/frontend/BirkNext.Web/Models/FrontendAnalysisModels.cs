using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public enum FrontendEnvironmentType
{
    Local, Development, QA, Test, RC, Production, Custom
}

public enum FrontendAuthenticationType
{
    None, MicrosoftEntraId, OpenIdConnect, OAuth2, Custom
}

public enum FrontendThresholdMode
{
    Default, Strict, Custom
}

public enum TargetApiAuthType
{
    None, BearerToken, ApiKey, BasicAuth
}

public enum IntegrationType
{
    REST, GraphQL, EventHub, ServiceBus, Kafka, RabbitMQ, File, SOAP
}

public enum IntegrationAuthType
{
    None, ApiKey, BearerToken, BasicAuth, ManagedIdentity, ConnectionString, SasToken
}

public sealed class IntegrationConfig
{
    [JsonPropertyName("id")]            public string             Id          { get; set; } = "";
    [JsonPropertyName("name")]          public string             Name        { get; set; } = "";
    [JsonPropertyName("type")]          public IntegrationType    Type        { get; set; }
    [JsonPropertyName("endpoint")]      public string?            Endpoint    { get; set; }
    [JsonPropertyName("resource")]      public string?            Resource    { get; set; }
    [JsonPropertyName("consumer")]      public string?            Consumer    { get; set; }
    [JsonPropertyName("authType")]      public IntegrationAuthType AuthType   { get; set; }
    [JsonPropertyName("healthUrl")]     public string?            HealthUrl   { get; set; }
    [JsonPropertyName("workerUrl")]     public string?            WorkerUrl   { get; set; }
    [JsonPropertyName("monitoringUrl")] public string?            MonitoringUrl { get; set; }
    [JsonPropertyName("owner")]         public string?            Owner       { get; set; }
    [JsonPropertyName("enabled")]       public bool               Enabled     { get; set; } = true;
}

public sealed class FrontendAnalysisSettings
{
    [JsonPropertyName("profiles")]        public List<FrontendAnalysisProfile> Profiles        { get; set; } = [];
    [JsonPropertyName("activeProfileId")] public string?                       ActiveProfileId { get; set; }
}

public sealed class TargetApiCredentials
{
    [JsonPropertyName("authType")]         public TargetApiAuthType AuthType         { get; set; } = TargetApiAuthType.None;
    [JsonPropertyName("apiKeyHeaderName")] public string?           ApiKeyHeaderName { get; set; }
    [JsonPropertyName("basicUsername")]    public string?           BasicUsername    { get; set; }

    // CRITICAL: These secret properties use [JsonIgnore] to prevent serialization to browser storage.
    // They can be set during runtime editing but are NEVER persisted.
    // After page reload, they will be null and the UI must indicate credentials need to be re-entered.
    [JsonIgnore] public string?            BearerToken      { get; set; }
    [JsonIgnore] public string?            ApiKey           { get; set; }
    [JsonIgnore] public string?            BasicPassword    { get; set; }
}

public sealed class FrontendAnalysisProfile
{
    [JsonPropertyName("id")]              public string                    Id              { get; set; } = "";
    [JsonPropertyName("name")]            public string                    Name            { get; set; } = "";
    [JsonPropertyName("environmentType")] public FrontendEnvironmentType   EnvironmentType { get; set; }
    [JsonPropertyName("description")]     public string?                   Description     { get; set; }
    [JsonPropertyName("notes")]           public string?                   Notes           { get; set; }

    // Frontend
    [JsonPropertyName("targetUrl")]               public string?       TargetUrl               { get; set; }

    // REST API
    [JsonPropertyName("restBaseUrl")]      public string? RestBaseUrl      { get; set; }
    [JsonPropertyName("healthEndpoint")]   public string? HealthEndpoint   { get; set; }
    [JsonPropertyName("swaggerUrl")]       public string? SwaggerUrl       { get; set; }

    // GraphQL
    [JsonPropertyName("graphQlEndpoint")] public string? GraphQlEndpoint  { get; set; }

    // API Authentication (for review tool calls to REST/GraphQL APIs)
    [JsonPropertyName("apiAuth")] public TargetApiCredentials ApiAuth { get; set; } = new();

    // Request settings
    [JsonPropertyName("requestTimeoutSeconds")] public int RequestTimeoutSeconds { get; set; } = 30;
    [JsonPropertyName("retryCount")]            public int RetryCount            { get; set; } = 3;

    // Legacy / advanced target fields
    [JsonPropertyName("expectedApiGateway")]      public string?       ExpectedApiGateway      { get; set; }
    [JsonPropertyName("allowedRestHosts")]        public List<string>  AllowedRestHosts        { get; set; } = [];
    [JsonPropertyName("allowedGraphQlEndpoints")] public List<string>  AllowedGraphQlEndpoints { get; set; } = [];
    [JsonPropertyName("expectedCdn")]             public string?       ExpectedCdn             { get; set; }

    [JsonPropertyName("authentication")] public FrontendAuthenticationSettings Authentication { get; set; } = new();
    [JsonPropertyName("performance")]    public FrontendPerformanceThresholds  Performance    { get; set; } = new();
    [JsonPropertyName("coreWebVitals")]  public CoreWebVitalsThresholds        CoreWebVitals  { get; set; } = new();
    [JsonPropertyName("security")]       public FrontendSecuritySettings       Security       { get; set; } = new();
    [JsonPropertyName("features")]       public FrontendAnalysisFeatureToggles Features       { get; set; } = new();
    [JsonPropertyName("engineRequirements")] public FrontendQualityEngineRequirementSettings EngineRequirements { get; set; } = new();
    [JsonPropertyName("releasePolicy")] public FrontendQualityReleasePolicySettings ReleasePolicy { get; set; } = new();

    [JsonPropertyName("integrations")]   public List<IntegrationConfig>        Integrations   { get; set; } = [];
}

public sealed class FrontendAuthenticationSettings
{
    [JsonPropertyName("requiresAuthentication")]     public bool                      RequiresAuthentication     { get; set; }
    [JsonPropertyName("authenticationType")]         public FrontendAuthenticationType AuthenticationType         { get; set; }
    [JsonPropertyName("useExistingBrowserSession")]  public bool                      UseExistingBrowserSession  { get; set; }
    [JsonPropertyName("automaticallyOpenLoginPage")] public bool                      AutomaticallyOpenLoginPage { get; set; }
    [JsonPropertyName("expectedAuthority")]          public string?                   ExpectedAuthority          { get; set; }
    [JsonPropertyName("expectedTenant")]             public string?                   ExpectedTenant             { get; set; }
    [JsonPropertyName("expectedClientId")]           public string?                   ExpectedClientId           { get; set; }
    [JsonPropertyName("allowedRedirectUrls")]        public List<string>              AllowedRedirectUrls        { get; set; } = [];
}

public sealed class FrontendPerformanceThresholds
{
    [JsonPropertyName("mode")]                           public FrontendThresholdMode Mode                           { get; set; } = FrontendThresholdMode.Default;
    [JsonPropertyName("maxStartupSizeBytes")]             public long                  MaxStartupSizeBytes             { get; set; } = 8L * 1024 * 1024;
    [JsonPropertyName("maxStartupRequests")]              public int                   MaxStartupRequests              { get; set; } = 30;
    [JsonPropertyName("maxStartupApiCalls")]              public int                   MaxStartupApiCalls              { get; set; } = 10;
    [JsonPropertyName("maxRestPayloadBytes")]             public long                  MaxRestPayloadBytes             { get; set; } = 500L * 1024;
    [JsonPropertyName("maxGraphQlPayloadBytes")]          public long                  MaxGraphQlPayloadBytes          { get; set; } = 1024L * 1024;
    [JsonPropertyName("maxAverageApiLatencyMs")]          public int                   MaxAverageApiLatencyMs          { get; set; } = 500;
    [JsonPropertyName("maxSingleRequestLatencyMs")]       public int                   MaxSingleRequestLatencyMs       { get; set; } = 1500;
    [JsonPropertyName("maxWasmRuntimeSizeBytes")]         public long                  MaxWasmRuntimeSizeBytes         { get; set; } = 3L * 1024 * 1024;
    [JsonPropertyName("maxFrameworkSizeBytes")]           public long                  MaxFrameworkSizeBytes           { get; set; } = 5L * 1024 * 1024;
    [JsonPropertyName("maxApplicationAssemblySizeBytes")] public long                  MaxApplicationAssemblySizeBytes { get; set; } = 3L * 1024 * 1024;
    [JsonPropertyName("maxIndividualAssetSizeBytes")]     public long                  MaxIndividualAssetSizeBytes     { get; set; } = 2L * 1024 * 1024;
}

public sealed class CoreWebVitalsThresholds
{
    [JsonPropertyName("lcpGoodMs")] public int    LcpGoodMs { get; set; } = 2500;
    [JsonPropertyName("lcpPoorMs")] public int    LcpPoorMs { get; set; } = 4000;
    [JsonPropertyName("inpGoodMs")] public int    InpGoodMs { get; set; } = 200;
    [JsonPropertyName("inpPoorMs")] public int    InpPoorMs { get; set; } = 500;
    [JsonPropertyName("clsGood")]   public double ClsGood   { get; set; } = 0.1;
    [JsonPropertyName("clsPoor")]   public double ClsPoor   { get; set; } = 0.25;
}

public sealed class FrontendSecuritySettings
{
    [JsonPropertyName("expectedAuthority")]       public string?      ExpectedAuthority       { get; set; }
    [JsonPropertyName("expectedTenant")]          public string?      ExpectedTenant          { get; set; }
    [JsonPropertyName("expectedClientId")]        public string?      ExpectedClientId        { get; set; }
    [JsonPropertyName("allowedRedirectUrls")]     public List<string> AllowedRedirectUrls     { get; set; } = [];
    [JsonPropertyName("allowedBackendDomains")]   public List<string> AllowedBackendDomains   { get; set; } = [];
    [JsonPropertyName("allowedRestHosts")]        public List<string> AllowedRestHosts        { get; set; } = [];
    [JsonPropertyName("allowedGraphQlHosts")]     public List<string> AllowedGraphQlHosts     { get; set; } = [];
    [JsonPropertyName("allowedCdnHosts")]         public List<string> AllowedCdnHosts         { get; set; } = [];
    [JsonPropertyName("expectedSecurityHeaders")] public List<string> ExpectedSecurityHeaders { get; set; } =
    [
        "Content-Security-Policy",
        "X-Content-Type-Options",
        "Referrer-Policy",
        "Permissions-Policy",
        "Strict-Transport-Security"
    ];
}

public sealed class FrontendAnalysisFeatureToggles
{
    [JsonPropertyName("enableSecurityEngine")]        public bool EnableSecurityEngine        { get; set; } = true;
    [JsonPropertyName("enablePerformanceEngine")]     public bool EnablePerformanceEngine     { get; set; } = true;
    [JsonPropertyName("enableBrowserRuntimeEngine")]  public bool EnableBrowserRuntimeEngine  { get; set; } = false;
    [JsonPropertyName("enableAccessibilityEngine")]   public bool EnableAccessibilityEngine   { get; set; } = false;
    [JsonPropertyName("enableLighthouseEngine")]      public bool EnableLighthouseEngine      { get; set; } = false;
    [JsonPropertyName("enablePassiveSecurityEngine")] public bool EnablePassiveSecurityEngine { get; set; } = false;

    [JsonPropertyName("assetDiscovery")]              public bool AssetDiscovery              { get; set; } = true;
    [JsonPropertyName("startupAnalysis")]             public bool StartupAnalysis             { get; set; } = true;
    [JsonPropertyName("restAnalysis")]                public bool RestAnalysis                { get; set; } = true;
    [JsonPropertyName("graphQlAnalysis")]             public bool GraphQlAnalysis             { get; set; } = true;
    [JsonPropertyName("cachingReview")]               public bool CachingReview               { get; set; } = true;
    [JsonPropertyName("compressionReview")]           public bool CompressionReview           { get; set; } = true;
    [JsonPropertyName("blazorArchitectureReview")]    public bool BlazorArchitectureReview    { get; set; } = true;
    [JsonPropertyName("securityHeaderReview")]        public bool SecurityHeaderReview        { get; set; } = true;
    [JsonPropertyName("configurationExposureReview")] public bool ConfigurationExposureReview { get; set; } = true;
    [JsonPropertyName("performanceReadiness")]        public bool PerformanceReadiness        { get; set; } = true;
    [JsonPropertyName("authenticatedBrowserReview")]  public bool AuthenticatedBrowserReview  { get; set; } = false;
    [JsonPropertyName("lighthouseIntegration")]       public bool LighthouseIntegration       { get; set; } = false;
    [JsonPropertyName("playwrightRuntimeInspection")] public bool PlaywrightRuntimeInspection { get; set; } = false;
}

/// <summary>Explicit coverage policy; enabled state and tool availability never alter these values.</summary>
public sealed class FrontendQualityEngineRequirementSettings
{
    [JsonPropertyName("staticSecurity")] public FrontendQualityEngineRequirement StaticSecurity { get; set; } = FrontendQualityEngineRequirement.Required;
    [JsonPropertyName("passivePerformance")] public FrontendQualityEngineRequirement PassivePerformance { get; set; } = FrontendQualityEngineRequirement.Required;
    [JsonPropertyName("browserRuntime")] public FrontendQualityEngineRequirement BrowserRuntime { get; set; } = FrontendQualityEngineRequirement.Optional;
    [JsonPropertyName("accessibility")] public FrontendQualityEngineRequirement Accessibility { get; set; } = FrontendQualityEngineRequirement.Optional;
    [JsonPropertyName("lighthouse")] public FrontendQualityEngineRequirement Lighthouse { get; set; } = FrontendQualityEngineRequirement.Optional;
    [JsonPropertyName("passiveSecurity")] public FrontendQualityEngineRequirement PassiveSecurity { get; set; } = FrontendQualityEngineRequirement.Optional;

    public FrontendQualityEngineRequirementPolicy ToPolicy() => new(new Dictionary<FrontendQualityEngineId, FrontendQualityEngineRequirement>
    {
        [FrontendQualityEngineId.StaticSecurity] = StaticSecurity,
        [FrontendQualityEngineId.PassivePerformance] = PassivePerformance,
        [FrontendQualityEngineId.BrowserRuntime] = BrowserRuntime,
        [FrontendQualityEngineId.Accessibility] = Accessibility,
        [FrontendQualityEngineId.Lighthouse] = Lighthouse,
        [FrontendQualityEngineId.PassiveSecurity] = PassiveSecurity,
    });
}

public sealed class FrontendQualityReleasePolicySettings
{
    [JsonPropertyName("blockingLogicalIssueIds")] public List<string> BlockingLogicalIssueIds { get; set; } = [];
    [JsonPropertyName("reviewOptionalEngineFailures")] public bool ReviewOptionalEngineFailures { get; set; } = true;
}

public sealed class ProfileValidationResult
{
    public bool         IsValid  => Errors.Count == 0;
    public List<string> Errors   { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public sealed class FrontendAnalysisDiagnostics
{
    public string                  ActiveProfileName  { get; init; } = "";
    public string                  Environment        { get; init; } = "";
    public string?                 TargetUrl          { get; init; }
    public string                  AuthenticationType { get; init; } = "";
    public string                  ThresholdMode      { get; init; } = "";
    public List<string>            EnabledFeatures    { get; init; } = [];
    public List<string>            DisabledFeatures   { get; init; } = [];
    public ProfileValidationResult ValidationStatus   { get; init; } = new();
}
