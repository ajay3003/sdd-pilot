using BirkNext.ManualReview;
using BirkNext.SourceEvidence;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Saved test cases (Test Plan Suggestions) in Test Coverage: shown in the Requirements view as test design with a drill-down to the case,
/// unlinked cases visible but not counted, never executed, and the review's own test counts untouched. An older result without traced cases
/// says to run the review again.
/// </summary>
public sealed class TestCoveragePreparedCasesTests : BunitContext
{
    private readonly Mock<ITestCoverageApiService> _api = new();
    private static readonly Guid Current = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid DraftId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private TestCoverageReviewRequest? _sent;

    public TestCoveragePreparedCasesTests()
    {
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext());
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var manual = new Mock<IManualReviewApiService>();
        manual.Setup(x => x.ListAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<ManualReviewDecisionView>?)[]);
        Services.AddSingleton(manual.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _api.Setup(x => x.HistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _api.Setup(x => x.SourcesAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new ReviewSourceOptions
        {
            Snapshots = [new ReviewSourceSnapshot { SnapshotId = Current, RepositoryKey = "library", Repository = "library", ArchiveName = "library.zip", Fingerprint = new string('e', 64),
                AnalyzedAt = DateTimeOffset.UtcNow, SourceStatus = "Ready", Latest = true, HasConsumerEvidence = true }],
        }, (string?)null));
    }

    private static PreparedCaseTrace Case(string id, string title, PreparedCaseStage stage, params PreparedCaseRequirementLink[] links) => new()
    {
        CaseKey = $"{DraftId:N}/{id}", DraftId = DraftId, CaseId = id, PlanName = "Library – test plan", Title = title, Stage = stage, StageLabel = stage.ToString(),
        SourceKind = links.Length == 0 ? ScenarioSourceKind.Manual : ScenarioSourceKind.ProjectDiscovery, Capability = links.Length == 0 ? null : "specification",
        Requirements = [.. links], Href = $"test-plan-suggestions?draft={DraftId}&area=prepare&case={id}",
    };

    private static PreparedCaseTraceView Trace() => new()
    {
        ProjectKey = "library", SpecificationPaths = ["specs/001-library/spec.md"], Plans = 1, Cases = 2, LinkedCases = 1, UnlinkedCases = 1, RequirementsWithApprovedCase = 1,
        CaseList =
        [
            Case("tsc-borrow", "Borrow a book", PreparedCaseStage.Approved, new PreparedCaseRequirementLink("FR-001", "specs/001-library/spec.md", PreparedCaseLinkBasis.SpecificationEvidence, true, "Prepared from specs/001-library/spec.md, which names FR-001.")),
            Case("manual-1", "Log in as a librarian", PreparedCaseStage.Prepared),
        ],
        Requirements = [new PreparedRequirementTrace { RequirementId = "FR-001", ApprovedCases = 1, State = "Approved test case", CaseKeys = [$"{DraftId:N}/tsc-borrow"] }],
    };

    private static TestCoverageReviewResult Result(PreparedCaseTraceView? trace) => new()
    {
        CompletedAt = DateTimeOffset.UtcNow, ProjectName = "Library", Current = new(Current, "library", "library.zip", new string('e', 64), DateTimeOffset.UtcNow),
        Inventory = new CoverageInventory { TestsDiscovered = 4, TestsAnalyzed = 4, ExecutionEvidence = TestCoverageText.NoExecution, CodeCoverage = TestCoverageText.NoPercentage },
        QaTests = [new QaTestCandidate { Id = "US1-1", Title = "Borrow a book", Source = "Acceptance scenario in the specification", RequirementIds = ["FR-001"] }],
        PreparedCases = trace,
    };

    private IRenderedComponent<TestCoverageReview> RunReview(TestCoverageReviewResult result)
    {
        _api.Setup(x => x.RunAsync(It.IsAny<TestCoverageReviewRequest>(), It.IsAny<CancellationToken>()))
            .Callback<TestCoverageReviewRequest, CancellationToken>((request, _) => _sent = request).ReturnsAsync((result, (string?)null));
        var cut = Render<TestCoverageReview>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tco-run]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=tco-run]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tco-result]").Should().ContainSingle());
        cut.Find("[data-testid=tco-tab-requirements]").Click();
        return cut;
    }

    [Fact]
    public void The_requirements_view_shows_saved_cases_as_test_design_with_a_drill_down_and_never_as_executed()
    {
        var result = Result(Trace());
        var cut = RunReview(result);

        _sent!.ProjectKey.Should().Be("library", "only the loaded project's cases are traced");
        var row = cut.Find("[data-testid=pct-requirement]");
        row.GetAttribute("data-requirement").Should().Be("FR-001");
        row.TextContent.Should().Contain("Approved test case");
        cut.Find("[data-testid=pct-requirement-case]").GetAttribute("href").Should().Be($"test-plan-suggestions?draft={DraftId}&area=prepare&case=tsc-borrow");
        cut.FindAll("[data-testid=pct-executed]").Should().BeEmpty("test design only: manual tests are executed in Azure DevOps Test Plans");
        cut.FindAll("[data-testid=pct-case-execution]").Should().BeEmpty();
        cut.Find("[data-testid=pct-boundary]").TextContent.Should().Contain("not test execution");
        // The review's own counts are not changed by the traced cases.
        result.Inventory.TestsDiscovered.Should().Be(4);
        result.QaTests.Should().ContainSingle();
    }

    [Fact]
    public void An_unlinked_case_is_listed_but_not_counted()
    {
        var cut = RunReview(Result(Trace()));

        cut.Find("[data-testid=pct-unlinked]").TextContent.Should().Be("1");
        var manual = cut.FindAll("[data-testid=pct-case]").Single(li => li.GetAttribute("data-case")!.EndsWith("/manual-1"));
        manual.GetAttribute("data-counted").Should().Be("false");
        manual.QuerySelector("[data-testid=pct-case-unlinked]")!.TextContent.Should().Contain("not counted as requirement coverage");
        cut.FindAll("[data-testid=pct-requirement]").Should().ContainSingle("only FR-001 is covered by evidence");
    }

    [Fact]
    public void A_result_stored_before_cases_were_traced_says_to_run_the_review_again()
    {
        var cut = RunReview(Result(null));

        cut.Find("[data-testid=pct-none]").TextContent.Should().Contain("run the review again");
    }

    [Fact]
    public void The_panel_explains_why_a_link_is_not_counted()
    {
        var view = Trace() with
        {
            CaseList = [Case("o", "Other spec", PreparedCaseStage.Prepared, new PreparedCaseRequirementLink("FR-001", "specs/002/spec.md", PreparedCaseLinkBasis.SpecificationEvidence, false, "Prepared from specs/002/spec.md, not from the reviewed specification."))],
            Requirements = [], Cases = 1, LinkedCases = 0, UnlinkedCases = 1,
        };
        var cut = Render<PreparedCaseTracePanel>(p => p.Add(x => x.View, view).Add(x => x.ShowCases, true));

        var link = cut.Find("[data-testid=pct-case-link]");
        link.GetAttribute("data-counted").Should().Be("false");
        link.TextContent.Should().Contain("not counted").And.Contain("not from the reviewed specification");
        cut.FindAll("[data-testid=pct-requirements]").Should().BeEmpty();
    }
}
