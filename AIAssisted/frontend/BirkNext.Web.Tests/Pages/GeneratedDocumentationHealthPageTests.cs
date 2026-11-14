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

    // ── module focus from an Explorer link ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Health modules carry the archive's top-level folder in their root path; the Explorer names the module without it.</summary>
    private static GeneratedDocumentationDiagnosticRun FocusRun() => new()
    {
        State = GeneratedDocumentationRunState.Ready,
        Modules =
        [
            new GeneratedDocumentationHealthResult
            {
                Module = "Person API", ModuleId = "module:person", RootPath = "M2LB/Person",
                Evidence = [new GeneratedDocumentationEvidence { SafeRelativePath = "M2LB/Person/autodoc/README.md" }, new GeneratedDocumentationEvidence { SafeRelativePath = "M2LB/Person/autodoc/dataflow.md" }],
            },
            new GeneratedDocumentationHealthResult { Module = "Order", ModuleId = "module:order", RootPath = "M2LB/Order" },
        ],
        Autodoc = new AutodocDiagnostics
        {
            DetectedModules = 2, ModulesUsingAutodoc = 2, ModulesWithGeneratedOutput = 2,
            Modules =
            [
                new AutodocModuleInventory { Name = "Orders", Identity = "component:orders", GeneratedModuleId = "module:order", GeneratedOutputPresent = true, Usage = AutodocUsage.Configured, ArtifactCount = 1, ArtifactPaths = ["M2LB/Order/autodoc/README.md"] },
                new AutodocModuleInventory
                {
                    Name = "Persons API", Identity = "component:persons", GeneratedModuleId = "module:person", GeneratedOutputPresent = true, Usage = AutodocUsage.Configured, ArtifactCount = 2,
                    ArtifactPaths = ["M2LB/Person/autodoc/README.md", "M2LB/Person/autodoc/dataflow.md"],
                },
            ],
        },
    };

    private IRenderedComponent<GeneratedDocumentationHealthPage> OpenLinked(Guid snapshot, string module, string? document = null)
    {
        _api.Result = FocusRun();
        var import = _repository.SddLifecycle.CurrentProjectImportId;
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(
            $"generated-documentation-health?import={import}&snapshot={snapshot}&module={Uri.EscapeDataString(module)}{(document is null ? "" : $"&document={Uri.EscapeDataString(document)}")}");
        return Render<GeneratedDocumentationHealthPage>();
    }

    [Fact]
    public void A_link_naming_a_module_runs_once_opens_that_module_and_marks_the_document()
    {
        var snapshot = Guid.NewGuid();
        Import(snapshot);
        var cut = OpenLinked(snapshot, "Person", "Person/autodoc/dataflow.md");

        cut.WaitForAssertion(() => cut.Find("[data-testid=gdh-autodoc-detail]"));
        _api.Requests.Should().ContainSingle("the link runs the same read-only diagnostics once").Which.SnapshotId.Should().Be(snapshot);
        cut.Find("#gdh-autodoc-focus-title").TextContent.Should().Be("Persons API", "joined by the Health module id, not by a similar name");
        cut.Find("[data-testid=gdh-module-focus]").GetAttribute("data-module-id").Should().Be("module:person");
        cut.FindAll("[data-testid=gdh-module-not-found]").Should().BeEmpty();
        cut.FindAll("[data-testid=gdh-autodoc-detail] li[data-focused=true] a").Should().ContainSingle()
            .Which.GetAttribute("href").Should().EndWith(Uri.EscapeDataString("M2LB/Person/autodoc/dataflow.md"));
        cut.FindAll("[data-testid=gdh-module][data-focused=true]").Should().ContainSingle().Which.GetAttribute("data-module").Should().Be("Person API");
        cut.FindAll("[data-testid=gdh-document][data-focused=true]").Should().ContainSingle();
    }

    [Fact]
    public void A_link_naming_an_unknown_module_says_so_and_shows_all()
    {
        var snapshot = Guid.NewGuid();
        Import(snapshot);
        var cut = OpenLinked(snapshot, "Billing");

        cut.WaitForAssertion(() => cut.Find("[data-testid=gdh-module-not-found]").TextContent.Should().Be("Module not found in this run; showing all."));
        cut.FindAll("[data-testid=gdh-autodoc-detail]").Should().BeEmpty();
        cut.FindAll("[data-testid=gdh-autodoc-row]").Should().HaveCount(2, "every module stays listed");
        cut.FindAll("[data-testid=gdh-module-focus]").Should().BeEmpty();
    }

    private sealed class RecordingApi : IGeneratedDocumentationApiService
    {
        public List<GeneratedDocumentationDiagnosticRequest> Requests { get; } = [];
        /// <summary>The run to return; a "no generated documentation" run when not set.</summary>
        public GeneratedDocumentationDiagnosticRun? Result { get; set; }

        public Task<GeneratedDocumentationDiagnosticRun?> RunAsync(GeneratedDocumentationDiagnosticRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult<GeneratedDocumentationDiagnosticRun?>(Result ?? new GeneratedDocumentationDiagnosticRun
            {
                State = GeneratedDocumentationRunState.NoGeneratedDocumentation, StateMessage = "No generated documentation was found in this snapshot.",
            });
        }
    }
}
