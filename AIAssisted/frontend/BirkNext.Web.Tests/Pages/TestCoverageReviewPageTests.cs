using BirkNext.SourceEvidence;
using BirkNext.TestCoverage;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Test Coverage &amp; Overlap Review page: source-required state, simple language by default, views of one result, confirmations, exports.</summary>
public sealed class TestCoverageReviewPageTests : BunitContext
{
    private readonly Mock<ITestCoverageApiService> _api = new();
    private readonly WorkspaceArtifactRepository _workspace = new();
    private static readonly Guid Current = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    public TestCoverageReviewPageTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(_workspace);
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "Northwind Orders", WorkspaceArtifactType.Specification)).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.HistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private void Sources(params ReviewSourceSnapshot[] snapshots) =>
        _api.Setup(x => x.SourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ReviewSourceOptions { Snapshots = [.. snapshots] }, (string?)null));

    private static ReviewSourceSnapshot Snap(bool evidence = true) => new()
    {
        SnapshotId = Current, RepositoryKey = "northwind", Repository = "northwind", ArchiveName = "northwind.zip", Fingerprint = new string('c', 64), AnalyzedAt = DateTimeOffset.UtcNow,
        SourceStatus = "Ready", Latest = true, HasConsumerEvidence = evidence, ConsumerEvidenceNote = evidence ? null : "Analyzed before per-test facts existed.",
    };

    private static TestCoverageReviewResult Result() => new()
    {
        CompletedAt = DateTimeOffset.UtcNow, ProjectName = "Northwind Orders", Current = new(Current, "northwind", "northwind.zip", new string('c', 64), DateTimeOffset.UtcNow),
        Inventory = new CoverageInventory { TestsDiscovered = 3, TestsAnalyzed = 3, ExecutionEvidence = TestCoverageText.NoExecution, CodeCoverage = TestCoverageText.NoPercentage },
        Tests =
        [
            new CoverageTest { TestId = "t1", Name = "OrderServiceTests.Create_Duplicate", Project = "Orders.Tests", Level = TestLevel.Unit, Analyzed = true, Execution = TestExecutionState.NotVerified,
                Boundaries = [new(BoundaryKind.Database, BoundaryMode.Mocked, "IOrderRepository", "method", 3)], Purpose = "Test appears to check \"create duplicate\": expects DuplicateOrderException." },
        ],
        Journeys =
        [
            new Journey
            {
                Id = "j1", Title = "Upstream → orders-inbound → Orders.Adapter", Weakest = ConnectionEvidence.NeedsConfirmation, FocusForTesters = ["A real message arriving on orders-inbound"],
                Steps =
                [
                    new JourneyStep { Id = "s1", FromName = "Upstream", ToName = "orders-inbound", Boundary = BoundaryKind.EventHub, Evidence = ConnectionEvidence.NeedsConfirmation, Developer = StepCoverage.NotVerified,
                        DeveloperExplanation = "The producing system is outside this source.", Provenance = ["No producer of this channel was found in the analyzed source."] },
                    new JourneyStep { Id = "s2", FromName = "Orders.Adapter", ToName = "Orders.Api", Boundary = BoundaryKind.Http, Evidence = ConnectionEvidence.StronglySupported, Developer = StepCoverage.MockOnly,
                        DeveloperExplanation = "1 test(s) cover this only with a mocked or fake HTTP dependency.", Provenance = ["Orders.Adapter/Program.cs:2 — HttpClient"] },
                ],
            },
        ],
        Components = [new ComponentCoverage { ComponentId = "c1", Name = "Orders.Api", Tests = 1, Dimensions = [new("Messaging", DimensionStatus.Partial, "1 test(s), but only with mocks.", ["t1"]), new("Performance", DimensionStatus.NotAssessed, "No performance tests found; not assessed.", [])] }],
        Overlaps = [new TestOverlap { Id = "o1", QaTestId = "AS-1", QaTitle = "Duplicate order is rejected by the API", Kind = OverlapKind.HighConfidenceOverlap, Strength = EvidenceStrength.Some,
            Recommendation = "Probably does not need to be repeated at the same test level.", RemainingRisk = "Not covered by the developer test: Orders.Adapter → Orders.Api (HTTP dependency).", Same = ["requirement", "behaviour"], Different = ["execution (no proof the developer test ran)"], Reasons = ["same requirement"], DeveloperTestIds = ["t1"] }],
        Behaviors = [new BehaviorCoverage { Id = "req:FR-002", Title = "FR-002 — Duplicate orders must be rejected", Status = "Partly covered", AlreadyTested = ["Test appears to check \"create duplicate\""], NotVerifiedYet = ["A real message arriving on orders-inbound"], ForTesters = "Do not repeat the developer's isolated checks." }],
        Gaps = [new CoverageGap { Id = "g1", Kind = GapKind.MockOnly, Title = "Messaging in Orders.Api is tested with mocks or in-memory substitutes only", Explanation = "The real broker path is not verified.", Attention = "Needs attention" }],
        QaScope = new(["One system test through Upstream → orders-inbound → Orders.Adapter"], ["Repeating \"Duplicate order is rejected by the API\" at the same level — developer automation already checks it"], ["Confirm the connection Upstream → orders-inbound (NeedsConfirmation)"]),
        Limitations = ["C# syntax only."],
    };

    [Fact]
    public void Without_a_source_snapshot_the_page_asks_for_source_analysis_and_cannot_run()
    {
        Sources();
        var cut = Render<TestCoverageReview>();
        cut.FindAll("[data-testid=tco-run]").Should().BeEmpty("there is nothing to review without a snapshot");
        cut.Find("[data-testid=tco-source-empty]").TextContent.Should().Contain(TestCoverageText.SourceRequired);
        cut.Find("[data-testid=tco-open-source-analysis]").Should().NotBeNull();
        cut.Markup.Should().Contain(TestCoverageText.Hero);
        TestCoveragePresentation.RunBlockedReason(null, false).Should().Be(TestCoverageText.SourceRequired);
    }

    [Fact]
    public void An_old_snapshot_is_flagged_and_the_run_still_uses_the_selected_snapshot()
    {
        Sources(Snap(evidence: false));
        TestCoverageReviewRequest? sent = null;
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>())).Callback<TestCoverageReviewRequest, CancellationToken>((r, _) => sent = r).ReturnsAsync((Result(), (string?)null));
        var cut = Render<TestCoverageReview>();
        cut.Find("[data-testid=tco-snapshot-old]").TextContent.Should().Contain("per-test facts");
        cut.Find("[data-testid=tco-run]").Click();
        sent!.CurrentSnapshotId.Should().Be(Current);
        sent.Workspace!.ProjectName.Should().Be("Northwind Orders");
    }

    [Fact]
    public void Results_default_to_simple_language_and_hide_internal_terms()
    {
        Sources(Snap());
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Result(), (string?)null));
        var cut = Render<TestCoverageReview>();
        cut.Find("[data-testid=tco-run]").Click();
        cut.Find("[data-testid=tco-card-tests]").TextContent.Should().Contain("3").And.Contain("Tests found");
        cut.Find("[data-testid=tco-execution-note]").TextContent.Should().Contain(TestCoverageText.NoExecution).And.Contain("no coverage percentage");
        var text = cut.Find("[data-testid=tco-result]").TextContent;
        text.Should().NotContain("EvidenceProvenance").And.NotContain("SemanticEquivalence").And.NotContain("BoundaryConfidence").And.NotContain("%");
        text.Should().NotContain("HighConfidenceOverlap").And.NotContain("MockOnly");
    }

    [Fact]
    public void Views_show_journeys_duplicates_scope_and_documentation_of_the_same_result()
    {
        Sources(Snap());
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Result(), (string?)null));
        var cut = Render<TestCoverageReview>();
        cut.Find("[data-testid=tco-run]").Click();
        cut.Find("[data-testid=tco-tab-journeys]").Click();
        var steps = cut.FindAll("[data-testid=tco-step]");
        steps[0].TextContent.Should().Contain("Not verified").And.Contain("Needs confirmation");
        steps[1].TextContent.Should().Contain("Covered by mocks only").And.Contain("Strongly supported by source");
        cut.FindAll("[data-testid=tco-confirm]").Should().ContainSingle("only unconfirmed connections can be confirmed");
        cut.Find("[data-testid=tco-tab-duplicates]").Click();
        cut.Find("[data-testid=tco-overlap]").TextContent.Should().Contain("Possible duplicate test").And.Contain("Probably does not need to be repeated at the same test level").And.Contain("Remaining risk");
        cut.Find("[data-testid=tco-tab-scope]").Click();
        cut.Find("[data-testid=tco-scope-keep]").TextContent.Should().Contain("One system test");
        cut.Find("[data-testid=tco-gap]").GetAttribute("data-kind").Should().Be("MockOnly");
        cut.Find("[data-testid=tco-tab-components]").Click();
        cut.FindAll("[data-testid=tco-dimension]").Should().ContainSingle("not-assessed dimensions are hidden by default");
        cut.Find("[data-testid=tco-tab-documentation]").Click();
        cut.Find("[data-testid=tco-doc-preview]").TextContent.Should().Contain("What is already tested").And.Contain("What is not verified yet");
    }

    [Fact]
    public void Confirming_a_connection_is_stored_as_a_reviewer_decision_and_the_review_reruns()
    {
        Sources(Snap());
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Result(), (string?)null));
        CoverageDecision? decision = null;
        _api.Setup(x => x.DecideAsync(It.IsAny<CoverageDecision>(), It.IsAny<CancellationToken>())).Callback<CoverageDecision, CancellationToken>((d, _) => decision = d).ReturnsAsync((new CoverageDecision(), (string?)null));
        var cut = Render<TestCoverageReview>();
        cut.Find("[data-testid=tco-run]").Click();
        cut.Find("[data-testid=tco-tab-journeys]").Click();
        cut.Find("[data-testid=tco-confirm]").Click();
        decision.Should().Match<CoverageDecision>(d => d.Kind == CoverageDecisionKind.JourneyConnection && d.SubjectKey == "s1" && d.Value == "Confirmed" && d.RepositoryKey == "northwind");
        _api.Verify(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void Tabs_are_keyboard_navigable()
    {
        Sources(Snap());
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Result(), (string?)null));
        var cut = Render<TestCoverageReview>();
        cut.Find("[data-testid=tco-run]").Click();
        cut.Find("[data-testid=tco-tab-overview]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        cut.Find("[data-testid=tco-panel]").GetAttribute("data-view").Should().Be("journeys");
        cut.Find("[data-testid=tco-tab-journeys]").GetAttribute("tabindex").Should().Be("0");
        cut.Find("[data-testid=tco-tab-overview]").GetAttribute("tabindex").Should().Be("-1");
    }

    [Fact]
    public void Exports_are_markdown_and_html_with_simple_sections_and_no_percentages()
    {
        var r = Result();
        var md = TestCoveragePresentation.Markdown(r);
        md.Should().Contain("## Remaining QA scope").And.Contain("### Tests testers probably do not need to repeat").And.Contain("What testers should focus on").And.Contain("## Technical details");
        md.Should().Contain("northwind.zip").And.NotContain("%");
        TestCoveragePresentation.Markdown(r, scopeOnly: true).Should().StartWith("# Remaining QA Scope").And.NotContain("## By journey");
        var html = TestCoveragePresentation.Html(r, coveredOnly: true);
        html.Should().Contain("Covered Tests").And.Contain("<details><summary>Technical details</summary>").And.NotContain("Remaining QA scope</h2>");
        TestCoveragePresentation.FileName(r, "covered-tests", "md").Should().StartWith("covered-tests-Northwind-Orders-").And.EndWith(".md");
    }

    [Fact]
    public void Workspace_evidence_sends_acceptance_scenarios_as_qa_candidates_and_imported_executions()
    {
        _workspace.SddLifecycle.TestExecutions.Add(new SddTestExecutionEvidence { TestId = "t1", TestName = "A.B", Result = "Passed" });
        var spec = new SpecificationSemanticModel();
        var requirement = new SemanticRequirement { Id = "FR-002", Text = "Duplicates are rejected" };
        spec.Requirements.Add(requirement);
        spec.AcceptanceScenarios.Add(new SemanticAcceptanceScenario { Id = "AS-1", Title = "Duplicate rejected", When = "POST twice", Then = "422", LinkedRequirements = [requirement] });
        var evidence = TestCoveragePresentation.WorkspaceEvidence(_workspace, new ReviewContext { Specification = spec }, "P");
        evidence.Requirements.Should().ContainSingle(r => r.Id == "FR-002");
        evidence.AcceptanceScenarios.Should().ContainSingle(s => s.Id == "AS-1" && s.Ownership == TestOwnership.QaManual && s.RequirementIds.Single() == "FR-002");
        evidence.Executions.Should().ContainSingle(e => e.TestId == "t1" && e.Result == "Passed");
    }

    [Fact]
    public void Production_review_code_names_no_project_specific_components()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "backend"))) root = Path.GetDirectoryName(root)!;
        var files = new[]
        {
            "backend/BirkNext.Api/Services/TestCoverage/TestCoverageReviewEngine.cs", "backend/BirkNext.Api/Services/TestCoverage/TestCoverageReviewService.cs",
            "backend/BirkNext.Api/Services/SourceAnalysis/TestBehavior/TestBehaviorSourceAnalyzer.cs", "frontend/BirkNext.Web/Pages/TestCoverageReview.razor",
            "frontend/BirkNext.Web/Services/TestCoveragePresentation.cs", "shared/TestCoverageContracts.cs",
        };
        foreach (var file in files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            foreach (var name in new[] { "M2LB", "PersonAdapter", "Tjeneste", "Hendelse", "BiRK", "Person API" })
                text.Should().NotContain(name, $"{file} must stay project-generic");
        }
    }
}
