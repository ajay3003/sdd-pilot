namespace BirkNext.Web.Models;

public sealed class FrontendAnalysisContext
{
    public FrontendAnalysisProfile           ActiveProfile              { get; set; } = new();
    public string                            TargetUrl                  { get; set; } = "";
    public FrontendAuthenticationType        AuthenticationType         { get; set; }
    public bool                              RequiresAuthentication     { get; set; }
    public bool                              UseExistingBrowserSession  { get; set; }
    public bool                              AutomaticallyOpenLoginPage { get; set; }
    public FrontendPerformanceThresholds     PerformanceThresholds      { get; set; } = new();
    public CoreWebVitalsThresholds           CoreWebVitalsThresholds    { get; set; } = new();
    public FrontendSecuritySettings          SecuritySettings           { get; set; } = new();
    public FrontendAnalysisFeatureToggles    FeatureToggles             { get; set; } = new();
    public IReadOnlyList<string>             AllowedRestHosts           { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             AllowedGraphQlEndpoints    { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             AllowedBackendDomains      { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             AllowedCdnHosts            { get; set; } = Array.Empty<string>();
    public bool                              IsAuthenticatedSessionAvailable { get; set; }
    public string?                           SessionId                  { get; set; }
    public IReadOnlyList<string>             ValidationWarnings         { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string>             ValidationErrors           { get; set; } = Array.Empty<string>();

    public bool HasTargetUrl         => !string.IsNullOrWhiteSpace(TargetUrl);
    public bool HasValidationErrors  => ValidationErrors.Count > 0;
    public bool HasValidationWarnings => ValidationWarnings.Count > 0;
    public bool AuthRequiredButUnavailable =>
        RequiresAuthentication && !IsAuthenticatedSessionAvailable;
}

public enum AuthenticatedBrowserSessionStatus
{
    NotConfigured,
    NotRequired,
    RequiredButNotAvailable,
    Available,
    Expired,
    Error
}

public sealed class AuthenticatedBrowserSession
{
    public string                    SessionId         { get; set; } = "";
    public string                    TargetUrl         { get; set; } = "";
    public bool                      IsAuthenticated   { get; set; }
    public DateTimeOffset            CreatedAt         { get; set; }
    public DateTimeOffset?           LastUsedAt        { get; set; }
    public string                    AuthenticationType { get; set; } = "";
    public string                    StatusMessage     { get; set; } = "";
    public IReadOnlyList<string>     SafeDiagnostics   { get; set; } = Array.Empty<string>();
}
