using Microsoft.Playwright;

namespace BirkNext.Api.Services.AuthenticatedReview;

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
    Disposed = 14
}

public enum AuthenticatedDeliveryContext
{
    None,
    DirectApplication,
    ConditionalAccessMonitoredSession,
    ProxiedApplicationDelivery
}

public sealed record AuthenticatedBrowserSessionRequest(
    string ReviewSessionId,
    string ProfileId,
    string TargetUrl);

public sealed record AuthenticatedBrowserSessionDescriptor(
    string SessionId,
    string ReviewSessionId,
    string ProfileId,
    string TargetOrigin,
    AuthenticatedBrowserSessionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    string? FailureCategory = null,
    AuthenticatedDeliveryContext DeliveryContext = AuthenticatedDeliveryContext.None,
    bool ApplicationValidationCurrent = false);

public sealed record BeginAuthenticationRequest(
    string SessionId,
    string ReviewSessionId,
    string ProfileId,
    string ExpectedAuthority,
    string? SyntheticMcasOrigin = null);

public interface IAuthenticatedBrowserPageLease : IAsyncDisposable
{
    string SessionId { get; }
    IPage Page { get; }
    IBrowserContext Context { get; }
    CancellationToken SessionCancellation { get; }
}

internal interface IAuthenticatedBrowserResources : IAsyncDisposable
{
    IBrowser Browser { get; }
    IBrowserContext Context { get; }
    IPage Page { get; }
    event EventHandler? BrowserDisconnected;
}

internal interface IAuthenticatedBrowserHost
{
    Task<IAuthenticatedBrowserResources> LaunchAsync(Uri target, CancellationToken cancellationToken);
}
