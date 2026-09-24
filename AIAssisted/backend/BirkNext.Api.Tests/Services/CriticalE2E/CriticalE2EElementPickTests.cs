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
/// Element picking for flow authoring rides the existing command transport: same pairing proof, same live-page binding,
/// same production refusal. What is new is a capability the extension must report, a lifetime long enough for a person to
/// click, and a descriptor that is sanitized like every other thing the page says.
/// </summary>
public sealed class CriticalE2EElementPickTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private readonly TestTime _time = new(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
    private readonly BrowserCompanionService _companion;
    private readonly CriticalE2EService _service;
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

    public CriticalE2EElementPickTests()
    {
        _companion = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);
        var store = new CriticalE2EStore(Path.Combine(Path.GetTempPath(), "birknext-e2e-pick-" + Guid.NewGuid().ToString("N")[..8]), NullLogger<CriticalE2EStore>.Instance);
        _service = new CriticalE2EService(store, _companion, new NoGateway(),
            new CriticalE2ERunner([], _time, NullLogger<CriticalE2ERunner>.Instance), _time, NullLogger<CriticalE2EService>.Instance);
    }

    private void Pair(string environmentType = "Development")
    {
        var challenge = _companion.StartPairing(new BrowserCompanionPairingStartRequest("dev", "M2LB DEV", environmentType, [Origin]));
        _sessionId = _companion.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.2.0"), Extension).SessionId!;
    }

    private BrowserCompanionAcceptResult Beat(List<string>? capabilities, params string[] tabs) =>
        _companion.Heartbeat(new BrowserCompanionHeartbeat(_sessionId, "dev", null, null, "0.2.0",
            tabs.Select(t => new BrowserCompanionLivePageReport($"t{t}-inst{t}", Origin, "/plassering", $"inst{t}")).ToList(), capabilities), Extension);

    private static readonly List<string> Picks = [CompanionCapabilities.ElementPick];

    private Task<CriticalE2EElementPickResult> Pick(string environmentType = "Development") =>
        _service.PickElementAsync(new CriticalE2EElementPickRequest { ProfileId = "dev", EnvironmentId = "dev", EnvironmentType = environmentType, TimeoutMs = 30_000 }, CancellationToken.None);

    [Fact]
    public void TheCapabilityComesFromTheHeartbeat_AndAnOlderBuildHasNone()
    {
        Pair();
        Beat(null, "1");
        _companion.Status("dev").Live.SupportsElementPick.Should().BeFalse("an older build reports no capabilities");

        Beat(["element-pick", "<script>", "UPPER", new string('x', 60)], "1");
        var live = _companion.Status("dev").Live;
        live.SupportsElementPick.Should().BeTrue();
        live.Capabilities.Should().Equal("element-pick");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData(null)]
    public async Task ProductionIsRefusedBeforeAnythingIsQueued(string? environmentType)
    {
        Pair();
        Beat(Picks, "1");

        var result = await Pick(environmentType!);

        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.Message.Should().Be(CriticalE2EEnvironmentPolicy.BlockedReason(environmentType));
        Beat(Picks, "1").PendingCommand.Should().BeNull();
    }

    [Fact]
    public async Task EachMissingPrerequisiteHasItsOwnReason()
    {
        (await Pick()).Message.Should().Be("The Browser Companion is not connected.");

        Pair();
        Beat(Picks);
        (await Pick()).Message.Should().Be("No approved application page is open in the paired browser.");

        Beat(Picks, "1", "2");
        (await Pick()).Message.Should().Be("2 approved pages are open. Leave only the page to pick from open.");

        Beat(null, "1");
        (await Pick()).Message.Should().Contain("does not support element picking");
    }

    [Fact]
    public async Task APickIsBoundToTheLivePage_AndItsDescriptorIsSanitized()
    {
        Pair();
        Beat(Picks, "1");

        var picking = Pick();
        var claimed = Beat(Picks, "1").PendingCommand;
        claimed.Should().NotBeNull();
        claimed!.Action.Should().Be(CompanionActionKind.PickElement);
        claimed.PageId.Should().Be("t1-inst1");
        claimed.ContentScriptInstanceId.Should().Be("inst1");
        claimed.TargetOrigin.Should().Be(Origin);
        claimed.Selector.Should().BeNull("a pick carries no selector — the tester chooses");

        _companion.CompleteCommand(new CompanionAutomationResultEnvelope
        {
            SessionId = _sessionId, ProfileId = "dev", ExtensionVersion = "0.2.0",
            Result = new CompanionAutomationResult
            {
                CommandId = claimed.CommandId, Status = CriticalE2EStatus.Passed,
                Element = new CompanionElementDescriptor
                {
                    PageOrigin = Origin + "/", PageRoute = "/plassering/?x=1", TagName = "button", Role = "button",
                    AccessibleName = "Ny plassering for ola.nordmann@example.no", TestId = "create-placement", Visible = true, Enabled = true,
                    Candidates = Enumerable.Range(0, 12).Select(i => new CompanionSelectorCandidate
                    {
                        Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "create-placement" }, MatchCount = 1, Unique = true,
                    }).ToList(),
                    Recommended = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "create-placement" },
                },
            },
        }, Extension).Accepted.Should().BeTrue();

        var result = await picking;
        result.Status.Should().Be(CriticalE2EStatus.Passed);
        result.Message.Should().Be("Element picked: testid=\"create-placement\".");
        var element = result.Element!;
        element.PageOrigin.Should().Be(Origin);
        element.PageRoute.Should().Be("/plassering");
        element.AccessibleName.Should().NotContain("ola.nordmann@example.no");
        element.Candidates.Should().HaveCount(8, "never more candidates than the strategies can produce");
        element.Recommended.Should().Be(new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "create-placement" });
    }

    [Fact]
    public async Task EscIsCancelled_NotAFailure()
    {
        Pair();
        Beat(Picks, "1");
        var picking = Pick();
        var claimed = Beat(Picks, "1").PendingCommand!;
        _companion.CompleteCommand(new CompanionAutomationResultEnvelope
        {
            SessionId = _sessionId, ProfileId = "dev", ExtensionVersion = "0.2.0",
            Result = new CompanionAutomationResult { CommandId = claimed.CommandId, Status = CriticalE2EStatus.Cancelled, SanitizedError = "Selection cancelled." },
        }, Extension);

        var result = await picking;
        result.Status.Should().Be(CriticalE2EStatus.Cancelled);
        result.Element.Should().BeNull();
    }

    [Fact]
    public void APickLivesLongEnoughForAPerson_OrdinaryStepsKeepTheShortLifetime()
    {
        Pair();
        Beat(Picks, "1");
        _companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = "pick-1", ProfileId = "dev", TargetOrigin = Origin, Action = CompanionActionKind.PickElement, TimeoutMs = 45_000,
        }).Accepted.Should().BeTrue();
        Beat(Picks, "1").PendingCommand.Should().NotBeNull();

        _time.Advance(TimeSpan.FromSeconds(60));   // longer than an ordinary step may live
        Beat(Picks, "1");
        _companion.Dispatch(new CompanionAutomationCommand { CommandId = "step-2", ProfileId = "dev", TargetOrigin = Origin, Action = CompanionActionKind.Click })
            .Message.Should().Contain("already in flight", "the pick is still waiting for the tester");

        _time.Advance(TimeSpan.FromSeconds(31));   // past timeout + delivery slack
        Beat(Picks, "1");
        _companion.Dispatch(new CompanionAutomationCommand { CommandId = "step-3", ProfileId = "dev", TargetOrigin = Origin, Action = CompanionActionKind.Click })
            .Accepted.Should().BeTrue("an unanswered pick expires rather than holding the page forever");
    }

    [Fact]
    public void ACompanionThatCannotPickIsRefusedAtTheQueue()
    {
        Pair();
        Beat(null, "1");
        var dispatch = _companion.Dispatch(new CompanionAutomationCommand
        {
            CommandId = "pick-old", ProfileId = "dev", TargetOrigin = Origin, Action = CompanionActionKind.PickElement,
        });
        dispatch.Accepted.Should().BeFalse();
        dispatch.Message.Should().Contain("Reload the extension");
    }

    [Fact]
    public void TheOverviewSaysWhetherPickingCanStart_EvenBeforeAnyBrowserFlowExists()
    {
        CriticalE2EEngineStatus PickStatus(string type = "Development") => _service.Overview(new CriticalE2EOverviewRequest
        {
            ProfileId = "dev", EnvironmentId = "dev", EnvironmentName = "M2LB DEV", EnvironmentType = type,
        }).ElementPick;

        PickStatus().Message.Should().Be("The Browser Companion is not connected.");
        Pair();
        Beat(Picks, "1");
        var ready = _service.Overview(new CriticalE2EOverviewRequest { ProfileId = "dev", EnvironmentId = "dev", EnvironmentType = "Development" });
        ready.BrowserEngine.State.Should().Be(CriticalE2EEngineState.NotConfigured, "no browser flow exists yet");
        ready.ElementPick.State.Should().Be(CriticalE2EEngineState.Ready, "picking is how the first flow gets written");
        PickStatus("Production").State.Should().NotBe(CriticalE2EEngineState.Ready);
    }

    [Fact]
    public void PickElementIsNeverAFlowStep()
    {
        var flow = new CriticalE2EFlowDefinition
        {
            Module = "M02", Name = "Foreta plassering", ProfileId = "dev", Mode = CriticalE2EExecutionMode.CompanionBrowser,
            Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.PickElement, IsFinalAssertion = true,
                Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "x" } }],
        };
        flow.ConfigurationProblem().Should().Be("Pick element is an authoring action, not a flow step.");
    }
}
