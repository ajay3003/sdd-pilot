namespace BirkNext.Api.Services.AuthenticatedReview;

/// <summary>
/// Safe enumeration of diagnostic markers emitted during authenticated browser initialization.
/// Used for parsing runtime logs only. Does not affect production authentication semantics.
/// </summary>
internal enum AuthDiagnosticMarker
{
    Unknown = 0,
    Diag1StartAsyncEntered,
    Diag2LaunchAsyncReturned,
    Diag3BeginAuthenticationAsyncEntered,
    Diag4EnsureSupportedPassed,
    Diag5ObserverAttachStarting,
    Diag6ObserverAttached,
    Diag7AboutToCallGotoAsync,
    Diag8GotoAsyncCompleted,
    Diag9ReturnedFromBeginAuthenticationAsync,
    DiagException
}

/// <summary>
/// Safe classification of where authenticated browser initialization sequence stopped.
/// Used for diagnostic interpretation only. Does not affect production flow.
/// </summary>
internal enum AuthDiagnosticClassification
{
    NoEvidence,
    LaunchReturnedButBeginAuthNotObserved,
    BeginAuthEnteredBeforeSupportPassed,
    ObserverAttachNotCompleted,
    GotoNotReached,
    GotoInProgressOrNoTerminalEvidence,
    GotoFailed,
    GotoCompletedPostNavigationIssue,
    BeginAuthCompleted,
    IncompleteOrOutOfOrder,
    Unknown
}

/// <summary>
/// Confidence level of diagnostic classification based on evidence and logging state.
/// </summary>
internal enum AuthDiagnosticConfidence
{
    Low,
    Medium,
    High
}

/// <summary>
/// Single diagnostic log entry parsed from runtime logs.
/// Retains only opaque session ID and marker type.
/// Intentionally excludes URLs, tokens, credentials, query strings.
/// </summary>
internal sealed record AuthDiagnosticLogEntry(
    string SessionId,
    AuthDiagnosticMarker Marker,
    string? Stage = null,
    string? ExceptionType = null,
    int SequenceNumber = 0)
{
    public bool IsException => Marker == AuthDiagnosticMarker.DiagException;
}

/// <summary>
/// Diagnostic summary for one authenticated browser session.
/// Safe for logging and reporting without exposing secrets.
/// </summary>
internal sealed record AuthDiagnosticSessionSummary(
    string SessionId,
    AuthDiagnosticClassification Classification,
    AuthDiagnosticMarker? HighestMarker,
    AuthDiagnosticConfidence Confidence,
    bool InformationLoggingEnabled,
    string? ExceptionType = null,
    string? ExceptionStage = null,
    bool HasDuplicateMarkers = false,
    bool IsOutOfOrder = false)
{
    public string SafeSummary => $"Classification={Classification} Confidence={Confidence} HighestMarker={HighestMarker}";
}
