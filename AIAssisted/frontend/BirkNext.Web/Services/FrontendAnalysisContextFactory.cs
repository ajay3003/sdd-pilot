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
        var sessionStatus = await _sessionService.GetStatusAsync();

        if (profile is null)
        {
            profile = _settings.Settings.Profiles.FirstOrDefault();

            if (profile is null)
            {
                return new FrontendAnalysisContext
                {
                    ValidationWarnings = [
                        "No active Target Environment is configured. " +
                        "Open System Settings → Target Environments to create and activate an environment."
                    ]
                };
            }
        }

        var validation = _settings.ValidateProfile(profile);

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
            AllowedRestHosts            = allowedRestHosts,
            AllowedGraphQlEndpoints     = allowedGraphQlEndpoints,
            AllowedBackendDomains       = allowedBackendDomains,
            AllowedCdnHosts             = allowedCdnHosts,
            IsAuthenticatedSessionAvailable = sessionStatus == AuthenticatedBrowserSessionStatus.Available,
            ValidationWarnings          = validation.Warnings,
            ValidationErrors            = validation.Errors,
            Integrations                = profile.Integrations.AsReadOnly()
        };
    }

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
            Authentication = new FrontendAuthenticationSettings
            {
                RequiresAuthentication = profile.Authentication.RequiresAuthentication,
                AuthenticationType = profile.Authentication.AuthenticationType,
                UseExistingBrowserSession = profile.Authentication.UseExistingBrowserSession,
                AutomaticallyOpenLoginPage = profile.Authentication.AutomaticallyOpenLoginPage,
                ExpectedAuthority = profile.Authentication.ExpectedAuthority,
                AllowedRedirectUrls = [.. profile.Authentication.AllowedRedirectUrls]
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
