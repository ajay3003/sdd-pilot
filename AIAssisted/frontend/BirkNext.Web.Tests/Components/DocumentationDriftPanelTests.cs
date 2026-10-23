using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Documentation Drift → Documents in the browser: provenance before the run (project revision, generated set, module, snapshot, generator,
/// fingerprint, "Generated at: Unknown"), the suggested intent and revision context, the self-compare guard, the result, saving with exact
/// identities and reopening a saved comparison as it was computed. Generic fixtures only.
/// </summary>
public sealed class DocumentationDriftPanelTests : BunitContext
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private static readonly Guid Snapshot = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public DocumentationDriftPanelTests()
    {
        var state = _repository.SddLifecycle;
        state.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-a", ProjectName = "cart", ProjectNameBasis = "ArchiveRoot", ArchiveFileName = "cart-r16.zip", ImportedAt = DateTimeOffset.UnixEpoch, SourceSnapshotId = Snapshot });
        state.Revisions.Add(new SddArtifactRevision { Role = "Specification", FileName = "cart/specs/spec.md", SourceReference = "cart/specs/spec.md", Content = "# Spec\n\n## Requirements\n\n- FR-001 store carts\n", Fingerprint = "AAA111", Revision = 1, WorkspaceScope = "import:import-a", ProjectImportId = "import-a" });
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

    [Fact]
    public async Task Selecting_authored_and_generated_of_the_same_revision_suggests_authored_vs_generated_and_saves_exact_identities()
    {
        var cut = Render<DocumentationDriftPanel>();
        cut.Find("[data-testid=dd-run]").HasAttribute("disabled").Should().BeTrue();
        var authored = DocumentationDrift.Catalog(_repository.SddLifecycle).Single(d => d.Origin == DriftOrigin.Authored);
        var generated = DocumentationDrift.Catalog(_repository.SddLifecycle).Single(d => d.Origin == DriftOrigin.Generated);

        cut.Find("[data-testid=dd-left-document]").Change(authored.Id);
        cut.Find("[data-testid=dd-left-provenance]").TextContent.Should().Contain("R1 · cart-r16.zip").And.Contain("Authored").And.Contain("AAA111");
        cut.FindAll("[data-testid=dd-suggestion]").Should().ContainSingle().Which.TextContent.Should().StartWith("Suggested comparison");
        cut.Find("[data-testid=dd-suggestion]").Click();

        var right = cut.Find("[data-testid=dd-right-provenance]").TextContent;
        right.Should().Contain("genset-import-a").And.Contain("maintain-docs").And.Contain(Snapshot.ToString()).And.Contain("Unknown — imported with project revision R1");
        cut.Find("[data-testid=dd-intent]").GetAttribute("value").Should().Be(nameof(DriftIntent.AuthoredVsGenerated));
        cut.Find("[data-testid=dd-revision-context]").TextContent.Should().StartWith(DocumentationDrift.SameRevision);
        cut.Find("[data-testid=dd-right-gdh]").GetAttribute("href").Should().Be("admin/system-settings?section=generated-docs-health");

        cut.Find("[data-testid=dd-run]").Click();
        cut.Find("[data-testid=dd-result]").GetAttribute("data-intent").Should().Be(nameof(DriftIntent.AuthoredVsGenerated));
        cut.FindAll("[data-testid=dd-item]").Should().Contain(i => i.GetAttribute("data-kind") == "ConsistencyDiscrepancy");
        cut.Markup.Should().NotContain("defect");

        await cut.Find("[data-testid=dd-save]").ClickAsync(new());
        var saved = _repository.SddLifecycle.DocumentationDriftComparisons.Should().ContainSingle().Subject;
        saved.Left.RevisionId.Should().Be(authored.RevisionId);
        saved.Right.GeneratedSetId.Should().Be("genset-import-a");
        saved.Right.SourceSnapshotId.Should().Be(Snapshot);
        cut.Find("[data-testid=dd-saved]").TextContent.Should().Contain("exact artifact revisions");
        _ = generated;
    }

    [Fact]
    public void The_same_document_on_both_sides_is_refused()
    {
        var cut = Render<DocumentationDriftPanel>();
        var authored = DocumentationDrift.Catalog(_repository.SddLifecycle).Single(d => d.Origin == DriftOrigin.Authored);
        cut.Find("[data-testid=dd-left-document]").Change(authored.Id);
        cut.Find("[data-testid=dd-right-document]").Change(authored.Id);
        cut.Find("[data-testid=dd-guard]").TextContent.Should().Be(DocumentationDrift.SelectDifferent);
        cut.Find("[data-testid=dd-run]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void A_saved_comparison_reopens_as_it_was_computed()
    {
        var catalog = DocumentationDrift.Catalog(_repository.SddLifecycle);
        var left = catalog.Single(d => d.Origin == DriftOrigin.Authored);
        var right = catalog.Single(d => d.Origin == DriftOrigin.Generated);
        DocumentationDrift.Save(_repository.SddLifecycle, left, right, new DriftComparisonResult(DriftIntent.ConsistencyDrift, DocumentationDrift.SameRevision, "stored outcome", [], new Dictionary<string, int>()), null, DateTimeOffset.UnixEpoch);
        var cut = Render<DocumentationDriftPanel>();
        cut.FindAll("[data-testid=dd-history-row]").Should().ContainSingle();
        cut.Find("[data-testid=dd-history-open]").Click();
        cut.Find("[data-testid=dd-outcome]").TextContent.Should().Be("stored outcome");
        cut.Find("[data-testid=dd-reopened]").TextContent.Should().Contain("exactly as compared");
    }

    [Fact]
    public void Generated_sets_show_their_state_per_revision()
    {
        _repository.SddLifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = "import-b", ProjectName = "cart", ArchiveFileName = "cart-r17.zip", ImportedAt = DateTimeOffset.UnixEpoch.AddDays(1) });
        _repository.SddLifecycle.GeneratedDocumentationSets.Add(new SddGeneratedDocumentationSet { SetId = "genset-import-b", ProjectImportId = "import-b", State = "NotPresent" });
        var cut = Render<DocumentationDriftPanel>();
        cut.FindAll("[data-testid=dd-set]").Select(s => s.TextContent).Should().Contain(t => t.StartsWith("R2") && t.Contains("Generated documentation not present"));
    }
}
