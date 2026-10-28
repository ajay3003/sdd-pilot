using BirkNext.SourceDomains;
using BirkNext.Web.Models;
using ExplorerPage = BirkNext.Web.Pages.GeneratedDocumentationExplorer;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Generated Documentation Explorer as a type-aware browser: Structure is semantic (diagrams as participants/steps and components/edges, change
/// log by release, OpenAPI/GraphQL by operation and type), Source is the raw imported text, cards state availability, nothing is fabricated.
/// </summary>
public sealed class GeneratedDocumentationStructureTests : BunitContext
{
    private const string Sequence = "sequenceDiagram\n    participant PA as PersonAdapter\n    participant API\n    PA->>API: PUT /api/person/v1/innmating/barn\n    alt unauthorized\n        API-->>PA: 401 Unauthorized\n    else parent missing\n        API-->>PA: 422\n    end\n    API->>DB: save";
    private const string Graph = "graph TB\n    subgraph Clients\n        Web[Web app]\n    end\n    Web --> Api[M2LB.Person.Api]\n    Api -->|EF Core| Db[(PersonDb)]\n    Api -.-> Bus{{Service Bus}}";

    [Fact]
    public void A_sequence_diagram_reads_as_participants_ordered_steps_and_named_failure_paths()
    {
        var d = GeneratedDiagrams.Read(Sequence)!;
        d.Kind.Should().Be(GeneratedDiagramKind.Sequence);
        d.Participants.Should().Equal("PersonAdapter", "API", "DB");
        d.Steps.Select(s => (s.From, s.To, s.Message)).Should().StartWith(("PersonAdapter", "API", "PUT /api/person/v1/innmating/barn"));
        d.Steps.Single(s => s.Message == "422").Branch.Should().Be("else parent missing");
        d.Branches.Should().Equal("alt unauthorized", "else parent missing");
    }

    [Fact]
    public void A_graph_reads_as_components_groups_and_labelled_edges()
    {
        var d = GeneratedDiagrams.Read(Graph)!;
        d.Kind.Should().Be(GeneratedDiagramKind.Graph);
        d.Groups.Single().Name.Should().Be("Clients");
        d.Groups.Single().Members.Should().Equal("Web app");
        d.Edges.Should().Contain(new DiagramEdge("M2LB.Person.Api", "PersonDb", "EF Core")).And.Contain(e => e.From == "Web app" && e.To == "M2LB.Person.Api");
    }

    [Fact]
    public void Unsupported_or_hostile_diagrams_fall_back_safely()
    {
        GeneratedDiagrams.IsDiagram("erDiagram\n  A ||--o{ B : has").Should().BeTrue();
        GeneratedDiagrams.Read("erDiagram\n  A ||--o{ B : has").Should().BeNull("unsupported type → preview unavailable, source kept");
        var hostile = GeneratedDiagrams.Read("graph TB\n  A[\"<script>alert(1)</script>\"] --> B")!;
        hostile.Nodes.Select(n => n.Label).Should().NotContain(l => l.Contains('<') || l.Contains('>'), "labels are plain text, never markup");
        GeneratedDiagrams.IsDiagram("dotnet run").Should().BeFalse();
    }

    [Fact]
    public void Non_markdown_generated_artifacts_keep_their_text_and_binaries_never_count()
    {
        ProjectImportActivation.CapturableGenerated("Person/autodoc/openapi.yaml").Should().BeTrue();
        ProjectImportActivation.CapturableGenerated("Person/autodoc/schema.graphql").Should().BeTrue();
        ProjectImportActivation.CapturableGenerated("Person/autodoc/data-model.md").Should().BeTrue();
        ProjectImportActivation.CapturableGenerated("Person/autodoc/diagram.png").Should().BeFalse();
        BirkNext.Web.Services.GeneratedDocumentationExplorer.SourceLanguage("x/openapi.yaml").Should().Be("YAML");
        BirkNext.Web.Services.GeneratedDocumentationExplorer.SourceLanguage("x/schema.graphql").Should().Be("GraphQL SDL");
    }

    // ── page ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IGeneratedDocumentationApiService> _api = new();

    public GeneratedDocumentationStructureTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton(_api.Object);
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "i1", ProjectName = "M2LB", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "M2LB.zip", ImportedAt = DateTimeOffset.UnixEpoch, SourceSnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555") });
        state.CurrentProjectImportId = "i1";
        state.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet { SetId = "set-i1", ProjectImportId = "i1", SourceSnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555"), State = "Present" });
        Add("dataflow.md", "DataFlow", $"# Data flow\n\n## REST Ingestion Flow\n\n```mermaid\n{Sequence}\n```\n", true);
        Add("CHANGELOG.md", "Changelog", "# Changelog\n\n## [1.2.0] - 2026-10-01\n\n### Added\n\n- A\n\n## [1.1.0] - 2026-09-01\n\n### Fixed\n\n- B\n", true);
        Add("openapi.yaml", "ApiContract", "openapi: 3.0.1\ninfo:\n  title: Person API\n", true);
        Add("schema.graphql", "GraphQlSchema", "", false);
    }

    private void Add(string file, string kind, string text, bool captured)
    {
        var state = _repository.SddLifecycle;
        state.GeneratedDocumentationSets[0].Artifacts.Add(new SddGeneratedDocumentArtifact { ModuleId = "module:person", ModuleRoot = "Person", RelativePath = $"Person/autodoc/{file}", Kind = kind, Fingerprint = "fp-" + file, Generator = "maintain-docs", ContentCaptured = captured });
        if (captured) state.GeneratedDocumentContents["fp-" + file] = text;
    }

    private IRenderedComponent<ExplorerPage> Open(string file)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"generated-documentation-explorer?import=i1&module=Person&document={Uri.EscapeDataString("Person/autodoc/" + file)}");
        return Render<ExplorerPage>();
    }

    private static void Tab(IRenderedComponent<ExplorerPage> cut, string tab) => cut.Find($"[data-testid=gde-tab][data-tab={tab}]").Click();

    [Fact]
    public void Cards_state_availability_and_offer_details_instead_of_open_when_content_is_missing()
    {
        var cut = Open("dataflow.md");
        var cards = cut.FindAll("[data-testid=gde-document-card]");
        var graphql = cards.Single(c => c.TextContent.Contains("schema.graphql"));
        graphql.QuerySelector("[data-testid=gde-card-state]")!.TextContent.Should().Be("Detected — content unavailable");
        graphql.QuerySelector("[data-testid=gde-open]")!.TextContent.Should().StartWith("Details");
        cards.Single(c => c.GetAttribute("aria-current") == "true").TextContent.Should().Contain("dataflow.md");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "gde-doc-h");
    }

    [Fact]
    public void Structure_shows_the_flow_semantically_and_the_diagram_source_only_on_request_while_Source_is_raw()
    {
        var cut = Open("dataflow.md");
        Tab(cut, "Structure");
        var diagram = cut.Find("[data-testid=gde-diagram]");
        diagram.GetAttribute("data-kind").Should().Be("Sequence");
        cut.FindAll("[data-testid=gde-diagram-steps] li").Should().HaveCount(4);
        cut.Find("[data-testid=gde-diagram-branches]").TextContent.Should().Contain("alt unauthorized");
        diagram.QuerySelector(".disclosure-body")!.HasAttribute("hidden").Should().BeTrue("raw Mermaid is not the primary view");

        Tab(cut, "Source");
        cut.Find("[data-testid=gde-raw]").TextContent.Should().Contain("sequenceDiagram");
        cut.Find("[data-testid=gde-source-language]").TextContent.Should().StartWith("Raw Markdown");
    }

    [Fact]
    public void The_change_log_groups_releases_with_only_the_latest_expanded()
    {
        var cut = Open("CHANGELOG.md");
        Tab(cut, "Structure");
        cut.Find("[data-testid=gde-changelog-summary]").TextContent.Should().Contain("2 releases").And.Contain("latest 1.2.0");
        var versions = cut.FindAll("[data-testid=gde-changelog-version]");
        versions[0].QuerySelector(".disclosure-body")!.HasAttribute("hidden").Should().BeFalse();
        versions[1].QuerySelector(".disclosure-body")!.HasAttribute("hidden").Should().BeTrue();
    }

    [Fact]
    public void OpenAPI_structure_comes_from_the_backend_reader_and_Source_shows_the_yaml()
    {
        _api.Setup(a => a.StructureAsync("Person/autodoc/openapi.yaml", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SourceContract
        {
            Name = "Person API", Version = "1", Operations = [new("GET /barn/{id}", "operation", "/barn/{id}", ["id"]) { OperationId = "hentBarn", ResponseCodes = ["200", "404"], Security = ["ApiKey"] }],
            Types = [new("Barn", "object", [new("Id", "string", true)])],
        });
        var cut = Open("openapi.yaml");
        Tab(cut, "Structure");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gde-contract]").GetAttribute("data-kind").Should().Be("openapi"));
        cut.Find("[data-testid=gde-contract-summary]").TextContent.Should().Contain("1 operation").And.Contain("1 type");
        cut.Find("[data-testid=gde-contract-operation]").TextContent.Should().Contain("hentBarn").And.Contain("200, 404").And.Contain("ApiKey");
        Tab(cut, "Source");
        cut.Find("[data-testid=gde-source-language]").TextContent.Should().StartWith("Raw YAML");
        cut.Find("[data-testid=gde-raw]").TextContent.Should().Contain("openapi: 3.0.1");
    }
}
