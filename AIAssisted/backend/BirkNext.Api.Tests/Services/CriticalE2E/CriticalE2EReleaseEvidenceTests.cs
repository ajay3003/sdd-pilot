using System.Text.Json;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Api.Services.TestData;
using BirkNext.BrowserCompanion;
using BirkNext.CriticalE2E;
using BirkNext.LocalHttpsProxy;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Critical E2E as release evidence: page context is enforced, selectors are ranked, outcomes are concrete, modules are registered,
/// Test Data is referenced centrally, the boundary decides what a pass means, and a definition change is a new version.
/// </summary>
public sealed class CriticalE2EReleaseEvidenceTests : IDisposable
{
    private const string Env = "env-1";
    private static readonly string[] Registered = ["M01"];
    private static readonly CriticalE2EAutomationBoundary BrowserOnly = new()
    {
        Start = "Approved application page", End = "Browser outcome after opening general roles", Downstream = "Service Bus / Bufdata verified separately", Source = "Integration Topology",
    };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-e2e-release-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CompanionSelector Role(string role, string name) => new() { Kind = CompanionSelectorKind.Role, Role = role, Name = name };

    private static CriticalE2EStepDefinition Click(string id = "open") => new()
    {
        StepId = id, Description = "Open Generelle roller", BrowserAction = CompanionActionKind.Click, Selector = Role("link", "Generelle roller"),
        CapturedOnRoute = "/admin/emergency-access", ExecutionRoute = "/admin/emergency-access", ExpectedDestination = "/admin/general-roles",
    };

    private static CriticalE2EFlowDefinition Flow(Func<CriticalE2EFlowDefinition, CriticalE2EFlowDefinition>? change = null)
    {
        var flow = new CriticalE2EFlowDefinition
        {
            Id = "m01-roles", Module = "M01", Name = "Open general roles", Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = Env,
            RequiredForRelease = true, Steps = [Click()], Boundary = BrowserOnly,
            FinalOutcome = new CriticalE2EFinalOutcome { Kind = CriticalE2EOutcomeKind.UrlEquals, Expected = "/admin/general-roles" },
        };
        return change is null ? flow : change(flow);
    }

    private static CriticalE2EFlowReadiness Readiness(CriticalE2EFlowDefinition flow, IReadOnlyList<string>? testDataIssues = null) =>
        CriticalE2EReleaseRules.Evaluate(flow, Registered, testDataIssues ?? []);

    // ── Route context ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/person/12345/details?tab=2#x", "/person/{id}/details")]
    [InlineData("https://m2lbdev.example.invalid/admin/general-roles/", "/admin/general-roles")]
    [InlineData("/case/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "/case/{id}")]
    [InlineData("/person/${data:personId}", "/person/${data:personId}")]
    [InlineData("/person/{personId}", "/person/{personId}")]
    public void RoutesAreStoredAsTemplatesWithoutObservedIdentifiers(string observed, string expected) =>
        CriticalE2ERoutes.Normalize(observed).Should().Be(expected);

    [Fact]
    public void RouteMatchingUsesTemplatesAndIgnoresTheQuery()
    {
        CriticalE2ERoutes.Matches("/person/{id}", "/person/987?x=1").Should().BeTrue();
        CriticalE2ERoutes.Matches("/admin/general-roles", "/Admin/General-Roles").Should().BeTrue();
        CriticalE2ERoutes.Matches("/admin/general-roles", "/admin/emergency-access").Should().BeFalse();
        CriticalE2ERoutes.Matches("/admin", "/admin/general-roles").Should().BeFalse("a prefix is not the same page");
    }

    [Fact]
    public void TheCapturedPageNeverBecomesTheExpectedDestination()
    {
        var button = new CompanionElementDescriptor { TagName = "button", Role = "button", AccessibleName = "Lagre", PageRoute = "/admin/emergency-access" };
        CriticalE2EAuthoring.SuggestDestination(button).Should().BeNull("only a link's own target proposes a destination");
        var link = button with { TagName = "a", Role = "link", AccessibleName = "Generelle roller", Href = "https://app.example.invalid/admin/general-roles?x=1" };
        CriticalE2EAuthoring.SuggestDestination(link).Should().Be("/admin/general-roles");
    }

    // ── Companion executor: page context enforced ───────────────────────────────────────────────────────────────────

    private sealed class LiveCompanion
    {
        private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
        public const string Origin = "https://m2lbdev.example.invalid";
        public BrowserCompanionService Service { get; } = new(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), TimeProvider.System, NullLogger<BrowserCompanionService>.Instance);
        private readonly string _session;

        public LiveCompanion()
        {
            var challenge = Service.StartPairing(new BrowserCompanionPairingStartRequest("dev", "M2LB DEV", "Development", [Origin]));
            _session = Service.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).SessionId!;
        }

        public BrowserCompanionAcceptResult Beat(string pageId, string route) =>
            Service.Heartbeat(new BrowserCompanionHeartbeat(_session, "dev", null, null, "0.1.0",
                [new BrowserCompanionLivePageReport(pageId, Origin, route, pageId.Split('-')[1])], [CompanionCapabilities.ElementPick]), Extension);

        public void Complete(CompanionAutomationCommand command) =>
            Service.CompleteCommand(new CompanionAutomationResultEnvelope
            {
                SessionId = _session, ProfileId = "dev", ExtensionVersion = "0.1.0",
                Result = new CompanionAutomationResult { CommandId = command.CommandId, Status = CriticalE2EStatus.Passed },
            }, Extension);
    }

    private static CriticalE2ERunContext Context(string pageId) => new()
    {
        Flow = new CriticalE2EFlowDefinition { Id = "f", ProfileId = "dev", EnvironmentId = Env },
        RunId = "run-" + Guid.NewGuid().ToString("N")[..6], CorrelationId = "c", StartedAt = DateTimeOffset.UtcNow,
        TargetOrigin = LiveCompanion.Origin, PageId = pageId,
    };

    [Fact]
    public async Task AStepOnTheWrongPageIsBlockedAsPageMismatch_AndNothingIsDispatched()
    {
        var live = new LiveCompanion();
        live.Beat("t1-a", "/admin/general-roles");
        var executor = new CompanionBrowserStepExecutor(live.Service, TimeProvider.System, NullLogger<CompanionBrowserStepExecutor>.Instance);

        var result = await executor.ExecuteAsync(Click(), Context("t1-a"), CancellationToken.None);

        result.Status.Should().Be(CriticalE2EStatus.Blocked);
        result.SanitizedError.Should().StartWith("Page mismatch: the step runs on /admin/emergency-access");
        live.Beat("t1-a", "/admin/general-roles").PendingCommand.Should().BeNull("a blocked step never reaches the page");
    }

    [Fact]
    public async Task ReachingTheExpectedDestinationPasses_AndMissingItFails()
    {
        var live = new LiveCompanion();
        live.Beat("t1-a", "/admin/emergency-access");
        var executor = new CompanionBrowserStepExecutor(live.Service, TimeProvider.System, NullLogger<CompanionBrowserStepExecutor>.Instance);

        var running = executor.ExecuteAsync(Click() with { TimeoutMs = 1_000 }, Context("t1-a"), CancellationToken.None);
        live.Complete(live.Beat("t1-a", "/admin/emergency-access").PendingCommand!);
        live.Beat("t1-a", "/admin/general-roles");
        var reached = await running;
        reached.Status.Should().Be(CriticalE2EStatus.Passed);
        reached.ObservedRoute.Should().Be("/admin/general-roles");

        var second = executor.ExecuteAsync(Click("again") with { TimeoutMs = 1_000, ExecutionRoute = null }, Context("t1-a"), CancellationToken.None);
        live.Complete(live.Beat("t1-a", "/admin/emergency-access").PendingCommand!);
        var missed = await second;
        missed.Status.Should().Be(CriticalE2EStatus.Failed, "the action executed but the flow went somewhere else");
        missed.SanitizedError.Should().Contain("Expected destination /admin/general-roles");
    }

    // ── Selector quality ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectorsAreRankedRoleNameThenTestIdThenLabelThenTextThenCss()
    {
        CompanionSelectorQuality.Stability(Role("link", "Generelle roller")).Should().Be(CompanionSelectorStability.Preferred);
        CompanionSelectorQuality.Stability(new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "roles" }).Should().Be(CompanionSelectorStability.Preferred);
        CompanionSelectorQuality.Stability(new CompanionSelector { Kind = CompanionSelectorKind.Label, Value = "Navn" }).Should().Be(CompanionSelectorStability.Acceptable);
        CompanionSelectorQuality.Stability(new CompanionSelector { Kind = CompanionSelectorKind.Text, Value = "Lagre" }).Should().Be(CompanionSelectorStability.Acceptable);
        var css = new CompanionSelector { Kind = CompanionSelectorKind.Css, Value = "main > div:nth-of-type(3) a" };
        CompanionSelectorQuality.Stability(css).Should().Be(CompanionSelectorStability.Fragile);
        CompanionSelectorQuality.Warning(css).Should().Be("Fragile selector · layout changes may break this step");

        CompanionSelectorQuality.Best([
            new CompanionSelectorCandidate { Selector = new CompanionSelector { Kind = CompanionSelectorKind.Text, Value = "Generelle roller" }, MatchCount = 1, Unique = true },
            new CompanionSelectorCandidate { Selector = Role("link", "Generelle roller"), MatchCount = 2, Unique = false },
            new CompanionSelectorCandidate { Selector = css, MatchCount = 1, Unique = true },
        ])!.Kind.Should().Be(CompanionSelectorKind.Text, "an ambiguous selector is never chosen, however well it ranks");
    }

    [Fact]
    public void AFragileSelectorBlocksReleaseUseUntilReviewed()
    {
        var fragile = Flow(f => f with { Steps = [Click() with { Selector = new CompanionSelector { Kind = CompanionSelectorKind.Css, Value = "div:nth-of-type(2) a" } }] });
        Readiness(fragile).Blockers.Select(b => b.Key).Should().Contain("selector");
        Readiness(fragile with { Steps = [fragile.Steps[0] with { SelectorReviewed = true }] }).ReleaseReady.Should().BeTrue();
    }

    // ── Authoring suggestions ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a", "link", null, "Generelle roller", "/admin/general-roles", CompanionActionKind.Click, "Open Generelle roller")]
    [InlineData("input", null, "text", "Child identifier", null, CompanionActionKind.Fill, "Fill Child identifier")]
    [InlineData("select", "combobox", null, "Kommune", null, CompanionActionKind.Select, "Select Kommune")]
    [InlineData("input", "checkbox", "checkbox", "Samtykke", null, CompanionActionKind.Click, "Check Samtykke")]
    [InlineData("button", "button", "submit", "Lagre", null, CompanionActionKind.Click, "Submit Lagre")]
    public void ActionAndNameAreSuggestedFromTheElement(string tag, string? role, string? type, string name, string? href, CompanionActionKind action, string stepName)
    {
        var element = new CompanionElementDescriptor { TagName = tag, Role = role, InputType = type, AccessibleName = name, Href = href };
        CriticalE2EAuthoring.SuggestAction(element).Should().Be(action);
        CriticalE2EAuthoring.SuggestName(action, element).Should().Be(stepName);
    }

    // ── Concrete outcomes ───────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(CriticalE2EOutcomeKind.UrlEquals, CompanionActionKind.AssertRoute, CompanionTextMatch.Equals)]
    [InlineData(CriticalE2EOutcomeKind.UrlContains, CompanionActionKind.AssertRoute, CompanionTextMatch.Contains)]
    [InlineData(CriticalE2EOutcomeKind.ElementVisible, CompanionActionKind.AssertVisible, CompanionTextMatch.Equals)]
    [InlineData(CriticalE2EOutcomeKind.ElementHidden, CompanionActionKind.AssertHidden, CompanionTextMatch.Equals)]
    [InlineData(CriticalE2EOutcomeKind.TextContains, CompanionActionKind.AssertText, CompanionTextMatch.Contains)]
    [InlineData(CriticalE2EOutcomeKind.TextEquals, CompanionActionKind.AssertText, CompanionTextMatch.Equals)]
    [InlineData(CriticalE2EOutcomeKind.ValueEquals, CompanionActionKind.AssertValue, CompanionTextMatch.Equals)]
    [InlineData(CriticalE2EOutcomeKind.ListContains, CompanionActionKind.AssertText, CompanionTextMatch.Contains)]
    public void EveryBrowserOutcomeIsAnExecutableAssertion(CriticalE2EOutcomeKind kind, CompanionActionKind action, CompanionTextMatch match)
    {
        var step = CriticalE2ERunner.OutcomeStep(new CriticalE2EFinalOutcome { Kind = kind, Selector = Role("status", "Melding"), Expected = "Tilgang lagret" })!;
        (step.BrowserAction, step.Match, step.IsFinalAssertion).Should().Be((action, match, true));
    }

    [Fact]
    public void ASuccessMessageWithoutARegionIsFoundByItsText()
    {
        var step = CriticalE2ERunner.OutcomeStep(new CriticalE2EFinalOutcome { Kind = CriticalE2EOutcomeKind.SuccessMessageVisible, Expected = "Tilgang lagret" })!;
        step.BrowserAction.Should().Be(CompanionActionKind.AssertVisible);
        step.Selector.Should().Be(new CompanionSelector { Kind = CompanionSelectorKind.Text, Value = "Tilgang lagret" });
    }

    [Fact]
    public void TheOldBooleanMarkerIsNotAConcreteOutcome()
    {
        var legacy = Flow(f => f with { FinalOutcome = null, Steps = [Click() with { IsFinalAssertion = true }] });
        CriticalE2EReleaseRules.HasConcreteOutcome(legacy).Should().BeFalse();
        CriticalE2EReleaseRules.OutcomeText(legacy).Should().Be("Legacy assertion marker · concrete outcome required");
        Readiness(legacy).Blockers.Single(b => b.Key == "outcome").Text.Should().Be("Legacy assertion marker · concrete outcome required.");

        var assertStep = legacy with { Steps = [Click(), new CriticalE2EStepDefinition { StepId = "check", BrowserAction = CompanionActionKind.AssertRoute, Expected = "/admin/general-roles", IsFinalAssertion = true }] };
        CriticalE2EReleaseRules.HasConcreteOutcome(assertStep).Should().BeTrue("a final step that is itself an assertion is concrete");
    }

    [Fact]
    public void AMissingAssertionBlocksReleaseReadiness() =>
        Readiness(Flow(f => f with { FinalOutcome = null })).Blockers.Select(b => b.Key).Should().Contain("outcome");

    private sealed class Scripted(Func<CriticalE2EStepDefinition, CriticalE2EStatus> outcome) : ICriticalE2EStepExecutor
    {
        public List<CriticalE2EStepDefinition> Executed { get; } = [];
        public bool CanExecute(CriticalE2EStepDefinition step) => true;
        public Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken)
        {
            Executed.Add(step with { Value = CriticalE2EVariables.Resolve(step.Value, context, DateTimeOffset.UtcNow) });
            var now = DateTimeOffset.UtcNow;
            var status = outcome(step);
            return Task.FromResult(CriticalE2EStepOutcome.From(step, now, now, status, error: status == CriticalE2EStatus.Passed ? null : "Expected route /admin/general-roles.",
                value: step.BrowserAction == CompanionActionKind.Fill ? "observed-control-value" : null));
        }
    }

    private static async Task<(CriticalE2ERunResult Run, Scripted Executor)> RunAsync(CriticalE2EFlowDefinition flow, Func<CriticalE2EStepDefinition, CriticalE2EStatus> outcome,
        string? build = "RC57", IReadOnlyDictionary<string, string>? data = null, string? dataProblem = null)
    {
        var executor = new Scripted(outcome);
        var runner = new CriticalE2ERunner([executor], TimeProvider.System, NullLogger<CriticalE2ERunner>.Instance);
        var run = await runner.RunAsync(new CriticalE2ERunRequest
        {
            Flow = flow, TargetOrigin = LiveCompanion.Origin, PageId = "t1-a", BuildId = build, Readiness = Readiness(flow), Data = data, TestDataProblem = dataProblem,
        }, CancellationToken.None);
        return (run, executor);
    }

    [Fact]
    public async Task ActionsPassingButTheOutcomeFailingIsAFailedFlow()
    {
        var (run, executor) = await RunAsync(Flow(), step => step.StepId == "final-outcome" ? CriticalE2EStatus.Failed : CriticalE2EStatus.Passed);
        run.Status.Should().Be(CriticalE2EStatus.Failed);
        run.StepResults.Last().Kind.Should().Be("Outcome");
        executor.Executed.Last().BrowserAction.Should().Be(CompanionActionKind.AssertRoute);
    }

    [Fact]
    public async Task ALegacyMarkerRunIsInconclusive_NeverPassed()
    {
        var (run, _) = await RunAsync(Flow(f => f with { FinalOutcome = null, Steps = [Click() with { IsFinalAssertion = true }] }), _ => CriticalE2EStatus.Passed);
        run.Status.Should().Be(CriticalE2EStatus.Inconclusive);
        run.FailureReason.Should().StartWith("Legacy assertion marker · concrete outcome required");
        run.ReleaseEvidence.Should().BeFalse();
    }

    [Fact]
    public async Task AProviderOutcomeWithoutAProviderIsInconclusive()
    {
        var (run, _) = await RunAsync(Flow(f => f with { FinalOutcome = new CriticalE2EFinalOutcome { Kind = CriticalE2EOutcomeKind.ProviderVerified, ProviderId = "bufdata-delivery" } }), _ => CriticalE2EStatus.Passed);
        run.Status.Should().Be(CriticalE2EStatus.Inconclusive);
        run.FailureReason.Should().Contain("No verification provider 'bufdata-delivery'");
    }

    // ── Run semantics and boundary ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADiagnosticRunWithoutABuildExecutes_ButIsNotReleaseEvidence()
    {
        var (run, _) = await RunAsync(Flow(), _ => CriticalE2EStatus.Passed, build: null);
        run.Status.Should().Be(CriticalE2EStatus.Passed);
        run.ReleaseEvidence.Should().BeFalse();
        run.ReleaseEvidenceNote.Should().StartWith("Diagnostic result · does not count as release evidence");
        CriticalE2EReleaseRules.ResultLabel(run).Should().Be("Diagnostic pass · browser journey only");
    }

    [Fact]
    public async Task AReleaseReadyRunWithABuildCounts_AndABrowserPassNeverClaimsDownstream()
    {
        var flow = Flow();
        var (run, _) = await RunAsync(flow, _ => CriticalE2EStatus.Passed);
        run.ReleaseEvidence.Should().BeTrue();
        run.FlowVersion.Should().Be(1);
        run.DownstreamVerified.Should().BeFalse();
        run.DownstreamOutcome.Should().Be("Not verified by this flow · Service Bus / Bufdata verified separately");
        CriticalE2EReleaseRules.ResultLabel(run).Should().Be("Browser journey passed · downstream not verified by this flow");
        run.Boundary!.Source.Should().Be("Integration Topology");

        var release = CriticalE2ECoverage.Release([flow], [run], Registered, Env, "RC57", null);
        release.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);

        var full = flow with { Boundary = BrowserOnly with { CoversDownstream = true } };
        var (fullRun, _) = await RunAsync(full, _ => CriticalE2EStatus.Passed);
        (fullRun.DownstreamVerified, CriticalE2EReleaseRules.ResultLabel(fullRun)).Should().Be((true, "Passed"));
    }

    [Fact]
    public void ABoundaryProposalNamesItsSource_AndAnEmptyTopologyIsNoSource()
    {
        var empty = CriticalE2EBoundarySuggestion.For(CriticalE2EExecutionMode.CompanionBrowser, new BirkNext.IntegrationTopology.IntegrationTopologyView());
        (empty.Source, empty.Downstream, empty.Explicit).Should().Be(("Manual", "", true));
        empty.Note.Should().StartWith("No Integration Topology is available");

        var topology = new BirkNext.IntegrationTopology.IntegrationTopologyView
        {
            Components = [new() { Id = "web", Name = "M2LB web", Kind = BirkNext.IntegrationTopology.TopologyComponentKind.Application },
                new() { Id = "sb", Name = "Service Bus", Kind = BirkNext.IntegrationTopology.TopologyComponentKind.Resource }],
        };
        var proposed = CriticalE2EBoundarySuggestion.For(CriticalE2EExecutionMode.CompanionBrowser, topology);
        (proposed.Source, proposed.Downstream, proposed.CoversDownstream).Should().Be(("Integration Topology", "Service Bus — verified separately (Integration Quality Review)", false));
        proposed.Note.Should().BeNull();
        var appsOnly = CriticalE2EBoundarySuggestion.For(CriticalE2EExecutionMode.CompanionBrowser, topology with { Components = [topology.Components[0]] });
        (appsOnly.Source, appsOnly.Note).Should().Be(("Integration Topology", "Integration Topology lists no downstream systems for this environment."));
    }

    // ── Modules ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AFreeTextModuleCreatesNoReleaseAuthority()
    {
        var typed = Flow(f => f with { Module = "M99" });
        CriticalE2ECoverage.Modules([typed], [], Registered, "RC57").Select(m => m.Module).Should().Equal("M01");
        CriticalE2ECoverage.UnresolvedModules([typed], Registered).Should().Equal("M99");
        Readiness(typed).Blockers.Single(b => b.Key == "module").Text.Should().StartWith("Unresolved module \"M99\" · manual label · does not count");
        CriticalE2ECoverage.Release([typed], [], [], Env, "RC57", null).ModulesTotal.Should().Be(0, "nothing is registered, so 0 / 0 — never 0 / 1 for a typed label");
    }

    [Fact]
    public void ARegisteredModuleIsCoveredOnlyByAReleaseReadyRequiredFlow()
    {
        var module = CriticalE2ECoverage.Modules([Flow()], [], Registered, null).Single();
        module.Covered.Should().BeTrue();
        module.Status.Should().Be("M01 · required flow ready · no build selected");

        var noOutcome = CriticalE2ECoverage.Modules([Flow(f => f with { FinalOutcome = null })], [], Registered, null).Single();
        noOutcome.Covered.Should().BeFalse();
        noOutcome.Status.Should().Be("M01 · required flow configured · assertion missing");
    }

    [Fact]
    public void ModuleRegistrationIsExplicitAndPersisted()
    {
        var store = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
        store.Save(Flow(f => f with { Module = "M02" }));
        store.Modules(Env).Should().BeEmpty("saving a flow never registers its module");
        store.RegisterModule(Env, new CriticalE2ERegisteredModule { Name = " M02 ", Source = "Project module" }).Should().BeTrue();
        store.RegisterModule(Env, new CriticalE2ERegisteredModule { Name = "m02" }).Should().BeFalse();

        var reloaded = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
        reloaded.RegisteredModules(Env).Should().ContainSingle().Which.Should().Match<CriticalE2ERegisteredModule>(m => m.Name == "M02" && m.Source == "Project module");
        reloaded.UnregisterModule(Env, "M02").Should().BeTrue();
        reloaded.Modules(Env).Should().BeEmpty();
    }

    // ── Release-required prerequisites ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EachMissingPrerequisiteIsItsOwnBlocker()
    {
        Readiness(Flow()).ReleaseReady.Should().BeTrue();
        Readiness(Flow()).Summary.Should().Be("Required for release · definition release-ready");
        Readiness(Flow(f => f with { Module = "" })).Blockers.Select(b => b.Key).Should().Contain("module");
        Readiness(Flow(f => f with { Enabled = false })).Blockers.Select(b => b.Key).Should().Contain("enabled");
        Readiness(Flow(f => f with { Boundary = null })).Blockers.Select(b => b.Key).Should().Contain("boundary");
        Readiness(Flow(f => f with { Steps = [Click() with { Selector = null }] })).Blockers.Select(b => b.Key).Should().Contain("runnable");
        Readiness(Flow(), ["Test Data missing for ${data:personId} (route)."]).Blockers.Select(b => b.Key).Should().Contain("testdata");
        Readiness(Flow(f => f with { Enabled = false })).Summary.Should().Be("Release requirement configured · not release-ready");
    }

    [Fact]
    public void ARequiredFlowWithoutABuildIsNotEvaluated_AndANotReadyRequiredFlowBlocksTheGate()
    {
        CriticalE2ECoverage.Release([Flow()], [], Registered, Env, null, null).Disposition.Should().Be(CriticalE2EReleaseDisposition.NotEvaluated);
        var notReady = CriticalE2ECoverage.Release([Flow(f => f with { Boundary = null })], [], Registered, Env, "RC57", null);
        notReady.Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);
        notReady.Summary.Should().Be("1 required flow(s) are configured but not release-ready.");
    }

    // ── Central Test Data ───────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Provider(params TestDataAsset[] assets) : ITestDataProvider
    {
        public string Id => "configured";
        public string Label => "Configured";
        public string Description => "";
        public IReadOnlyList<TestDataAsset> Assets() => assets;
    }

    private static TestDataAsset Asset(string alias, string value, TestDataKind kind = TestDataKind.Scalar, string? media = null, TestDataApproval approval = TestDataApproval.Approved) =>
        new() { Id = alias, Alias = alias, Kind = kind, MediaType = media, Approval = approval, Value = new TestDataValue(value), Source = "Configured" };

    private static readonly TestDataCatalog Catalog = new([new Provider(
        Asset("synthetic-child", "01010112345"),
        Asset("create-person-valid", "{\"name\":\"M2LB-E2E-x\"}", TestDataKind.Structured, "application/json"),
        Asset("melding-xml", "<melding/>", TestDataKind.Structured, "application/xml"),
        Asset("draft-child", "02020212345", approval: TestDataApproval.Unapproved),
        Asset("upload-pdf", "JVBERi0=", TestDataKind.File, "application/pdf"),
        Asset("expected-receipt", "Tilgang lagret", TestDataKind.ExpectedResult))]);

    private static CriticalE2EFlowDefinition WithData(params CriticalE2EDataMapping[] mappings) => Flow(f => f with
    {
        DataMappings = [.. mappings],
        Steps = [Click() with { ExecutionRoute = "/person/${data:childId}" }, new CriticalE2EStepDefinition
        {
            StepId = "fill", Description = "Fill Child identifier", BrowserAction = CompanionActionKind.Fill, Selector = Role("textbox", "Child identifier"), Value = "${data:childId}",
        }],
    });

    [Fact]
    public void MappedAliasesAreValidatedAgainstTheCentralCatalog()
    {
        var now = DateTimeOffset.UtcNow;
        CriticalE2ETestData.Issues(WithData(new CriticalE2EDataMapping("childId", ["synthetic-child"], TestDataDataType.SimpleValues, "form field")), Catalog, now).Should().BeEmpty();
        CriticalE2ETestData.Issues(WithData(new CriticalE2EDataMapping("childId", [], TestDataDataType.SimpleValues, "form field")), Catalog, now).Single().Should().Be("Test Data missing for ${data:childId} (form field).");
        CriticalE2ETestData.Issues(WithData(new CriticalE2EDataMapping("childId", ["draft-child"], TestDataDataType.SimpleValues, "form field")), Catalog, now).Single().Should().Contain("draft-child is unapproved");
        CriticalE2ETestData.Issues(WithData(new CriticalE2EDataMapping("childId", ["create-person-valid"], TestDataDataType.SimpleValues, "form field")), Catalog, now).Single().Should().Contain("needs Simple values");
        CriticalE2ETestData.Issues(WithData(), Catalog, now).Should().Contain("Test Data missing for ${data:childId} (no Test Data asset is mapped).");
        CriticalE2ETestData.Issues(Flow(f => f with { TestDataAliases = ["melding-xml", "create-person-valid"] }), Catalog, now).Should().BeEmpty("XML and JSON payload assets are referenced, not copied");
        CriticalE2ETestData.Issues(Flow(f => f with
        {
            DataMappings = [new("upload", ["upload-pdf"], TestDataDataType.FileFixture, "file upload"), new("receipt", ["expected-receipt"], TestDataDataType.ExpectedResult, "final outcome")],
            FinalOutcome = new CriticalE2EFinalOutcome { Kind = CriticalE2EOutcomeKind.SuccessMessageVisible, Expected = "${data:receipt}" },
        }), Catalog, now).Should().BeEmpty("a file fixture and an expected/reference asset map like any other type");
    }

    [Fact]
    public async Task ValuesAreResolvedOnlyForTheRun_AndNeverStoredInTheResult()
    {
        var flow = WithData(new CriticalE2EDataMapping("childId", ["synthetic-child"], TestDataDataType.SimpleValues, "form field"));
        var data = CriticalE2ETestData.Resolve(flow, Catalog);
        data.Should().Equal(new Dictionary<string, string> { ["childId"] = "01010112345" });

        var (run, executor) = await RunAsync(flow with { Steps = [flow.Steps[1]] }, _ => CriticalE2EStatus.Passed, data: data);
        executor.Executed[0].Value.Should().Be("01010112345", "the executor receives the value");
        var json = JsonSerializer.Serialize(run);
        json.Should().NotContain("01010112345", "the result keeps references, never the value");
        json.Should().NotContain("observed-control-value");
        run.TestDataAliases.Should().Equal("synthetic-child");
    }

    [Fact]
    public async Task UnresolvedTestDataBlocksTheRunBeforeAnyStep()
    {
        var (run, executor) = await RunAsync(WithData(new CriticalE2EDataMapping("childId", [], TestDataDataType.SimpleValues, "form field")), _ => CriticalE2EStatus.Passed,
            dataProblem: "Test Data missing for ${data:childId} (form field).");
        run.Status.Should().Be(CriticalE2EStatus.Blocked);
        executor.Executed.Should().BeEmpty();
    }

    // ── Versioning and staleness ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ADefinitionChangeIsANewVersion_AndOlderRunsStayTiedToTheirs()
    {
        var store = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
        store.Save(Flow()).Version.Should().Be(1);
        store.Save(Flow()).Version.Should().Be(1, "saving the same definition is not a change");
        var changed = store.Save(Flow(f => f with { Steps = [Click() with { ExpectedDestination = "/admin/roles" }] }));
        changed.Version.Should().Be(2);

        var oldRun = new CriticalE2ERunResult { RunId = "r1", FlowId = changed.Id, EnvironmentId = Env, Status = CriticalE2EStatus.Passed, BuildId = "RC57", FlowVersion = 1, ReleaseEvidence = true, StartedAt = DateTimeOffset.UtcNow };
        var summary = CriticalE2ECoverage.Summarize(changed, [oldRun], "RC57", Registered);
        summary.LastRunStale.Should().BeTrue();
        summary.LastResultMatchesRelease.Should().BeFalse("a pass of version 1 says nothing about version 2");
    }

    // ── Migration ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALegacyFlowLoadsWithoutLoss_AndRequiresReviewBeforeReleaseUse()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "flows.json"), """
            [{ "Id": "flow-legacy", "Module": "M01", "Name": "Test", "Mode": "CompanionBrowser", "ProfileId": "dev", "EnvironmentId": "env-1",
               "Enabled": true, "RequiredForRelease": true, "AutomationBoundary": "Browser only", "TestDataPolicy": "Synthetic data only, prefixed M2LB-E2E-",
               "Steps": [{ "StepId": "step-1", "Description": "Step 1 - Click", "BrowserAction": "Click",
                           "Selector": { "Kind": "Role", "Role": "link", "Name": "Generelle roller" }, "IsFinalAssertion": true }], "Version": 1 }]
            """);
        var store = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
        var legacy = store.Flow("flow-legacy")!;

        (legacy.Module, legacy.Name, legacy.TestDataPolicy, legacy.AutomationBoundary).Should().Be(("M01", "Test", "Synthetic data only, prefixed M2LB-E2E-", "Browser only"));
        legacy.Steps.Single().IsFinalAssertion.Should().BeTrue("the old marker is preserved as legacy metadata");

        var summary = CriticalE2ECoverage.Summarize(legacy, [], null, registeredModules: []);
        summary.Readiness.LegacyAssertionMarker.Should().BeTrue();
        summary.Readiness.ModuleResolved.Should().BeFalse();
        summary.Readiness.ReleaseReady.Should().BeFalse();
        summary.Outcome.Should().Be("Legacy assertion marker · concrete outcome required");
        summary.BoundaryText.Should().Be("Legacy note: Browser only");
        summary.Readiness.Summary.Should().Be("Release requirement configured · not release-ready");
        CriticalE2ECoverage.Release([legacy], [], [], Env, "RC57", null).ModulesTotal.Should().Be(0);
    }
}
