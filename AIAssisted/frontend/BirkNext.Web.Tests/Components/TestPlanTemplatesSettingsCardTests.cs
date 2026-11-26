using BirkNext.TestPlanning;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>System Settings → Manual Testing → Test plan templates (2026-11-26): built-ins are read-only (Duplicate only), custom templates are
/// edited, archived and restored; every change goes through the API and nothing touches a plan.</summary>
public sealed class TestPlanTemplatesSettingsCardTests : BunitContext
{
    private readonly Mock<ITestPlanSuggestionsApiService> _api = new();
    private static readonly TestPlanTemplate Custom = new() { Id = "custom:1", Name = "Regresjon", Suites = [new("a", "Smoke"), new("b", "Innlogging", "a")], UpdatedBy = "anna" };

    private IRenderedComponent<TestPlanTemplatesSettingsCard> Render(params TestPlanTemplate[] custom)
    {
        _api.Setup(x => x.TemplatesAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(([.. TestPlanTemplateRules.BuiltIn, .. custom], (string?)null));
        Services.AddSingleton(_api.Object);
        var cut = Render<TestPlanTemplatesSettingsCard>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mtpt-template]").Should().NotBeEmpty());
        return cut;
    }

    [Fact]
    public void Built_ins_are_read_only_with_a_preview_and_custom_templates_can_be_edited_and_archived()
    {
        var cut = Render(Custom);
        var standard = cut.Find($"[data-testid=mtpt-template][data-template='{TestPlanTemplateRules.StandardId}']");
        standard.QuerySelector("[data-testid=mtpt-kind]")!.TextContent.Should().Be("Built-in (read-only)");
        standard.QuerySelectorAll("[data-testid=mtpt-suite]").Select(s => s.TextContent).Should().Contain("Ende til ende · Ende til ende");
        standard.QuerySelector("[data-testid=mtpt-edit]").Should().BeNull();
        standard.QuerySelector("[data-testid=mtpt-archive]").Should().BeNull();
        standard.QuerySelector("[data-testid=mtpt-duplicate]").Should().NotBeNull();

        var custom = cut.Find("[data-testid=mtpt-template][data-template='custom:1']");
        custom.QuerySelector("[data-testid=mtpt-edit]").Should().NotBeNull();
        custom.QuerySelector("[data-testid=mtpt-archive]")!.TextContent.Should().Be("Archive");
    }

    [Fact]
    public void Editing_a_custom_template_saves_its_name_and_suites()
    {
        SaveTestPlanTemplateRequest? sent = null;
        _api.Setup(x => x.UpdateTemplateAsync("custom:1", It.IsAny<SaveTestPlanTemplateRequest>(), It.IsAny<CancellationToken>()))
            .Callback((string _, SaveTestPlanTemplateRequest r, CancellationToken _) => sent = r).ReturnsAsync((Custom with { Name = "Regresjon 2" }, (string?)null));
        var cut = Render(Custom);

        cut.Find("[data-testid=mtpt-template][data-template='custom:1'] [data-testid=mtpt-edit]").Click();
        cut.Find("[data-testid=mtpt-name]").Input("Regresjon 2");
        cut.Find("[data-testid=mtpt-new-suite-name]").Input("Betaling");
        cut.Find("[data-testid=mtpt-new-suite-add]").Click();
        cut.FindAll("[data-testid=mtpt-edit-suite-remove]")[0].Click(); // «Smoke»: its child moves up
        cut.Find("[data-testid=mtpt-save]").Click();

        sent!.Name.Should().Be("Regresjon 2");
        sent.Suites!.Select(s => (s.Name, s.ParentKey)).Should().Equal(("Innlogging", (string?)null), ("Betaling", null));
        cut.Find("[data-testid=mtpt-notice]").TextContent.Should().Contain("Existing test plans are unchanged");
    }

    [Fact]
    public void Duplicate_and_archive_go_through_the_api()
    {
        _api.Setup(x => x.TemplateActionAsync(TestPlanTemplateRules.StandardId, TemplateAction.Duplicate, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanTemplate { Id = "custom:9", Name = "Standard testplan (kopi)", Suites = [new("e2e", "Ende til ende")] }, (string?)null));
        _api.Setup(x => x.TemplateActionAsync("custom:1", TemplateAction.Archive, It.IsAny<CancellationToken>())).ReturnsAsync((Custom with { Archived = true }, (string?)null));
        var cut = Render(Custom);

        cut.Find($"[data-testid=mtpt-template][data-template='{TestPlanTemplateRules.StandardId}'] [data-testid=mtpt-duplicate]").Click();
        cut.Find("[data-testid=mtpt-notice]").TextContent.Should().Contain("Copied as «Standard testplan (kopi)»");
        cut.Find("[data-testid=mtpt-template][data-template='custom:1'] [data-testid=mtpt-archive]").Click();
        _api.Verify(x => x.TemplateActionAsync("custom:1", TemplateAction.Archive, It.IsAny<CancellationToken>()), Times.Once);
        cut.Find("[data-testid=mtpt-notice]").TextContent.Should().Contain("existing plans are unchanged");
    }
}
