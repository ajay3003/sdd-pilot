using System.Text.Json.Serialization;
using BirkNext.ManagedEdge;

namespace BirkNext.Web.Models;

/// <summary>
/// Detection state of a frontend profile.
/// Tracks the status of target environment detection for activation gate purposes.
/// </summary>
public enum DetectionState
{
    /// <summary>No detection has been run yet.</summary>
    NotChecked = 0,

    /// <summary>Interactive detection is currently running.</summary>
    Checking = 1,

    /// <summary>Detection succeeded; target is reachable without authentication.</summary>
    Complete = 2,

    /// <summary>Detection detected authentication is required to access the target.</summary>
    AuthenticationRequired = 3,

    /// <summary>Detection partially succeeded (warnings or incomplete results).</summary>
    Partial = 4,

    /// <summary>Detection was run but the target URL has changed since.</summary>
    Stale = 5,

    /// <summary>Detection failed with an error.</summary>
    Failed = 6,
    ManualAuthenticationVerificationRequired = 7
}

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

/// <summary>How an integration's configuration came to exist. Mirrors the backend enum by value.</summary>
public enum IntegrationConfigurationSource
{
    Unknown = 0,
    EndpointDiscovery = 1,
    CodeSuggested = 2,
    Manual = 3
}

/// <summary>The transport entity an integration addresses. Mirrors the backend enum by value.</summary>
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

/// <summary>
/// A reusable, logical M2LB integration known from an external audit of the source. Nothing here is
/// environment-specific: "Person CDC" is the same integration in Development, QA and Production, and
/// the values that differ between them live in <see cref="KnownIntegrationEnvironmentBinding"/>.
/// </summary>
public sealed class KnownIntegrationTemplate
{
    [JsonPropertyName("id")]                     public string Id { get; init; } = "";
    [JsonPropertyName("displayName")]            public string DisplayName { get; init; } = "";
    [JsonPropertyName("integrationType")]        public IntegrationType IntegrationType { get; init; }
    [JsonPropertyName("resourceKind")]           public IntegrationResourceKind ResourceKind { get; init; }
    [JsonPropertyName("suggestedProducer")]      public string? SuggestedProducer { get; init; }
    [JsonPropertyName("suggestedConsumer")]      public string? SuggestedConsumer { get; init; }
    [JsonPropertyName("relationshipNote")]       public string? RelationshipNote { get; init; }
    [JsonPropertyName("suggestionOrigin")]       public string SuggestionOrigin { get; init; } = "";
}

/// <summary>
/// The structural values one template has in ONE environment. A null field means no authoritative
/// value exists for that environment — never that another environment's value can stand in for it.
/// </summary>
public sealed class KnownIntegrationEnvironmentBinding
{
    [JsonPropertyName("templateId")]          public string TemplateId { get; init; } = "";
    [JsonPropertyName("environmentType")]     public string EnvironmentType { get; init; } = "";
    [JsonPropertyName("resource")]            public string? Resource { get; init; }
    [JsonPropertyName("endpointOrNamespace")] public string? EndpointOrNamespace { get; init; }
    [JsonPropertyName("consumerGroup")]       public string? ConsumerGroup { get; init; }
}

/// <summary>One template as it applies to one environment: the definition, its binding, and what is still missing.</summary>
public sealed class KnownIntegrationTemplateView
{
    [JsonPropertyName("template")]              public KnownIntegrationTemplate Template { get; init; } = new();
    [JsonPropertyName("environmentType")]       public string EnvironmentType { get; init; } = "";
    [JsonPropertyName("binding")]               public KnownIntegrationEnvironmentBinding? Binding { get; init; }
    [JsonPropertyName("requiredFields")]        public List<string> RequiredFields { get; init; } = [];
    [JsonPropertyName("missingRequiredFields")] public List<string> MissingRequiredFields { get; init; } = [];

    /// <summary>The environment cannot yet carry a stable structural identity for this integration.</summary>
    public bool NeedsConfiguration => MissingRequiredFields.Count > 0;
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

    // Provenance and entity kind. Absent in legacy configuration, which reads as Unknown rather
    // than claiming a provenance it never had.
    [JsonPropertyName("configurationSource")] public IntegrationConfigurationSource ConfigurationSource { get; set; } = IntegrationConfigurationSource.Unknown;
    [JsonPropertyName("resourceKind")]        public IntegrationResourceKind        ResourceKind        { get; set; } = IntegrationResourceKind.Unknown;

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
        // Determine readiness based on integration type and populated fields
        ContractMetadataReadiness = ComputeReadinessFor(Type);
    }

    private ContractMetadataReadiness ComputeReadinessFor(IntegrationType type)
    {
        var hasProducer = !string.IsNullOrWhiteSpace(LogicalProducerService);
        var hasConsumer = !string.IsNullOrWhiteSpace(LogicalConsumerService);
        var hasContract = !string.IsNullOrWhiteSpace(ContractName);
        var hasSourceType = ContractSourceType != ContractSourceType.Unknown;
        var hasSourceLocation = !string.IsNullOrWhiteSpace(ContractSourceLocation);

        // If nothing is configured, it's NotConfigured
        if (!hasProducer && !hasConsumer && !hasContract && !hasSourceType && !hasSourceLocation)
            return ContractMetadataReadiness.NotConfigured;

        // Type-specific readiness rules
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

public sealed class FrontendAnalysisSettings
{
    // The only persisted activity marker. UI selection is component-local.
    [JsonPropertyName("activeResolutionError")] public string? ActiveResolutionError { get; set; }
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
    [JsonPropertyName("manualVerification")] public ManualAuthenticationVerificationEvidence? ManualVerification { get; set; }

    [JsonPropertyName("id")]              public string                    Id              { get; set; } = "";
    [JsonPropertyName("name")]            public string                    Name            { get; set; } = "";
    [JsonPropertyName("environmentType")] public FrontendEnvironmentType   EnvironmentType { get; set; }
    [JsonPropertyName("description")]     public string?                   Description     { get; set; }
    [JsonPropertyName("notes")]           public string?                   Notes           { get; set; }

    // Frontend
    [JsonPropertyName("targetUrl")]               public string?       TargetUrl               { get; set; }
    [JsonPropertyName("lastDetectedUrl")]         public string?       LastDetectedUrl         { get; set; }
    [JsonPropertyName("lastDetectionSucceeded")]  public bool          LastDetectionSucceeded  { get; set; }
    [JsonPropertyName("lastDetectionFailure")]    public string?       LastDetectionFailure    { get; set; }

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
    [JsonPropertyName("reviewEngineSelection")] public ReviewEngineSelection ReviewEngineSelection { get; set; } = new();

    [JsonPropertyName("integrations")]   public List<IntegrationConfig>        Integrations   { get; set; } = [];
}

public sealed class FrontendAuthenticationSettings
{
    [JsonPropertyName("verificationMode")] public AuthenticationVerificationMode VerificationMode { get; set; }

    [JsonPropertyName("requiresAuthentication")]     public bool                      RequiresAuthentication     { get; set; }
    [JsonPropertyName("authenticationType")]         public FrontendAuthenticationType AuthenticationType         { get; set; }
    [JsonPropertyName("useExistingBrowserSession")]  public bool                      UseExistingBrowserSession  { get; set; }
    [JsonPropertyName("automaticallyOpenLoginPage")] public bool                      AutomaticallyOpenLoginPage { get; set; }
    [JsonPropertyName("expectedAuthority")]          public string?                   ExpectedAuthority          { get; set; }
    [JsonPropertyName("expectedTenant")]             public string?                   ExpectedTenant             { get; set; }
    [JsonPropertyName("expectedClientId")]           public string?                   ExpectedClientId           { get; set; }
    [JsonPropertyName("allowedRedirectUrls")]        public List<string>              AllowedRedirectUrls        { get; set; } = [];

    /// <summary>
    /// Saved Browser delivery trust policy for managed-Edge authenticated testing. This is the single policy source of truth:
    /// ExactOrigin (default) accepts only a tab at the configured target origin; ApprovedMcasProxyOrigin ALSO accepts a strongly
    /// correlated Microsoft Defender for Cloud Apps proxied delivery while still preferring the exact origin. Legacy profiles without
    /// the field deserialize to ExactOrigin. Observed delivery, trust decisions and sessions are runtime-only and never persisted.
    /// </summary>
    [JsonPropertyName("browserDeliveryTrust")]       public ManagedEdgeTrustModel     BrowserDeliveryTrust       { get; set; } = ManagedEdgeTrustModel.ExactOrigin;

    /// <summary>
    /// Saved choice of HOW authenticated testing is performed for this environment: the managed Edge browser context (CDP, default and
    /// legacy behaviour), the BirkNext-managed loopback HTTPS proxy (DEV/non-production only, credentials memory-only) or manual verification
    /// only. Legacy profiles without the field deserialize to ManagedEdgeCdp. BirkNext never switches this automatically. Proxy runtime state
    /// (session, port, certificate trust, observed traffic, credential availability) is transient and never persisted.
    /// </summary>
    [JsonPropertyName("authenticatedTestingMethod")] public BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod AuthenticatedTestingMethod { get; set; } = BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.ManagedEdgeCdp;
}

public sealed class FrontendPerformanceThresholds
{
    [JsonPropertyName("mode")]                           public FrontendThresholdMode Mode                           { get; set; } = FrontendThresholdMode.Default;
    [JsonPropertyName("maxStartupSizeBytes")]             public long                  MaxStartupSizeBytes             { get; set; } = 8L * 1024 * 1024;
    [JsonPropertyName("maxStartupRequests")]              public int                   MaxStartupRequests              { get; set; } = 30;
    [JsonPropertyName("maxStartupApiCalls")]              public int                   MaxStartupApiCalls              { get; set; } = 10;
    [JsonPropertyName("maxRestPayloadBytes")]             public long                  MaxRestPayloadBytes             { get; set; } = 500L * 1024;
    [JsonPropertyName("maxGraphQlPayloadBytes")]          public long                  MaxGraphQlPayloadBytes          { get; set; } = 1024L * 1024;
    /// <summary>
    /// Average API Latency. Owner: API Quality Review's aggregate check — the mean of one target's real review request timings (safe REST
    /// operations; needs ≥ 2 samples). Never applied to an individual request (that is <see cref="MaxSingleRequestLatencyMs"/>).
    /// </summary>
    [JsonPropertyName("maxAverageApiLatencyMs")]          public int                   MaxAverageApiLatencyMs          { get; set; } = 500;
    /// <summary>
    /// Latency of ONE API request. Owners: API Quality Review per-request response time (single tier: above = Warning, captured in
    /// the report's policy snapshot), FQR authenticated API-surface probes and Browser Quality browser-observed API calls.
    /// </summary>
    [JsonPropertyName("maxSingleRequestLatencyMs")]       public int                   MaxSingleRequestLatencyMs       { get; set; } = 1500;
    /// <summary>
    /// Compression Minimum Payload. Owner: API Quality Review's compression check — an uncompressed response smaller than this is Not
    /// applicable, not a Warning. 1 KB default: Azure Front Door / CDN (where M2LB is hosted) only compress responses from 1 KB.
    /// </summary>
    [JsonPropertyName("compressionMinPayloadBytes")]      public long                  CompressionMinPayloadBytes      { get; set; } = 1024;
    [JsonPropertyName("maxWasmRuntimeSizeBytes")]         public long                  MaxWasmRuntimeSizeBytes         { get; set; } = 3L * 1024 * 1024;
    [JsonPropertyName("maxFrameworkSizeBytes")]           public long                  MaxFrameworkSizeBytes           { get; set; } = 5L * 1024 * 1024;
    [JsonPropertyName("maxApplicationAssemblySizeBytes")] public long                  MaxApplicationAssemblySizeBytes { get; set; } = 3L * 1024 * 1024;
    [JsonPropertyName("maxIndividualAssetSizeBytes")]     public long                  MaxIndividualAssetSizeBytes     { get; set; } = 2L * 1024 * 1024;

    // ── BirkNext Performance Quality (native engine) thresholds. Documented defaults; a Target Environment may override each one. ──
    /// <summary>Proxy-observed API response time above which a call is "needs improvement" (warning). BirkNext Performance Quality only;
    /// API Quality Review uses <see cref="MaxSingleRequestLatencyMs"/>, never this pair.</summary>
    [JsonPropertyName("apiResponseWarningMs")]            public int                   ApiResponseWarningMs            { get; set; } = 500;
    /// <summary>Proxy-observed API response time above which a call is "poor".</summary>
    [JsonPropertyName("apiResponsePoorMs")]               public int                   ApiResponsePoorMs               { get; set; } = 1000;
    /// <summary>BirkNext Page Stabilization Time (route change → DOM/network quiet) considered good.</summary>
    [JsonPropertyName("pageStabilizationGoodMs")]         public int                   PageStabilizationGoodMs         { get; set; } = 2000;
    [JsonPropertyName("pageStabilizationPoorMs")]         public int                   PageStabilizationPoorMs         { get; set; } = 5000;
    /// <summary>Total JavaScript transferred for one page observation.</summary>
    [JsonPropertyName("maxJsTransferBytes")]              public long                  MaxJsTransferBytes              { get; set; } = 2L * 1024 * 1024;
    /// <summary>Single resource fetch duration (browser Resource Timing) above which the resource is slow.</summary>
    [JsonPropertyName("slowResourceMs")]                  public int                   SlowResourceMs                  { get; set; } = 2000;
    /// <summary>Long tasks (&gt; 50 ms) tolerated per page observation before a finding is raised.</summary>
    [JsonPropertyName("maxLongTasks")]                    public int                   MaxLongTasks                    { get; set; } = 3;
    /// <summary>BirkNext main-thread blocking time (Σ long task excess over 50 ms) above which the page needs improvement.</summary>
    [JsonPropertyName("mainThreadBlockingWarningMs")]     public int                   MainThreadBlockingWarningMs     { get; set; } = 300;
    /// <summary>Identical REST (method+host+path) / GraphQL (endpoint+operation) calls tolerated within one page generation.</summary>
    [JsonPropertyName("maxIdenticalApiCalls")]            public int                   MaxIdenticalApiCalls            { get; set; } = 2;
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

/// <summary>
/// Per-Target-Environment activation of the FRONTEND QUALITY REVIEW engines. Nothing here affects API Quality Review
/// (which derives its policy from Performance Thresholds + Environment Type and its targets from Endpoint Discovery)
/// or Integration Quality Review (which is driven by the per-integration <c>Enabled</c> flag under Integrations).
///
/// Thirteen legacy capability flags (assetDiscovery, startupAnalysis, restAnalysis, graphQlAnalysis, cachingReview,
/// compressionReview, blazorArchitectureReview, securityHeaderReview, configurationExposureReview, performanceReadiness,
/// authenticatedBrowserReview, lighthouseIntegration, playwrightRuntimeInspection) were removed: they had no consumer at
/// all, and the last three named capabilities that already ship under other names (Lighthouse, Browser Runtime and the
/// authenticated browser session). Their stale JSON keys in saved profiles are ignored on deserialization.
/// </summary>
public sealed class FrontendAnalysisFeatureToggles
{
    // Default engine activation for a new (or legacy, toggle-less) Target Environment: every quality engine is enabled except
    // Browser Runtime, which stays opt-in. Deployment policy / System Settings (Layer 1–2) may still keep an enabled engine
    // unavailable; that is reported as a capability blocker, never as "not enabled".
    [JsonPropertyName("enableSecurityEngine")]        public bool EnableSecurityEngine        { get; set; } = true;
    [JsonPropertyName("enablePerformanceEngine")]     public bool EnablePerformanceEngine     { get; set; } = true;
    [JsonPropertyName("enableBrowserRuntimeEngine")]  public bool EnableBrowserRuntimeEngine  { get; set; } = false;
    [JsonPropertyName("enableAccessibilityEngine")]   public bool EnableAccessibilityEngine   { get; set; } = true;
    [JsonPropertyName("enableLighthouseEngine")]      public bool EnableLighthouseEngine      { get; set; } = true;
    [JsonPropertyName("enablePassiveSecurityEngine")] public bool EnablePassiveSecurityEngine { get; set; } = true;
    /// <summary>BirkNext Browser Quality (Browser Companion): experimental native engine that needs the paired extension, so it is opt-in per Target Environment (like Browser Runtime). Release policy Optional.</summary>
    [JsonPropertyName("enableBrowserQualityEngine")]  public bool EnableBrowserQualityEngine  { get; set; } = false;
    /// <summary>BirkNext Performance Quality: native page/runtime/resource/API/Blazor performance engine over Browser Companion + Local HTTPS proxy evidence. Independent of Lighthouse; opt-in per Target Environment. Release policy Optional.</summary>
    [JsonPropertyName("enablePerformanceQualityEngine")] public bool EnablePerformanceQualityEngine { get; set; } = false;
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
    [JsonPropertyName("browserQuality")] public FrontendQualityEngineRequirement BrowserQuality { get; set; } = FrontendQualityEngineRequirement.Optional;
    [JsonPropertyName("performanceQuality")] public FrontendQualityEngineRequirement PerformanceQuality { get; set; } = FrontendQualityEngineRequirement.Optional;

    public FrontendQualityEngineRequirementPolicy ToPolicy() => new(new Dictionary<FrontendQualityEngineId, FrontendQualityEngineRequirement>
    {
        [FrontendQualityEngineId.StaticSecurity] = StaticSecurity,
        [FrontendQualityEngineId.PassivePerformance] = PassivePerformance,
        [FrontendQualityEngineId.BrowserRuntime] = BrowserRuntime,
        [FrontendQualityEngineId.Accessibility] = Accessibility,
        [FrontendQualityEngineId.Lighthouse] = Lighthouse,
        [FrontendQualityEngineId.PassiveSecurity] = PassiveSecurity,
        [FrontendQualityEngineId.BrowserQuality] = BrowserQuality,
        [FrontendQualityEngineId.PerformanceQuality] = PerformanceQuality,
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

/// <summary>
/// Runtime review engine selection state.
/// Distinct from FrontendAnalysisFeatureToggles (Layer 2 enabled state).
/// Selected tracks user choice for this review; enabled tracks System Settings.
/// Selected=false + Available=true is valid (engine can run, user didn't select it).
/// </summary>
/// <summary>
/// Per-review opt-out for the optional backend engines. Selection never activates an engine on its own: an engine is active only
/// when its saved feature toggle is enabled AND it is selected (see <see cref="FrontendQualityActiveEngines"/>). Defaults are
/// "selected", so enabling an engine in the Target Environment makes it active without a second step.
/// </summary>
public sealed class ReviewEngineSelection
{
    [JsonPropertyName("browserRuntimeSelected")]  public bool BrowserRuntimeSelected  { get; set; } = true;
    [JsonPropertyName("accessibilitySelected")]   public bool AccessibilitySelected   { get; set; } = true;
    [JsonPropertyName("lighthouseSelected")]      public bool LighthouseSelected      { get; set; } = true;
    [JsonPropertyName("passiveSecuritySelected")] public bool PassiveSecuritySelected { get; set; } = true;

    public Dictionary<FrontendQualityEngineIdDto, bool> ToSelectionMap() => new()
    {
        [FrontendQualityEngineIdDto.BrowserRuntime] = BrowserRuntimeSelected,
        [FrontendQualityEngineIdDto.Accessibility] = AccessibilitySelected,
        [FrontendQualityEngineIdDto.Lighthouse] = LighthouseSelected,
        [FrontendQualityEngineIdDto.PassiveSecurity] = PassiveSecuritySelected,
    };

    public static ReviewEngineSelection FromSelectionMap(Dictionary<FrontendQualityEngineIdDto, bool> map) =>
        new()
        {
            BrowserRuntimeSelected = map.TryGetValue(FrontendQualityEngineIdDto.BrowserRuntime, out var br) && br,
            AccessibilitySelected = map.TryGetValue(FrontendQualityEngineIdDto.Accessibility, out var acc) && acc,
            LighthouseSelected = map.TryGetValue(FrontendQualityEngineIdDto.Lighthouse, out var lh) && lh,
            PassiveSecuritySelected = map.TryGetValue(FrontendQualityEngineIdDto.PassiveSecurity, out var ps) && ps,
        };
}
