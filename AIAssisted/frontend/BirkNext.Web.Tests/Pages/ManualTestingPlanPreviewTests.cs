using BirkNext.TestPlanning;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Scenario-only items and the read-only preview in Planlegg og klargjør (2026-11-26): the preview shows the editor's CURRENT values
/// (marked «Ulagret utkast»), saves nothing, and closing it keeps every edit; a planned scenario without a case says so and is never «klar».</summary>
public sealed partial class ManualTestingPlanTests
{
    [Fact]
    public void P01_the_preview_shows_unsaved_edits_saves_nothing_and_closing_keeps_them()
    {
        Setup(Standard());
        var cut = Open();
        cut.Find("[data-testid=mtp-case][data-case=c1]").Click();
        cut.Find("[data-testid=mtp-title]").Input("Endret tittel i editoren");

        cut.Find("[data-testid=mtp-preview-open]").Click();
        cut.Find("[data-testid=mtp-preview-case-title]").TextContent.Should().Be("Endret tittel i editoren", "the preview reads the editor, not the stored case");
        cut.Find("[data-testid=mtp-preview-unsaved]").TextContent.Should().Be("Ulagret utkast");
        cut.FindAll("[data-testid=mtp-preview-step]").Should().NotBeEmpty();
        cut.Markup.Should().NotContain("Bestått").And.NotContain("Feilet", "no execution in the preview");

        cut.Find("[data-testid=mtp-preview-close]").Click();
        cut.FindAll("[data-testid=mtp-preview]").Should().BeEmpty();
        cut.Find("[data-testid=mtp-title]").GetAttribute("value").Should().Be("Endret tittel i editoren", "closing the preview keeps the edit");
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never, "the preview never saves");
    }

    [Fact]
    public void P02_a_planned_scenario_without_a_case_is_listed_as_not_prepared_and_its_preview_has_no_case()
    {
        var plan = Standard() with
        {
            PlannedScenarios = [new PlannedScenario { ScenarioId = "s-only", Title = "A Barn record arrives before its Person", ExpectedBehaviour = "It is delivered on the next retry", SuiteId = "suite-1", StoryId = "US1", NumberInStory = 2 }],
        };
        Setup(plan);
        var cut = Open();
        cut.Find("[data-testid=mtp-planned-item][data-scenario=s-only] [data-testid=mtp-planned-state]").TextContent.Should().Be("Scenario – ikke klargjort");
        cut.Find("[data-testid=mtp-planned-item][data-scenario=s-only] [data-testid=mtp-planned-preview]").Click();
        cut.Find("[data-testid=mtp-preview-no-case]").TextContent.Should().Be("Ingen testtilfelle er klargjort.");
        cut.Find("[data-testid=mtp-preview-status-text]").TextContent.Should().Contain("Ikke klart til kjøring");
        cut.Find("[data-testid=mtp-preview-scenario]").TextContent.Should().Contain("It is delivered on the next retry");
    }
}
