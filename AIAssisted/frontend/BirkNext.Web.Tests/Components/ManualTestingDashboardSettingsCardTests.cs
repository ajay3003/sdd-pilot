using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>System Settings → Manual Testing → Testoversikt (2026-11-25): grouped switches with defaults, save sends only changes, failures are said.</summary>
public sealed class ManualTestingDashboardSettingsCardTests : BunitContext
{
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();

    private IRenderedComponent<ManualTestingDashboardSettingsCard> Render()
    {
        _api.Setup(x => x.DashboardSettingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ManualTestingDashboardSettings(), (string?)null));
        Services.AddSingleton(_api.Object);
        var cut = Render<ManualTestingDashboardSettingsCard>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtds-toggle]").Should().NotBeEmpty());
        return cut;
    }

    [Fact]
    public void Every_widget_is_a_grouped_switch_with_its_default_and_saving_sends_only_the_changes()
    {
        SaveManualTestingDashboardSettingsRequest? sent = null;
        _api.Setup(x => x.SaveDashboardSettingsAsync(It.IsAny<SaveManualTestingDashboardSettingsRequest>(), It.IsAny<CancellationToken>()))
            .Callback((SaveManualTestingDashboardSettingsRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync((new ManualTestingDashboardSettings { Visible = new() { ["chart.history"] = true } }, (string?)null));
        var cut = Render();

        cut.FindAll("[data-testid=mtds-group]").Select(g => g.GetAttribute("data-group")).Should().Equal("Nøkkeltall", "Grafer", "Oppfølging", "Visning");
        cut.FindAll("[data-testid=mtds-toggle]").Should().HaveCount(ManualTestingDashboard.Widgets.Count);
        cut.Find("[data-testid=mtds-toggle][data-widget='chart.history']").HasAttribute("checked").Should().BeFalse();
        cut.Find("[data-testid=mtds-save]").HasAttribute("disabled").Should().BeTrue("nothing changed yet");

        cut.Find("[data-testid=mtds-toggle][data-widget='chart.history']").Change(true);
        cut.Find("[data-testid=mtds-save]").Click();

        sent!.Visible.Should().BeEquivalentTo(new Dictionary<string, bool> { ["chart.history"] = true });
        cut.Find("[data-testid=mtds-notice]").TextContent.Should().StartWith("Saved");
    }

    [Fact]
    public void A_failed_save_is_never_shown_as_saved_and_restoring_defaults_asks_first()
    {
        _api.Setup(x => x.SaveDashboardSettingsAsync(It.Is<SaveManualTestingDashboardSettingsRequest>(r => !r.ResetToDefaults), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((ManualTestingDashboardSettings?)null, "You do not have permission to change Test Plan proposals."));
        _api.Setup(x => x.SaveDashboardSettingsAsync(It.Is<SaveManualTestingDashboardSettingsRequest>(r => r.ResetToDefaults), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ManualTestingDashboardSettings(), (string?)null));
        var cut = Render();

        cut.Find("[data-testid=mtds-toggle][data-widget='chart.priority']").Change(false);
        cut.Find("[data-testid=mtds-save]").Click();
        cut.Find("[data-testid=mtds-error]").TextContent.Should().Contain("could not be saved").And.Contain("permission");
        cut.FindAll("[data-testid=mtds-notice]").Should().BeEmpty();

        cut.Find("[data-testid=mtds-reset]").Click();
        _api.Verify(x => x.SaveDashboardSettingsAsync(It.Is<SaveManualTestingDashboardSettingsRequest>(r => r.ResetToDefaults), It.IsAny<CancellationToken>()), Times.Never, "a confirmation first");
        cut.Find("[data-testid=mtds-reset-yes]").Click();
        _api.Verify(x => x.SaveDashboardSettingsAsync(It.Is<SaveManualTestingDashboardSettingsRequest>(r => r.ResetToDefaults), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void The_test_plan_structure_for_new_plans_is_chosen_here_and_saved_without_touching_the_switches()
    {
        var cut = Render();
        cut.FindAll("[data-testid=mtds-plan-structure-option]").Select(o => o.GetAttribute("data-structure")).Should().Equal(PlanStructureTemplates.All.Select(t => t.Key));
        cut.Find("[data-testid=mtds-plan-structure-option][data-structure=level-area]").HasAttribute("checked").Should().BeTrue("the recommended default");
        _api.Setup(x => x.SaveDashboardSettingsAsync(It.IsAny<SaveManualTestingDashboardSettingsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ManualTestingDashboardSettings { PlanStructure = PlanStructureTemplates.ModuleStory }, (string?)null));
        cut.Find("[data-testid=mtds-plan-structure-option][data-structure=module-story]").Change(true);
        cut.Find("[data-testid=mtds-save]").Click();
        _api.Verify(x => x.SaveDashboardSettingsAsync(It.Is<SaveManualTestingDashboardSettingsRequest>(r => r.PlanStructure == PlanStructureTemplates.ModuleStory && r.Visible!.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }
}
