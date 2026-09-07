using BirkNext.Api.Services.TargetEnvironmentDetection;

namespace BirkNext.Api.Services.AuthenticatedReview;

/// <summary>
/// Safe auth-event type enumeration for diagnostic timeline.
/// Contains only security-classified event types, never secret-bearing data.
/// </summary>
public enum AuthEventType
{
    BrowserLaunched,
    TargetNavigationStarted,
    TargetShellLoaded,
    EntraReached,
    AuthenticationWaiting,
    McasReached,
    AwaitingUserContinuation,
    CallbackReached,
    ApplicationReturned,
    AuthenticatedRecognized,
    AuthenticationFailed,
    AuthenticationExpired,
    UnexpectedOrigin,
    Cancelled,
    BrowserClosed,
    BrowserResourceFailure
}

/// <summary>
/// Single safe auth event for timeline. Contains only diagnostic fields.
/// Never includes: auth codes, tokens, cookies, passwords, query strings, etc.
/// </summary>
public sealed record AuthEvent(
    DateTimeOffset Timestamp,
    string AttemptId,
    AuthEventType EventType,
    string? SafeOrigin = null,
    string? SafePathCategory = null,
    AuthenticatedBrowserSessionStatus? SessionState = null,
    AuthenticationFailureReason? FailureReason = null,
    string? Note = null
);

/// <summary>
/// Compact acceptance summary for end-of-flow reporting.
/// Used for diagnostics and test assertions, not user-facing UI.
/// </summary>
public sealed record AuthAcceptanceSummary(
    bool InitialShellLoaded,
    bool EntraReached,
    AuthMfaObservability MfaEncountered,
    bool McasEncountered,
    bool CallbackReached,
    bool ReturnedToApplication,
    bool AuthenticatedRecognized,
    AuthenticatedBrowserSessionStatus FinalState,
    AuthenticationFailureReason? FailureReason
);

/// <summary>
/// MFA observability classification: only report what runtime can reliably detect.
/// </summary>
public enum AuthMfaObservability
{
    Unknown = 0,
    No = 1,
    Yes = 2
}
