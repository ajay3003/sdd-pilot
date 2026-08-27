using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.AuthenticatedReview;

internal sealed class AuthenticatedBrowserSessionManager : IAuthenticatedBrowserSessionManager, IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _reviewSessions = new(StringComparer.Ordinal);
    private readonly IAuthenticatedBrowserHost _browserHost;
    private readonly AuthenticatedReviewOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthenticatedBrowserSessionManager> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _expiryLoop;

    public AuthenticatedBrowserSessionManager(
        IAuthenticatedBrowserHost browserHost,
        IOptions<AuthenticatedReviewOptions> options,
        TimeProvider time,
        ILogger<AuthenticatedBrowserSessionManager> logger)
    {
        _browserHost = browserHost;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<AuthenticatedBrowserSessionDescriptor> StartAsync(AuthenticatedBrowserSessionRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        ValidateBinding(request);
        var target = NormalizeTarget(request.TargetUrl);
        var reviewKey = BindingKey(request.ReviewSessionId, request.ProfileId);

        if (_reviewSessions.TryGetValue(reviewKey, out var existingId) && _sessions.TryGetValue(existingId, out var existing))
        {
            if (!string.Equals(existing.TargetOrigin, target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
                throw new AuthenticatedSessionConflictException("The review already owns a session for another target origin.");
            return existing.Descriptor;
        }

        var now = _time.GetUtcNow();
        var id = CreateOpaqueId();
        var entry = new Entry(id, request.ReviewSessionId, request.ProfileId, target.GetLeftPart(UriPartial.Authority), now, now + _options.AbsoluteLifetime);
        if (!_reviewSessions.TryAdd(reviewKey, id) || !_sessions.TryAdd(id, entry))
        {
            _reviewSessions.TryGetValue(reviewKey, out existingId);
            if (existingId is not null && _sessions.TryGetValue(existingId, out existing)) return existing.Descriptor;
            throw new AuthenticatedSessionConflictException("A session is already starting for this review.");
        }

        _logger.LogInformation("Authenticated browser session {SessionId} starting for profile {ProfileId} origin {TargetOrigin}", id, request.ProfileId, entry.TargetOrigin);
        try
        {
            using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Cancellation.Token);
            entry.Resources = await _browserHost.LaunchAsync(target, launchCancellation.Token);
            entry.Resources.BrowserDisconnected += (_, _) => _ = FailAndDisposeAsync(entry, "browser_disconnected");
            entry.Status = AuthenticatedBrowserSessionStatus.BrowserReady;
            entry.Touch(_time.GetUtcNow());
            _logger.LogInformation("Authenticated browser session {SessionId} browser ready", id);
            return entry.Descriptor;
        }
        catch (Exception ex)
        {
            entry.Status = AuthenticatedBrowserSessionStatus.Failed;
            entry.FailureCategory = "browser_launch_failed";
            await RemoveAndDisposeAsync(entry, "launch_failed");
            _logger.LogError(ex, "Authenticated browser session {SessionId} launch failed", id);
            throw;
        }
    }

    public Task<AuthenticatedBrowserSessionDescriptor?> GetStatusAsync(string sessionId, string reviewSessionId, string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var entry) || !entry.Matches(reviewSessionId, profileId)) return Task.FromResult<AuthenticatedBrowserSessionDescriptor?>(null);
        if (IsExpired(entry)) _ = ExpireAsync(entry);
        return Task.FromResult<AuthenticatedBrowserSessionDescriptor?>(entry.Descriptor);
    }

    public async Task<bool> CancelAsync(string sessionId, string reviewSessionId, string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var entry)) return true;
        if (!entry.Matches(reviewSessionId, profileId)) return false;
        entry.Status = AuthenticatedBrowserSessionStatus.Cancelled;
        await RemoveAndDisposeAsync(entry, "cancelled");
        return true;
    }

    public Task<IAuthenticatedBrowserPageLease> AcquirePageLeaseAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var entry) || !entry.Matches(reviewSessionId, profileId)) throw new System.Collections.Generic.KeyNotFoundException("Authenticated browser session not found.");
        var origin = NormalizeTarget(targetUrl).GetLeftPart(UriPartial.Authority);
        if (!string.Equals(entry.TargetOrigin, origin, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Session target origin mismatch.");
        if (IsExpired(entry)) { _ = ExpireAsync(entry); throw new AuthenticatedSessionExpiredException(); }
        if (entry.Status is AuthenticatedBrowserSessionStatus.Cancelled or AuthenticatedBrowserSessionStatus.Failed or AuthenticatedBrowserSessionStatus.Disposed)
            throw new ObjectDisposedException(nameof(AuthenticatedBrowserSessionManager));
        if (entry.Resources is null) throw new InvalidOperationException("Browser is not ready.");
        entry.Touch(_time.GetUtcNow());
        return Task.FromResult<IAuthenticatedBrowserPageLease>(new PageLease(entry));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _expiryLoop = RunExpiryLoopAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAllAsync("application_shutdown");
    public async ValueTask DisposeAsync() { await DisposeAllAsync("manager_disposed"); _shutdown.Dispose(); }

    private async Task RunExpiryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), _time);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                foreach (var entry in _sessions.Values.Where(IsExpired)) await ExpireAsync(entry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private bool IsExpired(Entry entry) => _time.GetUtcNow() >= entry.ExpiresAt || _time.GetUtcNow() - entry.LastActivityAt >= _options.InactivityTimeout;
    private async Task ExpireAsync(Entry entry) { entry.Status = AuthenticatedBrowserSessionStatus.Expired; await RemoveAndDisposeAsync(entry, "expired"); }
    private async Task FailAndDisposeAsync(Entry entry, string category) { entry.Status = AuthenticatedBrowserSessionStatus.Failed; entry.FailureCategory = category; await RemoveAndDisposeAsync(entry, category); }

    private async Task RemoveAndDisposeAsync(Entry entry, string reason)
    {
        if (!_sessions.TryRemove(entry.SessionId, out _)) return;
        _reviewSessions.TryRemove(BindingKey(entry.ReviewSessionId, entry.ProfileId), out _);
        entry.Cancellation.Cancel();
        if (entry.Resources is not null) await entry.Resources.DisposeAsync();
        entry.Status = entry.Status == AuthenticatedBrowserSessionStatus.Cancelled ? AuthenticatedBrowserSessionStatus.Cancelled : entry.Status;
        _logger.LogInformation("Authenticated browser session {SessionId} cleaned up: {CleanupReason}", entry.SessionId, reason);
    }

    private async Task DisposeAllAsync(string reason)
    {
        if (!_shutdown.IsCancellationRequested) _shutdown.Cancel();
        foreach (var entry in _sessions.Values) { entry.Status = AuthenticatedBrowserSessionStatus.Disposed; await RemoveAndDisposeAsync(entry, reason); }
        if (_expiryLoop is not null) try { await _expiryLoop; } catch (OperationCanceledException) { }
    }

    private void EnsureSupported()
    {
        if (!_options.Enabled) throw new AuthenticatedReviewUnavailableException("Authenticated review is disabled.");
        if (!_options.IsLocalWorkstation) throw new AuthenticatedReviewUnavailableException("Authenticated review requires the LocalWorkstation runtime.");
    }

    private static void ValidateBinding(AuthenticatedBrowserSessionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ReviewSessionId) || string.IsNullOrWhiteSpace(request.ProfileId)) throw new ArgumentException("ReviewSessionId and ProfileId are required.");
    }

    private static Uri NormalizeTarget(string targetUrl)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("TargetUrl must be an absolute HTTP or HTTPS URL without user information.");
        return uri;
    }

    private static string BindingKey(string review, string profile) => $"{review.Length}:{review}{profile}";
    private static string CreateOpaqueId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private sealed class Entry
    {
        public Entry(string sessionId, string reviewSessionId, string profileId, string targetOrigin, DateTimeOffset startedAt, DateTimeOffset expiresAt)
        { SessionId = sessionId; ReviewSessionId = reviewSessionId; ProfileId = profileId; TargetOrigin = targetOrigin; StartedAt = startedAt; LastActivityAt = startedAt; ExpiresAt = expiresAt; }
        public string SessionId { get; }
        public string ReviewSessionId { get; }
        public string ProfileId { get; }
        public string TargetOrigin { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset LastActivityAt { get; private set; }
        public DateTimeOffset ExpiresAt { get; }
        public AuthenticatedBrowserSessionStatus Status { get; set; } = AuthenticatedBrowserSessionStatus.Starting;
        public string? FailureCategory { get; set; }
        public IAuthenticatedBrowserResources? Resources { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public AuthenticatedBrowserSessionDescriptor Descriptor => new(SessionId, ReviewSessionId, ProfileId, TargetOrigin, Status, StartedAt, ExpiresAt, FailureCategory);
        public bool Matches(string review, string profile) => string.Equals(ReviewSessionId, review, StringComparison.Ordinal) && string.Equals(ProfileId, profile, StringComparison.Ordinal);
        public void Touch(DateTimeOffset now) => LastActivityAt = now;
    }

    private sealed class PageLease(Entry entry) : IAuthenticatedBrowserPageLease
    {
        private int _disposed;
        public string SessionId => entry.SessionId;
        public Microsoft.Playwright.IPage Page => Volatile.Read(ref _disposed) == 0 && !entry.Cancellation.IsCancellationRequested ? entry.Resources!.Page : throw new ObjectDisposedException(nameof(PageLease));
        public Microsoft.Playwright.IBrowserContext Context => Volatile.Read(ref _disposed) == 0 && !entry.Cancellation.IsCancellationRequested ? entry.Resources!.Context : throw new ObjectDisposedException(nameof(PageLease));
        public CancellationToken SessionCancellation => entry.Cancellation.Token;
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }
    }
}

public sealed class AuthenticatedReviewUnavailableException(string message) : InvalidOperationException(message);
public sealed class AuthenticatedSessionConflictException(string message) : InvalidOperationException(message);
public sealed class AuthenticatedSessionExpiredException() : InvalidOperationException("Authenticated browser session expired.");
