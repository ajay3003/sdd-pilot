using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Documentation Drift in the browser: the comparison mode decides the selectors (version within one project, authored ↔ generated within one
/// import, manual for anything else); imported projects are listed, never chained into revisions; Changes walks one document within its
/// project; Drift separates equivalent-scope pairs from related documentation. Generic fixtures only.
/// </summary>
public sealed class DocumentationDriftPanelTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private static readonly Guid Snapshot = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public DocumentationDriftPanelTests()
    {
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-a", ProjectName = "cart", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "cart-r16.zip", ImportedAt = DateTimeOffset.UnixEpoch, SourceSnapshotId = Snapshot });
        state.Revisions.Add(new SddArtifactRevision { Role = "Specification", FileName = "cart/specs/001-cart/spec.md", SourceReference = "cart/specs/001-cart/spec.md", Content = "# Spec\n\n## Requirements\n\n- FR-001 store carts\n", Fingerprint = "AAA111", Revision = 1, WorkspaceScope = "import:import-a", ProjectImportId = "import-a" });
        state.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet
        {
            SetId = "genset-import-a", ProjectImportId = "import-a", SourceSnapshotId = Snapshot, State = "Present",
            Artifacts = [new SddGeneratedDocumentArtifact { ModuleId = "module:cart", ModuleRoot = "cart/src/Cart.Api", RelativePath = "cart/src/Cart.Api/autodoc/architecture.md", Kind = "Architecture", Fingerprint = "bbb222", Generator = "maintain-docs", ContentCaptured = true }],
        });
        state.GeneratedDocumentContents["bbb222"] = "# Architecture\n\n## Requirements\n\n- Cart API serves HTTP\n";
        state.CurrentProjectImportId = "import-a";
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>(s => s.SaveNowAsync() == Task.FromResult(true)));
    }

    private IReadOnlyList<DriftDocument> Catalog() => DocumentationDrift.Catalog(_repository.SddLifecycle);

    /// <summary>A second import of the same project "cart" with a changed spec and no generated documentation.</summary>
    private void SecondImportOfCart()
    {
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-b", ProjectName = "cart", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "cart-r17.zip", ImportedAt = DateTimeOffset.UnixEpoch.AddDays(1) });
        state.Revisions.Add(new SddArtifactRevision { Role = "Specification", FileName = "cart/specs/001-cart/spec.md", SourceReference = "cart/specs/001-cart/spec.md", Content = "# Spec\n\n## Requirements\n\n- FR-001 store carts\n- FR-002 expire carts\n", Fingerprint = "CCC333", Revision = 2, WorkspaceScope = "import:import-b", ProjectImportId = "import-b" });
        state.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet { SetId = "genset-import-b", ProjectImportId = "import-b", State = "NotPresent" });
    }

    /// <summary>An unrelated project imported later, with the same relative spec path.</summary>
    private void UnrelatedImport()
    {
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-t", ProjectName = "terraform", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "terraform.zip", ImportedAt = DateTimeOffset.UnixEpoch.AddDays(2) });
        state.Revisions.Add(new SddArtifactRevision { Role = "Specification", FileName = "terraform/specs/001-cart/spec.md", SourceReference = "terraform/specs/001-cart/spec.md", Content = "# Other\n", Fingerprint = "DDD444", Revision = 1, WorkspaceScope = "import:import-t", ProjectImportId = "import-t" });
    }

    [Fact]
    public async Task Consistency_mode_compares_authored_and_generated_of_one_import_neutrally_and_saves_exact_identities()
    {
        var cut = Render<DocumentationDriftPanel>();
        cut.Find("[data-testid=dd-mode] option[selected]").GetAttribute("value").Should().Be(nameof(DriftIntent.ConsistencyDrift), "no same-project revision exists yet");
        cut.Find("[data-testid=dd-run]").HasAttribute("disabled").Should().BeTrue();
        var authored = Catalog().Single(d => d.Origin == DriftOrigin.Authored);
        var generated = Catalog().Single(d => d.Origin == DriftOrigin.Generated);

        cut.Find("[data-testid=dd-left-document]").Change(authored.Id);
        cut.Find("[data-testid=dd-right-document]").Change(generated.Id);
        cut.Find("[data-testid=dd-left-project-revision]").TextContent.Should().Be("cart · Import 1");
        cut.Find("[data-testid=dd-left-imported-source]").TextContent.Should().Be("cart-r16.zip");
        cut.Find("[data-testid=dd-pairing-relationship]").TextContent.Should().Be("Manual relationship — not a deterministic pair");
        cut.Find("[data-testid=dd-revision-context]").TextContent.Should().StartWith(DocumentationDrift.SameRevision);

        cut.Find("[data-testid=dd-run]").Click();
        cut.Find("[data-testid=dd-result]").GetAttribute("data-intent").Should().Be(nameof(DriftIntent.ConsistencyDrift));
        cut.Find("[data-testid=dd-neutral]").TextContent.Should().Contain("neither side is automatically authoritative");
        cut.Find("[data-testid=dd-result]").TextContent.Should().NotContainAny("defect", "is wrong", "Passed", "Failed");

        await cut.Find("[data-testid=dd-save]").ClickAsync(new());
        var saved = _repository.SddLifecycle.DocumentationDriftComparisons.Should().ContainSingle().Subject;
        (saved.Left.RevisionId, saved.Left.Fingerprint, saved.Left.LineageKey).Should().Be((authored.RevisionId, "AAA111", "project:cart"));
        saved.Right.GeneratedSetId.Should().Be("genset-import-a");
        saved.Intent.Should().Be(nameof(DriftIntent.ConsistencyDrift));
        _repository.SddLifecycle.Revisions.Should().ContainSingle("comparing never edits or adds artifact revisions");
    }

    [Fact]
    public void Version_mode_offers_only_same_project_revisions()
    {
        SecondImportOfCart();
        UnrelatedImport();
        var cut = Render<DocumentationDriftPanel>();
        cut.Find("[data-testid=dd-mode] option[selected]").GetAttribute("value").Should().Be(nameof(DriftIntent.VersionDrift));
        var identity = DocumentationDrift.Identities(Catalog()).Single(i => i.Versions.Count > 1);
        cut.FindAll("[data-testid=dd-version-identity] option").Should().HaveCount(2, "the terraform spec is not a revision of cart's spec");
        cut.Find("[data-testid=dd-version-identity]").Change(identity.LogicalKey);
        cut.FindAll("[data-testid=dd-version-from] option").Select(o => o.TextContent).Should().OnlyContain(t => t.StartsWith("cart · Import"));
        cut.Find("[data-testid=dd-run]").Click();
        cut.Find("[data-testid=dd-counts]").TextContent.Should().Contain("Changed between cart · Import 1 and cart · Import 2");
        cut.FindAll("[data-testid=dd-neutral]").Should().BeEmpty();
    }

    [Fact]
    public void Manual_mode_allows_any_pair_labelled_manual_and_refuses_the_same_document()
    {
        UnrelatedImport();
        var cut = Render<DocumentationDriftPanel>();
        cut.Find("[data-testid=dd-mode]").Change(nameof(DriftIntent.Manual));
        var cart = Catalog().Single(d => d.ProjectImportId == "import-a" && d.Origin == DriftOrigin.Authored);
        var terraform = Catalog().Single(d => d.ProjectImportId == "import-t");
        cut.Find("[data-testid=dd-left-search]").GetAttribute("role").Should().Be("combobox");
        cut.Find("[data-testid=dd-left-search]").GetAttribute("list").Should().Be("dd-document-options");
        cut.FindAll("#dd-document-options option").Should().HaveCount(Catalog().Count(d => d.Supported), "every document is searchable by type, path and import");
        cut.Find("[data-testid=dd-left-search]").Change(DocumentationDriftPresentation.SearchText(cart));
        cut.Find("[data-testid=dd-right-search]").Change(DocumentationDriftPresentation.SearchText(cart));
        cut.Find("[data-testid=dd-guard]").TextContent.Should().Be("Select two different document versions or artifacts.");
        cut.Find("[data-testid=dd-run]").HasAttribute("disabled").Should().BeTrue();

        cut.Find("[data-testid=dd-right-search]").Change(DocumentationDriftPresentation.SearchText(terraform));
        cut.Find("[data-testid=dd-revision-context]").TextContent.Should().StartWith(DocumentationDrift.UnrelatedImports);
        cut.Find("[data-testid=dd-manual-note]").TextContent.Should().Contain("not assumed to describe equivalent scope");
        cut.Find("[data-testid=dd-compatibility]").GetAttribute("data-level").Should().Be(nameof(DriftCompatibility.Partial), "both are specifications of different projects");
        cut.Find("[data-testid=dd-run]").TextContent.Should().Be("Compare");
        cut.Find("[data-testid=dd-run]").Click();
        cut.Find("[data-testid=dd-result-manual]").TextContent.Should().Contain("Scope equivalence not assumed").And.Contain("Compatibility: Partial");
        cut.Find("[data-testid=dd-outcome]").TextContent.Should().StartWith("Document differences");
    }

    [Fact]
    public void Advanced_comparison_of_different_types_warns_and_offers_compare_anyway_never_blocks()
    {
        var cut = Render<DocumentationDriftPanel>();
        cut.Find("[data-testid=dd-mode]").Change(nameof(DriftIntent.Manual));
        var spec = Catalog().Single(d => d.Origin == DriftOrigin.Authored);
        var architecture = Catalog().Single(d => d.Origin == DriftOrigin.Generated);
        cut.Find("[data-testid=dd-left-search]").Change(DocumentationDriftPresentation.SearchText(spec));
        cut.Find("[data-testid=dd-right-search]").Change(DocumentationDriftPresentation.SearchText(architecture));
        cut.Find("[data-testid=dd-compatibility]").GetAttribute("data-level").Should().Be(nameof(DriftCompatibility.Low));
        cut.Find("[data-testid=dd-compatibility-warning]").TextContent.Should().Contain("You can still compare them");
        var run = cut.Find("[data-testid=dd-run]");
        run.TextContent.Should().Be("Compare anyway");
        run.HasAttribute("disabled").Should().BeFalse("a low compatibility is a warning, never a hard block");
        run.Click();
        cut.Find("[data-testid=dd-result-manual]").TextContent.Should().Contain("Scope equivalence not assumed").And.Contain("Compatibility: Low");
    }

    [Fact]
    public void Results_are_summary_first_grouped_filterable_and_searchable()
    {
        var cut = Render<DocumentationDriftPanel>();
        cut.Find("[data-testid=dd-left-document]").Change(Catalog().Single(d => d.Origin == DriftOrigin.Authored).Id);
        cut.Find("[data-testid=dd-right-document]").Change(Catalog().Single(d => d.Origin == DriftOrigin.Generated).Id);
        cut.Find("[data-testid=dd-run]").Click();

        var counts = cut.FindAll("[data-testid=dd-status-count]");
        counts.Should().NotBeEmpty();
        counts.Should().OnlyContain(c => c.ClassList.Any(k => k.StartsWith("dd-status-")) && c.TextContent.Contains(" · "), "colour always comes with text");
        cut.FindAll("[data-testid=dd-result-group]").Should().NotBeEmpty();
        var all = cut.FindAll("[data-testid=dd-item]").Count;
        var filters = cut.FindAll("[data-testid=dd-result-filter]");
        filters[0].TextContent.Should().Be($"All ({all})");
        filters[1].Click();
        var status = cut.FindAll("[data-testid=dd-result-filter]")[1].GetAttribute("data-status");
        cut.FindAll("[data-testid=dd-result-filter]")[1].GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("[data-testid=dd-item]").Should().OnlyContain(i => i.GetAttribute("data-status") == status);

        cut.FindAll("[data-testid=dd-result-filter]")[0].Click();
        cut.Find("[data-testid=dd-result-search]").Input("no-such-term-anywhere");
        cut.Find("[data-testid=dd-result-empty]").TextContent.Should().Be("No item matches this filter.");
    }

    [Fact]
    public void Overview_dashboard_summarises_one_import_and_shows_missing_generated_docs_neutrally()
    {
        SecondImportOfCart();
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview"));
        cut.Find("[data-testid=dd-dashboard]").TextContent.Should().Contain("Documentation review status");
        cut.Find("[data-testid=dd-dash-authored]").TextContent.Should().Be("1");
        cut.Find("[data-testid=dd-dash-generated]").TextContent.Should().Be("1 docs · 1 modules");
        cut.FindAll("[data-testid=dd-generated-missing]").Should().BeEmpty();
        cut.Find("[data-testid=dd-overview-all-toggle]").GetAttribute("aria-expanded").Should().Be("false", "the full inventory is secondary");

        cut.Find("[data-testid=dd-overview-import]").Change("import-b");
        cut.Find("[data-testid=dd-dash-generated]").TextContent.Should().Be("Not present");
        cut.Find("[data-testid=dd-generated-missing]").TextContent.Should().Contain("neutral state");
        cut.Find("[data-testid=dd-generated-missing-health]").GetAttribute("href").Should().Be($"{NavigationCatalog.GeneratedDocumentationHealthRoute}?import=import-b");
        cut.Find("[data-testid=dd-generated-missing-explorer]").GetAttribute("href").Should().Be($"{NavigationCatalog.GeneratedDocumentationExplorerRoute}?import=import-b");
        _repository.SddLifecycle.DocumentationDriftComparisons.Should().BeEmpty("the dashboard never compares");
    }

    [Fact]
    public void Changes_and_drift_say_what_they_are()
    {
        Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "changes")).Find("[data-testid=dd-changes-intro]").TextContent.Should().StartWith("Changes = same-lineage revisions.");
        Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "drift")).Find("[data-testid=dd-drift-intro]").TextContent.Should().StartWith("Drift = divergence inventory.");
    }

    [Fact]
    public void Overview_lists_imported_projects_not_a_revision_chain_and_links_generated_documentation_health()
    {
        SecondImportOfCart();
        UnrelatedImport();
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview"));
        cut.Find("[data-testid=dd-sum-imports]").TextContent.Should().Be("3");
        cut.Find("[data-testid=dd-sum-lineages]").TextContent.Should().Be("1", "only cart was imported twice");
        cut.Find("[data-testid=dd-sum-versioned]").TextContent.Should().Be("1");
        var rows = cut.FindAll("[data-testid=dd-revision-row]");
        rows.Select(r => r.GetAttribute("data-import")).Should().Equal("cart · Import 1", "cart · Import 2", "terraform · Import 1");
        rows[1].GetAttribute("data-generated").Should().Be("Not present");
        cut.Markup.Should().NotContain("Project revisions").And.NotContain("R1 → R2").And.NotContain("Generated documentation history");
        cut.Find("[data-testid=dd-gdh-link] a").GetAttribute("href").Should().Be(NavigationCatalog.GeneratedDocumentationHealthRoute);
        cut.Find("[data-testid=dd-lineage-rule]").TextContent.Should().Contain("never treated as revisions of each other");
    }

    [Fact]
    public void A_saved_comparison_reopens_from_overview_as_it_was_computed()
    {
        var catalog = Catalog();
        var left = catalog.Single(d => d.Origin == DriftOrigin.Authored);
        var right = catalog.Single(d => d.Origin == DriftOrigin.Generated);
        DocumentationDrift.Save(_repository.SddLifecycle, left, right, new DriftComparisonResult(DriftIntent.ConsistencyDrift, DocumentationDrift.SameRevision, "stored outcome", [], new Dictionary<string, int>()), null, DateTimeOffset.UnixEpoch);
        string? requested = null;
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview").Add(x => x.OnViewChange, (string v) => requested = v));
        cut.FindAll("[data-testid=dd-history-row]").Should().ContainSingle().Which.TextContent.Should().Contain("cart · Import 1").And.Contain("rev 1");
        cut.Find("[data-testid=dd-history-open]").Click();
        requested.Should().Be("compare");
        cut.Render(p => p.Add(x => x.View, "compare"));
        cut.Find("[data-testid=dd-outcome]").TextContent.Should().Be("stored outcome");
        cut.Find("[data-testid=dd-reopened]").TextContent.Should().Contain("exactly as compared");
    }

    [Fact]
    public void Changes_walks_one_document_within_its_project_only()
    {
        SecondImportOfCart();
        UnrelatedImport();
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "changes"));
        cut.Find("[data-testid=dd-changes-project]").Change("project:cart");
        var spec = DocumentationDrift.Identities(Catalog()).Single(i => i.LineageKey == "project:cart" && i.Origin == DriftOrigin.Authored);
        cut.Find("[data-testid=dd-changes-identity]").Change(spec.LogicalKey);
        cut.FindAll("[data-testid=dd-changes-version]").Select(r => r.QuerySelector("th")!.TextContent).Should().Equal("cart · Import 1", "cart · Import 2");
        cut.Find("[data-testid=dd-changes-run]").Click();
        cut.Find("[data-testid=dd-result]").GetAttribute("data-intent").Should().Be(nameof(DriftIntent.VersionDrift));

        var generated = DocumentationDrift.Identities(Catalog()).Single(i => i.Origin == DriftOrigin.Generated);
        cut.Find("[data-testid=dd-changes-identity]").Change(generated.LogicalKey);
        cut.Find("[data-testid=dd-changes-no-earlier]").TextContent.Should().Be("Observed in 1 import of cart. No earlier comparable revision is available.");
        cut.FindAll("[data-testid=dd-changes-version]").Should().Contain(r => r.GetAttribute("data-present") == "false" && r.TextContent.Contains("cart · Import 2"),
            "absence is stated only within the same project");

        cut.Find("[data-testid=dd-changes-project]").Change("project:terraform");
        cut.Find("[data-testid=dd-changes-identity]").Change(DocumentationDrift.Identities(Catalog()).Single(i => i.LineageKey == "project:terraform").LogicalKey);
        cut.FindAll("[data-testid=dd-changes-version]").Should().ContainSingle("cart imports are not earlier terraform revisions");
    }

    [Fact]
    public void Drift_separates_equivalent_pairs_from_related_documentation_and_compares_nothing_automatically()
    {
        var state = _repository.SddLifecycle;
        state.Revisions.Add(new SddArtifactRevision { Role = "DataModel", FileName = "cart/specs/001-cart/data-model.md", SourceReference = "cart/specs/001-cart/data-model.md", Content = "# DM\n\n## Cart\n- id\n", Fingerprint = "EEE555", Revision = 1, WorkspaceScope = "import:import-a", ProjectImportId = "import-a" });
        state.GeneratedDocumentationSets[0].Artifacts.Add(new SddGeneratedDocumentArtifact { ModuleId = "module:cart", ModuleRoot = "cart/src/Cart.Api", RelativePath = "cart/src/Cart.Api/autodoc/data-model.md", Kind = "DataModel", Fingerprint = "fff666", ContentCaptured = true });
        state.GeneratedDocumentContents["fff666"] = "# DM\n\n## Cart\n- id\n- expires\n";
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "drift"));
        cut.FindAll("[data-testid=dd-candidates] [data-testid=dd-candidate]").Should().BeEmpty();
        cut.Find("[data-testid=dd-no-candidates]").TextContent.Should().Contain("No authored ↔ generated pair with proven equivalent scope");
        cut.Find("[data-testid=dd-drift-intro]").TextContent.Should().Contain("neither side is automatically authoritative");
        state.DocumentationDriftComparisons.Should().BeEmpty("page load never runs comparisons");
    }
}

/// <summary>The Documentation Drift page: four views (Compare default), legacy tab links, both routes, no cross-workspace review history.</summary>
public sealed class DocumentationDriftPageTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();

    public DocumentationDriftPageTests()
    {
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton<IWorkspaceSessionService>(_repository);
        Services.AddSingleton(Mock.Of<BirkNext.Web.GraphQL.IBirkNextClient>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Theory]
    [InlineData("", "compare")]
    [InlineData("?tab=documents", "compare")]
    [InlineData("?tab=actions", "compare")]
    [InlineData("?tab=overview", "overview")]
    [InlineData("?tab=changes", "changes")]
    [InlineData("?tab=drift", "drift")]
    public void Tabs_are_overview_compare_changes_drift_and_old_links_still_open(string query, string view)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/documentation-drift" + query);
        var cut = Render<SpecDrift>();
        cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).Should().Equal("Overview", "Compare", "Changes", "Drift");
        cut.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().Be(char.ToUpperInvariant(view[0]) + view[1..]);
        cut.Find("[data-testid=dd-panel]").GetAttribute("data-view").Should().Be(view);
    }

    [Fact]
    public void A_link_to_another_tab_while_on_the_page_switches_the_view()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/documentation-drift?tab=drift");
        var cut = Render<SpecDrift>();
        cut.Find("[data-testid=dd-panel]").GetAttribute("data-view").Should().Be("drift");
        navigation.NavigateTo("/spec-drift?tab=changes");
        cut.WaitForAssertion(() => cut.Find("[data-testid=dd-panel]").GetAttribute("data-view").Should().Be("changes"));
        navigation.NavigateTo("/documentation-drift?tab=documents");
        cut.WaitForAssertion(() => cut.Find("[data-testid=dd-panel]").GetAttribute("data-view").Should().Be("compare"));
    }

    [Fact]
    public void Saved_specification_reviews_appear_only_for_a_sample_project_workspace()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/documentation-drift?tab=changes");
        var imported = Render<SpecDrift>();
        imported.FindAll("[data-testid=dd-spec-reviews]").Should().BeEmpty("an imported workspace never reads Sample Project review history");
        imported.Find("[data-testid=dd-spec-reviews-none]").TextContent.Should().Be("No saved specification reviews for this workspace.");
        imported.Markup.Should().NotContain("stored for a Sample Project");

        _repository.CurrentProject = "sample-cart";
        var sample = Render<SpecDrift>();
        sample.Find("[data-testid=dd-spec-reviews]").HasAttribute("open").Should().BeFalse();
    }
}
