using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// BirkNext Browser Quality — deterministic rules over safe page evidence. Pure (no I/O), so every rule is unit-testable with synthetic
/// evidence. Thresholds come from the Target Environment's saved performance / Core Web Vitals settings; the few rule-level constants that
/// have no configured counterpart are named here with their source. Severity is fixed per rule (no inflation). Correlation with proxy
/// evidence is always worded "correlated with", never "caused by".
/// </summary>
public static class BrowserQualityRules
{
    public const string CompanionSource = "Browser Companion";
    public const string CorrelatedSource = "Browser Companion + Local HTTPS Proxy";
    public const string ProxySource = "Local HTTPS Proxy";

    /// <summary>Lighthouse "avoid an excessive DOM size" guidance threshold (documented constant, not a configured value).</summary>
    public const int LargeDomNodeThreshold = 1500;
    /// <summary>Long Tasks API definition: a task above 50 ms blocks the main thread; the total-blocking guidance value is 300 ms.</summary>
    public const double LongTaskTotalThresholdMs = 300;
    /// <summary>Repeated identical authenticated API calls within one page generation that are worth reviewing.</summary>
    public const int RepeatedApiCallThreshold = 3;

    public static IReadOnlyList<BrowserQualityFinding> Evaluate(PageAnalysis page, FrontendPerformanceThresholds performance, CoreWebVitalsThresholds vitals)
    {
        var evidence = page.BrowserEvidence;
        if (evidence is null) return [];
        var findings = new List<BrowserQualityFinding>();
        var at = evidence.CapturedAt;
        var identity = page.Identity;
        if (evidence.Accessibility is { } wcagEvidence)
        {
            foreach (var check in wcagEvidence.Checks.Where(c => c.Failed > 0 && c.CheckId is "text-contrast" or "label-in-name" or "a11y-image-name"))
            {
                var definition = WcagRegistry.All.First(d => d.SupportedChecks.Contains(check.CheckId));
                findings.Add(new BrowserQualityFinding
                {
                    RuleId = check.CheckId, Wcag = definition.CriterionId, Level = definition.Level,
                    Category = BrowserQualityCategory.Accessibility, Severity = FrontendQualitySeverity.High,
                    Page = identity, Element = check.Selectors.FirstOrDefault(), Title = definition.Title,
                    Observed = $"{check.Failed} failed element(s) in {check.Tested} tested elements.",
                    Expected = check.CheckId == "text-contrast" ? "Text contrast at least 4.5:1, or 3:1 for large text."
                        : check.CheckId == "label-in-name" ? "Accessible name contains the visible text label." : "Exposed images have an accessible name.",
                    Explanation = "A deterministic native check detected a failure. Remaining criterion coverage still needs review.",
                    Recommendation = "Inspect the structural selector, correct the failing property and collect a fresh snapshot.",
                    Evidence = check.Selectors.Select(s => $"Selector: {s}").ToList(),
                    Confidence = WcagConfidence.High, EvidenceSource = WcagEvidenceSource.DOM, ObservedAt = at,
                });
            }
        }

        // ── Accessibility (rule results computed in the browser; guidance/severity fixed per rule id) ──
        if (evidence.Accessibility is { } a11y)
            foreach (var rule in a11y.Findings.Where(f => f.Count > 0))
                findings.Add(new BrowserQualityFinding
                {
                    RuleId = rule.RuleId, Category = BrowserQualityCategory.Accessibility, Severity = Severity(rule.Severity), Page = identity, ObservedAt = at,
                    Title = rule.Title, Wcag = rule.Wcag,
                    Explanation = $"{rule.Count} element(s) matched the BirkNext accessibility check \"{rule.Title}\" on this page. Automated checks do not establish WCAG conformance.",
                    Evidence = [$"Occurrences: {rule.Count}", .. rule.Selectors.Select(s => $"Selector: {s}"), .. (rule.Wcag is { Length: > 0 } w ? [$"WCAG: {w}"] : Array.Empty<string>())],
                    Recommendation = rule.Guidance,
                });

        // ── Performance ──
        if (evidence.Performance is { } perf)
        {
            if (perf.LcpMs is { } lcp && lcp > vitals.LcpPoorMs)
                findings.Add(Finding("perf-lcp-poor", BrowserQualityCategory.Performance, FrontendQualitySeverity.High, identity, at, "Largest Contentful Paint is poor",
                    $"LCP measured at {lcp:0} ms in the user's browser; the environment's poor threshold is {vitals.LcpPoorMs} ms.",
                    "Reduce render-blocking resources, framework payload and slow authenticated API calls on the critical path.",
                    [$"LCP: {lcp:0} ms", $"Poor threshold: {vitals.LcpPoorMs} ms", $"Good threshold: {vitals.LcpGoodMs} ms"]));
            else if (perf.LcpMs is { } lcpWarn && lcpWarn > vitals.LcpGoodMs)
                findings.Add(Finding("perf-lcp-needs-improvement", BrowserQualityCategory.Performance, FrontendQualitySeverity.Medium, identity, at, "Largest Contentful Paint needs improvement",
                    $"LCP measured at {lcpWarn:0} ms; the environment's good threshold is {vitals.LcpGoodMs} ms.",
                    "Profile the largest element's dependencies (framework boot, data fetch) and defer non-critical work.",
                    [$"LCP: {lcpWarn:0} ms", $"Good threshold: {vitals.LcpGoodMs} ms"]));

            if (perf.Cls is { } cls && cls > vitals.ClsPoor)
                findings.Add(Finding("perf-cls-poor", BrowserQualityCategory.Performance, FrontendQualitySeverity.Medium, identity, at, "Cumulative Layout Shift is poor",
                    $"CLS {cls:0.###} exceeds the environment's poor threshold {vitals.ClsPoor:0.##}.",
                    "Reserve space for late content (images, tables, skeletons) and avoid inserting content above existing content.",
                    [$"CLS: {cls:0.###}", $"Poor threshold: {vitals.ClsPoor:0.##}"]));

            if (perf.LongTaskTotalMs is { } longTotal && longTotal > LongTaskTotalThresholdMs)
                findings.Add(Finding("perf-long-tasks", BrowserQualityCategory.Performance, FrontendQualitySeverity.Medium, identity, at, "Main thread blocked by long tasks",
                    $"{perf.LongTaskCount} long task(s) totalling {longTotal:0} ms were observed (longest {perf.LongestTaskMs:0} ms).",
                    "Split heavy JavaScript/.NET interop work, defer non-critical startup work and avoid synchronous rendering of large collections.",
                    [$"Long tasks: {perf.LongTaskCount}", $"Total: {longTotal:0} ms", $"Guidance threshold: {LongTaskTotalThresholdMs:0} ms"]));

            if (perf.ResourceCount > performance.MaxStartupRequests)
                findings.Add(Finding("perf-resource-count", BrowserQualityCategory.Network, FrontendQualitySeverity.Low, identity, at, "High number of resources loaded",
                    $"{perf.ResourceCount} resources were loaded for this page; the environment's startup request threshold is {performance.MaxStartupRequests}.",
                    "Bundle or lazy-load assets and trim framework/CDN requests.",
                    [$"Resources: {perf.ResourceCount}", $"Threshold: {performance.MaxStartupRequests}"]));

            if (perf.TransferredBytes > performance.MaxStartupSizeBytes)
                findings.Add(Finding("perf-transfer-size", BrowserQualityCategory.Network, FrontendQualitySeverity.Medium, identity, at, "Transferred bytes exceed the startup size threshold",
                    $"{Mb(perf.TransferredBytes)} MB were transferred for this page (threshold {Mb(performance.MaxStartupSizeBytes)} MB).",
                    "Enable compression and caching, and reduce framework/JS payload.",
                    [$"Transferred: {Mb(perf.TransferredBytes)} MB", $"JS: {Mb(perf.JsBytes)} MB", $"WASM: {Mb(perf.WasmBytes)} MB", $"Images: {Mb(perf.ImageBytes)} MB", $"Threshold: {Mb(performance.MaxStartupSizeBytes)} MB"]));

            foreach (var big in perf.LongestResources.Where(r => r.TransferBytes is { } b && b > performance.MaxIndividualAssetSizeBytes).Take(5))
                findings.Add(Finding("perf-oversized-resource", BrowserQualityCategory.Network, FrontendQualitySeverity.Medium, identity, at, "Oversized resource",
                    $"{big.Url} transferred {Mb(big.TransferBytes ?? 0)} MB (threshold {Mb(performance.MaxIndividualAssetSizeBytes)} MB).",
                    "Split, compress or lazy-load the resource.",
                    [$"URL: {big.Url}", $"Kind: {big.Kind}", $"Transfer: {Mb(big.TransferBytes ?? 0)} MB"]));

            foreach (var slow in perf.LongestResources.Where(r => r.Kind == "api" && r.DurationMs is { } d && d > performance.MaxSingleRequestLatencyMs).Take(5))
                findings.Add(Finding("perf-slow-api-resource", BrowserQualityCategory.Performance, FrontendQualitySeverity.Medium, identity, at, "Slow API request observed in the browser",
                    $"{slow.Url} took {slow.DurationMs:0} ms as seen by the browser (threshold {performance.MaxSingleRequestLatencyMs} ms).",
                    "Profile the endpoint and compare with the proxy-observed status for the same path.",
                    [$"URL: {slow.Url}", $"Duration: {slow.DurationMs:0} ms", $"Threshold: {performance.MaxSingleRequestLatencyMs} ms"]));

            if (perf.FailedResourceCount > 0)
                findings.Add(Finding("net-failed-resources", BrowserQualityCategory.Network, FrontendQualitySeverity.High, identity, at, "Failed resource loads",
                    $"{perf.FailedResourceCount} resource(s) answered with an HTTP error status while loading this page.",
                    "Fix or remove the failing references; failed framework resources break Blazor startup.",
                    perf.FailedResources.Select(r => $"{r.Status} {r.Url}").ToList()));

            if (perf.DuplicateFetchCount > 0)
                findings.Add(Finding("net-duplicate-resources", BrowserQualityCategory.Network, FrontendQualitySeverity.Low, identity, at, "Duplicate resource fetches",
                    $"{perf.DuplicateFetchCount} resource fetch(es) repeated an identical URL during this page visit.",
                    "Check caching headers and component re-render patterns; repeated fetches of the same asset are usually avoidable.",
                    perf.DuplicateResources.Select(r => $"{r.Count}× {r.Url}").ToList()));
        }

        // ── Runtime ──
        if (evidence.Runtime is { } runtime)
        {
            if (runtime.ErrorCount > 0)
                findings.Add(Finding("runtime-errors", BrowserQualityCategory.Runtime, FrontendQualitySeverity.High, identity, at, "Unhandled JavaScript errors",
                    $"{runtime.ErrorCount} window error event(s) were observed on this page.",
                    "Fix the script errors; messages below are sanitized (no values, no query strings).",
                    runtime.Errors.Where(e => e.Kind == "error").Take(10).Select(e => $"{e.Count}× {e.Message}{(e.Source is { Length: > 0 } s ? $" ({s})" : "")}").ToList()));
            if (runtime.RejectionCount > 0)
                findings.Add(Finding("runtime-unhandled-rejections", BrowserQualityCategory.Runtime, FrontendQualitySeverity.Medium, identity, at, "Unhandled promise rejections",
                    $"{runtime.RejectionCount} unhandled promise rejection(s) were observed on this page.",
                    "Handle the rejected promises (often failed fetches or interop calls).",
                    runtime.Errors.Where(e => e.Kind == "unhandledrejection").Take(10).Select(e => $"{e.Count}× {e.Message}").ToList()));
            if (runtime.ResourceFailureCount > 0)
                findings.Add(Finding("runtime-resource-errors", BrowserQualityCategory.Runtime, FrontendQualitySeverity.Medium, identity, at, "Resource load errors",
                    $"{runtime.ResourceFailureCount} element resource error event(s) (script/style/image failed to load).",
                    "Check the failing resource URLs.",
                    runtime.Errors.Where(e => e.Kind == "resource").Take(10).Select(e => $"{e.Count}× {e.Source ?? e.Message}").ToList()));
        }

        // ── DOM ──
        if (evidence.Dom is { } dom)
        {
            if (dom.NodeCount > LargeDomNodeThreshold)
                findings.Add(Finding("dom-large", BrowserQualityCategory.Dom, FrontendQualitySeverity.Low, identity, at, "Large DOM",
                    $"The page has {dom.NodeCount} DOM nodes (depth {dom.MaxDepth}); guidance threshold {LargeDomNodeThreshold}.",
                    "Virtualize long lists and avoid rendering hidden content.",
                    [$"Nodes: {dom.NodeCount}", $"Depth: {dom.MaxDepth}", $"Interactive controls: {dom.InteractiveCount}"]));
            if (dom.IframeCount > 0)
                findings.Add(Finding("dom-iframes", BrowserQualityCategory.Dom, FrontendQualitySeverity.Info, identity, at, "Iframes present",
                    $"{dom.IframeCount} iframe(s) are embedded on this page.", "Verify each embedded frame's origin and sandboxing.", [$"Iframes: {dom.IframeCount}"]));
        }

        // ── Blazor WASM ──
        if (evidence.Blazor is { Detected: true } blazor)
        {
            if (blazor.BootManifestFailed)
                findings.Add(Finding("blazor-boot-manifest-failed", BrowserQualityCategory.Blazor, FrontendQualitySeverity.Critical, identity, at, "Blazor boot manifest failed to load",
                    "blazor.boot.json answered with an error status; the application cannot start.", "Check the deployment of the _framework folder.",
                    blazor.FrameworkFailures.Select(f => $"{f.Status} {f.Url}").ToList()));
            else if (blazor.FrameworkFailures.Count > 0)
                findings.Add(Finding("blazor-framework-resource-failed", BrowserQualityCategory.Blazor, FrontendQualitySeverity.High, identity, at, "Blazor framework resources failed",
                    $"{blazor.FrameworkFailures.Count} framework resource(s) answered with an error status.", "Check integrity/deployment of the _framework assets.",
                    blazor.FrameworkFailures.Select(f => $"{f.Status} {f.Url}").ToList()));
            if (blazor.ErrorUiVisible)
                findings.Add(Finding("blazor-error-ui", BrowserQualityCategory.Blazor, FrontendQualitySeverity.High, identity, at, "Blazor error UI displayed",
                    "The application's #blazor-error-ui element became visible, which Blazor shows after an unhandled .NET runtime error.",
                    "Inspect the browser console/backend logs for the unhandled exception.", ["#blazor-error-ui visible"]));
            if (blazor.WasmBytes > performance.MaxWasmRuntimeSizeBytes)
                findings.Add(Finding("blazor-wasm-size", BrowserQualityCategory.Blazor, FrontendQualitySeverity.Medium, identity, at, "WASM payload above threshold",
                    $"{Mb(blazor.WasmBytes)} MB of .wasm were transferred (threshold {Mb(performance.MaxWasmRuntimeSizeBytes)} MB).",
                    "Enable trimming/AOT settings review, lazy-load assemblies and verify compression.",
                    [$"WASM: {Mb(blazor.WasmBytes)} MB", $"Threshold: {Mb(performance.MaxWasmRuntimeSizeBytes)} MB"]));
            if (blazor.FrameworkBytes > performance.MaxFrameworkSizeBytes)
                findings.Add(Finding("blazor-framework-size", BrowserQualityCategory.Blazor, FrontendQualitySeverity.Medium, identity, at, "Framework payload above threshold",
                    $"{Mb(blazor.FrameworkBytes)} MB of _framework resources were transferred (threshold {Mb(performance.MaxFrameworkSizeBytes)} MB).",
                    "Review trimming, lazy loading and caching of framework assets.",
                    [$"Framework: {Mb(blazor.FrameworkBytes)} MB", $"Resources: {blazor.FrameworkResourceCount}", $"Threshold: {Mb(performance.MaxFrameworkSizeBytes)} MB"]));
            if (blazor.RepeatedFrameworkDownloads > 0)
                findings.Add(Finding("blazor-repeated-framework-downloads", BrowserQualityCategory.Blazor, FrontendQualitySeverity.Low, identity, at, "Framework resources downloaded repeatedly",
                    $"{blazor.RepeatedFrameworkDownloads} framework resource URL(s) were fetched more than once during this visit.",
                    "Check caching headers on the _framework folder.", [$"Repeated: {blazor.RepeatedFrameworkDownloads}"]));
        }

        // ── Network correlation with proxy evidence for the same page (Endpoint Discovery) ──
        var repeated = RepeatedApiCalls(page);
        if (repeated.Count > 0)
        {
            var lcpText = evidence.Performance?.LcpMs is { } l ? $" LCP for this page was {l:0} ms." : "";
            findings.Add(Finding("net-repeated-api-calls", BrowserQualityCategory.Network, FrontendQualitySeverity.Medium, identity, at, "Repeated authenticated API calls on one page",
                $"The Local HTTPS proxy observed {repeated.Count} endpoint(s) called {RepeatedApiCallThreshold} or more times while this page was in use, correlated with the browser evidence of the same page visit.{lcpText} Timing correlation is not proof of causality.",
                "Review component re-rendering and data loading; identical requests can often be de-duplicated or cached client-side.",
                repeated.Select(e => $"{e.Count}× {e.Method} {e.Host}{e.Path}{(e.OperationName is { Length: > 0 } op ? $" ({e.OperationType} {op})" : "")} · last status {e.LastStatus}").ToList(),
                CorrelatedSource));
        }

        return findings;
    }

    /// <summary>Repeated identical REST (method+host+path) / GraphQL (endpoint+operation) calls recorded by the proxy for this page generation.</summary>
    public static IReadOnlyList<ObservedNetworkEndpoint> RepeatedApiCalls(PageAnalysis page) => page.Endpoints
        .Where(e => e.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl && e.Count >= RepeatedApiCallThreshold)
        .OrderByDescending(e => e.Count).ToList();

    public static FrontendQualitySeverity Severity(string? value) => value switch
    {
        "Critical" => FrontendQualitySeverity.Critical, "High" => FrontendQualitySeverity.High, "Medium" => FrontendQualitySeverity.Medium,
        "Info" => FrontendQualitySeverity.Info, _ => FrontendQualitySeverity.Low,
    };

    public static FrontendQualityCategory ToReportCategory(BrowserQualityCategory category) => category switch
    {
        BrowserQualityCategory.Accessibility => FrontendQualityCategory.Accessibility,
        BrowserQualityCategory.Performance or BrowserQualityCategory.Network => FrontendQualityCategory.Performance,
        BrowserQualityCategory.Blazor or BrowserQualityCategory.Runtime => FrontendQualityCategory.BlazorWasm,
        BrowserQualityCategory.SecurityObservation => FrontendQualityCategory.Security,
        _ => FrontendQualityCategory.Standards,
    };

    private static BrowserQualityFinding Finding(string ruleId, BrowserQualityCategory category, FrontendQualitySeverity severity, string page, DateTimeOffset at,
        string title, string explanation, string recommendation, List<string> evidence, string source = CompanionSource) => new()
    {
        RuleId = ruleId, Category = category, Severity = severity, Page = page, ObservedAt = at, Title = title, Explanation = explanation,
        Recommendation = recommendation, Evidence = evidence, Source = source,
    };

    private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("0.00");
}
