using System.Text.Json;
using BirkNext.GeneratedDocumentation;
using BirkNext.Integrations;
using BirkNext.ProjectImport;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Documentation Drift over the existing provenance: a project revision is a Project Import; authored history is the append-only artifact
/// revisions; each import's Generated Documentation Set is derived from the same import, linked to the import and its source snapshot, with
/// text stored once per fingerprint. Authored ↔ authored, generated ↔ generated and authored ↔ generated comparisons, same/different revision
/// context, no-change by fingerprint, added/removed artifacts, the self-compare guard and reproducible saved comparisons. Generic fixtures.
/// </summary>
public sealed class DocumentationDriftTests
{
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly ProjectImportActivation _activation;

    public DocumentationDriftTests() => _activation = new ProjectImportActivation(_repository);

    private const string SpecV1 = "# Feature Specification: Cart\n\n## Requirements\n\n- **FR-001**: System MUST store carts.\n\n## Key Entities\n\n- **Cart**: a basket.\n";
    private const string SpecV2 = "# Feature Specification: Cart\n\n## Requirements\n\n- **FR-001**: System MUST store carts.\n- **FR-002**: System MUST expire carts after 30 days.\n\n## Key Entities\n\n- **Cart**: a basket.\n";
    private const string ArchitectureV1 = "# Architecture\n\n## Components\n\n- Cart API serves HTTP.\n\n## Storage\n\n- SQL Server.\n";
    private const string ArchitectureV2 = "# Architecture\n\n## Components\n\n- Cart API serves HTTP.\n- Expiry worker runs nightly.\n\n## Storage\n\n- SQL Server.\n";
    private const string Readme = "# Cart service\n\nOverview of the cart service.\n";
    private const string Dataflow = "# Data flow\n\n- Cart API → SQL Server\n";
    private const string Routes = "# Routes\n\n| Route | Method |\n|---|---|\n| /carts | GET |\n";

    private static string Fp(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>One import of project <paramref name="root"/>: authored spec plus generated docs under <c>{root}/src/Cart.Api/autodoc</c>.</summary>
    private SddProjectImportRecord Import(string root, string sha, string spec, (string File, string Text, GeneratedDocumentKind Kind)[] generated,
        ProjectImportSourceState state = ProjectImportSourceState.Created, bool includeText = true, DateTimeOffset? at = null)
    {
        var documents = new List<(string, string)> { ($"{root}/specs/001-cart/spec.md", spec) };
        if (includeText) documents.AddRange(generated.Select(g => ($"{root}/src/Cart.Api/autodoc/{g.File}", g.Text)));
        var preview = new ProjectImportPreview
        {
            StagingId = Guid.NewGuid(), ImportId = "import-" + sha[..16], Archive = new ProjectImportArchive(root + ".zip", sha, 4096, 12), ProjectName = root,
            ProjectNameBasis = ProjectNameBasis.ArchiveRoot, Source = new ProjectImportSourceDetection { Detected = true, SourceFiles = 3 },
            Documents = documents.Select(d => new ProjectImportDocument(d.Item1, d.Item1.Split('/')[^1], d.Item2.Length, d.Item2)).ToList(),
        };
        var snapshot = state == ProjectImportSourceState.Created ? Guid.NewGuid() : (Guid?)null;
        var commit = new ProjectImportCommitResult
        {
            StagingId = preview.StagingId,
            Provenance = new ProjectImportProvenance { ImportId = preview.ImportId, ArchiveFileName = preview.Archive.FileName, ArchiveSha256 = sha, ImportedAt = at ?? DateTimeOffset.UtcNow },
            Source = new ProjectImportSourceResult
            {
                State = state, SnapshotId = snapshot, SnapshotStatus = snapshot is null ? null : SourceAnalysisStatus.Ready,
                GeneratedDocuments = snapshot is null ? null : [.. generated.Select(g => new ProjectImportGeneratedDocument($"{root}/src/Cart.Api/autodoc/{g.File}", "module:cart",
                    $"{root}/src/Cart.Api", g.Kind, Fp(g.Text), "maintain-docs"))],
            },
        };
        return _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), commit);
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private (SddProjectImportRecord R1, SddProjectImportRecord R2) TwoRevisions()
    {
        var r1 = Import("cart-r16", new string('a', 64), SpecV1,
            [("README.md", Readme, GeneratedDocumentKind.Overview), ("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture), ("dataflow.md", Dataflow, GeneratedDocumentKind.DataFlow)], at: T0);
        var r2 = Import("cart-r17", new string('b', 64), SpecV2,
            [("README.md", Readme, GeneratedDocumentKind.Overview), ("architecture.md", ArchitectureV2, GeneratedDocumentKind.Architecture), ("routes.md", Routes, GeneratedDocumentKind.Routes)], at: T0.AddDays(1));
        return (r1, r2);
    }

    private IReadOnlyList<DriftDocument> Catalog() => DocumentationDrift.Catalog(_repository.SddLifecycle);

    private DriftDocument Generated(string file, string revision) => Catalog().Single(d => d.Origin == DriftOrigin.Generated && d.Path.EndsWith("/" + file) && d.ProjectRevision == revision);
    private DriftDocument Spec(string revision) => Catalog().Single(d => d.Origin == DriftOrigin.Authored && d.TypeLabel == "Specification" && d.ProjectRevision == revision);

    private DriftComparisonResult Compare(DriftDocument left, DriftDocument right, DriftIntent? intent = null) =>
        DocumentationDrift.Compare(left, DocumentationDrift.Text(_repository.SddLifecycle, left)!, right, DocumentationDrift.Text(_repository.SddLifecycle, right)!,
            intent ?? DocumentationDrift.SuggestIntent(left, right));

    [Fact]
    public void Each_import_gets_one_generated_set_linked_to_the_import_its_snapshot_and_module_with_text_stored_once()
    {
        var (r1, r2) = TwoRevisions();
        var sets = _repository.SddLifecycle.GeneratedDocumentationSets;
        sets.Should().HaveCount(2);
        var g1 = sets.Single(s => s.ProjectImportId == r1.ImportId);
        g1.SetId.Should().Be(DocumentationDrift.SetIdFor(r1.ImportId));
        g1.SourceSnapshotId.Should().Be(r1.SourceSnapshotId!.Value, "the set is linked to the snapshot of the same import");
        g1.State.Should().Be("Present");
        g1.Artifacts.Should().OnlyContain(a => a.ModuleId == "module:cart" && a.ContentCaptured && a.Generator == "maintain-docs");
        _repository.SddLifecycle.GeneratedDocumentContents.Should().HaveCount(5, "README is identical in both imports and stored once");
        _repository.SddLifecycle.GeneratedDocumentContents.Keys.Should().Contain(Fp(Readme));
        Generated("architecture.md", "R1").SourceSnapshotId.Should().Be(r1.SourceSnapshotId);
        Spec("R2").ProjectImportId.Should().Be(r2.ImportId, "artifacts imported together share the project revision");
    }

    [Fact]
    public void Generated_version_drift_across_imports_matches_module_and_path_and_shows_changes()
    {
        TwoRevisions();
        var left = Generated("architecture.md", "R1");
        var right = Generated("architecture.md", "R2");
        left.LogicalKey.Should().Be(right.LogicalKey, "module + module-relative path, independent of the archive's top-level folder");
        DocumentationDrift.SuggestIntent(left, right).Should().Be(DriftIntent.VersionDrift);
        DocumentationDrift.RevisionContext(left, right).Should().Be(DocumentationDrift.DifferentRevisions);
        DocumentationDrift.Versions(Catalog(), right).Select(v => v.ProjectRevision).Should().Equal("R1", "R2");

        var result = Compare(left, right);
        result.Items.Should().ContainSingle(i => i.Kind == "Changed" && i.Section == "Components").Which.Right.Should().Contain("Expiry worker runs nightly.");
        result.Outcome.Should().NotContain(DocumentationDrift.NoContentChange);
    }

    [Fact]
    public void Generated_set_overview_reports_unchanged_changed_added_and_removed()
    {
        var (r1, r2) = TwoRevisions();
        var overview = DocumentationDrift.CompareSets(Catalog(), DocumentationDrift.SetIdFor(r1.ImportId), DocumentationDrift.SetIdFor(r2.ImportId));
        (overview.Unchanged, overview.Changed, overview.Added, overview.Removed).Should().Be((1, 1, 1, 1));
        overview.Rows.Single(r => r.State == "Removed").Left!.Path.Should().EndWith("dataflow.md");
        overview.Rows.Single(r => r.State == "Added").Right!.Path.Should().EndWith("routes.md");
        DocumentationDrift.RevisionChanges(_repository.SddLifecycle, Catalog(), r2.ImportId).Should().Contain(c => c.Origin == "Authored" && c.State == "Changed");
    }

    [Fact]
    public void Identical_content_across_revisions_is_no_content_change()
    {
        TwoRevisions();
        var result = Compare(Generated("README.md", "R1"), Generated("README.md", "R2"));
        result.Outcome.Should().Be(DocumentationDrift.NoContentChange);
        result.Items.Should().BeEmpty("no artificial drift");
    }

    [Fact]
    public void Authored_revisions_compare_as_version_drift_without_source_snapshot_semantics()
    {
        TwoRevisions();
        var left = Spec("R1");
        var right = Spec("R2");
        left.LogicalKey.Should().Be(right.LogicalKey);
        var result = Compare(left, right);
        result.Intent.Should().Be(DriftIntent.VersionDrift);
        result.Items.Should().ContainSingle(i => i.Kind == "Changed").Which.Right.Should().ContainSingle(l => l.Contains("FR-002"));
    }

    [Fact]
    public void Authored_vs_generated_in_the_same_revision_is_a_neutral_consistency_discrepancy()
    {
        TwoRevisions();
        var left = Spec("R2");
        var suggestions = DocumentationDrift.SuggestedGenerated(Catalog(), left);
        suggestions.Should().NotBeEmpty().And.OnlyContain(s => s.ProjectRevision == "R2", "suggestions come from the set of the same project revision");
        var right = Generated("architecture.md", "R2");
        DocumentationDrift.SuggestIntent(left, right).Should().Be(DriftIntent.AuthoredVsGenerated);
        DocumentationDrift.RevisionContext(left, right).Should().Be(DocumentationDrift.SameRevision);

        var result = Compare(left, right);
        result.Items.Should().NotBeEmpty();
        string.Join(" ", result.Items.Select(i => i.Kind + " " + i.Detail) .Append(result.Outcome)).Should()
            .NotContainAny("defect", "Defect", "wrong", "incorrect").And.Contain("neither side is assumed correct");
        result.Items.Where(i => i.Kind == "ConsistencyDiscrepancy").Should().OnlyContain(i => i.Detail.StartsWith(DocumentationDrift.Neutral));
    }

    [Fact]
    public void Authored_old_revision_vs_newer_generated_shows_different_revision_context()
    {
        TwoRevisions();
        DocumentationDrift.RevisionContext(Spec("R1"), Generated("architecture.md", "R2")).Should().Be(DocumentationDrift.DifferentRevisions);
        Compare(Spec("R1"), Generated("architecture.md", "R2")).RevisionContext.Should().Be(DocumentationDrift.DifferentRevisions);
    }

    [Fact]
    public void Same_artifact_same_revision_is_refused_and_unsupported_types_are_explicit()
    {
        TwoRevisions();
        var spec = Spec("R2");
        DocumentationDrift.Guard(spec, spec).Should().Be(DocumentationDrift.SelectDifferent);
        DocumentationDrift.Guard(spec, spec with { Supported = false, Id = "other" }).Should().Be(DocumentationDrift.Unsupported);
        DocumentationDrift.Guard(spec, Generated("architecture.md", "R2")).Should().BeNull();
    }

    [Fact]
    public void A_later_import_without_generated_documentation_never_reuses_an_earlier_set()
    {
        TwoRevisions();
        var r3 = Import("cart-r18", new string('c', 64), SpecV2, [], at: T0.AddDays(2));
        var r4 = Import("cart-r19", new string('d', 64), SpecV2, [("README.md", Readme, GeneratedDocumentKind.Overview)], state: ProjectImportSourceState.NotCreated, at: T0.AddDays(3));
        var sets = _repository.SddLifecycle.GeneratedDocumentationSets;
        sets.Single(s => s.ProjectImportId == r3.ImportId).Should().Match<SddGeneratedDocumentationSet>(s => s.State == "NotPresent" && s.Artifacts.Count == 0);
        sets.Single(s => s.ProjectImportId == r4.ImportId).Should().Match<SddGeneratedDocumentationSet>(s => s.State == "NotEvaluated" && s.Artifacts.Count == 0);
        Catalog().Where(d => d.Origin == DriftOrigin.Generated).Select(d => d.ProjectRevision).Distinct().Should().BeEquivalentTo(["R1", "R2"], "historical sets stay under their own revisions");
    }

    [Fact]
    public void Saved_comparisons_keep_exact_identities_and_survive_newer_imports_and_restore()
    {
        TwoRevisions();
        var left = Generated("architecture.md", "R1");
        var right = Generated("architecture.md", "R2");
        var saved = DocumentationDrift.Save(_repository.SddLifecycle, left, right, Compare(left, right), _repository.SessionScope, T0);
        saved.Left.GeneratedSetId.Should().Be(left.GeneratedSetId);
        saved.Left.SourceSnapshotId.Should().Be(left.SourceSnapshotId);
        saved.Right.Fingerprint.Should().Be(Fp(ArchitectureV2));
        saved.AnalysisVersion.Should().Be(DocumentationDrift.AnalysisVersion);

        Import("cart-r18", new string('c', 64), SpecV2, [("architecture.md", ArchitectureV2 + "\n- Another line.\n", GeneratedDocumentKind.Architecture)], at: T0.AddDays(2));
        var restored = new WorkspaceArtifactRepository();
        restored.RestoreSddLifecycle(JsonSerializer.Serialize(_repository.SddLifecycle));
        var reloaded = restored.SddLifecycle.DocumentationDriftComparisons.Single();
        var (l, r) = DocumentationDrift.Resolve(DocumentationDrift.Catalog(restored.SddLifecycle), reloaded);
        l!.ProjectRevision.Should().Be("R1");
        r!.ProjectRevision.Should().Be("R2", "an old comparison never points at newer contents");
        r.Fingerprint.Should().Be(Fp(ArchitectureV2));
        reloaded.Items.Should().BeEquivalentTo(saved.Items);
        restored.SddLifecycle.GeneratedDocumentationSets.Should().HaveCount(3);
    }

    [Fact]
    public void Projects_without_generated_documentation_still_compare_authored_revisions()
    {
        Import("mu-r1", new string('e', 64), SpecV1, [], at: T0);
        Import("mu-r2", new string('f', 64), SpecV2, [], at: T0.AddDays(1));
        var specs = Catalog().Where(d => d.Origin == DriftOrigin.Authored && d.TypeLabel == "Specification").ToList();
        specs.Should().HaveCount(2);
        DocumentationDrift.Guard(specs[0], specs[1]).Should().BeNull();
        Compare(specs[0], specs[1]).Items.Should().NotBeEmpty();
    }

    [Fact]
    public void A_set_whose_text_was_not_captured_keeps_its_references_but_cannot_be_compared()
    {
        var r1 = Import("cart-r16", new string('a', 64), SpecV1, [("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture)], includeText: false, at: T0);
        var set = _repository.SddLifecycle.GeneratedDocumentationSets.Single(s => s.ProjectImportId == r1.ImportId);
        set.Artifacts.Should().ContainSingle().Which.ContentCaptured.Should().BeFalse();
        var generated = Catalog().Single(d => d.Origin == DriftOrigin.Generated);
        DocumentationDrift.Guard(Spec("R1"), generated).Should().Contain("not captured");
    }

    [Fact]
    public void Renamed_feature_keeps_its_internal_key_and_route_alias()
    {
        var item = NavigationCatalog.Sections.SelectMany(g => g.Items).Single(i => i.Route == "documentation-drift");
        item.Label.Should().Be("Documentation Drift");
        NavigationCatalog.Sections.SelectMany(g => g.Items).Should().NotContain(i => i.Label.Contains("Spec Drift"));
        var routes = typeof(BirkNext.Web.Pages.SpecDrift).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template);
        routes.Should().BeEquivalentTo(["/documentation-drift", "/spec-drift"], "the old route stays as an alias");
        DocumentationDrift.Description.Should().Be("Compare authored and generated documentation artifacts to identify version and consistency drift.");
    }
}
