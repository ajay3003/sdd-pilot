using System.Collections.Concurrent;
using BirkNext.Api.Services.TargetEnvironmentDetection;

namespace BirkNext.Api.Services.AuthenticatedReview;

/// <summary>
/// Thread-safe collector for auth events during a session.
/// Records only safe diagnostic data, never secrets.
/// </summary>
internal sealed class AuthEventTimelineCollector
{
    private readonly ConcurrentBag<AuthEvent> _events = new();
    private readonly string _attemptId;
    private readonly TimeProvider _timeProvider;

    public AuthEventTimelineCollector(string attemptId, TimeProvider timeProvider)
    {
        _attemptId = attemptId;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Record a diagnostic event. Safe to call concurrently.
    /// </summary>
    public void RecordEvent(
        AuthEventType eventType,
        string? safeOrigin = null,
        string? safePathCategory = null,
        AuthenticatedBrowserSessionStatus? sessionState = null,
        AuthenticationFailureReason? failureReason = null,
        string? note = null)
    {
        var evt = new AuthEvent(
            _timeProvider.GetUtcNow(),
            _attemptId,
            eventType,
            safeOrigin,
            safePathCategory,
            sessionState,
            failureReason,
            note
        );
        _events.Add(evt);
    }

    /// <summary>
    /// Get ordered timeline for diagnostics.
    /// </summary>
    public IReadOnlyList<AuthEvent> GetTimeline() =>
        _events.OrderBy(e => e.Timestamp).ToList();

    /// <summary>
    /// Generate compact acceptance summary from timeline and final state.
    /// </summary>
    public AuthAcceptanceSummary GetAcceptanceSummary(
        AuthenticatedBrowserSessionStatus finalState,
        AuthenticationFailureReason? failureReason)
    {
        var timeline = GetTimeline();

        return new AuthAcceptanceSummary(
            InitialShellLoaded: timeline.Any(e => e.EventType == AuthEventType.TargetShellLoaded),
            EntraReached: timeline.Any(e => e.EventType == AuthEventType.EntraReached),
            MfaEncountered: DetectMfaObservability(timeline),
            McasEncountered: timeline.Any(e => e.EventType == AuthEventType.McasReached),
            CallbackReached: timeline.Any(e => e.EventType == AuthEventType.CallbackReached),
            ReturnedToApplication: timeline.Any(e => e.EventType == AuthEventType.ApplicationReturned),
            AuthenticatedRecognized: timeline.Any(e => e.EventType == AuthEventType.AuthenticatedRecognized),
            FinalState: finalState,
            FailureReason: failureReason
        );
    }

    private static AuthMfaObservability DetectMfaObservability(IReadOnlyList<AuthEvent> timeline)
    {
        // Only report YES if we can reliably distinguish MFA from generic Entra waiting.
        // For now, report UNKNOWN unless there's explicit evidence.
        // This prevents false positives in automated acceptance.
        return AuthMfaObservability.Unknown;
    }
}
