using BirkNext.Api.Services.AuthenticatedReview;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.AuthenticatedReview;

[Trait("Category", "AuthDiagnosticParser")]
public sealed class AuthDiagnosticLogParserTests
{
    private readonly AuthDiagnosticLogParser _parser = new();
    private const string SessionId = "a1b2c3d4e5f6789012345678901234ab";

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        var result = _parser.ParseLogLines(Array.Empty<string>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Diag1Only_ExtractsMarkerAndSessionId()
    {
        var lines = new[] { $"[DIAG-1] StartAsync entered sessionId={SessionId}" };
        var result = _parser.ParseLogLines(lines);

        result.Should().HaveCount(1);
        result[0].Marker.Should().Be(AuthDiagnosticMarker.Diag1StartAsyncEntered);
        result[0].SessionId.Should().Be(SessionId);
    }

    [Fact]
    public void Parse_AllDiagMarkers_ExtractsAll()
    {
        var lines = new[]
        {
            $"[DIAG-1] StartAsync entered sessionId={SessionId}",
            $"[DIAG-2] LaunchAsync returned sessionId={SessionId}",
            $"[DIAG-3] BeginAuthenticationAsync entered sessionId={SessionId}",
            $"[DIAG-4] EnsureSupported passed sessionId={SessionId}",
            $"[DIAG-5] observer attach starting sessionId={SessionId}",
            $"[DIAG-6] observer attached sessionId={SessionId}",
            $"[DIAG-7] about to call GotoAsync sessionId={SessionId}",
            $"[DIAG-8] GotoAsync completed sessionId={SessionId}",
            $"[DIAG-9] returned from BeginAuthenticationAsync sessionId={SessionId}"
        };

        var result = _parser.ParseLogLines(lines);
        result.Should().HaveCount(9);
    }

    [Fact]
    public void Parse_WithUnrelatedLogs_IgnoresSafely()
    {
        var lines = new[]
        {
            "2026-09-07 10:15:42.123 INFO Some unrelated log line",
            $"[DIAG-1] StartAsync entered sessionId={SessionId}",
            "2026-09-07 10:15:42.456 WARN Another unrelated line",
            $"[DIAG-2] LaunchAsync returned sessionId={SessionId}"
        };

        var result = _parser.ParseLogLines(lines);
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_MalformedSessionId_RejectsNonHex()
    {
        var lines = new[] { $"[DIAG-1] StartAsync entered sessionId=NOT_HEX_12345" };
        var result = _parser.ParseLogLines(lines);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Exception_ExtractsStageAndType()
    {
        var lines = new[] { $"[DIAG-EXCEPTION] BeginAuthenticationAsync failed at GotoAsync stage=GotoAsync sessionId={SessionId}" };
        var result = _parser.ParseLogLines(lines);

        result.Should().HaveCount(1);
        result[0].Marker.Should().Be(AuthDiagnosticMarker.DiagException);
        result[0].Stage.Should().Be("GotoAsync");
    }

    [Fact]
    public void Classify_Diag2_LaunchReturnedButBeginAuthNotObserved()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 1)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.LaunchReturnedButBeginAuthNotObserved);
        summary.Confidence.Should().Be(AuthDiagnosticConfidence.High);
    }

    [Fact]
    public void Classify_Diag3_BeginAuthEnteredBeforeSupportPassed()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 1),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag3BeginAuthenticationAsyncEntered, SequenceNumber: 2)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.BeginAuthEnteredBeforeSupportPassed);
        summary.Confidence.Should().Be(AuthDiagnosticConfidence.High);
    }

    [Fact]
    public void Classify_Diag4_ObserverAttachNotCompleted()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 1),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag3BeginAuthenticationAsyncEntered, SequenceNumber: 2),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag4EnsureSupportedPassed, SequenceNumber: 3)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.ObserverAttachNotCompleted);
    }

    [Fact]
    public void Classify_Diag6_GotoNotReached()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 1),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag3BeginAuthenticationAsyncEntered, SequenceNumber: 2),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag4EnsureSupportedPassed, SequenceNumber: 3),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag5ObserverAttachStarting, SequenceNumber: 4),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag6ObserverAttached, SequenceNumber: 5)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.GotoNotReached);
    }

    [Fact]
    public void Classify_Diag7NoException_GotoInProgressOrNoTerminalEvidence()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag7AboutToCallGotoAsync, SequenceNumber: 6)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.GotoInProgressOrNoTerminalEvidence);
    }

    [Fact]
    public void Classify_Diag7WithGotoAsyncException_GotoFailed()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag7AboutToCallGotoAsync, SequenceNumber: 6),
            new AuthDiagnosticLogEntry(
                SessionId,
                AuthDiagnosticMarker.DiagException,
                Stage: "GotoAsync",
                ExceptionType: "PlaywrightException",
                SequenceNumber: 7)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.GotoFailed);
        summary.ExceptionType.Should().Be("PlaywrightException");
    }

    [Fact]
    public void Classify_Diag8_GotoCompletedPostNavigationIssue()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag8GotoAsyncCompleted, SequenceNumber: 7)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.GotoCompletedPostNavigationIssue);
    }

    [Fact]
    public void Classify_Diag9_BeginAuthCompleted()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag9ReturnedFromBeginAuthenticationAsync, SequenceNumber: 8)
        };

        var summary = _parser.ClassifySession(entries);

        summary.Classification.Should().Be(AuthDiagnosticClassification.BeginAuthCompleted);
        summary.Confidence.Should().Be(AuthDiagnosticConfidence.High);
    }

    [Fact]
    public void Classify_LoggingDisabled_LowerConfidence()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 1)
        };

        var summary = _parser.ClassifySession(entries, informationLoggingEnabled: false);

        summary.Confidence.Should().Be(AuthDiagnosticConfidence.Low);
    }

    [Fact]
    public void Classify_OutOfOrder_DetectsAndReports()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag3BeginAuthenticationAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 1)
        };

        var summary = _parser.ClassifySession(entries);

        summary.IsOutOfOrder.Should().BeTrue();
    }

    [Fact]
    public void Classify_DuplicateMarkers_DetectsAndReports()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 1),
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 2)
        };

        var summary = _parser.ClassifySession(entries);

        summary.HasDuplicateMarkers.Should().BeTrue();
    }

    [Fact]
    public void Parse_SecretSafety_NoTokensInSessionId()
    {
        // Ensure parser only accepts hex, not URL-like strings
        var lines = new[] { "[DIAG-1] StartAsync entered sessionId=http://example.com?token=secret" };
        var result = _parser.ParseLogLines(lines);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Classify_MultipleSessions_GroupsIndependently()
    {
        var sessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
        var sessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb2";

        var entries = new[]
        {
            new AuthDiagnosticLogEntry(sessionA, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 0),
            new AuthDiagnosticLogEntry(sessionA, AuthDiagnosticMarker.Diag2LaunchAsyncReturned, SequenceNumber: 1),
            new AuthDiagnosticLogEntry(sessionB, AuthDiagnosticMarker.Diag1StartAsyncEntered, SequenceNumber: 2),
            new AuthDiagnosticLogEntry(sessionB, AuthDiagnosticMarker.Diag9ReturnedFromBeginAuthenticationAsync, SequenceNumber: 3)
        };

        var summaryA = _parser.ClassifySession(entries.Where(e => e.SessionId == sessionA).ToList());
        var summaryB = _parser.ClassifySession(entries.Where(e => e.SessionId == sessionB).ToList());

        summaryA.Classification.Should().Be(AuthDiagnosticClassification.LaunchReturnedButBeginAuthNotObserved);
        summaryB.Classification.Should().Be(AuthDiagnosticClassification.BeginAuthCompleted);
    }

    [Fact]
    public void SafeSummary_ContainsNoSecrets()
    {
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag9ReturnedFromBeginAuthenticationAsync, SequenceNumber: 0)
        };

        var summary = _parser.ClassifySession(entries);
        var safeSummary = summary.SafeSummary;

        safeSummary.Should().NotContain(SessionId);
        safeSummary.Should().Contain("BeginAuthCompleted");
    }

    [Fact]
    public void Classify_Diag8WithGotoAsyncException_FlagsContradictionAsOutOfOrder()
    {
        // Impossible case: GotoAsync both completed AND failed
        // This indicates logging error or marker out-of-order evidence
        var entries = new[]
        {
            new AuthDiagnosticLogEntry(SessionId, AuthDiagnosticMarker.Diag8GotoAsyncCompleted, SequenceNumber: 7),
            new AuthDiagnosticLogEntry(
                SessionId,
                AuthDiagnosticMarker.DiagException,
                Stage: "GotoAsync",
                ExceptionType: "PlaywrightException",
                SequenceNumber: 8)
        };

        var summary = _parser.ClassifySession(entries);

        // This contradiction should be classified as incomplete/out-of-order with Low confidence
        summary.Classification.Should().Be(AuthDiagnosticClassification.IncompleteOrOutOfOrder);
        summary.Confidence.Should().Be(AuthDiagnosticConfidence.Low);
    }
}
