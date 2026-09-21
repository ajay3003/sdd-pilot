using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// The BirkNext → extension command channel. Every gate here exists because the thing on the other end is an
/// authenticated browser session belonging to a real person: a command that reaches the wrong session, the wrong
/// environment, the wrong origin, or the same session twice is not a bug in a test runner, it is an unwanted action in
/// a live application.
/// </summary>
public sealed class CompanionCommandTransportTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private readonly TestTime _time = new(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly BrowserCompanionService _service;
    /// <summary>The session most recently paired, so a result envelope can carry the right session proof.</summary>
    private string _sessionId = "";

    private sealed class TestTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    public CompanionCommandTransportTests() =>
        _service = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);

    /// <summary>Pairs, then reports an approved current page, which is what makes a session eligible for commands.</summary>
    private string Pair(string profile = "dev", string environmentType = "Development", string origin = Origin, bool onApprovedPage = true)
    {
        var challenge = _service.StartPairing(new BrowserCompanionPairingStartRequest(profile, "M2LB DEV", environmentType, [origin]));
        var paired = _service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension);
        paired.Accepted.Should().BeTrue();
        _sessionId = paired.SessionId!;
        _service.Heartbeat(new BrowserCompanionHeartbeat(paired.SessionId!, profile, onApprovedPage ? origin : null, onApprovedPage ? "/" : null, "0.1.0"), Extension);
        return paired.SessionId!;
    }

    private static CompanionAutomationCommand Command(string id = "cmd-1", string profile = "dev", string origin = Origin) => new()
    {
        CommandId = id, RunId = "run-1", FlowId = "flow-1", StepId = "step-1", ProfileId = profile,
        EnvironmentId = "env-1", TargetOrigin = origin, Action = CompanionActionKind.Click,
        Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "case-search" },
    };

    private CompanionAutomationCommand? Poll(string sessionId, string profile = "dev", string origin = Origin) =>
        _service.Heartbeat(new BrowserCompanionHeartbeat(sessionId, profile, origin, "/", "0.1.0"), Extension).PendingCommand;

    [Fact]
    public void AQueuedCommandReachesOnlyTheSessionItWasQueuedFor()
    {
        var mine = Pair("dev");
        var theirs = Pair("qa", origin: "https://m2lbqa.bufetat.no");
        _service.Dispatch(Command(profile: "dev")).Accepted.Should().BeTrue();

        Poll(theirs, "qa", "https://m2lbqa.bufetat.no").Should().BeNull("a command belongs to one pairing, not to whoever asks first");
        Poll(mine).Should().NotBeNull();
    }

    [Fact]
    public void ACommandForAnotherEnvironmentIsRefused()
    {
        Pair("dev");
        var refused = _service.Dispatch(Command(profile: "qa"));
        refused.Accepted.Should().BeFalse();
        refused.Message.Should().Contain("No Browser Companion session");
    }

    [Fact]
    public void ProductionIsNeverDriven()
    {
        Pair("prod", "Production");
        var refused = _service.Dispatch(Command(profile: "prod"));
        refused.Accepted.Should().BeFalse();
        refused.Message.Should().Contain("Production");
    }

    [Fact]
    public void AnUnknownEnvironmentTypeIsTreatedAsProduction()
    {
        Pair("mystery", environmentType: null!);
        var refused = _service.Dispatch(Command(profile: "mystery"));
        refused.Accepted.Should().BeFalse();
        refused.Message.Should().Contain("unknown");
    }

    [Fact]
    public void ACommandForAnUnapprovedOriginIsRefused()
    {
        Pair("dev");
        var refused = _service.Dispatch(Command(origin: "https://elsewhere.example"));
        refused.Accepted.Should().BeFalse();
        refused.Message.Should().Contain("not approved");
    }

    [Fact]
    public void ACommandIsRefusedWhenNoApprovedPageIsOpen()
    {
        Pair("dev", onApprovedPage: false);
        var refused = _service.Dispatch(Command());
        refused.Accepted.Should().BeFalse();
        refused.Message.Should().Contain("No approved page");
    }

    [Fact]
    public void ARetriedHeartbeatNeverHandsOutTheSameCommandTwice()
    {
        var session = Pair();
        _service.Dispatch(Command()).Accepted.Should().BeTrue();

        Poll(session).Should().NotBeNull();
        // This is the whole reason the state machine exists: the worker's heartbeat is retried routinely, and a second
        // delivery would be a second click.
        Poll(session).Should().BeNull();
        Poll(session).Should().BeNull();
    }

    [Fact]
    public void ACommandIdCannotBeReused()
    {
        Pair();
        _service.Dispatch(Command("cmd-1")).Accepted.Should().BeTrue();
        _service.CompleteCommand(Result("cmd-1", CriticalE2EStatus.Passed), Extension);

        var replay = _service.Dispatch(Command("cmd-1"));
        replay.Accepted.Should().BeFalse();
        replay.Message.Should().Contain("single-use");
    }

    [Fact]
    public void OnlyOneCommandIsInFlightAtATime()
    {
        Pair();
        _service.Dispatch(Command("cmd-1")).Accepted.Should().BeTrue();
        var second = _service.Dispatch(Command("cmd-2"));
        second.Accepted.Should().BeFalse();
        second.Message.Should().Contain("already in flight");
    }

    [Fact]
    public async Task AnExpiredCommandIsNeverExecuted()
    {
        var session = Pair();
        _service.Dispatch(Command()).Accepted.Should().BeTrue();
        _time.Advance(BrowserCompanionService.CommandLifetime + TimeSpan.FromSeconds(1));

        Poll(session).Should().BeNull("a click the user has since navigated away from is not a click we want");
        var result = await _service.AwaitResultAsync("cmd-1", CancellationToken.None);
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.SanitizedError.Should().Contain("expired");
    }

    [Fact]
    public async Task AResultIsRecordedOnceAndAReplayNeverOverwritesIt()
    {
        var session = Pair();
        _service.Dispatch(Command()).Accepted.Should().BeTrue();
        Poll(session);

        _service.CompleteCommand(Result("cmd-1", CriticalE2EStatus.Passed, "Clicked a \"Saker\""), Extension).Accepted.Should().BeTrue();
        var replay = _service.CompleteCommand(Result("cmd-1", CriticalE2EStatus.Failed, "something else"), Extension);
        replay.Accepted.Should().BeTrue("a replay is not an error");

        var stored = await _service.AwaitResultAsync("cmd-1", CancellationToken.None);
        stored.Status.Should().Be(CriticalE2EStatus.Passed);
        stored.SafeSummary.Should().Contain("Saker");
    }

    [Fact]
    public void AResultFromAnotherExtensionIsRefused()
    {
        var session = Pair();
        _service.Dispatch(Command()).Accepted.Should().BeTrue();
        Poll(session);

        var refused = _service.CompleteCommand(Result("cmd-1", CriticalE2EStatus.Passed), "chrome-extension://zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz");
        refused.Accepted.Should().BeFalse();
    }

    [Fact]
    public async Task AResultIsSanitizedBeforeItIsStored()
    {
        var session = Pair();
        _service.Dispatch(Command()).Accepted.Should().BeTrue();
        Poll(session);
        _service.CompleteCommand(Result("cmd-1", CriticalE2EStatus.Failed,
            summary: "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijkl",
            route: "/saker?token=secret#fragment"), Extension);

        var stored = await _service.AwaitResultAsync("cmd-1", CancellationToken.None);
        stored.SafeSummary.Should().NotContain("eyJhbGciOiJIUzI1NiJ9");
        stored.ObservedRoute.Should().Be("/saker", "a route is an identity, not a query string to keep");
    }

    [Fact]
    public async Task CancellingACommandEndsTheWaitWithCancelledNotFailed()
    {
        var session = Pair();
        _service.Dispatch(Command()).Accepted.Should().BeTrue();
        Poll(session);
        _service.CancelCommand("cmd-1", "The user stopped the run.");

        var result = await _service.AwaitResultAsync("cmd-1", CancellationToken.None);
        result.Status.Should().Be(CriticalE2EStatus.Cancelled);
    }

    [Fact]
    public void TheCompanionIsAskedToPollQuicklyOnlyWhileARunWindowIsOpen()
    {
        var session = Pair();
        _service.Heartbeat(new BrowserCompanionHeartbeat(session, "dev", Origin, "/", "0.1.0"), Extension)
            .NextHeartbeatMs.Should().BeNull("an observer that polls constantly is a cost with no reader");

        _service.OpenAutomationWindow("dev");
        _service.Heartbeat(new BrowserCompanionHeartbeat(session, "dev", Origin, "/", "0.1.0"), Extension)
            .NextHeartbeatMs.Should().BePositive();
    }

    private CompanionAutomationResultEnvelope Result(string commandId, CriticalE2EStatus status,
        string? summary = null, string? route = "/", string profile = "dev") => new()
    {
        SessionId = _sessionId, ProfileId = profile, ExtensionVersion = "0.1.0",
        Result = new CompanionAutomationResult
        {
            CommandId = commandId, StepId = "step-1", Status = status,
            SafeSummary = summary, ObservedRoute = route,
        },
    };
}
