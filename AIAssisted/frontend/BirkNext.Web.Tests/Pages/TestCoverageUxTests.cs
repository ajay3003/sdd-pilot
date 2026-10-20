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

/// <summary>
/// Test Coverage &amp; Overlap Review information architecture: summary → execution evidence → what needs attention → QA recommendation →
/// views; journeys summary-first with steps collapsed; component matrix; grouped Remaining QA Scope with counts preserved; contextualized
/// 0 duplicates; rendered documentation; collapsed history and limitations. Generic Northwind fixture.
/// </summary>
public sealed class TestCoverageUxTests : BunitContext
{
    private readonly Mock<ITestCoverageApiService> _api = new();
    private static readonly Guid Current = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    public TestCoverageUxTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "Northwind Orders", WorkspaceArtifactType.Specification)).Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.HistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new TestCoverageRunSummary(Guid.NewGuid(), DateTimeOffset.UtcNow, Current, null, "earlier")]);
        _api.Setup(x => x.SourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ReviewSourceOptions { Snapshots = [new ReviewSourceSnapshot
        {
            SnapshotId = Current, RepositoryKey = "northwind", Repository = "northwind", ArchiveName = "northwind.zip", Fingerprint = new string('c', 64), AnalyzedAt = DateTimeOffset.UtcNow,
            SourceStatus = "Ready", Latest = true, HasConsumerEvidence = true,
        }] }, (string?)null));
    }

    private static CoverageTest Test(string id, string project, TestLevel level, BoundaryMode? mode = null) => new()
    {
        TestId = id, Name = id, Project = project, Level = level, Analyzed = true, Execution = TestExecutionState.NotVerified,
        Boundaries = mode is { } m ? [new(BoundaryKind.Database, m, "IOrderRepository", "method", 3)] : [],
    };

    private static TestCoverageReviewResult Result(bool withQaTests = false, bool withOverlap = false) => new()
    {
        CompletedAt = DateTimeOffset.UtcNow, ProjectName = "Northwind Orders", Current = new(Current, "northwind", "northwind.zip", new string('c', 64), DateTimeOffset.UtcNow),
        Inventory = new CoverageInventory { TestsDiscovered = 5, TestsAnalyzed = 5, ExecutionEvidence = TestCoverageText.NoExecution, CodeCoverage = TestCoverageText.NoPercentage },
        Tests = [Test("t1", "Orders.Tests", TestLevel.Unit, BoundaryMode.Mocked), Test("t2", "Orders.Tests", TestLevel.Api, BoundaryMode.Real), Test("t3", "Orders.Legacy", TestLevel.Unknown),
            Test("t4", "Orders.Legacy", TestLevel.NeedsReview), Test("t5", "Orders.Ui", TestLevel.E2E)],
        QaTests = withQaTests ? [new QaTestCandidate { Id = "AS-1", Title = "Duplicate order is rejected" }] : [],
        Journeys =
        [
            new Journey
            {
                Id = "j1", Title = "Upstream → orders-inbound → Orders.Adapter → Orders.Api", Weakest = ConnectionEvidence.NeedsConfirmation, FocusForTesters = ["A real message arriving on orders-inbound", "A deployed HTTP call"],
                Steps =
                [
                    new JourneyStep { Id = "s1", From = "up", To = "in", FromName = "Upstream", ToName = "orders-inbound", Evidence = ConnectionEvidence.NeedsConfirmation, Developer = StepCoverage.NotVerified, DeveloperExplanation = "Outside this source." },
                    new JourneyStep { Id = "s2", From = "ad", To = "api", FromName = "Orders.Adapter", ToName = "Orders.Api", Evidence = ConnectionEvidence.StronglySupported, Developer = StepCoverage.MockOnly, DeveloperExplanation = "Mocked HTTP." },
                    new JourneyStep { Id = "s3", From = "api", To = "db", FromName = "Orders.Api", ToName = "OrdersDb", Evidence = ConnectionEvidence.Confirmed, Developer = StepCoverage.LowerLevelOnly, DeveloperExplanation = "Repository tests." },
                ],
            },
        ],
        Components = [new ComponentCoverage { ComponentId = "c1", Name = "Orders.Api", Tests = 2, Dimensions = [new("Messaging", DimensionStatus.Partial, "Only with mocks.", ["t1"]), new("API", DimensionStatus.Some, "One API test.", ["t2"]), new("Performance", DimensionStatus.NotAssessed, "Not assessed.", [])] },
            new ComponentCoverage { ComponentId = "c2", Name = "Orders.Adapter", Tests = 0, Dimensions = [new("Messaging", DimensionStatus.MissingEvidence, "No tests.", [])] }],
        Overlaps = withOverlap ? [new TestOverlap { Id = "o1", QaTestId = "AS-1", QaTitle = "Duplicate order is rejected", Kind = OverlapKind.HighConfidenceOverlap, Recommendation = "Probably does not need to be repeated at the same test level.", RemainingRisk = "HTTP path." }] : [],
        Behaviors = [new BehaviorCoverage { Id = "req:FR-002", Title = "FR-002 — Duplicate orders must be rejected", Status = "Partly covered", AlreadyTested = ["Duplicate check", "Validation"], NotVerifiedYet = ["Real message", "Deployed API", "Persistence"], ForTesters = "Keep one system test through the deployed path." }],
        Gaps =
        [
            new CoverageGap { Id = "g1", Kind = GapKind.MockOnly, Title = "Messaging in Orders.Api is tested with mocks only", Explanation = "Real broker not verified.", Attention = "Needs attention" },
            new CoverageGap { Id = "g2", Kind = GapKind.JourneyStepNotVerified, Title = "Upstream → orders-inbound is not verified", Explanation = "No real message.", Attention = "Needs attention" },
            new CoverageGap { Id = "g3", Kind = GapKind.NoTestEvidence, Title = "Orders.Adapter has no tests", Explanation = "No test evidence.", Attention = "Review recommended" },
        ],
        QaScope = new(["One system test through Upstream → orders-inbound → Orders.Adapter → Orders.Api"], ["Repeating isolated repository logic"],
            ["Confirm the connection Upstream → orders-inbound (NeedsConfirmation)", "Review whether \"Duplicate order\" overlaps developer automation", "2 test(s) whose level could not be determined — review their project setup."]),
        Limitations = ["C# syntax only."],
    };

    private IRenderedComponent<TestCoverageReview> Run(TestCoverageReviewResult? result = null)
    {
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((result ?? Result(), (string?)null));
        var cut = Render<TestCoverageReview>();
        cut.Find("[data-testid=tco-run]").Click();
        cut.WaitForElement("[data-testid=tco-result]");
        return cut;
    }

    [Fact]
    public void SummaryAttentionAndRecommendationComeBeforeTheViewsAndRawEvidence()
    {
        var cut = Run();
        var markup = cut.Markup;
        int At(string id) => markup.IndexOf($"data-testid=\"{id}\"", StringComparison.Ordinal);

        At("tco-overview-cards").Should().BeLessThan(At("tco-execution-note"));
        At("tco-execution-note").Should().BeLessThan(At("tco-attention"));
        At("tco-attention").Should().BeLessThan(At("tco-recommendation"));
        At("tco-recommendation").Should().BeLessThan(At("tco-tabs"));
        At("tco-recommendation").Should().BeLessThan(At("tco-projects"), "recommendation before raw test-project inventory");
        cut.Find("[data-testid=tco-card-tests]").TextContent.Should().Contain("5").And.Contain("5 analyzed · 0 with results");
    }

    [Fact]
    public void AttentionCountsComeFromTheResult_AndExecutionAndCoverageStayUnverified()
    {
        var cut = Run();

        cut.Find("[data-testid=tco-attention-gaps]").TextContent.Should().StartWith("2 remaining gaps need tester attention");
        cut.Find("[data-testid=tco-attention-connections]").TextContent.Should().StartWith("1 journey connection");
        cut.Find("[data-testid=tco-attention-unknown-level]").TextContent.Should().StartWith("2 tests have an unknown test level");
        cut.Find("[data-testid=tco-attention-execution]").TextContent.Should().Contain("not verified");
        var execution = cut.Find("[data-testid=tco-execution-note]").TextContent;
        execution.Should().Contain("Not verified").And.Contain("code coverage: Not available");
        cut.Find("[data-testid=tco-result]").TextContent.Should().NotContain("%");
    }

    [Fact]
    public void RecommendationUsesNonDestructiveWording()
    {
        var cut = Run();
        var text = cut.Find("[data-testid=tco-recommendation]").TextContent;

        text.Should().Contain("Focus on").And.Contain("Probably do not need to repeat").And.Contain("Needs confirmation").And.Contain("never removes a test");
        cut.Markup.Should().NotContainAny("Delete this", "delete the test", "Remove this test");
    }

    [Fact]
    public void TestProjectsAndHistoryAndLimitationsStartCollapsed()
    {
        var cut = Run();

        cut.Find("[data-testid=tco-projects-details]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=tco-projects-details] summary").TextContent.Should().Contain("3 projects · 5 tests");
        cut.Find("[data-testid=tco-history] details").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=tco-limitations]").HasAttribute("open").Should().BeFalse();
    }

    [Fact]
    public void JourneysAreSummaryFirst_WithStepsAndTechnicalDetailsCollapsed()
    {
        var cut = Run();
        cut.Find("[data-testid=tco-tab-journeys]").Click();

        var summary = cut.Find("[data-testid=tco-journey-summary]").TextContent;
        summary.Should().Contain("Not verified").And.Contain("Mock-only").And.Contain("Lower level only").And.Contain("Runtime verified").And.Contain("No");
        cut.Find("[data-testid=tco-journey-steps]").HasAttribute("open").Should().BeFalse();
        cut.FindAll("[data-testid=tco-step]").Should().HaveCount(3, "steps stay available behind the disclosure");
        cut.FindAll("[data-testid=tco-step]").Select(s => s.GetAttribute("data-coverage")).Should().Equal("NotVerified", "MockOnly", "LowerLevelOnly");
        cut.FindAll("[data-testid=tco-confirm]").Should().ContainSingle("only the unconfirmed connection has a decision");
        cut.FindAll("[data-testid=tco-journey-steps] details").Should().OnlyContain(d => !d.HasAttribute("open"));
    }

    [Fact]
    public void ComponentsRenderAsAMatrix_WithStatesKeptAndDetailsOnDemand()
    {
        var cut = Run();
        cut.Find("[data-testid=tco-tab-components]").Click();

        var matrix = cut.Find("[data-testid=tco-matrix]");
        matrix.QuerySelectorAll("thead th").Select(th => th.TextContent).Should().Contain(["Messaging", "API"]).And.NotContain("Performance", "not-assessed dimensions are hidden by default");
        cut.FindAll("[data-testid=tco-dimension]").Select(d => d.GetAttribute("data-status")).Should().BeEquivalentTo(["Partial", "Some", "MissingEvidence"]);
        matrix.TextContent.Should().NotContain(TestCoverageText.NoExecution, "the execution warning is shown once, globally");
        cut.FindAll("[data-testid=tco-component-details-toggle]")[0].Click();
        cut.Find(".tco-component-details").TextContent.Should().Contain("Only with mocks.").And.Contain("One API test.");
        cut.Find("[data-testid=tco-component-state]").Change(nameof(DimensionStatus.MissingEvidence));
        cut.FindAll("[data-testid=tco-component]").Should().ContainSingle().Which.TextContent.Should().Contain("Orders.Adapter");
    }

    [Fact]
    public void RequirementsSummarizeCounts_AndCollapseTheLists()
    {
        var cut = Run();
        cut.Find("[data-testid=tco-tab-requirements]").Click();

        var behavior = cut.Find("[data-testid=tco-behavior]");
        behavior.TextContent.Should().Contain("Already tested2").And.Contain("Not verified yet3");
        behavior.QuerySelectorAll("details").Should().OnlyContain(d => !d.HasAttribute("open"));
    }

    [Fact]
    public void RemainingQaScopeIsGrouped_AndEveryGapIsKept()
    {
        var cut = Run();
        cut.Find("[data-testid=tco-tab-scope]").Click();

        var groups = cut.FindAll("[data-testid=tco-scope-group]");
        groups.Select(g => g.GetAttribute("data-group")).Should().Equal("focus", "real-flow", "mock-only", "connections", "decisions", "unknown-level", "not-repeat", "other");
        groups.Should().OnlyContain(g => !g.HasAttribute("open"));
        cut.FindAll("[data-testid=tco-gap]").Should().HaveCount(3, "every underlying gap is in exactly one group");
        Count(groups, "real-flow").Should().Be(1);
        Count(groups, "mock-only").Should().Be(1);
        Count(groups, "other").Should().Be(1);
        Count(groups, "unknown-level").Should().Be(2);
        Count(groups, "connections").Should().Be(1);
        cut.Find("[data-testid=tco-connections]").TextContent.Should().Contain("Upstream").And.Contain("orders-inbound");
        cut.Find("[data-testid=tco-scope-review]").TextContent.Should().Contain("Review whether").And.NotContain("Confirm the connection");
        cut.Find("[data-testid=tco-unknown-level]").TextContent.Should().Contain("Orders.Legacy — 2 tests");
        static int Count(IEnumerable<AngleSharp.Dom.IElement> gs, string key) => int.Parse(gs.Single(g => g.GetAttribute("data-group") == key).GetAttribute("data-count")!);
    }

    [Fact]
    public void ZeroDuplicatesWithoutQaEvidenceIsExplained_AndOverlapsAreNeverRemovals()
    {
        var cut = Run();
        cut.Find("[data-testid=tco-tab-duplicates]").Click();
        cut.Find("[data-testid=tco-duplicates-context]").TextContent.Should().Contain("not evidence that no duplication exists");
        cut.Find("[data-testid=tco-card-duplicates]").TextContent.Should().Contain("No QA tests to compare");

        var withOverlap = Run(Result(withQaTests: true, withOverlap: true));
        withOverlap.Find("[data-testid=tco-tab-duplicates]").Click();
        withOverlap.FindAll("[data-testid=tco-duplicates-context]").Should().BeEmpty();
        withOverlap.Find("[data-testid=tco-overlap]").TextContent.Should().Contain("Probably does not need to be repeated").And.NotContain("Delete");
    }

    [Fact]
    public void DocumentationIsRenderedByDefault_WithRawMarkdownOptional()
    {
        var cut = Run();
        cut.Find("[data-testid=tco-tab-documentation]").Click();

        var preview = cut.Find("[data-testid=tco-doc-preview]");
        preview.TagName.Should().Be("ARTICLE");
        preview.QuerySelectorAll("h1, h2, h3").Should().NotBeEmpty("Markdown headings are rendered");
        preview.TextContent.Should().Contain("northwind.zip", "the snapshot identity stays in the documentation");
        cut.Find("[data-testid=tco-doc-raw-details]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=tco-doc-raw]").TextContent.Should().Contain("#");
    }
}
