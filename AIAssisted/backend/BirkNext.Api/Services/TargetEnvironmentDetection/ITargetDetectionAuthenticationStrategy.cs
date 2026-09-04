namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Strategy for continuing target environment detection through authentication.
/// Implementations handle different authentication mechanisms (browser-based, API-based, etc).
/// </summary>
public interface ITargetDetectionAuthenticationStrategy
{
    /// <summary>
    /// Execute the detection strategy for the given target URL.
    /// Should not store credentials, auto-enter passwords, or persist session cookies to profile.
    /// </summary>
    /// <param name="targetUrl">The target application URL to authenticate against</param>
    /// <param name="reviewSessionId">Review session ID for tracking and authorization</param>
    /// <param name="profileId">Profile ID associated with this detection</param>
    /// <param name="timeout">Maximum time to wait for authentication completion (default 15 minutes)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detection continuation result with terminal state and reason</returns>
    Task<DetectionContinuationResult> ContinueDetectionAsync(
        string targetUrl,
        string reviewSessionId,
        string profileId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a human-readable name for this strategy.
    /// </summary>
    string StrategyName { get; }
}
