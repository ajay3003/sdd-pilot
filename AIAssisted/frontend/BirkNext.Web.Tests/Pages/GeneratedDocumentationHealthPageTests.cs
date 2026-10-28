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

    private void Import(Guid? snapshot, string projectName = "M2LB")
    {
        var preview = Preview(new string('g', 64), source: snapshot is not null, ("specs/001-x/spec.md", Spec)) with { ProjectName = projectName };
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
        cut.Find("[data-testid=gdh-target]").TextContent.Should().Contain("Current project").And.Contain("M2LB");
        cut.Find("[data-testid=gdh-target-project]").TextContent.Should().Be("M2LB");
        cut.Find("[data-testid=gdh-target-snapshot]").TextContent.Should().Be("shop.zip");
        cut.Find("[data-testid=gdh-target-snapshot-id]").TextContent.Should().Be(snapshot.ToString());
        cut.Find("[data-testid=gdh-target-basis]").TextContent.Should().Be("Snapshot of the current imported project");
        cut.Markup.Should().Contain(GeneratedDocumentationHealthPage.IntroText).And.Contain("Autodoc: Not configured");
        cut.Find("[data-testid=gdh-page-boundary]").TextContent.Should().Contain("A discrepancy is a review candidate, not proof of a defect");

        cut.Find("[data-testid=gdh-run]").Click();
        _api.Requests.Should().ContainSingle().Which.ProjectImportId.Should().Be(_repository.SddLifecycle.CurrentProjectImportId, "only the current import's snapshot is analysed");
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
