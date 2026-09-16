namespace BirkNext.Web.Models;

public sealed class FrontendAnalysisContext
{
    public string? ActiveTargetError { get; init; }
    public FrontendAnalysisProfile           ActiveProfile              { get; set; } = new();

    // Frontend URL (= TargetUrl on the profile — used by Frontend Quality Review)
    public string                            TargetUrl                  { get; set; } = "";

    // Additional endpoint URLs from the Target Environment
    public string?                           RestBaseUrl                { get; set; }
    public string?                           HealthEndpoint             { get; set; }
    public string?                           SwaggerUrl                 { get; set; }
    public string?                           GraphQlEndpoint            { get; set; }

    // API authentication credentials
    public TargetApiCredentials              ApiAuth                    { get; set; } = new();

    // Request settings
    public int                               RequestTimeoutSeconds      { get; set; } = 30;
    public int                               RetryCount                 { get; set; } = 3;

    public FrontendAuthenticationType        AuthenticationType         { get; set; }
    public bool                              RequiresAuthentication     { get; set; }
    public bool                              UseExistingBrowserSession  { get; set; }
    public bool                              AutomaticallyOpenLoginPage { get; set; }
    public FrontendPerformanceThresholds     PerformanceThresholds      { get; set; } = new();
    public CoreWebVitalsThresholds           CoreWebVitalsThresholds    { get; set; } = new();
    public FrontendSecuritySettings          SecuritySettings           { get; set; } = new();
    public FrontendAnalysisFeatureToggles    FeatureToggles             { get; set; } = new();
    public FrontendQualityEngineRequirementSettings EngineRequirements   { get; set; } = new();
    public FrontendQualityReleasePolicySettings ReleasePolicy            { get; set; } = new();
    public ReviewEngineSelection              ReviewEngineSelection      { get; set; } = new();
    public IReadOnlyList<string>             AllowedRestHosts           { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             AllowedGraphQlEndpoints    { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             AllowedBackendDomains      { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             AllowedCdnHosts            { get; set; } = Array.Empty<string>();
    public bool                              IsAuthenticatedSessionAvailable { get; set; }
    public string?                           SessionId                  { get; set; }

    /// <summary>
    /// Non-secret authenticated-review identity (saved testing method, profile id, proxy context fingerprint digest) computed from
    /// the SAVED profile by the context factory. Reviews must use this instead of re-deriving it from <see cref="ActiveProfile"/>,
    /// whose data-minimized authentication copy omits tenant/client identifiers and therefore yields a different fingerprint.
    /// </summary>
    public BirkNext.LocalHttpsProxy.AuthenticatedReviewIdentity? ReviewIdentity { get; set; }

    /// <summary>Manual-verification context fingerprint digest of the saved profile (never configuration values).</summary>
    public string?                           ManualVerificationFingerprint { get; set; }

    /// <summary>Manual authentication verification status resolved against the saved profile; null when not resolved by the factory.</summary>
    public ManualAuthenticationVerificationStatus? ManualVerificationStatus { get; set; }
    public IReadOnlyList<string>             ValidationWarnings         { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             ValidationErrors           { get; set; } = Array.Empty<string>();

    public IReadOnlyList<IntegrationConfig> Integrations { get; set; } = Array.Empty<IntegrationConfig>();

    public bool HasTargetUrl           => !string.IsNullOrWhiteSpace(TargetUrl);
    public bool HasRestBaseUrl         => !string.IsNullOrWhiteSpace(RestBaseUrl);
    public bool HasGraphQlEndpoint     => !string.IsNullOrWhiteSpace(GraphQlEndpoint);
    public bool HasValidationErrors    => ValidationErrors.Count > 0;
    public bool HasValidationWarnings  => ValidationWarnings.Count > 0;
    public bool AuthRequiredButUnavailable =>
        RequiresAuthentication && !IsAuthenticatedSessionAvailable;
}

public enum AuthenticatedBrowserSessionStatus
{
    NotConfigured,
    NotRequired,
    ReadyToStart,
    Starting,
    BrowserReady,
    AuthenticationRequired,
    AuthenticationInProgress,
    ConditionalAccessIntermediary,
    AwaitingUserContinuation,
    Authenticated,
    AuthenticationExpired,
    AuthenticationCancelled,
    AuthenticationFailed,
    UnexpectedOrigin,
    Expired = AuthenticationExpired,
    Cancelled = AuthenticationCancelled,
    Failed = AuthenticationFailed,
    Disposed = 14,
    RequiredButNotAvailable = ReadyToStart,
    Available = Authenticated,
    Error = Failed
}

public sealed class AuthenticatedBrowserSession
{
    public string                    SessionId         { get; set; } = "";
    public string                    TargetUrl         { get; set; } = "";
    public bool                      IsAuthenticated   { get; set; }
    public DateTimeOffset            CreatedAt         { get; set; }
    public DateTimeOffset?           LastUsedAt        { get; set; }
    public DateTimeOffset?           ExpiresAt         { get; set; }
    public string                    AuthenticationType { get; set; } = "";
    public string                    StatusMessage     { get; set; } = "";
    public IReadOnlyList<string>     SafeDiagnostics   { get; set; } = Array.Empty<string>();
    public string                    DeliveryContext   { get; set; } = "None";
    public bool                      ApplicationValidationCurrent { get; set; }
}
