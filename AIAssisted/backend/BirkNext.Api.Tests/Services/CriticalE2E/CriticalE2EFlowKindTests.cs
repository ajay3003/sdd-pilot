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
/// Critical vs diagnostic flows, and the attended readiness snapshot. Found on a real page: fifteen smoke flows under a
/// "SMOKE" module made release coverage read 0 / 1, and "Not configured" looked like "no flows".
/// </summary>
public sealed class CriticalE2EFlowKindTests
{
    private static CriticalE2EFlowDefinition Flow(string id, string module, CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical,
        bool required = true, bool enabled = true) => new()
        {
            Id = id, Module = module, Name = id, Kind = kind, Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = "env-1",
            Enabled = enabled, RequiredForRelease = required,
            Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible,
                Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "status" }, IsFinalAssertion = true }],
        };

    [Fact]
    public void ADiagnosticOnlyModuleIsNotADeliveryModule_EvenWhenPreviouslyRecordedAsKnown()
    {
        var flows = new[] { Flow("smoke-1", "SMOKE", CriticalE2EFlowKind.Diagnostic, required: false), Flow("smoke-2", "SMOKE", CriticalE2EFlowKind.Diagnostic) };

        var modules = CriticalE2ECoverage.Modules(flows, [], knownModules: ["SMOKE"], buildId: null);
        var release = CriticalE2ECoverage.Release(flows, [], ["SMOKE"], "env-1", null, null);

        modules.Should().BeEmpty();
        release.ModulesTotal.Should().Be(0);
        release.Disposition.Should().Be(CriticalE2EReleaseDisposition.NotConfigured, "a required diagnostic flow is not release coverage");
        release.Summary.Should().Be("No critical flow is currently marked as required for release.");
    }

    [Fact]
    public void AModuleWithACriticalFlowCounts_AndItsDiagnosticFlowsDoNotCoverIt()
    {
        var flows = new[] { Flow("m02-smoke", "M02", CriticalE2EFlowKind.Diagnostic), Flow("m02", "M02", required: false) };

        var module = CriticalE2ECoverage.Modules(flows, [], [], null).Single();

        module.Module.Should().Be("M02");
        module.Covered.Should().BeFalse("only a required, enabled CRITICAL flow covers a module");
        module.Flows.Select(f => f.FlowId).Should().Equal("m02");
    }

    [Fact]
    public void SummariesCarryKindAndStepCount()
    {
        var summary = CriticalE2ECoverage.Summarize(Flow("x", "SMOKE", CriticalE2EFlowKind.Diagnostic), [], null);
        summary.Kind.Should().Be(CriticalE2EFlowKind.Diagnostic);
        summary.StepCount.Should().Be(1);
    }

    [Fact]
    public void AnOlderDefinitionWithoutKindReadsAsCritical()
    {
        var json = """{"id":"old","module":"M02","name":"Old","mode":"CompanionBrowser","steps":[]}""";
        var flow = System.Text.Json.JsonSerializer.Deserialize<CriticalE2EFlowDefinition>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        flow.Kind.Should().Be(CriticalE2EFlowKind.Critical);
    }

    // ── Attended readiness and batches ─────────────────────────────────────────────────────────────────────────

    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity i, string m, string u, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity i, string e, string q, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity i, string e, CancellationToken ct = default) => Task.FromResult(new AuthenticatedGraphQlSchemaOutcome());
    }

    private sealed class RecordingExecutor : ICriticalE2EStepExecutor
    {
        public List<string> Flows { get; } = [];
        public bool CanExecute(CriticalE2EStepDefinition step) => true;
        public Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken)
        {
            Flows.Add(context.Flow.Id);
            return Task.FromResult(new CriticalE2EStepResult { StepId = step.StepId, Status = CriticalE2EStatus.Passed, IsFinalAssertion = step.IsFinalAssertion });
        }
    }

    private static (CriticalE2EService Service, BrowserCompanionService Companion, RecordingExecutor Executor, CriticalE2EStore Store) Service()
    {
        var companion = new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), TimeProvider.System, NullLogger<BrowserCompanionService>.Instance);
        var store = new CriticalE2EStore(Path.Combine(Path.GetTempPath(), "birknext-e2e-kind-" + Guid.NewGuid().ToString("N")[..8]), NullLogger<CriticalE2EStore>.Instance);
        var executor = new RecordingExecutor();
        var service = new CriticalE2EService(store, companion, new NoGateway(), new CriticalE2ERunner([executor], TimeProvider.System, NullLogger<CriticalE2ERunner>.Instance),
            TimeProvider.System, NullLogger<CriticalE2EService>.Instance);
        return (service, companion, executor, store);
    }

    private static string Pair(BrowserCompanionService companion, params string[] pages)
    {
        var challenge = companion.StartPairing(new BrowserCompanionPairingStartRequest("dev", "M2LB DEV", "Development", [Origin]));
        var session = companion.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).SessionId!;
        companion.Heartbeat(new BrowserCompanionHeartbeat(session, "dev", null, null, "0.1.0",
            pages.Select(p => new BrowserCompanionLivePageReport(p, Origin, "/admin/general-roles", p.Split('-')[1])).ToList(), [CompanionCapabilities.ElementPick]), Extension);
        return session;
    }

    private static CriticalE2EOverviewRequest Request() => new() { ProfileId = "dev", EnvironmentId = "env-1", EnvironmentName = "M2LB DEV", EnvironmentType = "Development" };

    [Fact]
    public void TheAttendedSnapshotReadsTheCompanionsLiveSession()
    {
        var (service, companion, _, _) = Service();
        service.Overview(Request()).Attended.Status.Message.Should().Be("The Browser Companion is not connected.");

        Pair(companion, "t1-a");
        var ready = service.Overview(Request()).Attended;
        ready.Status.State.Should().Be(CriticalE2EEngineState.Ready);
        ready.CompanionConnected.Should().BeTrue();
        ready.OpenApprovedPages.Should().Be(1);
        ready.CurrentOrigin.Should().Be(Origin);
        ready.CurrentRoute.Should().Be("/admin/general-roles");
        ready.ElementPickSupported.Should().BeTrue();
    }

    [Fact]
    public void TwoOpenPagesAreAPreciseBlocker()
    {
        var (service, companion, _, _) = Service();
        Pair(companion, "t1-a", "t2-b");
        var attended = service.Overview(Request()).Attended;
        attended.Status.State.Should().Be(CriticalE2EEngineState.RequiresBrowserSession);
        attended.Status.Message.Should().Be("2 approved application pages are open. Keep exactly one approved target page open.");
        attended.OpenApprovedPages.Should().Be(2);
    }

    [Fact]
    public void ProductionIsUnavailableNotSomethingToSetUp()
    {
        var (service, _, _, _) = Service();
        service.Overview(Request() with { EnvironmentType = "Production" }).Attended.Status.State.Should().Be(CriticalE2EEngineState.Unavailable);
    }

    [Fact]
    public async Task ABatchRunsCriticalFlowsOnly_ADiagnosticFlowStillRunsOnItsOwn()
    {
        var (service, companion, executor, store) = Service();
        Pair(companion, "t1-a");
        store.Save(Flow("critical", "M02"));
        store.Save(Flow("smoke", "SMOKE", CriticalE2EFlowKind.Diagnostic));

        await service.RunAsync(new CriticalE2ERunFlowRequest { Context = Request(), Mode = CriticalE2EExecutionMode.CompanionBrowser }, CancellationToken.None);
        executor.Flows.Should().Equal("critical");

        await service.RunAsync(new CriticalE2ERunFlowRequest { Context = Request(), FlowId = "smoke" }, CancellationToken.None);
        executor.Flows.Should().Equal("critical", "smoke");
    }

    [Fact]
    public async Task APickTimeoutIsItsOwnOutcome()
    {
        var (service, companion, _, _) = Service();
        var session = Pair(companion, "t1-a");
        var picking = service.PickElementAsync(new CriticalE2EElementPickRequest { ProfileId = "dev", EnvironmentType = "Development" }, CancellationToken.None);
        var claimed = companion.Heartbeat(new BrowserCompanionHeartbeat(session, "dev", null, null, "0.1.0",
            [new BrowserCompanionLivePageReport("t1-a", Origin, "/", "a")], [CompanionCapabilities.ElementPick]), Extension).PendingCommand!;
        companion.CompleteCommand(new CompanionAutomationResultEnvelope
        {
            SessionId = session, ProfileId = "dev", ExtensionVersion = "0.1.0",
            Result = new CompanionAutomationResult { CommandId = claimed.CommandId, Status = CriticalE2EStatus.Blocked, SanitizedError = "No element was selected before the picker timed out." },
        }, Extension);

        var result = await picking;
        result.Outcome.Should().Be(CriticalE2EPickOutcome.TimedOut);
        result.Status.Should().Be(CriticalE2EStatus.Blocked);
    }

    [Fact]
    public async Task AnOlderExtensionIsUnsupported_NotBlocked()
    {
        var (service, companion, _, _) = Service();
        var challenge = companion.StartPairing(new BrowserCompanionPairingStartRequest("dev", "M2LB DEV", "Development", [Origin]));
        var session = companion.CompletePairing(new BrowserCompanionPairRequest(challenge.PairingCode, "0.1.0"), Extension).SessionId!;
        companion.Heartbeat(new BrowserCompanionHeartbeat(session, "dev", null, null, "0.1.0", [new BrowserCompanionLivePageReport("t1-a", Origin, "/", "a")]), Extension);

        var result = await service.PickElementAsync(new CriticalE2EElementPickRequest { ProfileId = "dev", EnvironmentType = "Development" }, CancellationToken.None);
        result.Outcome.Should().Be(CriticalE2EPickOutcome.Unsupported);
    }
}
