using BirkNext.DeliveryReporting;
using BirkNext.TestPlanning;
using BirkNext.Web.Components.AzureDevOps;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Azure DevOps – Test Plans → Field defaults (2026-11-25): the selected project's field values for every transfer are set here,
/// in English; saving keeps the remembered Azure DevOps ids and changes nothing in Azure DevOps. Manual Testing only shows them.
/// </summary>
public sealed class AdoFieldDefaultsSettingsTests : BunitContext
{
    private readonly Mock<IAdoTestPlansApiService> _ado = new();
    private readonly Mock<IAdoExportApiService> _export = new();
    private readonly Mock<ITestPlanSuggestionsApiService> _plans = new();
    private readonly List<AdoExportProfile> _saved = [];

    private void Setup(bool project = true, bool canWrite = true)
    {
        if (project) Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        _plans.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = canWrite }, (string?)null));
        _ado.Setup(x => x.SettingsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new AdoTestPlanSettingsView { Connections = [] }, (string?)null));
        var stored = new AdoExportProfile { AreaPath = @"Library\Loans", KnownTestCaseIds = new() { ["p/c1"] = "4711" } };
        _export.Setup(x => x.ProfileAsync("library", It.IsAny<CancellationToken>())).ReturnsAsync((stored, (string?)null));
        _export.Setup(x => x.SaveProfileAsync("library", It.IsAny<AdoExportProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, AdoExportProfile p, CancellationToken _) => { _saved.Add(p); return (p, (string?)null); });
        Services.AddSingleton(_ado.Object);
        Services.AddSingleton(_export.Object);
        Services.AddSingleton(_plans.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Field_defaults_are_edited_and_saved_for_the_project_in_english()
    {
        Setup();
        var cut = Render<AdoTestPlansSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ats-f-area]"));

        cut.Find("[data-testid=ats-fields] .gsx-panel-title").TextContent.Should().Be("Field defaults");
        cut.Find("[data-testid=ats-f-area]").GetAttribute("value").Should().Be(@"Library\Loans");
        cut.Find("[data-testid=ats-f-save]").HasAttribute("disabled").Should().BeTrue("nothing changed yet");

        cut.Find("[data-testid=ats-f-state]").Change("Ready");
        cut.Find("[data-testid=ats-f-assigned]").Change("lead@example.org");
        cut.Find("[data-testid=ats-f-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=ats-f-notice]").TextContent.Should().Contain("Nothing was changed in Azure DevOps."));
        _saved.Should().ContainSingle();
        _saved[0].State.Should().Be("Ready");
        _saved[0].AssignedTo.Should().Be("lead@example.org");
        _saved[0].AreaPath.Should().Be(@"Library\Loans");
        _saved[0].KnownTestCaseIds.Should().ContainKey("p/c1", "remembered Azure DevOps ids are kept");
        cut.Find("[data-testid=ats-open-transfer]").GetAttribute("href").Should().Be("manual-testing/plans");
    }

    [Fact]
    public void Without_a_project_there_is_nothing_to_set()
    {
        Setup(project: false);
        var cut = Render<AdoTestPlansSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ats-fields-no-project]").TextContent.Should().Be("Select a project to set its field defaults."));
        _export.Verify(x => x.SaveProfileAsync(It.IsAny<string>(), It.IsAny<AdoExportProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Without_write_access_the_defaults_cannot_be_saved()
    {
        Setup(canWrite: false);
        var cut = Render<AdoTestPlansSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ats-f-state]"));
        cut.Find("[data-testid=ats-f-state]").Change("Ready");
        cut.Find("[data-testid=ats-f-save]").HasAttribute("disabled").Should().BeTrue();
    }
}
