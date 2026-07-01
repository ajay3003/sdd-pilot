using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// Provides a connectivity-focused view of the configured environments.
/// Delegates storage to <see cref="IFrontendAnalysisSettingsService"/>; all CRUD
/// is still performed through that service so existing pages are unaffected.
/// </summary>
public interface ITargetEnvironmentService
{
    bool IsLoaded { get; }

    /// <summary>Ensures settings are loaded from localStorage.</summary>
    Task LoadAsync(IJSRuntime js);

    /// <summary>The currently active environment, or null if none is configured.</summary>
    TargetEnvironment? ActiveEnvironment { get; }

    /// <summary>All configured environments.</summary>
    IReadOnlyList<TargetEnvironment> Environments { get; }
}

public sealed class TargetEnvironmentService : ITargetEnvironmentService
{
    private readonly IFrontendAnalysisSettingsService _settings;

    public TargetEnvironmentService(IFrontendAnalysisSettingsService settings)
    {
        _settings = settings;
    }

    public bool IsLoaded => _settings.IsLoaded;

    public async Task LoadAsync(IJSRuntime js) => await _settings.LoadAsync(js);

    public TargetEnvironment? ActiveEnvironment => Map(_settings.ActiveProfile, isActive: true);

    public IReadOnlyList<TargetEnvironment> Environments =>
        _settings.Settings.Profiles
            .Select(p => Map(p, p.Id == _settings.Settings.ActiveProfileId)!)
            .Where(e => e is not null)
            .ToList();

    private static TargetEnvironment? Map(FrontendAnalysisProfile? profile, bool isActive)
    {
        if (profile is null) return null;

        return new TargetEnvironment
        {
            Id                     = profile.Id,
            Name                   = profile.Name,
            Description            = profile.Description,
            EnvironmentType        = profile.EnvironmentType,
            IsActive               = isActive,
            FrontendBaseUrl        = profile.TargetUrl,
            RestBaseUrl            = profile.RestBaseUrl,
            HealthEndpoint         = profile.HealthEndpoint,
            SwaggerUrl             = profile.SwaggerUrl,
            GraphQlEndpoint        = profile.GraphQlEndpoint,
            ApiAuth                = profile.ApiAuth,
            RequestTimeoutSeconds  = profile.RequestTimeoutSeconds,
            RetryCount             = profile.RetryCount,
        };
    }
}
