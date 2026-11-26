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
using O = BirkNext.Web.Services.ManualTestingOverviewPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The Manual Testing workspace: the home derives its two stages and the one next step from stored records only; the shared header links the
/// stages; Plan &amp; prepare ends with the export to Azure DevOps Test Plans, which executes manual tests (2026-11-19: Execute &amp; document was
/// retired). Nothing says a case was executed, and an old Execute &amp; document link lands on the saved case in Plan &amp; prepare.
/// </summary>
public sealed class ManualTestingWorkspaceTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private readonly Mock<ITestPlanSuggestionsApiService> _plans = new();

    private static TestPlanDraftSummary Summary(int cases = 2, int accepted = 1, int clarify = 0) =>
        new(PlanId, "project:library", "Library – test plan", null, 3, cases, accepted, clarify, 0, 0, 0, "anna", DateTimeOffset.UtcNow);

    private static ScenarioDiscoveryResult Discovery(int candidates) => new()
    {
        DiscoveryId = "d1", ProjectKey = "library", DiscoveredAt = DateTimeOffset.UtcNow,
        Candidates = [.. Enumerable.Range(1, candidates).Select(i => new DiscoveredScenario { CandidateId = $"c{i}", ProjectKey = "library", Title = $"Scenario {i}", ExpectedBehaviour = i == 1 ? null : "ok" })],
    };

    private void Setup(List<TestPlanDraftSummary>? plans = null, ScenarioDiscoveryResult? discovery = null, bool project = true)
    {
        if (project) Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        _plans.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = true, Drafts = plans ?? [] }, (string?)null));
        _plans.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((discovery, (string?)null));
        Services.AddSingleton(_plans.Object);
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
    public void Discover_filters_requirements_by_test_design_and_searches_id_and_text()
    {
        List<ProjectRequirementOption> reqs = [new("FR-001", "Store the report", "spec.md"), new("FR-002", "Reject a duplicate report", "spec.md"), new("FR-003", "Lock the period", "spec.md")];
        var designed = new Dictionary<string, PreparedRequirementTrace>(StringComparer.OrdinalIgnoreCase)
        {
            ["FR-001"] = new() { RequirementId = "FR-001", ApprovedCases = 1, PreparedCases = 1 },
            ["FR-002"] = new() { RequirementId = "FR-002", ApprovedCases = 0, PreparedCases = 2 },
        };
        M.FilterRequirements(reqs, designed, M.AllRequirements, null).Select(r => r.RequirementId).Should().Equal("FR-001", "FR-002", "FR-003");
        M.FilterRequirements(reqs, designed, M.WithoutDesign, null).Select(r => r.RequirementId).Should().Equal("FR-003");
        M.FilterRequirements(reqs, designed, M.PreparedOnly, null).Select(r => r.RequirementId).Should().Equal("FR-002");
        M.FilterRequirements(reqs, designed, M.WithApproved, null).Select(r => r.RequirementId).Should().Equal("FR-001");
        M.FilterRequirements(reqs, designed, M.AllRequirements, " report ").Select(r => r.RequirementId).Should().Equal("FR-001", "FR-002");
        M.FilterRequirements(reqs, designed, M.AllRequirements, "fr-003").Select(r => r.RequirementId).Should().Equal("FR-003");
    }

    [Fact]
    public void The_next_step_follows_the_stored_records_and_ends_with_the_export()
    {
        M.Facts F(List<TestPlanDraftSummary>? plans = null, ScenarioDiscoveryResult? d = null, string? project = "library") => new(project, "Library", plans ?? [], d, 3);

        M.Next(F(project: null)).Href.Should().Be("project-import");
        M.Next(F(project: null)).Label.Should().Be("Velg applikasjon", "projects exist: one has to be chosen");
        M.Next(new M.Facts(null, null, [], null, null, AnyProjectImported: false)).Label.Should().Be("Importer en applikasjon", "nothing to choose from: import first");
        M.Steps(new M.Facts(null, null, [], null, null, AnyProjectImported: false)).Should().OnlyContain(s => s.DisabledReason == "Import a project first.");
        M.Next(F()).Should().Match<M.NextAction>(n => n.Label == "Finn scenarioer" && n.Href == "manual-testing/discover");
        M.Next(F(d: Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Velg scenarioer" && n.Href == "manual-testing/discover",
            "scenarios are chosen where they are browsed (scenario-first)");
        M.Next(F([Summary(clarify: 1)], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Avklar testtilfeller" && n.Href.Contains($"draft={PlanId}"));
        M.Next(F([Summary()], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Overfør testplan" && n.Href == $"manual-testing/plans?transfer={PlanId}"
            && n.Why.Contains("Azure DevOps Test Plans, der manuelle tester kjøres"));
        M.Next(F([Summary(accepted: 0)], Discovery(4))).Label.Should().Be("Fortsett forberedelsen", "nothing approved: nothing to export yet");
    }

    [Fact]
    public void In_a_monorepo_without_a_chosen_specification_choosing_it_comes_before_discovery()
    {
        const string choose = "This project contains 39 specifications in 14 modules and none is chosen yet.";
        M.Facts F(List<TestPlanDraftSummary>? plans = null, ScenarioDiscoveryResult? d = null) => new("m2lb", "M2LB", plans ?? [], d, null, choose);

        M.Next(F()).Should().Match<M.NextAction>(n => n.Label == "Velg spesifikasjon" && n.Href == "manual-testing/discover" && n.Why == choose);
        M.Next(F(d: Discovery(4))).Label.Should().Be("Velg spesifikasjon", "discovery without a specification finds no business scenarios");
        M.Next(F([Summary()], Discovery(4))).Label.Should().Be("Overfør testplan", "prepared work is never held back by the choice");
        M.Steps(F())[0].Evidence.Should().Contain("Specification not chosen yet");
    }

    [Fact]
    public void Two_stages_are_never_all_blocked_and_their_counts_come_from_records()
    {
        M.Stages.Select(s => s.Id).Should().Equal("discover", "plan", "plans");
        var none = M.Steps(new(null, null, [], null, null));
        none.Should().OnlyContain(s => s.State == GuidedStepState.Waiting && s.DisabledReason != null, "without a project every stage says why");

        var fresh = M.Steps(new("library", "Library", [], null, null));
        fresh[0].State.Should().Be(GuidedStepState.Current, "a new project starts by discovering — the other stage stays reachable");
        fresh.Should().OnlyContain(s => s.Href != null);

        var busy = M.Steps(new("library", "Library", [Summary(cases: 5, accepted: 2, clarify: 1)], Discovery(6), 3));
        busy.Should().HaveCount(2);
        busy[0].StatusLabel.Should().Be("6 suggestions");
        busy[0].Evidence.Should().Contain("3 requirements").And.Contain("1 need clarification");
        busy[1].StatusLabel.Should().Be("5 cases · 2 approved");
        busy[1].Explanation.Should().Contain("export it to Azure DevOps Test Plans, where it is executed");
    }

    // ── Home ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Testplaner_lists_creates_and_transfers_test_plans_and_leaves_the_status_to_the_overview()
    {
        Setup([Summary()], Discovery(3));
        var cut = Open<ManualTesting>("manual-testing/plans");

        cut.Find("[data-testid=twh-stage][data-stage=plans]").GetAttribute("aria-current").Should().Be("page");
        cut.Find("[data-testid=twh-title]").TextContent.Should().Be("Testplaner");
        cut.Find("[data-testid=mt-new-plan]").Should().NotBeNull();
        cut.Find("[data-testid=mt-plan]").TextContent.Should().Contain("Library – test plan").And.Contain("2 testtilfeller").And.Contain("1 godkjent");
        cut.FindAll("[data-testid=mt-counts]").Should().BeEmpty("one place for the status: the overview");
        cut.FindAll("[data-testid=mt-steps]").Should().BeEmpty();
    }

    [Fact]
    public void An_older_overview_link_with_a_transfer_lands_on_Testplaner()
    {
        Setup([Summary()], Discovery(3));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Open<ManualTesting>($"manual-testing?transfer={PlanId}");

        cut.WaitForAssertion(() => navigation.Uri.Should().Be(navigation.BaseUri + $"manual-testing/plans?transfer={PlanId}"));
    }

    [Theory]
    [InlineData("manual-testing/execute?draft=44444444-4444-4444-4444-444444444444&case=tsc-borrow", "manual-testing/plan?draft=44444444-4444-4444-4444-444444444444&case=tsc-borrow")]
    [InlineData("manual-testing/execute?run=55555555-5555-5555-5555-555555555555", "manual-testing")]
    [InlineData("manual-testing/execute", "manual-testing")]
    public void An_old_Execute_and_document_link_lands_on_the_saved_case_or_on_Manual_Testing(string from, string to)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(from);

        var cut = Render<ManualTestingExecuteRedirect>();

        cut.WaitForAssertion(() => navigation.Uri.Should().Be(navigation.BaseUri + to));
    }

    // ── Testoversikt (2026-11-25) ─────────────────────────────────────────────────────────────────────────────────

    private const string Skole = "SkoleService/specs/001-skoleservice/spec.md", Front = "Frontend/specs/002-child-search-profile/spec.md";

    private static ManualTestingOverview Overview() => new()
    {
        ProjectKey = "library", ScenariosAvailable = true,
        Specifications =
        [
            new(Skole, "SkoleService", "001-skoleservice", [new("tss-1", "k1", "US1", "Report", 1, "P1", "A"), new("tss-2", "k1", "US1", "Report", 2, "P1", "B"), new("tss-3", "k2", "US2", "Lock", 1, "P2", "C")]),
            new(Front, "Frontend", "002-child-search-profile", [new("tss-f1", "f1", "US1", "Search", 1, null, "F")]),
        ],
        Cases =
        [
            // Five cases for one scenario = one covered scenario; one approved and ready, one with a hindrance.
            .. Enumerable.Range(1, 5).Select(i => new OverviewCase { PlanId = PlanId, CaseId = $"s1-{i}", Title = "A", SpecificationPath = Skole, Module = "SkoleService",
                ScenarioIdentity = "tss-1", StoryKey = "k1", Priority = "P1", Review = i == 1 ? TestCaseReviewState.Accepted : TestCaseReviewState.Generated, Ready = i == 1, Hindrance = i == 2 }),
            new OverviewCase { PlanId = PlanId, CaseId = "f1", Title = "F", SpecificationPath = Front, Module = "Frontend", ScenarioIdentity = "tss-f1", StoryKey = "f1",
                Review = TestCaseReviewState.Accepted, Ready = false, SourceState = CaseSourceState.Changed },
            new OverviewCase { PlanId = PlanId, CaseId = "free", Title = "Free", Module = "Uten modultilknytning", Review = TestCaseReviewState.Generated },
        ],
        Plans = [new(PlanId, "Library – test plan", 7, 2, 5, 1, ["Frontend", "SkoleService", "Uten modultilknytning"], DateTimeOffset.UtcNow)],
    };

    private void SetupOverview(ManualTestingDashboardSettings? settings = null)
    {
        _plans.Setup(x => x.OverviewAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((Overview(), (string?)null));
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((settings ?? new ManualTestingDashboardSettings(), (string?)null));
    }

    [Fact]
    public void Testoversikt_is_the_landing_page_with_project_figures_no_next_step_and_no_execution_results()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");

        cut.Find("[data-testid=twh-title]").TextContent.Should().Be("Testoversikt");
        cut.Find("[data-testid=twh-stage][data-stage=home]").GetAttribute("aria-current").Should().Be("page");
        cut.FindAll("[data-testid=mt-next]").Should().BeEmpty("«Neste steg» is removed");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));
        cut.Find("[data-testid=mto-scope]").TextContent.Should().Be("Hele prosjektet");
        string Kpi(string id) => cut.Find($"[data-testid=mto-kpi][data-widget='{id}'] .mto-kpi-v").TextContent;
        Kpi("metric.scenarios").Should().Be("4");
        Kpi("metric.testCases").Should().Be("7", "the hand-written case without a source is counted, not dropped");
        Kpi("metric.approved").Should().Be("2");
        Kpi("metric.ready").Should().Be("1", "approved is not ready");
        cut.FindAll("[data-testid=mto-kpi][data-widget='metric.testPlans']").Should().BeEmpty("off by default");
        cut.Markup.Should().NotContain("Gjennomført").And.NotContain("Bestått").And.NotContain("Feilet");
        cut.FindAll("[data-testid=mto-row]").Select(r => r.GetAttribute("data-key")).Should().Equal("Frontend", "SkoleService", "Uten modultilknytning");
        var skole = cut.Find("[data-testid=mto-row][data-key=SkoleService]").QuerySelectorAll("td").Select(td => td.TextContent).ToList();
        skole.Should().Equal("SkoleService", "3", "1", "5", "1", "4");
        cut.FindAll("[data-testid=mto-widget][data-widget='chart.history']").Should().BeEmpty("optional charts are off by default");
    }

    [Fact]
    public void A_switched_off_part_is_not_rendered_and_an_optional_history_chart_says_honestly_that_there_is_no_history()
    {
        Setup([Summary()], Discovery(3));
        var settings = new ManualTestingDashboardSettings { Visible = new() { ["chart.priority"] = false, ["chart.history"] = true, ["metric.approved"] = false } };
        SetupOverview(settings);
        var cut = Open<ManualTesting>("manual-testing");

        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));
        cut.FindAll("[data-testid=mto-widget][data-widget='chart.priority']").Should().BeEmpty();
        cut.FindAll("[data-testid=mto-kpi][data-widget='metric.approved']").Should().BeEmpty();
        cut.Find("[data-testid=mto-history-unavailable]").TextContent.Should().StartWith("Historiske tall er ikke tilgjengelige");
        cut.Find("[data-testid=mto-widget][data-widget='chart.preparation']").Should().NotBeNull();
    }

    [Fact]
    public void Without_a_project_Testoversikt_asks_for_one_and_shows_no_figures()
    {
        Setup(project: false);
        var cut = Open<ManualTesting>("manual-testing");

        cut.Find("[data-testid=mt-no-project]").TextContent.Should().StartWith("Velg et prosjekt for å se testarbeidet.");
        cut.FindAll("[data-testid=mto-kpis]").Should().BeEmpty();
        cut.FindAll("[data-testid=mt-next]").Should().BeEmpty();
    }

    [Fact]
    public void The_figures_count_unique_scenarios_and_cases_per_scope_and_never_add_overlapping_milestones()
    {
        var o = Overview();
        var project = O.Of(o, O.Scope.Project);
        var f = O.Compute(project, o.Plans);
        f.Should().Be(new O.Figures(4, 2, 7, 2, 1, 0, 1), "five cases for tss-1 cover ONE scenario");

        var module = O.Of(o, new O.Scope(O.Level.Module, "SkoleService", null));
        O.Compute(module, o.Plans).Should().Be(new O.Figures(3, 1, 5, 1, 1, 0, 1));
        var spec = O.Of(o, new O.Scope(O.Level.Specification, "Frontend", Front));
        O.Compute(spec, o.Plans).Should().Be(new O.Figures(1, 1, 1, 1, 0, 0, 1));

        O.Coverage(project, O.Level.Project).Select(b => (b.Label, b.Value)).Should().Equal(("Frontend (1/1)", 100), ("SkoleService (1/3)", 33));
        O.Coverage(module, O.Level.Module).Should().ContainSingle().Which.Label.Should().Be("001-skoleservice (1/3)", "one module: per specification, never a one-bar module chart");
        O.Gaps(project).Select(b => (b.Key, b.Value)).Should().Equal(("P1", 1), ("P2", 1));
        O.Preparation(project).Select(b => b.Value).Should().Equal(7, 7, 2, 1);
        O.Blockers(project).Single(b => b.Key == "hindrance").Value.Should().Be(1);
        O.NotReady(project).Should().Be(6);
        O.SourceChanges(project).Should().ContainSingle(b => b.Key == "changed" && b.Value == 1);
        O.P1Attention(project).Select(a => a.Key).Should().Contain(["p1-uncovered", "p1-approval", "p1-ready"]);
        O.Rows(spec, O.Level.Specification).Should().ContainSingle(r => r.Label == "US1 Search" && r.Covered == 1);
    }

    // ── Drill-down and loading (2026-11-26) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_bar_and_follow_up_line_opens_exactly_as_many_records_as_it_shows()
    {
        var o = Overview();
        foreach (var (scope, level) in new[] { (O.Scope.Project, O.Level.Project), (new O.Scope(O.Level.Module, "SkoleService", null), O.Level.Module), (new O.Scope(O.Level.Specification, "SkoleService", Skole), O.Level.Specification) })
        {
            var s = O.Of(o, scope);
            foreach (var (chart, bars) in new[] { ("chart.preparation", O.Preparation(s)), ("chart.priority", O.Priority(s)), ("chart.coverageGaps", O.Gaps(s)), ("chart.blockers", O.Blockers(s)),
                ("chart.sourceChanges", O.SourceChanges(s)), ("chart.manualTechnical", O.ManualTechnical(s)), ("chart.moduleApproval", O.ModuleApproval(s)), ("panel.translationReview", O.Translations(s)) })
                foreach (var bar in bars) O.DrillDown(s, level, chart, bar.Key)!.Count.Should().Be(bar.Value, $"{chart}/{bar.Key} at {level}");
            // A coverage bar (a percentage) opens the scenarios of its group that still have no test case.
            foreach (var bar in O.Coverage(s, level))
                O.DrillDown(s, level, "chart.moduleCoverage", bar.Key)!.Scenarios.Should().OnlyContain(x => !x.Covered);
        }
        var project = O.Of(o, O.Scope.Project);
        var p1 = O.DrillDown(project, O.Level.Project, "chart.coverageGaps", "P1")!;
        p1.Title.Should().Be("P1 uten testtilfeller");
        p1.Scenarios.Select(x => (x.Specification.Path, x.Scenario.Identity)).Should().Equal((Skole, "tss-2"));
        O.DrillDown(project, O.Level.Project, "panel.p1Attention", "p1-uncovered")!.Scenarios.Should().ContainSingle(x => x.Scenario.Identity == "tss-2");
        O.DrillDown(project, O.Level.Project, "chart.moduleCoverage", "SkoleService")!.Narrow.Should().Be(new O.Scope(O.Level.Module, "SkoleService", null));
        O.DrillDown(project, O.Level.Project, "chart.unknown", "x").Should().BeNull();
    }

    [Fact]
    public void Testoversikt_does_not_read_the_scenario_discovery_but_Testplaner_does()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));
        _plans.Verify(x => x.LastDiscoveryAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never,
            "2,000 scenarios on M2LB took seconds of the browser's time and Testoversikt does not use them");

        Services.GetRequiredService<NavigationManager>().NavigateTo("manual-testing/plans");
        cut.Render();
        cut.WaitForAssertion(() => _plans.Verify(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void A_bar_shows_its_records_and_opens_Finn_og_forsta_on_their_module_specification_and_scenarios()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var spec = new BirkNext.Web.Services.Explorers.ExplorerArtifact("a-skole", WorkspaceArtifactType.Specification, "001-skoleservice", "spec.md", Skole,
            BirkNext.Web.Services.Explorers.ExplorerArtifactSource.Workspace, "ProjectImport", 1, 1, "Draft", BirkNext.Web.Services.Explorers.ExplorerArtifactCurrentness.Current, null, Module: "SkoleService");
        var other = spec with { Id = "a-other", SourcePath = "SkoleService/specs/002-other/spec.md" };
        string? selectedModule = null;
        BirkNext.Web.Services.Explorers.ExplorerArtifact? selected = null;
        var explorer = new Mock<BirkNext.Web.Services.Explorers.IArtifactExplorerContext>();
        explorer.Setup(e => e.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            new BirkNext.Web.Services.Explorers.ArtifactExplorerState(WorkspaceArtifactType.Specification,
                selected is null ? BirkNext.Web.Services.Explorers.ExplorerArtifactStatus.SelectionRequired : BirkNext.Web.Services.Explorers.ExplorerArtifactStatus.Loaded,
                "library", "Library", selectedModule == "SkoleService" ? [other, spec] : [], selected, Module: selectedModule));
        explorer.Setup(e => e.SelectModuleAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Callback((string? m, CancellationToken _) => selectedModule = m).Returns(Task.CompletedTask);
        explorer.Setup(e => e.Select(WorkspaceArtifactType.Specification, It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((WorkspaceArtifactType _, string id, string? _) => selected = id == spec.Id ? spec : other);
        Services.AddSingleton(explorer.Object);
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));

        cut.Find("[data-widget='chart.coverageGaps'] [data-testid=mto-bar][data-key=P1] [data-testid=mto-bar-open]").Click();
        var drill = cut.Find("[data-testid=mto-drill]");
        drill.QuerySelector("[data-testid=mto-drill-count]")!.TextContent.Should().Be("(1)");
        drill.QuerySelectorAll("[data-testid=mto-drill-scenario]").Select(e => e.TextContent.Trim()).Should().Equal("US1 · B");

        cut.Find("[data-testid=mto-drill-open]").Click();
        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("manual-testing/discover"));
        selectedModule.Should().Be("SkoleService");
        selected.Should().Be(spec, "the exact specification of the scenarios, never another one in the module");
        var written = JSInterop.Invocations.Where(i => i.Identifier == "sessionStorage.setItem" && (string)i.Arguments[0]! == TabDraftStore.DiscoverFilterKey("library")).Last();
        var filter = System.Text.Json.JsonSerializer.Deserialize<ScenarioBrowserPresentation.Filter>((string)written.Arguments[1]!)!;
        filter.Identities.Should().Equal("tss-2");
        filter.Selection.Should().Be("P1 uten testtilfeller");
        filter.Queue.Should().BeNull("no work queue hides a scenario of the selection");
    }

    [Fact]
    public void When_the_specification_cannot_be_chosen_nothing_opens_and_the_reason_is_shown()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));
        cut.Find("[data-widget='chart.coverageGaps'] [data-testid=mto-bar][data-key=P1] [data-testid=mto-bar-open]").Click();
        cut.Find("[data-testid=mto-drill-open]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-drill-error]"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("manual-testing", "never a general page without the selection");
    }

    [Fact]
    public void A_case_bar_lists_its_cases_with_links_to_each_case_in_its_test_plan()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));
        cut.Find("[data-widget='chart.blockers'] [data-testid=mto-bar][data-key=hindrance] [data-testid=mto-bar-open]").Click();
        cut.FindAll("[data-testid=mto-drill-case]").Select(a => a.GetAttribute("href")).Should().Equal($"{M.PlanRoute}?draft={PlanId}&case=s1-2");
        cut.Find("[data-widget='chart.blockers'] [data-key=hindrance] [data-testid=mto-bar-open]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=mto-drill-close]").Click();
        cut.FindAll("[data-testid=mto-drill]").Should().BeEmpty();
    }

    [Fact]
    public void An_overview_selection_in_Finn_og_forsta_shows_exactly_its_scenarios()
    {
        var items = ScenarioBrowserPresentation.Items(
            [.. new[] { "tss-1", "tss-2", "tss-3" }.Select(id => new DiscoveredScenario { CandidateId = "c-" + id, ProjectKey = "library", Title = id, SourceIdentity = id })],
            new Dictionary<string, ScenarioBrowserPresentation.Prepared>());
        var f = ScenarioBrowserPresentation.FromOverview(["tss-2", "tss-3", "tss-3"], "P1 uten testtilfeller");
        ScenarioBrowserPresentation.Apply(items, f).Shown.Select(i => i.Candidate.SourceIdentity).Should().Equal("tss-2", "tss-3");
        ScenarioBrowserPresentation.HasFilters(f).Should().BeTrue();
        ScenarioBrowserPresentation.ResetFilters(f).Identities.Should().BeNull();
    }

    // ── US status and scenario type on Testoversikt (2026-11-26) ───────────────────────────────────────────────────────────

    private static ManualTestingOverview TypedOverview() => Overview() with
    {
        Specifications =
        [
            new(Skole, "SkoleService", "001-skoleservice", [new("tss-1", "k1", "US1", "Report", 1, "P1", "A", ScenarioRequirementType.Functional),
                new("tss-2", "k1", "US1", "Report", 2, "P1", "B", ScenarioRequirementType.NonFunctional, [QualityAttribute.Performance, QualityAttribute.Reliability]),
                new("tss-3", "k2", "US2", "Lock", 1, "P2", "C", ScenarioRequirementType.Mixed, [QualityAttribute.Performance]),
                new("tss-r", null, null, null, null, null, "FR-099", RequirementOnly: true)]),
            new(Front, "Frontend", "002-child-search-profile", [new("tss-f1", "f1", "US1", "Search", 1, null, "F", ScenarioRequirementType.Functional)]),
        ],
        StoryStatusAvailable = true,
        Stories =
        [
            new(Skole + "#US1", "US1", "Report", Skole, "SkoleService", UserStoryTaskStatus.InProgress, 8, 9, 1),
            new(Skole + "#US2", "US2", "Lock", Skole, "SkoleService", UserStoryTaskStatus.TasksCompleted, 4, 4, 0),
            new(Front + "#US1", "US1", "Search", Front, "Frontend", UserStoryTaskStatus.NeedsClarification, 1, 2, 1),
        ],
    };

    [Fact]
    public void Story_and_type_figures_count_unique_stories_and_acceptance_scenarios_once_and_follow_the_scope()
    {
        var o = TypedOverview();
        var project = O.Of(o, O.Scope.Project);
        (O.StoriesInProgress(project), O.StoriesWithOpenTasks(project), O.StoriesTasksCompleted(project), O.StoriesNeedingClarification(project)).Should().Be((1, 2, 1, 1));
        O.StoryImplementation(project).Select(b => (b.Key, b.Value)).Should().Equal(("InProgress", 1), ("TasksCompleted", 1), ("NeedsClarification", 1));
        O.StoryImplementation(project).Sum(b => b.Value).Should().Be(3, "mutually exclusive: every story once");
        O.ScenarioTypes(project).Select(b => (b.Key, b.Value)).Should().Equal(("Functional", 2), ("NonFunctional", 1), ("Mixed", 1));
        (O.ScenariosOfType(project, ScenarioRequirementType.Functional), O.ScenariosOfType(project, ScenarioRequirementType.NonFunctional)).Should().Be((2, 1),
            "a mixed scenario is never counted as non-functional; the requirement without a scenario is not a scenario");
        O.NonFunctionalCategories(project).Single(b => b.Key == "Performance").Value.Should().Be(2, "categories overlap: the non-functional and the mixed scenario");
        O.GapsByType(project).Select(b => (b.Key, b.Value)).Should().Equal([("NonFunctional", 1), ("Mixed", 1)], "tss-1 and tss-f1 have saved cases");
        var front = O.Of(o, new O.Scope(O.Level.Module, "Frontend", null));
        O.StoryImplementation(front).Select(b => b.Key).Should().Equal("NeedsClarification");
        O.DrillDown(project, O.Level.Project, "chart.usImplementation", "InProgress")!.Stories.Select(x => x.StoryId).Should().Equal("US1");
        O.DrillDown(project, O.Level.Project, "chart.nfrCategories", "Performance")!.Count.Should().Be(2);
        O.StoriesInProgress(O.Of(o with { StoryStatusAvailable = false }, O.Scope.Project)).Should().BeNull("unavailable is «—», never 0");
    }

    [Fact]
    public void A_story_status_bar_opens_its_stories_and_Finn_og_forsta_with_that_status_filter()
    {
        Setup([Summary()], Discovery(3));
        _plans.Setup(x => x.OverviewAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((TypedOverview(), (string?)null));
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ManualTestingDashboardSettings
            { Visible = new() { ["chart.usImplementation"] = true, ["metric.usInProgress"] = true } }, (string?)null));
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));
        cut.Find("[data-testid=mto-kpi][data-widget='metric.usInProgress'] .mto-kpi-v").TextContent.Should().Be("1");
        cut.FindAll("[data-testid=mto-kpi][data-widget='metric.functionalScenarios']").Should().BeEmpty("optional figures are off by default");
        cut.Find("[data-widget='chart.usImplementation'] [data-testid=mto-bar][data-key=InProgress] [data-testid=mto-bar-open]").Click();
        cut.FindAll("[data-testid=mto-drill-story]").Select(b => b.TextContent.Trim()).Should().Equal("US1 Report");
        cut.Find("[data-testid=mto-drill-open]").Click();
        // Without the shared selection nothing opens (the explorer is not registered in this test).
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-drill-error]"));
    }

    // ── Hierarchical plans: the structure of a new plan from a selection (2026-11-26) ─────────────────────────────────────────

    [Fact]
    public void A_new_plan_from_a_selection_previews_its_suite_tree_and_creates_it_with_the_chosen_structure()
    {
        var discovery = Discovery(3) with
        {
            Candidates =
            [
                new DiscoveredScenario { CandidateId = "c1", ProjectKey = "library", Title = "Gyldig rapport", Type = ScenarioCandidateType.BusinessE2E, Area = "Rapportering" },
                new DiscoveredScenario { CandidateId = "c2", ProjectKey = "library", Title = "Gyldig XML", Type = ScenarioCandidateType.Integration, Area = "Meldingsmottak" },
                new DiscoveredScenario { CandidateId = "c3", ProjectKey = "library", Title = "Ikke valgt", Type = ScenarioCandidateType.Integration, Area = "Annet" },
            ],
        };
        Setup([Summary()], discovery);
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ManualTestingDashboardSettings(), (string?)null));
        _plans.Setup(x => x.CreateProjectAsync(It.IsAny<CreateProjectTestPlanRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TestPlanDraft { Id = PlanId, PlanName = "Ny" }, (string?)null));
        JSInterop.Setup<string?>("sessionStorage.getItem", TabDraftStore.SelectionKey("library")).SetResult(System.Text.Json.JsonSerializer.Serialize(new List<string> { "c1", "c2" }));
        var cut = Open<ManualTesting>("manual-testing/plans");
        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.Find("[data-testid=mt-new-plan-selection]").Change(true);
        cut.FindAll("[data-testid=mt-new-plan-suite]").Select(e => (e.TextContent.Split('·')[0].Trim(), e.GetAttribute("data-depth"))).Should().Equal(
            ("Ende til ende", "0"), ("Rapportering", "1"), ("Integrasjon", "0"), ("Meldingsmottak", "1"));
        cut.Find("[data-testid=mt-new-plan-e2e-candidates]").TextContent.Should().Contain("kandidater");
        cut.Find("[data-testid=mt-new-plan-structure]").Change(PlanStructureTemplates.None);
        cut.FindAll("[data-testid=mt-new-plan-suite]").Should().OnlyContain(e => e.GetAttribute("data-depth") == "0");
        cut.Find("[data-testid=mt-new-plan-structure]").Change(PlanStructureTemplates.LevelArea);
        cut.Find("[data-testid=mt-new-plan-create]").Click();
        _plans.Verify(x => x.CreateProjectAsync(It.Is<CreateProjectTestPlanRequest>(r => r.Structure == PlanStructureTemplates.LevelArea && r.CandidateIds.SequenceEqual(new[] { "c1", "c2" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Testoversikt redesign (2026-11-26) ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void R01_the_overview_is_ordered_by_hierarchy_core_figures_first_and_keeps_every_widget_id()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));

        cut.Find("[data-testid=mt]").ClassList.Should().Contain("mto-page", "Testoversikt uses the full content width");
        cut.FindAll("[data-testid=mto-kpis] [data-testid=mto-kpi]").Select(k => k.GetAttribute("data-widget")).Should().Equal(
            "metric.scenarios", "metric.testCases", "metric.approved", "metric.ready");
        cut.FindAll("[data-testid=mto-band]").Select(b => b.GetAttribute("data-band")).Should().Equal("action", "readiness", "modules", "more");
        cut.Find("[data-testid=mto-band][data-band=action] h2").TextContent.Should().Be("Dette bør følges opp");
        cut.FindAll("[data-testid=mto-band][data-band=readiness] [data-testid=mto-widget]").Select(w => w.GetAttribute("data-widget")).Should().Equal("chart.preparation", "chart.priority", "chart.coverageGaps");
        cut.Find("[data-testid=mto-band][data-band=modules] [data-testid=mto-widget][data-widget='display.moduleOverview']").Should().NotBeNull();
    }

    [Fact]
    public void R02_meters_show_real_shares_as_text_and_never_for_an_empty_base()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));

        cut.Find("[data-testid=mto-kpi][data-widget='metric.approved'] [data-testid=mto-meter]").GetAttribute("aria-label").Should().Be("2 av 7 godkjent (29 %)");
        cut.Find("[data-testid=mto-kpi][data-widget='metric.ready'] [data-testid=mto-meter]").GetAttribute("aria-label").Should().Be("1 av 7 klar (14 %)");
        cut.Find("[data-testid=mto-kpi][data-widget='metric.scenarios'] [data-testid=mto-meter]").GetAttribute("aria-label").Should().StartWith("2 av 4 med testtilfeller");
        cut.Markup.Should().NotContain("denne uken", "no invented trend");
    }

    [Fact]
    public void R03_the_follow_up_section_lists_real_follow_up_with_working_drill_down()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-band][data-band=action]"));

        cut.FindAll("[data-testid=mto-action-empty]").Should().BeEmpty("the fixture has a changed source and a hindrance");
        var band = cut.Find("[data-testid=mto-band][data-band=action]");
        band.QuerySelectorAll("[data-testid=mto-widget]").Select(w => w.GetAttribute("data-widget")).Should().Contain(["chart.blockers", "chart.sourceChanges"]);
        cut.Find("[data-testid=mto-band][data-band=action] [data-testid=mto-widget][data-widget='chart.sourceChanges'] [data-testid=mto-bar-open]").Click();
        cut.Find("[data-testid=mto-drill]").GetAttribute("data-chart").Should().Be("chart.sourceChanges");
    }

    [Fact]
    public void R04_without_follow_up_the_section_says_so_instead_of_inventing_warnings()
    {
        Setup([Summary()], Discovery(3));
        var empty = Overview() with { Cases = [] };
        _plans.Setup(x => x.OverviewAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((empty with { Specifications = [.. empty.Specifications.Select(s => s with { Scenarios = [.. s.Scenarios.Select(sc => sc with { Priority = "P3" })] })] }, (string?)null));
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ManualTestingDashboardSettings(), (string?)null));
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-band][data-band=action]"));

        cut.Find("[data-testid=mto-action-empty]").TextContent.Should().StartWith("Ingenting i utvalget krever oppfølging nå");
    }

    [Fact]
    public void R05_a_long_module_comparison_shows_the_first_eight_and_Vis_alle_reaches_every_module()
    {
        Setup([Summary()], Discovery(3));
        var many = Overview() with
        {
            Specifications = [.. Enumerable.Range(1, 11).Select(i => new OverviewSpecification($"M{i:00}/specs/001-x/spec.md", $"M{i:00}", "001-x", [new($"tss-m{i}", $"m{i}", "US1", "S", 1, "P2", "A")]))],
        };
        _plans.Setup(x => x.OverviewAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((many, (string?)null));
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ManualTestingDashboardSettings(), (string?)null));
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-widget][data-widget='chart.moduleCoverage']"));

        string Sel(string w) => $"[data-testid=mto-widget][data-widget='{w}']";
        var total = O.Coverage(O.Of(many, new O.Scope(O.Level.Project, null, null)), O.Level.Project).Count;
        cut.FindAll($"{Sel("chart.moduleCoverage")} [data-testid=mto-bar]").Should().HaveCount(8);
        cut.Find($"{Sel("chart.moduleCoverage")} [data-testid=mto-show-all]").TextContent.Should().Be($"Vis alle ({total})");
        cut.Find($"{Sel("chart.moduleCoverage")} [data-testid=mto-show-all]").Click();
        cut.FindAll($"{Sel("chart.moduleCoverage")} [data-testid=mto-bar]").Should().HaveCount(total, "no module becomes unreachable");
        cut.FindAll("[data-testid=mto-row]").Count.Should().BeGreaterThanOrEqualTo(11, "the table always lists every module");
    }

    [Fact]
    public void R06_the_stylesheet_lifts_the_width_cap_for_Testoversikt_only_and_uses_container_aware_columns()
    {
        var css = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Web", "wwwroot", "css", "manual-testing.css"));
        css.Should().Contain(".mt.mto-page { max-width: none; container: mto / inline-size; }");
        css.Should().Contain(".mt { max-width: 72rem; }", "other Manual Testing pages keep their reading width");
        css.Should().Contain(".mto-grid.mto-grid-3 > .mto-card { flex: 1 1 22rem; }", "cards fill each row: two cards never leave an empty column").And.Contain("@container mto (max-width: 30rem)");
        css.Should().Contain(".mto-kpis-core { grid-template-columns: repeat(4, minmax(0, 1fr));");
    }

    // ── Testoversikt v2 (2026-11-26) ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void V01_legacy_html_bars_are_gone_and_charts_are_svg_with_axis_values_outside_and_a_text_alternative()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-kpis]"));
        cut.FindAll(".dd-bars").Should().BeEmpty("no legacy HTML bar widget on Testoversikt");
        var priority = cut.Find("[data-testid=mto-widget][data-widget='chart.priority']");
        priority.QuerySelector("svg.dbc-svg rect.dbc-bar").Should().NotBeNull();
        priority.QuerySelectorAll(".dbc-tick").Should().NotBeEmpty("an x-axis with tick values");
        priority.QuerySelectorAll("[data-testid=mto-bar] [data-testid=mto-bar-value]").Select(v => v.TextContent).Should().OnlyContain(t => t.Length > 0, "values are printed outside the bars");
        priority.QuerySelector("table.dbc-sr caption")!.TextContent.Should().Be("Scenarioer etter prioritet", "a text alternative for screen readers");
        priority.QuerySelectorAll("[data-testid=mto-bar]").Select(r => r.GetAttribute("data-key")).Should().StartWith(["P1", "P2"], "business order, never alphabetical");
    }

    [Fact]
    public void V02_follow_up_rows_show_real_counts_and_open_the_same_drill_down()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-band][data-band=action]"));
        var source = cut.Find("[data-testid=mto-widget][data-widget='chart.sourceChanges']");
        source.QuerySelectorAll("[data-testid=mto-bar]").Should().OnlyContain(r => r.QuerySelector(".mto-action-count")!.TextContent != "0", "only rows with a count");
        cut.Find("[data-testid=mto-widget][data-widget='chart.sourceChanges'] [data-testid=mto-bar-open]").Click();
        cut.Find("[data-testid=mto-drill]").GetAttribute("data-chart").Should().Be("chart.sourceChanges");
    }

    [Fact]
    public void V03_plan_cards_show_plan_specific_execution_and_remaining_ACs_never_a_global_result()
    {
        Setup([Summary()], Discovery(3));
        var ov = Overview() with { Plans = [Overview().Plans[0] with { SprintLabel = "Sprint 12", Execution = new PlanExecutionCounts(4, 1, 2, 1, 0) }] };
        _plans.Setup(x => x.OverviewAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((ov, (string?)null));
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ManualTestingDashboardSettings(), (string?)null));
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-plan-card]"));
        var card = cut.Find("[data-testid=mto-plan-card]");
        card.TextContent.Should().Contain("Sprint 12");
        var exec = card.QuerySelector("[data-testid=mto-plan-exec]")!;
        (exec.GetAttribute("data-passed"), exec.GetAttribute("data-failed"), exec.GetAttribute("data-notrun")).Should().Be(("2", "1", "1"));
        exec.QuerySelector("svg")!.GetAttribute("aria-label").Should().Contain("i denne planen");
        cut.Find("[data-testid=mto-widget][data-widget='panel.planExecution']").TextContent.Should().Contain("ikke kjørt i «Library – test plan»").And.Contain("1 feilet");
        cut.Find("[data-testid=mto-plan-card] a.mto-plan-name").GetAttribute("href").Should().Be($"{M.PlanRoute}?draft={PlanId}");
    }

    [Fact]
    public void V04_the_module_table_sorts_by_a_column_and_back()
    {
        Setup([Summary()], Discovery(3));
        SetupOverview();
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mto-row]").Count.Should().Be(3));
        cut.Find("[data-testid=mto-sort][data-col=cases]").Click();
        cut.FindAll("[data-testid=mto-row]").Select(r => r.GetAttribute("data-key")).First().Should().Be("SkoleService", "most test cases first");
        cut.Find("[data-testid=mto-sort][data-col=cases]").Click();
        cut.FindAll("[data-testid=mto-row]").Select(r => r.GetAttribute("data-key")).Last().Should().Be("SkoleService");
        cut.Find("th[aria-sort=ascending] [data-testid=mto-sort]").GetAttribute("data-col").Should().Be("cases");
    }

    [Fact]
    public void V05_nice_axis_maximum_and_ticks()
    {
        BirkNext.Web.Components.DashboardBarChart.NiceMax(842).Should().Be(1000);
        BirkNext.Web.Components.DashboardBarChart.NiceMax(325).Should().Be(500);
        BirkNext.Web.Components.DashboardBarChart.NiceMax(7).Should().Be(10);
        BirkNext.Web.Components.DashboardBarChart.NiceMax(0).Should().Be(1);
        BirkNext.Web.Components.DashboardBarChart.Ticks(1000).Should().Equal(0, 250, 500, 750, 1000);
    }

    // ── Custom KPI tiles (2026-11-26) ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One story (US2) with ACs of different types, a requirement without a scenario, the same AC listed twice, and standalone documented
    /// E2E tests of a test specification (no story).</summary>
    private static ManualTestingOverview TileOverview() => Overview() with
    {
        Specifications =
        [
            new(Skole, "SkoleService", "001-skoleservice", [
                new("ac-1", "k2", "US2", "Lock", 1, "P1", "A", ScenarioRequirementType.Functional),
                new("ac-2", "k2", "US2", "Lock", 2, "P1", "B", ScenarioRequirementType.NonFunctional),
                new("ac-3", "k2", "US2", "Lock", 3, "P2", "C", ScenarioRequirementType.Mixed),
                new("ac-4", "k2", "US2", "Lock", 4, "P2", "D", ScenarioRequirementType.Unclassified),
                new("req-1", null, null, null, null, null, "FR-099", RequirementOnly: true)]),
            new(Front, "Frontend", "002-child-search-profile", [new("ac-1", "k2", "US2", "Lock", 1, "P1", "A", ScenarioRequirementType.Functional)]),
            new("SkoleService/docs/Skole – Testspesifikasjon.md", "SkoleService", "Skole – Testspesifikasjon", [
                new("doc-e1", null, null, null, null, null, "TEST-E-01: Full flyt", ScenarioRequirementType.Functional) { Level = ScenarioTestLevel.EndToEnd, LevelDeclared = true, Standalone = true },
                new("doc-e2", null, null, null, null, null, "TEST-E-02: Svartid", ScenarioRequirementType.NonFunctional) { Level = ScenarioTestLevel.EndToEnd, LevelDeclared = true, Standalone = true }]),
        ],
    };

    [Fact]
    public void T01_tiles_count_unique_scenarios_on_one_dimension_never_user_stories_requirements_or_duplicates()
    {
        var project = O.Of(TileOverview(), O.Scope.Project);
        var e2e = DashboardKpiTiles.Defaults.Single();
        (e2e.Id, DashboardKpiTiles.DisplayTitle(e2e), e2e.Dimension, e2e.Metric).Should().Be(("tile.e2e", "Ende-til-ende-tester", KpiTileDimension.TestLevel, "EndToEnd"));
        O.TileScenarios(project).Should().HaveCount(6, "4 ACs (ac-1 once although listed twice) + 2 standalone tests; the requirement and the story are not scenarios");
        O.TileCount(project, e2e).Should().Be(2, "the documented TEST-E tests; the acceptance scenarios state no level");
        O.TileDeclared(project, e2e).Should().Be(2, "declared by the document, not verified");
        var functional = new DashboardKpiTile("t2", "Funksjonelle", KpiTileDimension.ScenarioType, "Functional");
        var nonFunctional = new DashboardKpiTile("t3", null, KpiTileDimension.ScenarioType, "NonFunctional");
        (O.TileCount(project, functional), O.TileCount(project, nonFunctional)).Should().Be((2, 2), "ac-1 + doc-e1; ac-2 + doc-e2 — each AC by its OWN type, US2 is never counted");
        O.TileCount(project, new("t4", null, KpiTileDimension.ScenarioType, "Mixed")).Should().Be(1);
        O.TileCount(project, new("t5", null, KpiTileDimension.ScenarioType, "Unclassified")).Should().Be(1);
        O.TileCount(project, new("t6", null, KpiTileDimension.TestLevel, "Unclassified")).Should().Be(4, "test level is independent of the type");
        O.TileCount(project, functional with { Title = "Helt annet navn" }).Should().Be(2, "the title never changes the count");
        O.TileCount(O.Of(TileOverview(), new O.Scope(O.Level.Module, "Frontend", null)), functional).Should().Be(1, "the scope changes the count, not the metric");
        O.TileCount(O.Of(TileOverview() with { ScenariosAvailable = false }, O.Scope.Project), e2e).Should().BeNull("unavailable is «—», never an invented 0");
        (O.TileFilter(e2e).TestLevel, O.TileFilter(e2e).ScenarioType, O.TileFilter(nonFunctional).ScenarioType, O.TileFilter(nonFunctional).TestLevel)
            .Should().Be(("EndToEnd", (string?)null, "NonFunctional", (string?)null), "a tile filters on exactly its own dimension");
    }

    [Fact]
    public void T02_tile_settings_are_validated_titles_cleaned_and_ids_stable()
    {
        var list = DashboardKpiTiles.Normalize([
            new("tile.e2e", "  E2E   –  Skolenærvær ", KpiTileDimension.TestLevel, "EndToEnd", Order: 5),
            new("tile.b", "   ", KpiTileDimension.ScenarioType, "NonFunctional", Order: 1),
            new("tile.c", new string('x', 90), KpiTileDimension.TestLevel, "EndToEnd", Order: 9),
        ]);
        list.Select(t => (t.Id, t.Order)).Should().Equal(("tile.b", 0), ("tile.e2e", 1), ("tile.c", 2));
        list[1].Title.Should().Be("E2E – Skolenærvær");
        list[0].Title.Should().BeNull("an empty name falls back to the metric's default");
        DashboardKpiTiles.DisplayTitle(list[0]).Should().Be("Ikke-funksjonelle scenarioer");
        list[2].Title!.Length.Should().Be(DashboardKpiTiles.MaxTitle);
        list.Count(t => t.Dimension == KpiTileDimension.TestLevel && t.Metric == "EndToEnd").Should().Be(2, "two tiles may measure the same metric");
        FluentActions.Invoking(() => DashboardKpiTiles.Normalize([new("a", null, KpiTileDimension.TestLevel, "EndToEnd"), new("a", null, KpiTileDimension.TestLevel, "Component")]))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => DashboardKpiTiles.Normalize([new("a", null, KpiTileDimension.TestLevel, "Functional")])).Should().Throw<ArgumentException>("Functional is a scenario type, not a test level");
        FluentActions.Invoking(() => DashboardKpiTiles.Normalize([new("<b>", null, KpiTileDimension.TestLevel, "EndToEnd")])).Should().Throw<ArgumentException>();
        new ManualTestingDashboardSettings().EffectiveTiles.Should().Equal(DashboardKpiTiles.Defaults);
        new ManualTestingDashboardSettings { Tiles = [] }.EffectiveTiles.Should().BeEmpty("removing every tile is an explicit choice, not «use the defaults»");
    }

    [Fact]
    public void T03_the_default_tile_shows_its_count_renames_through_the_dialog_and_saves_only_the_dashboard_setup()
    {
        Setup([Summary()], Discovery(3));
        _plans.Setup(x => x.OverviewAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((TileOverview(), (string?)null));
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ManualTestingDashboardSettings(), (string?)null));
        SaveManualTestingDashboardSettingsRequest? sent = null;
        _plans.Setup(x => x.SaveDashboardSettingsAsync(It.IsAny<SaveManualTestingDashboardSettingsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SaveManualTestingDashboardSettingsRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync((SaveManualTestingDashboardSettingsRequest r, CancellationToken _) => (new ManualTestingDashboardSettings { Tiles = r.ResetTiles ? null : r.Tiles }, (string?)null));
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mto-tile]"));
        var tile = cut.Find("[data-testid=mto-tile]");
        (tile.GetAttribute("data-tile"), tile.GetAttribute("data-dimension"), tile.GetAttribute("data-metric")).Should().Be(("tile.e2e", "TestLevel", "EndToEnd"));
        cut.Find("[data-testid=mto-tile-title]").TextContent.Should().Be("Ende-til-ende-tester");
        cut.Find("[data-testid=mto-tile-count]").TextContent.Should().Be("2");
        cut.Find("[data-testid=mto-tile-metric]").TextContent.Should().Be("Testnivå · Ende til ende");
        cut.Find("[data-testid=mto-tile-explain]").TextContent.Should().Contain("dokumentert som E2E i kilden – ikke verifisert");

        // The menu is a sibling of the clickable area: opening it never navigates.
        var before = Services.GetRequiredService<NavigationManager>().Uri;
        cut.Find("[data-testid=mto-tile-menu]").Click();
        Services.GetRequiredService<NavigationManager>().Uri.Should().Be(before);
        cut.Find("[data-testid=mto-tile-edit]").Click();
        var dialog = cut.Find("[data-testid=kte-dialog]");
        (dialog.GetAttribute("role"), dialog.GetAttribute("aria-modal")).Should().Be(("dialog", "true"));
        cut.Find("[data-testid=kte-title]").Input("<b>E2E – Skolenærvær</b>");
        cut.Find("[data-testid=kte-preview-count]").TextContent.Should().Be("2");
        cut.Find("[data-testid=kte-metric]").Change("ScenarioType|Functional");
        cut.Find("[data-testid=kte-preview-count]").TextContent.Should().Be("2", "the preview recalculates for the chosen metric (ac-1 + doc-e1)");
        cut.Find("[data-testid=kte-save]").Click();

        sent!.Tiles!.Single().Should().Be(new DashboardKpiTile("tile.e2e", "<b>E2E – Skolenærvær</b>", KpiTileDimension.ScenarioType, "Functional", true, 0), "same stable id; the name is a label");
        sent.Visible.Should().BeNull("no other dashboard part changes");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=kte]").Should().BeEmpty());
        cut.Find("[data-testid=mto-tile-title]").TextContent.Should().Be("<b>E2E – Skolenærvær</b>", "shown as text, never as markup");
        cut.Find("[data-testid=mto-tile-title]").QuerySelector("b").Should().BeNull();
        _plans.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void T04_add_hide_remove_and_reset_change_only_the_tile_list_and_the_tile_opens_Finn_og_forsta()
    {
        Setup([Summary()], Discovery(3));
        _plans.Setup(x => x.OverviewAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((TileOverview(), (string?)null));
        var stored = new ManualTestingDashboardSettings { Tiles = [.. DashboardKpiTiles.Defaults, new("tile.nf", "Ikke-funksjonelle", KpiTileDimension.ScenarioType, "NonFunctional", true, 1)] };
        _plans.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((stored, (string?)null));
        var requests = new List<SaveManualTestingDashboardSettingsRequest>();
        _plans.Setup(x => x.SaveDashboardSettingsAsync(It.IsAny<SaveManualTestingDashboardSettingsRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SaveManualTestingDashboardSettingsRequest, CancellationToken>((r, _) => requests.Add(r))
            .ReturnsAsync((SaveManualTestingDashboardSettingsRequest r, CancellationToken _) => (new ManualTestingDashboardSettings { Tiles = r.ResetTiles ? null : r.Tiles }, (string?)null));
        var cut = Open<ManualTesting>("manual-testing");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mto-tile]").Should().HaveCount(2));
        cut.FindAll("[data-testid=mto-tile-count]").Select(e => e.TextContent).Should().Equal("2", "2");

        cut.Find("[data-testid=mto-tile][data-tile='tile.nf'] [data-testid=mto-tile-menu]").Click();
        cut.Find("[data-testid=mto-tile-hide]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mto-tile]").Select(t => t.GetAttribute("data-tile")).Should().Equal("tile.e2e"));
        requests[^1].Tiles!.Single(t => t.Id == "tile.nf").Visible.Should().BeFalse("hidden, not removed");

        cut.Find("[data-testid=mto-tile-menu]").Click();
        cut.Find("[data-testid=mto-tile-add]").Click();
        cut.Find("[data-testid=kte-metric]").Change("TestLevel|Integration");
        cut.Find("[data-testid=kte-save]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mto-tile]").Should().HaveCount(2));
        var added = requests[^1].Tiles!.Last();
        (added.Id.StartsWith("tile."), added.Title, added.Metric, added.Order).Should().Be((true, (string?)null, "Integration", 2));
        cut.Find("[data-testid=mto-tile][data-metric=Integration] [data-testid=mto-tile-title]").TextContent.Should().Be("Integrasjonstester");

        cut.Find("[data-testid=mto-tile][data-metric=Integration] [data-testid=mto-tile-menu]").Click();
        cut.Find("[data-testid=mto-tile-remove]").Click();
        cut.WaitForAssertion(() => requests[^1].Tiles!.Select(t => t.Id).Should().Equal("tile.e2e", "tile.nf"));

        cut.Find("[data-testid=mto-tile-menu]").Click();
        cut.Find("[data-testid=mto-tile-reset]").Click();
        cut.WaitForAssertion(() => requests[^1].ResetTiles.Should().BeTrue());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mto-tile]").Select(t => t.GetAttribute("data-tile")).Should().Equal("tile.e2e"));
        requests.Should().OnlyContain(r => r.Visible == null && r.ResetToDefaults == false, "the other dashboard parts are never reset by the tile actions");

        cut.Find("[data-testid=mto-tile-open]").Click();
        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("manual-testing/discover"));
        _plans.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
