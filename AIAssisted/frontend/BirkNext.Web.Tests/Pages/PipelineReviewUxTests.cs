using System.Text.Json;
using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Pipeline Review information architecture over real backend output (the generic Contoso.Shop fixture): grouped findings with guidance once,
/// per-pipeline collapsed flow, summary-first tests (exists ≠ gates), compact environments with source/runtime limits, compact triggers,
/// comparison-first changes and technical details collapsed. Counts always equal the underlying review.
/// </summary>
public sealed class PipelineReviewUxTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static PipelineReviewResult Shop() => JsonSerializer.Deserialize<PipelineReviewResult>(
        File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Pages/Fixtures", "pipeline-review-shop.json"))), Json)!;

    private static readonly Guid SnapshotId = Guid.Parse("5a0b0000-0000-0000-0000-000000000001");
    private static readonly Guid PreviousId = Guid.Parse("5a0b0000-0000-0000-0000-000000000002");
    private readonly Mock<IPipelineReviewApiService> _api = new();
    private PipelineReviewResult _review = Shop();

    public PipelineReviewUxTests()
    {
        var sources = new PipelineReviewSourceList(true, [new(SnapshotId, "shop.zip", "abc", DateTimeOffset.Parse("2026-10-02T10:00:00Z"), 2, 4, 4, "shop"),
            new(PreviousId, "shop-old.zip", "def", DateTimeOffset.Parse("2026-10-01T10:00:00Z"), 2, 4, 4, "shop")], SnapshotId);
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

    private static PipelineReviewFinding Finding(string id, string title, string? env = "QA", PipelineFindingSeverity severity = PipelineFindingSeverity.High) => new()
    {
        Id = id, Category = PipelineFindingCategory.TestGatingGap, Severity = severity, EvidenceState = ArchitectureEvidenceState.Confirmed, Title = title,
        WhyItMatters = "Nothing on the path to this deployment runs tests.", Explanation = $"No test before `{id}`.", Pipelines = [$"p-{id}"], Environment = env,
        SuggestedAction = "Confirm which validation should run before this deployment.",
    };

    [Fact]
    public void SameRuleFindingsGroupWithGuidanceOnce_AndCountsArePreserved()
    {
        var findings = Enumerable.Range(1, 7).Select(i => Finding($"q{i}", "QA deployment is reachable without any detected test"))
            .Concat(Enumerable.Range(1, 3).Select(i => Finding($"d{i}", "DEV deployment is reachable without any detected test", "DEV", PipelineFindingSeverity.Medium))).ToList();

        var groups = PipelineReviewGrouping.Group(findings);

        groups.Should().HaveCount(2);
        groups[0].Title.Should().Be("QA deployment is reachable without any detected test");
        groups[0].Findings.Should().HaveCount(7);
        groups[0].SuggestedAction.Should().Be("Confirm which validation should run before this deployment.");
        groups.Sum(g => g.Findings.Count).Should().Be(findings.Count);
    }

    [Fact]
    public void GroupTitleDropsInstanceNames_AndFallsBackToTheRuleLabel()
    {
        PipelineReviewGrouping.GroupTitle(["No post-deployment validation detected after the QA deployment in `a`", "No post-deployment validation detected after the QA deployment in `b`"],
            PipelineFindingCategory.PostDeployValidationGap, "QA").Should().Be("No post-deployment validation detected after the QA deployment");
        PipelineReviewGrouping.GroupTitle(["`x` lacks tests", "`y` lacks tests"], PipelineFindingCategory.TestGatingGap, "QA")
            .Should().Be($"{PipelineReviewText.Label(PipelineFindingCategory.TestGatingGap)} · QA");
    }

    [Fact]
    public void OverviewSummarizesInsteadOfListingEveryFinding()
    {
        var page = Page();
        var overview = page.Find("[data-testid=pr-overview]");

        overview.QuerySelectorAll("[data-testid=pr-finding]").Should().BeEmpty("instances live in Gaps");
        page.Find("[data-testid=pr-counts]").TextContent.Should().Contain($"{_review.Findings.Count(f => f.Severity == PipelineFindingSeverity.High)} High");
        page.FindAll("[data-testid=pr-top-group]").Count.Should().BeLessThanOrEqualTo(4);
        page.Find("[data-testid=pr-all-gaps]").TextContent.Should().Be($"View all {_review.Findings.Count} findings in {PipelineReviewGrouping.Group(_review.Findings).Count} groups");
        // Triggers and artifacts are counts first; raw expressions and names are in collapsed details.
        page.Find("[data-testid=pr-trigger-summary]").TextContent.Should().NotBeEmpty();
        page.Find("[data-testid=pr-triggers]").Closest("details")!.HasAttribute("open").Should().BeFalse();
        page.Find("[data-testid=pr-artifacts]").Closest("details")!.HasAttribute("open").Should().BeFalse();
        page.Find("[data-testid=pr-artifact-summary]").TextContent.Should().Contain("Defined in pipeline source").And.Contain("Actual publicationNot verified");
        page.Find("[data-testid=pr-path-note]").TextContent.Should().Contain("not deployment history").And.Contain("no runtime execution evidence");
    }

    [Fact]
    public void GapsAreGroupedByRule_AndEveryUnderlyingFindingIsKept()
    {
        var page = Page("gaps");

        var groups = page.FindAll("[data-testid=pr-gap-group]");
        groups.Sum(g => int.Parse(g.GetAttribute("data-count")!)).Should().Be(_review.Findings.Count);
        page.FindAll("[data-testid=pr-finding]").Should().HaveCount(_review.Findings.Count);
        groups.Should().OnlyContain(g => !g.HasAttribute("open"), "groups start collapsed");
        foreach (var group in groups)
        {
            group.QuerySelectorAll("[data-testid=pr-finding]").Should().HaveCount(int.Parse(group.GetAttribute("data-count")!));
            group.TextContent.Split("Why it matters").Length.Should().Be(2, "guidance appears once per group, not per instance");
        }
        page.Find("[data-testid=pr-gaps-grouping]").TextContent.Should().StartWith($"{_review.Findings.Count} findings in {PipelineReviewGrouping.Group(_review.Findings).Count} groups");
    }

    [Fact]
    public void GapFiltersNarrowBySeverityEnvironmentAndSearch()
    {
        var page = Page("gaps");

        page.Find("[data-testid=pr-gap-severity]").Change("High");
        page.FindAll("[data-testid=pr-finding]").Should().HaveCount(_review.Findings.Count(f => f.Severity == PipelineFindingSeverity.High));
        page.Find("[data-testid=pr-gap-severity]").Change("");
        page.Find("[data-testid=pr-gap-environment]").Change("QA");
        page.FindAll("[data-testid=pr-finding]").Should().HaveCount(_review.Findings.Count(f => f.Environment == "QA"));
        page.Find("[data-testid=pr-gaps-filtered]").TextContent.Should().Contain($"of {_review.Findings.Count} findings");
    }

    [Fact]
    public void FlowIsGroupedByPipelineAndCollapsed_WithTheFullStoryKeptBehindDetails()
    {
        var page = Page("flow");

        var pipelines = page.FindAll("[data-testid=pr-flow-pipeline]");
        pipelines.Should().HaveCount(_review.Pipelines.Count(p => !p.IsTemplate));
        pipelines.Should().OnlyContain(p => !p.HasAttribute("open"));
        pipelines[0].QuerySelector("summary")!.TextContent.Should().Contain("Trigger").And.Contain("Gate").And.Contain("Status");
        page.Find("[data-testid=pr-story-details]").HasAttribute("open").Should().BeFalse();
        page.FindAll("[data-testid=pr-story-step]").Should().HaveCount(_review.Story.Count, "evidence statements are kept, not removed");

        page.Find("[data-testid=pr-flow-search]").Input("shop-build");
        page.Find("[data-testid=pr-flow-count]").TextContent.Should().StartWith("Showing 1 of");
    }

    [Fact]
    public void TestsLeadWithTheDifferenceBetweenExistingAndGating()
    {
        var page = Page("tests");

        page.Find("[data-testid=pr-tests-distinction]").TextContent.Should().Contain("A test that exists is not a test that gates deployment");
        var counts = page.FindAll("[data-testid=pr-gate-counts] li");
        counts.Sum(li => int.Parse(li.QuerySelector("strong")!.TextContent)).Should().Be(_review.Tests.Count);
        page.FindAll("[data-testid=pr-test-row]").Select(r => PipelineReviewGrouping.Priority(Enum.Parse<TestGateSummary>(r.GetAttribute("data-gate")!))).Should()
            .BeInAscendingOrder("review-relevant first");
        page.Find("[data-testid=pr-test-gate]").Change(nameof(TestGateSummary.RunsAfterDeployment));
        page.FindAll("[data-testid=pr-test-row]").Should().OnlyContain(r => r.GetAttribute("data-gate") == nameof(TestGateSummary.RunsAfterDeployment));
    }

    [Fact]
    public void TestGateWordingFollowsTheReviewsGateStates()
    {
        var evidence = new PipelineEvidenceRef { File = "p.yml", Line = 12 };
        var test = new ReviewedTest { Pipeline = "p", Category = ValidationCategory.Unit, Name = "unit", Gates = ["d1"], Evidence = evidence };
        PipelineReviewResult Review(GateState state) => new()
        {
            Deployments = [new DeploymentReview { Id = "d1", Pipeline = "p", Before = [new ValidationGate(ValidationCategory.Unit, state, "unit", "p", null, "j", null, evidence)] }],
        };

        PipelineReviewGrouping.Gate(test, Review(GateState.Gates)).Should().Be(TestGateSummary.GatesDeployment);
        PipelineReviewGrouping.Gate(test, Review(GateState.SoftGate)).Should().Be(TestGateSummary.MayNotBlock);
        PipelineReviewGrouping.Gate(test, Review(GateState.Conditional)).Should().Be(TestGateSummary.Conditional);
        PipelineReviewGrouping.Gate(test with { Gates = [], RunsAfter = ["d1"] }, Review(GateState.Gates)).Should().Be(TestGateSummary.RunsAfterDeployment);
        PipelineReviewGrouping.Gate(test with { Gates = [] }, Review(GateState.Gates)).Should().Be(TestGateSummary.NotAssociated);
        PipelineReviewGrouping.Label(TestGateSummary.MayNotBlock).Should().Be("May not block");
    }

    [Fact]
    public void EnvironmentsAreCompact_WithFullSentencesInDetailsAndSourceLimitsVisible()
    {
        var page = Page("environments");

        page.FindAll("[data-testid=pr-env-card]").Should().HaveCount(_review.Environments.Count);
        page.Find("[data-testid=pr-approval-notice]").TextContent.Should().Contain("Approval/check metadata not included");
        foreach (var deployment in page.FindAll("[data-testid=pr-deployment]"))
        {
            var primary = deployment.QuerySelector("tr")!.TextContent;
            primary.Should().NotContain(PipelineReviewText.ApprovalNotAssessable, "the long explanation is not repeated on every row");
            primary.Should().NotContain("No rollback exists");
            deployment.QuerySelector("details")!.HasAttribute("open").Should().BeFalse();
        }
        page.FindAll("[data-testid=pr-deployment]").Should().Contain(d => d.QuerySelector("tr")!.TextContent.Contains("Rollback not detected in source"));
        PipelineReviewGrouping.RollbackLabel("No rollback mechanism detected in pipeline source (this does not mean rollback is impossible).").Should().Be("Not detected in source");
        PipelineReviewGrouping.ApprovalLabel($"Deployment job targets environment `qa`. {PipelineReviewText.ApprovalNotAssessable}").Should().Be("Runtime metadata required");
        PipelineReviewGrouping.ApprovalLabel("Something new").Should().Be("Something new", "unknown wording is shown as written, never guessed");
    }

    [Fact]
    public void DependenciesShowTriggerLabels_AndKeepRelationshipStates()
    {
        var page = Page("dependencies");

        var rows = page.FindAll("[data-testid=pr-pipeline-row]");
        rows.Should().Contain(r => r.QuerySelector(".prv-trigger-labels") != null);
        rows.SelectMany(r => r.QuerySelectorAll(".prv-trigger-labels li")).Select(li => li.TextContent).Should().OnlyContain(l =>
            l == "Manual" || l == "Push" || l == "Pull request" || l == "Scheduled" || l == "Pipeline completion");
        page.FindAll("[data-testid=pr-relationship]").Should().HaveCount(_review.Dependencies.Count);
        page.FindAll("[data-testid=pr-relationship] .sd-pill, [data-testid=pr-relationship] [class*=pill]").Should().NotBeEmpty();
        page.Find("[data-testid=pr-coverage-details]").HasAttribute("open").Should().BeFalse();
    }

    [Fact]
    public void ChangesIsComparisonFirst_AndTechnicalDetailsStayCollapsedOnEveryTab()
    {
        var page = Page("changes");
        page.Find("[data-testid=pr-compare-hint]").TextContent.Should().Contain("Choose the snapshot to compare with");
        page.Find("[data-testid=pr-compare-current]").TextContent.Should().Contain("shop");

        foreach (var tab in new[] { "overview", "flow", "gaps", "tests", "environments", "dependencies", "changes" })
        {
            page.Find($"[data-testid=pr-tab-{tab}]").Click();
            page.Find("[data-testid=pr-technical-body]").HasAttribute("hidden").Should().BeTrue($"technical details are collapsed on {tab}");
            page.FindAll("details[open]").Should().BeEmpty($"no disclosure starts open on {tab}");
        }
    }

    [Fact]
    public void ComparingTheOldestSnapshotWaitsForAnExplicitChoice()
    {
        // Regression: with no earlier snapshot the select showed the first option as if chosen while Compare stayed disabled.
        _api.Setup(a => a.SourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineReviewSourceList(true,
            [new(SnapshotId, "shop.zip", "abc", DateTimeOffset.Parse("2026-10-02T10:00:00Z"), 2, 4, 4, "shop"),
             new(PreviousId, "shop-old.zip", "def", DateTimeOffset.Parse("2026-10-01T10:00:00Z"), 2, 4, 4, "shop")], PreviousId));
        var page = Page("changes");

        var select = page.Find("[data-testid=pr-compare-previous]");
        select.QuerySelector("option")!.TextContent.Should().Be("Choose a snapshot");
        page.Find("[data-testid=pr-compare]").HasAttribute("disabled").Should().BeTrue();
        select.Change(SnapshotId.ToString());
        page.Find("[data-testid=pr-compare]").HasAttribute("disabled").Should().BeFalse();
        page.Find("[data-testid=pr-compare-previous]").QuerySelectorAll("option").Select(o => o.TextContent).Should().NotContain("Choose a snapshot");
    }

    [Fact]
    public void SourceOnlyReviewNeverClaimsRuntimeResults_AndMetadataStaysOptional()
    {
        var page = Page();

        page.Find("[data-testid=pr-metadata]").HasAttribute("checked").Should().BeFalse();
        _api.Verify(a => a.ReviewAsync(It.IsAny<string>(), It.IsAny<Guid?>(), true, It.IsAny<CancellationToken>()), Times.Never);
        page.Find("[data-testid=pr-ready-meaning]").TextContent.Should().Contain("does not mean the pipelines passed");
        page.Find("[data-testid=pr-boundary]").TextContent.Should().Contain("does not prove that a pipeline ran");
        page.Markup.Should().NotContain("Deployment succeeded").And.NotContain("Tests passed");
    }

    [Fact]
    public void ANonAzurePipelinesReviewRendersThroughTheSameViews()
    {
        _review = _review with { Pipelines = [.. _review.Pipelines.Select(p => p with { Platform = PipelinePlatform.GitHubActions })] };
        var page = Page("flow");

        page.FindAll("[data-testid=pr-flow-pipeline]").Should().HaveCount(_review.Pipelines.Count(p => !p.IsTemplate));
        page.Find("[data-testid=pr-tab-gaps]").Click();
        page.FindAll("[data-testid=pr-finding]").Should().HaveCount(_review.Findings.Count);
    }
}
