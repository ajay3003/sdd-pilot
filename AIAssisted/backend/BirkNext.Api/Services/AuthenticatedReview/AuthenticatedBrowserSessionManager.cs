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
    private readonly AuthenticationOriginPolicy _originPolicy;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _expiryLoop;

    public AuthenticatedBrowserSessionManager(
        IAuthenticatedBrowserHost browserHost,
        IOptions<AuthenticatedReviewOptions> options,
        TimeProvider time,
        ILogger<AuthenticatedBrowserSessionManager> logger,
        AuthenticationOriginPolicy? originPolicy = null)
    {
        _browserHost = browserHost;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _originPolicy = originPolicy ?? new AuthenticationOriginPolicy(options);
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
        var entry = new Entry(id, request.ReviewSessionId, request.ProfileId, target, now, now + _options.AbsoluteLifetime);
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
            entry.Resources.Page.Close += (_, _) => _ = FailAndDisposeAsync(entry, "page_closed");
            entry.Resources.Page.Crash += (_, _) => _ = FailAndDisposeAsync(entry, "page_crashed");
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

    public async Task<AuthenticatedBrowserSessionDescriptor> BeginAuthenticationAsync(BeginAuthenticationRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        var entry = GetOwnedEntry(request.SessionId, request.ReviewSessionId, request.ProfileId);
        if (IsExpired(entry)) { await ExpireAsync(entry); throw new AuthenticatedSessionExpiredException(); }
        if (!Uri.TryCreate(request.ExpectedAuthority, UriKind.Absolute, out var authority) || !_originPolicy.IsValidEntraAuthority(authority))
            throw new ArgumentException("ExpectedAuthority is not an approved Entra authority.");

        Uri? syntheticMcas = null;
        if (!string.IsNullOrWhiteSpace(request.SyntheticMcasOrigin))
        {
            if (!_options.AllowSyntheticHttpOrigins || !Uri.TryCreate(request.SyntheticMcasOrigin, UriKind.Absolute, out syntheticMcas) || !syntheticMcas.IsLoopback)
                throw new ArgumentException("SyntheticMcasOrigin is allowed only for configured loopback fixtures.");
        }

        entry.ExpectedAuthority = authority;
        entry.SyntheticMcasOrigin = syntheticMcas;
        entry.Status = AuthenticatedBrowserSessionStatus.AuthenticationRequired;
        entry.ApplicationValidationCurrent = false;
        AttachNavigationObserver(entry);
        entry.Status = AuthenticatedBrowserSessionStatus.AuthenticationInProgress;
        entry.Touch(_time.GetUtcNow());

        try
        {
            await entry.Resources!.Page.GotoAsync(entry.TargetUrl.AbsoluteUri, new Microsoft.Playwright.PageGotoOptions
            {
                WaitUntil = Microsoft.Playwright.WaitUntilState.DOMContentLoaded,
                Timeout = 30_000
            });
            await ObserveNavigationAsync(entry, entry.Resources.Page.Url);
            return entry.Descriptor;
        }
        catch (Microsoft.Playwright.PlaywrightException ex) when (!entry.Cancellation.IsCancellationRequested)
        {
            entry.Status = AuthenticatedBrowserSessionStatus.AuthenticationFailed;
            entry.FailureCategory = "authentication_navigation_failed";
            _logger.LogWarning("Authenticated browser session {SessionId} navigation failed: {FailureCategory}", entry.SessionId, entry.FailureCategory);
            throw new AuthenticatedNavigationException("Authentication navigation failed.", ex);
        }
    }

    public Task<IAuthenticatedBrowserPageLease> AcquireAuthenticationPageLeaseAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, CancellationToken cancellationToken = default) =>
        AcquireLeaseCoreAsync(sessionId, reviewSessionId, profileId, targetUrl, requireAuthenticated: false, cancellationToken);

    public Task<IAuthenticatedBrowserPageLease> AcquirePageLeaseAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, CancellationToken cancellationToken = default)
        => AcquireLeaseCoreAsync(sessionId, reviewSessionId, profileId, targetUrl, requireAuthenticated: true, cancellationToken);

    private async Task<IAuthenticatedBrowserPageLease> AcquireLeaseCoreAsync(string sessionId, string reviewSessionId, string profileId, string targetUrl, bool requireAuthenticated, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.TryGetValue(sessionId, out var entry) || !entry.Matches(reviewSessionId, profileId)) throw new System.Collections.Generic.KeyNotFoundException("Authenticated browser session not found.");
        var origin = NormalizeTarget(targetUrl).GetLeftPart(UriPartial.Authority);
        if (!string.Equals(entry.TargetOrigin, origin, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Session target origin mismatch.");
        if (IsExpired(entry)) { _ = ExpireAsync(entry); throw new AuthenticatedSessionExpiredException(); }
        if (entry.Status is AuthenticatedBrowserSessionStatus.Cancelled or AuthenticatedBrowserSessionStatus.Failed or AuthenticatedBrowserSessionStatus.Disposed)
            throw new ObjectDisposedException(nameof(AuthenticatedBrowserSessionManager));
        if (requireAuthenticated && (entry.Status != AuthenticatedBrowserSessionStatus.Authenticated || !entry.ApplicationValidationCurrent))
            throw new AuthenticatedSessionNotEligibleException();
        if (entry.Resources is null) throw new InvalidOperationException("Browser is not ready.");

        await ValidateResourceLivenessAsync(entry);

        entry.Touch(_time.GetUtcNow());
        return new PageLease(entry, requireAuthenticated);
    }

    private Task ValidateResourceLivenessAsync(Entry entry)
    {
        var failure = entry.Resources switch
        {
            null => ("resources_null", "Browser resources are null"),
            _ when entry.Resources.Page.IsClosed => ("page_closed", "Manager-owned page is closed"),
            _ when !entry.Resources.Browser.IsConnected => ("browser_disconnected", "Browser is disconnected"),
            _ => (null, null)
        };

        if (failure.Item1 is not null)
        {
            entry.RevokeEngineEligibility();
            entry.Status = AuthenticatedBrowserSessionStatus.Failed;
            entry.FailureCategory = failure.Item1;
            entry.ApplicationValidationCurrent = false;
            _ = CleanupFailedResourcesAsync(entry, failure.Item1);
            throw new AuthenticatedResourceUnavailableException($"Resource liveness check failed: {failure.Item2}");
        }

        return Task.CompletedTask;
    }

    private async Task CleanupFailedResourcesAsync(Entry entry, string reason)
    {
        if (entry.Resources is not null) await entry.Resources.DisposeAsync();
        _logger.LogInformation("Authenticated browser session {SessionId} resource failure cleaned up: {Reason}", entry.SessionId, reason);
    }

    private void AttachNavigationObserver(Entry entry)
    {
        if (entry.ObserverAttached) return;
        entry.ObserverAttached = true;
        entry.Resources!.Page.FrameNavigated += (_, frame) =>
        {
            if (ReferenceEquals(frame, entry.Resources.Page.MainFrame)) _ = ObserveNavigationAsync(entry, frame.Url);
        };
    }

    private async Task ObserveNavigationAsync(Entry entry, string rawUrl)
    {
        await entry.NavigationGate.WaitAsync();
        try
        {
            if (entry.Cancellation.IsCancellationRequested || !_sessions.ContainsKey(entry.SessionId)) return;
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var candidate)) { await MarkUnexpectedAsync(entry); return; }
            var classification = _originPolicy.Classify(candidate, entry.TargetUrl, entry.ExpectedAuthority!, true, entry.EntraObserved, entry.SyntheticMcasOrigin);

            switch (classification)
            {
                case AuthenticationOriginClass.Application:
                    if (!entry.EntraObserved) { entry.RevokeEngineEligibility(); entry.Status = AuthenticatedBrowserSessionStatus.AuthenticationRequired; entry.ApplicationValidationCurrent = false; break; }
                    if (await ValidateApplicationPageAsync(entry, candidate, proxied: false))
                    {
                        entry.Status = AuthenticatedBrowserSessionStatus.Authenticated;
                        entry.DeliveryContext = entry.McasObserved ? AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession : AuthenticatedDeliveryContext.DirectApplication;
                        entry.ApplicationValidationCurrent = true;
                        entry.GrantEngineEligibility();
                    }
                    break;

                case AuthenticationOriginClass.EntraAuthority:
                    entry.RevokeEngineEligibility();
                    entry.ApplicationValidationCurrent = false;
                    if (entry.Status == AuthenticatedBrowserSessionStatus.Authenticated) entry.Status = AuthenticatedBrowserSessionStatus.AuthenticationExpired;
                    else { entry.EntraObserved = true; entry.Status = AuthenticatedBrowserSessionStatus.AuthenticationInProgress; }
                    break;

                case AuthenticationOriginClass.McasIntermediary:
                    entry.RevokeEngineEligibility();
                    entry.ApplicationValidationCurrent = false;
                    var origin = candidate.GetLeftPart(UriPartial.Authority);
                    if (entry.PinnedMcasOrigin is not null && !string.Equals(entry.PinnedMcasOrigin, origin, StringComparison.OrdinalIgnoreCase)) { await MarkUnexpectedAsync(entry); break; }
                    entry.PinnedMcasOrigin ??= origin;
                    if (entry.Status == AuthenticatedBrowserSessionStatus.Authenticated) { entry.Status = AuthenticatedBrowserSessionStatus.AuthenticationExpired; break; }
                    entry.McasObserved = true;
                    entry.DeliveryContext = AuthenticatedDeliveryContext.ConditionalAccessMonitoredSession;
                    if (await IsUserContinuationInterstitialAsync(entry.Resources!.Page)) entry.Status = AuthenticatedBrowserSessionStatus.AwaitingUserContinuation;
                    else if (await ValidateApplicationPageAsync(entry, candidate, proxied: true))
                    {
                        entry.Status = AuthenticatedBrowserSessionStatus.Authenticated;
                        entry.DeliveryContext = AuthenticatedDeliveryContext.ProxiedApplicationDelivery;
                        entry.ApplicationValidationCurrent = true;
                        entry.PinnedProxiedApplicationOrigin = origin;
                        entry.GrantEngineEligibility();
                    }
                    else entry.Status = AuthenticatedBrowserSessionStatus.ConditionalAccessIntermediary;
                    break;

                default:
                    await MarkUnexpectedAsync(entry);
                    break;
            }

            _logger.LogInformation("Authenticated browser session {SessionId} state {SessionState} delivery {DeliveryContext}", entry.SessionId, entry.Status, entry.DeliveryContext);
        }
        catch (Microsoft.Playwright.PlaywrightException) when (entry.Cancellation.IsCancellationRequested) { }
        finally { entry.NavigationGate.Release(); }
    }

    private static async Task<bool> IsUserContinuationInterstitialAsync(Microsoft.Playwright.IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                  if (document.documentElement?.dataset?.birknextAuthFixture === 'mcas-notice') return true;
                  const controls = [...document.querySelectorAll('form button, form input[type=submit], a[href]')];
                  return controls.some(c => {
                    const form = c.closest('form');
                    const destination = c.getAttribute('href') || form?.getAttribute('action') || '';
                    return /continue|resume|proceed/i.test(destination) && !document.querySelector('input[type=password]');
                  });
                }
                """);
        }
        catch { return false; }
    }

    private async Task<bool> ValidateApplicationPageAsync(Entry entry, Uri candidate, bool proxied)
    {
        if (!entry.EntraObserved) return false;
        if (!proxied && !AuthenticationOriginPolicy.SameOrigin(candidate, entry.TargetUrl)) return false;
        if (proxied && (!entry.McasObserved || entry.PinnedMcasOrigin is null || !string.Equals(entry.PinnedMcasOrigin, candidate.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))) return false;
        if (await IsUserContinuationInterstitialAsync(entry.Resources!.Page)) return false;
        try
        {
            return await entry.Resources.Page.EvaluateAsync<bool>("""
                () => ['interactive','complete'].includes(document.readyState) &&
                      !!document.body && document.body.childElementCount > 0 &&
                      !document.querySelector('input[type=password]') &&
                      document.documentElement?.dataset?.birknextAuthFixture !== 'login'
                """);
        }
        catch { return false; }
    }

    private async Task MarkUnexpectedAsync(Entry entry)
    {
        entry.RevokeEngineEligibility();
        entry.Status = AuthenticatedBrowserSessionStatus.UnexpectedOrigin;
        entry.ApplicationValidationCurrent = false;
        entry.FailureCategory = "unexpected_origin";
        try { await entry.Resources!.Page.EvaluateAsync("() => window.stop()"); } catch { }
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

    private Entry GetOwnedEntry(string sessionId, string reviewSessionId, string profileId)
    {
        if (!_sessions.TryGetValue(sessionId, out var entry) || !entry.Matches(reviewSessionId, profileId))
            throw new System.Collections.Generic.KeyNotFoundException("Authenticated browser session not found.");
        if (entry.Resources is null) throw new InvalidOperationException("Browser is not ready.");
        return entry;
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
        public Entry(string sessionId, string reviewSessionId, string profileId, Uri targetUrl, DateTimeOffset startedAt, DateTimeOffset expiresAt)
        { SessionId = sessionId; ReviewSessionId = reviewSessionId; ProfileId = profileId; TargetUrl = targetUrl; TargetOrigin = targetUrl.GetLeftPart(UriPartial.Authority); StartedAt = startedAt; LastActivityAt = startedAt; ExpiresAt = expiresAt; }
        public string SessionId { get; }
        public string ReviewSessionId { get; }
        public string ProfileId { get; }
        public string TargetOrigin { get; }
        public Uri TargetUrl { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset LastActivityAt { get; private set; }
        public DateTimeOffset ExpiresAt { get; }
        public AuthenticatedBrowserSessionStatus Status { get; set; } = AuthenticatedBrowserSessionStatus.Starting;
        public string? FailureCategory { get; set; }
        public AuthenticatedDeliveryContext DeliveryContext { get; set; }
        public bool ApplicationValidationCurrent { get; set; }
        public Uri? ExpectedAuthority { get; set; }
        public Uri? SyntheticMcasOrigin { get; set; }
        public string? PinnedMcasOrigin { get; set; }
        public string? PinnedProxiedApplicationOrigin { get; set; }
        public bool EntraObserved { get; set; }
        public bool McasObserved { get; set; }
        public bool ObserverAttached { get; set; }
        public SemaphoreSlim NavigationGate { get; } = new(1, 1);
        public IAuthenticatedBrowserResources? Resources { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationTokenSource EngineEligibility { get; private set; } = new();
        public AuthenticatedBrowserSessionDescriptor Descriptor => new(SessionId, ReviewSessionId, ProfileId, TargetOrigin, Status, StartedAt, ExpiresAt, FailureCategory, DeliveryContext, ApplicationValidationCurrent);
        public bool Matches(string review, string profile) => string.Equals(ReviewSessionId, review, StringComparison.Ordinal) && string.Equals(ProfileId, profile, StringComparison.Ordinal);
        public void Touch(DateTimeOffset now) => LastActivityAt = now;
        public void GrantEngineEligibility()
        {
            if (!EngineEligibility.IsCancellationRequested) return;
            EngineEligibility.Dispose();
            EngineEligibility = new CancellationTokenSource();
        }
        public void RevokeEngineEligibility()
        {
            if (!EngineEligibility.IsCancellationRequested) EngineEligibility.Cancel();
        }
    }

    private sealed class PageLease : IAuthenticatedBrowserPageLease
    {
        private int _disposed;
        private readonly Entry _entry;
        private readonly CancellationTokenSource _leaseCancellation;
        public PageLease(Entry entry, bool observeEligibility)
        {
            _entry = entry;
            _leaseCancellation = observeEligibility
                ? CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token, entry.EngineEligibility.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(entry.Cancellation.Token);
        }
        public string SessionId => _entry.SessionId;
        public Microsoft.Playwright.IPage Page => Volatile.Read(ref _disposed) == 0 && !_entry.Cancellation.IsCancellationRequested ? _entry.Resources!.Page : throw new ObjectDisposedException(nameof(PageLease));
        public Microsoft.Playwright.IBrowserContext Context => Volatile.Read(ref _disposed) == 0 && !_entry.Cancellation.IsCancellationRequested ? _entry.Resources!.Context : throw new ObjectDisposedException(nameof(PageLease));
        public CancellationToken SessionCancellation => _leaseCancellation.Token;
        public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _leaseCancellation.Dispose(); return ValueTask.CompletedTask; }
    }
}

public sealed class AuthenticatedReviewUnavailableException(string message) : InvalidOperationException(message);
public sealed class AuthenticatedSessionConflictException(string message) : InvalidOperationException(message);
public sealed class AuthenticatedSessionExpiredException() : InvalidOperationException("Authenticated browser session expired.");
public sealed class AuthenticatedSessionNotEligibleException() : InvalidOperationException("Authenticated browser session is not eligible for review-engine use.");
public sealed class AuthenticatedNavigationException(string message, Exception inner) : InvalidOperationException(message, inner);
public sealed class AuthenticatedResourceUnavailableException(string message) : InvalidOperationException(message);
