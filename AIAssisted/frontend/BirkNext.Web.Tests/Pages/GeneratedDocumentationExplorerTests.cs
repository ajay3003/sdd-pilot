using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Page = BirkNext.Web.Pages.GeneratedDocumentationExplorer;
using Gde = BirkNext.Web.Services.GeneratedDocumentationExplorer;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Generated Documentation Explorer: structured, read-only browsing of an import's generated (implementation-derived) documentation, by module
/// and document role. Content is resolved by fingerprint from the existing store (no new reader or copy); Data Model reuses the existing parser;
/// absent content is a neutral state; generated docs are never presented as source, runtime or requirement truth. M2LB-style paths, generic content.
/// </summary>
public sealed class GeneratedDocumentationExplorerTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private static readonly DataModelAnalysisService Parser = new();

    private const string DataModel = "# Person data model\n\n## Entities\n\n### Person\n\n| Field | Type | Nullable |\n|---|---|---|\n| Id | Guid | No |\n| Navn | string | Yes |\n\n### BarnType\n\n| Field | Type | Nullable |\n|---|---|---|\n| Id | Guid | No |\n";
    private const string Architecture = "# Architecture\n\n## Overview\n\nAutorisasjon evaluates access.\n\n## Components\n\n- Autorisasjon.Api — HTTP API\n- Cache worker\n\n## Azure resources\n\n| Resource | Kind |\n|---|---|\n| sb-dev | Service Bus |\n";
    private const string Dataflow = "# Data flow\n\n## Access Evaluation\n\n1. Request received\n2. Rules evaluated\n3. Decision returned\n\n## SCIM User Provisioning\n\n1. SCIM request received\n2. User validated\n3. Entity created or updated\n";
    private const string MessageBus = "# Message bus\n\n## Messages\n\n| Message | Producer | Consumer |\n|---|---|---|\n| HendelseOpprettet | Hendelse.Api | Not stated |\n\n## Queues\n\n- hendelse-queue\n";
    private const string Readme = "# Person\n\n## Solution Structure\n\n- Person.Api\n\n## Running Locally\n\n```bash\ndotnet run\n```\n";
    private const string Changelog = "# Changelog\n\n## [1.2.0] - 2026-10-01\n\n### Added\n\n- BarnType entity\n\n### Fixed\n\n- Null check\n\n## [1.1.0] - 2026-09-01\n\n### Changed\n\n- Renamed field\n";
    private const string Unknown = "# Notes\n\nFree text without structure.\n\n## Odd section\n\nSomething.\n";

    public GeneratedDocumentationExplorerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "i1", ProjectName = "M2LB", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "M2LB.zip", ImportedAt = DateTimeOffset.UnixEpoch, SourceSnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555") });
        state.CurrentProjectImportId = "i1";
        var set = new SddGeneratedDocumentationSet { SetId = "set-i1", ProjectImportId = "i1", SourceSnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555"), State = "Present" };
        state.GeneratedDocumentationSets.Add(set);
    }

    private void AddContent(string module, string file, string kind, string text, bool captured, bool root)
    {
        var state = _repository.SddLifecycle;
        var fingerprint = $"fp-{module}-{file}";
        var path = root ? $"docs/{file}" : $"{module}/autodoc/{file}";
        state.GeneratedDocumentationSets[0].Artifacts.Add(new SddGeneratedDocumentArtifact
        {
            ModuleId = root ? "repository" : "module:" + module.ToLowerInvariant(), ModuleRoot = root ? "" : module, RelativePath = path, Kind = kind, Fingerprint = fingerprint,
            Generator = root ? null : "maintain-docs", ContentCaptured = captured,
        });
        if (captured) state.GeneratedDocumentContents[fingerprint] = text;
    }

    private void Seed()
    {
        AddContent("Person", "data-model.md", "DataModel", DataModel, true, false);
        AddContent("Person", "README.md", "Overview", Readme, true, false);
        AddContent("Person", "CHANGELOG.md", "Changelog", Changelog, true, false);
        AddContent("Person", "notes.md", "OtherGeneratedDocumentation", Unknown, true, false);
        AddContent("Autorisasjon", "architecture.md", "Architecture", Architecture, true, false);
        AddContent("Autorisasjon", "dataflow.md", "DataFlow", Dataflow, true, false);
        AddContent("Hendelse", "message-bus.md", "Messaging", MessageBus, true, false);
        AddContent("Hendelse", "messaging.md", "Messaging", MessageBus + "\n## Topics\n\n- hendelse-topic\n", true, false);
        AddContent("Hendelse", "eventhub-routing.md", "Messaging", "# Event Hub routing\n\n- x\n", false, false);
        AddContent("", "architecture.md", "Architecture", Architecture, true, true);
    }

    private IReadOnlyList<GeneratedExplorerDocument> Docs() { Seed(); return Gde.Documents(DocumentationDrift.Catalog(_repository.SddLifecycle), "i1"); }
    private GeneratedDocumentView View(IReadOnlyList<GeneratedExplorerDocument> docs, string path) =>
        Gde.View(docs.Single(d => d.Path == path), DocumentationDrift.Text(_repository.SddLifecycle, docs.Single(d => d.Path == path).Document), Parser);

    // 1, 15. Each module once; repository-level docs kept apart, never assigned to a module.
    [Fact]
    public void Modules_are_listed_once_and_repository_level_is_separate()
    {
        var docs = Docs();
        Gde.Modules(docs).Should().Equal("Autorisasjon", "Hendelse", "Person", Gde.RepositoryLevel);
        docs.Single(d => d.Path == "docs/architecture.md").RepositoryLevel.Should().BeTrue();
        docs.Where(d => d.Module == "Person").Should().NotContain(d => d.Path.StartsWith("docs/"));
    }

    // 2, 14. Only available roles; two messaging docs both selectable.
    [Fact]
    public void Only_available_roles_are_shown_and_same_role_documents_stay_separate()
    {
        var docs = Docs();
        docs.Where(d => d.Module == "Person").Select(d => d.Role).Should().BeEquivalentTo([GeneratedDocumentRole.DataModel, GeneratedDocumentRole.Overview, GeneratedDocumentRole.ChangeLog, GeneratedDocumentRole.Other]);
        docs.Where(d => d.Module == "Hendelse" && d.Role == GeneratedDocumentRole.Messaging).Should().HaveCount(2, "no arbitrary winner");
        docs.Single(d => d.Path.EndsWith("eventhub-routing.md")).RoleLabel.Should().Be("Messaging / Event Routing");
    }

    // 3, 4, 19. Content by fingerprint; uncaptured content is neutral and safe.
    [Fact]
    public void Content_resolves_by_fingerprint_and_uncaptured_content_is_neutral()
    {
        var docs = Docs();
        View(docs, "Person/autodoc/README.md").Content.Should().Be(Readme, "the exact captured Markdown");
        var routing = View(docs, "Hendelse/autodoc/eventhub-routing.md");
        routing.State.Should().Be(GeneratedDocumentParseState.ContentUnavailable);
        routing.StateLabel.Should().Be("Content unavailable").And.NotContainAny("Failed", "Missing");
        Gde.ContentUnavailableText.Should().Contain("not captured during Project Import");
    }

    // 5, 6. Data Model reuses the existing parser; omitted attributes are Not stated, never Missing.
    [Fact]
    public void Data_model_reuses_the_parser_and_generated_semantics()
    {
        var view = View(Docs(), "Person/autodoc/data-model.md");
        view.DataModel!.Entities.Select(e => e.Name).Should().Contain(["Person", "BarnType"]);
        view.DataModel.IsGeneratedDocumentation.Should().BeTrue("DataModelGeneratedSemantics.Apply ran");
        view.StructuredItems.Should().Be(2);
    }

    // 7, 8, 9. Architecture sections, data flows with ordered steps, messaging tables.
    [Fact]
    public void Architecture_dataflow_and_messaging_get_deterministic_structure()
    {
        var docs = Docs();
        View(docs, "Autorisasjon/autodoc/architecture.md").Sections.Select(s => s.Heading).Should().Contain(["Overview", "Components", "Azure resources"]);
        var flows = View(docs, "Autorisasjon/autodoc/dataflow.md").Sections.Where(s => s.OrderedSteps.Count > 0).ToList();
        flows.Select(f => f.Heading).Should().Equal("Access Evaluation", "SCIM User Provisioning");
        flows[1].OrderedSteps.Should().Equal("SCIM request received", "User validated", "Entity created or updated");
        var messaging = View(docs, "Hendelse/autodoc/message-bus.md");
        messaging.Sections.Single(s => s.Heading == "Messages").Tables.Single().Rows.Single().Should().Equal("HendelseOpprettet", "Hendelse.Api", "Not stated");
    }

    // 10, 11, 12. README as sections (not a Specification), changelog versions/categories, generic fallback.
    [Fact]
    public void Readme_changelog_and_unknown_documents_are_structured_without_forcing_semantics()
    {
        var docs = Docs();
        var readme = View(docs, "Person/autodoc/README.md");
        readme.Document.Role.Should().Be(GeneratedDocumentRole.Overview);
        readme.Sections.Select(s => s.Heading).Should().Contain(["Solution Structure", "Running Locally"]);
        var changelog = View(docs, "Person/autodoc/CHANGELOG.md");
        changelog.ChangeLog.Select(v => v.Version).Should().Equal("1.2.0", "1.1.0");
        changelog.ChangeLog[0].Date.Should().Be("2026-10-01");
        changelog.ChangeLog[0].Groups.Select(g => g.Category).Should().Equal("Added", "Fixed");
        var unknown = View(docs, "Person/autodoc/notes.md");
        unknown.Document.Role.Should().Be(GeneratedDocumentRole.Other);
        unknown.Sections.Should().NotBeEmpty();
        unknown.State.Should().NotBe(GeneratedDocumentParseState.ContentUnavailable, "an unknown document still gets an outline and sections");
    }

    // UI: selection, origin wording, provenance, Drift link context, health link, source read-only, unmapped neutral.
    [Fact]
    public void Page_shows_documents_origin_provenance_and_context_links()
    {
        Seed();
        var cut = Render<Page>();
        cut.Find("[data-testid=gde-module]").Change("Person");
        cut.FindAll("[data-testid=gde-document-card]").Select(c => c.GetAttribute("data-role")).Should().BeEquivalentTo(["DataModel", "Overview", "ChangeLog", "Other"]);
        cut.FindAll("[data-testid=gde-document-card]").Single(c => c.GetAttribute("data-role") == "DataModel").QuerySelector("[data-testid=gde-open]")!.Click();
        cut.Find("[data-testid=gde-origin]").TextContent.Should().Be("Generated · implementation-derived documentation");
        cut.Find("[data-testid=gde-generator]").TextContent.Should().Be("maintain-docs");
        cut.Find("[data-testid=gde-compare-drift]").GetAttribute("href").Should().Be("documentation-drift?tab=compare&import=i1&module=Person&concern=DataModel");
        cut.Find("[data-testid=gde-open-health]").GetAttribute("href").Should().Be($"{NavigationCatalog.GeneratedDocumentationHealthRoute}?import=i1&snapshot=11111111-2222-3333-4444-555555555555", "Health opens on this import and its exact snapshot");
        cut.Markup.Should().NotContainAny("source truth is", "Passed", "Healthy", "verified implementation");
        cut.FindAll("[data-testid=gde-tab]").Single(t => t.GetAttribute("data-tab") == "Structure").Click();
        cut.FindAll("[data-testid=gde-entity]").Should().HaveCount(2);
        cut.FindAll("[data-testid=gde-tab]").Single(t => t.GetAttribute("data-tab") == "Source").Click();
        cut.Find("[data-testid=gde-raw]").TextContent.Should().Be(DataModel);
        cut.FindAll("[data-testid=gde-tab]").Single(t => t.GetAttribute("data-tab") == "Provenance").Click();
        cut.Find("[data-testid=gde-fingerprint]").TextContent.Should().Be("fp-Person-data-model.md");
        cut.Find("[data-testid=gde-search]").Input("BarnType");
        cut.Find("[data-testid=gde-search-results]").TextContent.Should().Contain("BarnType");
    }

    [Fact]
    public void Page_states_content_unavailable_neutrally()
    {
        Seed();
        var cut = Render<Page>();
        cut.Find("[data-testid=gde-module]").Change("Hendelse");
        cut.FindAll("[data-testid=gde-document-card]").Single(c => c.TextContent.Contains("eventhub-routing")).QuerySelector("[data-testid=gde-open]")!.Click();
        cut.FindAll("[data-testid=gde-tab]").Single(t => t.GetAttribute("data-tab") == "Structure").Click();
        cut.Find("[data-testid=gde-content-unavailable]").TextContent.Should().Contain("Detected — content unavailable").And.Contain("not captured during Project Import").And.Contain("nothing is fabricated");
        cut.FindAll("[data-testid=gde-compare-drift]").Should().BeEmpty("Drift has no authored counterpart for messaging documents");
    }

    // 18. Feature visibility: a stable key in Source Review, enabled by default.
    [Fact]
    public void Feature_is_a_source_review_feature_enabled_by_default()
    {
        FeatureVisibilityTaxonomy.Categories.Single(c => c.Id == "source-review").Keys.Should().Contain("GeneratedDocumentationExplorer");
        new FeatureVisibilityDto().GeneratedDocumentationExplorer.Should().BeTrue();
        NavigationCatalog.GeneratedDocumentationExplorerRoute.Should().Be("generated-documentation-explorer");
    }
}
