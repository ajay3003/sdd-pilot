using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using M = BirkNext.Web.Services.ManualTestingPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The Manual Testing workspace: the home derives its stages and the one next step from stored records only; the shared header links the three
/// stages; Execute &amp; document starts a run of a saved case, records only the tester's decisions, shows unsaved state, refuses completion while a
/// step is undecided, keeps evidence as references and defects as local observations, and shows a completed run read-only with its history.
/// </summary>
public sealed class ManualTestingWorkspaceTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RunId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly Mock<ITestPlanSuggestionsApiService> _plans = new();
    private readonly Mock<IManualTestRunsApiService> _runs = new();
    private SaveManualRunRequest? _saved;
    private StartManualRunRequest? _started;

    private static TestPlanDraftSummary Summary(int cases = 2, int accepted = 1, int clarify = 0) =>
        new(PlanId, "project:library", "Library – test plan", null, 3, cases, accepted, clarify, 0, 0, 0, "anna", DateTimeOffset.UtcNow);

    private static ManualRunSummary RunSummary(ManualRunState state, ManualRunOutcome outcome, Guid? id = null) =>
        new(id ?? RunId, PlanId, "tsc-borrow", $"{PlanId:N}/tsc-borrow", "Borrow a book", 3, "fp", state, outcome, "anna", "QA", DateTimeOffset.UtcNow.AddHours(-1),
            state == ManualRunState.Completed ? DateTimeOffset.UtcNow : null, 2, 1, 0, 0, 0, 1, 0, 0, 0);

    private static ScenarioDiscoveryResult Discovery(int candidates) => new()
    {
        DiscoveryId = "d1", ProjectKey = "library", DiscoveredAt = DateTimeOffset.UtcNow,
        Candidates = [.. Enumerable.Range(1, candidates).Select(i => new DiscoveredScenario { CandidateId = $"c{i}", ProjectKey = "library", Title = $"Scenario {i}", ExpectedBehaviour = i == 1 ? null : "ok" })],
    };

    private static TestPlanDraft Draft() => new()
    {
        Id = PlanId, GuideKey = "project:library", ProjectKey = "library", PlanName = "Library – test plan", Revision = 3,
        Suites = [new("suite:area:loans", "Loans", "area", null)],
        Cases =
        [
            new()
            {
                Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "library", ScenarioId = "tsc-borrow" }, SuiteId = "suite:area:loans",
                Generated = new TestCaseContent { Title = "Borrow a book", Steps = ["Choose a book", "Confirm the loan"], StepExpected = ["The book is shown", "The loan is registered"] },
            },
        ],
    };

    private static ManualTestRun Run(ManualRunState state = ManualRunState.InProgress, ManualRunOutcome outcome = ManualRunOutcome.NotDecided) => new()
    {
        Id = RunId, ProjectKey = "library", DraftId = PlanId, CaseId = "tsc-borrow", CaseKey = $"{PlanId:N}/tsc-borrow", PlanName = "Library – test plan", PlanRevision = 3,
        CaseFingerprint = "fp", Title = "Borrow a book", Tester = "anna", StartedAt = DateTimeOffset.UtcNow, State = state, Outcome = outcome, Revision = 2,
        Steps = [new() { Number = 1, Action = "Choose a book", Expected = "The book is shown" }, new() { Number = 2, Action = "Confirm the loan", Expected = "The loan is registered" }],
    };

    private void Setup(List<TestPlanDraftSummary>? plans = null, ScenarioDiscoveryResult? discovery = null, List<ManualRunSummary>? runs = null, ManualTestRun? run = null, bool project = true)
    {
        if (project) Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        _plans.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = true, Drafts = plans ?? [] }, (string?)null));
        _plans.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((discovery, (string?)null));
        _plans.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((Draft(), (string?)null));
        _runs.Setup(x => x.ListAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((runs ?? [], (string?)null));
        _runs.Setup(x => x.GetAsync("library", RunId, It.IsAny<CancellationToken>())).ReturnsAsync((run ?? Run(), (string?)null));
        _runs.Setup(x => x.StartAsync(It.IsAny<StartManualRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<StartManualRunRequest, CancellationToken>((r, _) => _started = r).ReturnsAsync((Run(), (string?)null));
        _runs.Setup(x => x.SaveAsync("library", RunId, It.IsAny<SaveManualRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, SaveManualRunRequest, CancellationToken>((_, _, r, _) => _saved = r)
            .ReturnsAsync((string _, Guid _, SaveManualRunRequest r, CancellationToken _) => (Run() with
            {
                Revision = r.BaseRevision + 1, Steps = [.. Run().Steps.Select(s => r.Steps.FirstOrDefault(x => x.Number == s.Number) is { } i ? s with { Outcome = i.Outcome, Actual = i.Actual } : s)],
                Evidence = [.. r.Evidence.Select(e => e with { Id = "ev-1", AddedBy = "anna" })], Defects = [.. r.Defects.Select(d => d with { Id = "df-1", RecordedBy = "anna" })],
            }, (string?)null));
        Services.AddSingleton(_plans.Object);
        Services.AddSingleton(_runs.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<T> Open<T>(string url) where T : IComponent
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<T>();
        cut.WaitForAssertion(() => cut.Find("[aria-busy=false]"));
        return cut;
    }

    // ── Presentation (pure) ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_next_step_follows_the_stored_records_never_a_fixed_position()
    {
        M.Facts F(List<TestPlanDraftSummary>? plans = null, ScenarioDiscoveryResult? d = null, List<ManualRunSummary>? runs = null, string? project = "library") =>
            new(project, "Library", plans ?? [], d, 3, runs ?? []);

        M.Next(F(project: null)).Href.Should().Be("project-import");
        M.Next(F()).Should().Match<M.NextAction>(n => n.Label == "Discover what to test" && n.Href == "test-plan-suggestions?area=choose");
        M.Next(F(d: Discovery(4))).Label.Should().Be("Choose scenarios to prepare");
        M.Next(F([Summary(clarify: 1)], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Resolve cases that need clarification" && n.Href.Contains($"draft={PlanId}"));
        M.Next(F([Summary()], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Run a test case" && n.Href == "manual-testing/execute");
        M.Next(F([Summary()], Discovery(4), [RunSummary(ManualRunState.InProgress, ManualRunOutcome.NotDecided)])).Should()
            .Match<M.NextAction>(n => n.Label == "Resume test run" && n.Href == $"manual-testing/execute?run={RunId}");
        M.Next(F([Summary()], Discovery(4), [RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed)])).Label.Should().Be("Review the failed run");
        M.Next(F([Summary()], Discovery(4), [RunSummary(ManualRunState.Completed, ManualRunOutcome.Passed)])).Label.Should().Be("Continue preparation");
    }

    [Fact]
    public void Stages_are_never_all_blocked_and_their_counts_come_from_records()
    {
        var none = M.Steps(new(null, null, [], null, null, []));
        none.Should().OnlyContain(s => s.State == GuidedStepState.Waiting && s.DisabledReason != null, "without a project every stage says why");

        var fresh = M.Steps(new("library", "Library", [], null, null, []));
        fresh[0].State.Should().Be(GuidedStepState.Current, "a new project starts by discovering — the other stages stay reachable");
        fresh.Should().OnlyContain(s => s.Href != null);

        var busy = M.Steps(new("library", "Library", [Summary(cases: 5, accepted: 2, clarify: 1)], Discovery(6), 3,
            [RunSummary(ManualRunState.Completed, ManualRunOutcome.Passed), RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed, Guid.NewGuid())]));
        busy[0].StatusLabel.Should().Be("6 suggestions");
        busy[0].Evidence.Should().Contain("3 requirements").And.Contain("1 need clarification");
        busy[1].StatusLabel.Should().Be("5 cases · 2 approved");
        busy[2].Should().Match<GuidedStep>(s => s.State == GuidedStepState.Done && s.StatusLabel == "2 completed runs" && s.Evidence!.StartsWith("1 passed · 1 failed"));
    }

    // ── Home ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_home_names_the_project_the_next_step_the_stages_plans_and_runs()
    {
        Setup([Summary()], Discovery(3), [RunSummary(ManualRunState.InProgress, ManualRunOutcome.NotDecided)]);
        var cut = Open<ManualTesting>("manual-testing");

        cut.Find("[data-testid=twh-project]").TextContent.Should().Contain("Library");
        cut.Find("[data-testid=twh-stage][data-stage=home]").GetAttribute("aria-current").Should().Be("page");
        cut.FindAll("[data-testid=twh-stage]").Select(a => a.GetAttribute("href")).Should().Equal("manual-testing", "manual-testing/discover", "test-plan-suggestions", "manual-testing/execute");
        cut.Find("[data-testid=mt-next-action]").Should().Match<AngleSharp.Dom.IElement>(a => a.TextContent == "Resume test run" && a.GetAttribute("href") == $"manual-testing/execute?run={RunId}");
        cut.FindAll("[data-testid=mt-stage]").Select(s => s.GetAttribute("data-step")).Should().Equal("discover", "plan", "execute");
        cut.Find("[data-testid=mt-plan]").TextContent.Should().Contain("Library – test plan").And.Contain("2 cases").And.Contain("1 approved");
        cut.Find("[data-testid=mt-run]").GetAttribute("data-state").Should().Be("InProgress");
        cut.Markup.Should().Contain("A prepared or approved test case is not an executed test");
    }

    [Fact]
    public void Without_a_project_the_home_says_how_to_start_and_shows_no_counts()
    {
        Setup(project: false);
        var cut = Open<ManualTesting>("manual-testing");

        cut.Find("[data-testid=mt-next-action]").GetAttribute("href").Should().Be("project-import");
        cut.FindAll("[data-testid=mt-plans]").Should().BeEmpty();
        cut.Find("[data-testid=twh-project]").TextContent.Should().Contain("No project selected");
    }

    // ── Execute & document ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_saved_case_is_started_for_this_project_with_the_environment_the_tester_names()
    {
        Setup([Summary()]);
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?draft={PlanId}&case=tsc-borrow");

        cut.Find("[data-testid=mte-start]").TextContent.Should().Contain("Borrow a book").And.Contain("2 steps");
        cut.Find("[data-testid=mte-start-env]").Input("QA");
        cut.Find("[data-testid=mte-start-run]").Click();

        cut.WaitForAssertion(() => _started.Should().Be(new StartManualRunRequest("library", PlanId, "tsc-borrow", "QA")));
    }

    [Fact]
    public void The_runner_records_only_the_testers_decisions_shows_unsaved_state_and_completes_only_when_every_step_is_decided()
    {
        Setup([Summary()]);
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?run={RunId}");

        cut.FindAll("[data-testid=mte-step]").Should().HaveCount(2).And.OnlyContain(s => s.GetAttribute("data-outcome") == "none", "nothing is pre-filled: no automatic PASS");
        cut.Find("[data-testid=mte-complete]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mte-complete-why]").TextContent.Should().Contain("2 left");

        cut.Find("[data-testid=mte-outcome-1-Pass]").Change(true);
        cut.Find("[data-testid=mte-outcome-2-Fail]").Change(true);
        cut.Find("[data-testid=mte-actual-2]").Input("Error 500 instead of a receipt");
        cut.Find("[data-testid=mte-unsaved]").TextContent.Should().Be("Unsaved changes");
        cut.Find("[data-testid=mte-preview]").TextContent.Should().Contain("Failed");
        cut.Find("[data-testid=mte-complete]").HasAttribute("disabled").Should().BeFalse();

        cut.Find("[data-testid=mte-save]").Click();

        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.BaseRevision.Should().Be(2);
        _saved.Steps.Should().Equal(new ManualStepInput(1, ManualStepOutcome.Pass, null), new ManualStepInput(2, ManualStepOutcome.Fail, "Error 500 instead of a receipt"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mte-unsaved]").Should().BeEmpty());
        cut.Find("[data-testid=mte-message]").TextContent.Should().Contain("Progress saved");
    }

    [Fact]
    public void Evidence_is_a_reference_with_a_checked_link_and_a_defect_is_a_local_observation_prefilled_from_the_failed_step()
    {
        Setup([Summary()]);
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?run={RunId}");

        cut.Find("[data-testid=mte-evidence]").TextContent.Should().Contain("Attach a screenshot, PDF or log (up to 10 MB), or add a link or a note");
        cut.FindAll("[data-testid=mte-attach]").Should().ContainSingle("a file can be attached to a run in progress");
        cut.Find("[data-testid=mte-ev-label]").Input("Screenshot");
        cut.Find("[data-testid=mte-ev-reference]").Input("javascript:alert(1)");
        cut.Find("[data-testid=mte-ev-invalid]");
        cut.Find("[data-testid=mte-ev-add]").HasAttribute("disabled").Should().BeTrue("only http(s) links are accepted");
        cut.Find("[data-testid=mte-ev-reference]").Input("https://share.example/shot.png");
        cut.Find("[data-testid=mte-ev-add]").Click();
        cut.Find("[data-testid=mte-evidence-item]").TextContent.Should().Contain("Screenshot").And.Contain("not saved yet");

        cut.Find("[data-testid=mte-outcome-2-Fail]").Change(true);
        cut.Find("[data-testid=mte-actual-2]").Input("Error 500");
        cut.Find("[data-testid=mte-defect-for-2]").Click();
        cut.Find("[data-testid=mte-df-title]").Input("Loan is not registered");
        cut.Find("[data-testid=mte-df-add]").Click();
        cut.Find("[data-testid=mte-defect]").TextContent.Should().Contain("Loan is not registered").And.Contain("step 2").And.Contain("local observation");
        cut.Find("[data-testid=mte-defects]").TextContent.Should().Contain("BirkNext never creates or changes work items");

        cut.Find("[data-testid=mte-save]").Click();
        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.Evidence.Single().Should().Match<ManualRunEvidence>(e => e.Kind == ManualEvidenceKind.Link && e.Reference == "https://share.example/shot.png");
        _saved.Defects.Single().Should().Match<ManualRunDefect>(d => d.Step == 2 && d.Expected == "The loan is registered" && d.Actual == "Error 500" && d.Status == ManualDefectStatus.Open);
    }

    [Fact]
    public void A_completed_run_is_read_only_and_the_overview_lists_cases_and_the_history()
    {
        var done = Run(ManualRunState.Completed, ManualRunOutcome.Failed) with
        {
            Steps = [new() { Number = 1, Action = "Choose a book", Outcome = ManualStepOutcome.Pass }, new() { Number = 2, Action = "Confirm", Outcome = ManualStepOutcome.Fail, Actual = "Error" }],
        };
        Setup([Summary()], runs: [RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed)], run: done);
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?run={RunId}");

        cut.Find("[data-testid=mte-run-state]").TextContent.Should().Be("Failed");
        cut.FindAll("input[type=radio]").Should().BeEmpty("a completed run cannot be changed");
        cut.FindAll("[data-testid=mte-save]").Should().BeEmpty();
        cut.Find("[data-testid=mte-run-meta]").TextContent.Should().Contain("plan revision 3 (pinned when the run started)");

        var overview = Open<ManualTestingExecute>("manual-testing/execute");
        overview.Find("[data-testid=mte-case]").TextContent.Should().Contain("Borrow a book").And.Contain("Last run: Failed");
        overview.Find("[data-testid=mte-history-row]").GetAttribute("data-outcome").Should().Be("Failed");
    }

    // ── Follow-up after a run, attachments, the project's defect list ─────────────────────────────────────────────

    private static ManualTestRun CompletedWithDefect() => Run(ManualRunState.Completed, ManualRunOutcome.Failed) with
    {
        CompletedAt = DateTimeOffset.UtcNow,
        Defects = [new ManualRunDefect { Id = "df-1", Title = "Loan is not saved", Status = ManualDefectStatus.Open, RecordedBy = "anna", RecordedAt = DateTimeOffset.UtcNow }],
        Evidence = [new ManualRunEvidence { Id = "ev-1", Kind = ManualEvidenceKind.Attachment, Label = "Error dialog", Reference = "0123456789abcdef0123456789abcdef", FileName = "error.png",
            ContentType = "image/png", Size = 2048, AddedBy = "anna", AddedAt = DateTimeOffset.UtcNow, AddedAfterCompletion = true }],
    };

    [Fact]
    public void A_defect_of_a_completed_run_is_followed_up_and_its_history_shown_while_the_results_stay_read_only()
    {
        UpdateManualDefectRequest? sent = null;
        Setup(plans: [Summary()], runs: [RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed)], run: CompletedWithDefect());
        _runs.Setup(x => x.UpdateDefectAsync("library", RunId, "df-1", It.IsAny<UpdateManualDefectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, string, UpdateManualDefectRequest, CancellationToken>((_, _, _, r, _) => sent = r)
            .ReturnsAsync((string _, Guid _, string _, UpdateManualDefectRequest r, CancellationToken _) => (CompletedWithDefect() with
            {
                Revision = 3,
                Defects = [CompletedWithDefect().Defects[0] with { Status = r.Status, History = [new ManualDefectEvent(DateTimeOffset.UtcNow, "dev", ManualDefectStatus.Open, r.Status, r.Note)] }],
            }, (string?)null));
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?run={RunId}");

        cut.FindAll("[data-testid=mte-defect-status-select]").Should().BeEmpty("the recorded results of a completed run are read-only");
        cut.Find("[data-testid=mte-defect-status]").TextContent.Should().Be("Open");
        cut.Find("[data-testid=mte-defect-followup]").Click();
        cut.Find("[data-testid=mte-followup-status]").Change("ReadyForRetest");
        cut.Find("[data-testid=mte-followup-note]").Input("Fixed in build 1.4");
        cut.Find("[data-testid=mte-followup-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=mte-defect-status]").TextContent.Should().Be("Ready for retest"));
        sent.Should().BeEquivalentTo(new UpdateManualDefectRequest(2, ManualDefectStatus.ReadyForRetest, "Fixed in build 1.4", null, null), "the run revision the tester saw is sent");
        cut.Find("[data-testid=mte-defect-history]").TextContent.Should().Contain("Open → Ready for retest").And.Contain("Fixed in build 1.4");
    }

    [Fact]
    public void An_attached_file_is_listed_with_its_size_a_download_and_whether_it_was_added_after_completion()
    {
        Setup(plans: [Summary()], runs: [RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed)], run: CompletedWithDefect());
        _runs.Setup(x => x.AttachmentAsync("library", RunId, "0123456789abcdef0123456789abcdef", It.IsAny<CancellationToken>())).ReturnsAsync(([1, 2, 3], (string?)null));
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?run={RunId}");

        var item = cut.Find("[data-testid=mte-evidence-item][data-kind=Attachment]");
        item.TextContent.Should().Contain("error.png").And.Contain("2 KB");
        cut.Find("[data-testid=mte-attachment-followup]").TextContent.Should().Be("added after completion");
        cut.Find("[data-testid=mte-attachment-download]").Click();
        cut.WaitForAssertion(() => JSInterop.Invocations.Should().Contain(i => i.Identifier == "downloadBase64File" && (string)i.Arguments[0]! == "error.png"));
        cut.FindAll("[data-testid=mte-attach]").Should().ContainSingle("follow-up evidence can still be attached to a completed run");
    }

    [Fact]
    public void An_attached_file_says_it_was_not_scanned_for_malware_and_can_be_withdrawn_with_a_reason_that_stays_on_record()
    {
        Setup(plans: [Summary()], runs: [RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed)], run: CompletedWithDefect());
        var fileId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var info = new ManualRunAttachmentInfo(fileId, "error.png", "error.png", "image/png", 2048, new string('a', 64), "dev", DateTimeOffset.UtcNow, "not-scanned", null, null, null);
        _runs.Setup(x => x.AttachmentsAsync("library", RunId, It.IsAny<CancellationToken>())).ReturnsAsync(([info], (string?)null));
        string? reason = null;
        _runs.Setup(x => x.WithdrawAttachmentAsync("library", RunId, "0123456789abcdef0123456789abcdef", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string _, Guid _, string _, string r, CancellationToken _) => reason = r)
            .ReturnsAsync((string _, Guid _, string _, string r, CancellationToken _) => (info with { WithdrawnAt = DateTimeOffset.UtcNow, WithdrawnBy = "dev", WithdrawReason = r }, (string?)null));
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?run={RunId}");

        cut.WaitForAssertion(() => cut.Find("[data-testid=mte-attachment-scan]").TextContent.Should().Be("not scanned for malware", "no scanner ran, so the file is never called clean"));
        cut.Find("[data-testid=mte-attachment-withdraw]").Click();
        cut.Find("[data-testid=mte-attachment-withdraw-confirm]").HasAttribute("disabled").Should().BeTrue("a reason is required");
        cut.Find("[data-testid=mte-attachment-withdraw-reason]").Input("Contains a personal identity number");
        cut.Find("[data-testid=mte-attachment-withdraw-confirm]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=mte-attachment-withdrawn]").TextContent.Should().Contain("Contains a personal identity number"));
        reason.Should().Be("Contains a personal identity number");
        cut.FindAll("[data-testid=mte-attachment-download]").Should().BeEmpty("a withdrawn file has no content to download");
    }

    [Fact]
    public void The_project_defect_list_shows_open_defects_across_runs_and_closed_ones_on_request()
    {
        Setup(plans: [Summary()], runs: [RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed)]);
        ManualDefectListItem Item(string title, ManualDefectStatus status) => new(RunId, "Borrow a book", $"{PlanId:N}/tsc-borrow", ManualRunState.Completed, ManualRunOutcome.Failed,
            DateTimeOffset.UtcNow, "anna", new ManualRunDefect { Id = title, Title = title, Status = status });
        _runs.Setup(x => x.DefectsAsync("library", It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<ManualDefectListItem> { Item("Loan is not saved", ManualDefectStatus.ReadyForRetest), Item("Typo in receipt", ManualDefectStatus.Closed) }, (string?)null));
        var cut = Open<ManualTestingExecute>("manual-testing/execute");

        cut.WaitForAssertion(() => cut.Find("[data-testid=mte-defect-list-count]").TextContent.Should().Contain("1 not closed · 2 in all runs"));
        cut.FindAll("[data-testid=mte-defect-list-item]").Select(e => e.GetAttribute("data-status")).Should().Equal("ReadyForRetest");
        cut.Find("[data-testid=mte-defect-list-open]").GetAttribute("href").Should().Be($"manual-testing/execute?run={RunId}");
        cut.Find("[data-testid=mte-defect-list-all]").Change(true);
        cut.FindAll("[data-testid=mte-defect-list-item]").Should().HaveCount(2);

        cut.Find("[data-testid=mte-defect-list-download]").Click();
        var csv = (string)JSInterop.Invocations.Single(i => i.Identifier == "downloadTextFile").Arguments[1]!;
        var lines = csv.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("\"Defect\",\"Status\"");
        lines.Skip(1).Select(l => l.Split(',')[1]).Should().BeEquivalentTo(new[] { "\"Ready for retest\"", "\"Closed\"" }, "the defect report states the follow-up status, closed ones included");
    }

    [Fact]
    public void Before_a_run_the_tester_sees_what_to_prepare_and_the_environment_of_the_last_run_is_filled_in()
    {
        Setup([Summary()], runs: [RunSummary(ManualRunState.Completed, ManualRunOutcome.Failed)]);
        var c = Draft().Cases[0];
        _plans.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((Draft() with
        {
            Cases = [c with
            {
                Edits = new TestCaseEdits { EnvironmentPrerequisites = "QA with the loan service enabled", BlockedBy = "Loan service outage" },
                TestData = [new TestCaseDataLink { AssetId = "asset-17", Alias = "member-with-card" }],
            }],
        }, (string?)null));
        var cut = Open<ManualTestingExecute>($"manual-testing/execute?draft={PlanId}&case=tsc-borrow");

        cut.Find("[data-testid=mte-start-blocked]").TextContent.Should().Contain("Loan service outage").And.Contain("still start");
        cut.Find("[data-testid=mte-start-env-needed]").TextContent.Should().Contain("QA with the loan service enabled");
        cut.Find("[data-testid=mte-start-test-data]").TextContent.Should().Contain("member-with-card").And.NotContain("asset-17");
        cut.Find("[data-testid=mte-start-env]").GetAttribute("value").Should().Be("QA", "the environment of this case's last run is filled in");
        cut.Find("[data-testid=mte-start-env-suggested]").TextContent.Should().Contain("last run of this case");

        cut.Find("[data-testid=mte-start-run]").Click();
        cut.WaitForAssertion(() => _started!.Environment.Should().Be("QA", "the suggestion is used unless the tester changes it"));
    }
}
