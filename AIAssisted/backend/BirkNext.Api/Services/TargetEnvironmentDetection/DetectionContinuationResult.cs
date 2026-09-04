using BirkNext.Api.Models;
using BirkNext.Api.Services.AuthenticatedReview;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Result from a target detection continuation (e.g., browser-based authentication).
/// Maps browser session terminal states to detection outcomes.
/// </summary>
public sealed class DetectionContinuationResult
{
    /// <summary>
    /// Whether authentication succeeded and the target is now reachable.
    /// </summary>
    public bool AuthenticationSucceeded { get; init; }

    /// <summary>
    /// User explicitly cancelled the authentication flow.
    /// </summary>
    public bool UserCancelled { get; init; }

    /// <summary>
    /// Session expired before authentication completed.
    /// </summary>
    public bool SessionExpired { get; init; }

    /// <summary>
    /// Authentication failed with a specific reason.
    /// Null if authentication succeeded or was cancelled.
    /// </summary>
    public AuthenticationFailureReason? AuthenticationFailureReason { get; init; }

    /// <summary>
    /// Session reached an unexpected origin or failed validation.
    /// Indicates potential attack or misconfiguration.
    /// </summary>
    public bool UnexpectedOriginEncountered { get; init; }

    /// <summary>
    /// Session is awaiting user to continue (e.g., MCAS interstitial).
    /// Not a failure - indicates intermediate step requiring user action.
    /// </summary>
    public bool AwaitingUserContinuation { get; init; }

    /// <summary>
    /// Duration the authentication attempt took.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Delivery context if authentication succeeded (Direct, ProxiedVia MCAS, etc).
    /// </summary>
    public AuthenticatedDeliveryContext DeliveryContext { get; init; } = AuthenticatedDeliveryContext.None;

    /// <summary>
    /// Final detection state to assign.
    /// Computed from terminal browser session status.
    /// </summary>
    public TargetDetectionState ResultingState { get; init; } = TargetDetectionState.Failed;

    /// <summary>
    /// Whether the result should be considered as Full or Partial completion.
    /// </summary>
    public bool IsFullCompletion { get; init; } = true;
}

/// <summary>
/// Reasons for authentication failure during browser-based detection.
/// </summary>
public enum AuthenticationFailureReason
{
    /// <summary>
    /// Authentication was not attempted.
    /// </summary>
    None = 0,

    /// <summary>
    /// Invalid credentials or authentication denied.
    /// </summary>
    InvalidCredentials = 1,

    /// <summary>
    /// MFA (Multi-Factor Authentication) required but not completed.
    /// </summary>
    MfaRequired = 2,

    /// <summary>
    /// Conditional access or security policy denied access.
    /// </summary>
    ConditionalAccessDenied = 3,

    /// <summary>
    /// Account locked or disabled.
    /// </summary>
    AccountDisabled = 4,

    /// <summary>
    /// Browser navigation timeout.
    /// </summary>
    NavigationTimeout = 5,

    /// <summary>
    /// Generic authentication failure.
    /// </summary>
    GenericFailure = 6,

    /// <summary>
    /// Browser session became unusable (disconnected, page crashed).
    /// </summary>
    BrowserResourceFailure = 7
}
