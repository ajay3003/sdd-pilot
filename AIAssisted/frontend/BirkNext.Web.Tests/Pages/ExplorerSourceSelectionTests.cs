using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Services.SampleProjects;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Explorer source selection: the user's workspace (Tiltak 2 imported last, M2LB with specifications and plans, M2LB.Common) is no longer locked
/// to the last import. One shared, saved Explorer source drives every Explorer; empty states name the selected source and offer the source that
/// has the artifact; importing another package never changes the choice; Project evidence is not needed to inspect an imported source.
/// </summary>
public sealed class ExplorerSourceSelectionTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _workspace = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly ArtifactExplorerContext _explorer;

    private const string Plan = "# Implementation Plan: Person\n\n## Summary\nBuild it.\n\n## Technical Context\n**Language**: C#\n";
    private const string Tasks = "# Tasks: Person\n\n## Phase 1: Setup\n\n- [ ] T001 Create project\n";

    public ExplorerSourceSelectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var constitution = new Mock<IConstitutionAnalysisService>();
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton<MarkdownRenderingService>();
        Services.AddSingleton(constitution.Object);
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IDataModelAnalysisService, DataModelAnalysisService>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton<ISampleProjectDocumentResolver>(_samples);
        Services.AddSingleton<ISampleProjectArtifactDiscovery>(_samples);
        _explorer = new ArtifactExplorerContext(_workspace, _samples, _samples);
        Services.AddSingleton<IArtifactExplorerContext>(_explorer);

        Import("import-m2lb", "M2LB", 1, (WorkspaceArtifactType.Specification, "specs/001-person/spec.md", BirkNext.Web.Tests.Services.ProjectImportActivationTests.Spec),
            (WorkspaceArtifactType.Plan, "specs/001-person/plan.md", Plan), (WorkspaceArtifactType.Tasks, "specs/001-person/tasks.md", Tasks));
        Import("import-common", "M2LB.Common", 2);
        Import("import-tiltak", "Tiltak 2 - Sikkert digitalt økosystem", 3);
        // The user's legacy workspace: the last import is the current one.
        _workspace.SddLifecycle.CurrentProjectImportId = "import-tiltak";
        ApplicationContexts.Ensure(_workspace.SddLifecycle, DateTimeOffset.UtcNow);
    }

    private void Import(string id, string name, int minutes, params (WorkspaceArtifactType Role, string Path, string Text)[] documents)
    {
        _workspace.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord
        {
            ImportId = id, ProjectName = name, ArchiveFileName = name + ".zip", ArchiveSha256 = "sha-" + id, ImportedAt = DateTimeOffset.UtcNow.AddMinutes(minutes), ArtifactDocumentCount = documents.Length,
        });
        foreach (var (role, path, text) in documents) _workspace.AddArtifactRevision(role, text, path, path, ProjectImportScope.For(id), "ProjectImport", select: true);
    }

    [Fact]
    public async Task EveryImportedSourceIsSelectable_AndChoosingM2lbShowsItsSpecification_WithoutProjectEvidence()
    {
        _explorer.Sources.Select(s => s.Label).Should().Equal("M2LB", "M2LB.Common", "Tiltak 2 - Sikkert digitalt økosystem");
        _explorer.CurrentScope.Should().Be("import:import-tiltak", "the legacy default before any choice");
        (await _explorer.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.Empty);
        _explorer.SourcesWith(WorkspaceArtifactType.Specification).Select(s => s.Label).Should().Equal("M2LB");

        _explorer.SelectSource("import-m2lb");

        _explorer.CurrentScope.Should().Be("import:import-m2lb");
        (await _explorer.GetStateAsync(WorkspaceArtifactType.Specification)).Status.Should().Be(ExplorerArtifactStatus.Loaded);
        _workspace.SddLifecycle.ExplorerSource.Should().Be("import-m2lb", "saved with the workspace (refresh and restart restore it)");
        _workspace.SddLifecycle.CurrentProjectImportId.Should().Be("import-tiltak", "choosing an Explorer source never changes imports or the application");
    }

    [Fact]
    public void TheEmptyStateNamesTheSelectedSource_AndOffersTheSourceWithPlans_NoAutomaticSwitch()
    {
        var cut = Render<PlanExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer-empty]"));
        cut.Find(".axh-title").TextContent.Should().Contain("No Plan artifact was detected in the selected source: Tiltak 2 - Sikkert digitalt økosystem");
        var selector = cut.Find("[data-testid=artifact-explorer-source-selector]");
        selector.QuerySelectorAll("option").Select(o => o.TextContent.Trim()).Should().Contain(["M2LB", "M2LB.Common", "Tiltak 2 - Sikkert digitalt økosystem"]);
        cut.Find("[for$=-source]").TextContent.Should().Be("Source", "an imported package is a source, not \"the project\"");
        _explorer.CurrentScope.Should().Be("import:import-tiltak", "nothing switches until the tester chooses");

        cut.Find("[data-testid=artifact-explorer-open-source][data-source=import-m2lb]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer]").GetAttribute("data-status").Should().Be("Loaded"));
    }

    [Fact]
    public void TheChoiceIsSharedAcrossExplorers_AndSurvivesAnotherImport()
    {
        _explorer.SelectSource("import-m2lb");
        var tasks = Render<TaskExplorer>();
        tasks.WaitForAssertion(() => tasks.Find("[data-testid=artifact-explorer]").GetAttribute("data-status").Should().Be("Loaded"));
        tasks.Find("[data-testid=artifact-explorer-source-selector]").GetAttribute("value").Should().Be("import-m2lb");

        // A new package arrives (and becomes the shown package of the application): the Explorer source stays as chosen.
        Import("import-terraform", "Terraform-iac-m2lb", 4);
        _workspace.SddLifecycle.CurrentProjectImportId = "import-terraform";
        _explorer.CurrentScope.Should().Be("import:import-m2lb");
        _explorer.Sources.Select(s => s.Value).Should().Contain(["import-m2lb", "import-common", "import-terraform"], "M2LB and M2LB.Common both stay selectable");
    }

    [Fact]
    public void AChoiceWhoseImportIsGone_FallsBackVisibly_NeverToAnArbitraryImportSilently()
    {
        _workspace.SddLifecycle.ExplorerSource = "import-deleted";
        _explorer.ExplorerSourceUnavailable.Should().BeTrue();
        var cut = Render<PlanExplorer>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=artifact-explorer-source-unavailable]").TextContent.Should().Contain("no longer imported"));
        _explorer.SelectSource("not-an-import");
        _workspace.SddLifecycle.ExplorerSource.Should().Be("import-deleted", "an unknown source is never stored");
    }
}
