using BirkNext.TestPlanning;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Reordering and removing in Plan &amp; prepare: scenarios and test cases move with ↑/↓ (keyboard) or by dragging their handle, steps by dragging;
/// the save carries the new order; a never-reviewed scenario is removed, a reviewed one is excluded with a reason through the server (nothing
/// deleted).
/// </summary>
public sealed class PrepareReorderTests : BunitContext
{
    private static readonly Guid DraftId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private readonly List<SaveTestPlanDraftRequest> _saves = [];
    private readonly List<ExcludeScenarioRequest> _excluded = [];

    private static TestCaseDraft Discovered(string id, TestCaseReviewState review = TestCaseReviewState.Generated) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "skole", ScenarioId = id, Fingerprint = "f" }, SuiteId = "suite:s", Review = review,
        ReviewHistory = review == TestCaseReviewState.Generated ? [] : [new TestCaseReviewEvent(review, "lead", null, DateTimeOffset.UtcNow)],
        Generated = new TestCaseContent { Title = "Scenario " + id, Purpose = "p", Capability = "specification", Steps = ["first", "second", "third"], StepExpected = ["1", "2", "3"] },
    };

    private static TestCaseDraft Written(string id, string scenario) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "skole", ScenarioId = id }, SuiteId = "suite:s", ScenarioKey = scenario, Generated = new TestCaseContent { Title = "Case " + id },
    };

    private static TestPlanDraft Draft(TestCaseReviewState s3Review = TestCaseReviewState.Generated) => new()
    {
        Id = DraftId, GuideKey = "project:skole", ProjectKey = "skole", ProjectName = "S", PlanName = "Plan", Revision = 2, Suites = [new("suite:s", "S", "area", null)],
        Cases = [Discovered("s1"), Written("m1", "s1"), Written("m2", "s1"), Discovered("s2"), Discovered("s3", s3Review)],
    };

    private IRenderedComponent<TestPlanSuggestions> Render(TestPlanDraft draft, string query = "")
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "skole", "S")).Object);
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.ProjectAsync("skole", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TestPlanWorkspace
        {
            ProjectKey = "skole", GuideKey = "project:skole", CanWrite = true, RuleVersion = "tps-1",
            Drafts = [new(DraftId, "project:skole", "Plan", null, draft.Revision, draft.Cases.Count, 0, 0, 0, 1, 1, "t", DateTimeOffset.UtcNow)],
        }, (string?)null));
        api.Setup(x => x.DraftAsync(DraftId, It.IsAny<CancellationToken>())).ReturnsAsync((draft, (string?)null));
        api.Setup(x => x.SaveAsync(DraftId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => _saves.Add(r))
            .ReturnsAsync((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => (draft with { Revision = r.BaseRevision + 1 }, (string?)null));
        api.Setup(x => x.ExcludeScenarioAsync(DraftId, It.IsAny<ExcludeScenarioRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, ExcludeScenarioRequest, CancellationToken>((_, r, _) => _excluded.Add(r)).ReturnsAsync((draft with { Revision = 3 }, (string?)null));
        Services.AddSingleton(api.Object);
        var explorers = new Mock<IArtifactExplorerContext>();
        explorers.Setup(c => c.GetStateAsync(It.IsAny<WorkspaceArtifactType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkspaceArtifactType role, CancellationToken _) => new ArtifactExplorerState(role, ExplorerArtifactStatus.Empty, "skole", "S", []));
        Services.AddSingleton(explorers.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        Services.AddSingleton(new Mock<IReviewContextProvider>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"test-plan-suggestions?draft={DraftId}&area=prepare{query}");
        var cut = Render<TestPlanSuggestions>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-scenarios]"));
        return cut;
    }

    private static List<string?> Scenarios(IRenderedComponent<TestPlanSuggestions> cut) =>
        [.. cut.FindAll("[data-testid=tps-scenario]").Select(s => s.GetAttribute("data-scenario"))];

    [Fact]
    public void Scenarios_and_cases_move_with_the_keyboard_buttons_and_the_save_carries_the_order()
    {
        var cut = Render(Draft());
        Scenarios(cut).Should().Equal("s1", "s2", "s3");
        cut.FindAll("[data-testid=tps-scenario-up]")[0].HasAttribute("disabled").Should().BeTrue("the first scenario cannot move up");
        cut.FindAll("[data-testid=tps-scenario-down]")[0].GetAttribute("aria-label").Should().Be("Move scenario Scenario s1 down");
        cut.FindAll("[data-testid=tps-scenario-down]")[0].Click();
        Scenarios(cut).Should().Equal("s2", "s1", "s3");

        cut.Find("[data-testid=tps-case-up-m2]").Click(); // s1 is open: m2 moves above m1
        cut.FindAll("[data-testid=tps-scenario]").Single(s => s.GetAttribute("data-scenario") == "s1").QuerySelectorAll("[data-case]").Select(c => c.GetAttribute("data-case"))
            .Should().Equal("s1", "m2", "m1");
        cut.Find("[data-testid=tps-save]").Click();

        cut.WaitForAssertion(() => _saves.Should().ContainSingle());
        _saves[0].ScenarioIds.Should().Equal("s2", "s1", "m2", "m1", "s3");
    }

    [Fact]
    public void Dragging_a_handle_onto_another_scenario_or_case_reorders_them()
    {
        var cut = Render(Draft());
        cut.Find("[data-testid=tps-scenario][data-scenario=s3] [data-testid=tps-scenario-drag]").DragStart();
        cut.Find("[data-testid=tps-scenario][data-scenario=s1]").Drop();
        Scenarios(cut).Should().Equal("s3", "s1", "s2");

        cut.Find("[data-testid=tps-case-drag-m2]").DragStart();
        cut.Find("[data-testid=tps-case-s1]").Drop();
        cut.FindAll("[data-testid=tps-scenario]").Single(s => s.GetAttribute("data-scenario") == "s1").QuerySelectorAll("[data-case]").Select(c => c.GetAttribute("data-case"))
            .Should().Equal(["s1", "m2", "m1"], "the scenario's own case stays first in the view; m2 now precedes m1");
        cut.Find("[data-testid=tps-save-state]").TextContent.Should().Be("Unsaved changes");
    }

    [Fact]
    public void Dragging_a_step_moves_it_with_its_expected_result()
    {
        var cut = Render(Draft(), "&case=s1");
        cut.Find("[data-testid=tps-step-drag-s1-2]").DragStart();
        cut.Find("[data-testid=tps-step-s1-0]").Drop();
        cut.Find("[data-testid=tps-step-action-s1-0]").GetAttribute("value").Should().Be("third");
        cut.Find("[data-testid=tps-step-expected-s1-0]").GetAttribute("value").Should().Be("3");
        cut.Find("[data-testid=tps-save]").Click();
        cut.WaitForAssertion(() => _saves.Should().ContainSingle());
        _saves[0].Cases.Single(c => c.ScenarioId == "s1").Edits.Steps.Should().Equal("third", "first", "second");
    }

    [Fact]
    public void A_never_reviewed_scenario_is_removed_and_a_reviewed_one_is_excluded_with_a_reason()
    {
        var cut = Render(Draft(s3Review: TestCaseReviewState.Accepted));
        cut.Find("[data-testid=tps-scenario][data-scenario=s2] [data-testid=tps-scenario-remove]").Click();
        Scenarios(cut).Should().Equal("s1", "s3");
        cut.Find("[data-testid=tps-message]").TextContent.Should().Contain("removed when you save").And.Contain("Earlier versions");
        cut.Find("[data-testid=tps-save]").Click();
        cut.WaitForAssertion(() => _saves.Should().ContainSingle());
        _saves[0].ScenarioIds.Should().NotContain("s2");

        var reviewed = cut.Find("[data-testid=tps-scenario][data-scenario=s3]");
        reviewed.QuerySelectorAll("[data-testid=tps-scenario-remove]").Should().BeEmpty("a reviewed case is never removed");
        reviewed.QuerySelector("[data-testid=tps-scenario-exclude]")!.Click();
        cut.Find("[data-testid=tps-scenario-exclude-confirm]").HasAttribute("disabled").Should().BeTrue("a reason is required");
        cut.Find("[data-testid=tps-scenario-exclude-reason]").Input("Covered by the integration suite");
        cut.Find("[data-testid=tps-scenario-exclude-confirm]").Click();
        cut.WaitForAssertion(() => _excluded.Should().ContainSingle(r => r.ScenarioKey == "s3" && r.Rationale == "Covered by the integration suite"));
    }
}
