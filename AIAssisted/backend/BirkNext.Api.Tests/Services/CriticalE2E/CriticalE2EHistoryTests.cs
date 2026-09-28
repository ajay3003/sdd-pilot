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
/// Archive old runs vs clear test history. Archive is reversible and view-only — it never changes a result, the latest
/// run, coverage or release evidence. Clear is destructive, flow-scoped, needs the typed confirmation and refuses while
/// the flow holds build-linked release evidence. Neither runs anything or touches the companion.
/// </summary>
public sealed class CriticalE2EHistoryTests : IDisposable
{
    private const string Env = "env-1";
    private const string Build = "build-42";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-e2e-history-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CriticalE2EStore _store;
    private readonly RecordingCompanion _companion;
    private readonly RecordingExecutor _executor = new();
    private readonly CriticalE2EService _service;
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    public CriticalE2EHistoryTests()
    {
        _store = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
        _companion = new RecordingCompanion(new BrowserCompanionService(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), TimeProvider.System, NullLogger<BrowserCompanionService>.Instance));
        _service = new CriticalE2EService(_store, _companion, new NoGateway(),
            new CriticalE2ERunner([_executor], TimeProvider.System, NullLogger<CriticalE2ERunner>.Instance), TimeProvider.System, NullLogger<CriticalE2EService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class NoGateway : IAuthenticatedReviewGateway
    {
        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) => new();
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity i, string m, string u, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity i, string e, string q, CancellationToken ct = default) => Task.FromResult(new AuthenticatedReviewExecutionOutcome());
        public Task<AuthenticatedGraphQlSchemaOutcome> FetchGraphQlSchemaAsync(AuthenticatedReviewIdentity i, string e, CancellationToken ct = default) => Task.FromResult(new AuthenticatedGraphQlSchemaOutcome());
    }

    private sealed class RecordingExecutor : ICriticalE2EStepExecutor
    {
        public int Executed { get; private set; }
        public bool CanExecute(CriticalE2EStepDefinition step) => true;
        public Task<CriticalE2EStepResult> ExecuteAsync(CriticalE2EStepDefinition step, CriticalE2ERunContext context, CancellationToken cancellationToken)
        {
            Executed++;
            return Task.FromResult(new CriticalE2EStepResult { StepId = step.StepId, Status = CriticalE2EStatus.Passed, IsFinalAssertion = step.IsFinalAssertion });
        }
    }

    /// <summary>Records the calls that would change the companion's behaviour; reads pass through.</summary>
    private sealed class RecordingCompanion(IBrowserCompanionService inner) : IBrowserCompanionService
    {
        public List<string> SideEffects { get; } = [];
        public BrowserCompanionPairingChallenge StartPairing(BrowserCompanionPairingStartRequest request) => inner.StartPairing(request);
        public BrowserCompanionPairResult CompletePairing(BrowserCompanionPairRequest request, string extensionOrigin) => inner.CompletePairing(request, extensionOrigin);
        public BrowserCompanionAcceptResult Heartbeat(BrowserCompanionHeartbeat heartbeat, string extensionOrigin) => inner.Heartbeat(heartbeat, extensionOrigin);
        public BrowserCompanionAcceptResult AcceptEvidence(BrowserCompanionEvidenceEnvelope envelope, string extensionOrigin) => inner.AcceptEvidence(envelope, extensionOrigin);
        public BrowserCompanionStatus Status(string profileId) => inner.Status(profileId);
        public BrowserCompanionStatus Unpair(string profileId) { SideEffects.Add("unpair"); return inner.Unpair(profileId); }
        public BrowserCompanionPairResult ValidateSession(string sessionId, string profileId, string extensionOrigin) => inner.ValidateSession(sessionId, profileId, extensionOrigin);
        public void OpenAutomationWindow(string profileId) { SideEffects.Add("automation-window"); inner.OpenAutomationWindow(profileId); }
        public CompanionCommandDispatchResult Dispatch(CompanionAutomationCommand command) { SideEffects.Add("dispatch"); return inner.Dispatch(command); }
        public Task<CompanionAutomationResult> AwaitResultAsync(string commandId, CancellationToken cancellationToken) => inner.AwaitResultAsync(commandId, cancellationToken);
        public void CancelCommand(string commandId, string reason) { SideEffects.Add("cancel"); inner.CancelCommand(commandId, reason); }
        public BrowserCompanionAcceptResult CompleteCommand(CompanionAutomationResultEnvelope envelope, string extensionOrigin) => inner.CompleteCommand(envelope, extensionOrigin);
    }

    private CriticalE2EFlowDefinition Flow(string id, string module = "M02", bool required = true, CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical) =>
        _store.Save(new CriticalE2EFlowDefinition
        {
            Id = id, Module = module, Name = $"Flow {id}", Kind = kind, Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = Env,
            Enabled = true, RequiredForRelease = required,
            Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible,
                Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "status" }, IsFinalAssertion = true }],
        });

    private CriticalE2ERunResult Run(string flowId, int minute, CriticalE2EStatus status, string? build = null, bool required = true)
    {
        var run = new CriticalE2ERunResult
        {
            RunId = $"{flowId}-run-{minute}", FlowId = flowId, FlowName = $"Flow {flowId}", Module = "M02", EnvironmentId = Env, ProfileId = "dev",
            StartedAt = T0.AddMinutes(minute), CompletedAt = T0.AddMinutes(minute).AddSeconds(30), DurationMs = 30_000, Status = status,
            FailureReason = status == CriticalE2EStatus.Failed ? "Setup: selector not found." : null, RequiredForRelease = required, BuildId = build,
            StepResults = [new CriticalE2EStepResult { StepId = "s1", Status = status, IsFinalAssertion = true }],
        };
        _store.Record(run);
        return run;
    }

    private static CriticalE2EOverviewRequest Context(string? build = null) =>
        new() { ProfileId = "dev", EnvironmentId = Env, EnvironmentName = "M2LB DEV", EnvironmentType = "Development", BuildId = build };

    private CriticalE2EHistoryActionResult Archive(string flowId, CriticalE2EArchiveSelection selection = CriticalE2EArchiveSelection.AllExceptLatest, string? build = null, params string[] runIds) =>
        _service.ArchiveRuns(new CriticalE2EArchiveRequest { Context = Context(build), FlowId = flowId, Selection = selection, RunIds = [.. runIds] });

    private CriticalE2EHistoryActionResult Clear(string flowId, string confirmation = "CLEAR", bool includeArchived = true, string? build = null) =>
        _service.ClearHistory(new CriticalE2EClearRequest { Context = Context(build), FlowId = flowId, Confirmation = confirmation, IncludeArchived = includeArchived });

    private CriticalE2ERunResult Stored(string runId) => _store.FlowHistory(runId.Split("-run-")[0]).Single(r => r.RunId == runId);

    // ── Archive ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveKeepsTheLatestRun_AndMarksTheOthersWithoutChangingThem()
    {
        Flow("gradert");
        var failed1 = Run("gradert", 1, CriticalE2EStatus.Failed);
        var failed2 = Run("gradert", 2, CriticalE2EStatus.Failed);
        var passed = Run("gradert", 3, CriticalE2EStatus.Passed);

        var result = Archive("gradert");

        result.Changed.Should().Be(2);
        Stored(passed.RunId).Archived.Should().BeFalse("the latest run is never archived by the bulk action");
        foreach (var original in new[] { failed1, failed2 })
        {
            var stored = Stored(original.RunId);
            stored.Archived.Should().BeTrue();
            stored.ArchiveReason.Should().Be("Archived: all runs except the latest");
            stored.Should().BeEquivalentTo(original, o => o.Excluding(r => r.ArchivedAt).Excluding(r => r.ArchiveReason).Excluding(r => r.Archived),
                "status, steps, timestamps, failure reason and build stay exactly as recorded");
        }
    }

    [Fact]
    public void ArchiveChangesNeitherTheFlowSummaryNorCoverageNorTheReleaseVerdict()
    {
        Flow("gradert");
        Run("gradert", 1, CriticalE2EStatus.Failed, Build);
        Run("gradert", 2, CriticalE2EStatus.Passed, Build);
        Run("gradert", 3, CriticalE2EStatus.Failed, "build-43");
        var before = _service.Overview(Context(Build));

        Archive("gradert");
        var after = _service.Overview(Context(Build));

        after.Flows.Should().BeEquivalentTo(before.Flows);
        after.Modules.Should().BeEquivalentTo(before.Modules);
        after.Release.Should().BeEquivalentTo(before.Release);
        after.Release.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);
    }

    [Fact]
    public void ArchiveNeverSelectsTheCurrentReleaseEvidence_AndSaysSoWhenNamed()
    {
        Flow("gradert");
        var evidence = Run("gradert", 1, CriticalE2EStatus.Passed, Build);
        var later = Run("gradert", 2, CriticalE2EStatus.Failed);   // a later setup run with no build
        var older = Run("gradert", 0, CriticalE2EStatus.Failed, Build);

        Archive("gradert", build: Build).Changed.Should().Be(1);
        Stored(evidence.RunId).Archived.Should().BeFalse("it is what the release verdict for build-42 reads");
        Stored(later.RunId).Archived.Should().BeFalse("it is the latest run");
        Stored(older.RunId).Archived.Should().BeTrue();

        var named = Archive("gradert", CriticalE2EArchiveSelection.Selected, Build, evidence.RunId, later.RunId);
        named.Changed.Should().Be(0);
        named.Skipped.Should().BeEquivalentTo(
            $"{evidence.RunId}: it is the current release evidence for this build",
            $"{later.RunId}: the latest run is kept");
    }

    [Fact]
    public void ArchiveNotOnCurrentBuildKeepsTheBuildsRuns_AndNeedsANamedBuild()
    {
        Flow("gradert");
        var other = Run("gradert", 1, CriticalE2EStatus.Failed, "build-41");
        var none = Run("gradert", 2, CriticalE2EStatus.Failed);
        var onBuildOlder = Run("gradert", 3, CriticalE2EStatus.Failed, Build);
        Run("gradert", 4, CriticalE2EStatus.Passed, Build);

        Archive("gradert", CriticalE2EArchiveSelection.NotOnCurrentBuild, Build).Changed.Should().Be(2);
        Stored(other.RunId).Archived.Should().BeTrue();
        Stored(none.RunId).Archived.Should().BeTrue();
        Stored(onBuildOlder.RunId).Archived.Should().BeFalse("runs on the current build are not old for this option");

        var act = () => Archive("gradert", CriticalE2EArchiveSelection.NotOnCurrentBuild, build: null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ArchiveIsScopedToTheFlow()
    {
        Flow("a"); Flow("b");
        Run("a", 1, CriticalE2EStatus.Failed); Run("a", 2, CriticalE2EStatus.Passed);
        var b1 = Run("b", 1, CriticalE2EStatus.Failed); Run("b", 2, CriticalE2EStatus.Passed);

        Archive("a", CriticalE2EArchiveSelection.Selected, null, "a-run-1", b1.RunId).Changed.Should().Be(1);
        Stored(b1.RunId).Archived.Should().BeFalse("a run of another flow is never touched");
    }

    [Fact]
    public void RestoreBringsARunBack_AndItsResultIsUnchanged()
    {
        Flow("gradert");
        var failed = Run("gradert", 1, CriticalE2EStatus.Failed);
        Run("gradert", 2, CriticalE2EStatus.Passed);
        Archive("gradert");

        var restored = _service.RestoreRuns(new CriticalE2ERestoreRequest { Context = Context(), RunIds = [failed.RunId] });

        restored.Changed.Should().Be(1);
        Stored(failed.RunId).Should().BeEquivalentTo(failed, "restoring removes the marker and nothing else");
        _service.RestoreRuns(new CriticalE2ERestoreRequest { Context = Context(), RunIds = [failed.RunId] }).Changed.Should().Be(0);
    }

    [Fact]
    public void ArchiveStateSurvivesARestart_AndOlderHistoryReadsAsActive()
    {
        Flow("gradert");
        Run("gradert", 1, CriticalE2EStatus.Failed);
        Run("gradert", 2, CriticalE2EStatus.Passed);
        Archive("gradert");

        var reloaded = new CriticalE2EStore(_dir, NullLogger<CriticalE2EStore>.Instance);
        reloaded.FlowHistory("gradert").Select(r => (r.RunId, r.Archived)).Should().BeEquivalentTo(new[] { ("gradert-run-2", false), ("gradert-run-1", true) });

        var legacy = System.Text.Json.JsonSerializer.Deserialize<CriticalE2ERunResult>("""{"RunId":"old","FlowId":"f","Status":"Failed"}""")!;
        legacy.Archived.Should().BeFalse();
        legacy.Status.Should().Be(CriticalE2EStatus.Failed);
    }

    // ── Overview counts ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOverviewCarriesArchivedRunsSeparately_AndCountsThem()
    {
        Flow("gradert");
        for (var i = 0; i < 5; i++) Run("gradert", i, CriticalE2EStatus.Failed);
        Run("gradert", 10, CriticalE2EStatus.Passed);
        Archive("gradert");

        var overview = _service.Overview(Context());
        overview.ArchivedRunCount.Should().Be(5);
        overview.History.Should().HaveCount(6);
        overview.History.Count(r => !r.Archived).Should().Be(1);
        overview.History.Should().BeInDescendingOrder(r => r.StartedAt);
    }

    [Fact]
    public void ThePreviewCountsWhatEachActionWouldTouch()
    {
        Flow("gradert");
        Run("gradert", 1, CriticalE2EStatus.Failed);
        Run("gradert", 2, CriticalE2EStatus.Failed, "build-41");
        Run("gradert", 3, CriticalE2EStatus.Passed, Build);
        Run("gradert", 4, CriticalE2EStatus.Failed);
        Archive("gradert", CriticalE2EArchiveSelection.Selected, null, "gradert-run-1");

        var preview = _service.HistoryPreview(new CriticalE2EHistoryPreviewRequest { Context = Context(Build), FlowId = "gradert" });

        preview.FlowName.Should().Be("Flow gradert");
        preview.Module.Should().Be("M02");
        preview.ActiveRuns.Should().Be(3);
        preview.ArchivedRuns.Should().Be(1);
        preview.BuildLinkedRuns.Should().Be(2);
        preview.ReleaseEvidenceRuns.Should().Be(2);
        preview.ActiveReleaseEvidenceRuns.Should().Be(2);
        preview.LatestRunId.Should().Be("gradert-run-4");
        preview.CurrentEvidenceRunId.Should().Be("gradert-run-3");
        preview.ArchiveCandidates.Should().Be(1, "run-2 only: run-4 is latest and run-3 is the current evidence");
        preview.ArchiveCandidatesNotOnCurrentBuild.Should().Be(1);
        preview.ClearBlocked.Should().BeTrue();
        preview.ClearBlockedReason.Should().Be(CriticalE2EHistoryRules.ClearBlockedReason);

        _service.HistoryPreview(new CriticalE2EHistoryPreviewRequest { Context = Context(), FlowId = "gradert" })
            .ArchiveCandidatesNotOnCurrentBuild.Should().BeNull("with no build named the build-aware option does not apply");
    }

    // ── Clear ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClearRemovesOnlyThisFlowsRuns_AndTheFlowReadsNotRun()
    {
        Flow("setup", required: false); Flow("other", required: false);
        Run("setup", 1, CriticalE2EStatus.Failed); Run("setup", 2, CriticalE2EStatus.Passed);
        var other = Run("other", 1, CriticalE2EStatus.Passed);
        Archive("setup");

        var result = Clear("setup");

        result.Blocked.Should().BeFalse();
        result.Changed.Should().Be(2, "archived runs are included by default");
        _store.FlowHistory("setup").Should().BeEmpty();
        _store.FlowHistory("other").Should().ContainSingle().Which.Should().BeEquivalentTo(other);
        var summary = result.Overview.Flows.Single(f => f.FlowId == "setup");
        summary.LastStatus.Should().Be(CriticalE2EStatus.NotRun);
        summary.LastRunAt.Should().BeNull();
        result.Overview.History.Should().OnlyContain(r => r.FlowId == "other");
        _store.Flow("setup").Should().NotBeNull("clearing history keeps the flow definition");
    }

    [Fact]
    public void ClearCanLeaveArchivedRunsInPlace()
    {
        Flow("setup", required: false);
        var archived = Run("setup", 1, CriticalE2EStatus.Failed);
        Run("setup", 2, CriticalE2EStatus.Passed);
        Archive("setup");

        Clear("setup", includeArchived: false).Changed.Should().Be(1);
        _store.FlowHistory("setup").Should().ContainSingle().Which.RunId.Should().Be(archived.RunId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("clear")]
    [InlineData("CLEAR ALL")]
    public void ClearNeedsTheExactTypedConfirmation(string confirmation)
    {
        Flow("setup", required: false);
        Run("setup", 1, CriticalE2EStatus.Failed);

        var result = Clear("setup", confirmation);

        result.Blocked.Should().BeTrue();
        result.Changed.Should().Be(0);
        result.Message.Should().Be("Type CLEAR to confirm.");
        _store.FlowHistory("setup").Should().ContainSingle();
    }

    [Fact]
    public void ClearIsRefusedWhileTheFlowHoldsReleaseEvidence_EvenArchived()
    {
        Flow("gradert");
        Run("gradert", 1, CriticalE2EStatus.Passed, Build);
        Run("gradert", 2, CriticalE2EStatus.Failed);
        Archive("gradert", CriticalE2EArchiveSelection.Selected, null, "gradert-run-1");   // not the latest, so archivable
        var before = _service.Overview(Context(Build)).Release;

        var result = Clear("gradert", build: Build);

        result.Blocked.Should().BeTrue();
        result.Message.Should().Be("This flow has build-linked release evidence, so its history cannot be cleared. Archive old runs instead.");
        _store.FlowHistory("gradert").Should().HaveCount(2);
        result.Overview.Release.Should().BeEquivalentTo(before);

        // Leaving the archived evidence alone is a clear the rules allow: only the unlinked setup run goes.
        Clear("gradert", includeArchived: false, build: Build).Changed.Should().Be(1);
        _store.FlowHistory("gradert").Should().ContainSingle().Which.BuildId.Should().Be(Build);
    }

    [Fact]
    public void ClearingANonEvidenceFlowRecalculatesCoverage()
    {
        Flow("m03", module: "M03", required: false);
        Run("m03", 1, CriticalE2EStatus.Passed, Build, required: false);
        var before = _service.Overview(Context(Build)).Modules.Single(m => m.Module == "M03").Flows.Single();
        before.LastResultMatchesRelease.Should().BeTrue();

        var after = Clear("m03", build: Build).Overview.Modules.Single(m => m.Module == "M03").Flows.Single();

        after.LastStatus.Should().Be(CriticalE2EStatus.NotRun);
        after.LastResultMatchesRelease.Should().BeFalse();
    }

    [Fact]
    public void ARunOfAnotherBuildIsStillReleaseEvidence_ForItsBuild()
    {
        Flow("gradert");
        Run("gradert", 1, CriticalE2EStatus.Passed, "build-41");

        Clear("gradert", build: Build).Blocked.Should().BeTrue("build-41's evidence is not disposable because another build is selected");
    }

    [Fact]
    public void AnUnknownFlowOrAnotherEnvironmentsFlowIsRejected()
    {
        _store.Save(Flow("elsewhere") with { EnvironmentId = "env-2" });

        ((Action)(() => Clear("missing"))).Should().Throw<ArgumentException>();
        ((Action)(() => Clear("elsewhere"))).Should().Throw<ArgumentException>();
        ((Action)(() => Archive("elsewhere"))).Should().Throw<ArgumentException>();
    }

    // ── No execution, no companion ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HistoryActionsNeverRunAFlowOrTouchTheCompanion()
    {
        Flow("setup", required: false);
        Run("setup", 1, CriticalE2EStatus.Failed); Run("setup", 2, CriticalE2EStatus.Passed);

        _service.HistoryPreview(new CriticalE2EHistoryPreviewRequest { Context = Context(), FlowId = "setup" });
        Archive("setup");
        _service.RestoreRuns(new CriticalE2ERestoreRequest { Context = Context(), RunIds = ["setup-run-1"] });
        Clear("setup", "wrong");
        Clear("setup");

        _companion.SideEffects.Should().BeEmpty("managing history is not a signal that a run is coming");
        _executor.Executed.Should().Be(0);

        _service.Overview(Context());
        _companion.SideEffects.Should().Equal("automation-window");   // the page-open overview still does, as before
    }
}
