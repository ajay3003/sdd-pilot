using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// BirkNext Performance Quality as an FQR engine and as a persisted, page-oriented analysis: outcome semantics (assessed / partial /
/// not assessed / disabled), refresh of exactly one page's generation, persistence round trip without live state, Target Environment
/// isolation, companion-only and proxy-only modes through the evidence source, export content and secret hygiene, and the UI components.
/// </summary>
public sealed class PerformanceQualityEngineTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter() },
    };

    private static BrowserPageEvidence Evidence(string path, DateTimeOffset visit, double lcp = 2900, string observation = "initial-load") => new()
    {
        ProfileId = "dev", PageOrigin = Origin, PagePath = path, VisitStartedAt = visit, CapturedAt = visit.AddSeconds(4), SnapshotSequence = 1, DocumentTitle = $"Page {path}", BrowserName = "Microsoft Edge",
        Performance = new BrowserPerformanceSummary { ObservationType = observation, LcpMs = observation == "initial-load" ? lcp : null, Cls = 0.02, StabilizationMs = 1500, StabilizedBy = "quiet", LongTaskCount = 1, MainThreadBlockingMs = 30, ResourceCount = 20, TransferredBytes = 2_000_000, JsBytes = 500_000, WasmBytes = 1_000_000 },
        Runtime = new BrowserRuntimeSummary(), Blazor = new BrowserBlazorSummary { Detected = true, FrameworkResourceCount = 30, FrameworkBytes = 2_000_000, WasmBytes = 1_000_000, LoadKind = "cold" },
    };

    private static ObservedNetworkEndpoint Endpoint(string pagePath, string apiPath, int count, DateTimeOffset at, double ms = 300, string? gql = null) => new()
    {
        Provenance = RequestProvenance.ApplicationTraffic, Category = gql is null ? ObservedTrafficCategory.Rest : ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "api-dev.bufetat.no", Port = 443, Path = apiPath, Method = gql is null ? "GET" : "POST",
        AuthObserved = true, LastStatus = 200, Count = count, FirstObservedAt = at, LastObservedAt = at.AddSeconds(count), PageOrigin = Origin, PagePath = pagePath, Confidence = ObservedEndpointConfidence.Verified,
        OperationType = gql is null ? GraphQlOperationType.None : GraphQlOperationType.Query, OperationName = gql,
        Samples = Enumerable.Range(0, count).Select(i => new ObservedRequestSample(at.AddSeconds(count - i), ms, 200, 1024)).ToList(), TotalDurationMs = ms * count, MinDurationMs = ms, MaxDurationMs = ms, LastDurationMs = ms,
    };

    private static FrontendAnalysisContext Context(bool performanceQuality = true, string id = "dev")
    {
        var profile = new FrontendAnalysisProfile { Id = id, Name = id.ToUpperInvariant(), EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/", Performance = new() };
        profile.Features.EnableBrowserRuntimeEngine = false; profile.Features.EnableAccessibilityEngine = false; profile.Features.EnableLighthouseEngine = false; profile.Features.EnablePassiveSecurityEngine = false;
        profile.Features.EnableBrowserQualityEngine = false; profile.Features.EnablePerformanceQualityEngine = performanceQuality;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/", FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
            PerformanceThresholds = profile.Performance, CoreWebVitalsThresholds = profile.CoreWebVitals,
            AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [], SecuritySettings = new(),
        };
    }

    // ── Engine outcome semantics ──────────────────────────────────────────────

    [Fact]
    public void Engine_IsOptionalAndOffByDefault_EnabledOnlyBySavedToggle()
    {
        var snapshot = FrontendQualityActiveEngines.Resolve(Context(performanceQuality: false));
        snapshot.Get(FrontendQualityEngineId.PerformanceQuality)!.Should().Match<FrontendQualityEngineActivation>(a => !a.Enabled && a.Policy == FrontendQualityEngineRequirement.Optional);
        new FrontendAnalysisFeatureToggles().EnablePerformanceQualityEngine.Should().BeFalse();
        FrontendQualityActiveEngines.Resolve(Context()).IsActive(FrontendQualityEngineId.PerformanceQuality).Should().BeTrue();
        FrontendQualityActiveEngines.DisplayName(FrontendQualityEngineId.PerformanceQuality).Should().Be("BirkNext Performance Quality");
        var access = FrontendQualityEngineAccessRegistry.For(FrontendQualityEngineId.PerformanceQuality);
        access.Should().Match<FrontendQualityEngineAccessRequirements>(a => !a.RequiresBrowserRuntime && !a.RequiresBrowserDom && !a.RequiresTargetReachability && !a.RequiresPublicHttp, "no Playwright, CDP or target request");
    }

    [Fact]
    public async Task Engine_WithEvidence_IsAssessed_AndReportCarriesFindingsAndResult()
    {
        var page = new PageAnalysis { PageOrigin = Origin, PagePath = "/children", FirstObservedAt = T0, LastObservedAt = T0.AddSeconds(5), BrowserEvidence = Evidence("/children", T0, lcp: 4500) };
        page.Endpoints.Add(Endpoint("/children", "/graphql", 7, T0, ms: 1200, gql: "GetChildren"));
        var evaluated = PerformanceQualityRules.Evaluate(page, new(), new());
        var source = new Mock<IPerformanceQualityEvidenceSource>();
        source.Setup(s => s.CollectAsync(It.IsAny<FrontendAnalysisContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PerformanceQualityReviewResult
        {
            CompanionState = BrowserCompanionState.Connected, CompanionMessage = "1 page(s)", ProxyEvidenceAvailable = true, Pages = [evaluated], Coverage = evaluated.Coverage, EvaluatedAt = DateTimeOffset.UtcNow, BrowserName = "Microsoft Edge",
        });

        var result = await OrchestrationTestHelpers.CreateOrchestrator(performanceQuality: source.Object).RunAsync(Origin + "/", Context());

        var report = result.QualityReport!;
        var outcome = report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.PerformanceQuality);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.None);
        outcome.ToolName.Should().Be("BirkNext Performance Quality");
        outcome.Limitations.Should().Contain(l => l.StartsWith("Assessment coverage: Complete assessment"));
        report.AssessedEngines.Should().Contain("BirkNext Performance Quality");
        report.PerformanceQualityReport.Should().NotBeNull();
        var findings = report.Findings.Where(f => f.EngineId == FrontendQualityEngineId.PerformanceQuality).ToList();
        findings.Should().Contain(f => f.SourceRuleId == "perf-lcp" && f.Category == FrontendQualityCategory.Performance && f.Description.Contains("[Initial load]") && f.Description.Contains("threshold: good ≤ 2.5 s"));
        findings.Should().Contain(f => f.SourceRuleId == "api-duplicate-graphql" && f.Title.Contains("GetChildren"));
        findings.Should().OnlyContain(f => f.SourceSystem == PerformanceQualitySources.EngineName && f.Evidence.Any(e => e.StartsWith("Phase: ")));
        report.PerformanceQualityReport!.Pages.Should().ContainSingle();
        report.IsBlazorWasm.Should().BeTrue();
        JsonSerializer.Serialize(report).Should().NotContainAny("Bearer", "Authorization", "Cookie");
    }

    [Fact]
    public async Task Engine_NoEvidence_IsNotAssessed_WithReason_NeverCompletedWithoutFindings()
    {
        var result = await OrchestrationTestHelpers.CreateOrchestrator(performanceQuality: new OrchestrationTestHelpers.NoEvidencePerformanceQualitySource()).RunAsync(Origin + "/", Context());
        var outcome = result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.PerformanceQuality);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Unavailable);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.PerformanceEvidenceUnavailable);
        outcome.SanitizedFailureReason.Should().Contain("No performance evidence collected");
        outcome.FindingCount.Should().BeNull("no assessment → no zero-finding claim");
        result.QualityReport.AssessedEngines.Should().NotContain("BirkNext Performance Quality");
        result.SkippedEngines.Should().Contain("BirkNext Performance Quality");
    }

    [Fact]
    public async Task Engine_Disabled_IsNotRun_AndReportedDisabled()
    {
        var source = new OrchestrationTestHelpers.AssessedPerformanceQualitySource();
        var result = await OrchestrationTestHelpers.CreateOrchestrator(performanceQuality: source).RunAsync(Origin + "/", Context(performanceQuality: false));
        source.CallCount.Should().Be(0);
        var outcome = result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.PerformanceQuality);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Disabled);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.DisabledInTargetEnvironment);
    }

    [Fact]
    public void Normalizer_PartialCoverage_IsAssessedButLabelledPartial_WithMissingCategories()
    {
        var page = new PageAnalysis { PageOrigin = Origin, PagePath = "/a", BrowserEvidence = Evidence("/a", T0), LastObservedAt = T0 };
        var evaluated = PerformanceQualityRules.Evaluate(page, new(), new());
        var report = new PerformanceQualityReviewResult { Pages = [evaluated], Coverage = evaluated.Coverage, CompanionState = BrowserCompanionState.Connected, EvaluatedAt = DateTimeOffset.UtcNow };
        var outcome = FrontendQualityEngineOutcomeNormalizer.PerformanceQuality(Origin, true, new FrontendQualityEngineRequirementSettings().ToPolicy(), report, null, true);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        outcome.Limitations.Should().Contain(l => l.Contains("Partial assessment") && l.Contains("API not available"));
        outcome.Limitations.Should().Contain(l => l.StartsWith("Network/API: not available"));
    }

    // ── Refresh, persistence, isolation ───────────────────────────────────────

    [Fact]
    public async Task Refresh_ResetsOnlyTheSelectedPagesPerformanceGeneration_OthersUnchanged_HistoryKept()
    {
        var js = new Mock<IJSRuntime>();
        var discovery = new EndpointDiscoveryService();
        var paths = new[] { "/p1", "/p2", "/p3", "/p4", "/p5" };
        await discovery.MergeBrowserEvidenceAsync(js.Object, "dev", paths.Select(p => Evidence(p, T0)).ToList());
        await discovery.MergeObservedAsync(js.Object, "dev", paths.Select(p => Endpoint(p, "/api/x", 3, T0)).ToList());
        discovery.GetSnapshot("dev").Pages.Should().HaveCount(5).And.OnlyContain(p => p.AnalysisGeneration == 1 && p.Endpoints.Count == 1 && p.BrowserEvidence != null && p.PerformanceHistory.Count == 1);

        await discovery.RefreshPageAsync(js.Object, "dev", Origin + "/p3");

        var snapshot = discovery.GetSnapshot("dev");
        var refreshed = snapshot.Pages.Single(p => p.PagePath == "/p3");
        refreshed.AnalysisGeneration.Should().Be(2);
        refreshed.Endpoints.Should().BeEmpty(); refreshed.BrowserEvidence.Should().BeNull(); refreshed.IsWaitingForFreshTraffic.Should().BeTrue();
        refreshed.PerformanceHistory.Should().ContainSingle(h => h.Generation == 1, "the previous generation stays for comparison");
        PerformanceQualityRules.Evaluate(refreshed, new(), new()).HasEvidence.Should().BeFalse("old performance evidence does not reappear");
        foreach (var other in snapshot.Pages.Where(p => p.PagePath != "/p3"))
            other.Should().Match<PageAnalysis>(p => p.AnalysisGeneration == 1 && p.Endpoints.Count == 1 && p.BrowserEvidence != null && p.PerformanceHistory.Count == 1);

        // The live proxy registry is cumulative: an old sample set with one new call after the boundary repopulates only the new call.
        var boundary = refreshed.RefreshedAtUtc!.Value;
        var cumulative = Endpoint("/p3", "/api/x", 4, T0) with { LastObservedAt = boundary.AddSeconds(2), Samples = [new(boundary.AddSeconds(2), 300, 200, 1024), new(T0.AddSeconds(3), 300, 200, 1024), new(T0.AddSeconds(2), 300, 200, 1024), new(T0.AddSeconds(1), 300, 200, 1024)] };
        await discovery.MergeObservedAsync(js.Object, "dev", [cumulative]);
        var repopulated = discovery.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/p3");
        repopulated.Endpoints.Should().ContainSingle().Which.Should().Match<ObservedNetworkEndpoint>(e => e.Count == 1 && e.Samples.Count == 1 && e.FirstObservedAt >= boundary);
        await discovery.MergeBrowserEvidenceAsync(js.Object, "dev", [Evidence("/p3", T0)]);
        discovery.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/p3").BrowserEvidence.Should().BeNull("a visit that started before the refresh never repopulates the page");
        await discovery.MergeBrowserEvidenceAsync(js.Object, "dev", [Evidence("/p3", boundary.AddSeconds(1), lcp: 1500)]);
        var fresh = discovery.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/p3");
        fresh.BrowserEvidence!.Performance!.LcpMs.Should().Be(1500);
        var comparison = PerformanceQualityRules.Evaluate(fresh, new(), new()).Comparison!;
        comparison.CurrentGeneration.Should().Be(2); comparison.PreviousGeneration.Should().Be(1);
        comparison.Deltas.Single(d => d.Metric == "LCP").Should().Match<PerformanceDelta>(d => d.Current == "1.5 s" && d.Previous == "2.9 s" && d.Worse == false);
    }

    [Fact]
    public void Persistence_RoundTrip_KeepsSafeMetricsSamplesAndHistory_NoLiveState()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, [Evidence("/children", T0)], T0);
        EndpointDiscoveryMerge.Merge(snapshot, [Endpoint("/children", "/graphql", 7, T0, ms: 1200, gql: "GetChildren") with { CacheDirectives = "private, max-age=0", HasEtag = true }], T0);
        var store = new Dictionary<string, EndpointDiscoverySnapshot> { ["dev"] = snapshot };

        var json = JsonSerializer.Serialize(store, StoreOptions);
        json.Should().NotContainAny("Bearer", "Authorization", "Cookie", "sessionId", "SessionId", "access_token");
        var restored = JsonSerializer.Deserialize<Dictionary<string, EndpointDiscoverySnapshot>>(json, StoreOptions)!["dev"];
        var page = restored.Pages.Single();
        page.Endpoints.Single().Samples.Should().HaveCount(7);
        page.Endpoints.Single().CacheDirectives.Should().Be("private, max-age=0");
        page.PerformanceHistory.Should().ContainSingle().Which.Should().Match<PagePerformanceHistoryEntry>(h => h.LcpMs == 2900 && h.GraphQlCalls == 7 && h.ApiCalls == 7);
        page.BrowserEvidence!.Performance!.StabilizationMs.Should().Be(1500);

        var before = PerformanceQualityRules.Evaluate(snapshot.Pages.Single(), new(), new());
        var after = PerformanceQualityRules.Evaluate(page, new(), new());
        after.Metrics.Select(m => (m.Id, m.Value, m.Status)).Should().Equal(before.Metrics.Select(m => (m.Id, m.Value, m.Status)));
        after.Findings.Select(f => f.RuleId).Should().Equal(before.Findings.Select(f => f.RuleId));
    }

    [Fact]
    public async Task TargetIsolation_DevEvidenceNeverAppearsUnderQa()
    {
        var js = new Mock<IJSRuntime>();
        var discovery = new EndpointDiscoveryService();
        await discovery.MergeBrowserEvidenceAsync(js.Object, "dev", [Evidence("/children", T0)]);
        await discovery.MergeObservedAsync(js.Object, "dev", [Endpoint("/children", "/api/children", 3, T0)]);
        await discovery.MergeObservedAsync(js.Object, "qa", [Endpoint("/children", "/api/children", 1, T0) with { PageOrigin = "https://m2lbqa.bufetat.no" }]);

        discovery.GetSnapshot("dev").Pages.Should().ContainSingle(p => p.BrowserEvidence != null);
        var qa = discovery.GetSnapshot("qa").Pages.Single();
        qa.BrowserEvidence.Should().BeNull();
        PerformanceQualityRules.Evaluate(qa, new(), new()).Coverage.Browser.Should().Be(PerformanceCoverageState.NotAvailable);
        discovery.GetSnapshot("prod").Pages.Should().BeEmpty();
    }

    [Fact]
    public void GraphQlOperations_ToOneEndpoint_AreSeparateRowsWithOwnCounts()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, [Endpoint("/search", "/graphql", 7, T0, gql: "GetChildren"), Endpoint("/search", "/graphql", 5, T0, gql: "GetRoles")], T0);
        var page = snapshot.Pages.Single();
        page.Endpoints.Should().HaveCount(2);
        var api = PerformanceQualityRules.Evaluate(page, new(), new()).Api;
        api.Operations.Select(o => (o.Display, o.Count)).Should().BeEquivalentTo([("Query GetChildren", 7), ("Query GetRoles", 5)]);
        api.DuplicateOperations.Should().HaveCount(2);
    }

    // ── Evidence source (companion-only, no proxy) ───────────────────────────

    [Fact]
    public async Task EvidenceSource_CompanionOnly_NoProxy_BrowserMetricsWork_ApiUnavailable_NoCrash()
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus
        {
            State = BrowserCompanionState.Connected, ProfileId = "dev", Pages = [Evidence("/children", T0), Evidence("/dashboard", T0.AddMinutes(1), observation: "spa-navigation")],
        });
        var companion = new BrowserCompanionRuntime(api.Object);
        var discovery = new EndpointDiscoveryService();
        var source = new PerformanceQualityEvidenceSource(companion, proxy: null, discovery, new Mock<IJSRuntime>().Object);

        var result = await source.CollectAsync(Context());

        result.Assessed.Should().BeTrue();
        result.PagesWithEvidence.Should().Be(2);
        result.ProxyEvidenceAvailable.Should().BeFalse();
        result.Coverage.Browser.Should().Be(PerformanceCoverageState.Complete);
        result.Coverage.Api.Should().Be(PerformanceCoverageState.NotAvailable);
        result.Coverage.Overall.Should().Be(PerformanceAssessmentState.Partial);
        result.Limitations.Should().Contain(l => l.StartsWith("Network/API correlation unavailable"));
        result.Pages.Select(p => p.ObservationType).Should().BeEquivalentTo([PerformancePhase.InitialLoad, PerformancePhase.SpaNavigation]);
        result.Pages.Should().OnlyContain(p => p.Metric("rest-calls")!.Status == PerformanceMetricStatus.NotMeasured);
        result.Thresholds.Should().Contain(t => t.Name == "API response" && t.Source == PerformanceThresholdSource.Default);
        await companion.DisposeAsync();
    }

    [Fact]
    public async Task EvidenceSource_NothingRecorded_NotAssessed()
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus { State = BrowserCompanionState.NotPaired, ProfileId = "dev" });
        var companion = new BrowserCompanionRuntime(api.Object);
        var source = new PerformanceQualityEvidenceSource(companion, null, new EndpointDiscoveryService(), new Mock<IJSRuntime>().Object);
        var result = await source.CollectAsync(Context());
        result.Assessed.Should().BeFalse();
        result.Assessment.Should().Be(PerformanceAssessmentState.NotAssessed);
        result.CompanionMessage.Should().Be(PerformanceQualityEvidenceSource.NoEvidenceMessage);
        await companion.DisposeAsync();
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [Fact]
    public void Export_IncludesPhaseMetricsThresholdSourcesFindingsApiBlazorAndMissingEvidence_NoSecrets()
    {
        var page = new PageAnalysis { PageOrigin = Origin, PagePath = "/children", BrowserEvidence = Evidence("/children", T0, lcp: 4500), LastObservedAt = T0 };
        page.Endpoints.Add(Endpoint("/children", "/graphql", 7, T0, ms: 1200, gql: "GetChildren") with { CacheDirectives = "no-store" });
        var evaluated = PerformanceQualityRules.Evaluate(page, new FrontendPerformanceThresholds { Mode = FrontendThresholdMode.Custom, ApiResponseWarningMs = 800 }, new());
        var proxyOnly = PerformanceQualityRules.Evaluate(new PageAnalysis { PageOrigin = Origin, PagePath = "/api-only", Endpoints = [Endpoint("/api-only", "/api/x", 2, T0)], LastObservedAt = T0 }, new(), new());
        var result = new PerformanceQualityReviewResult { Pages = [evaluated, proxyOnly], Coverage = PerformanceCoverage.Merge([evaluated.Coverage, proxyOnly.Coverage]), CompanionState = BrowserCompanionState.Connected, EvaluatedAt = DateTimeOffset.UtcNow, Thresholds = PerformanceQualityRules.ResolveThresholds(new FrontendPerformanceThresholds { Mode = FrontendThresholdMode.Custom, ApiResponseWarningMs = 800 }, new()).All.ToList(), Limitations = ["limitation text"] };
        var report = new FrontendQualityReviewReport { TargetUrl = Origin + "/", GeneratedAt = DateTime.UtcNow, PerformanceQualityReport = result };

        var html = new ReportExportService().ExportFrontendQualityReview(report, "BirkNext");

        html.Should().Contain("BirkNext Performance Quality").And.Contain("Initial load").And.Contain("Largest Contentful Paint").And.Contain("Poor")
            .And.Contain("Target Environment").And.Contain("Default").And.Contain("GetChildren").And.Contain("Slow GraphQL operation").And.Contain("Blazor WASM:")
            .And.Contain("Missing evidence:").And.Contain("Browser performance: not assessed").And.Contain("limitation text").And.Contain("Thresholds");
        html.Should().NotContainAny("Bearer", "Authorization", "Cookie", "access_token", "?token=");
    }

    // ── UI components ─────────────────────────────────────────────────────────

    [Fact]
    public void PageView_ShowsStatusesThresholdSourcesAndNotMeasuredInp()
    {
        var page = new PageAnalysis { PageOrigin = Origin, PagePath = "/children", DisplayName = "Children Search", BrowserEvidence = Evidence("/children", T0), LastObservedAt = T0 };
        page.Endpoints.Add(Endpoint("/children", "/graphql", 7, T0, ms: 1200, gql: "GetChildren"));
        var snapshot = PerformanceQualityRules.Evaluate(page, new(), new());

        var cut = Render<PerformanceQualityPageView>(p => p.Add(x => x.Snapshot, snapshot));

        cut.Find("[data-testid=pq-page-title]").TextContent.Should().Be("Children Search");
        cut.Find("[data-testid=pq-phase]").TextContent.Should().Be("Initial load");
        cut.Find("[data-testid=pq-coverage]").TextContent.Should().Be("Complete assessment");
        cut.Find("[data-testid=pq-value-lcp]").TextContent.Should().Be("2.9 s");
        cut.Find("[data-testid=pq-status-lcp]").TextContent.Should().Be("Needs improvement");
        cut.Find("[data-testid=pq-threshold-lcp]").TextContent.Should().Contain("Default");
        cut.Find("[data-testid=pq-value-inp]").TextContent.Should().Be("Not measured");
        cut.Find("[data-testid=pq-status-inp]").TextContent.Should().Be("Not measured");
        cut.Find("[data-testid=pq-value-stabilization]").TextContent.Should().Be("1.5 s");
        cut.FindAll("[data-testid=pq-finding]").Should().Contain(r => r.GetAttribute("data-rule-id") == "api-duplicate-graphql");
        cut.Find("[data-testid=pq-operation-count]").TextContent.Should().Be("7");
        cut.FindAll("[data-testid=pq-timeline-row]").Should().NotBeEmpty();
        cut.Markup.Should().NotContainAny("Bearer", "Authorization");
    }

    [Fact]
    public void ReportSection_NotAssessed_SaysSo_And_OverviewSortsWorstFirst()
    {
        var none = new PerformanceQualityReviewResult { CompanionMessage = PerformanceQualityEvidenceSource.NoEvidenceMessage, Coverage = new PerformanceCoverage { Reasons = ["No performance evidence collected for any page."] } };
        var empty = Render<PerformanceQualityReportSection>(p => p.Add(x => x.Result, none));
        empty.Find("[data-testid=pq-not-assessed]").TextContent.Should().Contain("Not assessed");
        empty.Find("[data-testid=pq-assessment]").TextContent.Should().Be("Not assessed");
        empty.FindAll("[data-testid=pq-overview]").Should().BeEmpty();

        var good = PerformanceQualityRules.Evaluate(new PageAnalysis { PageOrigin = Origin, PagePath = "/good", BrowserEvidence = Evidence("/good", T0, lcp: 1200), LastObservedAt = T0 }, new(), new());
        var bad = PerformanceQualityRules.Evaluate(new PageAnalysis { PageOrigin = Origin, PagePath = "/bad", BrowserEvidence = Evidence("/bad", T0, lcp: 5000), LastObservedAt = T0 }, new(), new());
        var result = new PerformanceQualityReviewResult { Pages = [good, bad], Coverage = PerformanceCoverage.Merge([good.Coverage, bad.Coverage]), CompanionState = BrowserCompanionState.Connected };
        var cut = Render<PerformanceQualityReportSection>(p => p.Add(x => x.Result, result));
        cut.Find("[data-testid=pq-assessment]").TextContent.Should().Be("Partial assessment");
        cut.Find("[data-testid=pq-api-evidence]").TextContent.Should().Be("Not available");
        var rows = cut.FindAll("[data-testid=pq-overview-row]");
        rows.Should().HaveCount(2);
        rows[0].GetAttribute("data-page-id").Should().EndWith("/bad");
        cut.Find("[data-testid=pq-page-title]").TextContent.Should().Be("/bad", "the worst page is selected first");
        rows[1].Click();
        cut.Find("[data-testid=pq-page-title]").TextContent.Should().Be("/good");
    }

    [Fact]
    public void EndpointDiscoveryTab_KeepsNetworkTrafficWithoutBrowserAssessment()
    {
        Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", TargetUrl = Origin + "/" };
        var status = new LocalHttpsProxyStatus { SessionId = "s", State = LocalHttpsProxyState.Ready, ObservedNetworkEndpoints = [Endpoint("/barn/1", "/api/children", 6, T0, ms: 1300)] };
        var cut = Render<EndpointDiscoveryTab>(p => p.Add(x => x.Profile, profile).Add(x => x.ProxyStatus, status));
        cut.Find("[data-testid=discovery-nav-pages]").Click();

        cut.FindAll("[data-testid=discovery-page-performance]").Should().BeEmpty();
        cut.FindAll("[data-testid=pq-coverage]").Should().BeEmpty();
        cut.Find("[data-testid=discovery-page-table]").TextContent.Should().Contain("/api/children");
    }
}
