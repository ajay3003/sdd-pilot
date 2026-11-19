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
        _plans.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync((discovery, (string?)null));
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
    public void The_next_step_follows_the_stored_records_and_ends_with_the_export()
    {
        M.Facts F(List<TestPlanDraftSummary>? plans = null, ScenarioDiscoveryResult? d = null, string? project = "library") => new(project, "Library", plans ?? [], d, 3);

        M.Next(F(project: null)).Href.Should().Be("project-import");
        M.Next(F()).Should().Match<M.NextAction>(n => n.Label == "Discover what to test" && n.Href == "test-plan-suggestions?area=choose");
        M.Next(F(d: Discovery(4))).Label.Should().Be("Choose scenarios to prepare");
        M.Next(F([Summary(clarify: 1)], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Resolve cases that need clarification" && n.Href.Contains($"draft={PlanId}"));
        M.Next(F([Summary()], Discovery(4))).Should().Match<M.NextAction>(n => n.Label == "Export to Azure DevOps" && n.Href == $"test-plan-suggestions?draft={PlanId}&area=export"
            && n.Why.Contains("Azure DevOps Test Plans, where manual tests are executed"));
        M.Next(F([Summary(accepted: 0)], Discovery(4))).Label.Should().Be("Continue preparation", "nothing approved: nothing to export yet");
    }

    [Fact]
    public void In_a_monorepo_without_a_chosen_specification_choosing_it_comes_before_discovery()
    {
        const string choose = "This project contains 39 specifications in 14 modules and none is chosen yet.";
        M.Facts F(List<TestPlanDraftSummary>? plans = null, ScenarioDiscoveryResult? d = null) => new("m2lb", "M2LB", plans ?? [], d, null, choose);

        M.Next(F()).Should().Match<M.NextAction>(n => n.Label == "Choose the specification" && n.Href == "manual-testing/discover" && n.Why == choose);
        M.Next(F(d: Discovery(4))).Label.Should().Be("Choose the specification", "discovery without a specification finds no business scenarios");
        M.Next(F([Summary()], Discovery(4))).Label.Should().Be("Export to Azure DevOps", "prepared work is never held back by the choice");
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
        cut.FindAll("[data-testid=twh-stage]").Select(a => a.GetAttribute("href")).Should().Equal("manual-testing", "manual-testing/discover", "test-plan-suggestions");
        cut.Find("[data-testid=mt-next-action]").Should().Match<AngleSharp.Dom.IElement>(a => a.TextContent == "Export to Azure DevOps"
            && a.GetAttribute("href") == $"test-plan-suggestions?draft={PlanId}&area=export");
        cut.FindAll("[data-testid=mt-stage]").Select(s => s.GetAttribute("data-step")).Should().Equal("discover", "plan");
        cut.Find("[data-testid=mt-plan]").TextContent.Should().Contain("Library – test plan").And.Contain("2 cases").And.Contain("1 approved");
        cut.FindAll("[data-testid=mt-runs]").Should().BeEmpty("BirkNext records no manual runs");
        cut.Markup.Should().Contain("A prepared, approved or exported test case is not an executed test").And.NotContain("manual-testing/execute");
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
    [InlineData("manual-testing/execute?draft=44444444-4444-4444-4444-444444444444&case=tsc-borrow", "test-plan-suggestions?draft=44444444-4444-4444-4444-444444444444&area=prepare&case=tsc-borrow")]
    [InlineData("manual-testing/execute?run=55555555-5555-5555-5555-555555555555", "manual-testing")]
    [InlineData("manual-testing/execute", "manual-testing")]
    public void An_old_Execute_and_document_link_lands_on_the_saved_case_or_on_Manual_Testing(string from, string to)
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(from);

        Render<ManualTestingExecuteRedirect>();

        navigation.Uri.Should().Be(navigation.BaseUri + to);
    }
}
