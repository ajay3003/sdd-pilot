using BirkNext.Web.Models;
using System.Net;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

public interface IAuthenticatedBrowserSessionService
{
    Task<AuthenticatedBrowserSession>       GetOrCreateSessionAsync(FrontendAnalysisContext context);
    Task<AuthenticatedBrowserSession?>      GetCurrentSessionAsync();
    Task<AuthenticatedBrowserSession>       BeginAuthenticationAsync(FrontendAnalysisContext context);
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

    public Task<AuthenticatedBrowserSession> BeginAuthenticationAsync(FrontendAnalysisContext context) =>
        GetOrCreateSessionAsync(context);
}

/// <summary>
/// Safe frontend client for the backend-owned ephemeral browser session. It transports
/// identifiers and status only; Playwright objects and browser authentication state never
/// leave the local backend process.
/// </summary>
public sealed class AuthenticatedBrowserSessionService(HttpClient http, IFrontendAnalysisSettingsService settings)
    : IAuthenticatedBrowserSessionService
{
    private readonly string _reviewSessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private AuthenticatedBrowserSession? _current;

    public async Task<AuthenticatedBrowserSession> GetOrCreateSessionAsync(FrontendAnalysisContext context)
    {
        if (!context.RequiresAuthentication)
            return new AuthenticatedBrowserSession { StatusMessage = "Authentication not required for this profile." };
        if (_current is not null) return _current;

        var request = new { ReviewSessionId = _reviewSessionId, ProfileId = context.ActiveProfile.Id, TargetUrl = context.TargetUrl };
        using var response = await http.PostAsJsonAsync("api/frontend-quality/auth-session/start", request);
        if (!response.IsSuccessStatusCode)
            return new AuthenticatedBrowserSession { StatusMessage = "Authenticated browser runtime is unavailable.", SafeDiagnostics = [$"http_status={(int)response.StatusCode}"] };

        var value = await response.Content.ReadFromJsonAsync<SessionResponse>()
            ?? throw new InvalidOperationException("Authenticated browser session returned an empty response.");
        _current = Map(value, context.AuthenticationType.ToString());
        return _current;
    }

    public Task<AuthenticatedBrowserSession?> GetCurrentSessionAsync() => Task.FromResult(_current);

    public async Task<AuthenticatedBrowserSession> BeginAuthenticationAsync(FrontendAnalysisContext context)
    {
        var current = await GetOrCreateSessionAsync(context);
        if (string.IsNullOrWhiteSpace(current.SessionId)) return current;
        var authority = context.ActiveProfile.Authentication.ExpectedAuthority;
        if (string.IsNullOrWhiteSpace(authority))
        {
            current.StatusMessage = "Expected Entra authority is not configured.";
            return current;
        }

        using var response = await http.PostAsJsonAsync(
            $"api/frontend-quality/auth-session/{Uri.EscapeDataString(current.SessionId)}/authenticate",
            new { ReviewSessionId = _reviewSessionId, ProfileId = context.ActiveProfile.Id, ExpectedAuthority = authority });
        if (!response.IsSuccessStatusCode)
        {
            current.StatusMessage = "Secure sign-in could not be started.";
            return current;
        }
        var value = await response.Content.ReadFromJsonAsync<SessionResponse>()
            ?? throw new InvalidOperationException("Authentication returned an empty response.");
        _current = Map(value, context.AuthenticationType.ToString());
        return _current;
    }

    public async Task ClearSessionAsync()
    {
        if (_current is null) return;
        var profileId = settings.ActiveProfile?.Id;
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            using var response = await http.PostAsJsonAsync($"api/frontend-quality/auth-session/{Uri.EscapeDataString(_current.SessionId)}/cancel", new { ReviewSessionId = _reviewSessionId, ProfileId = profileId });
            if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound)) response.EnsureSuccessStatusCode();
        }
        _current = null;
    }

    public async Task<AuthenticatedBrowserSessionStatus> GetStatusAsync()
    {
        var profile = settings.ActiveProfile;
        if (profile is null) return AuthenticatedBrowserSessionStatus.NotConfigured;
        if (!profile.Authentication.RequiresAuthentication) return AuthenticatedBrowserSessionStatus.NotRequired;
        if (_current is null) return AuthenticatedBrowserSessionStatus.ReadyToStart;

        using var response = await http.GetAsync($"api/frontend-quality/auth-session/{Uri.EscapeDataString(_current.SessionId)}?reviewSessionId={Uri.EscapeDataString(_reviewSessionId)}&profileId={Uri.EscapeDataString(profile.Id)}");
        if (response.StatusCode == HttpStatusCode.NotFound) { _current = null; return AuthenticatedBrowserSessionStatus.Disposed; }
        if (!response.IsSuccessStatusCode) return AuthenticatedBrowserSessionStatus.Failed;
        var value = await response.Content.ReadFromJsonAsync<SessionResponse>();
        if (value is null) return AuthenticatedBrowserSessionStatus.Failed;
        _current = Map(value, profile.Authentication.AuthenticationType.ToString());
        return value.Status;
    }

    private static AuthenticatedBrowserSession Map(SessionResponse value, string authenticationType) => new()
    {
        SessionId = value.SessionId,
        TargetUrl = value.TargetOrigin,
        CreatedAt = value.StartedAt,
        ExpiresAt = value.ExpiresAt,
        AuthenticationType = authenticationType,
        IsAuthenticated = value.Status == AuthenticatedBrowserSessionStatus.Authenticated,
        StatusMessage = value.Status.ToString(),
        DeliveryContext = value.DeliveryContext.ToString(),
        ApplicationValidationCurrent = value.ApplicationValidationCurrent
    };

    private sealed record SessionResponse(string SessionId, AuthenticatedBrowserSessionStatus Status, string TargetOrigin, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, string? FailureCategory, AuthenticatedDeliveryContext DeliveryContext, bool ApplicationValidationCurrent);
}

public enum AuthenticatedDeliveryContext { None, DirectApplication, ConditionalAccessMonitoredSession, ProxiedApplicationDelivery }
