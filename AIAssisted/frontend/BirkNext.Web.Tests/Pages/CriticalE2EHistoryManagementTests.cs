using BirkNext.CriticalE2E;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Run history management on the Critical E2E page: Archive old runs (reversible, hidden by default, restorable) and
/// Clear test history (destructive, one flow, typed confirmation, refused for release evidence). Both live under Run
/// history, and the dialogs say what will and will not change before anything does.
/// </summary>
public sealed class CriticalE2EHistoryManagementTests : BunitContext
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private readonly BunitJSModuleInterop _dialogJs;

    public CriticalE2EHistoryManagementTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _dialogJs = JSInterop.SetupModule("./js/historyDialog.js");
    }

    private sealed class StubApi(CriticalE2EOverview overview) : ICriticalE2EApiService
    {
        public CriticalE2EOverview Current { get; set; } = overview;
        public CriticalE2EHistoryPreview Preview { get; set; } = new();
        public Func<CriticalE2EHistoryActionResult>? NextResult { get; set; }
        public List<CriticalE2EArchiveRequest> Archives { get; } = [];
        public List<CriticalE2ERestoreRequest> Restores { get; } = [];
        public List<CriticalE2EClearRequest> Clears { get; } = [];
        public int Runs { get; private set; }

        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => Task.FromResult(Current);
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) { Runs++; return Task.FromResult(new CriticalE2ERunBatchResult { Overview = Current }); }
        public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default) => Task.FromResult(new CriticalE2EElementPickResult());
        public Task<CriticalE2EHistoryPreview> HistoryPreviewAsync(CriticalE2EHistoryPreviewRequest request, CancellationToken ct = default) => Task.FromResult(Preview);
        public Task<CriticalE2EHistoryActionResult> ArchiveRunsAsync(CriticalE2EArchiveRequest request, CancellationToken ct = default) { Archives.Add(request); return Task.FromResult(Result()); }
        public Task<CriticalE2EHistoryActionResult> RestoreRunsAsync(CriticalE2ERestoreRequest request, CancellationToken ct = default) { Restores.Add(request); return Task.FromResult(Result()); }
        public Task<CriticalE2EHistoryActionResult> ClearHistoryAsync(CriticalE2EClearRequest request, CancellationToken ct = default) { Clears.Add(request); return Task.FromResult(Result()); }
        private CriticalE2EHistoryActionResult Result() => NextResult?.Invoke() ?? new CriticalE2EHistoryActionResult { Changed = 1, Message = "Done.", Overview = Current };
    }

    private sealed class StubContextFactory : IFrontendAnalysisContextFactory
    {
        public Task<FrontendAnalysisContext> GetActiveContextAsync() => Task.FromResult(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development },
        });
    }

    private static CriticalE2EFlowSummary Flow(string id, string name, CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical,
        CriticalE2EStatus status = CriticalE2EStatus.Passed) => new()
        {
            FlowId = id, Name = name, Module = kind == CriticalE2EFlowKind.Diagnostic ? "SMOKE" : "M02", Kind = kind, Mode = CriticalE2EExecutionMode.CompanionBrowser,
            Enabled = true, RequiredForRelease = kind == CriticalE2EFlowKind.Critical, Configured = true, LastStatus = status, StepCount = 3,
            LastRunAt = status == CriticalE2EStatus.NotRun ? null : T0.AddMinutes(3),
        };

    private static CriticalE2ERunResult Run(string id, string flowId, int minute, CriticalE2EStatus status, bool archived = false) => new()
    {
        RunId = id, FlowId = flowId, FlowName = flowId == "gradert" ? "Gradert tilgang" : flowId, Module = "M02", Status = status, StartedAt = T0.AddMinutes(minute),
        ArchivedAt = archived ? T0.AddHours(1) : null, ArchiveReason = archived ? "Archived: all runs except the latest" : null,
        StepResults = [new CriticalE2EStepResult { StepId = "s1", Status = status, Description = "Assert visible" }],
    };

    private static CriticalE2EOverview Overview(List<CriticalE2ERunResult> history, params CriticalE2EFlowSummary[] flows) => new()
    {
        EnvironmentId = "dev", EnvironmentName = "M2LB DEV",
        Release = new CriticalE2EReleaseStatus { Disposition = CriticalE2EReleaseDisposition.NotEvaluated, ModulesTotal = 1, ModulesCovered = 1 },
        Flows = flows.Length > 0 ? [.. flows] : [Flow("gradert", "Gradert tilgang"), Flow("smoke", "Smoke", CriticalE2EFlowKind.Diagnostic)],
        Attended = new CriticalE2EAttendedReadiness { Status = new CriticalE2EEngineStatus { State = CriticalE2EEngineState.Ready } },
        History = history,
        ArchivedRunCount = history.Count(r => r.Archived),
    };

    /// <summary>Gradert tilgang: two failed setup runs, then the latest one passed. One smoke run.</summary>
    private static List<CriticalE2ERunResult> SetupHistory() =>
    [
        Run("g1", "gradert", 1, CriticalE2EStatus.Failed), Run("g2", "gradert", 2, CriticalE2EStatus.Failed),
        Run("g3", "gradert", 3, CriticalE2EStatus.Passed), Run("s1", "smoke", 4, CriticalE2EStatus.Passed),
    ];

    private static List<CriticalE2ERunResult> ArchivedHistory() =>
    [
        Run("g1", "gradert", 1, CriticalE2EStatus.Failed, archived: true), Run("g2", "gradert", 2, CriticalE2EStatus.Failed, archived: true),
        Run("g3", "gradert", 3, CriticalE2EStatus.Passed), Run("s1", "smoke", 4, CriticalE2EStatus.Passed),
    ];

    private static readonly CriticalE2EHistoryPreview GradertPreview = new()
    {
        FlowId = "gradert", FlowName = "Gradert tilgang", Module = "M02", ActiveRuns = 3, ArchivedRuns = 0, BuildLinkedRuns = 0,
        ReleaseEvidenceRuns = 0, LatestRunId = "g3", ArchiveCandidates = 2,
    };

    private IRenderedComponent<CriticalE2ERegression> Render(CriticalE2EOverview overview, out StubApi api)
    {
        api = new StubApi(overview) { Preview = GradertPreview };
        Services.AddSingleton<ICriticalE2EApiService>(api);
        Services.AddSingleton<IFrontendAnalysisContextFactory>(new StubContextFactory());
        var page = base.Render<CriticalE2ERegression>();
        page.Find("[data-testid=e2e-history-toggle]").Click();
        return page;
    }

    private static string Text(IRenderedComponent<CriticalE2ERegression> page, string testId) => page.Find($"[data-testid={testId}]").TextContent.Trim();
    private static List<string> RowIds(IRenderedComponent<CriticalE2ERegression> page) =>
        page.FindAll("tr[data-testid^=e2e-run-]").Select(r => r.GetAttribute("data-testid")!["e2e-run-".Length..]).ToList();

    private static void ChooseFlow(IRenderedComponent<CriticalE2ERegression> page, string flowId) =>
        page.Find("[data-testid=e2e-history-flow]").Change(flowId);

    // ── Placement and wording ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheActionsLiveUnderRunHistory_AndNeedAFlow()
    {
        var page = Render(Overview(SetupHistory()), out _);

        page.Find("[data-testid=e2e-flows]").QuerySelector("[data-testid=e2e-archive-open]").Should().BeNull("history actions are not run actions");
        page.Find("[data-testid=e2e-history]").QuerySelector("[data-testid=e2e-archive-open]").Should().NotBeNull();
        page.Find("[data-testid=e2e-archive-open]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=e2e-clear-open]").HasAttribute("disabled").Should().BeTrue();
        Text(page, "e2e-history-explain").Should().StartWith("Choose a flow to archive or clear its history.")
            .And.Contain("Archive hides old runs from this list and keeps them")
            .And.Contain("Clear permanently deletes this flow's runs");

        ChooseFlow(page, "gradert");
        page.Find("[data-testid=e2e-archive-open]").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-testid=e2e-clear-open]").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-testid=e2e-clear-open]").TextContent.Should().Contain("Clear test history", "destructive is said in words, not only colour");
        RowIds(page).Should().Equal("g3", "g2", "g1");
    }

    // ── Archive ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArchiveOldRuns_KeepsTheLatestPassedRun_AndHidesTheArchivedOnes()
    {
        var page = Render(Overview(SetupHistory()), out var api);
        ChooseFlow(page, "gradert");
        page.Find("[data-testid=e2e-archive-open]").Click();

        var dialog = page.Find("[data-testid=e2e-archive-dialog]");
        dialog.GetAttribute("aria-labelledby").Should().Be("e2e-history-dialog-title");
        page.Find("#e2e-history-dialog-title").TextContent.Should().Be("Archive old runs — Gradert tilgang");
        page.Find("#" + dialog.GetAttribute("aria-describedby")).TextContent.Should().Contain("can be restored");
        page.Find("[data-testid=e2e-archive-all-except-latest]").HasAttribute("checked").Should().BeTrue("all runs except the latest is the default");
        page.FindAll("[data-testid=e2e-archive-not-on-build]").Should().BeEmpty("no build is named");
        Text(page, "e2e-archive-kept").Should().Contain("The latest run is kept.");
        _dialogJs.VerifyInvoke("open");

        api.Current = Overview(ArchivedHistory());
        page.Find("[data-testid=e2e-archive-confirm]").TextContent.Trim().Should().Be("Archive 2 runs");
        page.Find("[data-testid=e2e-archive-confirm]").Click();

        api.Archives.Should().ContainSingle().Which.Should().Match<CriticalE2EArchiveRequest>(r => r.FlowId == "gradert" && r.Selection == CriticalE2EArchiveSelection.AllExceptLatest);
        page.FindAll("[data-testid=e2e-archive-dialog]").Should().BeEmpty();
        _dialogJs.VerifyInvoke("close");
        RowIds(page).Should().Equal("g3");
        Text(page, "e2e-status-gradert").Should().Be("Passed", "archive never changes the latest result");
        Text(page, "e2e-history-counts").Should().Be("1 recent run · 2 archived");
        Text(page, "e2e-history-status").Should().Be("Done.");
    }

    [Fact]
    public void ShowArchivedRuns_MarksThemBesideTheirOriginalResult_AndRestoreBringsOneBack()
    {
        var page = Render(Overview(ArchivedHistory()), out var api);
        RowIds(page).Should().Equal(new[] { "g3" }, "archived runs are hidden by default");

        page.Find("[data-testid=e2e-show-archived-runs]").Change(true);
        RowIds(page).Should().Equal("g3", "g2", "g1");
        page.Find("[data-testid=e2e-run-g1]").GetAttribute("data-archived").Should().Be("true");
        Text(page, "e2e-run-result-g1").Should().MatchRegex(@"^Archived ·\s+Failed$");
        Text(page, "e2e-run-result-g3").Should().Be("Passed");

        page.Find("[data-testid=e2e-restore-g1]").Click();
        api.Restores.Should().ContainSingle().Which.RunIds.Should().Equal("g1");
        api.Runs.Should().Be(0, "restoring runs nothing");
    }

    [Fact]
    public void RowArchiveIsOfferedForOlderRunsOnly()
    {
        var page = Render(Overview(SetupHistory()), out var api);

        page.FindAll("[data-testid=e2e-archive-run-g3]").Should().BeEmpty("the latest run of a flow is never archived");
        page.FindAll("[data-testid=e2e-archive-run-s1]").Should().BeEmpty();
        page.Find("[data-testid=e2e-archive-run-g1]").Click();

        api.Archives.Should().ContainSingle().Which.Should().Match<CriticalE2EArchiveRequest>(r =>
            r.FlowId == "gradert" && r.Selection == CriticalE2EArchiveSelection.Selected && r.RunIds.SequenceEqual(new[] { "g1" }));
    }

    [Fact]
    public void TheCurrentReleaseEvidenceRunIsNotOfferedForArchive_EvenWhenALaterRunExists()
    {
        var history = new List<CriticalE2ERunResult>
        {
            Run("g1", "gradert", 1, CriticalE2EStatus.Failed) with { BuildId = "RC-57", RequiredForRelease = true },
            Run("g2", "gradert", 2, CriticalE2EStatus.Passed) with { BuildId = "RC-57", RequiredForRelease = true },
            Run("g3", "gradert", 3, CriticalE2EStatus.Failed),
        };
        var rows = CriticalE2EPresentation.History(Overview(history), "RC-57", includeDiagnostic: true);

        rows.Select(r => (r.Run.RunId, r.Archivable, r.ReleaseEvidence)).Should().Equal(
            ("g3", false, false),   // latest
            ("g2", false, true),    // what the verdict for RC-57 reads
            ("g1", true, true));
        CriticalE2EPresentation.History(Overview(history), null, true).Single(r => r.Run.RunId == "g2").Archivable.Should().BeTrue("with no build named it is not current evidence");
    }

    [Fact]
    public void TheArchivedAndSmokeFiltersAreIndependent_AndTheCountsFollowThem()
    {
        var history = ArchivedHistory();
        history.Add(Run("s0", "smoke", 0, CriticalE2EStatus.Passed, archived: true));
        var page = Render(Overview(history), out _);

        Text(page, "e2e-history-counts").Should().Be("1 recent run · 3 archived · 1 smoke/diagnostic hidden");
        RowIds(page).Should().Equal("g3");

        page.Find("[data-testid=e2e-show-diagnostic-runs]").Change(true);
        RowIds(page).Should().Equal("s1", "g3");
        Text(page, "e2e-history-counts").Should().Be("2 recent runs · 3 archived");

        page.Find("[data-testid=e2e-show-diagnostic-runs]").Change(false);
        page.Find("[data-testid=e2e-show-archived-runs]").Change(true);
        RowIds(page).Should().Equal(new[] { "g3", "g2", "g1" }, "the archived smoke run stays hidden with the smoke filter off");
        Text(page, "e2e-history-counts").Should().Be("1 recent run · 3 archived · 2 smoke/diagnostic hidden");

        ChooseFlow(page, "gradert");
        Text(page, "e2e-history-counts").Should().Be("1 recent run · 2 archived");
    }

    // ── Clear ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheClearDialogStatesWhatIsDeleted_AndStaysDisabledUntilCLEARIsTyped()
    {
        var page = Render(Overview(SetupHistory()), out var api);
        api.Preview = GradertPreview with { ArchivedRuns = 1, BuildLinkedRuns = 1 };
        ChooseFlow(page, "gradert");
        page.Find("[data-testid=e2e-clear-open]").Click();

        page.Find("#e2e-history-dialog-title").TextContent.Should().Contain("Clear test history — Gradert tilgang");
        Text(page, "e2e-clear-active").Should().Be("3");
        Text(page, "e2e-clear-archived").Should().Be("1");
        Text(page, "e2e-clear-build-linked").Should().Be("1");
        Text(page, "e2e-clear-evidence").Should().Be("0");
        page.Find("[data-testid=e2e-clear-include-archived]").HasAttribute("checked").Should().BeTrue("archived runs are included by default");
        page.Find("#e2e-history-dialog-desc").TextContent.Should().Contain("permanently deletes 4 runs").And.Contain("cannot be undone");

        var confirm = () => page.Find("[data-testid=e2e-clear-confirm]");
        confirm().HasAttribute("disabled").Should().BeTrue();
        foreach (var wrong in new[] { "clear", "CLEA", "CLEAR!" })
        {
            page.Find("[data-testid=e2e-clear-confirmation]").Input(wrong);
            confirm().HasAttribute("disabled").Should().BeTrue($"'{wrong}' is not the confirmation");
        }

        page.Find("[data-testid=e2e-clear-include-archived]").Change(false);
        confirm().TextContent.Trim().Should().Be("Permanently clear 3 runs");

        page.Find("[data-testid=e2e-clear-confirmation]").Input("CLEAR");
        confirm().HasAttribute("disabled").Should().BeFalse();
        api.Clears.Should().BeEmpty("typing is not confirming");
    }

    [Fact]
    public async Task CancelAndEscCloseTheClearDialog_WithoutClearing()
    {
        var page = Render(Overview(SetupHistory()), out var api);
        ChooseFlow(page, "gradert");
        page.Find("[data-testid=e2e-clear-open]").Click();
        page.Find("[data-testid=e2e-clear-confirmation]").Input("CLEAR");

        page.Find("[data-testid=e2e-dialog-cancel]").Click();
        page.FindAll("[data-testid=e2e-clear-dialog]").Should().BeEmpty();
        _dialogJs.VerifyInvoke("close");
        JSInterop.VerifyFocusAsyncInvoke().Arguments[0].Should().BeOfType<Microsoft.AspNetCore.Components.ElementReference>("focus returns to the Clear button");

        page.Find("[data-testid=e2e-clear-open]").Click();
        page.FindAll("[data-testid=e2e-clear-confirmation]").Single().GetAttribute("value").Should().BeNullOrEmpty("a reopened dialog starts unconfirmed");
        await page.InvokeAsync(() => page.Instance.CancelHistoryDialog());
        page.FindAll("[data-testid=e2e-clear-dialog]").Should().BeEmpty();

        api.Clears.Should().BeEmpty();
        api.Runs.Should().Be(0);
    }

    [Fact]
    public void ConfirmedClear_ResetsTheFlowToNotRun()
    {
        var page = Render(Overview(SetupHistory()), out var api);
        ChooseFlow(page, "gradert");
        page.Find("[data-testid=e2e-clear-open]").Click();
        page.Find("[data-testid=e2e-clear-confirmation]").Input("CLEAR");
        api.Current = Overview([Run("s1", "smoke", 4, CriticalE2EStatus.Passed)],
            Flow("gradert", "Gradert tilgang", status: CriticalE2EStatus.NotRun), Flow("smoke", "Smoke", CriticalE2EFlowKind.Diagnostic));

        page.Find("[data-testid=e2e-clear-confirm]").Click();

        api.Clears.Should().ContainSingle().Which.Should().Match<CriticalE2EClearRequest>(r =>
            r.FlowId == "gradert" && r.Confirmation == "CLEAR" && r.IncludeArchived);
        page.FindAll("[data-testid=e2e-clear-dialog]").Should().BeEmpty();
        Text(page, "e2e-status-gradert").Should().Be("Not run");
        page.Find("[data-testid=e2e-flow-gradert]").QuerySelector("td[data-label='Last run']")!.TextContent.Trim().Should().Be("—");
        api.Runs.Should().Be(0);
    }

    [Fact]
    public void ReleaseEvidenceBlocksClear_AndOffersArchiveInstead()
    {
        var page = Render(Overview(SetupHistory()), out var api);
        api.Preview = GradertPreview with { BuildLinkedRuns = 1, ReleaseEvidenceRuns = 1, ActiveReleaseEvidenceRuns = 1, ClearBlocked = true };
        ChooseFlow(page, "gradert");
        page.Find("[data-testid=e2e-clear-open]").Click();

        Text(page, "e2e-clear-blocked").Should().Contain("Archive old runs instead");
        page.FindAll("[data-testid=e2e-clear-confirmation]").Should().BeEmpty("there is no way to confirm a protected clear");
        page.FindAll("[data-testid=e2e-clear-confirm]").Should().BeEmpty();

        page.Find("[data-testid=e2e-clear-archive-instead]").Click();
        page.Find("[data-testid=e2e-archive-dialog]").Should().NotBeNull();
        api.Clears.Should().BeEmpty();
    }

    [Fact]
    public void ABackendRefusalIsAnnounced_AndTheDialogStaysOpen()
    {
        var page = Render(Overview(SetupHistory()), out var api);
        api.NextResult = () => new CriticalE2EHistoryActionResult { Blocked = true, Message = "This flow has build-linked release evidence, so its history cannot be cleared. Archive old runs instead.", Overview = api.Current };
        ChooseFlow(page, "gradert");
        page.Find("[data-testid=e2e-clear-open]").Click();
        page.Find("[data-testid=e2e-clear-confirmation]").Input("CLEAR");
        page.Find("[data-testid=e2e-clear-confirm]").Click();

        var error = page.Find("[data-testid=e2e-dialog-error]");
        error.GetAttribute("role").Should().Be("alert");
        error.TextContent.Should().Contain("cannot be cleared");
        page.Find("[data-testid=e2e-clear-dialog]").Should().NotBeNull();
        RowIds(page).Should().Equal("g3", "g2", "g1");
    }
}
