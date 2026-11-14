using BirkNext.GeneratedDocumentation;
using BirkNext.Web.Models;
using ExplorerPage = BirkNext.Web.Pages.GeneratedDocumentationExplorer;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Generated Documentation Explorer context: cross-document search within the selected import, the additional diagram types (state, class and ER
/// diagrams as relationships; other Mermaid types honestly not drawn) and the contextual Generated Documentation Health findings of a document.
/// </summary>
public sealed class GeneratedDocumentationExplorerContextTests : BunitContext
{
    private static readonly Guid Snapshot1 = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Snapshot2 = Guid.Parse("99999999-2222-3333-4444-555555555555");

    private const string State = "stateDiagram-v2\n    [*] --> Draft\n    Draft --> Submitted : submit\n    Submitted --> [*]\n    state \"Under review\" as Review\n    Submitted --> Review : <b>assign</b>";
    private const string Class = "classDiagram\n    class Animal\n    Animal <|-- Dog\n    Owner \"1\" --> \"*\" Dog : owns\n    Car *-- Wheel\n    Pond o-- Duck\n    Service ..> Repo\n    A -- B";
    private const string Er = "erDiagram\n    CUSTOMER ||--o{ ORDER : places\n    ORDER ||--|{ LINE-ITEM : contains\n    CUSTOMER {\n        string name\n    }\n    PRODUCT";
    private const string Gantt = "gantt\n    title Plan\n    section A\n    Task :a1, 2024-01-01, 3d";

    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IGeneratedDocumentationApiService> _api = new();

    public GeneratedDocumentationExplorerContextTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton(_api.Object);
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "i1", ProjectName = "M2LB", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "M2LB.zip", ImportedAt = DateTimeOffset.UnixEpoch, SourceSnapshotId = Snapshot1 });
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "i2", ProjectName = "Other project", ArchiveFileName = "Other.zip", ImportedAt = DateTimeOffset.UnixEpoch.AddDays(1), SourceSnapshotId = Snapshot2 });
        state.CurrentProjectImportId = "i1";
        state.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet { SetId = "set-i1", ProjectImportId = "i1", SourceSnapshotId = Snapshot1, State = "Present" });
        state.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet { SetId = "set-i2", ProjectImportId = "i2", SourceSnapshotId = Snapshot2, State = "Present" });
        Add(0, "Person", "dataflow.md", "DataFlow", "# Data flow\n\n## Ingestion\n\nThe Barnetrygd payload is validated.\n");
        Add(0, "Person", "README.md", "Overview", "# Person overview\n\nBarnetrygd appears twice: Barnetrygd.\n");
        Add(0, "Person", "CHANGELOG.md", "Changelog", "# Changelog\n\n## [1.0.0] - 2026-10-01\n\n### Added\n\n- A\n");
        Add(0, "Order", "architecture.md", "Architecture", "# Order architecture\n\nNo match in this paragraph.\n\n## Barnetrygd link\n\nText.\n");
        Add(1, "Other", "README.md", "Overview", "# Other\n\nBarnetrygd in another import.\n");
    }

    private void Add(int set, string module, string file, string kind, string text)
    {
        var state = _repository.SddLifecycle;
        var fingerprint = $"fp-{set}-{module}-{file}";
        state.GeneratedDocumentationSets[set].Artifacts.Add(new SddGeneratedDocumentArtifact { ModuleId = "module:" + module.ToLowerInvariant(), ModuleRoot = module, RelativePath = $"{module}/autodoc/{file}", Kind = kind, Fingerprint = fingerprint, Generator = "maintain-docs", ContentCaptured = true });
        state.GeneratedDocumentContents[fingerprint] = text;
    }

    private IRenderedComponent<ExplorerPage> Open(string module, string file, string? view = null)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"generated-documentation-explorer?import=i1&module={module}&document={Uri.EscapeDataString($"{module}/autodoc/{file}")}{(view is null ? "" : $"&view={view}")}");
        return Render<ExplorerPage>();
    }

    private static IRenderedComponent<ExplorerPage> SearchAll(IRenderedComponent<ExplorerPage> cut, string term)
    {
        cut.Find("[data-testid=gde-search-scope]").Click();
        cut.Find("[data-testid=gde-search-scope]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=gde-search]").Input(term);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=gde-search-result]").Should().NotBeEmpty());
        return cut;
    }

    // ── cross-document search ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Single_document_search_stays_the_default()
    {
        var cut = Open("Person", "dataflow.md");
        cut.Find("[data-testid=gde-search-scope]").GetAttribute("aria-pressed").Should().Be("false");
        cut.Find("[data-testid=gde-search-scope]").TextContent.Should().Be("Search all documents of this import");
        cut.Find("[data-testid=gde-search]").Input("barnetrygd");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gde-search-count]").TextContent.Should().Be("1 of 1"));
        cut.FindAll("[data-testid=gde-search-results]").Should().BeEmpty();
    }

    [Fact]
    public void Searching_all_documents_groups_results_by_document_within_the_selected_import_only()
    {
        var cut = SearchAll(Open("Person", "dataflow.md"), "barnetrygd");

        var results = cut.FindAll("[data-testid=gde-search-result]");
        results.Select(r => r.GetAttribute("data-path")).Should().Equal("Person/autodoc/README.md", "Order/autodoc/architecture.md", "Person/autodoc/dataflow.md");
        results.Select(r => r.GetAttribute("data-path")).Should().NotContain(p => p!.StartsWith("Other/"), "another import's documents are never searched");
        cut.Find("[data-testid=gde-search-summary]").TextContent.Should().Be("4 matches in 3 documents of this import");
        results[0].QuerySelector("[data-testid=gde-search-result-open]")!.TextContent.Should().Be("Person overview", "the document's own title");
        results[0].QuerySelector("[data-testid=gde-search-result-count]")!.TextContent.Should().Be("2 matches");
        results[0].QuerySelector(".gde-pill")!.TextContent.Should().Be("Overview");
        var snippet = results[0].QuerySelector("[data-testid=gde-search-snippet]")!;
        snippet.GetAttribute("data-line").Should().Be("3");
        snippet.QuerySelector("mark")!.TextContent.Should().Be("Barnetrygd");

        cut.Find("[data-testid=gde-search-all-type]").Change("Architecture");
        cut.FindAll("[data-testid=gde-search-result]").Select(r => r.GetAttribute("data-path")).Should().Equal("Order/autodoc/architecture.md");
    }

    [Fact]
    public void Opening_a_result_switches_module_highlights_and_focuses_the_first_hit_and_Back_returns()
    {
        var cut = SearchAll(Open("Person", "dataflow.md"), "barnetrygd");
        cut.FindAll("[data-testid=gde-search-result]").Single(r => r.GetAttribute("data-path") == "Order/autodoc/architecture.md")
            .QuerySelector("[data-testid=gde-search-result-open]")!.Click();

        cut.Find("#gde-doc-h").TextContent.Should().StartWith("Order architecture");
        cut.Find("[data-testid=gde-module] option[selected]").TextContent.Should().Be("Order", "the module follows the opened document");
        cut.Find("[data-testid=gde-tab][aria-selected=true]").GetAttribute("data-tab").Should().Be("Read");
        cut.FindAll("[data-testid=gde-read] mark.gde-hit").Should().ContainSingle().Which.TextContent.Should().Be("Barnetrygd");
        cut.Find("[data-testid=gde-search]").GetAttribute("value").Should().Be("barnetrygd");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "gde-hit-0");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("module=Order").And.Contain(Uri.EscapeDataString("Order/autodoc/architecture.md"));

        var back = cut.Find("[data-testid=gde-search-back]");
        back.TextContent.Should().Contain("Back to Data flow");
        back.Click();
        cut.Find("#gde-doc-h").TextContent.Should().StartWith("Data flow");
        cut.Find("[data-testid=gde-module] option[selected]").TextContent.Should().Be("Person");
        cut.FindAll("[data-testid=gde-search-back]").Should().BeEmpty();
    }

    [Fact]
    public void Cross_document_search_is_bounded_plain_text_and_case_insensitive()
    {
        var docs = Enumerable.Range(0, 60).Select(i => (new GeneratedExplorerDocument(
            new DriftDocument($"d{i}", DriftOrigin.Generated, $"k|m|d{i}.md", "", $"M/d{i}.md", "Generated · Overview", $"f{i}", "i1", null, null, null, null, "s", null, "M", null, true, true, null),
            "M", false, GeneratedDocumentRole.Overview), (string?)$"x <script>TERM</script> y\nterm\nTerm\nTERM again"));
        var results = GeneratedDocumentationExplorerContext.Search(docs, "term");
        results.Results.Should().HaveCount(GeneratedDocumentationExplorerContext.MaxDocuments);
        results.Truncated.Should().BeTrue();
        results.Results[0].Matches.Should().Be(4);
        results.Results[0].Snippets.Should().HaveCount(GeneratedDocumentationExplorerContext.MaxSnippetsPerDocument);
        results.Results[0].Snippets[0].Before.Should().Be("x <script>", "snippets are raw text; Blazor escapes them");
        GeneratedDocumentationExplorerContext.Search(docs, "t").Results.Should().BeEmpty("one character is not searched");
    }

    // ── diagrams ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_state_diagram_reads_as_states_and_labelled_transitions_with_Start_and_End()
    {
        var d = GeneratedDiagrams.Read(State)!;
        d.Kind.Should().Be(GeneratedDiagramKind.Graph);
        d.SourceType.Should().Be("stateDiagram-v2");
        GeneratedDiagrams.TypeLabel(d).Should().Be("State diagram");
        d.Nodes.Select(n => n.Label).Should().Equal("Start", "Draft", "Submitted", "End", "Under review");
        d.Edges.Should().Contain(new DiagramEdge("Start", "Draft", null)).And.Contain(new DiagramEdge("Draft", "Submitted", "submit")).And.Contain(new DiagramEdge("Submitted", "End", null));
        d.Edges.Single(e => e.To == "Under review").Label.Should().NotContain("<").And.NotContain(">").And.Contain("assign");
    }

    [Fact]
    public void A_class_diagram_reads_relationships_only()
    {
        var d = GeneratedDiagrams.Read(Class)!;
        d.Kind.Should().Be(GeneratedDiagramKind.Graph);
        GeneratedDiagrams.TypeLabel(d).Should().Be("Class diagram (relationships only)");
        d.Nodes.Select(n => n.Label).Should().Equal("Animal", "Dog", "Owner", "Car", "Wheel", "Pond", "Duck", "Service", "Repo", "A", "B");
        d.Edges.Should().Equal(
            new DiagramEdge("Dog", "Animal", "inherits"), new DiagramEdge("Owner", "Dog", "owns"), new DiagramEdge("Car", "Wheel", "composition"),
            new DiagramEdge("Pond", "Duck", "aggregation"), new DiagramEdge("Service", "Repo", "dependency"), new DiagramEdge("A", "B", "link"));
    }

    [Fact]
    public void An_ER_diagram_reads_entities_and_relationships_with_cardinality_in_words()
    {
        var d = GeneratedDiagrams.Read(Er)!;
        GeneratedDiagrams.TypeLabel(d).Should().Be("ER diagram (relationships only)");
        d.Nodes.Select(n => n.Label).Should().Equal("CUSTOMER", "ORDER", "LINE-ITEM", "PRODUCT");
        d.Edges.Should().Equal(new DiagramEdge("CUSTOMER", "ORDER", "places (exactly one to zero or more)"), new DiagramEdge("ORDER", "LINE-ITEM", "contains (exactly one to one or more)"));
    }

    [Fact]
    public void Hostile_labels_in_the_new_diagram_types_stay_plain_text()
    {
        var state = GeneratedDiagrams.Read("stateDiagram\n    state \"<img src=x onerror=alert(1)>\" as X\n    [*] --> X : <script>alert(1)</script>")!;
        var er = GeneratedDiagrams.Read("erDiagram\n    A ||--o{ B : \"<script>x</script>\"")!;
        var cls = GeneratedDiagrams.Read("classDiagram\n    A --> B : <iframe src=x>")!;
        foreach (var d in new[] { state, er, cls })
        {
            d.Nodes.Select(n => n.Label).Concat(d.Edges.Select(e => e.Label ?? "")).Should().NotContain(l => l.Contains('<') || l.Contains('>'));
        }
    }

    [Fact]
    public void Other_mermaid_types_are_not_drawn_and_say_why()
    {
        GeneratedDiagrams.IsDiagram(Gantt).Should().BeTrue();
        GeneratedDiagrams.Read(Gantt).Should().BeNull();
        GeneratedDiagrams.UnavailableReason(Gantt).Should().Be("Diagram type 'gantt' is not drawn by BirkNext; its source is shown.");
        GeneratedDiagrams.UnavailableReason("pie title Pets\n  \"Dogs\" : 3").Should().Contain("'pie'");
    }

    [Theory]
    [InlineData("state", 5, "stateDiagram-v2", "State diagram")]
    [InlineData("class", 11, "classDiagram", "Class diagram (relationships only)")]
    [InlineData("er", 4, "erDiagram", "ER diagram (relationships only)")]
    public void The_new_diagram_types_are_drawn_in_Read_with_an_honest_caption(string which, int nodes, string sourceType, string caption)
    {
        var source = which switch { "state" => State, "class" => Class, _ => Er };
        Add(0, "Person", "diagram.md", "Architecture", $"# Diagram\n\n## Model\n\n```mermaid\n{source}\n```\n");
        var cut = Open("Person", "diagram.md");
        var diagram = cut.Find("[data-testid=gde-diagram]");
        diagram.GetAttribute("data-kind").Should().Be("Graph");
        diagram.GetAttribute("data-source-type").Should().Be(sourceType);
        cut.FindAll("[data-testid=gdv-svg] [data-testid=gdv-node]").Should().HaveCount(nodes, "one box per node — nothing invented");
        cut.Find("[data-testid=gdv-caption]").TextContent.Should().StartWith(caption);
        cut.Find("[data-testid=gde-read]").QuerySelectorAll("script, img, iframe, b").Should().BeEmpty();
    }

    [Fact]
    public void An_unsupported_diagram_in_a_document_shows_its_source_and_the_reason()
    {
        Add(0, "Person", "plan.md", "Other", $"# Plan\n\n```mermaid\n{Gantt}\n```\n");
        var cut = Open("Person", "plan.md");
        cut.Find("[data-testid=gde-diagram]").GetAttribute("data-kind").Should().Be("Unavailable");
        cut.Find("[data-testid=gdv-unavailable-reason]").TextContent.Should().Be("Diagram type 'gantt' is not drawn by BirkNext; its source is shown.");
        cut.Find("[data-testid=gde-diagram]").TextContent.Should().Contain("section A");
    }

    // ── contextual health ────────────────────────────────────────────────────────────────────────────────────────────

    private static GeneratedDocumentationDiagnosticRun HealthRun() => new()
    {
        State = GeneratedDocumentationRunState.Ready,
        Modules =
        [
            new GeneratedDocumentationHealthResult
            {
                Module = "Person", ModuleId = "module:person", RootPath = "Person", Status = GeneratedDocumentationHealthStatus.Warning,
                MissingExpectedDocs = ["messaging.md"], SourceDiscrepancies = ["d1", "d2"],
                Evidence =
                [
                    new GeneratedDocumentationEvidence { SafeRelativePath = "Person/autodoc/dataflow.md", StructurallyInvalid = true, Warnings = ["Unclosed fence at line 9"] },
                    new GeneratedDocumentationEvidence { SafeRelativePath = "Person/autodoc/README.md" },
                ],
            },
            new GeneratedDocumentationHealthResult { Module = "Order", ModuleId = "module:order", RootPath = "Order", MissingExpectedDocs = ["order-only.md"], SourceDiscrepancies = ["d3"] },
        ],
        Drift =
        [
            new CrossArtifactDriftCandidate { Id = "d1", Family = "Routes", StructuredKey = "PUT /barn", Explanation = "Route named in dataflow.md", LeftEvidence = [new DriftEvidenceRef("Generated", "dataflow", "Person/autodoc/dataflow.md")] },
            new CrossArtifactDriftCandidate { Id = "d2", Family = "Routes", StructuredKey = "GET /readme", Explanation = "Route named in README.md", LeftEvidence = [new DriftEvidenceRef("Generated", "readme", "Person/autodoc/README.md")] },
            new CrossArtifactDriftCandidate { Id = "d3", Family = "Entities", StructuredKey = "Order", Explanation = "Order entity" },
        ],
    };

    [Fact]
    public void Provenance_shows_only_the_health_findings_of_this_document_and_its_module()
    {
        _api.Setup(a => a.RunAsync(It.Is<GeneratedDocumentationDiagnosticRequest>(r => r.ProjectImportId == "i1" && r.SnapshotId == Snapshot1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(HealthRun());
        var cut = Open("Person", "dataflow.md", "provenance");

        cut.WaitForAssertion(() => cut.Find("[data-testid=gde-health]").GetAttribute("data-state").Should().Be("evaluated"));
        string Count(string kind) => cut.FindAll("[data-testid=gde-health-count]").Single(c => c.GetAttribute("data-kind") == kind).QuerySelector("strong")!.TextContent;
        Count("parse").Should().Be("1");
        Count("missing").Should().Be("1");
        Count("source").Should().Be("1", "the README candidate names another document");
        Count("drift").Should().Be("0");
        cut.Find("[data-testid=gde-health-summary]").TextContent.Should().Contain("3 findings").And.NotContain("healthy");
        var detail = cut.Find("[data-testid=gde-health-detail]");
        detail.QuerySelector("[data-testid=gde-health-parse]")!.TextContent.Should().Be("Unclosed fence at line 9");
        detail.QuerySelector("[data-testid=gde-health-missing]")!.TextContent.Should().Be("messaging.md");
        detail.QuerySelectorAll("[data-testid=gde-health-candidate]").Select(c => c.GetAttribute("data-id")).Should().Equal("d1");
        detail.TextContent.Should().NotContain("order-only.md").And.NotContain("Order entity");
        cut.Find("[data-testid=gde-health-open]").GetAttribute("href").Should().Be($"generated-documentation-health?import=i1&snapshot={Snapshot1}");

        Tab(cut, "Read");
        Tab(cut, "Provenance");
        _api.Verify(a => a.RunAsync(It.IsAny<GeneratedDocumentationDiagnosticRequest>(), It.IsAny<CancellationToken>()), Times.Once, "the existing run is read once per import + snapshot");
    }

    [Fact]
    public void Without_a_health_run_the_document_is_not_evaluated_never_healthy()
    {
        _api.Setup(a => a.RunAsync(It.IsAny<GeneratedDocumentationDiagnosticRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((GeneratedDocumentationDiagnosticRun?)null);
        var cut = Open("Person", "dataflow.md", "provenance");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gde-health-not-evaluated]").TextContent.Should().StartWith("Health not evaluated for this document."));
        cut.Find("[data-testid=gde-health]").TextContent.Should().NotContain("healthy");
        cut.Find("[data-testid=gde-health-open]");
    }

    [Fact]
    public void A_run_that_does_not_cover_the_module_is_not_evaluated_and_health_findings_match_paths_across_archive_roots()
    {
        _api.Setup(a => a.RunAsync(It.IsAny<GeneratedDocumentationDiagnosticRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeneratedDocumentationDiagnosticRun { State = GeneratedDocumentationRunState.Ready, Modules = [new GeneratedDocumentationHealthResult { Module = "Billing", RootPath = "Billing" }] });
        var cut = Open("Person", "dataflow.md", "provenance");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gde-health-not-evaluated]").TextContent.Should().Contain("does not cover"));

        GeneratedDocumentationExplorerContext.SamePath("M2LB/Person/autodoc/dataflow.md", "Person/autodoc/dataflow.md").Should().BeTrue();
        GeneratedDocumentationExplorerContext.SamePath("Person/autodoc/dataflow.md", "Person/autodoc/flow.md").Should().BeFalse();
        GeneratedDocumentationExplorerContext.SamePath("Person/autodoc/xdataflow.md", "dataflow.md").Should().BeFalse();
    }

    private static void Tab(IRenderedComponent<ExplorerPage> cut, string tab) => cut.Find($"[data-testid=gde-tab][data-tab={tab}]").Click();
}
