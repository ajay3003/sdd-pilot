using System.Text.Json;
using BirkNext.PipelineReview;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Pipeline Review page over the review the backend produces for the generic Contoso.Shop fixture (serialized backend output, so the page is
/// tested against real review semantics): source prerequisite, summary-first overview with the path probe, text-first flow with evidence
/// drilldown, gaps grouped by severity with separate evidence state, the gating matrix (symbol + word), environments, dependencies, changes, export.
/// </summary>
public sealed class PipelineReviewPageTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string FixturePath(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Pages/Fixtures", name));
    private static PipelineReviewResult Shop() => JsonSerializer.Deserialize<PipelineReviewResult>(File.ReadAllText(FixturePath("pipeline-review-shop.json")), Json)!;
    private static PathProbeResult Probe() => JsonSerializer.Deserialize<PathProbeResult>(File.ReadAllText(FixturePath("pipeline-review-shop-probe.json")), Json)!;

    private static readonly Guid SnapshotId = Guid.Parse("5a0b0000-0000-0000-0000-000000000001");
    private static readonly Guid PreviousId = Guid.Parse("5a0b0000-0000-0000-0000-000000000002");
    private readonly Mock<IPipelineReviewApiService> _api = new();
    private readonly Mock<IReportExportService> _export = new();
    private PipelineReviewSourceList _sources = new(true, [new(SnapshotId, "shop.zip", "abc", DateTimeOffset.Parse("2026-10-02T10:00:00Z"), 2, 4, 4, "shop"),
        new(PreviousId, "shop-old.zip", "def", DateTimeOffset.Parse("2026-10-01T10:00:00Z"), 2, 4, 4, "shop")], SnapshotId);

    public PipelineReviewPageTests()
    {
        _api.Setup(a => a.SourcesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _sources);
        _api.Setup(a => a.ReviewAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((Shop(), (string?)null));
        _api.Setup(a => a.ProbeAsync(It.IsAny<string>(), SnapshotId, "src/Shop.Common/Money.cs", It.IsAny<CancellationToken>())).ReturnsAsync(Probe());
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "shop-dev", Name = "Shop DEV" } });
        _export.Setup(e => e.ExportPipelineReview(It.IsAny<PipelineReviewResult>())).Returns("<html></html>");
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(_export.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<BirkNext.Web.Pages.PipelineReview> Page() => Render<BirkNext.Web.Pages.PipelineReview>();

    [Fact]
    public void Without_a_source_snapshot_the_page_asks_for_pipeline_source()
    {
        _sources = new(true, [], null);
        var page = Page();
        page.Find("[data-testid=pr-source-required]").TextContent.Should().Contain("Pipeline source required");
        page.Find("[data-testid=pr-open-source-analysis]").GetAttribute("href").Should().Be("source-analysis");
        _api.Verify(a => a.ReviewAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _sources = new(false, [], null);
        Page().Find("[data-testid=pr-source-disabled]").TextContent.Should().Contain("disabled in Feature Visibility");
    }

    [Fact]
    public void The_overview_is_summary_first_with_needs_attention_counts_and_no_score()
    {
        var page = Page();
        page.Find("[data-testid=pr-overview] h2").TextContent.Should().StartWith("Needs attention");
        page.Find("[data-testid=pr-counts]").TextContent.Should().Be("1 High · 8 Medium · 1 Low · 3 Info");
        page.Find("[data-testid=pr-pipeline-count]").TextContent.Should().Be("4");
        page.FindAll("[data-testid=pr-path] li").Select(li => li.TextContent).Should().Equal("pr-validation", "shop-build", "worker-build", "DEV", "QA", "no detected link", "PROD");
        page.Find("[data-testid=pr-top-findings]").TextContent.Should().Contain("PROD deployment is reachable without any detected test");
        page.Find("[data-testid=pr-gating-summary]").TextContent.Should().Contain("QA").And.Contain("no post-deployment validation");
        page.Markup.Should().NotContain("%").And.NotContain("Score");
        page.Find("[data-testid=pr-boundary]").TextContent.Should().Contain("not that it ran, passed or deployed");
    }

    [Fact]
    public void The_path_probe_shows_which_pipelines_a_change_starts_and_what_gates_each_environment()
    {
        var page = Page();
        page.Find("[data-testid=pr-probe-path]").Input("src/Shop.Common/Money.cs");
        page.Find("[data-testid=pr-probe-run]").HasAttribute("disabled").Should().BeFalse("the button enables while typing, before the field loses focus");
        page.Find("[data-testid=pr-probe-run]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid=pr-probe-row]").Should().HaveCount(4));
        page.Find("[data-testid=pr-probe-row][data-coverage=Yes]").TextContent.Should().Contain("shop-build").And.Contain("shop-qa: Unit tests");
        page.FindAll("[data-testid=pr-probe-row][data-coverage=No]").Should().Contain(r => r.TextContent.Contains("pr-validation"));
    }

    [Fact]
    public void The_flow_is_text_first_with_confidence_and_evidence_drilldown()
    {
        var page = Page();
        page.Find("[data-testid=pr-tab-flow]").Click();
        var steps = page.FindAll("[data-testid=pr-story-step]");
        steps.Should().HaveCount(Shop().Story.Count);
        steps[10].TextContent.Should().Contain("Integration tests run in this pipeline but are not on its path").And.Contain("Confirmed");
        steps[10].QuerySelectorAll("code").Select(c => c.TextContent).Should().Contain("Deploy QA");
        steps[0].QuerySelector("[data-testid=pr-evidence-toggle]")!.GetAttribute("aria-expanded").Should().Be("false");
        steps[10].QuerySelector("[data-testid=pr-evidence-toggle]")!.Click();
        var evidence = page.Find("[data-testid=pr-evidence]");
        evidence.TextContent.Should().Contain("shop/pipelines/templates/deploy-webapp.yml").And.Contain("DeployQA");
        page.FindAll("[data-testid=pr-map] [role=group]").Should().HaveCount(4);
        page.FindAll("[data-testid=pr-map] [role=img]").Should().BeEmpty();
    }

    [Fact]
    public void Gaps_are_grouped_by_severity_with_evidence_state_separate()
    {
        var page = Page();
        page.Find("[data-testid=pr-tab-gaps]").Click();
        page.Find("[data-testid=pr-gaps-High]").TextContent.Should().StartWith("High");
        var high = page.Find("[data-testid=pr-finding][data-severity=High]");
        high.TextContent.Should().Contain("Severity: High").And.Contain("Evidence: Confirmed").And.Contain("Why it matters").And.Contain("Suggested review");
        var unresolved = page.FindAll("[data-testid=pr-finding]").First(f => f.TextContent.Contains("No detectable link from QA to Production"));
        unresolved.TextContent.Should().Contain("Severity: Medium").And.Contain("Evidence: Unresolved");
        page.Find("[data-testid=pr-gap-category]").Change("ContractValidationGap");
        page.FindAll("[data-testid=pr-finding]").Should().ContainSingle().Which.TextContent.Should().Contain("consumer Worker");
    }

    [Fact]
    public void The_tests_view_separates_test_presence_from_gating_in_words_and_symbols()
    {
        var page = Page();
        page.Find("[data-testid=pr-tab-tests]").Click();
        var rows = page.FindAll("[data-testid=pr-matrix-row]");
        var integration = rows.Single(r => r.TextContent.StartsWith("Integration tests"));
        integration.QuerySelectorAll("td").Select(td => td.GetAttribute("data-state")).Should().Equal("None", "After", "None", "None");
        integration.TextContent.Should().Contain("Runs after");
        rows.Single(r => r.TextContent.StartsWith("Unit tests")).TextContent.Should().Contain("✓").And.Contain("Gates");
        page.FindAll("[data-testid=pr-test-row]").Should().Contain(r => r.TextContent.Contains("Integration tests") && r.TextContent.Contains("continueOnError") && r.TextContent.Contains("Runs after DEV"));
    }

    [Fact]
    public void Environments_show_reach_validation_artifact_approval_and_rollback_conservatively()
    {
        var page = Page();
        page.Find("[data-testid=pr-tab-environments]").Click();
        var prod = page.FindAll("[data-testid=pr-deployment]").Single(d => d.TextContent.Contains("shop-prod"));
        prod.TextContent.Should().Contain("No validation detected").And.Contain("not assessable from YAML alone").And.Contain("No rollback mechanism detected");
        prod.TextContent.Should().NotContain("No approval");
        page.Find("[data-testid=pr-progression]").TextContent.Should().Contain("DEV → QA");
    }

    [Fact]
    public void Dependencies_keep_triggers_artifacts_and_likely_follows_apart_and_show_path_coverage()
    {
        var page = Page();
        page.Find("[data-testid=pr-tab-dependencies]").Click();
        page.Find("[data-testid=pr-relationship][data-kind=LikelyFollows]").TextContent.Should().Contain("shop-build").And.Contain("shop-release").And.Contain("without a trigger");
        page.Find("[data-testid=pr-unresolved-templates]").TextContent.Should().Contain("compliance/checks.yml@platform");
        page.FindAll("[data-testid=pr-coverage-row]").Should().Contain(r => r.TextContent.Contains("Shop.Common") && r.TextContent.Contains("pr-validation: Does not start"));
    }

    [Fact]
    public void Changes_compare_two_snapshots_as_source_changes()
    {
        _api.Setup(a => a.CompareAsync("shop-dev", PreviousId, SnapshotId, It.IsAny<CancellationToken>())).ReturnsAsync(new PipelineReviewComparison(PreviousId, SnapshotId,
            [new(PipelineChangeKind.GatingChanged, "shop-build", "Integration tests are no longer on the QA deployment path (`DeployQA`)", "Integration tests removed from the QA dependency path — delivery validation concern.", PipelineFindingSeverity.High)],
            PipelineReviewText.ChangesBoundary));
        var page = Page();
        page.Find("[data-testid=pr-tab-changes]").Click();
        page.Find("[data-testid=pr-changes]").TextContent.Should().Contain("not pipeline runtime failures");
        page.Find("[data-testid=pr-compare]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=pr-change][data-kind=GatingChanged]").TextContent.Should().Contain("High").And.Contain("DeployQA"));
    }

    [Fact]
    public void Export_uses_the_shared_export_engine()
    {
        var page = Page();
        page.Find("[data-testid=pr-export]").Click();
        _export.Verify(e => e.ExportPipelineReview(It.Is<PipelineReviewResult>(r => r.SourceSnapshotId == SnapshotId)), Times.Once);
        JSInterop.VerifyInvoke("downloadHtmlFile");
    }

    [Fact]
    public void The_shared_export_renders_every_section_without_backticks_or_secrets()
    {
        var html = new ReportExportService().ExportPipelineReview(Shop());
        html.Should().Contain("Delivery flow").And.Contain("Gaps").And.Contain("Tests").And.Contain("Environment progression").And.Contain("Artifact lineage").And.Contain("Evidence and limitations");
        html.Should().NotContain("`").And.NotContain("SECRET_SENTINEL");
    }

    [Fact]
    public void Presentation_never_relies_on_colour_alone()
    {
        foreach (var state in Enum.GetValues<GateState>().Cast<GateState?>().Append(null))
        {
            var (symbol, text) = PipelineReviewPresentation.Cell(state);
            symbol.Should().NotBeNullOrEmpty();
            text.Should().NotBeNullOrEmpty();
        }
        PipelineReviewPresentation.Segments("Stage `Build` runs").Should().Equal(("Stage ", false), ("Build", true), (" runs", false));
    }
}
