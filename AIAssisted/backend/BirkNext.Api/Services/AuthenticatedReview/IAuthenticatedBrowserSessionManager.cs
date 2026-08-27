namespace BirkNext.Api.Services.AuthenticatedReview;

public interface IAuthenticatedBrowserSessionManager
{
    Task<AuthenticatedBrowserSessionDescriptor> StartAsync(AuthenticatedBrowserSessionRequest request, CancellationToken cancellationToken = default);
    Task<AuthenticatedBrowserSessionDescriptor?> GetStatusAsync(string sessionId, string reviewSessionId, string profileId, CancellationToken cancellationToken = default);
    Task<bool> CancelAsync(string sessionId, string reviewSessionId, string profileId, CancellationToken cancellationToken = default);
    Task<AuthenticatedBrowserSessionDescriptor> BeginAuthenticationAsync(BeginAuthenticationRequest request, CancellationToken cancellationToken = default);
    Task<IAuthenticatedBrowserPageLease> AcquireAuthenticationPageLeaseAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, CancellationToken cancellationToken = default);
    Task<IAuthenticatedBrowserPageLease> AcquirePageLeaseAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, CancellationToken cancellationToken = default);
}
