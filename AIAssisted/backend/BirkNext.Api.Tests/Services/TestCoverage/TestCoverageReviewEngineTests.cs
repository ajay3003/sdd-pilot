using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestCoverage;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.TestCoverage;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestCoverage;

/// <summary>
/// Test Coverage &amp; Overlap Review over real Source Analysis snapshots of a generic (non-M2LB) project: per-test facts captured at upload,
/// evidence-driven test levels, discovery ≠ execution, mocks ≠ real integrations, journeys from source evidence with honest step coverage,
/// conservative overlaps (same requirement alone is never a duplicate; a unit test never replaces a journey test), gaps and readable scope.
/// </summary>
public sealed class TestCoverageReviewEngineTests : IDisposable
{
    private readonly BirkNext.Api.Data.AppDbContext _db = TestCoverageFixture.Db();
    public void Dispose() => _db.Dispose();

    private Task<IqrSourceSnapshot> CurrentAsync() => TestCoverageFixture.AnalyzeAsync(_db, TestCoverageFixture.Current());

    private static TestCoverageReviewResult Review(IqrSourceSnapshot current, IqrSourceSnapshot? baseline = null, CoverageWorkspaceEvidence? workspace = null,
        IReadOnlyList<CoverageDecision>? decisions = null, IReadOnlyList<E2EFlowEvidence>? flows = null) =>
        TestCoverageReviewEngine.Review(current, baseline, new TestCoverageReviewRequest { CurrentSnapshotId = current.Id, BaselineSnapshotId = baseline?.Id, Workspace = workspace ?? TestCoverageFixture.Workspace() },
            decisions ?? [], flows ?? [], DateTimeOffset.UtcNow);

    private static CoverageTest Test(TestCoverageReviewResult r, string method) => r.Tests.Single(t => t.Name.EndsWith("." + method, StringComparison.Ordinal));

    [Fact]
    public async Task Source_analysis_captures_per_test_facts_once_at_upload()
    {
        var snapshot = await CurrentAsync();
        var facts = snapshot.TestBehaviorEvidence!;
        var unit = facts.Tests.Single(t => t.MethodName == "Create_DuplicateOrder_ThrowsDuplicateOrderException");
        unit.Signals.Should().Contain(s => s.Kind == BoundaryKind.Database && s.Mode == BoundaryMode.Mocked && s.Detail == "IOrderRepository");
        unit.Assertions.Should().ContainSingle(a => a.Kind == TestAssertionKind.ExceptionThrown && a.Detail == "DuplicateOrderException");
        unit.RequirementReferences.Should().Equal("FR-002");
        unit.TargetProjects.Should().Equal("Orders.Api");
        unit.ProductionProjects.Should().Equal("Orders.Api");
        unit.InInventory.Should().BeTrue("the source test inventory id is reused");

        var api = facts.Tests.Single(t => t.MethodName == "Post_DuplicateOrder_Returns422");
        api.Signals.Should().Contain(s => s.Kind == BoundaryKind.Http && s.Mode == BoundaryMode.InProcess && s.Scope == "class");
        api.ExpectedOutcomes.Should().Equal("422");
        facts.Tests.Single(t => t.MethodName == "Save_DuplicateOrder_StoresOneRecord").Signals.Should().Contain(s => s.Kind == BoundaryKind.Database && s.Mode == BoundaryMode.InMemory);
        facts.Tests.Single(t => t.MethodName == "Publish_OrderCreated_SendsMessage").Assertions.Should().Contain(a => a.Kind == TestAssertionKind.MessagePublished);
        facts.Tests.Single(t => t.MethodName == "Create_LargeOrder_IsAccepted").Skipped.Should().BeTrue();
        facts.Unsupported.Select(u => u.Language).Should().BeEquivalentTo(["TypeScript", "Python"]);
        JsonSerializer.Serialize(facts).Should().NotContain("\"A1\"", "literal test values are never recorded");
    }

    [Fact]
    public async Task Test_levels_come_from_setup_evidence_and_names_alone_never_make_integration()
    {
        var r = Review(await CurrentAsync());
        Test(r, "Create_DuplicateOrder_ThrowsDuplicateOrderException").Level.Should().Be(TestLevel.Unit);
        Test(r, "Post_DuplicateOrder_Returns422").Should().Match<CoverageTest>(t => t.Level == TestLevel.Api && t.LevelEvidence == EvidenceStrength.Strong);
        Test(r, "Save_DuplicateOrder_StoresOneRecord").Should().Match<CoverageTest>(t => t.Level == TestLevel.Repository && t.LevelBasis.Contains("not the deployed database"));
        Test(r, "Legacy_Orders_Work").Should().Match<CoverageTest>(t => t.Level == TestLevel.NeedsReview && t.LevelEvidence == EvidenceStrength.Weak,
            "a project named IntegrationTests without setup evidence is not an integration test");
        Test(r, "Create_Order_DoesSomething").Strength.Should().Be(EvidenceStrength.NotEnough, "no assertion was recognised");
        Test(r, "Create_DuplicateOrder_ThrowsDuplicateOrderException").Purpose.Should().StartWith("Test appears to check").And.Contain("replaces the database with a mock").And.Contain("expects DuplicateOrderException");
    }

    [Fact]
    public async Task Discovered_tests_are_never_executed_or_passed_without_imported_results()
    {
        var snapshot = await CurrentAsync();
        var none = Review(snapshot);
        none.Tests.Should().OnlyContain(t => t.Execution == TestExecutionState.NotVerified);
        none.Inventory.Should().Match<CoverageInventory>(i => i.TestsWithExecutionEvidence == 0 && i.Passed == 0 && i.ExecutionEvidence == TestCoverageText.NoExecution);
        none.Inventory.CodeCoverage.Should().Be(TestCoverageText.NoPercentage);

        var apiId = Test(none, "Post_DuplicateOrder_Returns422").TestId;
        var some = Review(snapshot, workspace: TestCoverageFixture.Workspace(new CoverageExecution(apiId, "", "Passed")));
        some.Tests.Single(t => t.Execution == TestExecutionState.Passed).TestId.Should().Be(apiId);
        some.Inventory.Should().Match<CoverageInventory>(i => i.TestsDiscovered == none.Inventory.TestsDiscovered && i.TestsWithExecutionEvidence == 1 && i.Passed == 1);
        Test(some, "Post_DuplicateOrder_Returns422").Strength.Should().Be(EvidenceStrength.Strong);
        Test(some, "Create_DuplicateOrder_ThrowsDuplicateOrderException").Strength.Should().Be(EvidenceStrength.Some, "analyzed but not executed");
    }

    [Fact]
    public async Task Journeys_come_from_source_evidence_and_mocks_never_count_as_real_integration()
    {
        var r = Review(await CurrentAsync());
        var steps = r.Journeys.SelectMany(j => j.Steps).DistinctBy(s => s.Id).ToList();
        var inbound = steps.Single(s => s.From.StartsWith("upstream:") && s.ToName == "orders-inbound");
        inbound.Should().Match<JourneyStep>(s => s.Evidence == ConnectionEvidence.NeedsConfirmation && s.Developer == StepCoverage.NotVerified);
        var call = steps.Single(s => s.FromName == "Orders.Adapter" && s.ToName == "Orders.Api");
        call.Should().Match<JourneyStep>(s => s.Boundary == BoundaryKind.Http && s.Evidence == ConnectionEvidence.StronglySupported && s.Developer == StepCoverage.MockOnly);
        call.Provenance.Should().Contain(p => p.Contains("OrdersApi:BaseUrl"));
        steps.Single(s => s.FromName == "Orders.Api" && s.Boundary == BoundaryKind.Database).Developer.Should().Be(StepCoverage.PartlyCovered, "the database is only in-memory in tests");
        steps.Single(s => s.FromName == "Orders.Api" && s.ToName == "order-events").Developer.Should().Be(StepCoverage.MockOnly);
        steps.Should().OnlyContain(s => s.Runtime == "Not verified");
        r.Journeys.Should().Contain(j => j.Title.Contains("orders-inbound → Orders.Adapter → Orders.Api"));
        r.Journeys.SelectMany(j => j.FocusForTesters).Should().Contain(f => f.Contains("A real message arriving on orders-inbound"));
    }

    [Fact]
    public async Task Overlaps_are_conservative_and_a_unit_test_never_replaces_a_journey_test()
    {
        var r = Review(await CurrentAsync());
        r.Overlaps.Single(o => o.QaTestId == "AS-1").Should().Match<TestOverlap>(o => o.Kind == OverlapKind.HighConfidenceOverlap && o.Dimensions.SameLevel && o.Dimensions.SameOutcome && o.Dimensions.SameRequirement);
        r.Overlaps.Single(o => o.QaTestId == "AS-1").Recommendation.Should().StartWith("Probably does not need to be repeated at the same test level");
        r.Overlaps.Single(o => o.QaTestId == "AS-2").Kind.Should().Be(OverlapKind.DifferentBoundary, "developer tests do not reach the event flow the QA scenario runs through");
        r.Overlaps.Single(o => o.QaTestId == "AS-3").Kind.Should().Be(OverlapKind.ComplementaryCoverage, "the same requirement alone is never a duplicate");
        r.Overlaps.Should().NotContain(o => o.QaTestId == "AS-4", "no developer test relates to the confirmation email");
        r.QaScope.ProbablyUnnecessary.Should().Contain(s => s.Contains("Duplicate order is rejected by the API"));
        r.QaScope.ProbablyUnnecessary.Should().NotContain(s => s.Contains("inbound flow"));
        r.QaScope.ProbablyUnnecessary.Should().NotContain(s => s.Contains("FR-002"), "a requirement-id link alone never justifies not repeating a test");
        r.QaScope.ProbablyUnnecessary.Where(s => s.StartsWith("Repeating the isolated logic")).Should().OnlyContain(s => s.Contains("execution not verified"));
        r.QaScope.Keep.Should().Contain(k => k.Contains("orders-inbound"));
    }

    [Fact]
    public async Task Unanalyzed_tests_give_insufficient_evidence_never_a_strong_overlap()
    {
        var old = (await CurrentAsync()) with { TestBehaviorEvidence = null };
        var r = Review(old);
        r.Partial.Should().BeTrue();
        r.Tests.Should().OnlyContain(t => !t.Analyzed && t.Strength == EvidenceStrength.Weak);
        r.Overlaps.Should().NotBeEmpty().And.OnlyContain(o => o.Kind == OverlapKind.InsufficientEvidence);
        r.Limitations.Should().Contain(l => l.Contains("before per-test facts existed"));
    }

    [Fact]
    public async Task Gaps_are_evidence_worded_and_change_aware()
    {
        var baseline = await TestCoverageFixture.AnalyzeAsync(_db, TestCoverageFixture.Baseline());
        var current = await CurrentAsync();
        var r = Review(current, baseline);
        r.Gaps.Should().Contain(g => g.Kind == GapKind.ChangedWithoutTestChange && g.Title == "Implementation changed in Orders.Api, but no related test change was found");
        r.Gaps.Should().Contain(g => g.Kind == GapKind.MockOnly && g.Title.Contains("Orders.Api"));
        r.Gaps.Should().Contain(g => g.Kind == GapKind.SkippedTests);
        r.Gaps.Should().Contain(g => g.Kind == GapKind.NoTestEvidence && g.Title == "No test evidence found for Orders.Notifier" && g.Explanation.Contains("does not prove that no test exists"));
        r.Gaps.Should().Contain(g => g.Kind == GapKind.RequirementWithoutTests && g.Subjects.Single() == "FR-009");
        r.Gaps.Should().Contain(g => g.Kind == GapKind.UnsupportedTests && g.Title.Contains("TypeScript"));
        r.Gaps.Select(g => g.Title + g.Explanation).Should().NotContain(t => t.Contains("untested", StringComparison.OrdinalIgnoreCase));
        r.Behaviors.Single(b => b.Id == "req:FR-002").Recommendation.Should().Be(CoverageRecommendationKind.ChangedSinceTest);
        Review(current).Gaps.Should().NotContain(g => g.Kind == GapKind.ChangedWithoutTestChange, "without a baseline there is no change review");
    }

    [Fact]
    public async Task A_requirement_id_used_by_tests_in_several_modules_is_ambiguous_and_never_links()
    {
        var bytes = TestCoverageFixture.CurrentWith(
            ("Billing/tests/Billing.Tests/Billing.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.0" /><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" /></ItemGroup></Project>"""),
            ("Billing/tests/Billing.Tests/InvoiceTests.cs", """
                public sealed class InvoiceTests
                {
                    [Fact]
                    [Trait("Requirement", "FR-002")]
                    public void Invoice_Duplicate_IsRejected() { Assert.Throws<InvalidOperationException>(() => throw new InvalidOperationException()); }
                }
                """));
        var r = Review(await TestCoverageFixture.AnalyzeAsync(_db, bytes));
        var fr2 = r.Behaviors.Single(b => b.Id == "req:FR-002");
        (fr2.Recommendation, fr2.DeveloperTestIds.Count).Should().Be((CoverageRecommendationKind.NeedsHumanReview, 0));
        fr2.ForTesters.Should().Contain("2 different modules");
        r.Overlaps.Where(o => o.Dimensions.SameRequirement).Should().BeEmpty("an ambiguous requirement id never counts as the same requirement");
        r.Limitations.Should().Contain(l => l.Contains("ambiguous"));
        r.QaScope.ProbablyUnnecessary.Should().NotContain(x => x.Contains("FR-002"));
    }

    [Fact]
    public void Requirement_titles_drop_markdown_markers_and_the_repeated_id() =>
        TestCoverageReviewEngine.CleanRequirement("FR-001", "- **FR-001**: System MUST reject duplicates").Should().Be("System MUST reject duplicates");

    [Fact]
    public async Task Components_show_dimensions_without_percentages()
    {
        var r = Review(await CurrentAsync());
        var api = r.Components.Single(c => c.Name == "Orders.Api");
        api.Dimensions.Single(d => d.Dimension == "Messaging").Status.Should().Be(DimensionStatus.Partial);
        api.Dimensions.Single(d => d.Dimension == "Database").Status.Should().Be(DimensionStatus.Partial);
        api.Dimensions.Single(d => d.Dimension == "API").Status.Should().Be(DimensionStatus.Some);
        api.Dimensions.Single(d => d.Dimension == "Performance").Status.Should().Be(DimensionStatus.NotAssessed);
        r.Components.Single(c => c.Name == "Orders.Notifier").Tests.Should().Be(0);
        JsonSerializer.Serialize(r).Should().NotContain("%");
    }

    [Fact]
    public async Task Reviewer_decisions_stay_separate_from_source_evidence()
    {
        var snapshot = await CurrentAsync();
        var first = Review(snapshot);
        var inbound = first.Journeys.SelectMany(j => j.Steps).First(s => s.From.StartsWith("upstream:"));
        var key = ReviewSourceEvidenceProvider.Identity(snapshot).Key;
        var r = Review(snapshot, decisions: [new CoverageDecision { RepositoryKey = key, Kind = CoverageDecisionKind.JourneyConnection, SubjectKey = inbound.Id, Value = "Confirmed", DecidedAt = DateTimeOffset.UtcNow },
            new CoverageDecision { RepositoryKey = key, Kind = CoverageDecisionKind.ProjectOwnership, SubjectKey = "Orders.Adapter.Tests", Value = "QaAutomated", DecidedAt = DateTimeOffset.UtcNow }]);
        var step = r.Journeys.SelectMany(j => j.Steps).First(s => s.Id == inbound.Id);
        (step.Evidence, step.ReviewerDecision).Should().Be((ConnectionEvidence.NeedsConfirmation, "Confirmed"));
        r.Tests.Where(t => t.Project == "Orders.Adapter.Tests").Should().OnlyContain(t => t.Ownership == TestOwnership.QaAutomated && t.OwnershipBasis.Contains("reviewer"));
        r.QaTests.Should().Contain(q => q.Ownership == TestOwnership.QaAutomated && q.Source.Contains("Orders.Adapter.Tests"));
    }

    [Fact]
    public async Task Critical_e2e_flows_are_qa_automated_evidence_and_runtime_stays_separate()
    {
        var r = Review(await CurrentAsync(), flows: [new E2EFlowEvidence("f1", "Place order", "Orders.Api", "Customer places an order", CriticalE2EStatus.Passed, DateTimeOffset.UtcNow)]);
        r.QaTests.Should().Contain(q => q.Id == "e2e:f1" && q.IntendedLevel == TestLevel.E2E && q.Execution == TestExecutionState.Passed);
        var step = r.Journeys.SelectMany(j => j.Steps).First(s => s.FromName == "Orders.Api" && s.Boundary == BoundaryKind.Database);
        step.Qa.Should().Be(StepCoverage.PartlyCovered);
        step.Runtime.Should().StartWith("Some").And.Contain("not proven");
        step.Developer.Should().Be(StepCoverage.PartlyCovered, "QA/runtime evidence never changes developer coverage");
    }

    [Fact]
    public async Task Service_binds_runs_to_exact_snapshots_and_refuses_other_repositories()
    {
        var current = await CurrentAsync();
        var other = await TestCoverageFixture.AnalyzeAsync(_db, TestCoverageFixture.Zip(("Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")), "other.zip");
        var service = new TestCoverageReviewService(_db, new ReviewSourceEvidenceProvider(new IqrSourceStore(_db)));
        (await service.RunAsync(new TestCoverageReviewRequest())).Error.Should().Be(TestCoverageText.SourceRequired);
        (await service.RunAsync(new TestCoverageReviewRequest { CurrentSnapshotId = current.Id, BaselineSnapshotId = other.Id })).Status.Should().Be(400);
        var outcome = await service.RunAsync(new TestCoverageReviewRequest { CurrentSnapshotId = current.Id, Workspace = TestCoverageFixture.Workspace() });
        outcome.Result!.Current.SnapshotId.Should().Be(current.Id);
        (await service.HistoryAsync()).Single().CurrentSnapshotId.Should().Be(current.Id);
        (await service.GetAsync(outcome.Result.RunId))!.Current.Fingerprint.Should().Be(current.Archive.Sha256);
        (await service.DecideAsync(new CoverageDecision { RepositoryKey = "r", Kind = CoverageDecisionKind.Overlap, SubjectKey = "x", Value = "Delete test" })).Error.Should().NotBeNull("only bounded decision values are stored");
        (await service.SourcesAsync()).Snapshots.Single(s => s.SnapshotId == current.Id).HasConsumerEvidence.Should().BeTrue();
    }
}
