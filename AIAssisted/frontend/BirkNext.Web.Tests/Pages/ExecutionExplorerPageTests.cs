using BirkNext.ExecutionEvidence;
using BirkNext.ExecutionExplorer;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Test Execution &amp; Evidence: four areas, the empty state, backend-owned outcomes with colour-independent labels, server-side paging and filters in
/// the URL, the unified run detail (stages, recorded timeline with unobserved stages, traceability, report, owner link), comparison, and that reading
/// never starts anything. The page shows what the backend projected; it never computes an outcome.
/// </summary>
public sealed class ExecutionExplorerPageTests : BunitContext
{
    private readonly Mock<IExecutionExplorerApiService> _explorer = new();
    private readonly Mock<IExecutionEvidenceApiService> _evidence = new();
    private readonly List<ExplorerFilter> _queries = [];
    private ExplorerOverview _overview = new();

    private static ExplorerRun Run(string id, ExplorerSource source, ExplorerOutcome outcome, string name, ExplorerCategory category = ExplorerCategory.FunctionalTest) => new()
    {
        Source = source, Category = category, SourceRunId = id, DisplayId = id[..Math.Min(8, id.Length)], Name = name, StartedAt = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero),
        DurationMs = 1500, EnvironmentId = "env-a", Outcome = outcome, SourceStatus = outcome.ToString(), Total = 3, Passed = 2, Failed = 1, CountLabel = "calls",
        Capabilities = [ExplorerCapability.Compare, ExplorerCapability.Timeline], OwnerHref = "api-quality-review/contracts", OwnerLabel = "Contract Testing", SuiteRef = "contract-chain:abc@r2",
    };

    public ExecutionExplorerPageTests()
    {
        Services.AddSingleton(_explorer.Object);
        Services.AddSingleton(_evidence.Object);
        _explorer.Setup(e => e.OverviewAsync(It.IsAny<ExplorerFilter>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => (_overview, null));
        _explorer.Setup(e => e.RunsAsync(It.IsAny<ExplorerFilter>(), It.IsAny<CancellationToken>()))
            .Callback((ExplorerFilter f, CancellationToken _) => _queries.Add(f))
            .ReturnsAsync((ExplorerFilter f, CancellationToken _) => (new ExplorerPage(
                [Run("11111111-aaaa", ExplorerSource.ContractChain, ExplorerOutcome.Failed, "Chain · lock"), Run("22222222-bbbb", ExplorerSource.SecurityReview, ExplorerOutcome.Passed, "Static review", ExplorerCategory.StaticAssessment)],
                45, f.Skip, f.Take, [new(ExplorerSource.Performance, ProviderState.Unavailable, 0, "down")], []), null));
        _explorer.Setup(e => e.ReportUrl(It.IsAny<ExplorerSource>(), It.IsAny<string>())).Returns((ExplorerSource s, string id) => $"http://localhost/api/execution-explorer/runs/{s}/{id}/report");
        _evidence.Setup(e => e.StorageAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ExecutionStorageUsage(2, 9, 11, 4096, 73728, 1, null, null, 0, 0, "PostgreSQL", ""), null));
    }

    private IRenderedComponent<ExecutionResults> Open(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("execution-results" + query);
        var page = Render<ExecutionResults>();
        page.WaitForAssertion(() => page.Find("[data-testid=xr-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void An_empty_scope_shows_guidance_not_retention_controls_or_a_run_button()
    {
        var page = Open("");
        page.Find("h1").TextContent.Should().Be("Test Execution & Evidence");
        page.FindAll("[data-testid^=xr-area-]").Select(a => a.TextContent).Should().HaveCount(4);
        page.Find("[data-testid=xr-area-overview]").GetAttribute("aria-current").Should().Be("page");
        page.Find("[data-testid=xr-empty]").TextContent.Should().Contain("No execution results yet.").And.Contain("Actual test results appear here after an authorized execution.");
        page.FindAll("[data-testid=xr-empty-links] a").Select(a => a.GetAttribute("href")).Should().Equal("api-quality-review/contracts", "integration-suites?area=generate", "performance-test-review", "parquet-viewer");
        page.FindAll("[data-testid=xr-retention]").Should().BeEmpty("retention lives under Storage & Retention");
        page.FindAll("button").Select(b => b.TextContent).Should().NotContain(t => t.Contains("Run", StringComparison.Ordinal), "no Run action without a selected test and target");
        _evidence.Verify(e => e.StartAsync(It.IsAny<ExecutionRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void The_overview_shows_backend_counts_static_assessments_apart_and_an_unavailable_owner()
    {
        _overview = new ExplorerOverview
        {
            TotalRuns = 7, PassedRuns = 3, FailedRuns = 2, PartialRuns = 1, NotAssessedRuns = 1, TestsExecuted = 40, StaticAssessments = 2,
            Latest = Run("11111111-aaaa", ExplorerSource.ContractChain, ExplorerOutcome.Failed, "Chain · lock"),
            Attention = [Run("11111111-aaaa", ExplorerSource.ContractChain, ExplorerOutcome.Failed, "Chain · lock")],
            Coverage = [new(ExplorerCategory.IntegrationTest, 3, 1, 1, 1, 0, 0, "of 3 integration run(s)", "1 partial (subflow or unobserved stage)")],
            Providers = [new(ExplorerSource.Performance, ProviderState.Unavailable, 0, "down")], Projects = ["synth.skole"], Environments = ["env-a"],
        };
        var page = Open("?area=overview");
        page.Find("[data-testid=xr-metric-total] .xr-metric-value").TextContent.Should().Be("7");
        page.Find("[data-testid=xr-metric-partial] .xr-metric-value").TextContent.Should().Be("1");
        page.Find("[data-testid=xr-metric-stored] .xr-metric-value").TextContent.Should().Be("4.0 KB", "stored evidence is the measured JSON, never the table allocation");
        page.FindAll("[data-testid=xr-metric-tables]").Should().BeEmpty("database allocation belongs under Storage & Retention");
        page.Find("[data-testid=xr-static-count]").TextContent.Should().Contain("2 assessment(s)").And.Contain("not executions");
        page.Find("[data-testid=xr-attention] [data-testid=xr-outcome]").TextContent.Should().Contain("✕").And.Contain("Failed");
        page.Find("[data-testid=xr-coverage-row]").TextContent.Should().Contain("of 3 integration run(s)").And.Contain("1 partial");
        page.Find("[data-testid=xr-provider-unavailable]").TextContent.Should().Contain("Performance");
        page.Find("[data-testid=xr-scope-project]").InnerHtml.Should().Contain("synth.skole");
    }

    [Fact]
    public void Runs_are_paged_and_filtered_by_the_backend_with_the_filters_in_the_url()
    {
        var page = Open("?area=runs&rq=lock&rsrc=ContractChain&rout=Failed&rpage=2&env=env-a&range=7");
        var q = _queries.Last();
        (q.Search, q.Skip, q.Take, q.EnvironmentId).Should().Be(("lock", 20, 20, "env-a"));
        q.Sources.Should().Equal(ExplorerSource.ContractChain);
        q.Outcomes.Should().Equal(ExplorerOutcome.Failed);
        q.From.Should().NotBeNull();
        page.Find("[data-testid=xr-upage]").TextContent.Should().Be("Page 2 of 3");
        page.Find("[data-testid=xr-chip-source][data-value=ContractChain]").GetAttribute("aria-pressed").Should().Be("true");
        page.Find("[data-testid=xr-chip-source][data-value=Performance]").GetAttribute("href").Should().Contain("rsrc=ContractChain%2CPerformance").And.NotContain("rpage=", "a filter change starts on page 1");
        page.FindAll("[data-testid=xr-urun]").Select(r => r.GetAttribute("data-outcome")).Should().Equal("Failed", "Passed");
        page.Find("[data-testid=xr-urun-assessment]").TextContent.Should().Be("assessment");
        page.Find("[data-testid=xr-urun-open]").GetAttribute("href").Should().Be("execution-results?source=ContractChain&id=11111111-aaaa");
        page.Find("[data-testid=xr-provider-unavailable]").Should().NotBeNull("an unavailable owner is stated, never shown as zero results");
    }

    [Fact]
    public void A_composite_run_detail_shows_stages_an_unobserved_stage_traceability_report_and_the_call_explorer()
    {
        var runId = Guid.Parse("33333333-0000-0000-0000-000000000001");
        var run = Run(runId.ToString("D"), ExplorerSource.CompositeE2E, ExplorerOutcome.Partial, "Composite · Skolenærvær", ExplorerCategory.IntegrationTest) with
        { CountLabel = "stages", Capabilities = [ExplorerCapability.Stages, ExplorerCapability.Timeline], OwnerHref = "integration-suites?area=execute&suite=x", OwnerLabel = "Integration Test Suites" };
        _explorer.Setup(e => e.RunAsync(ExplorerSource.CompositeE2E, runId.ToString("D"), It.IsAny<CancellationToken>())).ReturnsAsync((new ExplorerRunDetail
        {
            Run = run, ItemsLabel = "Stages", ExecutionRunId = runId,
            Items = [new("altinn", "Altinn submission", "Stage", "Succeeded", null, null), new("mm", "MM routing", "Stage", "TimedOut", "not observed", null)],
            Timeline = [new(DateTimeOffset.UtcNow, "altinn", "Submitted POST (attempt 1)", "ResponseReceived", "call 1", "X-BirkNext-Execution-Id x.1.1"), new(null, "mm", "Not observed by this run", "TimedOut", null, "none")],
            Traceability = [new("Contract version", "synthetic-altinn@v1", "exercised by the run", "api-quality-review/contracts")],
            Limitations = ["Subflow test: never a full end-to-end result."],
        }, null));
        var page = Open($"?source=CompositeE2E&id={runId:D}&dtab=timeline");
        page.Find("[data-testid=xr-udetail-outcome]").TextContent.Should().Contain("◐").And.Contain("Partial");
        page.Find("[data-testid=xr-open-calls]").GetAttribute("href").Should().Be($"execution-results?run={runId}");
        page.Find("[data-testid=xr-report]").GetAttribute("href").Should().EndWith($"/runs/CompositeE2E/{runId:D}/report");
        page.Find("[data-testid=xr-owner]").TextContent.Should().Be("Open in Integration Test Suites");
        page.FindAll("[data-testid=xr-tl-event]").Select(e => e.GetAttribute("data-observed")).Should().Equal("true", "false");
        page.FindAll("[data-testid=xr-tl-event]")[1].TextContent.Should().Contain("not observed");
        page.FindAll("[data-testid^=xr-dtab-]").Select(t => t.TextContent).Should().Equal("Summary", "Stages", "Timeline", "Traceability", "Technical details");
        page.Find("[data-testid=xr-udetail-limitations]").TextContent.Should().Contain("never a full end-to-end result");
    }

    [Fact]
    public void Comparison_offers_only_runs_of_the_same_definition_and_shows_a_refusal()
    {
        var current = Run("cccc0000-0001", ExplorerSource.ContractChain, ExplorerOutcome.Failed, "Chain · lock");
        _explorer.Setup(e => e.RunAsync(ExplorerSource.ContractChain, "cccc0000-0001", It.IsAny<CancellationToken>())).ReturnsAsync((new ExplorerRunDetail { Run = current }, null));
        _explorer.Setup(e => e.CompareAsync(ExplorerSource.ContractChain, "11111111-aaaa", "cccc0000-0001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ExplorerComparison(null, null, false, ["Different Target Environments: results are not comparable."], [], []), null));
        var page = Open("?source=ContractChain&id=cccc0000-0001&cmp=11111111-aaaa");
        _queries.Last().Search.Should().Be("contract-chain:abc@r2", "candidates are runs of the same definition");
        _queries.Last().Sources.Should().Equal(ExplorerSource.ContractChain);
        page.FindAll("[data-testid=xr-ucompare-pick]").Should().ContainSingle("the static review of another owner is filtered out by source in the backend; the current run is excluded");
        page.Find("[data-testid=xr-ucompare-refused]").TextContent.Should().Contain("Target Environments");
    }

    [Fact]
    public void Legacy_history_links_open_storage_and_retention_and_the_header_keeps_one_name()
    {
        var page = Open("?hq=x");
        page.Find("[data-testid=xr-area-storage]").GetAttribute("aria-current").Should().Be("page");
        page.Find("[data-testid=xr-storage-intro]").TextContent.Should().Contain("database tables are PostgreSQL's allocation");
        page.FindAll("[data-testid=xr-scope]").Should().BeEmpty("storage operations are not scoped by the result filters");
        _explorer.Verify(e => e.RunsAsync(It.IsAny<ExplorerFilter>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
