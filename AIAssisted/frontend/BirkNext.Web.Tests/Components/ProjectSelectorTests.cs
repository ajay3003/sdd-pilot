using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>The shared "Project ▾" control (2026-11-18): lists the registry, switches the workspace, protects unsaved work, never lies while loading.</summary>
public sealed class ProjectSelectorTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();
    private readonly ArtifactExplorerContext _explorer;
    private readonly UnsavedWorkGuard _guard = new();

    public ProjectSelectorTests()
    {
        var samples = new MockSampleProjectDocumentResolver { Repository = _repository };
        _explorer = new ArtifactExplorerContext(_repository, samples, samples);
        _autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        Services.AddSingleton(_repository);
        Services.AddSingleton(_autoSave.Object);
        Services.AddSingleton<IArtifactExplorerContext>(_explorer);
        Services.AddSingleton(new ProjectImportActivation(_repository));
        Services.AddSingleton<ProjectSelection>();
        Services.AddSingleton(_guard);
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
    }

    private void Application(string id, string name, string importId)
    {
        _repository.SddLifecycle.Applications.Add(new SddApplicationContext { Id = id, Name = name });
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = importId, ProjectName = name, ArchiveFileName = name + ".zip", ApplicationId = id });
    }

    private void Current(string applicationId, string importId)
    {
        _repository.SddLifecycle.CurrentApplicationId = applicationId;
        _repository.SddLifecycle.CurrentProjectImportId = importId;
    }

    [Fact]
    public void With_nothing_imported_it_says_so_and_links_to_Project_Import()
    {
        var cut = Render<ProjectSelector>();
        cut.Find("[data-testid=project-selector-empty]").TextContent.Should().Contain("No imported project yet");
        cut.Find("[data-testid=project-selector-empty] a").GetAttribute("href").Should().Be("project-import");
        cut.FindAll("select").Should().BeEmpty();
    }

    [Fact]
    public void Where_the_page_offers_Import_Project_itself_the_empty_selector_only_says_so()
    {
        var cut = Render<ProjectSelector>(p => p.Add(x => x.OfferImport, false));
        cut.Find("[data-testid=project-selector-empty]").TextContent.Trim().Should().Be("No imported project yet.");
        cut.FindAll("[data-testid=project-selector-empty] a").Should().BeEmpty();
    }

    [Fact]
    public void While_the_saved_selection_is_read_it_says_loading_never_a_wrong_default()
    {
        Application("app-a", "Library", "import-a");
        Current("app-a", "import-a");
        CurrentWorkspaceSnapshot? current = null;
        var read = new TaskCompletionSource();
        var projection = new Mock<ICurrentWorkspaceProjection>();
        projection.SetupGet(p => p.Current).Returns(() => current);
        projection.Setup(p => p.GetAsync()).Returns(async () => { await read.Task; return current = CurrentWorkspaceSnapshot.None(); });
        Services.AddSingleton(projection.Object);
        var cut = Render<ProjectSelector>();
        cut.Find("[data-testid=project-selector-restoring]").TextContent.Should().Be("Loading the selected project…");
        cut.FindAll("select").Should().BeEmpty();

        // Regression (browser acceptance, 2026-11-18): the first snapshot raises no Changed event and a page re-render does not re-render this
        // control, so it must wait for the snapshot itself — otherwise "Loading…" stayed after the project was known.
        read.SetResult();
        cut.WaitForAssertion(() => cut.Find("[data-testid=project-selector-select] option[selected]").TextContent.Should().Be("Library"));
        cut.FindAll("[data-testid=project-selector-restoring]").Should().BeEmpty();
    }

    [Fact]
    public void Lists_every_imported_project_with_an_accessible_label_and_marks_the_current_one()
    {
        Application("app-a", "Library", "import-a");
        Application("app-b", "Archive", "import-b");
        Current("app-a", "import-a");
        var cut = Render<ProjectSelector>();
        var select = cut.Find("[data-testid=project-selector-select]");
        cut.Find("label").GetAttribute("for").Should().Be(select.Id, "the select is named \"Project\" for assistive technology and keyboard users");
        cut.FindAll("option").Select(o => o.TextContent).Should().Equal("Archive", "Library");
        cut.Find("option[selected]").TextContent.Should().Be("Library");
        cut.Find("[data-testid=project-selector-manage]").GetAttribute("href").Should().Be("project-import");
    }

    [Fact]
    public void Without_a_current_project_it_asks_to_choose_one()
    {
        Application("app-a", "Library", "import-a");
        var cut = Render<ProjectSelector>();
        cut.Find("option[selected]").TextContent.Should().Be("Velg prosjekt …", "first use: nothing is chosen (V8)");
        cut.Find("label").TextContent.Should().Be("Prosjekt");
    }

    [Fact]
    public void Choosing_another_project_switches_the_workspace()
    {
        Application("app-a", "Library", "import-a");
        Application("app-b", "Archive", "import-b");
        Current("app-a", "import-a");
        var cut = Render<ProjectSelector>();
        cut.Find("select").Change("app:app-b");
        cut.WaitForAssertion(() => _explorer.CurrentScope.Should().Be(ProjectImportScope.For("import-b")));
        cut.WaitForAssertion(() => cut.Find("option[selected]").TextContent.Should().Be("Archive"));
        _autoSave.Verify(a => a.SaveNowAsync(), Times.Once);
    }

    [Fact]
    public void Unsaved_work_is_kept_with_the_project_when_the_tester_cancels()
    {
        Application("app-a", "Library", "import-a");
        Application("app-b", "Archive", "import-b");
        Current("app-a", "import-a");
        _guard.Register(() => "This plan has unsaved changes.");
        JSInterop.Setup<bool>("confirm", "This plan has unsaved changes. Switch project and discard them?").SetResult(false);
        var cut = Render<ProjectSelector>();
        cut.Find("select").Change("app:app-b");
        cut.WaitForAssertion(() => cut.Find("[data-testid=project-selector-status]").TextContent.Should().Contain("unsaved work is kept"));
        _explorer.CurrentScope.Should().Be(ProjectImportScope.For("import-a"));
        cut.Find("option[selected]").TextContent.Should().Be("Library", "the control shows the project still in use, not the refused choice");
        _autoSave.Verify(a => a.SaveNowAsync(), Times.Never);
    }

    [Fact]
    public void Unsaved_work_is_discarded_only_after_the_tester_confirms()
    {
        Application("app-a", "Library", "import-a");
        Application("app-b", "Archive", "import-b");
        Current("app-a", "import-a");
        _guard.Register(() => "This run has unsaved results.");
        JSInterop.Setup<bool>("confirm", "This run has unsaved results. Switch project and discard them?").SetResult(true);
        var cut = Render<ProjectSelector>();
        cut.Find("select").Change("app:app-b");
        cut.WaitForAssertion(() => _explorer.CurrentScope.Should().Be(ProjectImportScope.For("import-b")));
    }

    [Fact]
    public void The_testing_workspace_header_shows_the_selector_instead_of_plain_text()
    {
        Application("app-a", "Library", "import-a");
        Current("app-a", "import-a");
        var header = Render<TestingWorkspaceHeader>(p => p.Add(x => x.Workspace, "Manual Testing").Add(x => x.HomeRoute, "manual-testing")
            .Add(x => x.Stages, ManualTestingPresentation.Stages).Add(x => x.Title, "Manual Testing").Add(x => x.ProjectName, "Library"));
        header.Find("[data-testid=twh-project] [data-testid=project-selector-select]").Should().NotBeNull();
        header.Find("[data-testid=twh-project] option[selected]").TextContent.Should().Be("Library");
    }

    [Theory]
    [InlineData("http://localhost/test-plan-suggestions?area=prepare&draft=83fc", "http://localhost/test-plan-suggestions?area=prepare")]
    [InlineData("http://localhost/manual-testing/execute?run=1&case=2", "http://localhost/manual-testing/execute")]
    [InlineData("http://localhost/source-analysis?area=versions", "http://localhost/source-analysis?area=versions")]
    [InlineData("http://localhost/technical-testing", "http://localhost/technical-testing")]
    public void A_switch_drops_parameters_that_name_a_record_of_the_previous_project(string before, string after) =>
        ProjectSelector.WithoutRecordParameters(before).Should().Be(after);

    [Fact]
    public void After_a_switch_the_page_no_longer_opens_the_previous_projects_record()
    {
        Application("app-a", "Library", "import-a");
        Application("app-b", "Archive", "import-b");
        Current("app-a", "import-a");
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("test-plan-suggestions?area=prepare&draft=83fc");
        var cut = Render<ProjectSelector>();
        cut.Find("select").Change("app:app-b");
        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("test-plan-suggestions?area=prepare"));
    }
}
