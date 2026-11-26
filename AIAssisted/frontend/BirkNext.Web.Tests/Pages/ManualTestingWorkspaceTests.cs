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
}
