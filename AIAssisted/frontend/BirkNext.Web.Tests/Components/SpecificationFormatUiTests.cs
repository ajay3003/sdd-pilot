using BirkNext.Specifications;
using BirkNext.Web.Components.Admin;
using BirkNext.Web.Components.ManualTesting;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>System Settings → Spesifikasjonsformat and the Testoversikt OpenSpec panel (2026-11-27).</summary>
public sealed class SpecificationFormatUiTests : BunitContext
{
    private readonly Mock<ISpecificationFormatApiService> _api = new();

    private static SpecificationFormatSettingsView View(SpecificationFormat demo = SpecificationFormat.SpecKit, bool canWrite = true) => new()
    {
        CanWrite = canWrite,
        Applications =
        [
            new() { ApplicationKey = "repo:m2lb", ApplicationName = "M2LB", Format = SpecificationFormat.SpecKit, IsDefault = true, Imports = 3, Projects = ["import:m1"] },
            new() { ApplicationKey = "repo:demo", ApplicationName = "Demo", Format = demo, IsDefault = demo == SpecificationFormat.SpecKit, Imports = 1, Projects = ["import:d1"] },
        ],
    };

    private IRenderedComponent<SpecificationFormatSettingsCard> RenderCard(SpecificationFormatSettingsView view)
    {
        _api.Setup(x => x.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((view, (string?)null));
        _api.Setup(x => x.ConsumersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([new FormatConsumerStatus("Specifications Explorer", FormatConsumerSupport.Supported, FormatConsumerSupport.NotSupported, "Spec Kit-parser")], (string?)null));
        Services.AddSingleton(_api.Object);
        var cut = Render<SpecificationFormatSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sfx-application]"));
        return cut;
    }

    [Fact]
    public void C01_the_default_is_shown_per_application_and_nothing_can_be_saved_until_a_change()
    {
        var cut = RenderCard(View());
        cut.Find("[data-testid=sfx-active]").TextContent.Should().Contain("Aktivt format: Spec Kit (standard)");
        cut.FindAll("[data-testid=sfx-format-option]").Select(o => o.TextContent).Should().Contain(t => t.Contains("Spec Kit – eksisterende format")).And.Contain(t => t.Contains("OpenSpec – nytt format"));
        cut.Find("[data-testid=sfx-save]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=sfx-consumers]").TextContent.Should().Contain("Ikke støttet", "unsupported consumers are stated, not hidden");
    }

    [Fact]
    public void C02_C06_changing_an_application_with_data_shows_the_impact_and_needs_confirmation_before_saving()
    {
        _api.Setup(x => x.ImpactAsync("repo:m2lb", SpecificationFormat.OpenSpec, It.IsAny<CancellationToken>())).ReturnsAsync((new SpecificationFormatImpact
        {
            ApplicationKey = "repo:m2lb", Current = SpecificationFormat.SpecKit, Requested = SpecificationFormat.OpenSpec, Changes = true, HasImportedData = true, RequiresConfirmation = true,
            Consequences = ["Valget gjelder fremtidige importer", "Eksisterende data endres ikke"],
        }, (string?)null));
        SaveSpecificationFormatRequest? sent = null;
        var saved = View() with { Applications = [View().Applications[0] with { Format = SpecificationFormat.OpenSpec, IsDefault = false }, View().Applications[1]] };
        _api.Setup(x => x.SaveAsync(It.IsAny<SaveSpecificationFormatRequest>(), It.IsAny<CancellationToken>()))
            .Callback((SaveSpecificationFormatRequest r, CancellationToken _) => sent = r).ReturnsAsync((saved, (SpecificationFormatImpact?)null, (string?)null));
        var cut = RenderCard(View());

        cut.Find("[data-testid=sfx-format-option][data-value=OpenSpec] input").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=sfx-impact]").TextContent.Should().Contain("Eksisterende data endres ikke"));
        cut.Find("[data-testid=sfx-save]").HasAttribute("disabled").Should().BeTrue("the impact must be confirmed first");
        cut.Find("[data-testid=sfx-confirm]").Change(true);
        cut.Find("[data-testid=sfx-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sfx-message]").TextContent.Should().Contain("bruker nå OpenSpec"));
        sent.Should().BeEquivalentTo(new SaveSpecificationFormatRequest { ApplicationKey = "repo:m2lb", Format = SpecificationFormat.OpenSpec, ConfirmImpact = true });
        cut.Find("[data-testid=sfx-active]").TextContent.Should().Contain("Aktivt format: OpenSpec", "C03: the persisted value comes back from the server");
    }

    [Fact]
    public void C05_a_user_without_permission_sees_the_setting_read_only_and_a_refused_save_is_never_shown_as_saved()
    {
        var cut = RenderCard(View(canWrite: false));
        cut.Find("[data-testid=sfx-formats]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=sfx-readonly]").TextContent.Should().Contain("krever tilgang");
    }

    [Fact]
    public void D_the_reconciliation_runs_only_on_request_and_shows_every_check_with_an_export()
    {
        _api.Setup(x => x.OpenSpecReconciliationAsync("import:m1", It.IsAny<CancellationToken>())).ReturnsAsync((new OpenSpecReconciliationReport
        {
            ProjectKey = "import:m1", CatalogFormat = SpecificationFormat.OpenSpec, CatalogRevision = 2, Overall = ReconciliationStatus.NotVerified,
            Checks = [new("D01", "Gjeldende krav", ReconciliationStatus.Pass, "5", "5", ""), new("S00", "Skjema verifisert", ReconciliationStatus.NotVerified, "spec-driven", "custom", "")],
        }, (string?)null));
        _api.Setup(x => x.OpenSpecReconciliationMarkdownAsync("import:m1", It.IsAny<CancellationToken>())).ReturnsAsync(("# rapport", (string?)null));
        var cut = RenderCard(View());
        _api.Verify(x => x.OpenSpecReconciliationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "nothing runs when the section opens");
        cut.Find("[data-testid=sfx-reconcile]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sfx-report-overall]").TextContent.Should().Contain("IKKE VERIFISERT"));
        cut.FindAll("[data-testid=sfx-report] tbody tr").Should().HaveCount(2);
        cut.Find("[data-testid=sfx-export]").GetAttribute("href").Should().StartWith("data:text/markdown");
    }

    private IRenderedComponent<OpenSpecSourcePanel> RenderPanel(OpenSpecSourceStatus status)
    {
        _api.Setup(x => x.OpenSpecStatusAsync("import:p", It.IsAny<CancellationToken>())).ReturnsAsync((status, (string?)null));
        Services.AddSingleton(_api.Object);
        return Render<OpenSpecSourcePanel>(p => p.Add(x => x.ProjectKey, "import:p"));
    }

    [Fact]
    public void S05_a_SpecKit_project_renders_no_panel_so_Testoversikt_is_unchanged()
    {
        var cut = RenderPanel(new OpenSpecSourceStatus { ProjectKey = "import:p", CatalogFormat = SpecificationFormat.SpecKit, ConfiguredFormat = SpecificationFormat.SpecKit });
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void M05_M07_the_panel_separates_current_figures_from_proposals_and_archive()
    {
        var cut = RenderPanel(new OpenSpecSourceStatus
        {
            ProjectKey = "import:p", CatalogFormat = SpecificationFormat.OpenSpec, ConfiguredFormat = SpecificationFormat.OpenSpec, CatalogRevision = 3, ModelAvailable = true,
            CurrentRequirements = 5, CurrentScenarios = 5, CurrentRequirementsWithoutScenarios = 1,
            ActiveChanges = [new OpenSpecChangeView { ChangeId = "add-2fa", Title = "Add 2FA", Deltas = [new OpenSpecDeltaView { Operation = OpenSpecDeltaOperation.Modified, RequirementName = "Session Timeout", Capability = "auth", Resolution = "Conflict" }] }],
            ArchivedChanges = [new OpenSpecChangeView { ChangeId = "2026-01-10-add-billing", State = OpenSpecLifecycleState.ArchivedChange }],
            Diagnostics = [new("CONFLICTING_ACTIVE_CHANGES", OpenSpecDiagnosticSeverity.Error, "Konflikt", null, null, "add-2fa,shorter-sessions")],
        });
        cut.WaitForAssertion(() => cut.Find("[data-testid=osp-format]").TextContent.Should().Contain("OpenSpec"));
        cut.Find("[data-testid=osp-scenarios]").TextContent.Should().Be("5");
        cut.Find("[data-testid=osp-proposals]").TextContent.Should().Be("1");
        cut.Find("[data-testid=osp-archived]").TextContent.Should().Be("1");
        cut.Find("[data-testid=osp-changes]").TextContent.Should().Contain("påvirker ikke gjeldende scenarioer");
        cut.Find("[data-testid=osp-resolution]").TextContent.Should().Contain("Konflikt");
        cut.Find("[data-testid=osp-diagnostics-summary]").TextContent.Should().Contain("1 feil");
    }

    [Fact]
    public void C07_a_route_problem_is_shown_never_hidden()
    {
        var cut = RenderPanel(new OpenSpecSourceStatus { ProjectKey = "import:p", CatalogFormat = SpecificationFormat.SpecKit, ConfiguredFormat = SpecificationFormat.OpenSpec,
            RouteProblem = "Applikasjonen er satt til OpenSpec, men importen har ingen openspec/-mappe." });
        cut.WaitForAssertion(() => cut.Find("[data-testid=osp-problem]").TextContent.Should().Contain("ingen openspec/-mappe"));
    }
}
