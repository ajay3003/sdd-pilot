using System.Text.Json;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// BirkNext Performance Quality rules: deterministic fixtures for Web Vitals, INP honesty, initial-load vs SPA phases, long tasks,
/// resources (classification, totals, largest/slowest, duplicates vs cache, failures, cache quality), API metrics (latency, counts,
/// duplicates, statuses, minimum-sample percentiles, polling heuristic, bursts, sequential patterns), Blazor WASM startup, page
/// stabilization bounds, threshold provenance, coverage (partial / not assessed), comparison, timeline and secret hygiene.
/// </summary>
public sealed class PerformanceQualityRulesTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly FrontendPerformanceThresholds Defaults = new();
    private static readonly CoreWebVitalsThresholds Vitals = new();

    private static BrowserPerformanceSummary Perf() => new()
    {
        ObservationType = "initial-load", TtfbMs = 300, FirstContentfulPaintMs = 1100, LcpMs = 2900, Cls = 0.04, DomContentLoadedMs = 1500, LoadEventMs = 2200,
        StabilizationMs = 1800, StabilizedBy = "quiet",
        Interaction = new BrowserInteractionSummary { Status = "insufficient-samples", InteractionCount = 2, MinimumInteractions = 3, LongestInteractionMs = 250 },
        LongTaskCount = 4, LongTaskTotalMs = 620, LongestTaskMs = 310, MainThreadBlockingMs = 420, LongTasksAfterStabilization = 1,
        ResourceCount = 48, TransferredBytes = 8_700_000, JsBytes = 1_200_000, CssBytes = 90_000, WasmBytes = 6_300_000, ImageBytes = 400_000, FontBytes = 60_000, CachedResourceCount = 5,
        LargestResource = new BrowserResourceEntry { Url = Origin + "/_framework/dotnet.native.wasm", Kind = "wasm", TransferBytes = 2_600_000, DurationMs = 900 },
        SlowestResource = new BrowserResourceEntry { Url = Origin + "/_framework/dotnet.native.wasm", Kind = "wasm", TransferBytes = 2_600_000, DurationMs = 900 },
        Categories = [new BrowserResourceCategorySummary { Kind = "js", Count = 6, TransferBytes = 1_200_000, Largest = new BrowserResourceEntry { Url = Origin + "/app.js", Kind = "js", TransferBytes = 800_000 } }],
        Timeline = [new BrowserResourceEntry { Url = Origin + "/app.js", Kind = "js", StartMs = 10, DurationMs = 120, TransferBytes = 800_000, Delivery = "network" }],
        UnsupportedMetrics = [],
    };

    private static BrowserPageEvidence Evidence(BrowserPerformanceSummary perf, string path = "/children/search", BrowserBlazorSummary? blazor = null, BrowserRuntimeSummary? runtime = null) => new()
    {
        ProfileId = "dev", PageOrigin = Origin, PagePath = path, VisitStartedAt = T0, CapturedAt = T0.AddSeconds(5), SnapshotSequence = 1, DocumentTitle = "Children Search", BrowserName = "Microsoft Edge",
        Performance = perf, Runtime = runtime ?? new BrowserRuntimeSummary(),
        Blazor = blazor ?? new BrowserBlazorSummary { Detected = true, BootManifestObserved = true, FrameworkResourceCount = 70, FrameworkBytes = 6_600_000, WasmBytes = 6_300_000, AssemblyCount = 60, AssemblyBytes = 3_000_000, RuntimeResourceCount = 3, LoadKind = "cold", FrameworkLoadStartMs = 100, FrameworkLoadEndMs = 2400 },
    };

    private static PageAnalysis Page(BrowserPageEvidence? evidence = null, params ObservedNetworkEndpoint[] endpoints) => new()
    {
        PageOrigin = Origin, PagePath = evidence?.PagePath ?? "/children/search", DisplayName = evidence is null || evidence.PagePath == "/children/search" ? "Children Search" : null, FirstObservedAt = T0, LastObservedAt = T0.AddSeconds(5), BrowserEvidence = evidence, Endpoints = endpoints.ToList(),
    };

    private static ObservedNetworkEndpoint Rest(string path, params (double Ms, int Status, int Sec)[] samples) => new()
    {
        Category = ObservedTrafficCategory.Rest, Scheme = "https", Host = "api-dev.bufetat.no", Port = 443, Path = path, Method = "GET", AuthObserved = true,
        LastStatus = samples.Length > 0 ? samples[^1].Status : 200, Count = Math.Max(1, samples.Length), FirstObservedAt = T0, LastObservedAt = T0.AddSeconds(samples.Length > 0 ? samples.Max(s => s.Sec) : 0),
        PageOrigin = Origin, PagePath = "/children/search", Confidence = ObservedEndpointConfidence.Verified,
        Samples = samples.OrderByDescending(s => s.Sec).Select(s => new ObservedRequestSample(T0.AddSeconds(s.Sec), s.Ms, s.Status, 2048)).ToList(),
        TotalDurationMs = samples.Sum(s => s.Ms), MinDurationMs = samples.Length > 0 ? samples.Min(s => s.Ms) : null, MaxDurationMs = samples.Length > 0 ? samples.Max(s => s.Ms) : null,
        ErrorCount = samples.Count(s => s.Status >= 400), AuthRejectedCount = samples.Count(s => s.Status is 401 or 403),
    };

    private static ObservedNetworkEndpoint Gql(string operation, params (double Ms, int Status, double Sec)[] samples) => new()
    {
        Category = ObservedTrafficCategory.GraphQl, Scheme = "https", Host = "api-dev.bufetat.no", Port = 443, Path = "/graphql", Method = "POST", AuthObserved = true,
        LastStatus = 200, Count = samples.Length, FirstObservedAt = T0.AddSeconds(samples.Min(s => s.Sec)), LastObservedAt = T0.AddSeconds(samples.Max(s => s.Sec)),
        PageOrigin = Origin, PagePath = "/children/search", Confidence = ObservedEndpointConfidence.Verified, OperationType = GraphQlOperationType.Query, OperationName = operation,
        Samples = samples.OrderByDescending(s => s.Sec).Select(s => new ObservedRequestSample(T0.AddSeconds(s.Sec), s.Ms, s.Status, 4096)).ToList(),
        TotalDurationMs = samples.Sum(s => s.Ms), MinDurationMs = samples.Min(s => s.Ms), MaxDurationMs = samples.Max(s => s.Ms), ErrorCount = samples.Count(s => s.Status >= 400),
    };

    private static PagePerformanceSnapshot Eval(PageAnalysis page, FrontendPerformanceThresholds? perf = null, CoreWebVitalsThresholds? vitals = null) =>
        PerformanceQualityRules.Evaluate(page, perf ?? Defaults, vitals ?? Vitals);

    // ── Core metrics & Web Vitals ─────────────────────────────────────────────

    [Fact]
    public void CoreMetrics_InitialLoad_StatusesAndThresholdSourcesFromDefaults()
    {
        var snap = Eval(Page(Evidence(Perf())));

        snap.ObservationType.Should().Be(PerformancePhase.InitialLoad);
        snap.Metric("lcp")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 2900 && m.Status == PerformanceMetricStatus.NeedsImprovement && m.Threshold!.Source == PerformanceThresholdSource.Default);
        snap.Metric("cls")!.Status.Should().Be(PerformanceMetricStatus.Good);
        snap.Metric("stabilization")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 1800 && m.Status == PerformanceMetricStatus.Good && m.Note!.Contains("not LCP"));
        snap.Metric("ttfb")!.Status.Should().Be(PerformanceMetricStatus.Good);
        snap.Metric("fcp")!.Status.Should().Be(PerformanceMetricStatus.Good);
        snap.Metric("dcl")!.Status.Should().Be(PerformanceMetricStatus.Informational);
        snap.Findings.Should().Contain(f => f.RuleId == "perf-lcp" && f.Severity == FrontendQualitySeverity.Medium && f.Phase == PerformancePhase.InitialLoad && f.ThresholdSource == PerformanceThresholdSource.Default && f.ObservedValue == "2.9 s");
        snap.Findings.Should().NotContain(f => f.RuleId == "perf-cls");
        snap.Coverage.Overall.Should().Be(PerformanceAssessmentState.Partial, "no proxy traffic → API not available");
        snap.Coverage.Api.Should().Be(PerformanceCoverageState.NotAvailable);
    }

    [Fact]
    public void Lcp_AbovePoor_IsHighSeverity_And_Cls_Poor_Flagged()
    {
        var perf = Perf(); perf = perf with { LcpMs = 4500, Cls = 0.3 };
        var snap = Eval(Page(Evidence(perf)));
        snap.Metric("lcp")!.Status.Should().Be(PerformanceMetricStatus.Poor);
        snap.Findings.Should().Contain(f => f.RuleId == "perf-lcp" && f.Severity == FrontendQualitySeverity.High);
        snap.Findings.Should().Contain(f => f.RuleId == "perf-cls" && f.Severity == FrontendQualitySeverity.Medium);
    }

    [Fact]
    public void Inp_InsufficientSamples_IsNotMeasured_NeverFabricated()
    {
        var snap = Eval(Page(Evidence(Perf())));
        var inp = snap.Metric("inp")!;
        inp.Status.Should().Be(PerformanceMetricStatus.NotMeasured);
        inp.Value.Should().BeNull();
        inp.Display.Should().Be("Not measured");
        inp.Note.Should().Contain("Insufficient interaction samples (2 of at least 3");
        snap.Findings.Should().NotContain(f => f.RuleId == "perf-inp");
    }

    [Fact]
    public void Inp_Measured_UsesVitalsThresholds()
    {
        var perf = Perf() with { Interaction = new BrowserInteractionSummary { Status = "measured", InteractionCount = 6, MinimumInteractions = 3, InpMs = 350, LongestInteractionMs = 350 } };
        var snap = Eval(Page(Evidence(perf)));
        snap.Metric("inp")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 350 && m.Status == PerformanceMetricStatus.NeedsImprovement);
        snap.Findings.Should().Contain(f => f.RuleId == "perf-inp" && f.Severity == FrontendQualitySeverity.Medium && f.Evidence.Contains("Interactions: 6"));
        var unsupported = Perf() with { Interaction = new BrowserInteractionSummary { Status = "not-supported" } };
        Eval(Page(Evidence(unsupported))).Metric("inp")!.Note.Should().Contain("not supported");
    }

    [Fact]
    public void SpaNavigation_KeepsLcpClsNavigationTimingNotMeasured_StabilizationMeasured_PhaseSeparate()
    {
        var perf = Perf() with { ObservationType = "spa-navigation", LcpMs = null, Cls = null, TtfbMs = null, StabilizationMs = 5600, StabilizedBy = "max-wait", UnsupportedMetrics = ["largest-contentful-paint (SPA route)"] };
        var snap = Eval(Page(Evidence(perf)));

        snap.ObservationType.Should().Be(PerformancePhase.SpaNavigation);
        snap.Metrics.Where(m => m.Layer == PerformanceLayer.Page).Should().OnlyContain(m => m.Phase == PerformancePhase.SpaNavigation);
        snap.Metric("lcp")!.Status.Should().Be(PerformanceMetricStatus.NotMeasured);
        snap.Metric("lcp")!.Note.Should().Contain("initial document load only");
        snap.Metric("ttfb")!.Status.Should().Be(PerformanceMetricStatus.NotMeasured);
        snap.Metric("stabilization")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 5600 && m.Status == PerformanceMetricStatus.Poor && m.Confidence == PerformanceConfidence.Medium);
        snap.Findings.Should().Contain(f => f.RuleId == "perf-stabilization" && f.Severity == FrontendQualitySeverity.High && f.Phase == PerformancePhase.SpaNavigation && f.Confidence == PerformanceConfidence.Medium && f.Evidence.Contains("Ended by: max-wait"));
        snap.Findings.Should().NotContain(f => f.RuleId == "perf-lcp");
        snap.Notes.Should().Contain(n => n.Contains("Reload the target application page"));
        // Startup thresholds are not applied to SPA navigation totals.
        snap.Metric("requests")!.Status.Should().Be(PerformanceMetricStatus.Informational);
        snap.Metric("transfer")!.Status.Should().Be(PerformanceMetricStatus.Informational);
        snap.Findings.Should().NotContain(f => f.RuleId == "res-large-initial-transfer");
    }

    [Fact]
    public void InitialAndSpaEvidence_NeverMixInOneSnapshot()
    {
        var initial = Eval(Page(Evidence(Perf())));
        var spa = Eval(Page(Evidence(Perf() with { ObservationType = "spa-navigation" }, path: "/dashboard")));
        initial.Metrics.Should().OnlyContain(m => m.Phase == PerformancePhase.InitialLoad || m.Phase == PerformancePhase.Runtime);
        spa.Metrics.Should().OnlyContain(m => m.Phase == PerformancePhase.SpaNavigation || m.Phase == PerformancePhase.Runtime);
    }

    // ── Runtime ───────────────────────────────────────────────────────────────

    [Fact]
    public void LongTasks_AboveThreshold_ProduceFindingWithCountDurationLongestAndPhase()
    {
        var snap = Eval(Page(Evidence(Perf())));
        snap.Metric("long-tasks")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 4 && m.Status == PerformanceMetricStatus.NeedsImprovement);
        snap.Metric("main-thread-blocking")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 420 && m.Status == PerformanceMetricStatus.NeedsImprovement && m.Note!.Contains("Not Lighthouse TBT"));
        var finding = snap.Findings.Single(f => f.RuleId == "runtime-long-tasks");
        finding.Explanation.Should().Contain("4 long task(s) over 50 ms").And.Contain("initial load");
        finding.Evidence.Should().Contain("Count: 4").And.Contain("Longest: 310 ms").And.Contain("Phase: Initial load").And.Contain("Threshold: max 3");
        finding.Threshold.Should().Contain("3");
        snap.Findings.Should().Contain(f => f.RuleId == "runtime-main-thread-blocking");
        var fewer = Perf() with { LongTaskCount = 2, LongTaskTotalMs = 140, LongestTaskMs = 80, MainThreadBlockingMs = 40 };
        Eval(Page(Evidence(fewer))).Findings.Should().NotContain(f => f.RuleId == "runtime-long-tasks" || f.RuleId == "runtime-main-thread-blocking");
    }

    [Fact]
    public void LongTasksUnsupported_AreNotMeasured_RuntimeCoveragePartial()
    {
        var perf = Perf() with { UnsupportedMetrics = ["longtask"], LongTaskCount = 0 };
        var snap = Eval(Page(Evidence(perf)));
        snap.Metric("long-tasks")!.Status.Should().Be(PerformanceMetricStatus.NotMeasured);
        snap.Coverage.Runtime.Should().Be(PerformanceCoverageState.Partial);
    }

    [Fact]
    public void RuntimeErrorsBeforeStabilization_AreCorrelated_NotCausal()
    {
        var perf = Perf() with { StabilizationMs = 3200 };
        var runtime = new BrowserRuntimeSummary { ErrorCount = 3, ErrorsBeforeStabilization = 3, Errors = [new BrowserRuntimeError { Kind = "error", Message = "TypeError: x is undefined", Count = 3 }] };
        var finding = Eval(Page(Evidence(perf, runtime: runtime))).Findings.Single(f => f.RuleId == "runtime-errors-during-stabilization");
        finding.Explanation.Should().Contain("3 unhandled runtime exception(s)").And.Contain("not proof of causality");
        finding.Confidence.Should().Be(PerformanceConfidence.Medium);
    }

    [Fact]
    public void MemoryUnavailable_IsNotMeasured_NotRequired()
    {
        var snap = Eval(Page(Evidence(Perf())));
        snap.Metric("js-heap")!.Status.Should().Be(PerformanceMetricStatus.NotMeasured);
        Eval(Page(Evidence(Perf() with { JsHeapUsedBytes = 40_000_000 }))).Metric("js-heap")!.Status.Should().Be(PerformanceMetricStatus.Informational);
    }

    // ── Resources ─────────────────────────────────────────────────────────────

    [Fact]
    public void Resources_Totals_Largest_Slowest_AndLargePayloadRules()
    {
        var perf = Perf() with { JsBytes = 3_000_000, LongestResources = [new BrowserResourceEntry { Url = Origin + "/big.png", Kind = "image", TransferBytes = 2_500_000, DurationMs = 2600 }] };
        var snap = Eval(Page(Evidence(perf)));

        snap.Metric("js-bytes")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 3_000_000 && m.Status == PerformanceMetricStatus.NeedsImprovement);
        snap.Metric("wasm-bytes")!.Status.Should().Be(PerformanceMetricStatus.NeedsImprovement, "6.3 MB > 3 MB default");
        snap.Metric("transfer")!.Status.Should().Be(PerformanceMetricStatus.NeedsImprovement, "8.7 MB > 8 MB initial transfer default");
        snap.Metric("requests")!.Status.Should().Be(PerformanceMetricStatus.NeedsImprovement, "48 > 30");
        snap.Metric("largest-resource")!.Value.Should().Be(2_600_000);
        snap.Metric("slowest-resource")!.Value.Should().Be(900);
        snap.Findings.Should().Contain(f => f.RuleId == "res-large-js" && f.ObservedValue == "2.9 MB");
        snap.Findings.Should().Contain(f => f.RuleId == "res-large-wasm");
        snap.Findings.Should().Contain(f => f.RuleId == "res-large-initial-transfer");
        snap.Findings.Should().Contain(f => f.RuleId == "res-large-image" && f.Evidence.Contains("URL: " + Origin + "/big.png"));
        snap.Findings.Should().Contain(f => f.RuleId == "res-slow-resource" && f.ObservedValue == "2.6 s");
        snap.Findings.Should().Contain(f => f.RuleId == "res-large-resource" && f.Evidence.Contains("Kind: wasm"));
    }

    [Fact]
    public void DuplicateResources_NetworkRepeatsFlagged_CacheHitsNot()
    {
        var perf = Perf() with
        {
            DuplicateFetchCount = 2,
            DuplicateResources =
            [
                new BrowserResourceEntry { Url = Origin + "/api/config", Kind = "api", Count = 3, NetworkCount = 3 },
                new BrowserResourceEntry { Url = Origin + "/logo.svg", Kind = "image", Count = 2, NetworkCount = 1 },
            ],
        };
        var snap = Eval(Page(Evidence(perf)));
        var finding = snap.Findings.Single(f => f.RuleId == "res-duplicate-network");
        finding.Evidence.Should().ContainSingle().Which.Should().Contain("3× network").And.Contain("/api/config");
        finding.Evidence.Should().NotContain(e => e.Contains("logo.svg"), "a repeat served from the browser cache is not a network duplicate");
        snap.Metric("duplicate-resources")!.Note.Should().Contain("1 URL(s) transferred repeatedly").And.Contain("1 repeated from the browser cache");
    }

    [Fact]
    public void FailedResources_BecomeExplicitHighFinding_NeverSilentlyOmitted()
    {
        var perf = Perf() with { FailedResourceCount = 1, FailedResources = [new BrowserResourceEntry { Url = Origin + "/_framework/missing.wasm", Kind = "wasm", Status = 404 }] };
        var snap = Eval(Page(Evidence(perf)));
        snap.Metric("failed-resources")!.Status.Should().Be(PerformanceMetricStatus.Poor);
        snap.Findings.Should().Contain(f => f.RuleId == "res-failed" && f.Severity == FrontendQualitySeverity.High && f.Evidence.Contains("404 wasm " + Origin + "/_framework/missing.wasm"));
    }

    [Fact]
    public void CacheQuality_StaticAssetRepeatedlyTransferredWithoutPolicy_Flagged_FromProxyMetadata()
    {
        var asset = Rest("/_framework/dotnet.native.wasm", (400, 200, 1), (410, 200, 2), (390, 200, 3)) with { Category = ObservedTrafficCategory.StaticAsset, Host = "m2lbdev.bufetat.no", CacheDirectives = "no-store", HasEtag = false };
        var snap = Eval(Page(Evidence(Perf()), asset));
        snap.Findings.Should().Contain(f => f.RuleId == "cache-static-repeated-transfer" && f.EvidenceSource == PerformanceQualitySources.Proxy && f.Evidence.Contains("Cache-Control: no-store"));
        snap.Findings.Should().Contain(f => f.RuleId == "cache-framework-no-policy");
        var cached = asset with { CacheDirectives = "public, max-age=31536000, immutable", NotModifiedCount = 2 };
        Eval(Page(Evidence(Perf()), cached)).Findings.Should().NotContain(f => f.RuleId.StartsWith("cache-"));
    }

    // ── API / network ─────────────────────────────────────────────────────────

    [Fact]
    public void Api_Latency_Counts_Duplicates_Statuses_AndMinimumSampleRule()
    {
        var children = Gql("GetChildren", (1200, 200, 1.0), (1150, 200, 1.4), (1300, 200, 1.9), (1250, 200, 2.3), (1180, 200, 2.8), (1220, 200, 3.2), (1210, 200, 3.7));
        var roles = Gql("GetRoles", (90, 200, 1.1), (95, 200, 1.6), (88, 200, 2.0), (92, 200, 2.5), (91, 200, 3.0));
        var two = Rest("/api/config", (640, 200, 1), (650, 200, 2));
        var failing = Rest("/api/placements", (300, 500, 1), (290, 200, 2), (310, 500, 3));
        var auth = Rest("/api/me", (50, 401, 1), (55, 401, 2), (52, 401, 3));
        var snap = Eval(Page(Evidence(Perf()), children, roles, two, failing, auth));
        var api = snap.Api;

        api.Available.Should().BeTrue(); api.TimingAvailable.Should().BeTrue();
        api.GraphQlCalls.Should().Be(12); api.RestCalls.Should().Be(8);
        var getChildren = api.Operations.Single(o => o.OperationName == "GetChildren");
        getChildren.Count.Should().Be(7);
        getChildren.Display.Should().Be("Query GetChildren");
        getChildren.Statistics.Sufficient.Should().BeTrue();
        getChildren.Statistics.P50.Should().Be(1210);
        getChildren.Statistics.P95.Should().Be(1300);
        getChildren.LatencyStatus.Should().Be(PerformanceMetricStatus.Poor);
        getChildren.IsDuplicate.Should().BeTrue();
        api.Operations.Single(o => o.OperationName == "GetRoles").LatencyStatus.Should().Be(PerformanceMetricStatus.Good);
        var config = api.Operations.Single(o => o.Path == "/api/config");
        config.Statistics.Sufficient.Should().BeFalse("two samples are not enough for percentiles");
        config.Statistics.P50.Should().BeNull(); config.Statistics.P95.Should().BeNull();
        config.Statistics.RepresentativeLabel.Should().Be("observed");
        config.Statistics.Representative.Should().Be(650);
        config.LatencyStatus.Should().Be(PerformanceMetricStatus.NeedsImprovement, "650 ms observed > 500 ms default warning");
        api.SlowCalls.Should().Be(9, "7 GetChildren + 2 config calls above 500 ms");
        api.ErrorResponses.Should().Be(5); api.AuthRejected.Should().Be(3);
        api.DuplicateOperations.Select(d => d.Display).Should().BeEquivalentTo(["Query GetChildren", "Query GetRoles", "GET api-dev.bufetat.no/api/placements", "GET api-dev.bufetat.no/api/me"]);
        api.SlowestOperation!.OperationName.Should().Be("GetChildren");
        api.Overall.Sufficient.Should().BeTrue();
        snap.Metric("slowest-api")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 1210 && m.Status == PerformanceMetricStatus.Poor && m.Source == PerformanceQualitySources.Proxy);
        snap.Metric("api-p50")!.Status.Should().Be(PerformanceMetricStatus.Informational);
        snap.Metric("api-errors")!.Status.Should().Be(PerformanceMetricStatus.Poor);

        snap.Findings.Should().Contain(f => f.RuleId == "api-slow" && f.Severity == FrontendQualitySeverity.High && f.Title.Contains("GetChildren") && f.ObservedValue == "1.2 s" && f.Evidence.Contains("Calls: 7") && f.Threshold.Contains("500 ms"));
        snap.Findings.Should().Contain(f => f.RuleId == "api-slow" && f.Severity == FrontendQualitySeverity.Medium && f.Title.Contains("/api/config") && f.Evidence.Contains("Observed (observed): 650 ms"));
        var dup = snap.Findings.Single(f => f.RuleId == "api-duplicate-graphql" && f.Title.Contains("GetChildren"));
        dup.Severity.Should().Be(FrontendQualitySeverity.High, "7 > 3 × 2");
        dup.ObservedValue.Should().Be("7 calls"); dup.Threshold.Should().Contain("2"); dup.ThresholdSource.Should().Be(PerformanceThresholdSource.Default);
        dup.EvidenceSource.Should().Be(PerformanceQualitySources.Correlated); dup.Confidence.Should().Be(PerformanceConfidence.Medium);
        dup.Evidence.Should().Contain("Calls: 7").And.Contain(e => e.StartsWith("Window:")).And.Contain(e => e.StartsWith("Total latency:"));
        snap.Findings.Should().Contain(f => f.RuleId == "api-errors" && f.Severity == FrontendQualitySeverity.High && f.Evidence.Any(e => e.Contains("/api/placements")) && !f.Evidence.Any(e => e.Contains("/api/me")));
        snap.Findings.Should().Contain(f => f.RuleId == "api-auth-rejected" && f.Evidence.Any(e => e.Contains("/api/me: 3× 401/403")));
        snap.Coverage.Overall.Should().Be(PerformanceAssessmentState.Complete);
    }

    [Fact]
    public void GraphQlDuplicates_OneRowPerOperation_Count7_NoBodyPersisted()
    {
        var snap = Eval(Page(Evidence(Perf()), Gql("GetChildren", (400, 200, 1), (410, 200, 2), (420, 200, 3), (400, 200, 4), (390, 200, 5), (405, 200, 6), (415, 200, 7))));
        snap.Api.Operations.Should().ContainSingle(o => o.Kind == ApiOperationKind.GraphQl).Which.Count.Should().Be(7);
        snap.Findings.Should().ContainSingle(f => f.RuleId == "api-duplicate-graphql").Which.Title.Should().Contain("GetChildren");
        var json = JsonSerializer.Serialize(snap);
        json.Should().NotContainAny("query {", "Bearer", "Authorization", "Cookie", "access_token");
    }

    [Fact]
    public void PollingLikeCadence_KeepsEvidence_LowersConfidence_AndIsReportedAsHeuristic()
    {
        var poll = Rest("/api/notifications", Enumerable.Range(0, 8).Select(i => (60.0, 200, i * 5)).ToArray());
        var snap = Eval(Page(Evidence(Perf()), poll));
        var op = snap.Api.Operations.Single();
        op.IsPollingLike.Should().BeTrue();
        op.IsDuplicate.Should().BeTrue("evidence is never suppressed");
        var finding = snap.Findings.Single(f => f.RuleId == "api-duplicate-rest");
        finding.Title.Should().Contain("polling-like cadence");
        finding.Confidence.Should().Be(PerformanceConfidence.Low);
        finding.Explanation.Should().Contain("no configured polling classification exists");
        PerformanceQualityRules.IsPollingLike([new(T0, 10, 200, null), new(T0.AddSeconds(1), 10, 200, null), new(T0.AddSeconds(30), 10, 200, null), new(T0.AddSeconds(31), 10, 200, null)]).Should().BeFalse("irregular intervals");
    }

    [Fact]
    public void Burst_Detected_WhenManyRequestsCompleteWithinTheWindow()
    {
        var burst = Enumerable.Range(0, 31).Select(i => Gql($"Op{i}", (80, 200, 1.0 + i * 0.04))).ToArray();
        var snap = Eval(Page(Evidence(Perf()), burst));
        snap.Api.Bursts.Should().ContainSingle().Which.RequestCount.Should().Be(31);
        snap.Api.Bursts[0].GraphQlCount.Should().Be(31);
        snap.Findings.Should().Contain(f => f.RuleId == "api-burst" && f.Explanation.Contains("31 API requests") && f.Confidence == PerformanceConfidence.Medium);
        var spread = Enumerable.Range(0, 31).Select(i => Gql($"Op{i}", (80, 200, 1.0 + i * 2.0))).ToArray();
        Eval(Page(Evidence(Perf()), spread)).Api.Bursts.Should().BeEmpty();
    }

    [Fact]
    public void SequentialPattern_ObservedNotCausal_LowConfidence()
    {
        // A: 1.0→1.3 s, B starts 1.35 s (50 ms after A), C starts 1.75 s (50 ms after B), D far later.
        var a = Rest("/api/a", (300, 200, 0)) with { Samples = [new(T0.AddSeconds(1.3), 300, 200, null)] };
        var b = Rest("/api/b", (300, 200, 0)) with { Samples = [new(T0.AddSeconds(1.7), 350, 200, null)] };
        var c = Rest("/api/c", (300, 200, 0)) with { Samples = [new(T0.AddSeconds(2.05), 300, 200, null)] };
        var d = Rest("/api/d", (300, 200, 0)) with { Samples = [new(T0.AddSeconds(9), 100, 200, null)] };
        var snap = Eval(Page(Evidence(Perf()), a, b, c, d));
        snap.Api.SequentialPatterns.Should().ContainSingle().Which.Operations.Should().Equal("GET api-dev.bufetat.no/api/a", "GET api-dev.bufetat.no/api/b", "GET api-dev.bufetat.no/api/c");
        var finding = snap.Findings.Single(f => f.RuleId == "api-sequential");
        finding.Title.Should().Be("Observed sequential request pattern");
        finding.Explanation.Should().Contain("not proof of a dependency").And.NotContain("depends on");
        finding.Confidence.Should().Be(PerformanceConfidence.Low);
    }

    [Fact]
    public void ThresholdOverride_TargetEnvironment_ClassificationAndSourceShown()
    {
        var page = Page(Evidence(Perf()), Rest("/api/children", (650, 200, 1)));
        var withDefault = Eval(page);
        withDefault.Metric("slowest-api")!.Should().Match<PerformanceQualityMetric>(m => m.Status == PerformanceMetricStatus.NeedsImprovement && m.Threshold!.Source == PerformanceThresholdSource.Default);
        withDefault.Findings.Should().Contain(f => f.RuleId == "api-slow");

        var overridden = new FrontendPerformanceThresholds { Mode = FrontendThresholdMode.Custom, ApiResponseWarningMs = 800 };
        var withOverride = Eval(page, overridden);
        withOverride.Metric("slowest-api")!.Should().Match<PerformanceQualityMetric>(m => m.Status == PerformanceMetricStatus.Good && m.Threshold!.Source == PerformanceThresholdSource.TargetEnvironment && m.Threshold.SourceLabel == "Target Environment");
        withOverride.Findings.Should().NotContain(f => f.RuleId == "api-slow");

        var strict = new FrontendPerformanceThresholds { Mode = FrontendThresholdMode.Strict, ApiResponseWarningMs = 300, ApiResponsePoorMs = 800 };
        Eval(page, strict).Metric("slowest-api")!.Threshold!.Source.Should().Be(PerformanceThresholdSource.Policy);
        var vitals = new CoreWebVitalsThresholds { LcpGoodMs = 3000 };
        Eval(page, null, vitals).Metric("lcp")!.Should().Match<PerformanceQualityMetric>(m => m.Status == PerformanceMetricStatus.Good && m.Threshold!.Source == PerformanceThresholdSource.TargetEnvironment);
    }

    // ── Blazor WASM ───────────────────────────────────────────────────────────

    [Fact]
    public void Blazor_FrameworkTotals_LargePayload_BootFailure_AssemblyCount()
    {
        var snap = Eval(Page(Evidence(Perf())));
        snap.Metric("blazor-framework-bytes")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 6_600_000 && m.Status == PerformanceMetricStatus.NeedsImprovement);
        snap.Metric("blazor-wasm-bytes")!.Value.Should().Be(6_300_000);
        snap.Metric("blazor-assemblies")!.Should().Match<PerformanceQualityMetric>(m => m.Value == 60 && m.Status == PerformanceMetricStatus.Good);
        snap.Metric("blazor-startup")!.Value.Should().Be(2400);
        snap.Metric("blazor-load-kind")!.Display.Should().Be("Cold");
        snap.Findings.Should().Contain(f => f.RuleId == "blazor-large-framework" && f.ObservedValue == "6.3 MB" && f.Evidence.Contains("Assemblies: 60 (2.9 MB)"));

        var failed = new BrowserBlazorSummary { Detected = true, BootManifestObserved = true, BootManifestFailed = true, FrameworkFailures = [new BrowserResourceEntry { Url = Origin + "/_framework/blazor.boot.json", Kind = "framework-data", Status = 500 }], AssemblyCount = 130, LoadKind = "cold", FrameworkResourceCount = 131 };
        var failedSnap = Eval(Page(Evidence(Perf(), blazor: failed)));
        failedSnap.Findings.Should().Contain(f => f.RuleId == "blazor-boot-failed" && f.Severity == FrontendQualitySeverity.Critical);
        failedSnap.Findings.Should().Contain(f => f.RuleId == "blazor-many-assemblies" && f.ObservedValue == "130");
    }

    [Fact]
    public void Blazor_WarmNavigation_NotFlaggedAsColdPayload_ColdFrameworkAfterSpaFlagged()
    {
        var warm = new BrowserBlazorSummary { Detected = true, FrameworkResourceCount = 70, CachedFrameworkResourceCount = 70, FrameworkBytes = 6_600_000, WasmBytes = 6_300_000, LoadKind = "warm" };
        var warmSnap = Eval(Page(Evidence(Perf() with { ObservationType = "spa-navigation" }, blazor: warm)));
        warmSnap.Findings.Should().NotContain(f => f.RuleId == "blazor-large-framework" || f.RuleId == "blazor-framework-after-spa-navigation");
        warmSnap.Metric("blazor-load-kind")!.Display.Should().Be("Warm");
        warmSnap.Metric("blazor-startup")!.Status.Should().Be(PerformanceMetricStatus.NotMeasured, "startup applies to the initial load");

        var cold = new BrowserBlazorSummary { Detected = true, FrameworkResourceCount = 12, CachedFrameworkResourceCount = 0, FrameworkBytes = 900_000, LoadKind = "cold" };
        Eval(Page(Evidence(Perf() with { ObservationType = "spa-navigation" }, blazor: cold))).Findings.Should().Contain(f => f.RuleId == "blazor-framework-after-spa-navigation" && f.Confidence == PerformanceConfidence.Medium);
    }

    [Fact]
    public void Blazor_NotDetected_IsInformational_NotAFinding()
    {
        var snap = Eval(Page(Evidence(Perf(), blazor: new BrowserBlazorSummary { Detected = false })));
        snap.Metric("blazor-detected")!.Display.Should().Be("Not detected");
        snap.Findings.Should().NotContain(f => f.Category == PerformanceLayer.Blazor);
        snap.Coverage.Blazor.Should().Be(PerformanceCoverageState.Complete);
    }

    // ── Coverage / partial assessment ─────────────────────────────────────────

    [Fact]
    public void CompanionOnly_BrowserMetricsWork_ApiNotMeasured_PartialAssessment()
    {
        var snap = Eval(Page(Evidence(Perf())));
        snap.Coverage.Should().Match<PerformanceCoverage>(c => c.Browser == PerformanceCoverageState.Complete && c.Api == PerformanceCoverageState.NotAvailable && c.Overall == PerformanceAssessmentState.Partial);
        snap.Coverage.Reasons.Should().ContainSingle(r => r.StartsWith("Network/API: not available"));
        snap.Metric("rest-calls")!.Status.Should().Be(PerformanceMetricStatus.NotMeasured);
        snap.Metric("slowest-api")!.Note.Should().Contain("No Local HTTPS proxy traffic");
        snap.Metric("lcp")!.Status.Should().NotBe(PerformanceMetricStatus.NotMeasured);
    }

    [Fact]
    public void ProxyOnly_ApiMetricsWork_BrowserExplicitlyNotMeasured_PartialAssessment()
    {
        var snap = Eval(Page(null, Rest("/api/children", (700, 200, 1), (720, 200, 2), (690, 200, 3), (710, 200, 4), (705, 200, 5), (698, 200, 6))));
        snap.Coverage.Should().Match<PerformanceCoverage>(c => c.Browser == PerformanceCoverageState.NotAvailable && c.Runtime == PerformanceCoverageState.NotAvailable && c.Api == PerformanceCoverageState.Complete && c.Overall == PerformanceAssessmentState.Partial);
        snap.Coverage.Reasons.Should().Contain(r => r.StartsWith("Browser performance: not assessed"));
        snap.Metric("lcp").Should().BeNull("no browser evidence → no browser metrics are fabricated");
        snap.Metric("slowest-api")!.Status.Should().Be(PerformanceMetricStatus.NeedsImprovement);
        snap.ObservationType.Should().Be(PerformancePhase.Runtime);
        snap.Findings.Should().OnlyContain(f => f.Category == PerformanceLayer.Api || f.Category == PerformanceLayer.Resources);
        snap.HasEvidence.Should().BeTrue();
    }

    [Fact]
    public void NoEvidence_IsNotAssessed_NotCompletedWithoutFindings()
    {
        var snap = Eval(Page());
        snap.HasEvidence.Should().BeFalse();
        snap.Coverage.Overall.Should().Be(PerformanceAssessmentState.NotAssessed);
        snap.Coverage.OverallLabel.Should().Be("Not assessed");
        snap.Findings.Should().BeEmpty();
        snap.Metrics.Where(m => m.Layer == PerformanceLayer.Api).Should().OnlyContain(m => m.Status == PerformanceMetricStatus.NotMeasured);
        PerformanceCoverage.Merge([]).Reasons.Should().ContainSingle().Which.Should().Contain("No performance evidence collected");
    }

    [Fact]
    public void EndpointsWithoutTimingSamples_CountsOnly_ApiCoveragePartial()
    {
        var legacy = Rest("/api/children") with { Count = 4, Samples = [] };
        var snap = Eval(Page(Evidence(Perf()), legacy));
        snap.Api.TimingAvailable.Should().BeFalse();
        snap.Coverage.Api.Should().Be(PerformanceCoverageState.Partial);
        snap.Metric("rest-calls")!.Value.Should().Be(4);
        snap.Metric("slow-calls")!.Status.Should().Be(PerformanceMetricStatus.NotMeasured);
        snap.Findings.Should().Contain(f => f.RuleId == "api-duplicate-rest", "duplicate detection still works from counts");
    }

    // ── Stabilization bounds, comparison, timeline, overview ──────────────────

    [Fact]
    public void Stabilization_GoodQuiet_And_PoorBounded_ClassifiedAgainstTargetThresholds()
    {
        var thresholds = new FrontendPerformanceThresholds { Mode = FrontendThresholdMode.Custom, PageStabilizationGoodMs = 1000, PageStabilizationPoorMs = 3000 };
        var snap = Eval(Page(Evidence(Perf())), thresholds);
        snap.Metric("stabilization")!.Should().Match<PerformanceQualityMetric>(m => m.Status == PerformanceMetricStatus.NeedsImprovement && m.Threshold!.Source == PerformanceThresholdSource.TargetEnvironment);
        snap.Findings.Single(f => f.RuleId == "perf-stabilization").ThresholdSource.Should().Be(PerformanceThresholdSource.TargetEnvironment);
    }

    [Fact]
    public void Comparison_CurrentVsPreviousGeneration_DeltasAndDirection()
    {
        var page = Page(Evidence(Perf()));
        page.AnalysisGeneration = 2;
        page.PerformanceHistory =
        [
            new PagePerformanceHistoryEntry { Generation = 1, ObservationType = "initial-load", CapturedAt = T0.AddMinutes(-10), LcpMs = 2100, StabilizationMs = 1800, TransferredBytes = 8_000_000, LongTaskCount = 2, ApiCalls = 18, GraphQlCalls = 18 },
            new PagePerformanceHistoryEntry { Generation = 2, ObservationType = "initial-load", CapturedAt = T0, LcpMs = 2900, StabilizationMs = 1800, TransferredBytes = 8_700_000, LongTaskCount = 4, ApiCalls = 31, GraphQlCalls = 31 },
        ];
        var cmp = Eval(page).Comparison!;
        cmp.CurrentGeneration.Should().Be(2); cmp.PreviousGeneration.Should().Be(1); cmp.SamePhase.Should().BeTrue();
        cmp.Deltas.Single(d => d.Metric == "LCP").Should().Match<PerformanceDelta>(d => d.Current == "2.9 s" && d.Previous == "2.1 s" && d.Change == "+38%" && d.Worse == true);
        cmp.Deltas.Single(d => d.Metric == "GraphQL calls").Change.Should().Be("+13");
        cmp.Deltas.Single(d => d.Metric == "Page stabilization").Worse.Should().BeFalse();
        Eval(Page(Evidence(Perf()))).Comparison.Should().BeNull("a single generation has nothing to compare with");
    }

    [Fact]
    public void Timeline_MergesBrowserResourcesAndProxyCalls_ChronologicallyAndBounded()
    {
        var perf = Perf() with { Timeline = Enumerable.Range(0, 90).Select(i => new BrowserResourceEntry { Url = $"{Origin}/r{i}.js", Kind = "js", StartMs = i * 10, DurationMs = 5 }).ToList() };
        var api = Rest("/api/children") with { Count = 2, Samples = [new(T0.AddMilliseconds(500), 200, 200, 1000), new(T0.AddMilliseconds(250), 100, 200, 500)] };
        var timeline = Eval(Page(Evidence(perf), api)).Timeline;
        timeline.Should().HaveCount(PerformanceQualityRules.MaxTimelineEntries);
        timeline.Should().BeInAscendingOrder(t => t.StartMs);
        timeline.Should().Contain(t => t.Source == PerformanceQualitySources.Proxy && t.StartMs == 150 && t.Kind == "rest" && t.Label.Contains("/api/children"));
        timeline.Should().Contain(t => t.Source == PerformanceQualitySources.Proxy && t.StartMs == 300 && t.DurationMs == 200);
    }

    [Fact]
    public void Overview_SortsPagesByWorstMetric()
    {
        var good = Eval(Page(Evidence(Perf() with { LcpMs = 1200, LongTaskCount = 0, LongTaskTotalMs = 0, LongestTaskMs = null, MainThreadBlockingMs = 0, TransferredBytes = 1_000_000, JsBytes = 100_000, WasmBytes = 500_000, ResourceCount = 10, LargestResource = new BrowserResourceEntry { Url = Origin + "/app.js", Kind = "js", TransferBytes = 100_000, DurationMs = 200 }, SlowestResource = new BrowserResourceEntry { Url = Origin + "/app.js", Kind = "js", TransferBytes = 100_000, DurationMs = 200 } }, path: "/good", blazor: new BrowserBlazorSummary { Detected = false })));
        var bad = Eval(Page(Evidence(Perf() with { LcpMs = 5000 }, path: "/bad")));
        var rows = PerformanceQualityRules.Overview([good, bad]);
        rows[0].PageId.Should().EndWith("/bad"); rows[0].Status.Should().Be(PerformanceMetricStatus.Poor); rows[0].WorstMetric.Should().Be("Largest Contentful Paint");
        rows[1].Status.Should().Be(PerformanceMetricStatus.Good);
        rows[0].Lcp!.Display.Should().Be("5.0 s");
    }

    [Fact]
    public void LatencyStatistics_MinimumSampleRule_NearestRankPercentiles()
    {
        PerformanceLatencyStatistics.Compute([100, 200]).Should().Match<PerformanceLatencyStatistics>(s => !s.Sufficient && s.P50 == null && s.Max == 200 && s.Representative == 200);
        var stats = PerformanceLatencyStatistics.Compute([100, 200, 300, 400, 1000]);
        stats.Sufficient.Should().BeTrue(); stats.P50.Should().Be(300); stats.P95.Should().Be(1000); stats.Min.Should().Be(100); stats.Total.Should().Be(2000);
    }

    [Fact]
    public void SnapshotSerialization_ContainsNoCredentialShapedValues()
    {
        var api = Rest("/api/children", (700, 200, 1)) with { CacheDirectives = "private, max-age=60", HasEtag = true };
        var snap = Eval(Page(Evidence(Perf()), api, Gql("GetChildren", (900, 200, 1), (950, 200, 2), (910, 200, 3))));
        var json = JsonSerializer.Serialize(snap);
        json.Should().NotContainAny("Bearer ", "Authorization", "Cookie", "Set-Cookie", "access_token", "refresh_token", "eyJ", "?token=");
        json.Should().Contain("GetChildren").And.Contain("max-age=60");
    }
}
