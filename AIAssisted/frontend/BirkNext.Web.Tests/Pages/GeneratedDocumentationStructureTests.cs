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
        GeneratedDiagrams.Read("erDiagram\n  A ||--o{ B : has")!.SourceType.Should().Be("erDiagram", "ER diagrams are read as relationships only");
        GeneratedDiagrams.IsDiagram("gantt\n  title Plan").Should().BeTrue();
        GeneratedDiagrams.Read("gantt\n  title Plan").Should().BeNull("unsupported type → preview unavailable, source kept");
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
        graphql.GetAttribute("data-state").Should().Be("unavailable", "the card itself opens the document's details; no separate Open/Details button");
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
        diagram.QuerySelectorAll("[data-testid=gdv-source]").Should().BeEmpty("raw Mermaid is not the primary view");

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
    // ── Read (default), drawn diagrams, safe rendering, outline, deep links ────────────────────────────────────────

    [Fact]
    public void Opening_a_document_reads_it_with_its_diagram_drawn_from_its_own_source()
    {
        var cut = Open("dataflow.md");

        cut.Find("[data-testid=gde-tab][aria-selected=true]").GetAttribute("data-tab").Should().Be("Read", "opening a document reads it; metadata is under Provenance");
        cut.FindAll("[data-testid=gde-tab]").Select(t => t.TextContent).Should().Equal("Read", "Outline", "Source", "Provenance");
        var read = cut.Find("[data-testid=gde-read]");
        read.QuerySelector("h2")!.TextContent.Should().Be("REST Ingestion Flow");
        read.TextContent.Should().NotContain("sequenceDiagram", "the diagram is drawn, its source is one click away");
        var diagram = cut.Find("[data-testid=gde-diagram]");
        diagram.GetAttribute("data-kind").Should().Be("Sequence");
        diagram.GetAttribute("aria-label").Should().Be("REST Ingestion Flow sequence diagram");
        cut.FindAll("[data-testid=gdv-svg] [data-testid=gdv-node]").Should().HaveCount(3, "one box per declared participant");
        cut.FindAll("[data-testid=gdv-label]").Select(l => l.QuerySelector("title")!.TextContent).Should().StartWith(["1. PersonAdapter → API: PUT /api/person/v1/innmating/barn"]);
        cut.Find("figcaption").TextContent.Should().Contain("not verified runtime architecture");

        diagram.QuerySelectorAll("[data-testid=gdv-mode]").Single(b => b.GetAttribute("data-mode") == "source").Click();
        cut.Find("[data-testid=gdv-source]").TextContent.Should().Contain("sequenceDiagram").And.Contain("PA->>API");
    }

    [Fact]
    public void Zoom_and_fit_change_only_the_view()
    {
        var cut = Open("dataflow.md");
        cut.Find("[data-testid=gdv-zoom-value]").TextContent.Should().Be("Fit");
        cut.Find("[data-testid=gdv-zoom-in]").Click();
        cut.Find("[data-testid=gdv-zoom-value]").TextContent.Should().Be("125%");
        cut.Find("[data-testid=gdv-svg]").GetAttribute("width").Should().NotBe("100%");
        cut.Find("[data-testid=gdv-fit]").Click();
        cut.Find("[data-testid=gdv-svg]").GetAttribute("width").Should().Be("100%");
    }

    [Fact]
    public void Imported_markdown_is_rendered_without_executable_html_or_remote_images()
    {
        Add("README.md", "Overview", "# Overview\n\n<script>alert('x')</script>\n\n<img src=\"x\" onerror=\"alert(1)\">\n\n![logo](https://example.test/logo.png)\n\n[bad](javascript:alert(1)) and [ok](https://example.test)\n\n| A | B |\n|---|---|\n| 1 | 2 |\n", true);
        var cut = Open("README.md");

        var read = cut.Find("[data-testid=gde-read]");
        read.QuerySelectorAll("script, img").Should().BeEmpty("raw HTML is text and remote images are not loaded");
        read.TextContent.Should().Contain("<script>", "the raw HTML stays visible as text — nothing is silently dropped");
        read.TextContent.Should().Contain("[image not loaded: logo]");
        read.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).Should().NotContain(h => h != null && h.StartsWith("javascript", StringComparison.OrdinalIgnoreCase));
        read.QuerySelector("table td")!.TextContent.Should().Be("1");
    }

    [Fact]
    public void The_outline_lists_the_headings_and_opens_the_section_in_Read()
    {
        var cut = Open("CHANGELOG.md");
        Tab(cut, "Structure");

        var items = cut.FindAll("[data-testid=gde-outline-item]");
        items.Select(i => i.TextContent).Should().Equal("Changelog", "[1.2.0] - 2026-10-01", "Added", "[1.1.0] - 2026-09-01", "Fixed");
        cut.Find("[data-testid=gde-outline-filter]").Input("1.1");
        cut.FindAll("[data-testid=gde-outline-item]").Should().ContainSingle();
        cut.Find("[data-testid=gde-structured]").TextContent.Should().Contain("latest 1.2.0", "the structured release reading stays in Outline");
        var anchor = cut.Find("[data-testid=gde-outline-item]").GetAttribute("data-anchor")!;
        cut.Find("[data-testid=gde-outline-item]").Click();

        cut.Find("[data-testid=gde-tab][aria-selected=true]").GetAttribute("data-tab").Should().Be("Read");
        cut.Find($"[data-testid=gde-read] #{anchor}").TextContent.Should().Contain("1.1.0");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == anchor);
    }

    [Theory]
    [InlineData("source", "Source")]
    [InlineData("provenance", "Provenance")]
    [InlineData("overview", "Provenance")]
    [InlineData("structure", "Structure")]
    [InlineData("nonsense", "Read")]
    public void An_explicit_view_deep_link_is_honoured_and_unknown_views_read(string view, string expected)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"generated-documentation-explorer?import=i1&module=Person&document={Uri.EscapeDataString("Person/autodoc/dataflow.md")}&view={view}");
        var cut = Render<ExplorerPage>();
        cut.Find("[data-testid=gde-tab][aria-selected=true]").GetAttribute("data-tab").Should().Be(expected);
    }

    [Fact]
    public void Provenance_holds_the_former_overview_facts_and_selecting_another_document_never_shows_stale_content()
    {
        var cut = Open("dataflow.md");
        Tab(cut, "Provenance");
        cut.Find("[data-testid=gde-overview]").TextContent.Should().Contain("Headings");
        cut.Find("[data-testid=gde-provenance]").TextContent.Should().Contain("fp-dataflow.md");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("view=provenance");

        cut.FindAll("[data-testid=gde-document-card]").Single(c => c.TextContent.Contains("CHANGELOG.md")).QuerySelector("[data-testid=gde-open]")!.Click();
        cut.Find("[data-testid=gde-tab][aria-selected=true]").GetAttribute("data-tab").Should().Be("Read");
        cut.Find("[data-testid=gde-read]").TextContent.Should().Contain("1.2.0").And.NotContain("REST Ingestion Flow");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain(Uri.EscapeDataString("Person/autodoc/CHANGELOG.md")).And.NotContain("view=");
    }

    [Fact]
    public void Read_segments_keep_every_line_in_order_and_only_diagram_fences_become_diagrams()
    {
        var doc = $"# T\n\nIntro\n\n```bash\ndotnet run\n```\n\n```mermaid\n{Graph}\n```\n\nAfter\n```mermaid\nnot a diagram\n```\n";
        var segments = BirkNext.Web.Services.GeneratedDocumentationExplorer.ReadSegments(doc);
        segments.Select(s => s.Diagram is null ? "md" : "diagram").Should().Equal("md", "diagram", "md");
        segments[0].Markdown.Should().Contain("dotnet run", "an ordinary code fence stays in the Markdown");
        segments[2].Markdown.Should().Contain("After").And.Contain("not a diagram");
        var layout = DiagramLayout.Of(GeneratedDiagrams.Read(segments[1].Diagram!)!);
        layout.Boxes.Select(b => b.Full).Should().Contain(["Web app", "M2LB.Person.Api", "PersonDb", "Service Bus"]);
        layout.Lines.Count(l => l.Arrow).Should().Be(3, "one arrow per declared edge — nothing invented");
    }

    // ── reader: library, search, sections, diagram viewer, evidence ────────────────────────────────────────────────

    [Fact]
    public void The_library_titles_documents_by_their_own_heading_and_filters_by_type_and_text()
    {
        var cut = Open("dataflow.md");
        cut.FindAll("[data-testid=gde-card-title]").Select(t => t.TextContent).Should().Contain(["Data flow", "Changelog"]);
        cut.Find("#gde-doc-h").TextContent.Should().StartWith("Data flow");
        cut.Find("[data-testid=gde-doc-type]").Change("ChangeLog");
        cut.FindAll("[data-testid=gde-document-card]").Select(c => c.GetAttribute("data-role")).Should().Equal("ChangeLog");
        cut.Find("[data-testid=gde-doc-type]").Change("");
        cut.Find("[data-testid=gde-doc-search]").Input("openapi");
        cut.FindAll("[data-testid=gde-document-card]").Select(c => c.GetAttribute("data-role")).Should().Equal("ApiContract");
        cut.Find("[data-testid=gde-doc-search]").Input("nothing like this");
        cut.Find("[data-testid=gde-doc-none]");
    }

    [Fact]
    public void Search_highlights_matches_in_Read_counts_them_and_steps_through_them()
    {
        var cut = Open("dataflow.md");
        cut.Find("[data-testid=gde-search]").Input("flow");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gde-search-count]").TextContent.Should().Be("1 of 2"));
        cut.FindAll("[data-testid=gde-read] mark.gde-hit").Should().HaveCount(2, "the H1 'Data flow' and the H2 'REST Ingestion Flow'");
        cut.Find("[data-testid=gde-search-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gde-search-count]").TextContent.Should().Be("2 of 2"));
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "gde-hit-1");
        Tab(cut, "Source");
        cut.FindAll("[data-testid=gde-source-lines] li[data-hit=true]").Should().HaveCount(2);
        cut.Find("[data-testid=gde-search-clear]").Click();
        cut.FindAll("[data-testid=gde-search-count]").Should().BeEmpty();
    }

    [Fact]
    public void A_section_deep_link_opens_Read_at_that_heading_and_the_outline_marks_it()
    {
        var anchor = BirkNext.Web.Services.GeneratedDocumentationExplorer.Outline(BirkNext.Web.Services.GeneratedDocumentationExplorer.ReadSegments(
            $"# Data flow\n\n## REST Ingestion Flow\n\n```mermaid\n{Sequence}\n```\n")).Single(o => o.Text == "REST Ingestion Flow").AnchorId;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"generated-documentation-explorer?import=i1&module=Person&document={Uri.EscapeDataString("Person/autodoc/dataflow.md")}&section={anchor}");
        var cut = Render<ExplorerPage>();
        cut.Find("[data-testid=gde-read]");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == anchor);
        Tab(cut, "Structure");
        cut.Find($"[data-testid=gde-outline-item][data-anchor='{anchor}']").GetAttribute("aria-current").Should().Be("location");
    }

    [Fact]
    public void A_diagram_zooms_and_pans_by_keyboard_resets_and_opens_in_an_expanded_dialog_that_Escape_closes()
    {
        var cut = Open("dataflow.md");
        var canvas = cut.Find("[data-testid=gde-read] [data-testid=gdv-canvas]");
        canvas.ClassList.Should().Contain("is-fit");
        canvas.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "+" });
        cut.Find("[data-testid=gdv-zoom-value]").TextContent.Should().Be("125%");
        cut.Find("[data-testid=gdv-canvas]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find("[data-testid=gdv-svg]").GetAttribute("style").Should().Contain("translate(-60px,0px)");
        cut.Find("[data-testid=gdv-reset]").Click();
        cut.Find("[data-testid=gdv-zoom-value]").TextContent.Should().Be("100%");
        cut.Find("[data-testid=gdv-caption]").TextContent.Should().Contain("Sequence diagram").And.Contain("REST Ingestion Flow").And.Contain("not verified runtime architecture");

        cut.Find("[data-testid=gdv-expand]").Click();
        var dialog = cut.Find("[data-testid=gdv-dialog]");
        dialog.GetAttribute("role").Should().Be("dialog");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        dialog.QuerySelector("[data-testid=gdv-svg]").Should().NotBeNull();
        dialog.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.FindAll("[data-testid=gdv-dialog]").Should().BeEmpty();
        cut.Find("[data-testid=gde-read] [data-testid=gdv-canvas]");
    }

    [Fact]
    public void Highlighting_marks_text_only_and_never_injects_markup()
    {
        var html = "<p>Flow and <a href=\"flow.md\">flow</a> &lt;script&gt;</p>";
        var marked = BirkNext.Web.Services.GeneratedDocumentationReader.Highlight(html, "flow", 0, out var count);
        count.Should().Be(2);
        marked.Should().Contain("href=\"flow.md\"", "attributes are untouched");
        BirkNext.Web.Services.GeneratedDocumentationReader.Highlight(html, "<script>", 0, out var scripts).Should().NotContain("<script>");
        scripts.Should().Be(1, "the encoded text is found, and the result stays encoded");
        BirkNext.Web.Services.GeneratedDocumentationReader.Highlight(html, "f", 0, out var tooShort).Should().Be(html);
        tooShort.Should().Be(0);
    }

    [Fact]
    public void Only_exact_registered_names_become_system_links_and_outline_comparison_reports_structure_only()
    {
        BirkNext.Scopes.SystemOverviewItem S(string key, string name, params string[] aliases) => new()
        {
            Key = key, Name = name, Aliases = [.. aliases.Select(a => new BirkNext.Scopes.SystemAliasInfo(a, BirkNext.Interpretation.AliasState.Explicit, "profile"))],
        };
        var systems = new[] { S("Service|m2lb.person.api", "M2LB.Person.Api"), S("System|person", "Person"), S("System|mu", "MU", "Meldingsutveksleren") };
        var mentions = BirkNext.Web.Services.GeneratedDocumentationReader.SystemMentions("M2LB.Person.Api publishes to the bus. M2LB.PersonBiRKAdapter.Worker reads it. Meldingsutveksleren forwards.", systems);
        mentions.Select(m => (m.System.Key, m.Mention)).Should().Equal(("Service|m2lb.person.api", "M2LB.Person.Api"), ("System|mu", "Meldingsutveksleren"));

        var diff = BirkNext.Web.Services.GeneratedDocumentationReader.CompareOutline(
            "# A\n\n## Old\n\n```mermaid\ngraph TB\n  A --> B\n```\n", "# A\n\n## New\n\n```mermaid\ngraph TB\n    A --> B\n```\n");
        diff.Added.Should().Equal("New");
        diff.Removed.Should().Equal("Old");
        diff.DiagramsChanged.Should().BeFalse("indentation is not a change of the diagram");
    }

    [Fact]
    public void Provenance_leads_with_the_evidence_kind_and_keeps_identifiers_under_technical_details()
    {
        var cut = Open("dataflow.md");
        Tab(cut, "Provenance");
        cut.Find("[data-testid=gde-evidence-kind]").TextContent.Should().Contain("Implementation-derived").And.Contain("not runtime-verified");
        cut.Find("[data-testid=gde-technical] [data-testid=gde-fingerprint]").TextContent.Should().Be("fp-dataflow.md");
        cut.Find("[data-testid=gde-revisions-none]");
        cut.Find("[data-testid=gde-systems-unavailable]").TextContent.Should().Contain("not available");
    }
}
