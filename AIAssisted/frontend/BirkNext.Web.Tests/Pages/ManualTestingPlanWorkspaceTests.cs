using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using W = BirkNext.Web.Services.ScenarioWorkspacePresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Planlegg og klargjør in the User Story–aware workspace (2026-11-24): the case says where it belongs (story › scenario › case), leads back to
/// its scenario in Finn og forstå with the story chosen, and shows the same six readiness questions as the scenario.
/// </summary>
public sealed partial class ManualTestingPlanTests
{
    [Fact]
    public void W01_a_case_shows_its_story_and_scenario_and_leads_back_to_the_scenario_in_Finn_og_forstå()
    {
        Setup(Plan(Case("c1", "Submit a valid report", story: Story6)));
        var cut = Open();

        cut.Find("[data-testid=mtp-breadcrumb]").TextContent.Should().Contain("US6").And.Contain("Testtilfelle");

        cut.Find("[data-testid=mtp-show-in-discover]").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith(ManualTestingPresentation.DiscoverRoute);
        var context = JSInterop.Invocations.Where(i => i.Identifier == "sessionStorage.setItem" && (string)i.Arguments[0]! == TabDraftStore.DiscoverContextKey("library"))
            .Select(i => System.Text.Json.JsonSerializer.Deserialize<W.WorkContext>((string)i.Arguments[1]!)).Last();
        context!.Scenario.Should().Be("c1", "the case's own scenario opens");
        context.Story.Should().Be(Story6.Key);
        var filter = JSInterop.Invocations.Where(i => i.Identifier == "sessionStorage.setItem" && (string)i.Arguments[0]! == TabDraftStore.DiscoverFilterKey("library"))
            .Select(i => System.Text.Json.JsonSerializer.Deserialize<ScenarioBrowserPresentation.Filter>((string)i.Arguments[1]!)).Last();
        filter!.Story.Should().Be(Story6.Key, "the scenario's story is chosen so the scenario is in the list");
        filter.Queue.Should().BeNull("no work queue hides it");
    }

    [Fact]
    public void W02_a_case_written_by_hand_without_a_scenario_has_no_way_back_and_says_it_has_no_story()
    {
        Setup(Plan(Case("m9", "Exploratory check", kind: ScenarioSourceKind.Manual)));
        var cut = Open();

        cut.Find("[data-testid=mtp-breadcrumb]").TextContent.Should().Contain("Uten brukerhistorie");
        cut.FindAll("[data-testid=mtp-show-in-discover]").Should().BeEmpty();
    }

    [Fact]
    public void W03_readiness_uses_the_same_six_questions_as_the_scenario()
    {
        Setup(Plan(Case("c1", "Submit a valid report", story: Story6)));
        var cut = Open();

        cut.FindAll("[data-testid=tdp-check]").Select(c => c.GetAttribute("data-key")).Should().Equal("source", "data", "trigger", "verify", "env", "review");
        cut.Find("[data-testid=tdp-readiness-count]").TextContent.Should().MatchRegex(@"^\d / 6$");
    }
}
