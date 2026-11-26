using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Testgjennomføring in Testplaner (2026-11-26): one result per scenario in THIS plan (with or without a case), counts that add up, history,
/// no step or case execution, and a conflict that keeps the tester's comment.</summary>
public sealed class PlanExecutionTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("12121212-0000-0000-0000-000000000012");
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();

    private static PlanExecutionView View(ScenarioExecutionStatus a1 = ScenarioExecutionStatus.NotRun, ScenarioExecutionEvent? latest = null) => new()
    {
        PlanId = PlanId, PlanName = "Skolenærvær – Sprint 12", SprintLabel = "Sprint 12",
        Occurrences =
        [
            new ScenarioOccurrence { Key = "scenario:a1", ScenarioId = "a1", Title = "Barn registreres senere", StoryId = "US1", NumberInStory = 1, ExpectedBehaviour = "Leveres ved neste forsøk",
                SuitePath = "Ende til ende / Rapportering", CaseIds = ["case-1"], Status = a1, Latest = latest, History = latest is null ? [] : [latest] },
            new ScenarioOccurrence { Key = "scenario:a3", ScenarioId = "a3", Title = "Periodelås", SuitePath = "Integrasjon", ScenarioOnly = true },
        ],
    };

    private IRenderedComponent<PlanExecution> Render(PlanExecutionView view)
    {
        _api.Setup(x => x.ExecutionAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((view, (string?)null));
        return Render<PlanExecution>(p => p.Add(x => x.PlanId, PlanId).Add(x => x.Api, _api.Object).Add(x => x.CanWrite, true));
    }

    [Fact]
    public void X01_counts_add_up_and_a_scenario_without_a_case_can_be_executed_at_scenario_level()
    {
        RecordScenarioResultRequest? sent = null;
        var passed = new ScenarioExecutionEvent(Guid.NewGuid(), ScenarioExecutionStatus.Passed, null, null, "tester", DateTimeOffset.UtcNow);
        _api.Setup(x => x.RecordResultAsync(PlanId, It.IsAny<RecordScenarioResultRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, RecordScenarioResultRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync((View() with { Occurrences = [.. View().Occurrences.Select(o => o.Key == "scenario:a3" ? o with { Status = ScenarioExecutionStatus.Passed, Latest = passed, History = [passed] } : o)] }, (string?)null));
        var cut = Render(View());
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtx-counts]").TextContent.Should().Be("2 scenarioer · 0 bestått · 0 feilet · 0 blokkert · 2 ikke kjørt"));

        cut.Find("[data-testid=mtx-row][data-key='scenario:a3'] [data-testid=mtx-row-open]").Click();
        cut.Find("[data-testid=mtx-no-case]").TextContent.Should().Contain("Ingen testtilfelle er forberedt");
        cut.Markup.Should().NotContain("Steg 1", "no step-level execution");
        cut.Find("[data-testid=mtx-choice][data-status=Passed]").Change(true);
        cut.Find("[data-testid=mtx-save]").Click();
        sent!.Should().Be(new RecordScenarioResultRequest("scenario:a3", ScenarioExecutionStatus.Passed, "", "", null));
        cut.Find("[data-testid=mtx-counts]").TextContent.Should().Contain("1 bestått");
        cut.Find("[data-testid=mtx-notice]").TextContent.Should().Be("Lagret: Bestått.");
    }

    [Fact]
    public void X02_a_case_is_an_instruction_link_and_a_conflict_keeps_the_comment()
    {
        var earlier = new ScenarioExecutionEvent(Guid.NewGuid(), ScenarioExecutionStatus.Failed, "Feil kvittering", "BUG-12", "anna", DateTimeOffset.UtcNow.AddHours(-1));
        _api.Setup(x => x.RecordResultAsync(PlanId, It.IsAny<RecordScenarioResultRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(((PlanExecutionView?)null, "409 stale-result"));
        var cut = Render(View(ScenarioExecutionStatus.Failed, earlier));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtx-row][data-key='scenario:a1'] [data-testid=mtx-status]").TextContent.Should().Be("Feilet"));
        cut.Find("[data-testid=mtx-row][data-key='scenario:a1'] [data-testid=mtx-row-open]").Click();
        cut.Find("[data-testid=mtx-case]").GetAttribute("href").Should().Be($"manual-testing/plan?draft={PlanId}&case=case-1");
        cut.FindAll("[data-testid=mtx-history-item]").Single().TextContent.Should().Contain("Feilet").And.Contain("BUG-12");

        cut.Find("[data-testid=mtx-choice][data-status=Passed]").Change(true);
        cut.Find("[data-testid=mtx-comment]").Input("Retest ok");
        cut.Find("[data-testid=mtx-save]").Click();
        cut.Find("[data-testid=mtx-error]").Should().NotBeNull();
        cut.Find("[data-testid=mtx-comment]").GetAttribute("value").Should().Be("Retest ok", "a conflict never loses the tester's comment");
        _api.Verify(x => x.RecordResultAsync(PlanId, It.Is<RecordScenarioResultRequest>(r => r.ExpectedLatestEventId == earlier.Id), It.IsAny<CancellationToken>()), Times.Once,
            "the write names the latest result the tester saw");
    }

    [Fact]
    public void X03_an_archived_plan_shows_results_without_controls()
    {
        var cut = Render(View() with { Archived = true });
        cut.WaitForAssertion(() => cut.Find("[data-testid=mtx-archived]"));
        cut.Find("[data-testid=mtx-row][data-key='scenario:a1'] [data-testid=mtx-row-open]").Click();
        cut.FindAll("[data-testid=mtx-result]").Should().BeEmpty();
    }

    [Fact]
    public void X10_a_scenario_moves_to_another_suite_and_only_a_scenario_only_item_without_results_offers_removal()
    {
        // Testplaner (2026-11-26): move / remove a scenario between suites, revision-checked.
        var view = View() with
        {
            Revision = 7,
            Suites = [new PlanSuiteChoice("s-e2e", "Ende til ende / Rapportering"), new PlanSuiteChoice("s-int", "Integrasjon"), new PlanSuiteChoice("s-reg", "Regresjon")],
            Occurrences = [.. View().Occurrences.Select(o => o with { SuiteId = o.Key == "scenario:a1" ? "s-e2e" : "s-int" })],
        };
        var sent = new List<PlaceScenarioRequest>();
        _api.Setup(x => x.PlaceScenarioAsync(PlanId, It.IsAny<PlaceScenarioRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, PlaceScenarioRequest r, CancellationToken _) => sent.Add(r)).ReturnsAsync((new TestPlanDraft(), (string?)null));
        var cut = Render(view);

        cut.WaitForAssertion(() => cut.Find("[data-testid=mtx-row][data-key='scenario:a1'] [data-testid=mtx-row-open]").Click());
        cut.FindAll("[data-testid=mtx-move-to] option").Select(o => o.TextContent).Should().Equal(["Velg testgruppe …", "Integrasjon", "Regresjon"], "the current suite is not a target");
        cut.Find("[data-testid=mtx-move]").HasAttribute("disabled").Should().BeTrue("nothing chosen yet");
        cut.FindAll("[data-testid=mtx-remove]").Should().BeEmpty("a scenario with a case is excluded with a reason, not removed");
        cut.Find("[data-testid=mtx-remove-why]").TextContent.Should().Contain("ekskluder");
        cut.Find("[data-testid=mtx-move-to]").Change("s-reg");
        cut.Find("[data-testid=mtx-move]").Click();
        sent.Should().ContainSingle().Which.Should().Be(new PlaceScenarioRequest(7, "a1", "s-reg", false));
        cut.Find("[data-testid=mtx-notice]").TextContent.Should().Be("Flyttet til «Regresjon».");

        cut.Find("[data-testid=mtx-row][data-key='scenario:a3'] [data-testid=mtx-row-open]").Click();
        cut.Find("[data-testid=mtx-remove]").Click();
        sent.Last().Should().Be(new PlaceScenarioRequest(7, "a3", null, true));
        cut.Find("[data-testid=mtx-notice]").TextContent.Should().Be("Scenarioet er fjernet fra testplanen.");
    }
}
