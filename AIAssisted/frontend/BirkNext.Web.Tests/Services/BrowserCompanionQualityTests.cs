using System.Text.Json;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.JSInterop;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// BirkNext Browser Quality: Browser Companion evidence converges with proxy evidence on one PageAnalysis per page identity, respects
/// the per-page refresh generation, stays isolated per Target Environment, feeds deterministic rules, and drives the FQR engine with an
/// accurate "not connected" blocker instead of a timeout. Nothing credential-shaped survives persistence.
/// </summary>
public sealed class BrowserCompanionQualityTests : BunitContext
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero); // firmly in the past relative to any refresh boundary

    private static BrowserPageEvidence Evidence(string path, DateTimeOffset visit, int seq = 1, Action<Dictionary<string, object>>? _ = null) => new()
    {
        ProfileId = "dev", PageOrigin = Origin, PagePath = path, VisitStartedAt = visit, CapturedAt = visit.AddSeconds(seq), SnapshotSequence = seq,
        DocumentTitle = $"Page {path}", BrowserName = "Microsoft Edge",
        Dom = new BrowserDomSummary { NodeCount = 400, MaxDepth = 12, InteractiveCount = 20, HeadingCounts = new() { ["h1"] = 1 } },
        Accessibility = new BrowserAccessibilitySummary { RulesEvaluated = 14, Findings = [] },
        Performance = new BrowserPerformanceSummary { LcpMs = 1200, Cls = 0.02, ResourceCount = 12, TransferredBytes = 900_000 },
        Runtime = new BrowserRuntimeSummary(), Blazor = new BrowserBlazorSummary { Detected = true, BootManifestObserved = true, FrameworkResourceCount = 40, FrameworkBytes = 3_000_000, WasmBytes = 2_000_000 },
    };

    private static ObservedNetworkEndpoint Endpoint(string pagePath, string apiPath, int count, DateTimeOffset at, GraphQlOperationType op = GraphQlOperationType.None, string? opName = null) => new()
    {
        Category = op == GraphQlOperationType.None ? ObservedTrafficCategory.Rest : ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "api-dev.bufetat.no", Port = 443,
        Path = apiPath, Method = op == GraphQlOperationType.None ? "GET" : "POST", AuthObserved = true, LastStatus = 200, Count = count, FirstObservedAt = at, LastObservedAt = at,
        PageOrigin = Origin, PagePath = pagePath, OperationType = op, OperationName = opName, Confidence = ObservedEndpointConfidence.Verified,
    };

    // ── Page correlation & SPA navigation ──────────────────────────────────────

    [Fact]
    public void ProxyAndCompanion_SamePagePath_OneUnifiedPageAnalysis()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.Merge(snapshot, [Endpoint("/children/search", "/api/children", 2, T0)], T0);
        EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, [Evidence("/children/search", T0.AddSeconds(1))], T0.AddSeconds(5));

        snapshot.Pages.Should().ContainSingle();
        var page = snapshot.Pages.Single();
        page.Identity.Should().Be("https://m2lbdev.bufetat.no/children/search");
        page.Endpoints.Should().ContainSingle();
        page.BrowserEvidence.Should().NotBeNull();
        page.DisplayName.Should().Be("Page /children/search", "the document title names the page when nothing else does");
    }

    [Fact]
    public void SpaNavigation_ThreeRoutes_ThreeIsolatedPageAnalyses()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, [Evidence("/dashboard", T0), Evidence("/children", T0.AddMinutes(1)), Evidence("/placements", T0.AddMinutes(2))], T0.AddMinutes(3));

        snapshot.Pages.Select(p => p.PagePath).Should().BeEquivalentTo(["/dashboard", "/children", "/placements"]);
        snapshot.Pages.Should().OnlyContain(p => p.BrowserEvidence != null && p.BrowserEvidence.PagePath == p.PagePath);
    }

    [Fact]
    public void LaterSnapshotOfSameVisitReplaces_OlderVisitNeverOverwrites()
    {
        var snapshot = new EndpointDiscoverySnapshot();
        EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, [Evidence("/a", T0, 1)], T0);
        EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, [Evidence("/a", T0, 3)], T0);
        snapshot.Pages.Single().BrowserEvidence!.SnapshotSequence.Should().Be(3);

        var changed = EndpointDiscoveryMerge.MergeBrowserEvidence(snapshot, [Evidence("/a", T0.AddMinutes(-5), 9)], T0);
        changed.Should().BeFalse();
        snapshot.Pages.Single().BrowserEvidence!.VisitStartedAt.Should().Be(T0);
    }

    // ── Refresh generation ─────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshPage_ExcludesOldProxyAndBrowserEvidence_RepopulatesFromFreshVisitOnly_OtherPagesUntouched()
    {
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<string?>("birkNextStorage.getDiscovery", It.IsAny<object[]>())).ReturnsAsync((string?)null);
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("birkNextStorage.setDiscovery", It.IsAny<object[]>())).ReturnsAsync(Mock.Of<Microsoft.JSInterop.Infrastructure.IJSVoidResult>());
        var service = new EndpointDiscoveryService();
        var pages = new[] { "/dashboard", "/children", "/placements", "/users", "/reports" };
        await service.MergeObservedAsync(js.Object, "dev", pages.Select(p => Endpoint(p, "/api" + p, 1, T0)).ToList());
        await service.MergeBrowserEvidenceAsync(js.Object, "dev", pages.Select(p => Evidence(p, T0)).ToList());
        var before = service.GetSnapshot("dev").Pages.Where(p => p.PagePath != "/children").Select(p => (p.PagePath, p.Endpoints.Count, p.BrowserEvidence!.CapturedAt, p.AnalysisGeneration)).ToList();

        await service.RefreshPageAsync(js.Object, "dev", $"{Origin}/children");
        var refreshed = service.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/children");
        refreshed.AnalysisGeneration.Should().Be(2);
        refreshed.Endpoints.Should().BeEmpty();
        refreshed.BrowserEvidence.Should().BeNull();
        refreshed.IsWaitingForFreshTraffic.Should().BeTrue();

        // Cumulative runtime state (proxy registry + companion registry) re-delivers the OLD observations: they must not repopulate the page.
        await service.MergeObservedAsync(js.Object, "dev", [Endpoint("/children", "/api/children", 1, T0)]);
        await service.MergeBrowserEvidenceAsync(js.Object, "dev", [Evidence("/children", T0, 5)]);
        refreshed = service.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/children");
        refreshed.Endpoints.Should().BeEmpty("proxy observations from before the refresh boundary are excluded");
        refreshed.BrowserEvidence.Should().BeNull("a visit that started before the refresh never repopulates the page");

        // A fresh visit (started after the boundary) repopulates the same page entry.
        var fresh = refreshed.RefreshedAtUtc!.Value.AddSeconds(10);
        await service.MergeBrowserEvidenceAsync(js.Object, "dev", [Evidence("/children", fresh)]);
        await service.MergeObservedAsync(js.Object, "dev", [Endpoint("/children", "/api/children", 1, fresh)]);
        refreshed = service.GetSnapshot("dev").Pages.Single(p => p.PagePath == "/children");
        refreshed.BrowserEvidence!.VisitStartedAt.Should().Be(fresh);
        refreshed.Endpoints.Should().ContainSingle();
        refreshed.AnalysisGeneration.Should().Be(2);

        var after = service.GetSnapshot("dev").Pages.Where(p => p.PagePath != "/children").Select(p => (p.PagePath, p.Endpoints.Count, p.BrowserEvidence!.CapturedAt, p.AnalysisGeneration)).ToList();
        after.Should().BeEquivalentTo(before, "the other four pages are unchanged");
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rules_AccessibilityPerformanceRuntimeBlazor_FromSyntheticEvidence()
    {
        var evidence = Evidence("/children/search", T0) with
        {
            Accessibility = new BrowserAccessibilitySummary { RulesEvaluated = 14, Findings = [new BrowserAccessibilityRuleResult { RuleId = "a11y-button-name", Severity = "High", Wcag = "4.1.2", Title = "Button without accessible name", Guidance = "Name it.", Count = 3, Selectors = ["button.icon"] }] },
            Performance = new BrowserPerformanceSummary { LcpMs = 4500, Cls = 0.3, LongTaskCount = 4, LongTaskTotalMs = 620, LongestTaskMs = 300, ResourceCount = 45, TransferredBytes = 9_000_000, FailedResourceCount = 1, FailedResources = [new BrowserResourceEntry { Url = "https://m2lbdev.bufetat.no/missing.js", Status = 404 }] },
            Runtime = new BrowserRuntimeSummary { ErrorCount = 2, RejectionCount = 1, Errors = [new BrowserRuntimeError { Kind = "error", Message = "TypeError: x is undefined", Count = 2, FirstAt = T0, LastAt = T0 }, new BrowserRuntimeError { Kind = "unhandledrejection", Message = "fetch failed", Count = 1, FirstAt = T0, LastAt = T0 }] },
            Blazor = new BrowserBlazorSummary { Detected = true, BootManifestObserved = true, BootManifestFailed = true, FrameworkFailures = [new BrowserResourceEntry { Url = "https://m2lbdev.bufetat.no/_framework/blazor.boot.json", Status = 500 }], WasmBytes = 4_000_000, FrameworkBytes = 6_000_000, ErrorUiVisible = true },
        };
        var page = new PageAnalysis { PageOrigin = Origin, PagePath = "/children/search", BrowserEvidence = evidence };

        var findings = BrowserQualityRules.Evaluate(page, new FrontendPerformanceThresholds(), new CoreWebVitalsThresholds());

        var ids = findings.Select(f => f.RuleId).ToList();
        ids.Should().Contain(["a11y-button-name", "perf-lcp-poor", "perf-cls-poor", "perf-long-tasks", "perf-resource-count", "perf-transfer-size", "net-failed-resources", "runtime-errors", "runtime-unhandled-rejections", "blazor-boot-manifest-failed", "blazor-error-ui", "blazor-wasm-size", "blazor-framework-size"]);
        findings.Single(f => f.RuleId == "a11y-button-name").Should().Match<BrowserQualityFinding>(f => f.Severity == FrontendQualitySeverity.High && f.Category == BrowserQualityCategory.Accessibility && f.Wcag == "4.1.2" && f.Evidence.Contains("Selector: button.icon"));
        findings.Single(f => f.RuleId == "perf-lcp-poor").Evidence.Should().Contain("LCP: 4500 ms").And.Contain("Poor threshold: 4000 ms");
        findings.Single(f => f.RuleId == "blazor-boot-manifest-failed").Severity.Should().Be(FrontendQualitySeverity.Critical);
        findings.Should().OnlyContain(f => f.Page == "https://m2lbdev.bufetat.no/children/search" && f.Source == BrowserQualityRules.CompanionSource);
        findings.Should().NotContain(f => f.RuleId == "net-repeated-api-calls", "no proxy evidence for this page");
    }

    [Fact]
    public void Rules_GoodPage_NoFindings_AndThresholdsComeFromEnvironment()
    {
        var page = new PageAnalysis { PageOrigin = Origin, PagePath = "/dashboard", BrowserEvidence = Evidence("/dashboard", T0) };
        BrowserQualityRules.Evaluate(page, new FrontendPerformanceThresholds(), new CoreWebVitalsThresholds()).Should().BeEmpty();

        var strict = new CoreWebVitalsThresholds { LcpGoodMs = 1000, LcpPoorMs = 1100 };
        BrowserQualityRules.Evaluate(page, new FrontendPerformanceThresholds(), strict).Should().ContainSingle(f => f.RuleId == "perf-lcp-poor");
    }

    [Fact]
    public void Rules_RepeatedApiCalls_CorrelatedWithProxyEvidence_WordedAsCorrelation()
    {
        var page = new PageAnalysis
        {
            PageOrigin = Origin, PagePath = "/children/search", BrowserEvidence = Evidence("/children/search", T0) with { Performance = new BrowserPerformanceSummary { LcpMs = 2700 } },
            Endpoints = [Endpoint("/children/search", "/api/children", 8, T0), Endpoint("/children/search", "/api/roles", 5, T0, GraphQlOperationType.Query, "GetRoles"), Endpoint("/children/search", "/api/config", 1, T0)],
        };

        var finding = BrowserQualityRules.Evaluate(page, new(), new()).Should().ContainSingle(f => f.RuleId == "net-repeated-api-calls").Subject;

        finding.Source.Should().Be(BrowserQualityRules.CorrelatedSource);
        finding.Explanation.Should().Contain("correlated with").And.Contain("LCP for this page was 2700 ms").And.NotContain("caused by");
        finding.Evidence.Should().Contain("8× GET api-dev.bufetat.no/api/children · last status 200");
        finding.Evidence.Should().Contain(e => e.Contains("5× POST api-dev.bufetat.no/api/roles (Query GetRoles)"));
        finding.Evidence.Should().NotContain(e => e.Contains("/api/config"), "below the repetition threshold");
        System.Text.Json.JsonSerializer.Serialize(finding).Should().NotContainAny("Bearer", "Authorization", "Cookie");
    }

    // ── FQR engine ────────────────────────────────────────────────────────────

    private static FrontendAnalysisContext Context(bool browserQuality = true)
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = Origin + "/", Performance = new() };
        profile.Features.EnableBrowserRuntimeEngine = false; profile.Features.EnableAccessibilityEngine = false; profile.Features.EnableLighthouseEngine = false; profile.Features.EnablePassiveSecurityEngine = false;
        profile.Features.EnableBrowserQualityEngine = browserQuality;
        return new FrontendAnalysisContext
        {
            ActiveProfile = profile, TargetUrl = Origin + "/", FeatureToggles = profile.Features, EngineRequirements = profile.EngineRequirements, ReviewEngineSelection = profile.ReviewEngineSelection,
            AllowedBackendDomains = [], AllowedRestHosts = [], AllowedGraphQlEndpoints = [], AllowedCdnHosts = [], SecuritySettings = new(),
        };
    }

    [Fact]
    public async Task Engine_CompanionNotConnected_BlockedWithAccurateReason_NoTimeout()
    {
        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(browserQuality: new OrchestrationTestHelpers.DisconnectedBrowserQualitySource());
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await orchestrator.RunAsync(Origin + "/", Context());

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        var outcome = result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.BrowserQuality);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Unavailable);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.BrowserCompanionNotConnected);
        outcome.SanitizedFailureReason.Should().Contain("Browser Companion not connected");
        outcome.AccessKind.Should().Be(FrontendQualityEngineAccessKind.BrowserCompanion);
        FrontendQualityEngineOutcomePresentation.StateLabel(outcome).Should().Be("Blocked");
        FrontendQualityEngineOutcomePresentation.GetLabel(outcome.OutcomeReason).Should().Be("Browser Companion not connected");
        result.SkippedEngines.Should().Contain("Browser Quality");
        result.QualityReport.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed, "the optional engine never blocks the required HTTP engines");
    }

    [Fact]
    public async Task Engine_CompanionEvidenceAvailable_Assessed_FindingsInReport_ProxyOptional()
    {
        var source = new Mock<IBrowserQualityEvidenceSource>();
        source.Setup(s => s.CollectAsync(It.IsAny<FrontendAnalysisContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserQualityReviewResult
        {
            CompanionState = BrowserCompanionState.Connected, CompanionMessage = "connected", ProxyEvidenceAvailable = false, PagesWithEvidence = 2,
            PageIdentities = [$"{Origin}/dashboard", $"{Origin}/children"], EvaluatedAt = DateTimeOffset.UtcNow, BrowserName = "Microsoft Edge",
            Findings = [new BrowserQualityFinding { RuleId = "a11y-button-name", Category = BrowserQualityCategory.Accessibility, Severity = FrontendQualitySeverity.High, Page = $"{Origin}/children", Title = "Button without accessible name", Explanation = "3 elements", Evidence = ["Occurrences: 3"], Recommendation = "Name it.", ObservedAt = DateTimeOffset.UtcNow }],
            Limitations = ["Network correlation unavailable: no Local HTTPS proxy traffic was recorded for these pages (proxy not running or not configured in the browser)."],
        });
        var orchestrator = OrchestrationTestHelpers.CreateOrchestrator(browserQuality: source.Object);

        var result = await orchestrator.RunAsync(Origin + "/", Context());

        var outcome = result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.BrowserQuality);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        outcome.EvidenceCount.Should().Be(2);
        outcome.FindingCount.Should().Be(1);
        outcome.BrowserName.Should().Be("Microsoft Edge");
        outcome.ToolName.Should().Be("BirkNext Browser Companion");
        result.QualityReport.Findings.Should().ContainSingle(f => f.EngineId == FrontendQualityEngineId.BrowserQuality && f.Category == FrontendQualityCategory.Accessibility && f.SourceRuleId == "a11y-button-name");
        result.QualityReport.Limitations.Should().Contain(l => l.Contains("Network correlation unavailable"));
        result.QualityReport.AssessedEngines.Should().Contain("Browser Quality");
        source.Verify(s => s.CollectAsync(It.IsAny<FrontendAnalysisContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Engine_Disabled_NotCollected()
    {
        var source = new Mock<IBrowserQualityEvidenceSource>(MockBehavior.Strict);
        var result = await OrchestrationTestHelpers.CreateOrchestrator(browserQuality: source.Object).RunAsync(Origin + "/", Context(browserQuality: false));
        result.QualityReport!.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.BrowserQuality).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Disabled);
        source.VerifyNoOtherCalls();
    }

    // ── Active target isolation & runtime scoping ─────────────────────────────

    [Fact]
    public async Task EvidenceSource_UsesOnlyPagesOfTheEnvironmentUnderReview()
    {
        var api = new Mock<IBrowserCompanionApiService>();
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus { State = BrowserCompanionState.Connected, ProfileId = "dev", Pages = [Evidence("/dashboard", T0)] });
        api.Setup(a => a.StatusAsync("qa", It.IsAny<CancellationToken>())).ReturnsAsync(new BrowserCompanionStatus { State = BrowserCompanionState.NotPaired, ProfileId = "qa" });
        var runtime = new BrowserCompanionRuntime(api.Object);
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("birkNextStorage.setDiscovery", It.IsAny<object[]>())).ReturnsAsync(Mock.Of<Microsoft.JSInterop.Infrastructure.IJSVoidResult>());
        var discovery = new EndpointDiscoveryService();
        var source = new BrowserQualityEvidenceSource(runtime, discovery, js.Object);

        var dev = await source.CollectAsync(Context());
        dev.CompanionState.Should().Be(BrowserCompanionState.Connected);
        dev.PagesWithEvidence.Should().Be(1);
        discovery.GetSnapshot("dev").Pages.Should().ContainSingle();

        var qaContext = Context();
        qaContext.ActiveProfile.Id = "qa"; qaContext.ActiveProfile.Name = "M2LB QA";
        var qa = await source.CollectAsync(qaContext);
        qa.CompanionState.Should().Be(BrowserCompanionState.NotPaired, "DEV pairing is not accepted into the QA context");
        qa.PagesWithEvidence.Should().Be(0);
        discovery.GetSnapshot("qa").Pages.Should().BeEmpty("DEV evidence never leaks into QA");
        runtime.For("dev").Connected.Should().BeFalse("the runtime follows the environment being viewed");
    }

    // ── Persistence / restart ─────────────────────────────────────────────────

    [Fact]
    public async Task Restart_PersistedBrowserEvidenceReloads_LiveCompanionStateDoesNot()
    {
        string? persisted = null;
        var js = new Mock<IJSRuntime>();
        js.Setup(j => j.InvokeAsync<Microsoft.JSInterop.Infrastructure.IJSVoidResult>("birkNextStorage.setDiscovery", It.IsAny<object[]>()))
            .Callback<string, object[]>((_, args) => persisted = (string)args[0]).ReturnsAsync(Mock.Of<Microsoft.JSInterop.Infrastructure.IJSVoidResult>());
        var service = new EndpointDiscoveryService();
        var evidence = Evidence("/children/search", T0) with { DocumentTitle = "Search ola.nordmann@bufetat.no", Runtime = new BrowserRuntimeSummary { ErrorCount = 1, Errors = [new BrowserRuntimeError { Kind = "error", Message = "sanitized upstream", Source = "https://m2lbdev.bufetat.no/app.js", FirstAt = T0, LastAt = T0 }] } };
        await service.MergeBrowserEvidenceAsync(js.Object, "dev", [evidence]);
        persisted.Should().NotBeNull();
        persisted.Should().NotContainAny("Bearer", "Authorization", "Set-Cookie", "<html", "<div");

        var fresh = new EndpointDiscoveryService();
        var js2 = new Mock<IJSRuntime>();
        js2.Setup(j => j.InvokeAsync<string?>("birkNextStorage.getDiscovery", It.IsAny<object[]>())).ReturnsAsync(persisted);
        await fresh.LoadAsync(js2.Object);
        var page = fresh.GetSnapshot("dev").Pages.Single();
        page.BrowserEvidence!.Performance!.LcpMs.Should().Be(1200);
        page.BrowserEvidence.Runtime!.Errors.Should().ContainSingle();

        var runtime = new BrowserCompanionRuntime(Mock.Of<IBrowserCompanionApiService>());
        runtime.For("dev").State.Should().Be(BrowserCompanionState.NotPaired, "live pairing is never restored from persistence");
    }

    // ── Panel ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Panel_ShowsNotConnected_PairingCode_AndConnectedStates()
    {
        var api = new Mock<IBrowserCompanionApiService>();
        var status = new BrowserCompanionStatus { State = BrowserCompanionState.NotPaired, ProfileId = "dev", Message = "Browser Companion not paired." };
        api.Setup(a => a.StatusAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        api.Setup(a => a.StartPairingAsync(It.IsAny<BrowserCompanionPairingStartRequest>(), It.IsAny<CancellationToken>()))
            .Callback<BrowserCompanionPairingStartRequest, CancellationToken>((r, _) => { r.ApprovedOrigins.Should().BeEquivalentTo([Origin]); status = new BrowserCompanionStatus { State = BrowserCompanionState.PairingPending, ProfileId = "dev", PairingCode = "ABCD2345", PairingExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3), ApprovedOrigins = [Origin] }; })
            .ReturnsAsync(new BrowserCompanionPairingChallenge { PairingCode = "ABCD2345", ProfileId = "dev" });
        var runtime = new BrowserCompanionRuntime(api.Object);
        var profile = Context().ActiveProfile;

        var cut = Render<BrowserCompanionPanel>(p => p.Add(x => x.Profile, profile).Add(x => x.Runtime, runtime).Add(x => x.ProxyRunning, false));
        cut.WaitForAssertion(() => cut.Find("[data-testid=browser-companion-state]").TextContent.Should().Be("Not connected"));
        cut.FindAll("[data-testid=browser-companion-network]").Should().BeEmpty();
        cut.Find("[data-testid=browser-companion-dom]").TextContent.Should().Be("Unavailable");

        await cut.InvokeAsync(() => cut.Find("[data-testid=browser-companion-pair]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=browser-companion-code]").TextContent.Should().Be("ABCD2345"));
        cut.Find("[data-testid=browser-companion-state]").TextContent.Should().Be("Pairing…");

        status = new BrowserCompanionStatus { State = BrowserCompanionState.Connected, ProfileId = "dev", EnvironmentName = "M2LB DEV", PagesWithEvidence = 3, Pages = [Evidence("/a", T0), Evidence("/b", T0), Evidence("/c", T0)], CurrentPageOrigin = Origin, CurrentPagePath = "/children/search", ApprovedOrigins = [Origin] };
        await runtime.RefreshAsync();
        cut.WaitForAssertion(() => cut.Find("[data-testid=browser-companion-state]").TextContent.Should().Be("Connected"));
        cut.Find("[data-testid=browser-companion-current-page]").TextContent.Should().Be($"{Origin}/children/search");
        cut.Find("[data-testid=browser-companion-pages]").TextContent.Should().Be("3");
        cut.Find("[data-testid=browser-companion-accessibility]").TextContent.Should().Be("Evidence available");
        cut.FindAll("[data-testid=browser-companion-unpair]").Should().ContainSingle();
    }
}
