using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>«Legg til scenarioer» (Testplaner v4, 2026-11-26): existing acceptance scenarios and documented tests into a chosen testgruppe of an
/// existing plan — scenario-only, no case, no result, no duplicate; requirements without a scenario are not offered.</summary>
public sealed class AddScenariosToPlanTests : BunitContext
{
    private static readonly Guid PlanId = Guid.NewGuid();
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();
    private readonly List<CreateProjectTestPlanRequest> _sent = [];

    private static DiscoveredScenario Ac(string id, string story, int n, ScenarioCandidateType type = ScenarioCandidateType.BusinessFunctional, string priority = "P1") => new()
    {
        CandidateId = id, SourceIdentity = "tss-" + id, CapabilityId = "specification", CanonicalKey = $"scenario:M|{id}", Title = $"Scenario {id}", Type = type, NumberInStory = n,
        Story = new CoverageUserStory { Key = story, StoryId = story, Title = "T", SpecificationPath = "Skole/specs/001-x/spec.md", Priority = priority },
    };

    private static readonly ScenarioDiscoveryResult Discovery = new()
    {
        DiscoveryId = "d1", ProjectKey = "m2lb",
        Candidates =
        [
            Ac("a1", "US1", 1), Ac("a2", "US1", 2), Ac("a3", "US2", 1, priority: "P4"),
            new DiscoveredScenario { CandidateId = "e1", SourceIdentity = "tss-e1", CapabilityId = "test-specification", CanonicalKey = "testspec:Hendelse/docs/H – Testspesifikasjon.md#TEST-E-01",
                Title = "TEST-E-01: Full flyt", Type = ScenarioCandidateType.BusinessE2E, Area = "Hendelse" },
            new DiscoveredScenario { CandidateId = "r1", CapabilityId = "specification", CanonicalKey = "requirement:M|FR-01", Title = "FR-01: krav" },
        ],
    };

    private TestPlanDraft _plan = new()
    {
        Id = PlanId, ProjectKey = "m2lb", PlanName = "E2E – Sprint 1", Revision = 2,
        Suites = [new("s-e2e", "Ende til ende", "template", null), new("s-int", "Integrasjon", "template", null)],
        PlannedScenarios = [new PlannedScenario { ScenarioId = "a2", SourceIdentity = "tss-a2", Title = "Scenario a2", SuiteId = "s-e2e" }],
    };

    public AddScenariosToPlanTests()
    {
        _api.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync(() => (_plan, (string?)null));
        _api.Setup(x => x.CreateProjectAsync(It.IsAny<CreateProjectTestPlanRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateProjectTestPlanRequest r, CancellationToken _) =>
            {
                _sent.Add(r);
                _plan = _plan with
                {
                    Revision = _plan.Revision + 1,
                    PlannedScenarios = [.. _plan.PlannedScenarios ?? [], .. r.CandidateIds.Select(id => new PlannedScenario { ScenarioId = id, SourceIdentity = "tss-" + id, SuiteId = r.SuiteId ?? "" })],
                };
                return (_plan, (string?)null);
            });
    }

    private IRenderedComponent<AddScenariosToPlan> Open() =>
        Render<AddScenariosToPlan>(p => p.Add(x => x.Api, _api.Object).Add(x => x.ProjectKey, "m2lb").Add(x => x.PlanId, PlanId).Add(x => x.Discovery, Discovery));

    private static string[] Rows(IRenderedComponent<AddScenariosToPlan> cut) => [.. cut.FindAll("[data-testid=asp-row]").Select(r => r.GetAttribute("data-id")!)];

    [Fact]
    public void Offers_acceptance_scenarios_and_documented_tests_marks_planned_ones_and_filters()
    {
        var cut = Open();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-list]"));
        Rows(cut).Should().BeEquivalentTo(["a1", "a2", "a3", "e1"], "a requirement without a scenario is not a scenario");
        var planned = cut.Find("[data-testid=asp-row][data-id=a2]");
        (planned.GetAttribute("data-in-plan"), planned.QuerySelector("input")!.HasAttribute("disabled")).Should().Be(("true", true));
        cut.Find("[data-testid=asp-level]").Change("EndToEnd");
        Rows(cut).Should().Equal("e1");
        cut.Find("[data-testid=asp-level]").Change("");
        cut.Find("[data-testid=asp-priority]").Change("P4");
        Rows(cut).Should().Equal("a3");
        cut.Find("[data-testid=asp-priority]").Change("");
        cut.Find("[data-testid=asp-search]").Input("TEST-E");
        Rows(cut).Should().Equal("e1");
        cut.FindAll("[data-testid=asp-suite] option").Select(o => o.TextContent.Trim()).Should().Equal("Ende til ende", "Integrasjon");
    }

    [Fact]
    public void Adds_to_the_chosen_testgruppe_as_scenario_only_twice_into_different_suites_without_cases_or_duplicates()
    {
        var cut = Open();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-list]"));
        cut.Find("[data-testid=asp-row][data-id=e1] input").Change(true);
        cut.Find("[data-testid=asp-add]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-notice]").TextContent.Should().Be("1 scenario lagt til i «Ende til ende»."));

        cut.Find("[data-testid=asp-suite]").Change("s-int");
        cut.Find("[data-testid=asp-row][data-id=a1] input").Change(true);
        cut.Find("[data-testid=asp-add]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=asp-notice]").TextContent.Should().Be("1 scenario lagt til i «Integrasjon»."));

        _sent.Select(r => (string.Join(",", r.CandidateIds), r.SuiteId, r.BaseRevision, r.ScenarioOnly, r.DraftId)).Should().Equal(
            ("e1", "s-e2e", (int?)2, true, (Guid?)PlanId), ("a1", "s-int", 3, true, PlanId));
        cut.Find("[data-testid=asp-row][data-id=e1]").GetAttribute("data-in-plan").Should().Be("true", "the list follows the saved plan");
        _api.Verify(x => x.AddManualCaseAsync(It.IsAny<Guid>(), It.IsAny<AddManualTestCaseRequest>(), It.IsAny<CancellationToken>()), Times.Never, "no case is created");
        _api.Verify(x => x.RecordResultAsync(It.IsAny<Guid>(), It.IsAny<RecordScenarioResultRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
