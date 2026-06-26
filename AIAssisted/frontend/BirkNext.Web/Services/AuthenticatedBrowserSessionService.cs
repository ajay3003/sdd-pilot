using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IAuthenticatedBrowserSessionService
{
    Task<AuthenticatedBrowserSession>       GetOrCreateSessionAsync(FrontendAnalysisContext context);
    Task<AuthenticatedBrowserSession?>      GetCurrentSessionAsync();
    Task                                    ClearSessionAsync();
    Task<AuthenticatedBrowserSessionStatus> GetStatusAsync();
}

/// <summary>
/// Safe placeholder — authenticated browser automation is not yet implemented.
/// Returns NotRequired when no auth is configured; RequiredButNotAvailable otherwise.
/// Anonymous analysis continues in both cases.
/// </summary>
public sealed class PlaceholderAuthenticatedBrowserSessionService : IAuthenticatedBrowserSessionService
{
    private readonly IFrontendAnalysisSettingsService _settings;

    public PlaceholderAuthenticatedBrowserSessionService(IFrontendAnalysisSettingsService settings)
        => _settings = settings;

    public Task<AuthenticatedBrowserSessionStatus> GetStatusAsync()
    {
        var profile = _settings.ActiveProfile;
        if (profile is null || !profile.Authentication.RequiresAuthentication)
            return Task.FromResult(AuthenticatedBrowserSessionStatus.NotRequired);

        return Task.FromResult(AuthenticatedBrowserSessionStatus.RequiredButNotAvailable);
    }

    public Task<AuthenticatedBrowserSession> GetOrCreateSessionAsync(FrontendAnalysisContext context)
    {
        if (!context.RequiresAuthentication)
        {
            return Task.FromResult(new AuthenticatedBrowserSession
            {
                StatusMessage   = "Authentication not required for this profile.",
                SafeDiagnostics = ["auth_required=false"]
            });
        }

        return Task.FromResult(new AuthenticatedBrowserSession
        {
            TargetUrl          = context.TargetUrl,
            AuthenticationType = context.AuthenticationType.ToString(),
            StatusMessage      = "Authenticated browser session is required but not implemented yet. " +
                                 "Anonymous analysis will run where possible.",
            SafeDiagnostics    = [$"auth_type={context.AuthenticationType}", "session_available=false"]
        });
    }

    public Task<AuthenticatedBrowserSession?> GetCurrentSessionAsync() =>
        Task.FromResult<AuthenticatedBrowserSession?>(null);

    public Task ClearSessionAsync() => Task.CompletedTask;
}
