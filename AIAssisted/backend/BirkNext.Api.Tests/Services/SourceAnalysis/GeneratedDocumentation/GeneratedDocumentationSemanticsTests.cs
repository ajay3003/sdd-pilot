using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;
using BirkNext.GeneratedDocumentation;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using static BirkNext.Api.Tests.Services.SourceAnalysis.GeneratedDocumentation.GeneratedDocumentationFixture;

namespace BirkNext.Api.Tests.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// Health vs consistency semantics: unresolved correlation is never drift, a review candidate needs two comparable conflicting claims, a missing
/// document needs an established requirement, freshness needs trustworthy same-scope relevant evidence, and stored snapshots are read the same way.
/// </summary>
public sealed class GeneratedDocumentationSemanticsTests
{
    private const string Root = "shop/orders";

    private static GeneratedDocumentationDiagnosticService Diagnostic(BirkNext.Api.Data.AppDbContext db) => new(new IqrSourceStore(db), new MemoryCache(new MemoryCacheOptions()));

    private static List<(string Path, string Content)> WithReadmeKey(string key)
    {
        var files = Orders();
        files[files.FindIndex(f => f.Path.EndsWith("README.md"))] = ($"{Root}/generated-docs/README.md", Readme + $"| `{key}` | Documented key |\n");
        return files;
    }

    // A. CONFIG KEY UNRESOLVED

    [Fact]
    public async Task A_generated_configuration_key_without_a_literal_source_read_is_source_correlation_unresolved()
    {
        using var db = Db();
        var snapshot = await AnalyzeAsync(db, WithReadmeKey("ConnectionStrings:ServiceBus"));
        var candidate = snapshot.GeneratedDocumentation!.Drift.Single(d => d.Family == "Configuration keys");
        candidate.Consistency.Should().Be(GeneratedConsistencyCategory.SourceCorrelationUnresolved);
        candidate.ReviewRequired.Should().BeFalse();
        candidate.Explanation.Should().Contain(GeneratedDocumentationText.UnresolvedConfigurationKey).And.NotContain("Missing in source");
        candidate.WhyReview.Should().Contain("IOptions<T>").And.Contain("Key Vault");
        GeneratedDocumentationText.Label(GeneratedDocumentationSemantics.Classify(candidate)).Should().Be("Source correlation unresolved");
        var comparison = Module(snapshot, Root).Comparisons.Single(c => c.Family == "Configuration keys");
        comparison.State.Should().Be(GeneratedComparisonState.SourceCorrelationUnresolved);
        comparison.Detail.Should().Contain("could not be deterministically correlated with source evidence");
        GeneratedDocumentationText.Label(comparison.State).Should().NotContain("drift");

        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());
        run.Summary.SourceDocumentationDiscrepancies.Should().Be(0, "unresolved correlation is not a review candidate");
        run.Modules.Single(m => m.RootPath == Root).UnresolvedCorrelations.Should().Contain(candidate.Id);
    }

    // B. REAL CONFIG CONFLICT

    [Fact]
    public async Task B_a_generated_key_below_a_key_the_configuration_declares_as_a_value_is_a_potential_inconsistency()
    {
        using var db = Db();
        // appsettings.json declares Orders:PageSize as a single value; the document makes it a section with a child.
        var snapshot = await AnalyzeAsync(db, WithReadmeKey("Orders:PageSize:Max"));
        var candidate = snapshot.GeneratedDocumentation!.Drift.Single(d => d.Family == "Configuration keys");
        candidate.DifferenceKind.Should().Be(DriftDifferenceKind.StructureMismatch);
        candidate.Consistency.Should().Be(GeneratedConsistencyCategory.PotentialInconsistency);
        candidate.ReviewRequired.Should().BeTrue();
        Module(snapshot, Root).Comparisons.Single(c => c.Family == "Configuration keys").State.Should().Be(GeneratedComparisonState.PotentialInconsistency);
        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());
        run.Summary.SourceDocumentationDiscrepancies.Should().Be(1);
        var health = run.Modules.Single(m => m.RootPath == Root);
        health.SourceDiscrepancies.Should().ContainSingle();
        health.Review.Should().Be(GeneratedDocumentationReviewState.ReviewSuggested);
        health.ReviewReasons.Should().Contain(r => r.Contains("comparable generated and source claims differ"));
        health.Status.Should().NotBe(GeneratedDocumentationHealthStatus.Warning, "consistency is kept out of health status");
    }

    // C/D. REQUIRED vs GENERATOR CAPABILITY (C is covered in GeneratedDocumentationEvidenceTests)

    [Fact]
    public async Task D_a_document_the_workflow_declares_required_and_absent_is_required_and_missing()
    {
        using var db = Db();
        var files = Orders();
        files.RemoveAll(f => f.Path.StartsWith(".claude/"));
        files.Add((".claude/skills/doc-writer/SKILL.md", Workflow + "\n\nThe file generated-docs/message-bus.md is required for every module.\n"));
        var snapshot = await AnalyzeAsync(db, files);
        snapshot.GeneratedDocumentation!.Generators.Single(g => g.Path.EndsWith("SKILL.md")).RequiredOutputFiles.Should().Equal(["message-bus.md"]);
        var module = Module(snapshot, Root);
        module.ExpectedDocuments.Single(e => e.FileName == "message-bus.md").State.Should().Be(ExpectedDocumentState.RequiredMissing);
        module.MissingExpectedDocs.Should().Equal(["message-bus.md"]);

        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());
        run.Summary.MissingExpectedDocs.Should().Be(1);
        var health = run.Modules.Single(m => m.RootPath == Root);
        health.Status.Should().Be(GeneratedDocumentationHealthStatus.Warning);
        health.Review.Should().Be(GeneratedDocumentationReviewState.ReviewRequired);
        health.ReviewReasons.Should().Contain(r => r.Contains("message-bus.md") && r.Contains("Declared required by"));
    }

    [Fact]
    public void E_source_capability_without_a_profile_rule_is_applicability_not_established()
    {
        var document = new ExpectedGeneratedDocument("message-bus.md", GeneratedDocumentKind.Messaging, false, true, "Declared by SKILL.md; module shows Messaging");
        var generator = new GeneratorWorkflowEvidence { Id = "workflow:a", Path = ".claude/skills/a/SKILL.md", DeclaredOutputFiles = ["message-bus.md"] };
        var unestablished = GeneratedDocumentationSemantics.Expected(document, [generator], ["Messaging"]);
        unestablished.State.Should().Be(ExpectedDocumentState.ApplicabilityNotEstablished);
        GeneratedDocumentationSemantics.CountsAsMissing(unestablished).Should().BeFalse();

        // A profile rule mapping the capability to the document + the capability's evidence → expected by profile (counts when absent).
        var profile = generator with { ConditionalOutputFiles = ["message-bus.md|Messaging"] };
        var expected = GeneratedDocumentationSemantics.Expected(document, [profile], ["Messaging"]);
        expected.State.Should().Be(ExpectedDocumentState.ExpectedByProfile);
        GeneratedDocumentationSemantics.CountsAsMissing(expected).Should().BeTrue();
        // Same rule, no capability evidence → not applicable.
        GeneratedDocumentationSemantics.Expected(document, [profile], ["Source"]).State.Should().Be(ExpectedDocumentState.NotApplicable);
    }

    [Theory]
    [InlineData("If the module uses Service Bus, write generated-docs/message-bus.md.", false, "Messaging")]
    [InlineData("Always write generated-docs/README.md.", true, null)]
    [InlineData("### Write generated-docs/message-bus.md", false, null)]
    [InlineData("Optionally write generated-docs/schema.graphql when the module exposes GraphQL.", false, "GraphQL")]
    public void Workflow_lines_declare_requirement_or_capability_mapping_only_explicitly(string line, bool required, string? capability) =>
        GeneratedDocumentationDetector.OutputRule(line).Should().Be((required, capability));

    // F/G/H/I/J. FRESHNESS

    [Fact]
    public async Task F_generation_date_without_relevant_dated_source_evidence_is_freshness_unknown_and_neutral()
    {
        using var db = Db();
        var snapshot = await AnalyzeAsync(db, Orders());
        var readme = Doc(snapshot, $"{Root}/generated-docs/README.md");
        readme.FreshnessStatus.Should().Be(GeneratedDocumentationFreshnessStatus.NotEnoughEvidence);
        readme.FreshnessReason.Should().Contain("Not stale — not established");
        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());
        var health = run.Modules.Single(m => m.RootPath == Root);
        health.Freshness.Should().Be(GeneratedDocumentationFreshnessStatus.NotEnoughEvidence);
        health.FreshnessReason.Should().StartWith("Freshness could not be established from trustworthy evidence");
        // M. Unknown freshness alone never creates a review state.
        health.Review.Should().Be(GeneratedDocumentationReviewState.NoReviewSuggested);
        health.Status.Should().Be(GeneratedDocumentationHealthStatus.Partial);
        run.Summary.FreshnessUnknown.Should().Be(1);
    }

    [Fact]
    public async Task G_same_scope_relevant_source_evidence_newer_than_the_generation_date_is_potentially_stale()
    {
        using var db = Db();
        var snapshot = await AnalyzeAsync(db, Orders(generatedDate: "2026-03-01", migrationId: "20260415090000"));
        Doc(snapshot, $"{Root}/generated-docs/data-model.md").FreshnessStatus.Should().Be(GeneratedDocumentationFreshnessStatus.Stale);
        // Relevance is per document type: the migration never dates the README.
        Doc(snapshot, $"{Root}/generated-docs/README.md").FreshnessStatus.Should().Be(GeneratedDocumentationFreshnessStatus.NotEnoughEvidence);
        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest());
        var health = run.Modules.Single(m => m.RootPath == Root);
        health.Freshness.Should().Be(GeneratedDocumentationFreshnessStatus.Stale);
        health.Review.Should().Be(GeneratedDocumentationReviewState.ReviewSuggested);
    }

    [Fact]
    public async Task H_I_newer_archive_times_and_another_modules_source_never_make_a_document_stale()
    {
        using var db = Db();
        var files = Orders(generatedDate: "2026-03-01");
        files.AddRange([
            ("shop/billing/src/Shop.Billing.Api/Shop.Billing.Api.csproj", Csproj),
            ("shop/billing/src/Shop.Billing.Api/Program.cs", Program("/invoices")),
            ("shop/billing/src/Shop.Billing.Api/Migrations/20260420100000_AddInvoices.cs", Migration),
        ]);
        // Every orders source file has an archive time later than the declared generation date.
        var snapshot = await AnalyzeAsync(db, files, path => path.Contains("generated-docs") ? new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(2026, 4, 20, 10, path.Length % 50, 0, TimeSpan.Zero));
        snapshot.GeneratedDocumentation!.ArchiveTimestampsReliable.Should().BeTrue();
        var orders = Module(snapshot, Root);
        orders.Freshness.Should().NotBe(GeneratedDocumentationFreshnessStatus.Stale, "archive (ZIP/copy) times are not freshness evidence and billing's migration is another scope");
        snapshot.GeneratedDocumentation.Documents.Where(d => d.ModuleId == orders.ModuleId).Should().NotContain(d => d.FreshnessStatus == GeneratedDocumentationFreshnessStatus.Stale);
        snapshot.GeneratedDocumentation.Documents.Where(d => d.SourceLastModified != null).Should().NotContain(d => d.SourceLastModified!.Basis == GeneratedDocumentationSemantics.ArchiveTimeBasis);
    }

    [Fact]
    public async Task J_a_changelog_is_a_history_record_with_freshness_not_applicable()
    {
        using var db = Db();
        var files = Orders(migrationId: "20260415090000");
        files.Add(($"{Root}/generated-docs/CHANGELOG.md", "# Changelog\n\n## [1.2.0] - 2026-02-01\n### Added\n- Orders\n"));
        var snapshot = await AnalyzeAsync(db, files);
        Doc(snapshot, $"{Root}/generated-docs/CHANGELOG.md").FreshnessStatus.Should().Be(GeneratedDocumentationFreshnessStatus.NotApplicable);
    }

    // L. CROSS-ARTIFACT CONSISTENCY

    [Fact]
    public async Task L_conflicting_authored_and_generated_claims_are_a_potential_inconsistency_and_an_unpaired_claim_is_unresolved()
    {
        using var db = Db();
        var files = Orders();
        files[files.FindIndex(f => f.Path.EndsWith("architecture.md"))] = ($"{Root}/generated-docs/architecture.md", Architecture + "\nRead models are cached in MongoDB.\n");
        await AnalyzeAsync(db, files, importId: "import-a");
        var constitution = Authored("Constitution", $"{Root}/.specify/memory/constitution.md", "# Constitution\n\nServices MUST NOT use MongoDB.\n");
        var spec = Authored("Specification", $"{Root}/specs/001-orders/spec.md", "# Spec\n\n- **FR-001**: System MUST expose GET /payments.\n");
        var run = await Diagnostic(db).RunAsync(new GeneratedDocumentationDiagnosticRequest { ProjectImportId = "import-a", AuthoredArtifacts = [constitution, spec] });
        var conflict = run.Drift.Single(d => d.DriftType == CrossArtifactDriftType.ConstitutionDrift);
        conflict.Consistency.Should().Be(GeneratedConsistencyCategory.PotentialInconsistency);
        var unpaired = run.Drift.Single(d => d.DriftType == CrossArtifactDriftType.SpecificationDrift && d.Family == "Specified routes");
        unpaired.Consistency.Should().Be(GeneratedConsistencyCategory.SourceCorrelationUnresolved);
        GeneratedDocumentationText.Label(unpaired.Consistency!.Value, unpaired.DriftType).Should().Be("Correlation unresolved");
        run.Summary.AuthoredGeneratedCandidates.Should().Be(1);
        run.Summary.CrossArtifactDriftCandidates.Should().Be(1);
        var health = run.Modules.Single(m => m.RootPath == Root);
        health.CrossArtifactDrift.Should().Equal([conflict.Id]);
        health.UnresolvedCorrelations.Should().Contain(unpaired.Id);
    }

    // Stored snapshots analyzed under the earlier rules read with the same semantics.

    [Fact]
    public void A_legacy_snapshot_is_reinterpreted_without_changing_inventory_or_identity()
    {
        var archiveSource = new DatedEvidence(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), GeneratedDocumentationSemantics.ArchiveTimeBasis, "m/src/a.cs", "Second");
        var doc = new GeneratedDocumentationEvidence
        {
            EvidenceId = "gendoc:m/docs/README.md", ModuleId = "module:m", SafeRelativePath = "m/docs/README.md", Origin = DocumentationOrigin.Generated,
            LastModified = new DatedEvidence(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), "Declared in the document (line 1)", "m/docs/README.md", "Day"),
            SourceLastModified = archiveSource, FreshnessStatus = GeneratedDocumentationFreshnessStatus.Stale, FreshnessReason = "Archive modification time 2026-04-01 is newer.",
            ContentFingerprint = "abc",
        };
        var candidate = new CrossArtifactDriftCandidate { Id = "drift:1", ModuleId = "module:m", Family = "Configuration keys", DriftType = CrossArtifactDriftType.DocumentationDrift, DifferenceKind = DriftDifferenceKind.MissingOnRight, StructuredKey = "ConnectionStrings:ServiceBus" };
        var legacy = new GeneratedDocumentationSnapshot
        {
            Generators = [new GeneratorWorkflowEvidence { Id = "workflow:x", Path = "x/SKILL.md", DeclaredOutputFiles = ["message-bus.md"] }],
            Documents = [doc], Drift = [candidate],
            Modules = [new GeneratedDocumentationModule
            {
                ModuleId = "module:m", RootPath = "m", DocumentIds = [doc.EvidenceId], GeneratorIds = ["workflow:x"], Freshness = GeneratedDocumentationFreshnessStatus.Stale,
                LatestSourceEvidence = archiveSource, ExpectedDocuments = [new("message-bus.md", GeneratedDocumentKind.Messaging, false, true, "Declared by x/SKILL.md")], MissingExpectedDocs = ["message-bus.md"],
                Comparisons = [new("Configuration keys", GeneratedComparisonState.PotentiallyDrifted, "1 documented key(s).", 1)],
            }],
        };
        var read = GeneratedDocumentationSemantics.Reinterpret(legacy);
        read.Documents.Single().FreshnessStatus.Should().Be(GeneratedDocumentationFreshnessStatus.NotEnoughEvidence);
        read.Documents.Single().EvidenceId.Should().Be(doc.EvidenceId);
        read.Documents.Single().ContentFingerprint.Should().Be("abc");
        var module = read.Modules.Single();
        module.Freshness.Should().Be(GeneratedDocumentationFreshnessStatus.NotEnoughEvidence);
        module.LatestSourceEvidence.Should().BeNull();
        module.SupportingFreshnessSignals.Should().ContainSingle(s => s.Contains("supporting only"));
        module.MissingExpectedDocs.Should().BeEmpty();
        module.Comparisons.Single().State.Should().Be(GeneratedComparisonState.SourceCorrelationUnresolved);
        read.Drift.Single().ReviewRequired.Should().BeFalse();
        // Idempotent.
        GeneratedDocumentationSemantics.Reinterpret(read).Modules.Single().SupportingFreshnessSignals.Should().HaveCount(1);
    }
}
