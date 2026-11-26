using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Cross-page context hardening (2026-11-26, P0): «Forbered testtilfelle» for scenario B must never open scenario A's US/AC/case. The intent
/// travels as ?scenario=…; Planlegg opens only a case that belongs to it, says when it is not in the plan, and never adds or saves by itself.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    private static readonly CoverageUserStory StoryA = new() { Key = "specs/001/spec.md#us1", StoryId = "US1", Title = "Rapport", SpecificationPath = "specs/001/spec.md" };
    private static readonly CoverageUserStory StoryB = new() { Key = "specs/002/spec.md#us1", StoryId = "US1", Title = "Fravær", SpecificationPath = "specs/002/spec.md" };

    /// <summary>Scenario A (c1, US1/AC1 of spec 001) has a case; scenario B (cB, US1/AC1 of spec 002 — same numbering) is in the discovery only.</summary>
    private void SetupTwoScenarios(TestPlanDraft? plan = null)
    {
        Setup(plan ?? Plan(Case("c1", "Submit a valid report", story: StoryA)), selection: ["c1"]);
        _api.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ScenarioDiscoveryResult
        {
            DiscoveryId = "d1", ProjectKey = "library",
            Candidates =
            [
                new DiscoveredScenario { CandidateId = "c1", ProjectKey = "library", Title = "Submit a valid report", Story = StoryA, NumberInStory = 1 },
                new DiscoveredScenario { CandidateId = "cB", ProjectKey = "library", Title = "Register an absence", Story = StoryB, NumberInStory = 1 },
            ],
        }, (string?)null));
    }

    [Fact]
    public void X01_scenario_B_opens_as_B_never_with_scenario_As_case_and_nothing_is_added_or_saved_by_itself()
    {
        SetupTwoScenarios();
        var cut = Open("manual-testing/plan?scenario=cB");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-intent]"));
        var intent = cut.Find("[data-testid=mtp-intent]");
        (intent.GetAttribute("data-scenario"), intent.GetAttribute("data-in-plan")).Should().Be(("cB", "false"));
        cut.Find("[data-testid=mtp-intent-title]").TextContent.Should().Be("US1 · AC 1 — Register an absence");
        cut.FindAll("[data-testid=mtp-editor]").Should().BeEmpty("scenario A's case is never opened for scenario B");
        cut.Markup.Should().NotContain("value=\"Submit a valid report\"");
        cut.Find("[data-testid=mtp-intent-not-in-plan]").TextContent.Should().Contain("Ingenting er lagt til automatisk");
        _api.Verify(x => x.CreateProjectAsync(It.IsAny<CreateProjectTestPlanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _api.Verify(x => x.AddManualCaseAsync(It.IsAny<Guid>(), It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void X02_Legg_til_i_testplanen_adds_exactly_scenario_B_as_scenario_only_and_then_shows_B()
    {
        SetupTwoScenarios();
        CreateProjectTestPlanRequest? sent = null;
        _api.Setup(x => x.CreateProjectAsync(It.IsAny<CreateProjectTestPlanRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateProjectTestPlanRequest, CancellationToken>((r, _) => sent = r)
            .ReturnsAsync((Plan(Case("c1", "Submit a valid report", story: StoryA)) with { Revision = 4, PlannedScenarios = [new PlannedScenario { ScenarioId = "cB", Title = "Register an absence", SuiteId = "suite-1" }] }, (string?)null));
        var cut = Open("manual-testing/plan?scenario=cB");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-intent-add]"));
        cut.Find("[data-testid=mtp-intent-add]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-intent]").GetAttribute("data-in-plan").Should().Be("true"));
        (sent!.CandidateIds.Single(), sent.ScenarioOnly, sent.DraftId, sent.BaseRevision).Should().Be(("cB", true, PlanId, (int?)3));
        cut.Find("[data-testid=mtp-prep-item]").Should().NotBeNull("B is planned without a case: its «Klargjør AC» is shown");
        cut.FindAll("[data-testid=mtp-editor]").Should().BeEmpty();
    }

    [Fact]
    public void X03_scenario_A_opens_its_own_case_and_an_unknown_scenario_opens_nothing()
    {
        SetupTwoScenarios();
        var cut = Open("manual-testing/plan?scenario=c1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-editor]"));
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Submit a valid report");
        cut.Find("[data-testid=mtp-intent]").GetAttribute("data-in-plan").Should().Be("true");
    }

    [Fact]
    public void X04_a_scenario_id_that_is_not_in_the_project_opens_no_case_and_says_so()
    {
        SetupTwoScenarios();
        var cut = Open("manual-testing/plan?scenario=gone-1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtp-intent-unknown]"));
        cut.FindAll("[data-testid=mtp-editor]").Should().BeEmpty("never falls back to a remembered or first case");
    }
}
