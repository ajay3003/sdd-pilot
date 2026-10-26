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
/// Documentation Drift in the browser: symmetrical Left/Right selectors with compact empty states, separate project revision / imported source /
/// source snapshot / generated set facts, the comparison type following the pair, neutral authored ↔ generated results, saving with exact
/// identities, reopening as computed, the Overview (revision availability, generated history collapsed and explained as inventory), Changes
/// (versions of one document, same engine) and Drift (deterministic candidates only). Generic fixtures only.
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
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>(s => s.SaveNowAsync() == Task.FromResult(true)));
    }

    private IReadOnlyList<DriftDocument> Catalog() => DocumentationDrift.Catalog(_repository.SddLifecycle);

    /// <summary>A second import (R2) with a changed spec and no generated documentation.</summary>
    private void SecondRevision()
    {
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-b", ProjectName = "cart", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "cart-r17.zip", ImportedAt = DateTimeOffset.UnixEpoch.AddDays(1) });
        state.Revisions.Add(new SddArtifactRevision { Role = "Specification", FileName = "cart/specs/001-cart/spec.md", SourceReference = "cart/specs/001-cart/spec.md", Content = "# Spec\n\n## Requirements\n\n- FR-001 store carts\n- FR-002 expire carts\n", Fingerprint = "CCC333", Revision = 2, WorkspaceScope = "import:import-b", ProjectImportId = "import-b" });
        state.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet { SetId = "genset-import-b", ProjectImportId = "import-b", State = "NotPresent" });
    }

    [Fact]
    public void Left_and_right_share_one_structure_with_a_compact_empty_state()
    {
        var cut = Render<DocumentationDriftPanel>();
        foreach (var side in new[] { "left", "right" })
        {
            var fieldset = cut.Find($"[data-testid=dd-side-{side}]");
            fieldset.QuerySelectorAll("select").Select(s => s.GetAttribute("data-testid")).Should().Equal($"dd-{side}-revision", $"dd-{side}-document");
            cut.Find($"[data-testid=dd-{side}-empty]").TextContent.Should().Be("Select a document to compare.");
        }
        cut.FindAll("[data-testid=dd-left-provenance], [data-testid=dd-right-provenance]").Should().BeEmpty();
        cut.Find("[data-testid=dd-no-result]").TextContent.Should().Be("Select two documents and run Compare.");
        cut.Find("[data-testid=dd-scope]").TextContent.Should().Be("Scope: Whole document", "only the supported scope is shown, never a fake option");
        cut.FindAll("[data-testid=dd-sets]").Should().BeEmpty("generated documentation sets no longer sit in Compare");

        var generated = Catalog().Single(d => d.Origin == DriftOrigin.Generated);
        cut.Find("[data-testid=dd-right-document]").Change(generated.Id);
        cut.Find("[data-testid=dd-left-empty]");
        cut.Find("[data-testid=dd-right-provenance]");
        cut.Find("[data-testid=dd-side-left]").QuerySelectorAll("select").Length.Should().Be(cut.Find("[data-testid=dd-side-right]").QuerySelectorAll("select").Length);
    }

    [Fact]
    public async Task Authored_vs_generated_is_a_neutral_consistency_comparison_saved_with_exact_identities()
    {
        var cut = Render<DocumentationDriftPanel>();
        cut.Find("[data-testid=dd-run]").HasAttribute("disabled").Should().BeTrue();
        var authored = Catalog().Single(d => d.Origin == DriftOrigin.Authored);

        cut.Find("[data-testid=dd-left-document]").Change(authored.Id);
        var left = cut.Find("[data-testid=dd-left-provenance]").TextContent;
        cut.Find("[data-testid=dd-left-project-revision]").TextContent.Should().Be("R1", "the revision label is not concatenated with the archive name");
        cut.Find("[data-testid=dd-left-imported-source]").TextContent.Should().Be("cart-r16.zip");
        left.Should().Contain("Authored").And.Contain("AAA111").And.Contain("rev 1").And.Contain("001-cart").And.Contain(Snapshot.ToString());
        cut.Find("[data-testid=dd-left-document] option[value='" + authored.Id + "']").TextContent.Should().Be("Authored · Specification · cart/specs/001-cart/spec.md · R1 · rev 1");
        cut.FindAll("[data-testid=dd-suggestion]").Should().ContainSingle().Which.TextContent.Should().StartWith("Suggested comparison");
        cut.Find("[data-testid=dd-suggestion]").Click();

        var right = cut.Find("[data-testid=dd-right-provenance]").TextContent;
        right.Should().Contain("genset-import-a").And.Contain("maintain-docs").And.Contain(Snapshot.ToString()).And.Contain("Unknown — imported with project revision R1").And.Contain("implementation-derived");
        cut.Find("[data-testid=dd-intent] option[selected]").GetAttribute("value").Should().Be(nameof(DriftIntent.ConsistencyDrift));
        cut.FindAll("[data-testid=dd-intent] option").Select(o => o.GetAttribute("value")).Should().Equal([nameof(DriftIntent.ConsistencyDrift)], "version drift does not apply to authored ↔ generated");
        cut.Find("[data-testid=dd-revision-context]").TextContent.Should().StartWith(DocumentationDrift.SameRevision).And.Contain("Content changed");
        cut.Find("[data-testid=dd-right-gdh]").TextContent.Should().Be("View generated documentation health");

        cut.Find("[data-testid=dd-run]").Click();
        cut.Find("[data-testid=dd-result]").GetAttribute("data-intent").Should().Be(nameof(DriftIntent.ConsistencyDrift));
        cut.FindAll("[data-testid=dd-item]").Should().Contain(i => i.GetAttribute("data-kind") == "ConsistencyDiscrepancy");
        cut.Find("[data-testid=dd-neutral]").TextContent.Should().Contain("Neither side is assumed correct");
        cut.Find("[data-testid=dd-source-boundary]").TextContent.Should().Contain("Use Source Analysis for raw source evidence");
        cut.Find("[data-testid=dd-basis]").TextContent.Should().Contain("not assessed");
        cut.Find("[data-testid=dd-result]").TextContent.Should().NotContainAny("defect", "is wrong", "is correct", "Passed", "Failed");

        await cut.Find("[data-testid=dd-save]").ClickAsync(new());
        var saved = _repository.SddLifecycle.DocumentationDriftComparisons.Should().ContainSingle().Subject;
        saved.Left.RevisionId.Should().Be(authored.RevisionId);
        saved.Left.RevisionNumber.Should().Be(1);
        saved.Right.GeneratedSetId.Should().Be("genset-import-a");
        saved.Right.SourceSnapshotId.Should().Be(Snapshot);
        saved.Scope.Should().Be("Whole document");
        cut.Find("[data-testid=dd-saved]").TextContent.Should().Contain("exact artifact revisions");
        _repository.SddLifecycle.Revisions.Should().ContainSingle("comparing never edits or adds artifact revisions");
    }

    [Fact]
    public void The_same_document_on_both_sides_is_refused()
    {
        var cut = Render<DocumentationDriftPanel>();
        var authored = Catalog().Single(d => d.Origin == DriftOrigin.Authored);
        cut.Find("[data-testid=dd-left-document]").Change(authored.Id);
        cut.Find("[data-testid=dd-right-document]").Change(authored.Id);
        cut.Find("[data-testid=dd-guard]").TextContent.Should().Be(DocumentationDrift.SelectDifferent);
        cut.Find("[data-testid=dd-run]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Authored_versions_compare_as_version_drift_without_generated_documentation()
    {
        SecondRevision();
        var cut = Render<DocumentationDriftPanel>();
        var specs = Catalog().Where(d => d.Origin == DriftOrigin.Authored).ToList();
        cut.Find("[data-testid=dd-left-document]").Change(specs[0].Id);
        cut.Find("[data-testid=dd-previous-current]").Click();
        cut.Find("[data-testid=dd-intent] option[selected]").GetAttribute("value").Should().Be(nameof(DriftIntent.VersionDrift));
        cut.Find("[data-testid=dd-revision-context]").TextContent.Should().Contain("R1 ↔ R2");
        cut.Find("[data-testid=dd-run]").Click();
        cut.Find("[data-testid=dd-counts]").TextContent.Should().Contain("Changed between R1 and R2");
        cut.FindAll("[data-testid=dd-neutral]").Should().BeEmpty();

        cut.Find("[data-testid=dd-right-revision]").Change("R2");
        cut.Find("[data-testid=dd-right-no-generated]").TextContent.Should().Be("Generated documentation not present for this project revision.");
    }

    [Fact]
    public void Changing_the_pair_updates_the_selected_comparison_type_visibly()
    {
        SecondRevision();
        var cut = Render<DocumentationDriftPanel>();
        var catalog = Catalog();
        var specs = catalog.Where(d => d.Origin == DriftOrigin.Authored).ToList();
        cut.Find("[data-testid=dd-left-document]").Change(specs[0].Id);
        cut.Find("[data-testid=dd-right-document]").Change(catalog.Single(d => d.Origin == DriftOrigin.Generated).Id);
        cut.FindAll("[data-testid=dd-intent] option[selected]").Select(o => o.GetAttribute("value")).Should().Equal(nameof(DriftIntent.ConsistencyDrift));
        cut.Find("[data-testid=dd-right-document]").Change(specs[1].Id);
        cut.FindAll("[data-testid=dd-intent] option").Should().HaveCount(2);
        cut.FindAll("[data-testid=dd-intent] option[selected]").Select(o => o.GetAttribute("value")).Should().Equal(nameof(DriftIntent.VersionDrift));
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
        cut.FindAll("[data-testid=dd-history-row]").Should().ContainSingle().Which.TextContent.Should().Contain("R1").And.Contain("rev 1");
        cut.Find("[data-testid=dd-history-open]").Click();
        requested.Should().Be("compare");
        cut.Render(p => p.Add(x => x.View, "compare"));
        cut.Find("[data-testid=dd-outcome]").TextContent.Should().Be("stored outcome");
        cut.Find("[data-testid=dd-reopened]").TextContent.Should().Contain("exactly as compared");
    }

    [Fact]
    public void Overview_shows_revision_availability_neutrally_and_keeps_generated_history_collapsed()
    {
        SecondRevision();
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "overview"));
        var rows = cut.FindAll("[data-testid=dd-revision-row]");
        rows.Select(r => r.GetAttribute("data-revision")).Should().Equal("R1", "R2");
        rows[0].TextContent.Should().Contain("cart-r16.zip").And.Contain(Snapshot.ToString());
        rows[1].GetAttribute("data-generated").Should().Be("Not present");
        rows[1].TextContent.Should().Contain("Not present").And.NotContain("Error").And.NotContain("Missing");
        cut.Find("[data-testid=dd-not-present-meaning]").TextContent.Should().Contain("neutral");

        var history = cut.Find("[data-testid=dd-generated-history]");
        history.HasAttribute("open").Should().BeFalse("inventory history is collapsed by default");
        history.TextContent.Should().Contain("inventory, not drift");
        cut.Find("[data-testid=dd-coverage-text]").TextContent.Should().Be("Generated documentation coverage decreased from 1 module / 1 document to 0 modules / 0 documents.");
        cut.Find("[data-testid=dd-inventory-counts]").TextContent.Should().Contain("1 generated artifact(s) present in R1 that are not present in R2");
        cut.FindAll("[data-testid=dd-inventory-list]").Should().OnlyContain(d => !d.HasAttribute("open"), "artifact rows are never listed open by default");
    }

    [Fact]
    public void Changes_lists_versions_per_revision_and_compares_them_with_the_same_engine()
    {
        SecondRevision();
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "changes"));
        var spec = DocumentationDrift.Identities(Catalog()).Single(i => i.Origin == DriftOrigin.Authored);
        cut.Find("[data-testid=dd-changes-identity]").Change(spec.LogicalKey);
        cut.FindAll("[data-testid=dd-changes-version]").Should().HaveCount(2);
        cut.Find("[data-testid=dd-changes-run]").TextContent.Should().Be("Compare R1 → R2");
        cut.Find("[data-testid=dd-changes-run]").Click();
        cut.Find("[data-testid=dd-result]").GetAttribute("data-intent").Should().Be(nameof(DriftIntent.VersionDrift));
        cut.Find("[data-testid=dd-counts]").TextContent.Should().Contain("Changed between R1 and R2");

        var generated = DocumentationDrift.Identities(Catalog()).Single(i => i.Origin == DriftOrigin.Generated);
        cut.Find("[data-testid=dd-changes-identity]").Change(generated.LogicalKey);
        cut.Find("[data-testid=dd-changes-no-earlier]").TextContent.Should().Be("No earlier revision of this document is available.");
        cut.FindAll("[data-testid=dd-changes-version]").Should().Contain(r => r.GetAttribute("data-present") == "false" && r.TextContent.Contains("R2"), "a missing version is stated, never substituted");
    }

    [Fact]
    public void Drift_lists_no_candidate_without_a_deterministic_pair()
    {
        var cut = Render<DocumentationDriftPanel>(p => p.Add(x => x.View, "drift"));
        cut.FindAll("[data-testid=dd-candidate]").Should().BeEmpty("a specification is never paired with generated architecture automatically");
        cut.Find("[data-testid=dd-no-candidates]").TextContent.Should().Contain("Select any two documents in Compare");
        cut.Find("[data-testid=dd-drift-intro]").TextContent.Should().Contain("Neither side is assumed correct");
    }
}

/// <summary>The Documentation Drift page: four views (Compare default), legacy tab links, both routes.</summary>
public sealed class DocumentationDriftPageTests : BunitContext
{
    public DocumentationDriftPageTests()
    {
        var repository = new WorkspaceArtifactRepository();
        Services.AddSingleton(repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(repository);
        Services.AddSingleton<IWorkspaceSessionService>(repository);
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
        cut.Markup.Should().NotContain("Recommended Actions").And.NotContain("Requirements at Risk");
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
    public void Changes_keeps_saved_specification_reviews_as_collapsed_secondary_history()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/documentation-drift?tab=changes");
        var cut = Render<SpecDrift>();
        cut.Find("[data-testid=dd-spec-reviews]").HasAttribute("open").Should().BeFalse();
    }
}
