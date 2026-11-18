using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The one project-selection mechanism (2026-11-18): options come from the imported-project registry (applications and their Project Import
/// records), switching goes through ProjectImportActivation and changes what every page reads (IArtifactExplorerContext.CurrentScope), and the
/// choice is saved. No project is created, no second selection state exists.
/// </summary>
public sealed class ProjectSelectionTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IWorkspaceAutoSaveService> _autoSave = new();
    private readonly ArtifactExplorerContext _explorer;
    private readonly ProjectSelection _selection;

    public ProjectSelectionTests()
    {
        var samples = new MockSampleProjectDocumentResolver { Repository = _repository };
        _explorer = new ArtifactExplorerContext(_repository, samples, samples);
        _autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(true);
        var services = new ServiceCollection().AddSingleton(_autoSave.Object).AddSingleton<IArtifactExplorerContext>(_explorer).BuildServiceProvider();
        _selection = new ProjectSelection(_repository, new ProjectImportActivation(_repository), services);
    }

    private SddLifecycleState Lifecycle => _repository.SddLifecycle;

    private void Application(string id, string name, params (string ImportId, int Minute)[] imports)
    {
        Lifecycle.Applications.Add(new SddApplicationContext { Id = id, Name = name, NameBasis = ApplicationContexts.PackageBasis });
        foreach (var (importId, minute) in imports)
            Lifecycle.ProjectImports.Add(new SddProjectImportRecord
            {
                ImportId = importId, ProjectName = importId, ArchiveFileName = importId + ".zip", ApplicationId = id,
                ImportedAt = new DateTimeOffset(2026, 11, 18, 9, minute, 0, TimeSpan.Zero),
            });
    }

    [Fact]
    public void Without_imports_there_is_nothing_to_choose_and_nothing_is_selected()
    {
        _selection.HasProjects.Should().BeFalse();
        _selection.Choices.Should().BeEmpty();
        _selection.CurrentValue.Should().BeEmpty();
    }

    [Fact]
    public void Each_application_is_one_choice_however_many_packages_it_has()
    {
        Application("app-a", "Library", ("import-a1", 1), ("import-a2", 5));
        Application("app-b", "Archive", ("import-b1", 3));
        Lifecycle.CurrentApplicationId = "app-a"; Lifecycle.CurrentProjectImportId = "import-a2";

        _selection.Choices.Select(c => (c.Value, c.Label, c.Packages)).Should().Equal(
            ("app:app-b", "Archive", 1), ("app:app-a", "Library", 2));
        _selection.CurrentValue.Should().Be("app:app-a");
    }

    [Fact]
    public void Equal_names_are_told_apart_by_when_they_were_imported_and_long_names_are_kept_whole()
    {
        var longName = new string('L', 140);
        Application("app-1", "Library", ("import-1", 1));
        Application("app-2", "library", ("import-2", 9));
        Application("app-3", longName, ("import-3", 4));

        var labels = _selection.Choices.Select(c => c.Label).ToList();
        labels.Count(l => l.StartsWith("Library — imported", StringComparison.OrdinalIgnoreCase)).Should().Be(2);
        labels.Distinct(StringComparer.Ordinal).Should().HaveCount(3, "every option is distinguishable");
        labels.Should().Contain(longName, "the name is never truncated in the data; the control ellipsises it");
    }

    [Fact]
    public void Many_projects_are_listed_in_name_order()
    {
        for (var i = 0; i < 120; i++) Application($"app-{i:000}", $"Project {i:000}", ($"import-{i:000}", i % 60));
        _selection.Choices.Should().HaveCount(120);
        _selection.Choices.Select(c => c.Name).Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_selected_Sample_Project_is_named_first_and_is_the_current_value()
    {
        Application("app-a", "Library", ("import-a1", 1));
        _repository.ProjectName = "lendo-plus";
        _selection.Choices[0].Should().Match<ProjectChoice>(c => c.Value == "sample:lendo-plus" && c.Label == "lendo-plus (Sample Project)");
        _selection.CurrentValue.Should().Be("sample:lendo-plus");
    }

    [Fact]
    public async Task Switching_changes_the_scope_every_page_reads_and_saves_the_choice()
    {
        Application("app-a", "Library", ("import-a1", 1));
        Application("app-b", "Archive", ("import-b1", 3));
        Lifecycle.CurrentApplicationId = "app-a"; Lifecycle.CurrentProjectImportId = "import-a1";
        var changed = 0;
        _explorer.Changed += (_, _) => changed++;

        (await _selection.SwitchAsync("app:app-b")).Should().Be(ProjectSwitchOutcome.Switched);

        _explorer.CurrentScope.Should().Be(ProjectImportScope.For("import-b1"), "pages read the new project's documents, not only a new label");
        Lifecycle.CurrentApplicationId.Should().Be("app-b");
        _selection.CurrentValue.Should().Be("app:app-b");
        changed.Should().BeGreaterThan(0, "pages are told to reload");
        _selection.Generation.Should().Be(1, "the layout re-creates the page for the new project");
        _autoSave.Verify(a => a.SaveNowAsync(), Times.Once);
        Lifecycle.ProjectImports.Should().HaveCount(2, "selecting never creates a project");
    }

    [Fact]
    public async Task Switching_away_from_a_Sample_Project_leaves_it()
    {
        Application("app-a", "Library", ("import-a1", 1));
        _repository.ProjectName = "lendo-plus";
        (await _selection.SwitchAsync("app:app-a")).Should().Be(ProjectSwitchOutcome.Switched);
        _repository.ProjectName.Should().BeNull();
        _explorer.CurrentScope.Should().Be(ProjectImportScope.For("import-a1"));
    }

    [Fact]
    public async Task A_single_source_view_of_the_previous_project_is_cleared_so_it_cannot_keep_showing_that_project()
    {
        Application("app-a", "Library", ("import-a1", 1));
        Application("app-b", "Archive", ("import-b1", 3));
        Lifecycle.CurrentApplicationId = "app-a"; Lifecycle.CurrentProjectImportId = "import-a1";
        _explorer.SelectSource("import-a1");

        await _selection.SwitchAsync("app:app-b");

        _explorer.ExplorerSource.Should().BeNull();
        _explorer.CurrentScope.Should().Be(ProjectImportScope.For("import-b1"));
    }

    [Fact]
    public async Task Choosing_the_current_project_again_changes_and_saves_nothing()
    {
        Application("app-a", "Library", ("import-a1", 1));
        Lifecycle.CurrentApplicationId = "app-a"; Lifecycle.CurrentProjectImportId = "import-a1";
        (await _selection.SwitchAsync("app:app-a")).Should().Be(ProjectSwitchOutcome.Unchanged);
        _autoSave.Verify(a => a.SaveNowAsync(), Times.Never);
    }

    [Theory]
    [InlineData("app:app-removed")]
    [InlineData("import:import-removed")]
    [InlineData("something-else")]
    public async Task An_unknown_or_removed_project_is_refused_and_the_current_one_stays(string value)
    {
        Application("app-a", "Library", ("import-a1", 1));
        Lifecycle.CurrentApplicationId = "app-a"; Lifecycle.CurrentProjectImportId = "import-a1";
        (await _selection.SwitchAsync(value)).Should().Be(ProjectSwitchOutcome.Unavailable);
        _selection.CurrentValue.Should().Be("app:app-a");
        _autoSave.Verify(a => a.SaveNowAsync(), Times.Never);
    }

    [Fact]
    public async Task A_failed_save_is_reported_while_the_tab_already_shows_the_new_project()
    {
        Application("app-a", "Library", ("import-a1", 1));
        Application("app-b", "Archive", ("import-b1", 3));
        Lifecycle.CurrentApplicationId = "app-a"; Lifecycle.CurrentProjectImportId = "import-a1";
        _autoSave.Setup(a => a.SaveNowAsync()).ReturnsAsync(false);
        (await _selection.SwitchAsync("app:app-b")).Should().Be(ProjectSwitchOutcome.SwitchedNotSaved);
        _selection.CurrentValue.Should().Be("app:app-b");
    }

    [Fact]
    public async Task Rapid_switching_ends_on_the_last_choice()
    {
        Application("app-a", "Library", ("import-a1", 1));
        Application("app-b", "Archive", ("import-b1", 3));
        Application("app-c", "Catalogue", ("import-c1", 5));
        Lifecycle.CurrentApplicationId = "app-a"; Lifecycle.CurrentProjectImportId = "import-a1";
        await Task.WhenAll(_selection.SwitchAsync("app:app-b"), _selection.SwitchAsync("app:app-c"));
        _selection.CurrentValue.Should().Be("app:app-c");
        _explorer.CurrentScope.Should().Be(ProjectImportScope.For("import-c1"));
    }

    [Fact]
    public void The_unsaved_work_guard_reports_registered_work_until_the_page_goes()
    {
        var guard = new UnsavedWorkGuard();
        var dirty = false;
        var registration = guard.Register(() => dirty ? "This plan has unsaved changes." : null);
        guard.Pending.Should().BeNull();
        dirty = true;
        guard.Pending.Should().Be("This plan has unsaved changes.");
        registration.Dispose();
        guard.Pending.Should().BeNull("a page that is gone protects nothing");
    }
}
