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
/// Documentation Drift over the existing provenance, with project lineage: imports of one project (same authored project root) are its
/// revisions; imports of different projects are never revisions of each other. Version comparison needs the same document identity in one
/// lineage; authored ↔ generated is consistency within one import; anything else is a manual comparison. Pairs expose scope, and the same
/// module + type is never evidence of equivalent scope. Generic fixtures.
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
    private const string Model = "# Data Model\n\n## Cart\n\n- id: guid\n";
    private const string GeneratedModel = "# Data Model\n\n## Cart\n\n- id: guid\n- expiresAt: date\n";

    private static string Fp(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>One import of project <paramref name="root"/> (its archive root folder): authored spec plus generated docs under the module.</summary>
    private SddProjectImportRecord Import(string root, char sha, string spec, (string File, string Text, GeneratedDocumentKind Kind)[] generated,
        ProjectImportSourceState state = ProjectImportSourceState.Created, bool includeText = true, DateTimeOffset? at = null,
        string moduleDir = "src/Cart.Api", (string Path, string Text)[]? authored = null, ProjectNameBasis basis = ProjectNameBasis.ArchiveRoot, string? archive = null)
    {
        var shaText = new string(sha, 64);
        var moduleRoot = moduleDir.Length == 0 ? root : $"{root}/{moduleDir}";
        var documents = new List<(string, string)> { ($"{root}/specs/001-cart/spec.md", spec) };
        documents.AddRange((authored ?? []).Select(a => ($"{root}/{a.Path}", a.Text)));
        if (includeText) documents.AddRange(generated.Select(g => ($"{moduleRoot}/autodoc/{g.File}", g.Text)));
        var preview = new ProjectImportPreview
        {
            StagingId = Guid.NewGuid(), ImportId = "import-" + shaText[..16], Archive = new ProjectImportArchive(archive ?? root + ".zip", shaText, 4096, 12), ProjectName = root,
            ProjectNameBasis = basis, Source = new ProjectImportSourceDetection { Detected = true, SourceFiles = 3 },
            Documents = documents.Select(d => new ProjectImportDocument(d.Item1, d.Item1.Split('/')[^1], d.Item2.Length, d.Item2)).ToList(),
        };
        var snapshot = state == ProjectImportSourceState.Created ? Guid.NewGuid() : (Guid?)null;
        var commit = new ProjectImportCommitResult
        {
            StagingId = preview.StagingId,
            Provenance = new ProjectImportProvenance { ImportId = preview.ImportId, ArchiveFileName = preview.Archive.FileName, ArchiveSha256 = shaText, ImportedAt = at ?? DateTimeOffset.UtcNow },
            Source = new ProjectImportSourceResult
            {
                State = state, SnapshotId = snapshot, SnapshotStatus = snapshot is null ? null : SourceAnalysisStatus.Ready,
                GeneratedDocuments = snapshot is null ? null : [.. generated.Select(g => new ProjectImportGeneratedDocument($"{moduleRoot}/autodoc/{g.File}", "module:cart",
                    moduleRoot, g.Kind, Fp(g.Text), "maintain-docs"))],
            },
        };
        return _activation.Activate(preview, ProjectImportArtifactDiscovery.From(preview), commit);
    }

    /// <summary>Two imports of the same project "cart" (same authored project root).</summary>
    private void TwoImportsOfOneProject()
    {
        Import("cart", 'a', SpecV1,
            [("README.md", Readme, GeneratedDocumentKind.Overview), ("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture), ("dataflow.md", Dataflow, GeneratedDocumentKind.DataFlow)], at: T0);
        Import("cart", 'b', SpecV2,
            [("README.md", Readme, GeneratedDocumentKind.Overview), ("architecture.md", ArchitectureV2, GeneratedDocumentKind.Architecture), ("routes.md", Routes, GeneratedDocumentKind.Routes)], at: T0.AddDays(1));
    }

    private IReadOnlyList<DriftDocument> Catalog() => DocumentationDrift.Catalog(_repository.SddLifecycle);
    private DriftDocument Generated(string file, string import) => Catalog().Single(d => d.Origin == DriftOrigin.Generated && d.Path.EndsWith("/" + file) && d.ProjectRevision == import);
    private DriftDocument Spec(string import) => Catalog().Single(d => d.Origin == DriftOrigin.Authored && d.TypeLabel == "Specification" && d.ProjectRevision == import);

    private DriftComparisonResult Compare(DriftDocument left, DriftDocument right, DriftIntent? intent = null) =>
        DocumentationDrift.Compare(left, DocumentationDrift.Text(_repository.SddLifecycle, left)!, right, DocumentationDrift.Text(_repository.SddLifecycle, right)!,
            intent ?? DocumentationDrift.SuggestIntent(left, right));

    // ── Lineage ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Unrelated_imports_are_independent_projects_never_a_revision_chain()
    {
        Import("M2LB.Common", 'a', SpecV1, [], at: T0);
        Import("Terraform-iac-m2lb", 'b', SpecV1, [("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture)], at: T0.AddDays(1));
        Import("M2LB", 'c', SpecV2, [("architecture.md", ArchitectureV2, GeneratedDocumentKind.Architecture)], at: T0.AddDays(2));

        var imports = DocumentationDrift.Imports(_repository.SddLifecycle);
        imports.Select(i => i.Label).Should().Equal("M2LB.Common · Import 1", "Terraform-iac-m2lb · Import 1", "M2LB · Import 1");
        imports.Select(i => i.LineageKey).Should().OnlyHaveUniqueItems();
        imports.Select(i => i.GlobalRevision).Should().Equal("R1", "R2", "R3");
        DocumentationDrift.VersionComparableLineages(imports).Should().Be(0);

        var identities = DocumentationDrift.Identities(Catalog());
        identities.Should().OnlyContain(i => i.Versions.Count == 1, "the same path in another project is not an earlier revision");
        var (common, m2lb) = (Spec("M2LB.Common · Import 1"), Spec("M2LB · Import 1"));
        DocumentationDrift.RevisionContext(common, m2lb).Should().Be(DocumentationDrift.UnrelatedImports);
        DocumentationDrift.SuggestIntent(common, m2lb).Should().Be(DriftIntent.Manual);
        DocumentationDrift.Eligibility(DriftIntent.VersionDrift, common, m2lb).Should().NotBeNull("chronological order never creates version history");
        var (terraform, m2lbGen) = (Generated("architecture.md", "Terraform-iac-m2lb · Import 1"), Generated("architecture.md", "M2LB · Import 1"));
        DocumentationDrift.Versions(Catalog(), m2lbGen).Should().ContainSingle("Terraform generated docs are not M2LB history");
        DocumentationDrift.Eligibility(DriftIntent.VersionDrift, terraform, m2lbGen).Should().NotBeNull();
    }

    [Fact]
    public void Repeated_imports_of_one_project_are_its_revisions_and_fingerprints_decide_change()
    {
        TwoImportsOfOneProject();
        var imports = DocumentationDrift.Imports(_repository.SddLifecycle);
        imports.Select(i => i.Label).Should().Equal("cart · Import 1", "cart · Import 2");
        DocumentationDrift.VersionComparableLineages(imports).Should().Be(1);

        var (s1, s2) = (Spec("cart · Import 1"), Spec("cart · Import 2"));
        s1.LogicalKey.Should().Be(s2.LogicalKey);
        DocumentationDrift.Versions(Catalog(), s2).Should().HaveCount(2);
        DocumentationDrift.SuggestIntent(s1, s2).Should().Be(DriftIntent.VersionDrift);
        DocumentationDrift.RevisionContext(s1, s2).Should().Be(DocumentationDrift.DifferentRevisions);
        DocumentationDrift.ContentState(s1, s2).Should().Be("Content changed");
        Compare(s1, s2).Items.Should().ContainSingle(i => i.Kind == "Changed").Which.Right.Should().ContainSingle(l => l.Contains("FR-002"));

        var (r1, r2) = (Generated("README.md", "cart · Import 1"), Generated("README.md", "cart · Import 2"));
        DocumentationDrift.ContentState(r1, r2).Should().Be(DocumentationDrift.NoContentChange);
        Compare(r1, r2).Outcome.Should().Be(DocumentationDrift.NoContentChange);
        _repository.SddLifecycle.GeneratedDocumentContents.Should().HaveCount(5, "README is identical in both imports and stored once");
    }

    [Fact]
    public void Generated_history_exists_only_within_one_project_and_absence_is_neutral()
    {
        Import("cart", 'a', SpecV1, [], at: T0);
        Import("cart", 'b', SpecV2, [("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture)], at: T0.AddDays(1));
        var summaries = DocumentationDrift.ImportSummaries(_repository.SddLifecycle, Catalog());
        summaries[0].GeneratedState.Should().Be(DocumentationDrift.NotPresent);
        summaries[0].GeneratedDocuments.Should().Be(0, "the later set is never reused for the earlier import");
        summaries[1].GeneratedState.Should().Be("Present");
        DocumentationDrift.Versions(Catalog(), Generated("architecture.md", "cart · Import 2")).Should().ContainSingle("first observed in Import 2");
        DocumentationDrift.NotPresentMeaning.Should().Contain("neutral").And.Contain("not an error");
    }

    [Fact]
    public void An_archive_named_project_without_an_authored_root_is_its_own_lineage()
    {
        Import("cart", 'a', SpecV1, [], at: T0, basis: ProjectNameBasis.ArchiveFileName);
        Import("cart", 'b', SpecV2, [], at: T0.AddDays(1), basis: ProjectNameBasis.ArchiveFileName);
        DocumentationDrift.VersionComparableLineages(DocumentationDrift.Imports(_repository.SddLifecycle)).Should().Be(0, "a ZIP name alone is no proof of lineage");
    }

    // ── Modes and guards ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Modes_follow_identity_import_and_origin()
    {
        TwoImportsOfOneProject();
        var (s1, s2) = (Spec("cart · Import 1"), Spec("cart · Import 2"));
        var (g1, g2) = (Generated("architecture.md", "cart · Import 1"), Generated("architecture.md", "cart · Import 2"));
        DocumentationDrift.AllowedIntents(s1, s2).Should().Contain(DriftIntent.VersionDrift).And.NotContain(DriftIntent.ConsistencyDrift);
        DocumentationDrift.SuggestIntent(s2, g2).Should().Be(DriftIntent.ConsistencyDrift);
        DocumentationDrift.AllowedIntents(s2, g2).Should().Equal(DriftIntent.ConsistencyDrift, DriftIntent.Manual);
        DocumentationDrift.Eligibility(DriftIntent.ConsistencyDrift, s1, g2).Should().NotBeNull("consistency needs the same import");
        DocumentationDrift.SuggestIntent(s1, g2).Should().Be(DriftIntent.Manual);
        DocumentationDrift.SuggestIntent(g2, Generated("README.md", "cart · Import 2")).Should().Be(DriftIntent.Manual, "different documents of one origin are not versions");
        DocumentationDrift.SuggestIntent(g1, g2).Should().Be(DriftIntent.VersionDrift);
        DocumentationDrift.ComparisonType(DriftIntent.ConsistencyDrift).Should().Be("Authored ↔ generated consistency");
    }

    [Fact]
    public void The_same_revision_is_never_compared_with_itself()
    {
        TwoImportsOfOneProject();
        var spec = Spec("cart · Import 2");
        DocumentationDrift.Guard(spec, spec).Should().Be("Select two different document versions or artifacts.");
        DocumentationDrift.Guard(DriftIntent.Manual, spec, spec).Should().Be(DocumentationDrift.SelectDifferent);
        DocumentationDrift.Guard(spec, spec with { Supported = false, Id = "other" }).Should().Be(DocumentationDrift.Unsupported);
    }

    [Fact]
    public void Manual_comparison_reports_document_differences_only()
    {
        Import("cart", 'a', SpecV1, [("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture)], at: T0);
        var (spec, arch) = (Spec("cart · Import 1"), Generated("architecture.md", "cart · Import 1"));
        var result = Compare(spec, arch, DriftIntent.Manual);
        result.Intent.Should().Be(DriftIntent.Manual);
        result.Outcome.Should().StartWith("Document differences");
        result.Items.Should().NotContain(i => i.Kind == nameof(DriftItemKind.ConsistencyDiscrepancy) || i.Kind == nameof(DriftItemKind.Added) || i.Kind == nameof(DriftItemKind.Removed));
        DocumentationDrift.ManualNote.Should().Contain("not assumed to describe equivalent scope");
    }

    [Fact]
    public void Consistency_results_are_neutral_and_never_name_a_winner()
    {
        Import("cart", 'a', SpecV1, [("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture)], at: T0);
        var result = Compare(Spec("cart · Import 1"), Generated("architecture.md", "cart · Import 1"));
        result.Intent.Should().Be(DriftIntent.ConsistencyDrift);
        var text = string.Join(" ", result.Items.Select(i => i.Detail).Append(result.Outcome));
        text.Should().NotContainAny("wrong", "incorrect", "defect", "should be updated");
        result.Items.Where(i => i.Kind == nameof(DriftItemKind.ConsistencyDiscrepancy)).Should().OnlyContain(i => i.Detail.StartsWith(DocumentationDrift.Neutral));
        DocumentationDrift.NeutralReview.Should().Contain("neither side is automatically authoritative");
    }

    // ── Pairing ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_feature_data_model_and_the_module_generated_data_model_are_related_not_equivalent()
    {
        Import("cart", 'a', SpecV1, [("data-model.md", GeneratedModel, GeneratedDocumentKind.DataModel), ("dataflow.md", Dataflow, GeneratedDocumentKind.DataFlow)],
            at: T0, moduleDir: "", authored: [("specs/001-cart/data-model.md", Model)]);
        var catalog = Catalog();
        var importId = _repository.SddLifecycle.CurrentProjectImportId;
        var candidate = DocumentationDrift.Candidates(catalog, [], importId).Should().ContainSingle().Subject;
        candidate.Pairing.Should().Be(DriftPairing.RelatedScopeDiffers, "same module and type are no evidence of equivalent scope");
        candidate.AuthoredScope.Should().Be("Artifact Pack 001-cart");
        candidate.GeneratedScope.Should().Be("Repository");
        candidate.Status.Should().Be("Comparison candidate", "nothing is compared automatically");

        DocumentationDrift.Save(_repository.SddLifecycle, candidate.Authored, candidate.Generated, Compare(candidate.Authored, candidate.Generated), null, T0);
        DocumentationDrift.Candidates(catalog, _repository.SddLifecycle.DocumentationDriftComparisons, importId).Single().Status.Should().Be("Differences found");
    }

    [Fact]
    public void Several_feature_data_models_never_become_several_equivalent_pairs_of_one_generated_model()
    {
        Import("cart", 'a', SpecV1, [("data-model.md", GeneratedModel, GeneratedDocumentKind.DataModel)], at: T0, moduleDir: "Cart",
            authored: [("Cart/specs/001-a/data-model.md", Model), ("Cart/specs/002-b/data-model.md", Model + "\n## B\n"), ("Cart/specs/003-c/data-model.md", Model + "\n## C\n")]);
        var candidates = DocumentationDrift.Candidates(Catalog(), [], _repository.SddLifecycle.CurrentProjectImportId);
        candidates.Should().HaveCount(3).And.OnlyContain(c => c.Pairing == DriftPairing.RelatedScopeDiffers);
        candidates.Select(c => c.Generated.Id).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void Generated_documentation_rooted_at_the_authored_folder_is_an_equivalent_pair()
    {
        Import("cart", 'a', SpecV1, [("data-model.md", GeneratedModel, GeneratedDocumentKind.DataModel)], at: T0, moduleDir: "specs/001-cart",
            authored: [("specs/001-cart/data-model.md", Model)]);
        var candidate = DocumentationDrift.Candidates(Catalog(), [], _repository.SddLifecycle.CurrentProjectImportId).Should().ContainSingle().Subject;
        candidate.Pairing.Should().Be(DriftPairing.Equivalent);
        candidate.Basis.Should().Contain("same scope");
    }

    // ── Saved comparisons ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Saved_comparisons_stay_pinned_to_exact_fingerprints_and_lineage()
    {
        TwoImportsOfOneProject();
        var (left, right) = (Generated("architecture.md", "cart · Import 1"), Generated("architecture.md", "cart · Import 2"));
        var saved = DocumentationDrift.Save(_repository.SddLifecycle, left, right, Compare(left, right), _repository.SessionScope, T0);
        saved.Left.LineageKey.Should().Be("project:cart");
        saved.Right.Fingerprint.Should().Be(Fp(ArchitectureV2));
        saved.Intent.Should().Be(nameof(DriftIntent.VersionDrift));

        Import("cart", 'c', SpecV2, [("architecture.md", ArchitectureV2 + "\n- Another line.\n", GeneratedDocumentKind.Architecture)], at: T0.AddDays(2));
        var restored = new WorkspaceArtifactRepository();
        restored.RestoreSddLifecycle(JsonSerializer.Serialize(_repository.SddLifecycle));
        var reloaded = restored.SddLifecycle.DocumentationDriftComparisons.Single();
        var (l, r) = DocumentationDrift.Resolve(DocumentationDrift.Catalog(restored.SddLifecycle), reloaded);
        l!.ProjectRevision.Should().Be("cart · Import 1");
        r!.ProjectRevision.Should().Be("cart · Import 2", "a later import never retargets an old comparison");
        r.Fingerprint.Should().Be(Fp(ArchitectureV2));
        reloaded.Items.Should().BeEquivalentTo(saved.Items);
    }

    [Fact]
    public void Legacy_saved_comparisons_with_global_revisions_still_load()
    {
        Import("cart", 'a', SpecV1, [("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture)], at: T0);
        var (spec, arch) = (Spec("cart · Import 1"), Generated("architecture.md", "cart · Import 1"));
        var legacy = new SddDocumentationDriftComparison
        {
            Intent = nameof(DriftIntent.AuthoredVsGenerated), Left = DocumentationDrift.Side(spec), Right = DocumentationDrift.Side(arch), Outcome = "1 section(s) differ.",
        };
        legacy.Left.ProjectRevision = "R1"; legacy.Left.LineageKey = null;
        DocumentationDrift.SavedIntent(legacy).Should().Be(DriftIntent.ConsistencyDrift);
        DocumentationDrift.Resolve(Catalog(), legacy).Left.Should().NotBeNull("exact ids and fingerprints still resolve");
        DocumentationDrift.Resolve(Catalog(), new SddDocumentationDriftComparison { Left = new() { DocumentId = "gone", Fingerprint = "x" }, Right = new() }).Left
            .Should().BeNull("an unresolved historical comparison is never reinterpreted");
    }

    [Fact]
    public void A_set_whose_text_was_not_captured_keeps_its_references_but_cannot_be_compared()
    {
        Import("cart", 'a', SpecV1, [("architecture.md", ArchitectureV1, GeneratedDocumentKind.Architecture)], includeText: false, at: T0);
        var generated = Catalog().Single(d => d.Origin == DriftOrigin.Generated);
        DocumentationDrift.Guard(Spec("cart · Import 1"), generated).Should().Contain("not captured");
    }

    [Fact]
    public void Option_text_and_identities_keep_origin_type_path_and_import_apart()
    {
        TwoImportsOfOneProject();
        DocumentationDrift.OptionText(Spec("cart · Import 2")).Should().Be("Authored · Specification · cart/specs/001-cart/spec.md · cart · Import 2 · rev 1");
        var identities = DocumentationDrift.Identities(Catalog());
        identities.Single(i => i.Label == "Authored · Specification · specs/001-cart/spec.md").Versions.Select(v => v.ProjectRevision).Should().Equal("cart · Import 1", "cart · Import 2");
        identities.Single(i => i.Label.EndsWith("autodoc/routes.md")).Versions.Should().ContainSingle("routes.md exists only in Import 2");
        DocumentationDrift.KindLabel(Generated("dataflow.md", "cart · Import 1")).Should().Be("Data Flow");
    }

    [Fact]
    public void Renamed_feature_keeps_its_internal_key_and_route_alias()
    {
        var item = NavigationCatalog.Sections.SelectMany(g => g.Items).Single(i => i.Route == "documentation-drift");
        item.Label.Should().Be("Documentation Drift");
        var routes = typeof(BirkNext.Web.Pages.SpecDrift).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template);
        routes.Should().BeEquivalentTo(["/documentation-drift", "/spec-drift"], "the old route stays as an alias");
    }
}
