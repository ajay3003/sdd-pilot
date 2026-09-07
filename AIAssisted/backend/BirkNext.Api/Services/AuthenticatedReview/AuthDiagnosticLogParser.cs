using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.AuthenticatedReview;

/// <summary>
/// Safe parser for [DIAG-*] runtime log markers emitted during authenticated browser initialization.
/// Extracts only opaque session IDs and marker types.
/// Intentionally excludes URLs, tokens, credentials, auth codes.
/// Does not affect production authentication behavior.
/// </summary>
internal sealed class AuthDiagnosticLogParser
{
    private static readonly Regex MarkerPattern = new(
        @"\[DIAG-(?<marker>\d+|EXCEPTION)\]\s+(?<marker_name>[^\s]+)\s+.*?sessionId=(?<sessionId>[A-Fa-f0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExceptionPattern = new(
        @"\[DIAG-EXCEPTION\].*?stage=(?<stage>\S+).*?sessionId=(?<sessionId>[A-Fa-f0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<AuthDiagnosticLogEntry> ParseLogLines(IEnumerable<string> logLines)
    {
        var entries = new List<AuthDiagnosticLogEntry>();
        var sequence = 0;

        foreach (var line in logLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var entry = ParseSingleLine(line, sequence++);
            if (entry != null)
                entries.Add(entry);
        }

        return entries;
    }

    private AuthDiagnosticLogEntry? ParseSingleLine(string line, int sequence)
    {
        // Try exception pattern first
        var exMatch = ExceptionPattern.Match(line);
        if (exMatch.Success)
        {
            return new AuthDiagnosticLogEntry(
                SessionId: exMatch.Groups["sessionId"].Value,
                Marker: AuthDiagnosticMarker.DiagException,
                Stage: exMatch.Groups["stage"].Value,
                ExceptionType: ExtractExceptionType(line),
                SequenceNumber: sequence);
        }

        // Try standard marker pattern
        var match = MarkerPattern.Match(line);
        if (!match.Success)
            return null;

        var markerNum = match.Groups["marker"].Value;
        var sessionId = match.Groups["sessionId"].Value;

        // Validate session ID is opaque (hex only)
        if (!IsOpaqueSessionId(sessionId))
            return null;

        var marker = markerNum switch
        {
            "1" => AuthDiagnosticMarker.Diag1StartAsyncEntered,
            "2" => AuthDiagnosticMarker.Diag2LaunchAsyncReturned,
            "3" => AuthDiagnosticMarker.Diag3BeginAuthenticationAsyncEntered,
            "4" => AuthDiagnosticMarker.Diag4EnsureSupportedPassed,
            "5" => AuthDiagnosticMarker.Diag5ObserverAttachStarting,
            "6" => AuthDiagnosticMarker.Diag6ObserverAttached,
            "7" => AuthDiagnosticMarker.Diag7AboutToCallGotoAsync,
            "8" => AuthDiagnosticMarker.Diag8GotoAsyncCompleted,
            "9" => AuthDiagnosticMarker.Diag9ReturnedFromBeginAuthenticationAsync,
            _ => AuthDiagnosticMarker.Unknown
        };

        if (marker == AuthDiagnosticMarker.Unknown)
            return null;

        return new AuthDiagnosticLogEntry(
            SessionId: sessionId,
            Marker: marker,
            SequenceNumber: sequence);
    }

    private bool IsOpaqueSessionId(string sessionId)
    {
        // Accept only hex characters, typical opaque ID format
        return !string.IsNullOrWhiteSpace(sessionId) &&
               sessionId.All(c => char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
    }

    private string ExtractExceptionType(string line)
    {
        // Extract only the exception type name, e.g., "PlaywrightException"
        // Never extract URLs, query params, tokens, etc.
        var match = Regex.Match(line, @"(?:stage|exception)Type\s*=\s*(?<type>\w+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["type"].Value : "Unknown";
    }

    public AuthDiagnosticSessionSummary ClassifySession(
        IReadOnlyList<AuthDiagnosticLogEntry> sessionEntries,
        bool informationLoggingEnabled = true)
    {
        if (sessionEntries.Count == 0)
            return new AuthDiagnosticSessionSummary(
                SessionId: "unknown",
                Classification: AuthDiagnosticClassification.NoEvidence,
                HighestMarker: null,
                Confidence: AuthDiagnosticConfidence.Low,
                InformationLoggingEnabled: informationLoggingEnabled);

        var sessionId = sessionEntries[0].SessionId;

        // Check for duplicates
        var markerCounts = sessionEntries
            .Where(e => !e.IsException)
            .GroupBy(e => e.Marker)
            .ToDictionary(g => g.Key, g => g.Count());
        var hasDuplicates = markerCounts.Values.Any(c => c > 1);

        // Get non-exception entries in original order (for out-of-order detection)
        var nonExceptionEntries = sessionEntries
            .Where(e => !e.IsException)
            .ToList();

        var isOutOfOrder = DetectOutOfOrder(nonExceptionEntries);

        // Find highest marker
        var sortedEntries = nonExceptionEntries
            .OrderBy(e => (int)e.Marker)
            .ToList();

        var highestMarker = sortedEntries.LastOrDefault()?.Marker;

        // Check for exception
        var exceptionEntry = sessionEntries.FirstOrDefault(e => e.IsException);

        // Classify
        var (classification, confidence) = ClassifyByMarkerSequence(
            highestMarker,
            exceptionEntry,
            sortedEntries,
            informationLoggingEnabled);

        return new AuthDiagnosticSessionSummary(
            SessionId: sessionId,
            Classification: classification,
            HighestMarker: highestMarker,
            Confidence: confidence,
            InformationLoggingEnabled: informationLoggingEnabled,
            ExceptionType: exceptionEntry?.ExceptionType,
            ExceptionStage: exceptionEntry?.Stage,
            HasDuplicateMarkers: hasDuplicates,
            IsOutOfOrder: isOutOfOrder);
    }

    private (AuthDiagnosticClassification, AuthDiagnosticConfidence) ClassifyByMarkerSequence(
        AuthDiagnosticMarker? highestMarker,
        AuthDiagnosticLogEntry? exceptionEntry,
        List<AuthDiagnosticLogEntry> sequencedEntries,
        bool loggingEnabled)
    {
        // Detect impossible case: both DIAG-8 (completed) and Goto-exception
        // This indicates either logging error or out-of-order evidence
        if (highestMarker == AuthDiagnosticMarker.Diag8GotoAsyncCompleted &&
            exceptionEntry != null &&
            exceptionEntry.Stage?.Contains("GotoAsync", StringComparison.OrdinalIgnoreCase) == true)
        {
            // This is contradictory: cannot be both completed and failed
            var confidence = AuthDiagnosticConfidence.Low;
            return (AuthDiagnosticClassification.IncompleteOrOutOfOrder, confidence);
        }

        // Exception takes precedence only if no contradicting completed marker
        if (exceptionEntry != null && exceptionEntry.Stage?.Contains("GotoAsync", StringComparison.OrdinalIgnoreCase) == true)
        {
            var confidence = loggingEnabled ? AuthDiagnosticConfidence.High : AuthDiagnosticConfidence.Medium;
            return (AuthDiagnosticClassification.GotoFailed, confidence);
        }

        var confidence_level = loggingEnabled ? AuthDiagnosticConfidence.High : AuthDiagnosticConfidence.Medium;

        var classification = highestMarker switch
        {
            AuthDiagnosticMarker.Diag9ReturnedFromBeginAuthenticationAsync =>
                AuthDiagnosticClassification.BeginAuthCompleted,

            AuthDiagnosticMarker.Diag8GotoAsyncCompleted =>
                AuthDiagnosticClassification.GotoCompletedPostNavigationIssue,

            AuthDiagnosticMarker.Diag7AboutToCallGotoAsync =>
                AuthDiagnosticClassification.GotoInProgressOrNoTerminalEvidence,

            AuthDiagnosticMarker.Diag6ObserverAttached =>
                AuthDiagnosticClassification.GotoNotReached,

            AuthDiagnosticMarker.Diag5ObserverAttachStarting =>
                AuthDiagnosticClassification.ObserverAttachNotCompleted,

            AuthDiagnosticMarker.Diag4EnsureSupportedPassed =>
                AuthDiagnosticClassification.ObserverAttachNotCompleted,

            AuthDiagnosticMarker.Diag3BeginAuthenticationAsyncEntered =>
                AuthDiagnosticClassification.BeginAuthEnteredBeforeSupportPassed,

            AuthDiagnosticMarker.Diag2LaunchAsyncReturned =>
                AuthDiagnosticClassification.LaunchReturnedButBeginAuthNotObserved,

            AuthDiagnosticMarker.Diag1StartAsyncEntered =>
                AuthDiagnosticClassification.LaunchReturnedButBeginAuthNotObserved,

            _ => AuthDiagnosticClassification.Unknown
        };

        // Lower confidence if logging might be disabled
        if (!loggingEnabled)
            confidence_level = AuthDiagnosticConfidence.Low;

        return (classification, confidence_level);
    }

    private bool DetectOutOfOrder(List<AuthDiagnosticLogEntry> entries)
    {
        if (entries.Count < 2)
            return false;

        // Sort by marker value and check if sequence matches
        var sorted = entries.OrderBy(e => (int)e.Marker).ToList();
        for (int i = 0; i < entries.Count; i++)
        {
            if ((int)entries[i].Marker != (int)sorted[i].Marker)
                return true;
        }

        return false;
    }
}
