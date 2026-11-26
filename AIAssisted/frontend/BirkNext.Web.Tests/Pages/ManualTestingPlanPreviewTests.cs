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

    [Fact]
    public void P03_the_preview_is_one_read_only_modal_dialog_with_no_inputs_and_Escape_closes_it()
    {
        Setup(Standard());
        var cut = Open();
        cut.Find("[data-testid=mtp-case][data-case=c1]").Click();
        cut.Find("[data-testid=mtp-preview-open]").Click();
        cut.Find("[data-testid=mtp-preview-open]").Click();

        cut.FindAll("[data-testid=mtp-preview]").Should().ContainSingle("never two previews");
        var dialog = cut.Find("[data-testid=mtp-preview-dialog]");
        (dialog.GetAttribute("role"), dialog.GetAttribute("aria-modal")).Should().Be(("dialog", "true"));
        dialog.QuerySelectorAll("input, textarea, select").Should().BeEmpty("read-only: no editable fields");
        dialog.QuerySelectorAll("button").Select(b => b.GetAttribute("data-testid")).Should().BeEquivalentTo(["mtp-preview-print", "mtp-preview-close"], "no save, approval or execution controls");
        cut.Find("[data-testid=mtp-preview-close]").TextContent.Should().Be("Lukk forhåndsvisning");

        dialog.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.FindAll("[data-testid=mtp-preview]").Should().BeEmpty();
    }

    [Fact]
    public void P04_Norsk_shows_the_stored_translation_and_says_when_it_is_missing()
    {
        var translated = Case("c1", "Submit a valid report") with
        {
            Translations = new() { [ScenarioPlanning.Norwegian] = new TestCaseTranslation { Title = "Send inn en gyldig rapport", Origin = TranslationOrigin.Machine } },
        };
        Setup(Plan(translated, Case("c2", "Reject an invalid report")));
        var cut = Open();
        cut.Find("[data-testid=mtp-case][data-case=c1]").Click();
        cut.Find("[data-testid=mtp-lang-option][data-lang=Norwegian]").Click();
        cut.Find("[data-testid=mtp-preview-open]").Click();
        cut.Find("[data-testid=mtp-preview-case-title]").TextContent.Should().Be("Send inn en gyldig rapport");
        cut.Find("[data-testid=mtp-preview-machine]").TextContent.Should().Be("Maskinoversatt – ikke gjennomgått");
        cut.FindAll("[data-testid=mtp-preview-unsaved]").Should().BeEmpty("a stored translation without step texts is not an unsaved change (2026-11-26 fix)");
        cut.Find("[data-testid=mtp-preview-close]").Click();

        cut.Find("[data-testid=mtp-lang-option][data-lang=Compare]").Click();
        cut.Find("[data-testid=mtp-preview-open]").Click();
        cut.Find("[data-testid=mtp-preview-case-title-original]").TextContent.Should().Be("Submit a valid report");
        cut.Find("[data-testid=mtp-preview-close]").Click();

        cut.Find("[data-testid=mtp-case][data-case=c2]").Click();
        cut.Find("[data-testid=mtp-lang-option][data-lang=Norwegian]").Click();
        cut.Find("[data-testid=mtp-preview-open]").Click();
        cut.Find("[data-testid=mtp-preview-no-nb]").TextContent.Should().Be("Norsk oversettelse mangler – viser originalen");
        cut.Find("[data-testid=mtp-preview-case-title]").TextContent.Should().Be("Reject an invalid report");
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never, "opening and closing the preview never saves");
    }

    [Fact]
    public void U01_step_editor_inserts_duplicates_and_adds_on_Ctrl_Enter_without_saving()
    {
        Setup(Standard());
        var cut = Open();
        cut.Find("[data-testid=mtp-case][data-case=c1]").Click();
        cut.Find("[data-testid=mtp-step-add]").Click();
        cut.FindAll("[data-testid=mtp-step-action]")[0].Input("Åpne skjemaet");
        var before = cut.FindAll("[data-testid=mtp-step]").Count;

        cut.FindAll("[data-testid=mtp-step-duplicate]")[0].Click();
        cut.FindAll("[data-testid=mtp-step]").Count.Should().Be(before + 1);
        cut.FindAll("[data-testid=mtp-step-action]")[1].GetAttribute("value").Should().Be("Åpne skjemaet");

        cut.FindAll("[data-testid=mtp-step-insert]")[0].Click();
        cut.FindAll("[data-testid=mtp-step-action]")[1].GetAttribute("value").Should().BeNullOrEmpty("a new empty step directly below");

        cut.FindAll("[data-testid=mtp-step-action]")[0].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter", CtrlKey = true });
        cut.FindAll("[data-testid=mtp-step]").Count.Should().Be(before + 3);
        cut.FindAll("[data-testid=mtp-step-action]")[0].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        cut.FindAll("[data-testid=mtp-step]").Count.Should().Be(before + 3, "plain Enter is a new line, not a new step");
        cut.Find("[data-testid=mtp-steps-count]").TextContent.Should().Be((before + 3).ToString());

        var bar = cut.Find("[data-testid=mtp-actions]");
        (bar.GetAttribute("role"), bar.ClassList.Contains("is-sticky")).Should().Be(("toolbar", true));
        cut.Find("[data-testid=mtp-dirty]").TextContent.Should().Be("Ulagrede endringer");
        _api.Verify(x => x.SaveAsync(It.IsAny<Guid>(), It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
