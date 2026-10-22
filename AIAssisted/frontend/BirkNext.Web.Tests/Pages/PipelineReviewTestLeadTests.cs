using System.Text.Json;
using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Pipeline Review for a test lead (generic Contoso.Shop fixture): compact evidence header where "Review ready" coexists with Partial source
/// coverage, one source-evidence banner, a release path that says "No source relationship detected" (never "no promotion"), plain-language
/// triggers, validation by environment, tests that exist vs gate, a single approval notice, rollback "not detected in source", and grouped
/// source-definition changes. Nothing claims runtime execution.
/// </summary>
public sealed class PipelineReviewTestLeadTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static PipelineReviewResult Shop() => JsonSerializer.Deserialize<PipelineReviewResult>(
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Pages/Fixtures", "pipeline-review-shop.json"))), Json)!;

    private static readonly Guid SnapshotId = Guid.Parse("5a0b0000-0000-0000-0000-000000000001");
    private static readonly Guid OtherId = Guid.Parse("5a0b0000-0000-0000-0000-000000000003");
    private readonly Mock<IPipelineReviewApiService> _api = new();
    private PipelineReviewResult _review = Shop();

    public PipelineReviewTestLeadTests()
    {
        var sources = new PipelineReviewSourceList(true, [new(SnapshotId, "shop.zip", "abc", DateTimeOffset.Parse("2026-10-02T10:00:00Z"), 2, 4, 4, "shop", "Partial"),
            new(OtherId, "billing.zip", "def", DateTimeOffset.Parse("2026-10-01T10:00:00Z"), 2, 4, 4, "billing")], SnapshotId);
        _api.Setup(a => a.SourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(sources);
        _api.Setup(a => a.ReviewAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => (_review, (string?)null));
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "shop-dev", Name = "Shop DEV" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IReportExportService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<BirkNext.Web.Pages.PipelineReview> Page(string? tab = null)
    {
        var page = Render<BirkNext.Web.Pages.PipelineReview>();
        if (tab is not null) page.Find($"[data-testid=pr-tab-{tab}]").Click();
        return page;
    }

    [Fact]
    public void EvidenceHeader_IsCompact_AndReviewReadyIsNotPipelineHealth()
    {
        _review = Shop() with { UnresolvedTemplates = [], Findings = [.. PipelineReviewScoring.QualityFindings(Shop().Findings)] };
        var page = Page();

        page.Find("[data-testid=pr-review-readiness]").TextContent.Should().Contain("Review ready");
        page.Find("[data-testid=pr-source-coverage]").TextContent.Should().Contain("Partial", "review readiness and source coverage are separate and may differ");
        page.Find("[data-testid=pr-runtime-metadata]").TextContent.Should().Contain("Not included");
        page.Find("[data-testid=pr-ready-meaning]").TextContent.Should().Contain("does not mean the pipelines passed");
        page.Find("[data-testid=pr-evidence-details]").HasAttribute("open").Should().BeFalse();
        page.FindAll("[data-testid=pr-source-card], [data-testid=pr-enrichment]").Should().BeEmpty("the three large setup cards are gone");
        page.FindAll("[data-testid=pr-boundary]").Should().ContainSingle("one evidence-scope banner, not repeated warnings");
    }

    [Fact]
    public void ReleasePath_SaysNoSourceRelationshipDetected_NeverNoPromotion()
    {
        var page = Page();

        var path = page.Find("[data-testid=pr-release-path]").TextContent;
        page.FindAll("[data-testid=pr-release-step]").Select(s => s.QuerySelector("strong")!.TextContent).Should().Contain(["DEV deployment", "QA deployment"]);
        path.Should().Contain("gated by tests").And.Contain("post-deployment validation");
        if (_review.Progression.Count == 0) path.Should().Contain("No source relationship detected");
        path.Should().NotContainAny("No promotion exists", "no promotion exists");
        page.Find("[data-testid=pr-path-note]").TextContent.Should().Contain("does not prove that promotion never happens outside source");
    }

    [Fact]
    public void TriggersReadInPlainLanguage_NeverABareNone()
    {
        var page = Page();
        var runnable = _review.Pipelines.Where(p => !p.IsTemplate).ToList();

        page.FindAll("[data-testid=pr-trigger-row]").Should().HaveCount(runnable.Count);
        page.Find("[data-testid=pr-triggers]").TextContent.Should().NotMatchRegex(@"(?i)\bnone\b(?! )|trigger:\s*none|pr:\s*none");

        var reading = PipelineReviewGrouping.ReadTriggers(new ReviewedPipeline
        {
            Triggers = [new PipelineTriggerSummary("none", "trigger: none", [], [], [], null), new PipelineTriggerSummary("none", "pr: none", [], [], [], null)], ManualOnly = true,
        });
        reading.Starts.Should().StartWith("No push trigger");
        reading.PullRequest.Should().Be("None");
        var many = PipelineReviewGrouping.ReadTriggers(new ReviewedPipeline
        {
            Triggers = [new PipelineTriggerSummary("push", "trigger", ["main"], [.. Enumerable.Range(1, 12).Select(i => $"src/Area{i}/**")], [], null)],
        });
        (many.Starts, many.PathScope).Should().Be(("Push to main", "12 paths"));
    }

    [Fact]
    public void TestsHeader_SeparatesDefinedGatingAndRuntime_AndOpensOnTheReviewWorthySubset()
    {
        var page = Page("tests");
        var gates = _review.Tests.Select(t => PipelineReviewGrouping.Gate(t, _review)).ToList();

        var header = System.Text.RegularExpressions.Regex.Replace(page.Find("[data-testid=pr-tests-header]").TextContent, @"\s+", "");
        header.Should().Contain($"Definedinpipelinesource{_review.Tests.Count}").And.Contain($"Gateadeployment{gates.Count(g => g == TestGateSummary.GatesDeployment)}")
            .And.Contain("Runtimeexecution/resultsNotassessedfromsource");
        page.Find("[data-testid=pr-gating-help]").HasAttribute("open").Should().BeFalse();
        var reviewWorthy = gates.Count(g => g is TestGateSummary.MayNotBlock or TestGateSummary.Conditional or TestGateSummary.NotAssessable);
        if (reviewWorthy > 0)
        {
            page.Find("[data-testid=pr-test-gate]").GetAttribute("value").Should().Be("review");
            page.FindAll("[data-testid=pr-test-row]").Should().HaveCount(reviewWorthy);
            page.Find("[data-testid=pr-test-gate]").Change("");
            page.FindAll("[data-testid=pr-test-row]").Should().HaveCount(_review.Tests.Count, "All stays available");
        }
    }

    [Fact]
    public void Environments_StateTheApprovalLimitationOnce_AndRollbackIsNotDetectedInSource()
    {
        var page = Page("environments");

        page.FindAll("[data-testid=pr-approval-notice]").Should().ContainSingle();
        page.Find("[data-testid=pr-environments]").TextContent.Should().NotContain("Runtime metadata required").And.NotContain("Not assessable from source");
        page.FindAll("[data-testid=pr-deployment]").Should().OnlyContain(d => !d.QuerySelector("tr")!.TextContent.Contains("No rollback"), "the row says what source shows, never that rollback is absent");
        if (_review.Progression.Count == 0) page.Find("[data-testid=pr-no-progression]").TextContent.Should().Contain("does not prove promotion does not occur outside source");
    }

    [Fact]
    public void Changes_ExplainPurpose_AndWarnWhenComparingAnotherSource()
    {
        var page = Page("changes");

        page.Find("[data-testid=pr-changes-purpose]").TextContent.Should().Contain("compares two immutable Source Analysis snapshots").And.Contain("does not compare runtime execution outcomes");
        page.Find("[data-testid=pr-compare-previous]").Change(OtherId.ToString());
        page.Find("[data-testid=pr-compare-unrelated]").TextContent.Should().Contain("different source").And.Contain("billing");
        PipelineReviewGrouping.ChangeGroup(PipelineChangeKind.PathFilterChanged).Should().Be("Triggers");
        PipelineReviewGrouping.ChangeAction(PipelineChangeKind.TestRemoved).Should().Be("Removed");
    }

    [Fact]
    public void Findings_ReadAsSourceBasedObservations_GroupedBySeverity()
    {
        var page = Page("gaps");

        page.Find("[data-testid=pr-tab-gaps]").TextContent.Should().Be("Findings");
        page.Find("[data-testid=pr-gaps-meaning]").TextContent.Should().Contain("not proof that a pipeline execution failed");
        page.Find("[data-testid=pr-gap-severity-summary]").TextContent.Should().Contain("group");
        page.FindAll("[data-testid=pr-gap-group]").Should().OnlyContain(g => !g.HasAttribute("open"), "occurrences are collapsed by default");
    }

    [Fact]
    public void Flow_OpensOnePipelineAtATime()
    {
        var page = Page("flow");
        var pipelines = page.FindAll("[data-testid=pr-flow-pipeline]");
        pipelines.Should().OnlyContain(p => !p.HasAttribute("open"), "compact summary rows first");

        pipelines[0].Toggle();
        page.FindAll("[data-testid=pr-flow-pipeline][open]").Should().ContainSingle();
        if (pipelines.Count > 1)
        {
            page.FindAll("[data-testid=pr-flow-pipeline]")[1].Toggle();
            page.FindAll("[data-testid=pr-flow-pipeline][open]").Select(p => p.GetAttribute("data-pipeline")).Should().ContainSingle()
                .Which.Should().Be(page.FindAll("[data-testid=pr-flow-pipeline]")[1].GetAttribute("data-pipeline"));
        }
    }

    [Fact]
    public void TechnicalDetails_ListPipelineFilesOnDemand()
    {
        var page = Page();

        page.Find("[data-testid=pr-tech-file-count]").TextContent.Should().Be(_review.Pipelines.Count.ToString());
        page.Find("[data-testid=pr-tech-files]").HasAttribute("open").Should().BeFalse("29 file paths are not dumped by default");
        page.FindAll("[data-testid=pr-tech-pipeline]").Should().HaveCount(_review.Pipelines.Count);
        page.Find("[data-testid=pr-tech-file-search]").Input(_review.Pipelines[0].Name);
        page.FindAll("[data-testid=pr-tech-pipeline]").Count.Should().BeLessThanOrEqualTo(_review.Pipelines.Count);
    }
}
