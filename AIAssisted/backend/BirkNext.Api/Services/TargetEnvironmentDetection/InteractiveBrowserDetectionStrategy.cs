using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Interactive browser-based detection strategy for target environments.
/// Delegates to authenticated browser session manager to handle Entra ID, MCAS, and multi-step auth flows.
/// No auto-credential entry, no MFA automation, no MCAS auto-click.
/// Polls for terminal state and maps browser session status to detection results.
/// </summary>
internal sealed class InteractiveBrowserDetectionStrategy : ITargetDetectionAuthenticationStrategy
{
    private const int PollIntervalMs = 500;
    private const int DefaultTimeoutMinutes = 15;

    private readonly IAuthenticatedBrowserSessionManager _sessionManager;
    private readonly ILogger<InteractiveBrowserDetectionStrategy> _logger;

    public string StrategyName => "interactive-browser";

    public InteractiveBrowserDetectionStrategy(
        IAuthenticatedBrowserSessionManager sessionManager,
        ILogger<InteractiveBrowserDetectionStrategy> logger)
    {
        _sessionManager = sessionManager;
        _logger = logger;
    }

    public async Task<DetectionContinuationResult> ContinueDetectionAsync(
        string targetUrl,
        string reviewSessionId,
        string profileId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var actualTimeout = timeout ?? TimeSpan.FromMinutes(DefaultTimeoutMinutes);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri))
            {
                _logger.LogWarning("Invalid target URL: {TargetUrl}", targetUrl);
                return new DetectionContinuationResult
                {
                    AuthenticationSucceeded = false,
                    AuthenticationFailureReason = AuthenticationFailureReason.GenericFailure,
                    Duration = stopwatch.Elapsed
                };
            }

            // Start authenticated browser session
            _logger.LogInformation("Starting authenticated browser session for {TargetUrl}", targetUrl);
            var sessionRequest = new AuthenticatedBrowserSessionRequest(reviewSessionId, profileId, targetUrl);
            var sessionDescriptor = await _sessionManager.StartAsync(sessionRequest, cancellationToken);

            if (sessionDescriptor.Status != AuthenticatedBrowserSessionStatus.BrowserReady)
            {
                _logger.LogWarning("Browser session failed to start: {Status}", sessionDescriptor.Status);
                return new DetectionContinuationResult
                {
                    AuthenticationSucceeded = false,
                    AuthenticationFailureReason = AuthenticationFailureReason.BrowserResourceFailure,
                    Duration = stopwatch.Elapsed
                };
            }

            var sessionId = sessionDescriptor.SessionId;

            // Begin authentication (triggers navigation to target URL)
            try
            {
                var expectedAuthority = $"{targetUri.Scheme}://{targetUri.Host}";
                var authRequest = new BeginAuthenticationRequest(sessionId, reviewSessionId, profileId, expectedAuthority);
                await _sessionManager.BeginAuthenticationAsync(authRequest, cancellationToken);
            }
            catch (AuthenticatedSessionExpiredException)
            {
                _logger.LogWarning("Session expired during authentication initiation");
                return new DetectionContinuationResult
                {
                    SessionExpired = true,
                    Duration = stopwatch.Elapsed
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to begin authentication");
                return new DetectionContinuationResult
                {
                    AuthenticationSucceeded = false,
                    AuthenticationFailureReason = AuthenticationFailureReason.GenericFailure,
                    Duration = stopwatch.Elapsed
                };
            }

            // Poll for terminal state
            var result = await PollForTerminalStateAsync(sessionId, reviewSessionId, profileId, targetUri, actualTimeout, stopwatch, cancellationToken);

            // Clean up session
            try
            {
                await _sessionManager.CancelAsync(sessionId, reviewSessionId, profileId, CancellationToken.None);
            }
            catch
            {
                // Ignore cleanup errors
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Detection strategy cancelled");
            return new DetectionContinuationResult
            {
                UserCancelled = true,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in detection strategy");
            return new DetectionContinuationResult
            {
                AuthenticationSucceeded = false,
                AuthenticationFailureReason = AuthenticationFailureReason.GenericFailure,
                Duration = stopwatch.Elapsed
            };
        }
    }

    private async Task<DetectionContinuationResult> PollForTerminalStateAsync(
        string sessionId,
        string reviewSessionId,
        string profileId,
        Uri targetUri,
        TimeSpan timeout,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (!linkedCts.Token.IsCancellationRequested)
        {
            try
            {
                var descriptor = await _sessionManager.GetStatusAsync(sessionId, reviewSessionId, profileId, linkedCts.Token);

                if (descriptor == null)
                {
                    _logger.LogWarning("Session not found during polling");
                    return new DetectionContinuationResult
                    {
                        AuthenticationSucceeded = false,
                        AuthenticationFailureReason = AuthenticationFailureReason.GenericFailure,
                        Duration = stopwatch.Elapsed
                    };
                }

                // Check for terminal states
                var terminalResult = MapTerminalState(descriptor, stopwatch.Elapsed);
                if (terminalResult != null)
                {
                    _logger.LogInformation("Browser session reached terminal state: {Status}", descriptor.Status);
                    return terminalResult;
                }

                // Not terminal yet, wait before next poll
                await Task.Delay(PollIntervalMs, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                _logger.LogWarning("Authentication timeout after {Elapsed} ms", stopwatch.ElapsedMilliseconds);
                return new DetectionContinuationResult
                {
                    AuthenticationSucceeded = false,
                    AuthenticationFailureReason = AuthenticationFailureReason.NavigationTimeout,
                    Duration = stopwatch.Elapsed
                };
            }
        }

        _logger.LogWarning("Polling cancelled");
        return new DetectionContinuationResult
        {
            UserCancelled = true,
            Duration = stopwatch.Elapsed
        };
    }

    private DetectionContinuationResult? MapTerminalState(AuthenticatedBrowserSessionDescriptor descriptor, TimeSpan elapsed)
    {
        return descriptor.Status switch
        {
            // Success states
            AuthenticatedBrowserSessionStatus.Authenticated =>
                new DetectionContinuationResult
                {
                    AuthenticationSucceeded = true,
                    IsFullCompletion = true,
                    ResultingState = TargetDetectionState.Complete,
                    DeliveryContext = descriptor.DeliveryContext,
                    Duration = elapsed
                },

            // Awaiting user continuation (MCAS interstitial) - not a failure, but partial
            AuthenticatedBrowserSessionStatus.AwaitingUserContinuation =>
                new DetectionContinuationResult
                {
                    AuthenticationSucceeded = true, // Partial success
                    AwaitingUserContinuation = true,
                    IsFullCompletion = false,
                    ResultingState = TargetDetectionState.Partial,
                    DeliveryContext = descriptor.DeliveryContext,
                    Duration = elapsed
                },

            // Intermediate states without explicit continuation required
            AuthenticatedBrowserSessionStatus.ConditionalAccessIntermediary =>
                new DetectionContinuationResult
                {
                    AwaitingUserContinuation = true,
                    IsFullCompletion = false,
                    ResultingState = TargetDetectionState.Partial,
                    DeliveryContext = descriptor.DeliveryContext,
                    Duration = elapsed
                },

            // Cancellation
            AuthenticatedBrowserSessionStatus.Cancelled or AuthenticatedBrowserSessionStatus.AuthenticationCancelled =>
                new DetectionContinuationResult
                {
                    UserCancelled = true,
                    Duration = elapsed
                },

            // Expiration
            AuthenticatedBrowserSessionStatus.Expired or AuthenticatedBrowserSessionStatus.AuthenticationExpired =>
                new DetectionContinuationResult
                {
                    SessionExpired = true,
                    Duration = elapsed
                },

            // Failure states
            AuthenticatedBrowserSessionStatus.AuthenticationFailed or AuthenticatedBrowserSessionStatus.Failed =>
                new DetectionContinuationResult
                {
                    AuthenticationSucceeded = false,
                    AuthenticationFailureReason = MapFailureCategory(descriptor.FailureCategory),
                    Duration = elapsed
                },

            // Unexpected origin
            AuthenticatedBrowserSessionStatus.UnexpectedOrigin =>
                new DetectionContinuationResult
                {
                    UnexpectedOriginEncountered = true,
                    AuthenticationSucceeded = false,
                    AuthenticationFailureReason = AuthenticationFailureReason.GenericFailure,
                    Duration = elapsed
                },

            // Non-terminal states return null (continue polling)
            _ => null
        };
    }

    private AuthenticationFailureReason MapFailureCategory(string? failureCategory)
    {
        if (string.IsNullOrEmpty(failureCategory))
            return AuthenticationFailureReason.GenericFailure;

        return failureCategory.ToLowerInvariant() switch
        {
            "invalid_credentials" => AuthenticationFailureReason.InvalidCredentials,
            "mfa_required" => AuthenticationFailureReason.MfaRequired,
            "conditional_access_denied" => AuthenticationFailureReason.ConditionalAccessDenied,
            "account_disabled" => AuthenticationFailureReason.AccountDisabled,
            "navigation_timeout" => AuthenticationFailureReason.NavigationTimeout,
            "browser_resource_failure" or "browser_disconnected" or "page_closed" or "page_crashed" =>
                AuthenticationFailureReason.BrowserResourceFailure,
            _ => AuthenticationFailureReason.GenericFailure
        };
    }
}
