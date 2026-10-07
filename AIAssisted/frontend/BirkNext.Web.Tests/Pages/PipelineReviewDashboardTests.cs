using System.Text.Json;
using BirkNext.Applicability;
using BirkNext.PipelineReview;
using BirkNext.Technology;
using BirkNext.Web.Layout;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using D = BirkNext.Web.Services.PipelineReviewDashboard;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Pipeline Review as a dashboard: Needs refresh ≠ N/A ≠ Unsupported ≠ Analysis failed, structure visible in every state, real counts only
/// (never 0 for unknown), optional Azure DevOps enrichment, deterministic flow types, readable conditions, and sidebar/page agreement.
/// </summary>
public sealed class PipelineReviewDashboardTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string FixturePath(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Pages/Fixtures", name));
    private static PipelineReviewResult Shop() => JsonSerializer.Deserialize<PipelineReviewResult>(File.ReadAllText(FixturePath("pipeline-review-shop.json")), Json)!;

    private static readonly Guid SnapshotId = Guid.Parse("5a0b0000-0000-0000-0000-000000000009");
    private readonly Mock<IPipelineReviewApiService> _api = new();
    private PipelineReviewSourceList _sources = new(true, [], null);
    private PipelineReviewResult _result = Shop();

    public PipelineReviewDashboardTests()
    {
        _api.Setup(a => a.SourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _sources);
        _api.Setup(a => a.ReviewAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid? _, bool metadata, CancellationToken _) => (metadata
                ? _result with { Metadata = new PipelineMetadataSummary { State = "Available", Detail = "2 definitions" } }
                : _result, (string?)null));
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "env", Name = "Env" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(new Mock<IReportExportService>().Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PipelineReviewSourceOption Option(int ciCd, int pipelines, string? status = "Partial", string? ciCdStatus = null, string? repository = null, string archive = "M2LB _2_.zip") =>
        new(SnapshotId, archive, "fp", DateTimeOffset.Parse("2026-10-01T11:14:00Z"), ciCd, pipelines, 0, repository, status, ciCdStatus);

    private IRenderedComponent<BirkNext.Web.Pages.PipelineReview> Page(PipelineReviewSourceOption option, PipelineReviewResult result)
    {
        _sources = new(true, [option], SnapshotId);
        _result = result;
        var page = Render<BirkNext.Web.Pages.PipelineReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=pr-status]"));
        return page;
    }

    private static string Status(IRenderedComponent<BirkNext.Web.Pages.PipelineReview> page) =>
        page.Find("[data-testid=pr-status] .sd-pill").GetAttribute("data-status")!;

    private static PipelineReviewResult Stale() => new() { SourceSnapshotId = SnapshotId, State = "NeedsReanalysis", StateReason = "Analyze again." };

    [Fact]
    public void StaleEvidence_IsNeedsRefresh_WithACompactAmberCard_AndTheWholeStructureStillVisible()
    {
        var page = Page(Option(ciCd: 1, pipelines: 3), Stale());

        Status(page).Should().Be("Needs refresh");
        page.Find("[data-testid=pr-status] .visually-hidden").TextContent.Should().Be("Pipeline Review status: ");
        page.Find("[data-testid=pr-status] .sd-pill").ClassList.Should().Contain("sd-pill-partial").And.NotContain("sd-pill-attention", "stale evidence is amber, never red");
        var state = page.Find("[data-testid=pr-state]");
        state.GetAttribute("data-state").Should().Be("NeedsReanalysis");
        page.Find("[data-testid=pr-evidence-versions]").TextContent.Should().Contain("v1").And.Contain("v2");
        page.Find("[data-testid=pr-reanalyze]").GetAttribute("href").Should().Be("source-analysis");

        page.FindAll("[data-testid^=pr-tab-]").Select(t => t.TextContent).Should().Equal("Overview", "Flow", "Gaps", "Tests", "Environments", "Dependencies", "Changes");
        foreach (var tab in new[] { "flow", "gaps", "tests", "environments", "dependencies", "changes" })
        {
            page.Find($"[data-testid=pr-tab-{tab}]").Click();
            page.Find("[data-testid=pr-placeholder]").GetAttribute("data-tab").Should().Be(tab);
            page.Find("[data-testid=pr-placeholder]").TextContent.Should().Contain("Refresh");
        }

        var tiles = page.FindAll("[data-testid=pr-summary-card]").ToDictionary(t => t.GetAttribute("data-key")!, t => t.QuerySelector(".prv-tile-value")!.TextContent);
        tiles["structure"].Should().Be("3", "the older evidence's pipeline-file count is shown, labelled historical");
        page.Find("[data-testid=pr-summary-card][data-key=structure]").TextContent.Should().Contain("historical");
        tiles.Where(t => t.Key != "structure").Should().OnlyContain(t => t.Value == "—", "unknown counts are never 0");
    }

    [Fact]
    public void ReviewedShop_IsPartial_BecauseOfUnresolvedTemplates_AndShowsRealCounts()
    {
        var page = Page(Option(ciCd: 2, pipelines: 4, status: "Ready", ciCdStatus: "Partial", repository: "shop"), Shop());

        Status(page).Should().Be("Partial");
        page.FindAll("[data-testid=pr-provider]").Select(p => p.TextContent).Should().Equal("Pipeline provider: Azure Pipelines");
        var tiles = page.FindAll("[data-testid=pr-summary-card]").ToDictionary(t => t.GetAttribute("data-key")!, t => t.QuerySelector(".prv-tile-value")!.TextContent);
        tiles["provider"].Should().Be("Azure Pipelines");
        tiles["gates"].Should().Be(Shop().Tests.Count.ToString());
        page.Find("[data-testid=pr-summary-card][data-key=gates]").TextContent.Should().Contain("defined, not executed");
        page.FindAll("[data-testid=pr-state]").Should().BeEmpty();
        page.Find("[data-testid=pr-triggers]").TextContent.Should().NotBeEmpty();
    }

    [Fact]
    public void ReviewedWithoutGaps_IsReady()
    {
        var clean = Shop() with { UnresolvedTemplates = [], Findings = [.. PipelineReviewScoring.QualityFindings(Shop().Findings)] };
        var page = Page(Option(ciCd: 2, pipelines: 4, ciCdStatus: "Partial", repository: "shop"), clean);

        Status(page).Should().Be("Ready", "the CI/CD domain's static-reader 'Partial' disclaimer is not partial coverage");
    }

    [Fact]
    public void NoPipelines_IsNA_WithAUsefulEmptyState()
    {
        var page = Page(Option(ciCd: 2, pipelines: 0, ciCdStatus: "NotDetected"), new PipelineReviewResult { State = "NoPipelines", StateReason = "No pipeline definition was found in this snapshot." });

        Status(page).Should().Be("N/A");
        page.Find("[data-testid=pr-state]").TextContent.Should().Contain("No pipeline definition detected").And.Contain("Technology Coverage");
    }

    [Fact]
    public void UnsupportedProvider_IsUnsupported_NotNA()
    {
        var page = Page(Option(ciCd: 2, pipelines: 0, ciCdStatus: "Unsupported"), new PipelineReviewResult { State = "NoPipelines", StateReason = "Pipeline files of an unsupported provider." });

        Status(page).Should().Be("Unsupported");
        page.Find("[data-testid=pr-state]").TextContent.Should().Contain("provider not supported").And.Contain("not a quality failure");
    }

    [Fact]
    public void FailedAnalysis_IsTheOnlyRedState()
    {
        var page = Page(Option(ciCd: 2, pipelines: 0, ciCdStatus: "FailedAnalysis"), new PipelineReviewResult { State = "NoPipelines", StateReason = "The YAML could not be read." });

        Status(page).Should().Be("Analysis failed");
        page.Find("[data-testid=pr-status] .sd-pill").ClassList.Should().Contain("sd-pill-attention");
        page.Find("[data-testid=pr-state]").GetAttribute("data-state").Should().Be("FailedAnalysis");
    }

    [Fact]
    public void SourceCard_ShowsTheProjectName_AndKeepsTheRawArchiveInTechnicalDetails()
    {
        var page = Page(Option(ciCd: 1, pipelines: 2, repository: null), Stale());

        page.Find("[data-testid=pr-source-name]").TextContent.Should().Be("M2LB _2_");
        page.Find("[data-testid=pr-source-card]").TextContent.Should().Contain("Partial").And.Contain("Needs refresh");
        page.Find("[data-testid=pr-snapshot] option").TextContent.Should().Be("M2LB _2_ · 2026-10-01 11:14 UTC · Partial · needs refresh · latest");
        page.Find("[data-testid=pr-technical-body]").HasAttribute("hidden").Should().BeTrue();
        page.Find("[data-testid=pr-technical-body]").TextContent.Should().Contain("M2LB _2_.zip").And.Contain(SnapshotId.ToString());
    }

    [Fact]
    public void AzureDevOpsMetadata_IsOptionalEnrichment_AndShowsWhenIncluded()
    {
        var page = Page(Option(ciCd: 2, pipelines: 4, repository: "shop"), Shop());

        var box = page.Find("[data-testid=pr-metadata]");
        box.GetAttribute("aria-describedby").Should().Be("pr-metadata-help");
        page.Find("#pr-metadata-help").TextContent.Should().Contain("works without it");
        page.Find("[data-testid=pr-enrichment] .prv-enrich-state .sd-pill").GetAttribute("data-status").Should().Be("Not included");
        page.FindAll("[data-testid=pr-tab-flow]").Should().ContainSingle("the source review is usable without metadata");

        box.Change(true);
        page.WaitForAssertion(() => page.Find("[data-testid=pr-enrichment] .prv-enrich-state .sd-pill").GetAttribute("data-status").Should().Be("Included"));
    }

    [Fact]
    public void FlowNodes_AreTypedFromEvidence_AndReadAsAnOrderedList()
    {
        var page = Page(Option(ciCd: 2, pipelines: 4, repository: "shop"), Shop());
        page.Find("[data-testid=pr-tab-flow]").Click();

        var nodes = page.FindAll("[data-testid=pr-flow-node]");
        nodes.Should().NotBeEmpty();
        nodes.Select(n => n.GetAttribute("data-type")).Should().Contain("Deploy").And.OnlyContain(t => Enum.IsDefined(Enum.Parse<FlowNodeType>(t!)));
        page.FindAll("ol.prv-flow").Should().OnlyContain(o => o.GetAttribute("aria-label")!.Contains("in "));
        nodes[0].QuerySelector(".visually-hidden")!.TextContent.Should().MatchRegex(@"step 1 of \d+");
    }

    [Fact]
    public void SingleJobPipelineSteps_AreMergedPerStep_InDefinitionOrder()
    {
        var evidence = new PipelineEvidenceRef { File = "azure-pipelines.yml", Line = 10 };
        var r = new PipelineReviewResult
        {
            Tests =
            [
                new ReviewedTest { Pipeline = "p", Name = "VALIDATE tools", Category = ValidationCategory.Accessibility, Evidence = evidence },
                new ReviewedTest { Pipeline = "p", Name = "VALIDATE tools", Category = ValidationCategory.Security, Evidence = evidence },
                new ReviewedTest { Pipeline = "p", Name = "ZAP", Category = ValidationCategory.Security, Condition = "succeeded()", Evidence = evidence with { Line = 20 } },
                new ReviewedTest { Pipeline = "p", Name = "Unit", Category = ValidationCategory.Unit, Evidence = evidence with { Line = 15 } },
            ],
        };
        var steps = D.Steps(r, "p");
        steps.Select(s => s.Name).Should().Equal("VALIDATE tools", "Unit", "ZAP");
        steps[0].Categories.Should().Equal(ValidationCategory.Accessibility, ValidationCategory.Security);
        D.StepType(steps[0].Categories).Should().Be(FlowNodeType.Test);
        D.StepType(steps[2].Categories).Should().Be(FlowNodeType.Security);
    }

    [Theory]
    [InlineData("succeeded()", "previous steps succeeded")]
    [InlineData("always()", "always (even after a failure or cancellation)")]
    [InlineData("and(succeeded(), eq(variables['RUN_TESTS'], 'true'))", "previous steps succeeded AND RUN_TESTS is 'true'")]
    [InlineData("and(succeeded(), ne(variables.BUILD_BACKEND, 'false'))", "previous steps succeeded AND BUILD_BACKEND is not 'false'")]
    [InlineData("or(failed(), and(succeeded(), eq(variables['A'], '1')))", "(a previous step failed OR (previous steps succeeded AND A is '1'))")]
    public void Conditions_ReadInPlainLanguage(string condition, string expected) => D.ReadableCondition(condition).Should().Be(expected);

    [Theory]
    [InlineData("contains(variables['Build.SourceBranch'], 'release')")]
    [InlineData("eq(variables['A'], 'x'")]
    [InlineData("${{ parameters.run }}")]
    public void UnknownConditions_AreNotGuessed(string condition) => D.ReadableCondition(condition).Should().BeNull();

    [Fact]
    public void Changes_WithOneSnapshot_IsNotAvailable_NotNoChanges()
    {
        var page = Page(Option(ciCd: 2, pipelines: 4, repository: "shop"), Shop());
        page.Find("[data-testid=pr-tab-changes]").Click();
        page.Find("[data-testid=pr-changes]").TextContent.Should().Contain("Only one Source Analysis snapshot").And.NotContain("No pipeline definition change");
    }
}

/// <summary>The sidebar uses the same shared evaluation: outdated CI/CD evidence is "Needs refresh", never N/A.</summary>
public sealed class PipelineReviewSidebarStatusTests : BunitContext
{
    private sealed class Api(ProjectTechnologyCoverage coverage) : ITechnologyCoverageApiService
    {
        public ProjectTechnologyCoverage Coverage { get; set; } = coverage;
        public Task<ProjectTechnologyCoverage?> GetAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<ProjectTechnologyCoverage?>(Coverage);
    }

    private static ProjectTechnologyCoverage Coverage(bool outdated, bool pipeline, int? version) => new()
    {
        EnvironmentId = "env", SourceSnapshotId = Guid.NewGuid(), CiCdEvidenceOutdated = outdated, CiCdEvidenceVersion = version,
        Source = new SourceTechnologyCoverage
        {
            Technologies = pipeline ? [new("pipeline.azuredevops", "Azure Pipelines", TechnologyArea.Pipeline, BirkNext.Applicability.DetectionConfidence.Confirmed, 1, ["azure-pipelines.yml"])] : [],
        },
    };

    [Fact]
    public void Sidebar_GoesFromNA_ToNeedsRefresh_ToApplicable_WithTheEvidence()
    {
        var api = new Api(Coverage(outdated: false, pipeline: false, version: 2));
        Services.AddSingleton<FeatureVisibilityService>();
        Services.AddSingleton<NavigationSectionState>();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "env", Name = "Env" } });
        Services.AddSingleton(context.Object);
        var workspace = BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None());
        Services.AddSingleton(workspace.Object);
        Services.AddSingleton<ITechnologyCoverageApiService>(api);
        Services.AddScoped<ProjectApplicabilityState>();
        var nav = Render<NavMenu>();
        string? Badge() => nav.FindAll("[data-testid=nav-applicability-pipeline-review]").SingleOrDefault()?.TextContent;

        nav.WaitForAssertion(() => Badge().Should().Be("N/A", "an analyzed snapshot without pipeline definitions"));

        api.Coverage = Coverage(outdated: true, pipeline: false, version: null);
        workspace.Raise(w => w.Changed += null);
        nav.WaitForAssertion(() => Badge().Should().Be("Needs refresh"));
        nav.Find("[data-testid=nav-applicability-pipeline-review]").GetAttribute("title").Should().Contain("before CI/CD evidence existed");
        nav.Find("a[href='pipeline-review'] .visually-hidden").TextContent.Should().Be(", Needs refresh");

        api.Coverage = Coverage(outdated: false, pipeline: true, version: 2);
        workspace.Raise(w => w.Changed += null);
        nav.WaitForAssertion(() => Badge().Should().BeNull("an applicable review needs no badge"));
    }

    [Fact]
    public void Evaluator_ReservesNAForNoPipelines_AndNeedsRefreshForOutdatedEvidence()
    {
        var baseInput = new ProjectApplicabilityInput { HasSourceSnapshot = true };
        ApplicabilityEvaluator.Evaluate("pipeline-review", baseInput).Status.Should().Be(ApplicabilityStatus.NotApplicable);
        var outdated = ApplicabilityEvaluator.Evaluate("pipeline-review", baseInput with { CiCdEvidenceOutdated = true, CiCdEvidenceVersion = 1 });
        outdated.Status.Should().Be(ApplicabilityStatus.NeedsRefresh);
        outdated.Reason.Should().Contain("v1").And.Contain("v2");
        ScoreSemantics.Label(ApplicabilityStatus.NeedsRefresh).Should().Be("Needs refresh");
        ScoreSemantics.ExcludedFromQuality(ApplicabilityStatus.NeedsRefresh).Should().BeTrue();
        ApplicabilityEvaluator.Evaluate("pipeline-review", new ProjectApplicabilityInput { CiCdEvidenceOutdated = true }).Status
            .Should().Be(ApplicabilityStatus.NotEnoughEvidence, "without a snapshot there is nothing to refresh");
        TechnologyCoveragePresentation.Tone(ApplicabilityStatus.NeedsRefresh).Should().Be("partial");
    }
}
