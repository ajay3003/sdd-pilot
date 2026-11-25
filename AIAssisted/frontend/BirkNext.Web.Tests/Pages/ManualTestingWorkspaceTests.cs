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
        M.Next(F()).Should().Match<M.NextAction>(n => n.Label == "Oppdag scenarioer" && n.Href == "manual-testing/discover");
        M.Next(F(d: Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Velg scenarioer" && n.Href == "manual-testing/discover",
            "scenarios are chosen where they are browsed (scenario-first)");
        M.Next(F([Summary(clarify: 1)], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Avklar testtilfeller" && n.Href.Contains($"draft={PlanId}"));
        M.Next(F([Summary()], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Eksporter til Azure DevOps" && n.Href == $"manual-testing/plan?draft={PlanId}&panel=export"
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
        M.Next(F([Summary()], Discovery(4))).Label.Should().Be("Eksporter til Azure DevOps", "prepared work is never held back by the choice");
        M.Steps(F())[0].Evidence.Should().Contain("Specification not chosen yet");
    }

    [Fact]
    public void Two_stages_are_never_all_blocked_and_their_counts_come_from_records()
    {
        M.Stages.Select(s => s.Id).Should().Equal("discover", "plan");
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
    public void The_home_names_the_project_the_next_step_the_stages_and_plans_and_never_runs()
    {
        Setup([Summary()], Discovery(3));
        var cut = Open<ManualTesting>("manual-testing");

        cut.Find("[data-testid=twh-project]").TextContent.Should().Contain("Library");
        cut.Find("[data-testid=twh-stage][data-stage=home]").GetAttribute("aria-current").Should().Be("page");
        cut.FindAll("[data-testid=twh-stage]").Select(a => a.GetAttribute("href")).Should().Equal("manual-testing", "manual-testing/discover", "manual-testing/plan");
        cut.Find("[data-testid=mt-next-action]").Should().Match<AngleSharp.Dom.IElement>(a => a.TextContent == "Eksporter til Azure DevOps"
            && a.GetAttribute("href") == $"manual-testing/plan?draft={PlanId}&panel=export");
        cut.FindAll("[data-testid=mt-stage]").Should().BeEmpty("the stages are navigated once, in the header (no duplicate workflow navigation)");
        cut.FindAll("[data-testid=twh-stage]").Should().HaveCount(3);
        cut.Find("[data-testid=mt-plan]").TextContent.Should().Contain("Library – test plan").And.Contain("2 testtilfeller").And.Contain("1 godkjent");
        cut.Find("[data-testid=mt-count-suggestions]").TextContent.Should().Be("3", "suggestions are counted as suggestions");
        cut.Find("[data-testid=mt-count-cases]").TextContent.Should().Be("2", "and saved test cases as test cases — never one for the other");
        cut.FindAll("[data-testid=mt-runs]").Should().BeEmpty("BirkNext records no manual runs");
        cut.Markup.Should().Contain("et forberedt, godkjent eller eksportert testtilfelle er ikke en kjørt test").And.NotContain("manual-testing/execute");
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

    // ── Retired Execute & document links ─────────────────────────────────────────────────────────────────────

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

    [Fact]
    public void The_landing_page_shows_what_is_missing_before_tests_can_start_with_denominators_and_never_an_execution_number()
    {
        Setup([new(PlanId, "project:library", "Library – test plan", null, 3, 10, 4, 1, 2, 6, 7, "anna", DateTimeOffset.UtcNow)], Discovery(4));
        var cut = Open<ManualTesting>("manual-testing");
        cut.Find("[data-testid=mt-count-missing-expected]").TextContent.Should().Be("2 av 10");
        cut.Find("[data-testid=mt-count-missing-data]").TextContent.Should().Be("6 av 10");
        cut.Find("[data-testid=mt-count-blocked]").TextContent.Should().Be("7 av 10");
        cut.Find("[data-testid=mt-readiness-counts]").TextContent.Should().Contain("Ikke klar til kjøring").And.Contain("Uten koblet testdata");
        cut.Find("[data-testid=mt-count-executed]").TextContent.Should().Be("Registreres i Azure DevOps", "no execution or pass count is invented");
    }
}
