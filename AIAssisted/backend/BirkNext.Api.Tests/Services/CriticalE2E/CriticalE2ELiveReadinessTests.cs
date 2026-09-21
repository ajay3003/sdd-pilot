using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using BirkNext.LocalHttpsProxy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Critical E2E readiness against the live browser, and only the live browser.
///
/// The failure this guards against is the most dangerous one in the whole capability: reporting Ready because a DOM was
/// captured yesterday, then sending a click into a browser that has nothing open. Readiness has to be a claim about the
/// present tense.
/// </summary>
public sealed class CriticalE2ELiveReadinessTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private readonly TestTime _time = new(new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly BrowserCompanionService _companion;
    private readonly CriticalE2EService _service;
    private readonly CriticalE2EStore _store;
    private string _sessionId = "";

    private sealed class TestTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity i, string m, string u, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity i, string e, string q, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity i, string e, CancellationToken ct = default) => Task.FromResult(new AuthenticatedGraphQlSchemaOutcome());
    }

    public CriticalE2ELiveReadinessTests()
    {
        _companion = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);
        _store = new CriticalE2EStore(Path.Combine(Path.GetTempPath(), "birknext-e2e-live-" + Guid.NewGuid().ToString("N")[..8]), NullLogger<CriticalE2EStore>.Instance);
        _store.Save(new CriticalE2EFlowDefinition
        {
            Id = "flow-1", Module = "Tjeneste", Name = "Open a service", Mode = CriticalE2EExecutionMode.CompanionBrowser,
            ProfileId = "dev", EnvironmentId = "dev", RequiredForRelease = true,
            Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible,
                Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "status" }, IsFinalAssertion = true }],
        });
        _service = new CriticalE2EService(_store, _companion, new NoGateway(),
            new CriticalE2ERunner([], _time, NullLogger<CriticalE2ERunner>.Instance), _time, NullLogger<CriticalE2EService>.Instance);
    }

    private void Pair()
    {
        var challenge = _companion.StartPairing(new BrowserCompanionPairingStartRequest("dev", "M2LB DEV", "Development", [Origin]));
        _sessionId = _companion.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).SessionId!;
    }

    private void Beat(params (string Tab, string Route, string Instance)[] pages) =>
        _companion.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.1.0",
            pages.Select(p => new BrowserCompanionLivePageReport($"t{p.Tab}-{p.Instance}", Origin, p.Route, p.Instance)).ToList()), Extension);

    private void SendEvidence() => _companion.AcceptEvidence(new BrowserCompanionEvidenceEnvelope
    {
        SessionId = _sessionId, ProfileId = "dev", ExtensionVersion = "0.1.0",
        Pages = [new BrowserPageEvidence
        {
            ProfileId = "dev", PageOrigin = Origin, PagePath = "/saker", VisitStartedAt = _time.GetUtcNow(),
            CapturedAt = _time.GetUtcNow(), Dom = new BrowserDomSummary { NodeCount = 10 },
        }],
    }, Extension);

    private CriticalE2EEngineStatus Browser() => _service.Overview(new CriticalE2EOverviewRequest
    {
        ProfileId = "dev", EnvironmentId = "dev", EnvironmentName = "M2LB DEV", EnvironmentType = "Development",
    }).BrowserEngine;

    [Fact]
    public void ConnectedWithStoredEvidenceAndNoOpenPageIsNotReady()
    {
        Pair();
        Beat();          // connected, nothing open
        SendEvidence();  // and plenty of history

        var engine = Browser();
        engine.Ready.Should().BeFalse("a DOM captured earlier is not a page a command can be delivered to");
        engine.State.Should().Be(CriticalE2EEngineState.RequiresBrowserSession);
        engine.Message.Should().Contain("no approved application page is open");
        engine.Action.Should().Contain("Open a signed-in page");
    }

    [Fact]
    public void OneLiveApprovedPageIsReadyEvenWithNoEvidenceAtAll()
    {
        Pair();
        Beat(("1", "/admin/operations", "abc"));

        var engine = Browser();
        engine.Ready.Should().BeTrue("nothing has been captured yet, and nothing needs to have been");
        engine.Message.Should().Contain("/admin/operations");
    }

    [Fact]
    public void ClosingTheTabTakesReadinessAwayAndLeavesTheEvidence()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        SendEvidence();
        Browser().Ready.Should().BeTrue();

        Beat();   // tab closed

        Browser().Ready.Should().BeFalse();
        _companion.Status("dev").Evidence.PagesWithEvidence.Should().Be(1);
    }

    [Fact]
    public void TwoOpenPagesRequireAChoiceRatherThanPickingOne()
    {
        Pair();
        Beat(("1", "/saker", "abc"), ("2", "/arkiv", "def"));

        var engine = Browser();
        engine.Ready.Should().BeFalse();
        engine.Message.Should().Contain("2 approved pages are open");
        engine.Action.Should().Contain("select the page");
    }

    [Fact]
    public void ADisconnectedCompanionIsNotReadyHoweverMuchEvidenceExists()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        SendEvidence();
        _time.Advance(BrowserCompanionLimits.ConnectedWindow + TimeSpan.FromSeconds(5));

        Browser().Ready.Should().BeFalse();
    }

    [Fact]
    public void ACommandIsRefusedWhenNoPageIsLiveAndAcceptedWhenOneIs()
    {
        Pair();
        Beat();
        SendEvidence();

        var command = new CompanionAutomationCommand
        {
            CommandId = "c1", RunId = "r", FlowId = "f", StepId = "s", ProfileId = "dev", EnvironmentId = "dev",
            TargetOrigin = Origin, Action = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "go" },
        };
        var refused = _companion.Dispatch(command);
        refused.Accepted.Should().BeFalse();
        refused.Message.Should().Contain("No approved page is open");

        Beat(("1", "/saker", "abc"));
        _companion.Dispatch(command with { CommandId = "c2" }).Accepted.Should().BeTrue();
    }

    [Fact]
    public void ACommandBindsToThePageItWasQueuedAgainstAndIsRefusedOnceThatPageIsGone()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        var dispatched = _companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = "c1", ProfileId = "dev", TargetOrigin = Origin, Action = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "go" },
        });
        dispatched.Accepted.Should().BeTrue();
        var handed = _companion.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.1.0",
            [new BrowserCompanionLivePageReport("t1-abc", Origin, "/saker", "abc")]), Extension).PendingCommand;
        handed!.PageId.Should().Be("t1-abc");
        handed.ContentScriptInstanceId.Should().Be("abc");

        // The user reloaded: same tab, new content script. A command still bound to the old page must not be re-aimed.
        Beat(("1", "/saker", "xyz"));
        var stale = _companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = "c2", ProfileId = "dev", TargetOrigin = Origin, PageId = "t1-abc",
            Action = CompanionActionKind.Click, Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "go" },
        });
        stale.Accepted.Should().BeFalse();
        stale.Message.Should().Contain("no longer open");
    }

    [Fact]
    public void WithTwoPagesOpenACommandMustNameOne()
    {
        Pair();
        Beat(("1", "/saker", "abc"), ("2", "/arkiv", "def"));

        var ambiguous = _companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = "c1", ProfileId = "dev", TargetOrigin = Origin, Action = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "go" },
        });
        ambiguous.Accepted.Should().BeFalse();
        ambiguous.Message.Should().Contain("2 approved pages are open");

        var chosen = _companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = "c2", ProfileId = "dev", TargetOrigin = Origin, PageId = "t2-def",
            Action = CompanionActionKind.Click, Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "go" },
        });
        chosen.Accepted.Should().BeTrue("an explicit page resolves the ambiguity");
    }

    [Fact]
    public void ARouteChangeDoesNotInterruptAnInFlightRun()
    {
        Pair();
        Beat(("1", "/saker", "abc"));
        _companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = "c1", ProfileId = "dev", TargetOrigin = Origin, Action = CompanionActionKind.Click,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "go" },
        }).Accepted.Should().BeTrue();

        // The click navigated the SPA. The page is the same page.
        Beat(("1", "/saker/42", "abc"));
        _companion.Status("dev").Live.CurrentPageId.Should().Be("t1-abc");
        Browser().Ready.Should().BeTrue();
    }
}
