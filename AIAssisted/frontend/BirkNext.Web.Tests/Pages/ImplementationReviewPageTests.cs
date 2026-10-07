using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Implementation Review (/task-alignment): task results vs findings, link vs coverage, fingerprint currentness, export wording.</summary>
public sealed class ImplementationReviewPageTests : BunitContext
{
    private const string Spec = """
        # Feature Specification

        ### User Story 1 - Sign in (Priority: P1)
        Users sign in.

        ## Requirements

        ### Functional Requirements
        - **FR-001**: Users can sign in with valid credentials.

        ## Success Criteria
        - **SC-001**: Valid users reach the dashboard after sign-in.
        """;

    private const string Tasks = """
        # Tasks

        ## Phase 1: Setup
        - [ ] T001 Create Sample.sln and the Api.csproj project skeleton
        - [ ] T002 Implement sign-in flow for FR-001 and SC-001
        - [ ] T003 Add AdminController with POST /admin/replay to replay events
        - [ ] T004 Wire FR-099 handling
        """;

    private readonly TaskAlignmentSessionService _session = new();
    private readonly Mock<ISampleProjectDocumentResolver> _resolver = new();
    private readonly Mock<IReportExportService> _export = new();
    private string _tasks = Tasks;

    public ImplementationReviewPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _resolver.Setup(r => r.GetSelectedProject()).Returns("sample");
        _resolver.Setup(r => r.GetAvailableProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SampleProjectDto("sample", "Sample Project", "test", "", "", false, [])]);
        _resolver.Setup(r => r.ResolveAsync("sample", ExplorerDocumentType.Specification, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SampleProjectDocumentResult.Success("sample", ExplorerDocumentType.Specification, "spec.md", Spec));
        _resolver.Setup(r => r.ResolveAsync("sample", ExplorerDocumentType.Tasks, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => SampleProjectDocumentResult.Success("sample", ExplorerDocumentType.Tasks, "tasks.md", _tasks));

        Services.AddSingleton(_resolver.Object);
        Services.AddSingleton(_session);
        Services.AddSingleton<IConstitutionAnalysisService, ConstitutionAnalysisService>();
        Services.AddSingleton<IPlanAnalysisService, PlanAnalysisService>();
        Services.AddSingleton<IArtifactParserService, ArtifactParserService>();
        Services.AddSingleton<TaskSpecAlignmentService>();
        Services.AddSingleton(_export.Object);
    }

    [Fact]
    public void Summary_SeparatesTaskResultsFromFindings()
    {
        var cut = Render<TaskToSpecAlignment>();
        var report = _session.Report!;

        report.TotalTasks.Should().Be(4);
        cut.Find("[data-testid=ir-card-analyzed] .ir-card-value").TextContent.Should().Be("4");
        cut.Find("[data-testid=ir-card-findings] .ir-card-value").TextContent.Should().Be(report.FindingCount.ToString());
        report.FindingCount.Should().Be(2, "T003 adds an endpoint without a spec reference and T004 references FR-099, which is not in the spec");
        cut.Find("[data-testid=ir-card-linked] .ir-card-hint").TextContent.Should().Contain("not coverage");
        cut.Find("[data-testid=ir-card-technical] .ir-card-hint").TextContent.Should().Contain("direct spec link not expected");
        cut.Markup.Should().NotContain("covered by spec").And.NotContain("no spec needed");
        cut.Find("[data-testid=ir-showing]").TextContent.Should().Contain("Showing 4 of 4 task results");
        cut.Markup.Should().NotContain("4 findings");
    }

    [Fact]
    public void FindingsFilter_ShowsExactlyTheFindingsCount()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Find("[data-testid=ir-filters] [data-filter=findings]").Click();

        cut.FindAll("[data-testid=ir-row]").Should().HaveCount(_session.Report!.FindingCount);
        cut.Find("[data-testid=ir-filters] [data-filter=findings]").GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("[data-testid=ir-row]").Select(r => r.GetAttribute("data-status"))
            .Should().OnlyContain(s => s == "NeedsReview" || s == "PossibleDeviation");
    }

    [Fact]
    public void NoConfidencePercentageIsShown()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Markup.Should().NotContain("Confidence").And.NotContain("80%").And.NotContain("95%");
    }

    [Fact]
    public void ExpandedTechnicalOnlyTask_ExplainsRuleAndThatALinkIsNotExpected()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Find("[data-testid=ir-filters] [data-filter=TechnicalOnly]").Click();
        cut.Find("[data-testid=ir-row] .ir-row-toggle").Click();

        var detail = cut.Find("[data-testid=ir-detail]");
        detail.TextContent.Should().Contain("a direct spec link is not expected");
        cut.Find("[data-testid=ir-basis]").TextContent.Should().Contain("matched");
        cut.Find("[data-testid=ir-row] .ir-row-toggle").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public void Search_FiltersBySpecItemId()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Find("[data-testid=ir-search]").Input("FR-001");

        cut.FindAll("[data-testid=ir-row]").Should().ContainSingle()
            .Which.TextContent.Should().Contain("T002");
    }

    [Fact]
    public void Status_IsCurrentWithBothArtifactFingerprints()
    {
        var cut = Render<TaskToSpecAlignment>();

        var status = cut.Find("[data-testid=ir-status]");
        status.GetAttribute("data-state").Should().Be("Current");
        cut.Find("[data-testid=ir-currentness]").TextContent.Should().Be("Current");
        status.TextContent.Should().Contain(TaskAlignmentSessionService.ShortHash(_session.Snapshot!.SpecificationHash))
            .And.Contain(TaskAlignmentSessionService.ShortHash(_session.Snapshot.TasksHash));
        cut.FindAll("[data-testid=ir-stale]").Should().BeEmpty();
    }

    [Fact]
    public void ChangedTaskArtifact_ShowsStaleUntilReanalyze()
    {
        Render<TaskToSpecAlignment>().Dispose();
        var firstHash = _session.Snapshot!.TasksHash;
        _tasks = Tasks + "\n- [ ] T005 Implement sign-out for FR-001\n";

        var cut = Render<TaskToSpecAlignment>();

        cut.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Stale");
        cut.Find("[data-testid=ir-stale]").TextContent.Should().Contain("changed");
        _session.Snapshot!.TasksHash.Should().Be(firstHash, "a stale result is not silently replaced");

        cut.Find("[data-testid=ir-reanalyze]").Click();

        cut.Find("[data-testid=ir-status]").GetAttribute("data-state").Should().Be("Current");
        _session.Report!.TotalTasks.Should().Be(5);
        cut.FindAll("[data-testid=ir-stale]").Should().BeEmpty();
    }

    [Fact]
    public void RiskTab_UsesTestPriorityNotRisk()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Find("[data-testid=ir-tab-impact]").Click();

        cut.Find("[data-testid=ir-risk-scope]").TextContent.Should().Contain("not a measured risk");
        cut.Find("[data-testid=ir-priority-high] .ir-card-label").TextContent.Should().Be("High test priority");
        cut.Markup.Should().NotContain("high risk").And.NotContain("High Risk");
        cut.Find("[data-testid=ir-tab-impact]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("#ir-panel-impact").GetAttribute("role").Should().Be("tabpanel");
    }

    [Fact]
    public void ZeroFindings_ShowsNoFindingsState()
    {
        _tasks = """
            # Tasks

            - [ ] T001 Create Sample.sln and the Api.csproj project skeleton
            - [ ] T002 Implement sign-in flow for FR-001
            """;

        var cut = Render<TaskToSpecAlignment>();

        cut.Find("[data-testid=ir-card-findings] .ir-card-value").TextContent.Should().Be("0");
        cut.Find("[data-testid=ir-zero-findings]").TextContent.Should().Contain("No findings");
        cut.Find("[data-testid=ir-filters] [data-filter=findings]").Click();
        cut.Find("[data-testid=ir-filter-empty]").TextContent.Should().Contain("No findings");
    }

    [Fact]
    public void Export_PassesSnapshotAndCurrentness()
    {
        var cut = Render<TaskToSpecAlignment>();

        cut.Find("[data-testid=ir-export]").Click();

        _export.Verify(e => e.ExportImplementationReview(
            It.IsAny<AlignmentReport>(), "Sample Project", _session.Snapshot, TaskAlignmentCurrentness.Current), Times.Once);
    }

    [Fact]
    public void ExportHtml_SeparatesFindingsFromTaskResultsAndStatesCurrentness()
    {
        Render<TaskToSpecAlignment>();
        var report = _session.Report!;

        var html = new ReportExportService().ExportImplementationReview(report, "Sample Project", _session.Snapshot, TaskAlignmentCurrentness.Stale);

        html.Should().Contain("Stale").And.Contain("Task analysis results").And.Contain("<h2>Findings</h2>")
            .And.Contain(TaskAlignmentSessionService.ShortHash(_session.Snapshot!.TasksHash))
            .And.Contain("Direct spec link").And.Contain("not coverage");
        html.Should().NotContain("Confidence").And.NotContain("Task Findings").And.NotContain("High Risk");
    }

    [Fact]
    public void Session_NotRunForOtherProject_StaleForChangedArtifact()
    {
        var session = new TaskAlignmentSessionService();
        session.GetCurrentness("A", "spec", "tasks").Should().Be(TaskAlignmentCurrentness.NotRun);

        session.SaveResult(new AlignmentReport(), "A", "spec", "tasks");

        session.GetCurrentness("A", "spec", "tasks").Should().Be(TaskAlignmentCurrentness.Current);
        session.GetCurrentness("A", "spec v2", "tasks").Should().Be(TaskAlignmentCurrentness.Stale);
        session.GetCurrentness("A", "spec", "tasks v2").Should().Be(TaskAlignmentCurrentness.Stale);
        session.GetCurrentness("B", "spec", "tasks").Should().Be(TaskAlignmentCurrentness.NotRun);
        session.HasCurrentResult("A", "spec", "tasks").Should().BeTrue();
    }
}
