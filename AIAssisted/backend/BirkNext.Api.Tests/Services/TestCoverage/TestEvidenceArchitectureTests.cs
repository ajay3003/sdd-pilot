using BirkNext.Api.Services.TestCoverage;
using BirkNext.Delivery;
using BirkNext.Integrations;
using BirkNext.PipelineReview;
using BirkNext.TestCoverage;
using BirkNext.TestEvidence;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestCoverage;

/// <summary>
/// Shared Test Evidence: one canonical test identity used by Test Coverage, Source Analysis, pipeline delivery evidence and Impact Analysis;
/// executions matched by identity with explicit context (local ≠ QA), execution-only results preserved, requirement links keep their
/// confidence, overlap separates layered coverage from potential redundancy, unsupported frameworks are limitations — never zero tests.
/// </summary>
public sealed class TestEvidenceArchitectureTests : IDisposable
{
    private readonly BirkNext.Api.Data.AppDbContext _db = TestCoverageFixture.Db();
    public void Dispose() => _db.Dispose();

    private static SourceTestDefinition Definition(string project, string cls, string method, TestKind kind = TestKind.Unit, params SourceTestReference[] refs) => new()
    {
        TestDefinitionId = $"id-{cls}.{method}", StableIdentity = $"repo::{project}::{cls}.{method}", Project = project, FilePath = $"tests/{project}/{cls}.cs", Line = 10,
        ClassName = cls, MethodName = method, FullyQualifiedName = $"{cls}.{method}", Kind = kind, References = [.. refs],
    };

    private static SourceTestInventory Inventory(params SourceTestDefinition[] definitions) => new()
    {
        SnapshotId = Guid.NewGuid(), Status = SourceTestDiscoveryStatus.Partial, Definitions = [.. definitions],
        Projects = [new SourceTestProject { Name = "Orders.UnitTests", Path = "tests/Orders.UnitTests/Orders.UnitTests.csproj", Framework = "xUnit", DiscoverySupported = true, DefinitionCount = definitions.Length },
            new SourceTestProject { Name = "Legacy.Tests", Path = "tests/Legacy.Tests/Legacy.Tests.csproj", Framework = "NUnit", DiscoverySupported = false }],
    };

    [Fact]
    public void Canonical_tests_keep_identity_layer_and_link_confidence_and_unsupported_frameworks_are_not_zero()
    {
        var inv = Inventory(
            Definition("Orders.UnitTests", "Orders.CreateTests", "Rejects_duplicate", TestKind.Unit, new SourceTestReference("FR-002", TestReferenceKind.Requirement, TestEvidenceConfidence.Confirmed, "Trait", 9)),
            Definition("Orders.UnitTests", "Orders.CreateTests", "Maybe_related", TestKind.Unit, new SourceTestReference("FR-009", TestReferenceKind.Requirement, TestEvidenceConfidence.Inferred, "Comment", 20)));
        var tests = TestEvidenceQueries.FromInventory(inv);
        tests.Select(t => t.Id).Should().Equal("repo::Orders.UnitTests::Orders.CreateTests.Rejects_duplicate", "repo::Orders.UnitTests::Orders.CreateTests.Maybe_related");
        tests[0].Requirements.Single().Confidence.Should().Be(TestMappingConfidence.Explicit);
        tests[1].Requirements.Single().Confidence.Should().Be(TestMappingConfidence.Candidate, "an inferred reference is a candidate, never confirmed coverage");
        TestEvidenceQueries.ForRequirement(tests, "FR-009").Should().BeEmpty();
        TestEvidenceQueries.ForRequirement(tests, "FR-009", includeCandidates: true).Should().ContainSingle();
        tests.Should().OnlyContain(t => t.State == "Discovered in source");
        TestEvidenceQueries.Limitations(inv).Should().Contain(l => l.Contains("Legacy.Tests") && l.Contains("not zero tests"));
        TestEvidenceQueries.Limitations(null).Single().Should().Contain("not assessed (not zero)");
    }

    [Fact]
    public void Executions_match_by_identity_keep_context_and_preserve_execution_only_results()
    {
        var tests = TestEvidenceQueries.FromInventory(Inventory(Definition("Orders.UnitTests", "Orders.CreateTests", "Rejects_duplicate")));
        var id = tests[0].Id;
        TestExecutionRecord Run(string key, string result, string env, int day) => new(key, key, result, DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddDays(day), env, env, null, null, "TRX");
        var matches = TestEvidenceQueries.Match([Run(id, "Failed", "Local", 1), Run(id, "Passed", "Local", 3), Run(id, "Failed", "QA", 2), Run("Unknown.Test.Method", "Passed", "QA", 2)], tests);
        matches.Count(m => m.Test is null).Should().Be(1, "an execution matching no discovered test is kept as execution-only");
        matches.Single(m => m.Test is null).Basis.Should().StartWith("Execution-only");
        TestEvidenceQueries.LatestRelevant(matches, id, "Local")!.Result.Should().Be("Passed", "the most recent local run wins");
        TestEvidenceQueries.LatestRelevant(matches, id, "QA")!.Result.Should().Be("Failed", "a passing local run never answers for QA");
        TestEvidenceQueries.LatestRelevant(matches, id, "Production").Should().BeNull();
        TestEvidenceQueries.ResultText(null).Should().Be(TestEvidenceQueries.NotExecuted);
        TestEvidenceQueries.ResultText(Run(id, "Passed", "QA", 1) with { Negative = true }).Should().Be("Passed (expected rejection observed)");
        var dims = TestEvidenceQueries.Coverage(null, tests, new HashSet<string>(["Orders.UnitTests"]), matches, "QA");
        (dims.Discovered, dims.ConfiguredInCi, dims.ExecutedInContext, dims.PassedInContext, dims.ExecutionOnly).Should().Be((1, 1, 1, 0, 1));
    }

    [Fact]
    public void Overlap_separates_layered_coverage_from_potential_redundancy_and_names_alone_are_never_compared()
    {
        var req = new SourceTestReference("FR-002", TestReferenceKind.Requirement, TestEvidenceConfidence.Confirmed, "Trait", 1);
        var t = TestEvidenceQueries.FromInventory(Inventory(
            Definition("Orders.UnitTests", "A", "Create_rejects_duplicate_order", TestKind.Unit, req),
            Definition("Orders.IntegrationTests", "B", "Create_rejects_duplicate_order", TestKind.Integration, req),
            Definition("Orders.UnitTests", "C", "Create_rejects_duplicate_order_again", TestKind.Unit, req),
            Definition("Orders.UnitTests", "D", "Create_rejects_duplicate_order")));
        TestEvidenceQueries.Overlap(t[0], t[1]).Class.Should().Be(TestOverlapClass.Complementary);
        TestEvidenceQueries.Overlap(t[0], t[2]).Class.Should().Be(TestOverlapClass.PotentialRedundancy);
        TestEvidenceQueries.Overlap(t[0], t[3]).Class.Should().Be(TestOverlapClass.NotComparable, "identical names without a shared explicit requirement are not duplicates");
    }

    [Fact]
    public void Impacted_tests_by_contract_package_and_component_and_pipeline_removal_is_visible()
    {
        var t = TestEvidenceQueries.FromInventory(Inventory(
            Definition("Orders.ContractTests", "Schema", "Accepts_valid_report"), Definition("Orders.Api.UnitTests", "Api", "Returns_404"), Definition("Billing.UnitTests", "Bill", "Totals")));
        var impacted = TestEvidenceQueries.Impacted(t, ["Orders.Api"], ["tests/Orders.ContractTests/Schema.cs"], ["tests/Billing.UnitTests/Billing.UnitTests.csproj"]);
        impacted.Should().Contain(i => i.Test.MethodName == "Accepts_valid_report" && i.Confidence == "Direct");
        impacted.Should().Contain(i => i.Test.MethodName == "Totals" && i.Confidence == "Strong");
        impacted.Should().Contain(i => i.Test.MethodName == "Returns_404" && i.Confidence == "Candidate");
        TestEvidenceQueries.NoLongerConfigured(t, new HashSet<string>(["Billing.UnitTests", "Orders.Api.UnitTests"]), new HashSet<string>(["Orders.Api.UnitTests"]))
            .Should().ContainSingle(x => x.Project == "Billing.UnitTests", "the test still exists in source but no pipeline runs its project any more");
    }

    [Fact]
    public void Pipeline_test_steps_reference_the_canonical_tests_of_the_projects_they_run()
    {
        var where = new DeliveryProvenance("p.yml", 3, "p.yml", "Build", "test", null, "azure-pipelines-yaml");
        var delivery = new DeliveryEvidence { Tests = [new("Unit tests", ValidationCategory.Unit, "p.yml", "Build", "test", ["tests/Orders.UnitTests/Orders.UnitTests.csproj"], [], false, false, null, "Does not gate a deployment")] };
        var linked = DeliveryEvidenceQueries.WithCanonicalTests(delivery, Inventory(Definition("Orders.UnitTests", "A", "One"), Definition("Orders.UnitTests", "A", "Two")));
        linked.Tests.Single().Should().Match<DeliveryTestDefinition>(x => x.TestProjects.Single() == "Orders.UnitTests" && x.CanonicalTests == 2 && x.Execution.StartsWith("Configured"));
        _ = where;
    }

    [Fact]
    public async Task Test_coverage_review_matches_imported_results_by_stable_identity_keeps_the_latest_and_preserves_execution_only()
    {
        var snapshot = await TestCoverageFixture.AnalyzeAsync(_db, TestCoverageFixture.Current());
        var definition = snapshot.TestInventory!.Definitions.First(d => d.MethodName == "Post_DuplicateOrder_Returns422");
        var workspace = TestCoverageFixture.Workspace(
            new CoverageExecution(definition.StableIdentity, definition.FullyQualifiedName, "Failed", "QA", DateTimeOffset.Parse("2026-10-01T00:00:00Z")),
            new CoverageExecution(definition.StableIdentity, definition.FullyQualifiedName, "Passed", "QA", DateTimeOffset.Parse("2026-10-02T00:00:00Z")),
            new CoverageExecution("", "Some.Removed.Test", "Passed", "QA", DateTimeOffset.Parse("2026-10-02T00:00:00Z")));
        var r = TestCoverageReviewEngine.Review(snapshot, null, new TestCoverageReviewRequest { CurrentSnapshotId = snapshot.Id, Workspace = workspace }, [], [], DateTimeOffset.UtcNow);
        r.Tests.Single(t => t.Name.EndsWith(".Post_DuplicateOrder_Returns422")).Execution.Should().Be(TestExecutionState.Passed, "the canonical stable identity matches and the latest result wins");
        r.ExecutionOnly.Should().ContainSingle(e => e.TestName == "Some.Removed.Test" && e.Environment == "QA");
        r.Dimensions!.Context.Should().Be("QA");
        r.Dimensions.ExecutionOnly.Should().Be(1);
        r.Tests.GroupBy(t => (t.FilePath, t.Name)).Should().OnlyContain(g => g.Count() == 1, "a fact and its discovered definition are one test");
    }
}
