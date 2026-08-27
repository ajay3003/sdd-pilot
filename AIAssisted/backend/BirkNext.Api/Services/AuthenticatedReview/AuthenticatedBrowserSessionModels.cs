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
    Authenticated,
    Expired,
    Cancelled,
    Failed,
    Disposed
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
    string? FailureCategory = null);

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
