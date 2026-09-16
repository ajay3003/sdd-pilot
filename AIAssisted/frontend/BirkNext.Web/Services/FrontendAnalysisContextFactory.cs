using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

public interface IFrontendAnalysisContextFactory
{
    Task<FrontendAnalysisContext> GetActiveContextAsync();
}

/// <summary>
/// Builds a <see cref="FrontendAnalysisContext"/> from the active Frontend Analysis profile.
/// Calls <see cref="IFrontendAnalysisSettingsService.LoadAsync"/> before reading state,
/// so components do not need to load settings separately.
/// </summary>
public sealed class FrontendAnalysisContextFactory : IFrontendAnalysisContextFactory
{
    private readonly IFrontendAnalysisSettingsService       _settings;
    private readonly IAuthenticatedBrowserSessionService    _sessionService;
    private readonly IJSRuntime                             _js;

    public FrontendAnalysisContextFactory(
        IFrontendAnalysisSettingsService    settings,
        IAuthenticatedBrowserSessionService sessionService,
        IJSRuntime                          js)
    {
        _settings       = settings;
        _sessionService = sessionService;
        _js             = js;
    }

    public async Task<FrontendAnalysisContext> GetActiveContextAsync()
    {
        await _settings.LoadAsync(_js);

        var profile       = _settings.ActiveProfile;
        if (profile is null)
        {
            var error = _settings.Settings.ActiveResolutionError ??
                (string.IsNullOrWhiteSpace(_settings.Settings.ActiveProfileId)
                    ? "No active Target Environment"
                    : "Active Target Environment is unavailable.");
            return new FrontendAnalysisContext
            {
                ActiveTargetError = error,
                ValidationWarnings = [error],
                ValidationErrors = [error]
            };
        }

        // Detach all mutable configuration before yielding. Secrets remain memory-only;
        // the diagnostic/report profile below deliberately omits them.
        var credentials = profile.ApiAuth;
        profile = System.Text.Json.JsonSerializer.Deserialize<FrontendAnalysisProfile>(
            System.Text.Json.JsonSerializer.Serialize(profile))!;
        profile.ApiAuth = new TargetApiCredentials
        {
            AuthType = credentials.AuthType, ApiKeyHeaderName = credentials.ApiKeyHeaderName,
            BasicUsername = credentials.BasicUsername, BearerToken = credentials.BearerToken,
            ApiKey = credentials.ApiKey, BasicPassword = credentials.BasicPassword
        };
        var validation = _settings.ValidateProfile(profile);
        var sessionStatus = await _sessionService.GetStatusAsync();

        var allowedRestHosts        = (IReadOnlyList<string>) profile.AllowedRestHosts;
        var allowedGraphQlEndpoints = (IReadOnlyList<string>) profile.AllowedGraphQlEndpoints;
        var allowedBackendDomains   = (IReadOnlyList<string>) profile.Security.AllowedBackendDomains;
        var allowedCdnHosts         = (IReadOnlyList<string>) profile.Security.AllowedCdnHosts;

        return new FrontendAnalysisContext
        {
            ActiveProfile               = CreateSafeProfileSnapshot(profile),
            TargetUrl                   = profile.TargetUrl?.Trim() ?? "",
            RestBaseUrl                 = profile.RestBaseUrl?.Trim(),
            HealthEndpoint              = profile.HealthEndpoint?.Trim(),
            SwaggerUrl                  = profile.SwaggerUrl?.Trim(),
            GraphQlEndpoint             = profile.GraphQlEndpoint?.Trim(),
            ApiAuth                     = profile.ApiAuth,
            RequestTimeoutSeconds       = profile.RequestTimeoutSeconds,
            RetryCount                  = profile.RetryCount,
            AuthenticationType          = profile.Authentication.AuthenticationType,
            RequiresAuthentication      = profile.Authentication.RequiresAuthentication,
            UseExistingBrowserSession   = profile.Authentication.UseExistingBrowserSession,
            AutomaticallyOpenLoginPage  = profile.Authentication.AutomaticallyOpenLoginPage,
            PerformanceThresholds       = profile.Performance,
            CoreWebVitalsThresholds     = profile.CoreWebVitals,
            SecuritySettings            = profile.Security,
            FeatureToggles              = profile.Features,
            EngineRequirements          = profile.EngineRequirements,
            ReleasePolicy               = profile.ReleasePolicy,
            ReviewEngineSelection       = profile.ReviewEngineSelection,
            AllowedRestHosts            = allowedRestHosts,
            AllowedGraphQlEndpoints     = allowedGraphQlEndpoints,
            AllowedBackendDomains       = allowedBackendDomains,
            AllowedCdnHosts             = allowedCdnHosts,
            IsAuthenticatedSessionAvailable = sessionStatus == AuthenticatedBrowserSessionStatus.Authenticated,
            ValidationWarnings          = validation.Warnings,
            ValidationErrors            = validation.Errors,
            Integrations                = profile.Integrations.AsReadOnly(),
            // Identity and verification state are resolved against the SAVED profile (full authentication object), so the
            // digests match the proxy session / manual verification record. Digests only; no configuration values.
            ReviewIdentity              = ReviewAuthenticationIdentity.For(profile),
            ManualVerificationFingerprint = ManualAuthenticationVerificationEvidence.Fingerprint(profile),
            ManualVerificationStatus    = ResolveManualVerificationStatus(profile),
        };
    }

    private static ManualAuthenticationVerificationStatus ResolveManualVerificationStatus(FrontendAnalysisProfile profile) =>
        profile.ManualVerification?.StatusFor(profile)
        ?? (profile.Authentication.AuthenticatedTestingMethod == BirkNext.LocalHttpsProxy.AuthenticatedTestingMethod.ManualOnly
            || profile.Authentication.VerificationMode == AuthenticationVerificationMode.ManualManagedEdge
                ? ManualAuthenticationVerificationStatus.Required
                : ManualAuthenticationVerificationStatus.NotRequired);

    private static FrontendAnalysisProfile CreateSafeProfileSnapshot(FrontendAnalysisProfile profile)
    {
        // Create snapshot that preserves configuration but excludes secret credential values.
        // Secrets (BearerToken, ApiKey, BasicPassword) are NOT serialized to JSON by design.
        // The JSON serialization skips them because they lack [JsonPropertyName] attributes.
        var safeApiAuth = new TargetApiCredentials
        {
            AuthType = profile.ApiAuth.AuthType,
            ApiKeyHeaderName = profile.ApiAuth.ApiKeyHeaderName,
            BasicUsername = profile.ApiAuth.BasicUsername,
            // BearerToken, ApiKey, and BasicPassword are NOT included in serialization.
        };

        var snapshot = new FrontendAnalysisProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            EnvironmentType = profile.EnvironmentType,
            Description = profile.Description,
            Notes = profile.Notes,
            TargetUrl = profile.TargetUrl,
            RestBaseUrl = profile.RestBaseUrl,
            HealthEndpoint = profile.HealthEndpoint,
            SwaggerUrl = profile.SwaggerUrl,
            GraphQlEndpoint = profile.GraphQlEndpoint,
            ApiAuth = safeApiAuth,
            RequestTimeoutSeconds = profile.RequestTimeoutSeconds,
            RetryCount = profile.RetryCount,
            ExpectedApiGateway = profile.ExpectedApiGateway,
            AllowedRestHosts = [.. profile.AllowedRestHosts],
            AllowedGraphQlEndpoints = [.. profile.AllowedGraphQlEndpoints],
            ExpectedCdn = profile.ExpectedCdn,
            // Data-minimized copy: tenant and client identifiers stay out of the context (see
            // GetActiveContextAsync_DiagnosticsDoNotExposeSecrets). The saved policy choices (testing method, verification mode,
            // browser delivery trust) are plain enums and must be preserved so review pages and access resolution see the real
            // method. Fingerprints that depend on the full authentication object are computed from the saved profile by the
            // factory (ReviewIdentity / ManualVerificationFingerprint), never re-derived from this copy.
            Authentication = new FrontendAuthenticationSettings
            {
                VerificationMode = profile.Authentication.VerificationMode,
                RequiresAuthentication = profile.Authentication.RequiresAuthentication,
                AuthenticationType = profile.Authentication.AuthenticationType,
                UseExistingBrowserSession = profile.Authentication.UseExistingBrowserSession,
                AutomaticallyOpenLoginPage = profile.Authentication.AutomaticallyOpenLoginPage,
                ExpectedAuthority = profile.Authentication.ExpectedAuthority,
                AllowedRedirectUrls = [.. profile.Authentication.AllowedRedirectUrls],
                BrowserDeliveryTrust = profile.Authentication.BrowserDeliveryTrust,
                AuthenticatedTestingMethod = profile.Authentication.AuthenticatedTestingMethod,
            },
            Performance = profile.Performance,
            CoreWebVitals = profile.CoreWebVitals,
            Security = profile.Security,
            Features = profile.Features,
            EngineRequirements = profile.EngineRequirements,
            ReleasePolicy = profile.ReleasePolicy,
            Integrations = [.. profile.Integrations]
        };

        return snapshot;
    }
}
