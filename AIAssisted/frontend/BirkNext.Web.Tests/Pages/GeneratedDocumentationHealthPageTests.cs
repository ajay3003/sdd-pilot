using BirkNext.GeneratedDocumentation;
using BirkNext.ProjectImport;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Generated Documentation Health as project analysis: Source Review navigation and feature visibility, one canonical route, the target
/// project and its source snapshot shown first, a neutral fail-safe without an imported project, and the existing diagnostics reused unchanged.
/// </summary>
public sealed class GeneratedDocumentationHealthPageTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();
    private readonly RecordingApi _api = new();

    public GeneratedDocumentationHealthPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _samples.Repository = _repository;
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton<IArtifactExplorerContext>(new ArtifactExplorerContext(_repository, _samples, _samples));
        Services.AddSingleton<IGeneratedDocumentationApiService>(_api);
    }

    private void Import(Guid? snapshot, string projectName = "M2LB", char sha = 'g')
    {
        var preview = Preview(new string(sha, 64), source: snapshot is not null, ("specs/001-x/spec.md", Spec)) with { ProjectName = projectName };
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview),
            Commit(preview, snapshot is null ? ProjectImportSourceState.NotDetected : ProjectImportSourceState.Created, snapshot));
    }

    [Fact]
    public void Navigation_places_it_under_source_review_after_the_explorer_with_one_route()
    {
        var row = NavigationCatalog.Sections.SelectMany(s => s.Items.Select(i => (Section: s.Id, Item: i))).Single(x => x.Item.Label == "Generated Documentation Health");
        row.Section.Should().Be("source-review", "it runs over the import's Source Analysis snapshot and checks generated docs against source");
        var sourceRoutes = NavigationCatalog.Sections.Single(s => s.Id == "source-review").Items.Select(i => i.Route).ToList();
        sourceRoutes.IndexOf(NavigationCatalog.GeneratedDocumentationHealthRoute).Should().Be(sourceRoutes.IndexOf(NavigationCatalog.GeneratedDocumentationExplorerRoute) + 1,
            "discovery (Explorer) first, then its health");
        row.Item.Route.Should().Be("generated-documentation-health");
        row.Item.Visible(new FeatureVisibilityService()).Should().BeTrue("a configuration without the new key keeps it visible");
        NavigationCatalog.Sections.Single(s => s.Id == "admin").Items.Should().NotContain(i => i.Route.Contains("generated-docs-health"));
        DocumentationCatalog.Features.Single(f => f.FeatureId == "generated-documentation-health").Category.Should().Be("Source Review");
        typeof(GeneratedDocumentationHealthPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template).Should().Equal("/generated-documentation-health");
    }

    [Fact]
    public void Without_an_imported_project_the_page_is_neutral_and_never_runs()
    {
        var cut = Render<GeneratedDocumentationHealthPage>();

        cut.Find("[data-testid=gdh-no-project]").TextContent.Should().Contain(GeneratedDocumentationHealthPage.NoProjectMessage);
        cut.FindAll("[data-testid=gdh-run]").Should().BeEmpty("no other project's or newest snapshot is ever used");
        _api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Shows_the_target_project_and_its_snapshot_and_runs_the_existing_diagnostics_for_that_import()
    {
        var snapshot = Guid.NewGuid();
        Import(snapshot);
        var cut = Render<GeneratedDocumentationHealthPage>();

        cut.Find("h1").TextContent.Should().Be("Generated Documentation Health", "the feature identity is project-neutral");
        cut.Find("[data-testid=gdh-target]").TextContent.Should().Contain("Analyzing").And.Contain("M2LB");
        cut.Find("[data-testid=gdh-target-project]").TextContent.Should().Be("M2LB");
        cut.Find("[data-testid=gdh-target-snapshot]").TextContent.Should().Be("shop.zip");
        cut.Find("[data-testid=gdh-target-snapshot-id]").TextContent.Should().Be(snapshot.ToString());
        cut.Find("[data-testid=gdh-target-basis]").TextContent.Should().Be("Snapshot of the current imported project");
        cut.Markup.Should().Contain(GeneratedDocumentationHealthPage.IntroText).And.Contain("Generated docs: Not configured");
        cut.Find("[data-testid=gdh-page-boundary]").TextContent.Should().Contain("A discrepancy is a review candidate, not proof of a defect");

        cut.Find("[data-testid=gdh-run]").Click();
        var request = _api.Requests.Should().ContainSingle().Subject;
        request.ProjectImportId.Should().Be(_repository.SddLifecycle.CurrentProjectImportId, "only the current import's snapshot is analysed");
        request.SnapshotId.Should().Be(snapshot, "the exact selected snapshot, never the newest");
        cut.Find("[data-testid=gdh-open-explorer-page]").GetAttribute("href").Should().Be($"generated-documentation-explorer?import={_repository.SddLifecycle.CurrentProjectImportId}");
    }

    [Fact]
    public void Selectors_list_projects_and_their_snapshots_and_switching_runs_the_selected_snapshot_without_changing_the_current_project()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Import(first, sha: 'a');
        var firstImport = _repository.SddLifecycle.CurrentProjectImportId;
        Import(second, sha: 'b');
        var current = _repository.SddLifecycle.CurrentProjectImportId;
        var cut = Render<GeneratedDocumentationHealthPage>();

        cut.FindAll("[data-testid=gdh-project] option").Select(o => o.TextContent).Should().Equal("M2LB");
        var options = cut.FindAll("[data-testid=gdh-snapshot-select] option");
        options.Select(o => o.GetAttribute("value")).Should().Equal(first.ToString(), second.ToString());
        options[0].TextContent.Should().MatchRegex(@"^shop\.zip · analyzed .+ · [0-9a-f]{8}$");
        cut.Find("[data-testid=gdh-target-snapshot-id]").TextContent.Should().Be(second.ToString(), "the current project's own snapshot");

        cut.Find("[data-testid=gdh-snapshot-select]").Change(first.ToString());
        cut.Find("[data-testid=gdh-target-basis]").TextContent.Should().Be("Selected on this page");
        cut.Find("[data-testid=gdh-run]").Click();
        _api.Requests.Should().ContainSingle().Which.Should().Match<GeneratedDocumentationDiagnosticRequest>(r => r.SnapshotId == first && r.ProjectImportId == firstImport);
        _api.Requests[0].AuthoredArtifacts.Should().BeEmpty("authored artifacts of the current project are not compared with another import's snapshot");
        _repository.SddLifecycle.CurrentProjectImportId.Should().Be(current, "selecting never changes the global current project");

        cut.Find("[data-testid=gdh-snapshot-select]").Change(second.ToString());
        cut.FindAll("[data-testid=gdh-run-status]").Should().BeEmpty("a result is never shown for a selection it was not run for");
    }

    [Fact]
    public void A_deep_link_from_the_explorer_wins_over_the_current_project()
    {
        var first = Guid.NewGuid();
        Import(first, sha: 'a');
        var firstImport = _repository.SddLifecycle.CurrentProjectImportId;
        Import(Guid.NewGuid(), sha: 'b');
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"generated-documentation-health?import={firstImport}&snapshot={first}");
        var cut = Render<GeneratedDocumentationHealthPage>();

        cut.Find("[data-testid=gdh-target-snapshot-id]").TextContent.Should().Be(first.ToString());
        cut.Find("[data-testid=gdh-target-basis]").TextContent.Should().Be("Link from Generated Documentation Explorer");
    }

    [Fact]
    public void A_deep_link_to_an_unknown_snapshot_falls_back_to_the_linked_imports_own_snapshot()
    {
        var own = Guid.NewGuid();
        Import(own);
        var import = _repository.SddLifecycle.CurrentProjectImportId;
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"generated-documentation-health?import={import}&snapshot={Guid.NewGuid()}");
        var cut = Render<GeneratedDocumentationHealthPage>();

        cut.Find("[data-testid=gdh-target-snapshot-id]").TextContent.Should().Be(own.ToString(), "an unknown or other-project snapshot is never borrowed");
    }

    [Fact]
    public void An_import_without_a_source_snapshot_says_so_and_does_not_borrow_another()
    {
        Import(snapshot: null);
        var cut = Render<GeneratedDocumentationHealthPage>();

        cut.Find("[data-testid=gdh-no-snapshot]").TextContent.Should().Contain("Snapshots of other projects are not used");
        cut.FindAll("[data-testid=gdh-run]").Should().BeEmpty();
    }

    private sealed class RecordingApi : IGeneratedDocumentationApiService
    {
        public List<GeneratedDocumentationDiagnosticRequest> Requests { get; } = [];

        public Task<GeneratedDocumentationDiagnosticRun?> RunAsync(GeneratedDocumentationDiagnosticRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult<GeneratedDocumentationDiagnosticRun?>(new GeneratedDocumentationDiagnosticRun
            {
                State = GeneratedDocumentationRunState.NoGeneratedDocumentation, StateMessage = "No generated documentation was found in this snapshot.",
            });
        }
    }
}
