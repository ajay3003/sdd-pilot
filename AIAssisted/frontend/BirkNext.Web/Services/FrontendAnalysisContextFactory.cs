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
            ActiveProfile               = profile,
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
}
