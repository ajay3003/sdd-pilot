using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Resolved thresholds of BirkNext Performance Quality with provenance (Default / Target Environment / Policy). Web Vitals and
/// startup thresholds come from the Target Environment's saved settings; a value equal to the documented default is labelled
/// Default, a Strict preset is labelled Policy, anything else Target Environment.
/// </summary>
public sealed record PerformanceThresholdSet(
    PerformanceThreshold Lcp, PerformanceThreshold Cls, PerformanceThreshold Inp, PerformanceThreshold Ttfb, PerformanceThreshold Fcp,
    PerformanceThreshold Stabilization, PerformanceThreshold ApiResponse, PerformanceThreshold LongTasks, PerformanceThreshold MainThreadBlocking,
    PerformanceThreshold StartupRequests, PerformanceThreshold StartupTransfer, PerformanceThreshold JsTransfer, PerformanceThreshold IndividualAsset,
    PerformanceThreshold SlowResource, PerformanceThreshold Wasm, PerformanceThreshold Framework, PerformanceThreshold IdenticalApiCalls,
    PerformanceThreshold Assemblies)
{
    public IReadOnlyList<PerformanceThreshold> All =>
    [
        Lcp, Cls, Inp, Ttfb, Fcp, Stabilization, ApiResponse, LongTasks, MainThreadBlocking, StartupRequests, StartupTransfer, JsTransfer,
        IndividualAsset, SlowResource, Wasm, Framework, IdenticalApiCalls, Assemblies,
    ];
}

/// <summary>
/// BirkNext Performance Quality — deterministic, pure evaluation of one page's recorded evidence (Browser Companion page/runtime/resource/
/// Blazor summaries + Local HTTPS proxy API samples) into metrics, findings, coverage, API summary, timeline and generation comparison.
/// No I/O, so every rule is unit-testable with synthetic evidence. It never fabricates a metric: what was not observed is Not measured.
/// Correlation between browser and proxy evidence is worded "correlated"/"observed", never "caused by".
/// </summary>
public static class PerformanceQualityRules
{
    // ── Documented defaults that have no configured counterpart (named constants, not settings) ──
    /// <summary>web.dev Time to First Byte guidance.</summary>
    public const double TtfbGoodMs = 800, TtfbPoorMs = 1800;
    /// <summary>Web Vitals First Contentful Paint guidance.</summary>
    public const double FcpGoodMs = 1800, FcpPoorMs = 3000;
    /// <summary>A "network burst": at least this many REST/GraphQL requests completing inside one window.</summary>
    public const int BurstMinRequests = 15;
    public const double BurstWindowMs = 1500;
    /// <summary>Sequential pattern: the next request starts within this gap after the previous one finished; chains of at least this length are reported.</summary>
    public const double SequentialGapMs = 150;
    public const int SequentialMinLength = 3;
    /// <summary>Blazor assembly/resource count above which trimming/lazy loading is worth reviewing (documented heuristic).</summary>
    public const int ManyAssembliesThreshold = 100;
    /// <summary>Large DOM mutation batches after stabilization that indicate sustained churn.</summary>
    public const int SustainedChurnBatches = 3;
    /// <summary>Polling-like cadence heuristic: at least this many samples spanning at least this window with regular intervals.</summary>
    public const int PollingMinSamples = 4;
    public const double PollingMinSpanMs = 10_000;
    public const double PollingCadenceTolerance = 0.3;
    public const int MaxTimelineEntries = 80;
    public const int MaxPatterns = 5;

    private static readonly FrontendPerformanceThresholds DefaultPerformance = new();
    private static readonly CoreWebVitalsThresholds DefaultVitals = new();

    // ── Thresholds ─────────────────────────────────────────────────────────────

    public static PerformanceThresholdSet ResolveThresholds(FrontendPerformanceThresholds perf, CoreWebVitalsThresholds vitals)
    {
        PerformanceThresholdSource Src(Func<FrontendPerformanceThresholds, double> pick) =>
            perf.Mode == FrontendThresholdMode.Strict ? PerformanceThresholdSource.Policy
            : Math.Abs(pick(perf) - pick(DefaultPerformance)) < 1e-9 ? PerformanceThresholdSource.Default : PerformanceThresholdSource.TargetEnvironment;
        PerformanceThresholdSource Vit(Func<CoreWebVitalsThresholds, double> pick) =>
            Math.Abs(pick(vitals) - pick(DefaultVitals)) < 1e-9 ? PerformanceThresholdSource.Default : PerformanceThresholdSource.TargetEnvironment;
        static PerformanceThresholdSource Both(PerformanceThresholdSource a, PerformanceThresholdSource b) => a == b ? a : PerformanceThresholdSource.TargetEnvironment;

        return new PerformanceThresholdSet(
            Lcp: new("LCP", vitals.LcpGoodMs, vitals.LcpPoorMs, "ms", Both(Vit(v => v.LcpGoodMs), Vit(v => v.LcpPoorMs))),
            Cls: new("CLS", vitals.ClsGood, vitals.ClsPoor, "score", Both(Vit(v => v.ClsGood), Vit(v => v.ClsPoor))),
            Inp: new("INP", vitals.InpGoodMs, vitals.InpPoorMs, "ms", Both(Vit(v => v.InpGoodMs), Vit(v => v.InpPoorMs))),
            Ttfb: new("TTFB", TtfbGoodMs, TtfbPoorMs, "ms", PerformanceThresholdSource.Default),
            Fcp: new("FCP", FcpGoodMs, FcpPoorMs, "ms", PerformanceThresholdSource.Default),
            Stabilization: new("Page stabilization", perf.PageStabilizationGoodMs, perf.PageStabilizationPoorMs, "ms", Both(Src(p => p.PageStabilizationGoodMs), Src(p => p.PageStabilizationPoorMs))),
            ApiResponse: new("API response", perf.ApiResponseWarningMs, perf.ApiResponsePoorMs, "ms", Both(Src(p => p.ApiResponseWarningMs), Src(p => p.ApiResponsePoorMs))),
            LongTasks: new("Long tasks", perf.MaxLongTasks, perf.MaxLongTasks * 2d, "count", Src(p => p.MaxLongTasks)),
            MainThreadBlocking: new("Main-thread blocking", perf.MainThreadBlockingWarningMs, perf.MainThreadBlockingWarningMs * 2d, "ms", Src(p => p.MainThreadBlockingWarningMs)),
            StartupRequests: new("Initial requests", perf.MaxStartupRequests, null, "count", Src(p => p.MaxStartupRequests)),
            StartupTransfer: new("Initial transfer", perf.MaxStartupSizeBytes, null, "bytes", Src(p => p.MaxStartupSizeBytes)),
            JsTransfer: new("JavaScript transfer", perf.MaxJsTransferBytes, null, "bytes", Src(p => p.MaxJsTransferBytes)),
            IndividualAsset: new("Single resource", perf.MaxIndividualAssetSizeBytes, null, "bytes", Src(p => p.MaxIndividualAssetSizeBytes)),
            SlowResource: new("Resource duration", perf.SlowResourceMs, null, "ms", Src(p => p.SlowResourceMs)),
            Wasm: new("WASM payload", perf.MaxWasmRuntimeSizeBytes, null, "bytes", Src(p => p.MaxWasmRuntimeSizeBytes)),
            Framework: new("Framework payload", perf.MaxFrameworkSizeBytes, null, "bytes", Src(p => p.MaxFrameworkSizeBytes)),
            IdenticalApiCalls: new("Identical API calls", perf.MaxIdenticalApiCalls, null, "count", Src(p => p.MaxIdenticalApiCalls)),
            Assemblies: new("Assemblies", ManyAssembliesThreshold, null, "count", PerformanceThresholdSource.Default));
    }

    /// <summary>Good ≤ good; Poor &gt; poor; otherwise Needs improvement. Null value → Not measured; no thresholds → Informational.</summary>
    public static PerformanceMetricStatus Classify(double? value, PerformanceThreshold? threshold)
    {
        if (value is null) return PerformanceMetricStatus.NotMeasured;
        if (threshold is null || (threshold.Good is null && threshold.Poor is null)) return PerformanceMetricStatus.Informational;
        var v = value.Value;
        if (threshold.Good is { } good && v <= good) return PerformanceMetricStatus.Good;
        if (threshold.Poor is { } poor) return v > poor ? PerformanceMetricStatus.Poor : PerformanceMetricStatus.NeedsImprovement;
        return threshold.Good is null ? PerformanceMetricStatus.Good : PerformanceMetricStatus.NeedsImprovement;
    }

    // ── Evaluation ─────────────────────────────────────────────────────────────

    public static PagePerformanceSnapshot Evaluate(PageAnalysis page, FrontendPerformanceThresholds performance, CoreWebVitalsThresholds vitals)
    {
        var t = ResolveThresholds(performance, vitals);
        var ev = page.BrowserEvidence;
        var perf = ev?.Performance;
        var api = SummarizeApi(page, t);
        var phase = perf is null ? PerformancePhase.Runtime : perf.ObservationType == "spa-navigation" ? PerformancePhase.SpaNavigation : PerformancePhase.InitialLoad;
        var at = ev?.CapturedAt ?? (page.LastObservedAt == default ? DateTimeOffset.UtcNow : page.LastObservedAt);
        var metrics = new List<PerformanceQualityMetric>();
        var findings = new List<PerformanceQualityFinding>();
        var notes = new List<string>();
        var identity = page.Identity;

        if (perf is null)
        {
            notes.Add("No Browser Companion evidence for this page: browser, runtime, resource and Blazor metrics are not measured.");
            if (api.Available) notes.Add("Only proxy-observed API traffic exists; the observation phase is unknown, so API findings are attributed to the runtime phase.");
        }
        else
        {
            EvaluatePage(perf, phase, t, identity, at, metrics, findings);
            EvaluateRuntime(perf, ev!.Runtime, phase, t, identity, at, metrics, findings);
            EvaluateResources(perf, phase, t, identity, at, metrics, findings, page);
            EvaluateBlazor(ev.Blazor, perf, phase, t, identity, at, metrics, findings);
            if (phase == PerformancePhase.SpaNavigation)
                notes.Add("SPA navigation observation: LCP/CLS and Navigation Timing apply to full document loads only. Reload the target application page to collect a fresh initial-load measurement.");
        }
        EvaluateApi(api, phase, t, identity, at, metrics, findings, perf);

        var coverage = Coverage(ev, perf, api);
        return new PagePerformanceSnapshot
        {
            PageId = identity, PageTitle = page.Title, Generation = page.AnalysisGeneration, ObservationType = phase,
            StartedAt = ev?.VisitStartedAt, CapturedAt = ev?.CapturedAt,
            StabilizedAt = ev is not null && perf?.StabilizationMs is { } stab ? ev.VisitStartedAt.AddMilliseconds(stab) : null,
            BrowserName = ev?.BrowserName, Metrics = metrics, Findings = findings.OrderBy(f => f.Severity).ToList(), Api = api, Coverage = coverage,
            Resources = perf, Blazor = ev?.Blazor, Timeline = Timeline(page), Comparison = Compare(page), Notes = notes,
        };
    }

    private static PerformanceCoverage Coverage(BrowserPageEvidence? ev, BrowserPerformanceSummary? perf, ApiPerformanceSummary api)
    {
        var reasons = new List<string>();
        var browser = perf is null ? PerformanceCoverageState.NotAvailable : PerformanceCoverageState.Complete;
        var runtime = perf is null ? PerformanceCoverageState.NotAvailable
            : perf.UnsupportedMetrics.Any(m => m.StartsWith("longtask", StringComparison.OrdinalIgnoreCase)) ? PerformanceCoverageState.Partial : PerformanceCoverageState.Complete;
        var resources = perf is null ? PerformanceCoverageState.NotAvailable : PerformanceCoverageState.Complete;
        var blazor = ev is null ? PerformanceCoverageState.NotAvailable : PerformanceCoverageState.Complete;
        var apiState = !api.Available ? PerformanceCoverageState.NotAvailable : api.TimingAvailable ? PerformanceCoverageState.Complete : PerformanceCoverageState.Partial;
        if (perf is null) reasons.Add("Browser performance: not assessed — Browser Companion evidence not collected for this page.");
        if (runtime == PerformanceCoverageState.Partial) reasons.Add("Runtime: long task observation not supported by this browser.");
        if (!api.Available) reasons.Add("Network/API: not available — no Local HTTPS proxy traffic recorded for this page.");
        else if (!api.TimingAvailable) reasons.Add("Network/API: counts only — the recorded traffic carries no timing samples.");
        return new PerformanceCoverage { Browser = browser, Runtime = runtime, Resources = resources, Api = apiState, Blazor = blazor, Reasons = reasons };
    }

    // ── Page / browser layer ───────────────────────────────────────────────────

    private static void EvaluatePage(BrowserPerformanceSummary perf, PerformancePhase phase, PerformanceThresholdSet t, string page, DateTimeOffset at,
        List<PerformanceQualityMetric> metrics, List<PerformanceQualityFinding> findings)
    {
        var initial = phase == PerformancePhase.InitialLoad;
        const string spaNote = "Defined for the initial document load only (Web Vitals); not measured for SPA navigation.";
        const string navNote = "Navigation Timing applies to full document loads only.";
        metrics.Add(Metric("ttfb", "TTFB", PerformanceLayer.Page, phase, initial ? perf.TtfbMs : null, "ms", t.Ttfb, initial ? null : navNote));
        metrics.Add(Metric("fcp", "First Contentful Paint", PerformanceLayer.Page, phase, initial ? perf.FirstContentfulPaintMs : null, "ms", t.Fcp, initial ? null : spaNote));
        metrics.Add(Metric("lcp", "Largest Contentful Paint", PerformanceLayer.Page, phase, initial ? perf.LcpMs : null, "ms", t.Lcp, initial ? null : spaNote));
        metrics.Add(Metric("cls", "Cumulative Layout Shift", PerformanceLayer.Page, phase, initial ? perf.Cls : null, "score", t.Cls, initial ? null : spaNote));
        metrics.Add(Metric("dcl", "DOMContentLoaded", PerformanceLayer.Page, phase, initial ? perf.DomContentLoadedMs : null, "ms", null, initial ? null : navNote));
        metrics.Add(Metric("load", "Load event", PerformanceLayer.Page, phase, initial ? perf.LoadEventMs : null, "ms", null, initial ? null : navNote));

        var interaction = perf.Interaction;
        var inpNote = interaction?.Status switch
        {
            "measured" => $"Event Timing, {interaction.InteractionCount} interaction(s); web-vitals methodology.",
            "insufficient-samples" => $"Insufficient interaction samples ({interaction.InteractionCount} of at least {interaction.MinimumInteractions} required).",
            "not-supported" => "Event Timing with interaction ids is not supported by this browser.",
            _ => "No user interactions observed during this visit.",
        };
        metrics.Add(Metric("inp", "Interaction to Next Paint", PerformanceLayer.Page, phase, interaction?.Status == "measured" ? interaction.InpMs : null, "ms", t.Inp, inpNote));

        var bounded = perf.StabilizedBy == "max-wait";
        metrics.Add(Metric("stabilization", "Page stabilization", PerformanceLayer.Page, phase, perf.StabilizationMs, "ms", t.Stabilization,
            "BirkNext Page Stabilization Time: route change → DOM/network quiet. BirkNext-specific; not LCP." + (bounded ? " Observation ended at the bounded maximum wait." : ""),
            PerformanceQualitySources.Companion, bounded ? PerformanceConfidence.Medium : PerformanceConfidence.High));

        if (initial && perf.LcpMs is { } lcp)
        {
            var status = Classify(lcp, t.Lcp);
            if (status != PerformanceMetricStatus.Good)
                findings.Add(Finding("perf-lcp", PerformanceLayer.Page, status == PerformanceMetricStatus.Poor ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at,
                    $"Largest Contentful Paint {(status == PerformanceMetricStatus.Poor ? "is poor" : "needs improvement")}",
                    $"LCP measured at {Ms(lcp)} in the user's browser (PerformanceObserver).",
                    Ms(lcp), t.Lcp, ["Metric: LCP", $"Observed: {Ms(lcp)}", $"Threshold: {t.Lcp.Display}"],
                    "Reduce render-blocking resources, framework payload and slow authenticated API calls on the critical path."));
        }
        if (initial && perf.Cls is { } cls)
        {
            var status = Classify(cls, t.Cls);
            if (status != PerformanceMetricStatus.Good)
                findings.Add(Finding("perf-cls", PerformanceLayer.Page, status == PerformanceMetricStatus.Poor ? FrontendQualitySeverity.Medium : FrontendQualitySeverity.Low, page, phase, at,
                    $"Cumulative Layout Shift {(status == PerformanceMetricStatus.Poor ? "is poor" : "needs improvement")}",
                    $"CLS {N(cls, "0.###")} summed from layout-shift entries without recent input.", N(cls, "0.###"), t.Cls,
                    ["Metric: CLS", $"Observed: {N(cls, "0.###")}", $"Threshold: {t.Cls.Display}"],
                    "Reserve space for late content (images, tables, skeletons) and avoid inserting content above existing content."));
        }
        if (interaction?.Status == "measured" && interaction.InpMs is { } inp)
        {
            var status = Classify(inp, t.Inp);
            if (status != PerformanceMetricStatus.Good)
                findings.Add(Finding("perf-inp", PerformanceLayer.Page, status == PerformanceMetricStatus.Poor ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at,
                    $"Interaction to Next Paint {(status == PerformanceMetricStatus.Poor ? "is poor" : "needs improvement")}",
                    $"INP {Ms(inp)} from {interaction.InteractionCount} real interaction(s) (Event Timing, web-vitals methodology).", Ms(inp), t.Inp,
                    ["Metric: INP", $"Observed: {Ms(inp)}", $"Interactions: {interaction.InteractionCount}", $"Threshold: {t.Inp.Display}"],
                    "Break up long event handlers, defer non-urgent work after input and avoid synchronous layout in handlers."));
        }
        if (perf.StabilizationMs is { } stab)
        {
            var status = Classify(stab, t.Stabilization);
            if (status != PerformanceMetricStatus.Good)
                findings.Add(Finding("perf-stabilization", PerformanceLayer.Page, status == PerformanceMetricStatus.Poor ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at,
                    $"Page stabilization {(status == PerformanceMetricStatus.Poor ? "is slow" : "needs improvement")}",
                    $"BirkNext Page Stabilization Time was {Ms(stab)} ({(bounded ? "observation ended at the bounded maximum wait; the page may still have been changing" : "DOM and network went quiet")}). BirkNext-specific metric, not LCP.",
                    Ms(stab), t.Stabilization, ["Metric: BirkNext Page Stabilization Time", $"Observed: {Ms(stab)}", $"Ended by: {perf.StabilizedBy}", $"Threshold: {t.Stabilization.Display}"],
                    "Profile the data loading and rendering after the route change; load critical data first and render incrementally.",
                    confidence: bounded ? PerformanceConfidence.Medium : PerformanceConfidence.High));
        }
    }

    // ── Runtime layer ──────────────────────────────────────────────────────────

    private static void EvaluateRuntime(BrowserPerformanceSummary perf, BrowserRuntimeSummary? runtime, PerformancePhase phase, PerformanceThresholdSet t, string page, DateTimeOffset at,
        List<PerformanceQualityMetric> metrics, List<PerformanceQualityFinding> findings)
    {
        var longTasksSupported = !perf.UnsupportedMetrics.Any(m => m.StartsWith("longtask", StringComparison.OrdinalIgnoreCase));
        const string unsupported = "Long Tasks API not supported by this browser.";
        metrics.Add(Metric("long-tasks", "Long tasks", PerformanceLayer.Runtime, phase, longTasksSupported ? perf.LongTaskCount : null, "count", t.LongTasks, longTasksSupported ? "Tasks above 50 ms (Long Tasks API) during this visit." : unsupported));
        metrics.Add(Metric("main-thread-blocking", "Main-thread blocking time", PerformanceLayer.Runtime, phase, longTasksSupported ? (perf.MainThreadBlockingMs ?? (perf.LongTaskCount == 0 ? 0 : null)) : null, "ms", t.MainThreadBlocking,
            longTasksSupported ? "BirkNext main-thread blocking time: Σ max(0, duration − 50 ms) over observed long tasks. Not Lighthouse TBT (different observation window)." : unsupported));
        metrics.Add(Metric("longest-task", "Longest task", PerformanceLayer.Runtime, phase, longTasksSupported ? perf.LongestTaskMs : null, "ms", null, longTasksSupported && perf.LongTaskCount == 0 ? "No long tasks observed." : longTasksSupported ? null : unsupported));
        metrics.Add(Metric("long-tasks-after-stabilization", "Long tasks after stabilization", PerformanceLayer.Runtime, PerformancePhase.Runtime, longTasksSupported ? perf.LongTasksAfterStabilization : null, "count", null, longTasksSupported ? null : unsupported));
        var interaction = perf.Interaction;
        metrics.Add(Metric("longest-interaction", "Longest interaction", PerformanceLayer.Runtime, PerformancePhase.Runtime, interaction?.LongestInteractionMs, "ms", null,
            interaction is null || interaction.InteractionCount == 0 ? "No user interactions observed." : $"{interaction.InteractionCount} interaction(s) (Event Timing)."));
        var mutations = perf.Mutations;
        metrics.Add(Metric("dom-mutation-batches", "DOM mutation batches", PerformanceLayer.Runtime, phase, mutations?.BatchCount, "count", null,
            mutations is null ? "Not collected." : $"{mutations.MutationCount} mutation record(s); largest batch {mutations.LargestBatch}; {mutations.LargeBatchesAfterStabilization} large batch(es) after stabilization."));
        metrics.Add(Metric("js-heap", "JS heap used", PerformanceLayer.Runtime, PerformancePhase.Runtime, perf.JsHeapUsedBytes, "bytes", null,
            perf.JsHeapUsedBytes is null ? "Browser memory API not available (not measured)." : "Single informational snapshot (Chromium performance.memory); not a leak indicator."));
        metrics.Add(Metric("runtime-errors", "Runtime errors during visit", PerformanceLayer.Runtime, phase, runtime is null ? null : runtime.ErrorCount + runtime.RejectionCount, "count", null,
            runtime is null ? "Not collected." : $"{runtime.ErrorsBeforeStabilization} before stabilization; console output is not intercepted."));

        if (longTasksSupported && perf.LongTaskCount > t.LongTasks.Good)
        {
            var status = Classify(perf.LongTaskCount, t.LongTasks);
            findings.Add(Finding("runtime-long-tasks", PerformanceLayer.Runtime, status == PerformanceMetricStatus.Poor ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at,
                "Long main-thread tasks",
                $"{perf.LongTaskCount} long task(s) over 50 ms detected during this {PerformanceFormat.PhaseLabel(phase).ToLowerInvariant()} observation (total {Ms(perf.LongTaskTotalMs)}, longest {Ms(perf.LongestTaskMs)}; {perf.LongTasksAfterStabilization} after stabilization).",
                perf.LongTaskCount.ToString(), t.LongTasks,
                [$"Count: {perf.LongTaskCount}", $"Total: {Ms(perf.LongTaskTotalMs)}", $"Longest: {Ms(perf.LongestTaskMs)}", $"Phase: {PerformanceFormat.PhaseLabel(phase)}", $"Threshold: max {N(t.LongTasks.Good, "0")}"],
                "Investigate synchronous work on the main thread: split heavy JavaScript/.NET interop, defer non-critical startup work and avoid rendering large collections synchronously."));
        }
        if (longTasksSupported && perf.MainThreadBlockingMs is { } blocking && blocking > t.MainThreadBlocking.Good)
        {
            var status = Classify(blocking, t.MainThreadBlocking);
            findings.Add(Finding("runtime-main-thread-blocking", PerformanceLayer.Runtime, status == PerformanceMetricStatus.Poor ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at,
                "Main thread blocked", $"BirkNext main-thread blocking time of {Ms(blocking)} (Σ long task excess over 50 ms). Not Lighthouse TBT.", Ms(blocking), t.MainThreadBlocking,
                [$"Blocking: {Ms(blocking)}", $"Long tasks: {perf.LongTaskCount}", $"Threshold: {t.MainThreadBlocking.Display}"],
                "Reduce the duration of the longest tasks first; profile with the browser performance panel on this page."));
        }
        if (mutations is { LargeBatchesAfterStabilization: >= SustainedChurnBatches })
            findings.Add(Finding("runtime-dom-churn", PerformanceLayer.Runtime, FrontendQualitySeverity.Low, page, PerformancePhase.Runtime, at,
                "Sustained DOM churn after stabilization", $"{mutations.LargeBatchesAfterStabilization} large DOM mutation batches (≥ 50 records) were observed after the page had stabilized; largest batch {mutations.LargestBatch} records.",
                mutations.LargeBatchesAfterStabilization.ToString(), null, [$"Large batches after stabilization: {mutations.LargeBatchesAfterStabilization}", $"Largest batch: {mutations.LargestBatch}", $"Total mutation records: {mutations.MutationCount}"],
                "Check for components re-rendering on timers or on every state change; virtualize large lists.", confidence: PerformanceConfidence.Medium, thresholdText: $"≥ {SustainedChurnBatches} large batches (documented heuristic)"));
        if (runtime is { ErrorsBeforeStabilization: > 0 } && perf.StabilizationMs is { } stab && Classify(stab, t.Stabilization) != PerformanceMetricStatus.Good)
            findings.Add(Finding("runtime-errors-during-stabilization", PerformanceLayer.Runtime, FrontendQualitySeverity.Medium, page, phase, at,
                "Runtime errors correlated with slow stabilization",
                $"Page stabilization took {Ms(stab)} while {runtime.ErrorsBeforeStabilization} unhandled runtime exception(s)/rejection(s) occurred before the page stabilized. Timing correlation is not proof of causality.",
                runtime.ErrorsBeforeStabilization.ToString(), null, [$"Errors before stabilization: {runtime.ErrorsBeforeStabilization}", $"Stabilization: {Ms(stab)}", .. runtime.Errors.Take(3).Select(e => $"{e.Count}× {e.Kind}: {e.Message}")],
                "Fix the runtime exceptions first, then re-measure the page.", confidence: PerformanceConfidence.Medium, thresholdText: "correlation only"));
    }

    // ── Resources layer ────────────────────────────────────────────────────────

    private static void EvaluateResources(BrowserPerformanceSummary perf, PerformancePhase phase, PerformanceThresholdSet t, string page, DateTimeOffset at,
        List<PerformanceQualityMetric> metrics, List<PerformanceQualityFinding> findings, PageAnalysis analysis)
    {
        var initial = phase == PerformancePhase.InitialLoad;
        var spaNote = "Startup thresholds apply to the initial application load only; SPA navigation totals are informational.";
        metrics.Add(Metric("requests", "Requests", PerformanceLayer.Resources, phase, perf.ResourceCount, "count", initial ? t.StartupRequests : null, initial ? null : spaNote));
        metrics.Add(Metric("transfer", "Transferred", PerformanceLayer.Resources, phase, perf.TransferredBytes, "bytes", initial ? t.StartupTransfer : null,
            (initial ? "" : spaNote + " ") + $"{perf.CachedResourceCount} resource(s) served from the browser cache."));
        metrics.Add(Metric("js-bytes", "JavaScript", PerformanceLayer.Resources, phase, perf.JsBytes, "bytes", t.JsTransfer));
        metrics.Add(Metric("css-bytes", "CSS", PerformanceLayer.Resources, phase, perf.CssBytes, "bytes", null));
        metrics.Add(Metric("wasm-bytes", "WASM", PerformanceLayer.Resources, phase, perf.WasmBytes, "bytes", t.Wasm));
        metrics.Add(Metric("image-bytes", "Images", PerformanceLayer.Resources, phase, perf.ImageBytes, "bytes", null));
        metrics.Add(Metric("font-bytes", "Fonts", PerformanceLayer.Resources, phase, perf.FontBytes, "bytes", null));
        metrics.Add(Metric("largest-resource", "Largest resource", PerformanceLayer.Resources, phase, perf.LargestResource?.TransferBytes ?? perf.LongestResources.Max(r => r.TransferBytes), "bytes", t.IndividualAsset,
            perf.LargestResource is { } lr ? $"{lr.Kind}: {lr.Url}" : null));
        metrics.Add(Metric("slowest-resource", "Slowest resource", PerformanceLayer.Resources, phase, perf.SlowestResource?.DurationMs ?? perf.LongestResources.FirstOrDefault()?.DurationMs, "ms", t.SlowResource,
            perf.SlowestResource is { } sr ? $"{sr.Kind}: {sr.Url}" : perf.LongestResources.FirstOrDefault() is { } lo ? $"{lo.Kind}: {lo.Url}" : null));
        var networkDuplicates = perf.DuplicateResources.Where(d => (d.NetworkCount ?? d.Count) > 1).ToList();
        var cacheDuplicates = perf.DuplicateResources.Where(d => (d.NetworkCount ?? d.Count) <= 1).ToList();
        metrics.Add(Metric("duplicate-resources", "Duplicate fetches", PerformanceLayer.Resources, phase, perf.DuplicateFetchCount, "count", new PerformanceThreshold("Duplicate fetches", 0, null, "count", PerformanceThresholdSource.Default),
            perf.DuplicateFetchCount == 0 ? null : $"{networkDuplicates.Count} URL(s) transferred repeatedly over the network; {cacheDuplicates.Count} repeated from the browser cache."));
        metrics.Add(Metric("failed-resources", "Failed resources", PerformanceLayer.Resources, phase, perf.FailedResourceCount, "count", new PerformanceThreshold("Failed resources", 0, 0, "count", PerformanceThresholdSource.Default)));

        if (perf.JsBytes > t.JsTransfer.Good)
            findings.Add(Finding("res-large-js", PerformanceLayer.Resources, FrontendQualitySeverity.Medium, page, phase, at, "Large JavaScript payload",
                $"{Bytes(perf.JsBytes)} of JavaScript were loaded for this page observation.", Bytes(perf.JsBytes), t.JsTransfer,
                [$"JavaScript: {Bytes(perf.JsBytes)}", $"Threshold: {t.JsTransfer.Display}", .. TopOf(perf, "js")], "Split bundles, defer non-critical scripts and verify compression."));
        if (perf.WasmBytes > t.Wasm.Good)
            findings.Add(Finding("res-large-wasm", PerformanceLayer.Resources, FrontendQualitySeverity.Medium, page, phase, at, "Large WASM payload",
                $"{Bytes(perf.WasmBytes)} of WebAssembly were transferred for this page observation.", Bytes(perf.WasmBytes), t.Wasm,
                [$"WASM: {Bytes(perf.WasmBytes)}", $"Threshold: {t.Wasm.Display}"], "Review assemblies/trimming, lazy-load assemblies and verify Brotli compression of the _framework folder."));
        if (initial && perf.TransferredBytes > t.StartupTransfer.Good)
            findings.Add(Finding("res-large-initial-transfer", PerformanceLayer.Resources, FrontendQualitySeverity.Medium, page, phase, at, "Large total initial transfer",
                $"{Bytes(perf.TransferredBytes)} were transferred during the initial application load ({perf.ResourceCount} requests).", Bytes(perf.TransferredBytes), t.StartupTransfer,
                [$"Transferred: {Bytes(perf.TransferredBytes)}", $"JS: {Bytes(perf.JsBytes)}", $"WASM: {Bytes(perf.WasmBytes)}", $"Images: {Bytes(perf.ImageBytes)}", $"Threshold: {t.StartupTransfer.Display}"],
                "Enable compression and long-lived caching for immutable assets; reduce framework/JS payload."));
        if (initial && perf.ResourceCount > t.StartupRequests.Good)
            findings.Add(Finding("res-many-initial-requests", PerformanceLayer.Resources, FrontendQualitySeverity.Low, page, phase, at, "High number of initial requests",
                $"{perf.ResourceCount} resources were requested during the initial application load.", perf.ResourceCount.ToString(), t.StartupRequests,
                [$"Requests: {perf.ResourceCount}", $"Threshold: {t.StartupRequests.Display}"], "Bundle or lazy-load assets and trim framework/CDN requests."));
        foreach (var big in AllResources(perf).Where(r => r.TransferBytes is { } b && b > t.IndividualAsset.Good).DistinctBy(r => r.Url).Take(5))
        {
            var isImage = big.Kind == "image";
            findings.Add(Finding(isImage ? "res-large-image" : "res-large-resource", PerformanceLayer.Resources, FrontendQualitySeverity.Medium, page, phase, at,
                isImage ? "Large image" : $"Large {big.Kind} resource", $"{big.Url} transferred {Bytes(big.TransferBytes ?? 0)}.", Bytes(big.TransferBytes ?? 0), t.IndividualAsset,
                [$"URL: {big.Url}", $"Kind: {big.Kind}", $"Transfer: {Bytes(big.TransferBytes ?? 0)}", $"Threshold: {t.IndividualAsset.Display}"],
                isImage ? "Resize/compress the image, serve modern formats (WebP/AVIF) and lazy-load below-the-fold images." : "Split, compress or lazy-load the resource."));
        }
        foreach (var slow in AllResources(perf).Where(r => r.Kind != "api" && r.DurationMs is { } d && d > t.SlowResource.Good).DistinctBy(r => r.Url).Take(5))
            findings.Add(Finding("res-slow-resource", PerformanceLayer.Resources, FrontendQualitySeverity.Low, page, phase, at, $"Slow {slow.Kind} resource",
                $"{slow.Url} took {Ms(slow.DurationMs)} to load as seen by the browser.", Ms(slow.DurationMs), t.SlowResource,
                [$"URL: {slow.Url}", $"Kind: {slow.Kind}", $"Duration: {Ms(slow.DurationMs)}", $"Threshold: {t.SlowResource.Display}"], "Check server timing, compression and CDN placement for this resource."));
        if (networkDuplicates.Count > 0)
            findings.Add(Finding("res-duplicate-network", PerformanceLayer.Resources, FrontendQualitySeverity.Medium, page, phase, at, "Resources transferred repeatedly",
                $"{networkDuplicates.Count} resource URL(s) were transferred over the network more than once during this page observation (not served from the browser cache).",
                networkDuplicates.Sum(d => d.NetworkCount ?? d.Count).ToString(), null, networkDuplicates.Take(8).Select(d => $"{d.NetworkCount ?? d.Count}× network ({d.Count} fetches) {d.Url}").ToList(),
                "Add cache headers (Cache-Control max-age/immutable, ETag) or de-duplicate the fetch in the component.", confidence: PerformanceConfidence.Medium, thresholdText: "1 network transfer per URL"));
        if (perf.FailedResourceCount > 0)
            findings.Add(Finding("res-failed", PerformanceLayer.Resources, FrontendQualitySeverity.High, page, phase, at, "Failed resource loads",
                $"{perf.FailedResourceCount} resource(s) answered with an HTTP error status while loading this page.", perf.FailedResourceCount.ToString(), null,
                perf.FailedResources.Take(10).Select(r => $"{r.Status} {r.Kind} {r.Url}").ToList(), "Fix or remove the failing references; failed framework resources break Blazor startup.", thresholdText: "0"));

        // Cache quality from proxy response metadata for this page's static assets (Endpoint Discovery).
        foreach (var asset in analysis.Endpoints.Where(e => e.Category == ObservedTrafficCategory.StaticAsset && e.Count > 1 && e.NotModifiedCount == 0 && e.Samples.Count > 0
                     && (e.CacheDirectives is null || e.CacheDirectives.Contains("no-store", StringComparison.Ordinal) || e.CacheDirectives.Contains("no-cache", StringComparison.Ordinal))).Take(5))
            findings.Add(Finding("cache-static-repeated-transfer", PerformanceLayer.Resources, FrontendQualitySeverity.Medium, page, phase, at, "Static asset repeatedly transferred without an effective cache policy",
                $"{asset.Method} {asset.Host}{asset.Path} was transferred {asset.Count}× through the proxy with Cache-Control {asset.CacheDirectives ?? "absent"} and no 304 revalidation.",
                $"{asset.Count} transfers", null, [$"Path: {asset.Path}", $"Cache-Control: {asset.CacheDirectives ?? "absent"}", $"ETag: {(asset.HasEtag ? "yes" : "no")}", $"Last-Modified: {(asset.HasLastModified ? "yes" : "no")}", $"Transfers: {asset.Count}"],
                "Serve static assets with Cache-Control max-age (plus immutable for fingerprinted files) and ETag/Last-Modified for revalidation.", PerformanceQualitySources.Proxy, thresholdText: "cacheable static asset"));
        foreach (var fw in analysis.Endpoints.Where(e => e.Category == ObservedTrafficCategory.StaticAsset && e.Path.Contains("/_framework/", StringComparison.OrdinalIgnoreCase) && e.Samples.Count > 0
                     && (e.CacheDirectives is null || !(e.CacheDirectives.Contains("max-age=", StringComparison.Ordinal) || e.CacheDirectives.Contains("immutable", StringComparison.Ordinal)))).Take(3))
            findings.Add(Finding("cache-framework-no-policy", PerformanceLayer.Resources, FrontendQualitySeverity.Low, page, phase, at, "No cache policy for a framework asset",
                $"{fw.Host}{fw.Path} is served with Cache-Control {fw.CacheDirectives ?? "absent"}; fingerprinted _framework assets are immutable and benefit from long max-age.",
                fw.CacheDirectives ?? "absent", null, [$"Path: {fw.Path}", $"Cache-Control: {fw.CacheDirectives ?? "absent"}", $"Observed transfers: {fw.Count}"],
                "Configure long-lived caching (max-age, immutable) for the _framework folder.", PerformanceQualitySources.Proxy, PerformanceConfidence.Medium, thresholdText: "max-age / immutable present"));
    }

    private static IEnumerable<BrowserResourceEntry> AllResources(BrowserPerformanceSummary perf) =>
        new[] { perf.LargestResource, perf.SlowestResource }.Where(r => r is not null).Cast<BrowserResourceEntry>()
            .Concat(perf.LongestResources).Concat(perf.Timeline).Concat(perf.Categories.SelectMany(c => new[] { c.Largest, c.Slowest }).Where(r => r is not null).Cast<BrowserResourceEntry>());

    private static IEnumerable<string> TopOf(BrowserPerformanceSummary perf, string kind)
    {
        var cat = perf.Categories.FirstOrDefault(c => c.Kind == kind);
        if (cat?.Largest is { } l) yield return $"Largest {kind}: {l.Url} ({Bytes(l.TransferBytes ?? 0)})";
    }

    // ── API / network layer ────────────────────────────────────────────────────

    public static ApiPerformanceSummary SummarizeApi(PageAnalysis page, PerformanceThresholdSet t)
    {
        var endpoints = page.Endpoints.Where(e => e.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl or ObservedTrafficCategory.WebSocket).ToList();
        if (endpoints.Count == 0) return new ApiPerformanceSummary();
        var operations = endpoints.Select(e => Operation(e, t)).OrderByDescending(o => o.Count).ThenByDescending(o => o.Statistics.Max ?? 0).ToList();
        var api = operations.Where(o => o.Kind != ApiOperationKind.WebSocket).ToList();
        var timing = api.Any(o => o.Statistics.SampleCount > 0);
        var events = api.SelectMany(o => endpoints.First(e => Identity(e) == o.Identity).Samples.Select(s => new Event(o, s))).OrderBy(e => e.End).ToList();
        var durations = events.Select(e => e.Sample.DurationMs).ToList();
        return new ApiPerformanceSummary
        {
            Available = true, TimingAvailable = timing,
            RestCalls = operations.Where(o => o.Kind == ApiOperationKind.Rest).Sum(o => o.Count),
            GraphQlCalls = operations.Where(o => o.Kind == ApiOperationKind.GraphQl).Sum(o => o.Count),
            WebSocketConnections = operations.Where(o => o.Kind == ApiOperationKind.WebSocket).Sum(o => o.Count),
            SlowCalls = durations.Count(d => t.ApiResponse.Good is { } warn && d > warn),
            ErrorResponses = api.Sum(o => o.ErrorCount), AuthRejected = api.Sum(o => o.AuthRejectedCount),
            Operations = operations, DuplicateOperations = api.Where(o => o.IsDuplicate).ToList(),
            SlowestOperation = api.Where(o => o.Statistics.Representative is not null).OrderByDescending(o => o.Statistics.Representative).FirstOrDefault(),
            Bursts = Bursts(events), SequentialPatterns = Sequential(events), Overall = PerformanceLatencyStatistics.Compute(durations),
        };
    }

    private sealed record Event(ApiOperationSummary Operation, ObservedRequestSample Sample)
    {
        public DateTimeOffset End => Sample.At;
        public DateTimeOffset Start => Sample.At.AddMilliseconds(-Sample.DurationMs);
    }

    private static string Identity(ObservedNetworkEndpoint e) => e.Category == ObservedTrafficCategory.GraphQl
        ? $"gql|{e.Host}{e.Path}|{e.OperationType}|{e.OperationName}" : $"{Kind(e)}|{e.Method}|{e.Host}{e.Path}";

    private static ApiOperationKind Kind(ObservedNetworkEndpoint e) => e.Category switch
    {
        ObservedTrafficCategory.GraphQl => ApiOperationKind.GraphQl, ObservedTrafficCategory.WebSocket => ApiOperationKind.WebSocket, _ => ApiOperationKind.Rest,
    };

    private static ApiOperationSummary Operation(ObservedNetworkEndpoint e, PerformanceThresholdSet t)
    {
        var kind = Kind(e);
        var stats = PerformanceLatencyStatistics.Compute(e.Samples.Select(s => s.DurationMs).ToList());
        return new ApiOperationSummary
        {
            Kind = kind, Method = e.Method, Host = e.Host, Path = e.Path, OperationType = e.OperationType, OperationName = e.OperationName, Count = e.Count,
            Statistics = stats, LastStatus = e.LastStatus, ErrorCount = e.ErrorCount, AuthRejectedCount = e.AuthRejectedCount, NotModifiedCount = e.NotModifiedCount,
            FirstObservedAt = e.FirstObservedAt, LastObservedAt = e.LastObservedAt, CacheDirectives = e.CacheDirectives,
            LatencyStatus = kind == ApiOperationKind.WebSocket ? PerformanceMetricStatus.Informational : Classify(stats.Representative, t.ApiResponse),
            IsDuplicate = kind != ApiOperationKind.WebSocket && e.Count > t.IdenticalApiCalls.Good,
            IsPollingLike = IsPollingLike(e.Samples),
        };
    }

    /// <summary>Regular cadence heuristic: ≥ 4 samples spanning ≥ 10 s whose intervals vary by less than 30 %. Never suppresses evidence; lowers confidence.</summary>
    public static bool IsPollingLike(IReadOnlyList<ObservedRequestSample> samples)
    {
        if (samples.Count < PollingMinSamples) return false;
        var times = samples.Select(s => s.At).OrderBy(x => x).ToList();
        if ((times[^1] - times[0]).TotalMilliseconds < PollingMinSpanMs) return false;
        var intervals = times.Zip(times.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToList();
        var mean = intervals.Average();
        if (mean <= 0) return false;
        var sd = Math.Sqrt(intervals.Sum(i => (i - mean) * (i - mean)) / intervals.Count);
        return sd / mean < PollingCadenceTolerance;
    }

    private static List<ApiBurst> Bursts(List<Event> events)
    {
        var bursts = new List<ApiBurst>();
        var i = 0;
        while (i < events.Count)
        {
            var windowEnd = events[i].End.AddMilliseconds(BurstWindowMs);
            var j = i;
            while (j + 1 < events.Count && events[j + 1].End <= windowEnd) j++;
            var count = j - i + 1;
            if (count >= BurstMinRequests)
            {
                var slice = events.Skip(i).Take(count).ToList();
                bursts.Add(new ApiBurst(slice[0].Start < slice[0].End ? slice[0].Start : slice[0].End, BurstWindowMs, count,
                    slice.Count(e => e.Operation.Kind == ApiOperationKind.GraphQl), slice.Count(e => e.Operation.Kind == ApiOperationKind.Rest)));
                i = j + 1;
            }
            else i++;
        }
        return bursts;
    }

    private static List<ApiSequentialPattern> Sequential(List<Event> events)
    {
        var ordered = events.OrderBy(e => e.Start).ToList();
        var patterns = new List<ApiSequentialPattern>();
        var chain = new List<Event>();
        void Flush()
        {
            if (chain.Count >= SequentialMinLength && patterns.Count < MaxPatterns)
                patterns.Add(new ApiSequentialPattern(chain.Select(c => c.Operation.Display).ToList(), (chain[^1].End - chain[0].Start).TotalMilliseconds,
                    chain.Zip(chain.Skip(1), (a, b) => (b.Start - a.End).TotalMilliseconds).DefaultIfEmpty(0).Max()));
            chain.Clear();
        }
        foreach (var e in ordered)
        {
            if (chain.Count > 0)
            {
                var gap = (e.Start - chain[^1].End).TotalMilliseconds;
                if (gap < -5 || gap > SequentialGapMs) Flush();
            }
            chain.Add(e);
        }
        Flush();
        return patterns;
    }

    private static void EvaluateApi(ApiPerformanceSummary api, PerformancePhase phase, PerformanceThresholdSet t, string page, DateTimeOffset at,
        List<PerformanceQualityMetric> metrics, List<PerformanceQualityFinding> findings, BrowserPerformanceSummary? perf)
    {
        const string src = PerformanceQualitySources.Proxy;
        if (!api.Available)
        {
            const string none = "No Local HTTPS proxy traffic recorded for this page (proxy not running or not configured in the browser).";
            foreach (var (id, name) in new[] { ("rest-calls", "REST calls"), ("graphql-calls", "GraphQL calls"), ("slow-calls", "Slow API calls"), ("duplicate-calls", "Duplicate API calls"), ("slowest-api", "Slowest API") })
                metrics.Add(Metric(id, name, PerformanceLayer.Api, phase, null, id == "slowest-api" ? "ms" : "count", null, none, src));
            return;
        }
        var timingNote = api.TimingAvailable ? null : "Recorded traffic carries no timing samples; counts only.";
        metrics.Add(Metric("rest-calls", "REST calls", PerformanceLayer.Api, phase, api.RestCalls, "count", null, null, src));
        metrics.Add(Metric("graphql-calls", "GraphQL calls", PerformanceLayer.Api, phase, api.GraphQlCalls, "count", null, null, src));
        metrics.Add(Metric("websocket-connections", "WebSocket connections", PerformanceLayer.Api, phase, api.WebSocketConnections, "count", null, null, src));
        metrics.Add(Metric("slow-calls", "Slow API calls", PerformanceLayer.Api, phase, api.TimingAvailable ? api.SlowCalls : null, "count", new PerformanceThreshold("Slow API calls", 0, null, "count", t.ApiResponse.Source),
            timingNote ?? $"Calls above the API response warning threshold ({t.ApiResponse.Display}).", src));
        metrics.Add(Metric("duplicate-calls", "Duplicate API operations", PerformanceLayer.Api, phase, api.DuplicateOperations.Count, "count", new PerformanceThreshold("Duplicate operations", 0, null, "count", t.IdenticalApiCalls.Source),
            api.DuplicateOperations.Count == 0 ? null : string.Join(", ", api.DuplicateOperations.Take(4).Select(d => $"{d.Display} × {d.Count}")), src));
        var slowest = api.SlowestOperation;
        metrics.Add(Metric("slowest-api", "Slowest API", PerformanceLayer.Api, phase, slowest?.Statistics.Representative, "ms", t.ApiResponse,
            slowest is null ? timingNote ?? "No timed API calls." : $"{slowest.Display} ({slowest.Statistics.RepresentativeLabel}, {slowest.Count} call(s))", src));
        metrics.Add(Metric("api-p50", "API latency p50", PerformanceLayer.Api, phase, api.Overall.Sufficient ? api.Overall.P50 : null, "ms", null,
            api.Overall.Sufficient ? $"{api.Overall.SampleCount} samples" : $"Fewer than {PerformanceLatencyStatistics.MinimumSamplesForPercentiles} timed calls ({api.Overall.SampleCount}); percentiles not statistically meaningful.", src));
        metrics.Add(Metric("api-p95", "API latency p95", PerformanceLayer.Api, phase, api.Overall.Sufficient ? api.Overall.P95 : null, "ms", null,
            api.Overall.Sufficient ? $"{api.Overall.SampleCount} samples" : $"Fewer than {PerformanceLatencyStatistics.MinimumSamplesForPercentiles} timed calls ({api.Overall.SampleCount}).", src));
        metrics.Add(Metric("api-errors", "API error responses", PerformanceLayer.Api, phase, api.ErrorResponses, "count", new PerformanceThreshold("API errors", 0, 0, "count", PerformanceThresholdSource.Default),
            api.AuthRejected > 0 ? $"{api.AuthRejected} authentication rejection(s) (401/403) counted separately." : null, src));

        var correlated = perf is not null ? PerformanceQualitySources.Correlated : src;
        foreach (var op in api.Operations.Where(o => o.LatencyStatus is PerformanceMetricStatus.Poor or PerformanceMetricStatus.NeedsImprovement).Take(8))
            findings.Add(Finding("api-slow", PerformanceLayer.Api, op.LatencyStatus == PerformanceMetricStatus.Poor ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at,
                $"Slow {(op.Kind == ApiOperationKind.GraphQl ? "GraphQL operation" : "REST call")}: {op.Display}",
                $"{op.Display} took {Ms(op.Statistics.Representative)} ({op.Statistics.RepresentativeLabel}{(op.Statistics.Sufficient ? $", p95 {Ms(op.Statistics.P95)}, max {Ms(op.Statistics.Max)}" : "")}) over {op.Count} call(s) as observed by the Local HTTPS proxy.",
                Ms(op.Statistics.Representative), t.ApiResponse,
                [$"Operation: {op.Display}", $"Observed ({op.Statistics.RepresentativeLabel}): {Ms(op.Statistics.Representative)}", .. (op.Statistics.Sufficient ? [$"p95: {Ms(op.Statistics.P95)}", $"min/max: {Ms(op.Statistics.Min)} / {Ms(op.Statistics.Max)}"] : Array.Empty<string>()),
                 $"Calls: {op.Count}", $"Threshold: {t.ApiResponse.Display}", $"Last status: {op.LastStatus}"],
                "Profile the endpoint server-side (query plan, N+1, serialization) and consider caching or paging.", src));
        foreach (var dup in api.DuplicateOperations.Take(8))
        {
            var window = (dup.LastObservedAt - dup.FirstObservedAt).TotalSeconds;
            var isGql = dup.Kind == ApiOperationKind.GraphQl;
            findings.Add(Finding(isGql ? "api-duplicate-graphql" : "api-duplicate-rest", PerformanceLayer.Api, dup.Count > t.IdenticalApiCalls.Good * 3 ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at,
                $"Repeated {(isGql ? "GraphQL operation" : "REST call")}: {dup.Display}" + (dup.IsPollingLike ? " (polling-like cadence)" : ""),
                $"{dup.Display} was called {dup.Count}× within one page generation ({N(window, "0")} s window, statuses {StatusSummary(dup)}{(dup.Statistics.Total > 0 ? $", total latency {Ms(dup.Statistics.Total)}" : "")}), correlated with this page by the request Referer. " +
                (dup.IsPollingLike ? "The regular cadence looks like polling; no configured polling classification exists, so this is a heuristic and the finding is kept at low confidence." : "Repeated identical fetches are usually avoidable."),
                $"{dup.Count} calls", t.IdenticalApiCalls,
                [$"Operation: {dup.Display}", $"Calls: {dup.Count}", $"Window: {N(window, "0")} s", $"Statuses: {StatusSummary(dup)}", $"Total latency: {(dup.Statistics.Total > 0 ? Ms(dup.Statistics.Total) : "n/a")}", $"Threshold: max {N(t.IdenticalApiCalls.Good, "0")}"],
                isGql ? "Cache or de-duplicate the query client-side (shared store, request coalescing) and check components that re-query on every render." : "De-duplicate the call client-side (shared state, request coalescing) or cache the response.",
                correlated, dup.IsPollingLike ? PerformanceConfidence.Low : PerformanceConfidence.Medium));
        }
        foreach (var burst in api.Bursts.Take(3))
            findings.Add(Finding("api-burst", PerformanceLayer.Api, FrontendQualitySeverity.Low, page, phase, at, "Network burst",
                $"{burst.RequestCount} API requests ({burst.GraphQlCount} GraphQL, {burst.RestCount} REST) completed within {N(burst.WindowMs / 1000, "0.#")} s after the page was in use.",
                $"{burst.RequestCount} requests", null, [$"Requests: {burst.RequestCount}", $"GraphQL: {burst.GraphQlCount}", $"REST: {burst.RestCount}", $"Window: {N(burst.WindowMs, "0")} ms", $"Started: {burst.StartedAt:HH:mm:ss.fff}"],
                "Batch requests (GraphQL batching/aggregate queries) or defer non-critical data loads.", src, PerformanceConfidence.Medium, thresholdText: $"≥ {BurstMinRequests} requests in {N(BurstWindowMs, "0")} ms (documented default)"));
        foreach (var pattern in api.SequentialPatterns.Take(3))
            findings.Add(Finding("api-sequential", PerformanceLayer.Api, FrontendQualitySeverity.Low, page, phase, at, "Observed sequential request pattern",
                $"Observed sequential request pattern: {string.Join(" → ", pattern.Operations)} ({Ms(pattern.TotalMs)} end to end, each starting within {N(SequentialGapMs, "0")} ms of the previous completion). This is an observation of timing, not proof of a dependency.",
                $"{pattern.Operations.Count} requests in sequence", null, [$"Sequence: {string.Join(" → ", pattern.Operations)}", $"Total: {Ms(pattern.TotalMs)}", $"Max gap: {Ms(pattern.MaxGapMs)}"],
                "If these requests are independent, issue them in parallel; if dependent, consider a combined endpoint/query.", src, PerformanceConfidence.Low, thresholdText: "observation"));
        var serverErrors = api.Operations.Where(o => o.ErrorCount - o.AuthRejectedCount > 0).ToList();
        if (serverErrors.Count > 0)
            findings.Add(Finding("api-errors", PerformanceLayer.Api, serverErrors.Any(o => o.LastStatus >= 500) ? FrontendQualitySeverity.High : FrontendQualitySeverity.Medium, page, phase, at, "API error responses",
                $"{serverErrors.Sum(o => o.ErrorCount - o.AuthRejectedCount)} response(s) with HTTP 4xx/5xx (authentication rejections excluded) on {serverErrors.Count} endpoint(s).",
                serverErrors.Sum(o => o.ErrorCount - o.AuthRejectedCount).ToString(), null, serverErrors.Take(8).Select(o => $"{o.Display}: {o.ErrorCount - o.AuthRejectedCount} error(s), last status {o.LastStatus}").ToList(),
                "Check server logs for the failing endpoints; failed data loads leave pages incomplete.", src, thresholdText: "0"));
        var authRejected = api.Operations.Where(o => o.AuthRejectedCount > 0).ToList();
        if (authRejected.Count > 0)
            findings.Add(Finding("api-auth-rejected", PerformanceLayer.Api, authRejected.Sum(o => o.AuthRejectedCount) > 2 ? FrontendQualitySeverity.Medium : FrontendQualitySeverity.Low, page, phase, at, "Repeated authentication rejections (401/403)",
                $"{authRejected.Sum(o => o.AuthRejectedCount)} authenticated request(s) were rejected with 401/403 on {authRejected.Count} endpoint(s); kept distinct from other errors.",
                authRejected.Sum(o => o.AuthRejectedCount).ToString(), null, authRejected.Take(8).Select(o => $"{o.Display}: {o.AuthRejectedCount}× 401/403").ToList(),
                "Verify token acquisition/refresh and the API's expected audience; repeated rejections often indicate a retry loop.", src, thresholdText: "0"));
    }

    private static string StatusSummary(ApiOperationSummary op) => op.Statistics.SampleCount == 0 ? op.LastStatus.ToString()
        : op.ErrorCount == 0 ? $"all 2xx/3xx (last {op.LastStatus})" : $"{op.ErrorCount} error(s), last {op.LastStatus}";

    // ── Blazor layer ───────────────────────────────────────────────────────────

    private static void EvaluateBlazor(BrowserBlazorSummary? blazor, BrowserPerformanceSummary perf, PerformancePhase phase, PerformanceThresholdSet t, string page, DateTimeOffset at,
        List<PerformanceQualityMetric> metrics, List<PerformanceQualityFinding> findings)
    {
        if (blazor is null || !blazor.Detected)
        {
            metrics.Add(Metric("blazor-detected", "Blazor WASM", PerformanceLayer.Blazor, phase, null, "count", null, "No Blazor framework resources observed on this page (non-Blazor page, or framework fully cached without Resource Timing entries).", text: "Not detected"));
            return;
        }
        var initial = phase == PerformancePhase.InitialLoad;
        metrics.Add(Metric("blazor-framework-bytes", "Framework payload", PerformanceLayer.Blazor, phase, blazor.FrameworkBytes, "bytes", t.Framework, $"{blazor.FrameworkResourceCount} framework resource(s); {blazor.CachedFrameworkResourceCount} from cache."));
        metrics.Add(Metric("blazor-wasm-bytes", "WASM", PerformanceLayer.Blazor, phase, blazor.WasmBytes, "bytes", t.Wasm));
        metrics.Add(Metric("blazor-assemblies", "Assemblies", PerformanceLayer.Blazor, phase, blazor.AssemblyCount, "count", t.Assemblies, $"{Bytes(blazor.AssemblyBytes)}; runtime {blazor.RuntimeResourceCount} resource(s) {Bytes(blazor.RuntimeBytes)}; culture/timezone {blazor.CultureResourceCount} ({Bytes(blazor.CultureBytes)})."));
        metrics.Add(Metric("blazor-startup", "Framework load window", PerformanceLayer.Blazor, phase, initial ? blazor.FrameworkLoadEndMs : null, "ms", null,
            initial ? $"First framework request at {Ms(blazor.FrameworkLoadStartMs)}, last framework response at {Ms(blazor.FrameworkLoadEndMs)} after navigation start. Bootstrap-to-first-render is not directly observable; use Page stabilization." : "Startup applies to the initial application load; this is an SPA navigation."));
        metrics.Add(Metric("blazor-boot-manifest", "Boot manifest", PerformanceLayer.Blazor, phase, blazor.BootManifestMs, "ms", null,
            blazor.BootManifestFailed ? "blazor.boot.json failed." : blazor.BootManifestObserved ? "blazor.boot.json loaded." : "Boot manifest not observed in this visit (cached or SPA navigation)."));
        metrics.Add(Metric("blazor-load-kind", "Load kind", PerformanceLayer.Blazor, phase, null, "count", null,
            blazor.LoadKind switch { "cold" => "Framework transferred over the network (cold/initial load).", "warm" => "Framework served from the browser cache (warm/revisit).", "mixed" => "Framework partly cached, partly transferred.", _ => "No framework transfer observed." },
            text: blazor.LoadKind switch { "cold" => "Cold", "warm" => "Warm", "mixed" => "Mixed", "none" => "None", _ => "Unknown" }));

        if (blazor.BootManifestFailed)
            findings.Add(Finding("blazor-boot-failed", PerformanceLayer.Blazor, FrontendQualitySeverity.Critical, page, phase, at, "Blazor boot manifest failed to load",
                "blazor.boot.json answered with an error status; the application cannot start.", "failed", null, blazor.FrameworkFailures.Select(f => $"{f.Status} {f.Url}").ToList(), "Check the deployment of the _framework folder.", thresholdText: "loads successfully"));
        else if (blazor.FrameworkFailures.Count > 0)
            findings.Add(Finding("blazor-framework-failed", PerformanceLayer.Blazor, FrontendQualitySeverity.High, page, phase, at, "Blazor framework resources failed",
                $"{blazor.FrameworkFailures.Count} framework resource(s) answered with an error status.", blazor.FrameworkFailures.Count.ToString(), null,
                blazor.FrameworkFailures.Select(f => $"{f.Status} {f.Url}").ToList(), "Check integrity/deployment of the _framework assets.", thresholdText: "0"));
        if (blazor.FrameworkBytes > t.Framework.Good && blazor.LoadKind != "warm")
            findings.Add(Finding("blazor-large-framework", PerformanceLayer.Blazor, FrontendQualitySeverity.Medium, page, phase, at, "Large initial framework payload",
                $"{Bytes(blazor.FrameworkBytes)} of _framework resources ({blazor.FrameworkResourceCount} files; {blazor.AssemblyCount} assemblies, WASM {Bytes(blazor.WasmBytes)}) were loaded in a {blazor.LoadKind} load.",
                Bytes(blazor.FrameworkBytes), t.Framework, [$"Framework: {Bytes(blazor.FrameworkBytes)}", $"WASM: {Bytes(blazor.WasmBytes)}", $"Assemblies: {blazor.AssemblyCount} ({Bytes(blazor.AssemblyBytes)})", $"Culture data: {Bytes(blazor.CultureBytes)}", $"Load kind: {blazor.LoadKind}", $"Threshold: {t.Framework.Display}"],
                "Review trimming/AOT settings, lazy-load assemblies, use invariant globalization where acceptable and verify Brotli compression."));
        if (blazor.AssemblyCount > t.Assemblies.Good)
            findings.Add(Finding("blazor-many-assemblies", PerformanceLayer.Blazor, FrontendQualitySeverity.Low, page, phase, at, "Excessive assembly count",
                $"{blazor.AssemblyCount} managed assemblies were loaded for this page observation.", blazor.AssemblyCount.ToString(), t.Assemblies,
                [$"Assemblies: {blazor.AssemblyCount}", $"Assembly bytes: {Bytes(blazor.AssemblyBytes)}", $"Threshold: {t.Assemblies.Display}"], "Trim unused assemblies and lazy-load feature assemblies."));
        if (blazor.SlowestFrameworkResource is { DurationMs: { } d } slow && d > t.SlowResource.Good)
            findings.Add(Finding("blazor-slow-boot-resource", PerformanceLayer.Blazor, FrontendQualitySeverity.Low, page, phase, at, "Slow framework resource",
                $"{slow.Url} took {Ms(d)} to download.", Ms(d), t.SlowResource, [$"URL: {slow.Url}", $"Duration: {Ms(d)}", $"Threshold: {t.SlowResource.Display}"], "Check compression, CDN placement and HTTP/2 for the _framework folder."));
        if (blazor.RepeatedFrameworkDownloads > 0)
            findings.Add(Finding("blazor-repeated-framework", PerformanceLayer.Blazor, FrontendQualitySeverity.Low, page, phase, at, "Framework resources downloaded repeatedly",
                $"{blazor.RepeatedFrameworkDownloads} framework resource URL(s) were fetched more than once during this visit.", blazor.RepeatedFrameworkDownloads.ToString(), null,
                [$"Repeated: {blazor.RepeatedFrameworkDownloads}"], "Check caching headers on the _framework folder.", thresholdText: "0"));
        if (phase == PerformancePhase.SpaNavigation && blazor.LoadKind is "cold" or "mixed" && blazor.FrameworkResourceCount - blazor.CachedFrameworkResourceCount > 0)
            findings.Add(Finding("blazor-framework-after-spa-navigation", PerformanceLayer.Blazor, FrontendQualitySeverity.Medium, page, phase, at, "Framework resources downloaded after SPA navigation",
                $"{blazor.FrameworkResourceCount - blazor.CachedFrameworkResourceCount} framework resource(s) ({Bytes(blazor.FrameworkBytes)}) were transferred over the network during a client-side route change. Lazy-loaded assemblies are expected; repeated core framework downloads are not.",
                $"{blazor.FrameworkResourceCount - blazor.CachedFrameworkResourceCount} transfers", null, [$"Framework transfers: {blazor.FrameworkResourceCount - blazor.CachedFrameworkResourceCount}", $"Bytes: {Bytes(blazor.FrameworkBytes)}", $"Load kind: {blazor.LoadKind}"],
                "Verify the assets are lazy-loaded assemblies; otherwise fix cache headers so the framework is not re-downloaded per route.", confidence: PerformanceConfidence.Medium, thresholdText: "lazy-loaded assemblies only"));
        if (initial && blazor.FrameworkLoadEndMs is { } end && t.Stabilization.Poor is { } poor && end > poor)
            findings.Add(Finding("blazor-long-bootstrap", PerformanceLayer.Blazor, FrontendQualitySeverity.Medium, page, phase, at, "Unusually long framework bootstrap window",
                $"The last framework resource finished {Ms(end)} after navigation start (page stabilization {Ms(perf.StabilizationMs)}). Bootstrap-to-first-render itself is not directly observable.",
                Ms(end), t.Stabilization, [$"Framework load end: {Ms(end)}", $"Framework: {Bytes(blazor.FrameworkBytes)}", $"Load kind: {blazor.LoadKind}", $"Threshold (stabilization poor): {t.Stabilization.Display}"],
                "Reduce framework payload, enable compression and caching, and check server latency for the _framework folder.", confidence: PerformanceConfidence.Low));
    }

    // ── Timeline / comparison / overview ───────────────────────────────────────

    public static List<PerformanceTimelineEntry> Timeline(PageAnalysis page)
    {
        var entries = new List<PerformanceTimelineEntry>();
        var ev = page.BrowserEvidence;
        if (ev?.Performance is { } perf)
            entries.AddRange(perf.Timeline.Where(r => r.StartMs is not null).Select(r => new PerformanceTimelineEntry(r.StartMs!.Value, r.DurationMs, r.Kind, r.Url, PerformanceQualitySources.Companion, r.TransferBytes, r.Status, r.Delivery)));
        var samples = page.Endpoints.Where(e => e.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl)
            .SelectMany(e => e.Samples.Select(s => (Endpoint: e, Sample: s))).ToList();
        if (samples.Count > 0)
        {
            var origin = ev?.VisitStartedAt ?? samples.Min(s => s.Sample.At.AddMilliseconds(-s.Sample.DurationMs));
            entries.AddRange(samples.Select(s => new PerformanceTimelineEntry(
                Math.Max(0, (s.Sample.At.AddMilliseconds(-s.Sample.DurationMs) - origin).TotalMilliseconds), s.Sample.DurationMs,
                s.Endpoint.Category == ObservedTrafficCategory.GraphQl ? "graphql" : "rest",
                s.Endpoint.Category == ObservedTrafficCategory.GraphQl && s.Endpoint.OperationName is { Length: > 0 } op ? $"{s.Endpoint.OperationType} {op}" : $"{s.Endpoint.Method} {s.Endpoint.Host}{s.Endpoint.Path}",
                PerformanceQualitySources.Proxy, s.Sample.ResponseBytes, s.Sample.Status, null)));
        }
        return entries.OrderBy(e => e.StartMs).Take(MaxTimelineEntries).ToList();
    }

    public static PerformanceComparison? Compare(PageAnalysis page)
    {
        var current = page.PerformanceHistory.FirstOrDefault(h => h.Generation == page.AnalysisGeneration);
        var previous = page.PerformanceHistory.Where(h => h.Generation < page.AnalysisGeneration).OrderByDescending(h => h.Generation).FirstOrDefault();
        if (current is null || previous is null) return null;
        var deltas = new List<PerformanceDelta>();
        void Add(string name, double? cur, double? prev, string unit)
        {
            if (cur is null && prev is null) return;
            var change = cur is null || prev is null ? "n/a"
                : unit == "count" ? $"{(cur - prev >= 0 ? "+" : "")}{N(cur - prev, "0")}"
                : prev == 0 ? (cur == 0 ? "±0%" : "new") : $"{(cur - prev >= 0 ? "+" : "")}{N((cur - prev) / prev * 100, "0")}%";
            deltas.Add(new PerformanceDelta(name, cur is null ? "Not measured" : PerformanceFormat.Value(cur, unit), prev is null ? "Not measured" : PerformanceFormat.Value(prev, unit), change,
                cur is null || prev is null ? null : cur > prev));
        }
        Add("LCP", current.LcpMs, previous.LcpMs, "ms");
        Add("CLS", current.Cls, previous.Cls, "score");
        Add("Page stabilization", current.StabilizationMs, previous.StabilizationMs, "ms");
        Add("Transferred", current.TransferredBytes, previous.TransferredBytes, "bytes");
        Add("Long tasks", current.LongTaskCount, previous.LongTaskCount, "count");
        Add("Requests", current.ResourceCount, previous.ResourceCount, "count");
        Add("API calls", current.ApiCalls, previous.ApiCalls, "count");
        Add("GraphQL calls", current.GraphQlCalls, previous.GraphQlCalls, "count");
        Add("WASM", current.WasmBytes, previous.WasmBytes, "bytes");
        return new PerformanceComparison(current.Generation, previous.Generation, previous.CapturedAt, current.ObservationType == previous.ObservationType, deltas);
    }

    public static List<PerformanceOverviewRow> Overview(IEnumerable<PagePerformanceSnapshot> pages) => pages.Select(p => new PerformanceOverviewRow(
            p.PageId, p.PageTitle, p.ObservationType, p.Metric("lcp"), p.Metric("stabilization"), p.Metric("transfer"), ApiCallsMetric(p), p.Metric("slow-calls"), p.Metric("long-tasks"),
            p.WorstStatus, p.WorstMetric?.Name, p.Coverage.Overall))
        .OrderBy(r => PerformanceFormat.Rank(r.Status)).ThenBy(r => r.Title).ToList();

    private static PerformanceQualityMetric? ApiCallsMetric(PagePerformanceSnapshot p)
    {
        var rest = p.Metric("rest-calls"); var gql = p.Metric("graphql-calls");
        if (rest is null && gql is null) return null;
        if (rest?.Value is null && gql?.Value is null) return rest ?? gql;
        return new PerformanceQualityMetric { Id = "api-calls", Name = "API calls", Layer = PerformanceLayer.Api, Phase = p.ObservationType, Value = (rest?.Value ?? 0) + (gql?.Value ?? 0), Unit = "count", Status = PerformanceMetricStatus.Informational, Source = PerformanceQualitySources.Proxy };
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static PerformanceQualityMetric Metric(string id, string name, PerformanceLayer layer, PerformancePhase phase, double? value, string unit, PerformanceThreshold? threshold, string? note = null,
        string source = PerformanceQualitySources.Companion, PerformanceConfidence confidence = PerformanceConfidence.High, string? text = null) => new()
    {
        Id = id, Name = name, Layer = layer, Phase = phase, Value = value, Unit = unit, Threshold = threshold, Note = note, Source = source, Confidence = confidence, Text = text,
        Status = text is not null ? PerformanceMetricStatus.Informational : Classify(value, threshold),
    };

    private static PerformanceQualityFinding Finding(string ruleId, PerformanceLayer category, FrontendQualitySeverity severity, string page, PerformancePhase phase, DateTimeOffset at,
        string title, string explanation, string observed, PerformanceThreshold? threshold, List<string> evidence, string recommendation,
        string source = PerformanceQualitySources.Companion, PerformanceConfidence confidence = PerformanceConfidence.High, string? thresholdText = null) => new()
    {
        RuleId = ruleId, Category = category, Severity = severity, Page = page, Phase = phase, ObservedAt = at, Title = title, Explanation = explanation, ObservedValue = observed,
        Threshold = thresholdText ?? threshold?.Display ?? "—", ThresholdSource = threshold?.Source, Evidence = evidence, EvidenceSource = source, Recommendation = recommendation, Confidence = confidence,
    };

    private static string Ms(double? v) => PerformanceFormat.Value(v, "ms");
    private static string N(double? v, string format) => v is null ? "—" : PerformanceFormat.Inv(v.Value, format);
    private static string Bytes(long v) => PerformanceFormat.Bytes(v);
}
