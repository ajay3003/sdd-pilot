using System.Text.Json.Serialization;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Models;

/// <summary>The five performance layers of BirkNext Performance Quality. Kept separate in metrics, findings, coverage and UI.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceLayer { Page, Runtime, Resources, Api, Blazor }

/// <summary>Observation phase a metric or finding belongs to. Initial application load and SPA navigation are never mixed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformancePhase { InitialLoad, SpaNavigation, Runtime, Background }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceMetricStatus { Good, NeedsImprovement, Poor, Informational, NotMeasured }

/// <summary>Where the threshold applied to a metric came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceThresholdSource
{
    /// <summary>Documented BirkNext / Web Vitals default.</summary>
    Default,
    /// <summary>Saved Target Environment override (value differs from the documented default).</summary>
    TargetEnvironment,
    /// <summary>Explicit project policy preset (Strict thresholds).</summary>
    Policy,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceConfidence { High, Medium, Low }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceCoverageState { Complete, Partial, NotAvailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceAssessmentState { Complete, Partial, NotAssessed }

/// <summary>A resolved threshold with its provenance. Good/Poor may be null for informational metrics.</summary>
public sealed record PerformanceThreshold(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("good")] double? Good,
    [property: JsonPropertyName("poor")] double? Poor,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("source")] PerformanceThresholdSource Source)
{
    [JsonIgnore] public string Display => Good is null && Poor is null ? "—"
        : Poor is null ? $"≤ {PerformanceFormat.Value(Good, Unit)}"
        : Good is null ? $"< {PerformanceFormat.Value(Poor, Unit)}"
        : $"good ≤ {PerformanceFormat.Value(Good, Unit)} · poor > {PerformanceFormat.Value(Poor, Unit)}";
    [JsonIgnore] public string SourceLabel => PerformanceFormat.SourceLabel(Source);
}

/// <summary>One measured (or explicitly unmeasured) metric of a page observation.</summary>
public sealed record PerformanceQualityMetric
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("layer")] public PerformanceLayer Layer { get; init; }
    [JsonPropertyName("phase")] public PerformancePhase Phase { get; init; }
    [JsonPropertyName("value")] public double? Value { get; init; }
    /// <summary>"ms" | "score" | "bytes" | "count".</summary>
    [JsonPropertyName("unit")] public string Unit { get; init; } = "ms";
    [JsonPropertyName("status")] public PerformanceMetricStatus Status { get; init; }
    [JsonPropertyName("threshold")] public PerformanceThreshold? Threshold { get; init; }
    /// <summary>"Browser Companion", "Local HTTPS Proxy" or "BirkNext" (derived).</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = PerformanceQualitySources.Companion;
    [JsonPropertyName("confidence")] public PerformanceConfidence Confidence { get; init; } = PerformanceConfidence.High;
    /// <summary>Why a metric is not measured, or a methodology note (e.g. "BirkNext-specific metric, not LCP").</summary>
    [JsonPropertyName("note")] public string? Note { get; init; }
    /// <summary>Textual value for non-numeric informational metrics (e.g. Blazor load kind "warm").</summary>
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonIgnore] public string Display => Status == PerformanceMetricStatus.NotMeasured ? "Not measured" : Text ?? PerformanceFormat.Value(Value, Unit);
}

/// <summary>Common performance finding model (rule id, layer, severity, page, phase, observed/threshold with source, evidence, confidence).</summary>
public sealed record PerformanceQualityFinding
{
    [JsonPropertyName("ruleId")] public string RuleId { get; init; } = "";
    [JsonPropertyName("category")] public PerformanceLayer Category { get; init; }
    [JsonPropertyName("severity")] public FrontendQualitySeverity Severity { get; init; }
    [JsonPropertyName("page")] public string Page { get; init; } = "";
    [JsonPropertyName("phase")] public PerformancePhase Phase { get; init; }
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("explanation")] public string Explanation { get; init; } = "";
    [JsonPropertyName("observedValue")] public string ObservedValue { get; init; } = "";
    [JsonPropertyName("threshold")] public string Threshold { get; init; } = "";
    [JsonPropertyName("thresholdSource")] public PerformanceThresholdSource? ThresholdSource { get; init; }
    /// <summary>Sanitized values only: metrics, counts, operation names, sanitized URLs. Never a credential, body or query value.</summary>
    [JsonPropertyName("evidence")] public List<string> Evidence { get; init; } = [];
    /// <summary>"Browser Companion" | "Local HTTPS Proxy" | "Browser Companion + Local HTTPS Proxy".</summary>
    [JsonPropertyName("evidenceSource")] public string EvidenceSource { get; init; } = PerformanceQualitySources.Companion;
    [JsonPropertyName("recommendation")] public string Recommendation { get; init; } = "";
    [JsonPropertyName("confidence")] public PerformanceConfidence Confidence { get; init; } = PerformanceConfidence.High;
    [JsonPropertyName("observedAt")] public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>Latency statistics with an explicit minimum-sample rule: p50/p95 are only published when <see cref="Sufficient"/>.</summary>
public sealed record PerformanceLatencyStatistics
{
    /// <summary>Samples needed before percentiles are meaningful enough to publish.</summary>
    public const int MinimumSamplesForPercentiles = 5;

    [JsonPropertyName("sampleCount")] public int SampleCount { get; init; }
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("p50")] public double? P50 { get; init; }
    [JsonPropertyName("p95")] public double? P95 { get; init; }
    [JsonPropertyName("max")] public double? Max { get; init; }
    [JsonPropertyName("total")] public double Total { get; init; }
    [JsonPropertyName("sufficient")] public bool Sufficient { get; init; }
    /// <summary>The single value used for slow-call classification: p50 when sufficient, otherwise the observed maximum.</summary>
    [JsonIgnore] public double? Representative => Sufficient ? P50 : Max;
    [JsonIgnore] public string RepresentativeLabel => Sufficient ? "p50" : "observed";

    public static PerformanceLatencyStatistics Compute(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return new PerformanceLatencyStatistics();
        var sorted = samples.OrderBy(v => v).ToList();
        var sufficient = sorted.Count >= MinimumSamplesForPercentiles;
        return new PerformanceLatencyStatistics
        {
            SampleCount = sorted.Count, Min = sorted[0], Max = sorted[^1], Total = sorted.Sum(), Sufficient = sufficient,
            P50 = sufficient ? Percentile(sorted, 0.50) : null, P95 = sufficient ? Percentile(sorted, 0.95) : null,
        };
    }

    /// <summary>Nearest-rank percentile on the sorted sample list.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiOperationKind { Rest, GraphQl, WebSocket }

/// <summary>One REST endpoint, GraphQL operation or WebSocket connection observed for a page, with latency statistics and status counts.</summary>
public sealed record ApiOperationSummary
{
    [JsonPropertyName("kind")] public ApiOperationKind Kind { get; init; }
    [JsonPropertyName("method")] public string Method { get; init; } = "";
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("operationType")] public GraphQlOperationType OperationType { get; init; }
    [JsonPropertyName("operationName")] public string? OperationName { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("statistics")] public PerformanceLatencyStatistics Statistics { get; init; } = new();
    [JsonPropertyName("lastStatus")] public int LastStatus { get; init; }
    [JsonPropertyName("errorCount")] public int ErrorCount { get; init; }
    [JsonPropertyName("authRejectedCount")] public int AuthRejectedCount { get; init; }
    [JsonPropertyName("notModifiedCount")] public int NotModifiedCount { get; init; }
    [JsonPropertyName("firstObservedAt")] public DateTimeOffset FirstObservedAt { get; init; }
    [JsonPropertyName("lastObservedAt")] public DateTimeOffset LastObservedAt { get; init; }
    [JsonPropertyName("status")] public PerformanceMetricStatus LatencyStatus { get; init; } = PerformanceMetricStatus.Informational;
    [JsonPropertyName("isDuplicate")] public bool IsDuplicate { get; init; }
    /// <summary>Regular cadence over ≥ 4 samples spanning more than 10 s: polling-like (heuristic, lowers duplicate confidence; never suppresses evidence).</summary>
    [JsonPropertyName("isPollingLike")] public bool IsPollingLike { get; init; }
    [JsonPropertyName("cacheDirectives")] public string? CacheDirectives { get; init; }
    [JsonIgnore] public string Display => Kind == ApiOperationKind.GraphQl && OperationName is { Length: > 0 }
        ? $"{OperationType} {OperationName}" : $"{Method} {Host}{Path}";
    [JsonIgnore] public string Identity => Kind == ApiOperationKind.GraphQl ? $"gql|{Host}{Path}|{OperationType}|{OperationName}" : $"{Kind}|{Method}|{Host}{Path}";
}

/// <summary>A cluster of API requests completing inside one short window after navigation ("network burst").</summary>
public sealed record ApiBurst(
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("windowMs")] double WindowMs,
    [property: JsonPropertyName("requestCount")] int RequestCount,
    [property: JsonPropertyName("graphQlCount")] int GraphQlCount,
    [property: JsonPropertyName("restCount")] int RestCount);

/// <summary>Observed sequential request pattern (A finishes → B starts → C starts). Wording is deliberately not causal.</summary>
public sealed record ApiSequentialPattern(
    [property: JsonPropertyName("operations")] List<string> Operations,
    [property: JsonPropertyName("totalMs")] double TotalMs,
    [property: JsonPropertyName("maxGapMs")] double MaxGapMs);

/// <summary>API / network layer summary for one page (Local HTTPS proxy evidence via Endpoint Discovery).</summary>
public sealed record ApiPerformanceSummary
{
    [JsonPropertyName("available")] public bool Available { get; init; }
    /// <summary>Endpoints exist but carry no timing samples (traffic recorded before timing metadata existed): counts only.</summary>
    [JsonPropertyName("timingAvailable")] public bool TimingAvailable { get; init; }
    [JsonPropertyName("restCalls")] public int RestCalls { get; init; }
    [JsonPropertyName("graphQlCalls")] public int GraphQlCalls { get; init; }
    [JsonPropertyName("webSocketConnections")] public int WebSocketConnections { get; init; }
    [JsonPropertyName("slowCalls")] public int SlowCalls { get; init; }
    [JsonPropertyName("errorResponses")] public int ErrorResponses { get; init; }
    [JsonPropertyName("authRejected")] public int AuthRejected { get; init; }
    [JsonPropertyName("operations")] public List<ApiOperationSummary> Operations { get; init; } = [];
    [JsonPropertyName("duplicateOperations")] public List<ApiOperationSummary> DuplicateOperations { get; init; } = [];
    [JsonPropertyName("slowestOperation")] public ApiOperationSummary? SlowestOperation { get; init; }
    [JsonPropertyName("bursts")] public List<ApiBurst> Bursts { get; init; } = [];
    [JsonPropertyName("sequentialPatterns")] public List<ApiSequentialPattern> SequentialPatterns { get; init; } = [];
    [JsonPropertyName("overall")] public PerformanceLatencyStatistics Overall { get; init; } = new();
}

/// <summary>Per-layer coverage of one page observation. One available category never implies the others were assessed.</summary>
public sealed record PerformanceCoverage
{
    [JsonPropertyName("browser")] public PerformanceCoverageState Browser { get; init; } = PerformanceCoverageState.NotAvailable;
    [JsonPropertyName("runtime")] public PerformanceCoverageState Runtime { get; init; } = PerformanceCoverageState.NotAvailable;
    [JsonPropertyName("resources")] public PerformanceCoverageState Resources { get; init; } = PerformanceCoverageState.NotAvailable;
    [JsonPropertyName("api")] public PerformanceCoverageState Api { get; init; } = PerformanceCoverageState.NotAvailable;
    [JsonPropertyName("blazor")] public PerformanceCoverageState Blazor { get; init; } = PerformanceCoverageState.NotAvailable;
    [JsonPropertyName("reasons")] public List<string> Reasons { get; init; } = [];

    [JsonIgnore] public IEnumerable<PerformanceCoverageState> Layers => [Browser, Runtime, Resources, Api, Blazor];
    [JsonIgnore] public PerformanceAssessmentState Overall =>
        Layers.All(l => l == PerformanceCoverageState.Complete) ? PerformanceAssessmentState.Complete
        : Layers.Any(l => l != PerformanceCoverageState.NotAvailable) ? PerformanceAssessmentState.Partial
        : PerformanceAssessmentState.NotAssessed;
    [JsonIgnore] public string OverallLabel => Overall switch
    {
        PerformanceAssessmentState.Complete => "Complete assessment",
        PerformanceAssessmentState.Partial => "Partial assessment",
        _ => "Not assessed",
    };

    public static PerformanceCoverage Merge(IEnumerable<PerformanceCoverage> pages)
    {
        var list = pages.ToList();
        if (list.Count == 0) return new PerformanceCoverage { Reasons = ["No performance evidence collected for any page."] };
        static PerformanceCoverageState Best(IEnumerable<PerformanceCoverageState> states) =>
            states.Any(s => s == PerformanceCoverageState.Complete) ? PerformanceCoverageState.Complete
            : states.Any(s => s == PerformanceCoverageState.Partial) ? PerformanceCoverageState.Partial : PerformanceCoverageState.NotAvailable;
        return new PerformanceCoverage
        {
            Browser = Best(list.Select(p => p.Browser)), Runtime = Best(list.Select(p => p.Runtime)), Resources = Best(list.Select(p => p.Resources)),
            Api = Best(list.Select(p => p.Api)), Blazor = Best(list.Select(p => p.Blazor)),
            Reasons = list.SelectMany(p => p.Reasons).Distinct().ToList(),
        };
    }
}

/// <summary>One row of the lightweight chronological timeline (browser resources and proxy-observed API calls of the same page generation).</summary>
public sealed record PerformanceTimelineEntry(
    [property: JsonPropertyName("startMs")] double StartMs,
    [property: JsonPropertyName("durationMs")] double? DurationMs,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("bytes")] long? Bytes,
    [property: JsonPropertyName("status")] int? Status,
    [property: JsonPropertyName("delivery")] string? Delivery);

/// <summary>
/// Compact, threshold-independent performance history of one page generation, persisted with the page so a later generation can be
/// compared with the previous one (Current vs Previous). Raw numbers only; status is recomputed against current thresholds.
/// </summary>
public sealed class PagePerformanceHistoryEntry
{
    [JsonPropertyName("generation")] public int Generation { get; set; }
    [JsonPropertyName("observationType")] public string ObservationType { get; set; } = "initial-load";
    [JsonPropertyName("capturedAt")] public DateTimeOffset CapturedAt { get; set; }
    [JsonPropertyName("lcpMs")] public double? LcpMs { get; set; }
    [JsonPropertyName("cls")] public double? Cls { get; set; }
    [JsonPropertyName("stabilizationMs")] public double? StabilizationMs { get; set; }
    [JsonPropertyName("transferredBytes")] public long? TransferredBytes { get; set; }
    [JsonPropertyName("longTaskCount")] public int? LongTaskCount { get; set; }
    [JsonPropertyName("resourceCount")] public int? ResourceCount { get; set; }
    [JsonPropertyName("apiCalls")] public int? ApiCalls { get; set; }
    [JsonPropertyName("graphQlCalls")] public int? GraphQlCalls { get; set; }
    [JsonPropertyName("wasmBytes")] public long? WasmBytes { get; set; }
    [JsonPropertyName("frameworkBytes")] public long? FrameworkBytes { get; set; }
    /// <summary>Bound on entries kept per page.</summary>
    public const int MaxEntries = 10;
}

public sealed record PerformanceDelta(
    [property: JsonPropertyName("metric")] string Metric,
    [property: JsonPropertyName("current")] string Current,
    [property: JsonPropertyName("previous")] string Previous,
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("worse")] bool? Worse);

/// <summary>Current generation vs the previous recorded generation of the same page.</summary>
public sealed record PerformanceComparison(
    [property: JsonPropertyName("currentGeneration")] int CurrentGeneration,
    [property: JsonPropertyName("previousGeneration")] int PreviousGeneration,
    [property: JsonPropertyName("previousCapturedAt")] DateTimeOffset PreviousCapturedAt,
    [property: JsonPropertyName("samePhase")] bool SamePhase,
    [property: JsonPropertyName("deltas")] List<PerformanceDelta> Deltas);

/// <summary>The per-page result of BirkNext Performance Quality: metrics + findings per layer, coverage, API summary, timeline, comparison.</summary>
public sealed record PagePerformanceSnapshot
{
    [JsonPropertyName("pageId")] public string PageId { get; init; } = "";
    [JsonPropertyName("pageTitle")] public string PageTitle { get; init; } = "";
    [JsonPropertyName("generation")] public int Generation { get; init; }
    [JsonPropertyName("observationType")] public PerformancePhase ObservationType { get; init; } = PerformancePhase.InitialLoad;
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; init; }
    [JsonPropertyName("stabilizedAt")] public DateTimeOffset? StabilizedAt { get; init; }
    [JsonPropertyName("capturedAt")] public DateTimeOffset? CapturedAt { get; init; }
    [JsonPropertyName("browserName")] public string? BrowserName { get; init; }
    [JsonPropertyName("metrics")] public List<PerformanceQualityMetric> Metrics { get; init; } = [];
    [JsonPropertyName("findings")] public List<PerformanceQualityFinding> Findings { get; init; } = [];
    [JsonPropertyName("api")] public ApiPerformanceSummary Api { get; init; } = new();
    [JsonPropertyName("coverage")] public PerformanceCoverage Coverage { get; init; } = new();
    [JsonPropertyName("resources")] public BrowserPerformanceSummary? Resources { get; init; }
    [JsonPropertyName("blazor")] public BrowserBlazorSummary? Blazor { get; init; }
    [JsonPropertyName("timeline")] public List<PerformanceTimelineEntry> Timeline { get; init; } = [];
    [JsonPropertyName("comparison")] public PerformanceComparison? Comparison { get; init; }
    [JsonPropertyName("notes")] public List<string> Notes { get; init; } = [];

    [JsonIgnore] public bool HasEvidence => Coverage.Overall != PerformanceAssessmentState.NotAssessed;
    public PerformanceQualityMetric? Metric(string id) => Metrics.FirstOrDefault(m => m.Id == id);
    public IEnumerable<PerformanceQualityMetric> MetricsFor(PerformanceLayer layer) => Metrics.Where(m => m.Layer == layer);
    /// <summary>Worst measured status across all metrics (Poor &gt; NeedsImprovement &gt; Good); NotMeasured when nothing was measured.</summary>
    [JsonIgnore] public PerformanceMetricStatus WorstStatus => PerformanceFormat.Worst(Metrics.Select(m => m.Status));
    [JsonIgnore] public PerformanceQualityMetric? WorstMetric => Metrics
        .Where(m => m.Status is PerformanceMetricStatus.Poor or PerformanceMetricStatus.NeedsImprovement)
        .OrderBy(m => m.Status == PerformanceMetricStatus.Poor ? 0 : 1).FirstOrDefault();
}

/// <summary>Application-wide overview row (one per page) for sorting by the worst metric.</summary>
public sealed record PerformanceOverviewRow(
    string PageId, string Title, PerformancePhase Phase, PerformanceQualityMetric? Lcp, PerformanceQualityMetric? Stabilization, PerformanceQualityMetric? Transfer,
    PerformanceQualityMetric? ApiCalls, PerformanceQualityMetric? SlowCalls, PerformanceQualityMetric? LongTasks, PerformanceMetricStatus Status, string? WorstMetric, PerformanceAssessmentState Coverage);

/// <summary>Result of the BirkNext Performance Quality engine for one review.</summary>
public sealed record PerformanceQualityReviewResult
{
    [JsonPropertyName("companionState")] public BrowserCompanionState CompanionState { get; init; }
    [JsonPropertyName("companionMessage")] public string CompanionMessage { get; init; } = "";
    [JsonPropertyName("proxyState")] public LocalHttpsProxyState? ProxyState { get; init; }
    [JsonPropertyName("proxyEvidenceAvailable")] public bool ProxyEvidenceAvailable { get; init; }
    [JsonPropertyName("pages")] public List<PagePerformanceSnapshot> Pages { get; init; } = [];
    [JsonPropertyName("coverage")] public PerformanceCoverage Coverage { get; init; } = new();
    [JsonPropertyName("limitations")] public List<string> Limitations { get; init; } = [];
    [JsonPropertyName("evaluatedAt")] public DateTimeOffset EvaluatedAt { get; init; }
    [JsonPropertyName("browserName")] public string? BrowserName { get; init; }
    [JsonPropertyName("thresholds")] public List<PerformanceThreshold> Thresholds { get; init; } = [];
    [JsonIgnore] public int PagesWithEvidence => Pages.Count(p => p.HasEvidence);
    [JsonIgnore] public IEnumerable<PerformanceQualityFinding> Findings => Pages.SelectMany(p => p.Findings);
    /// <summary>Assessed when at least one page has any performance evidence (browser or API); coverage says how complete it is.</summary>
    [JsonIgnore] public bool Assessed => PagesWithEvidence > 0;
    [JsonIgnore] public PerformanceAssessmentState Assessment => Assessed ? Coverage.Overall : PerformanceAssessmentState.NotAssessed;
}

public static class PerformanceQualitySources
{
    public const string Companion = "Browser Companion";
    public const string Proxy = "Local HTTPS Proxy";
    public const string Correlated = "Browser Companion + Local HTTPS Proxy";
    public const string Derived = "BirkNext";
    public const string EngineName = "BirkNext Performance Quality";
}

public static class PerformanceFormat
{
    public static string Value(double? value, string unit)
    {
        if (value is null) return "—";
        var v = value.Value;
        return unit switch
        {
            "ms" => v >= 1000 ? $"{v / 1000:0.0} s" : $"{v:0} ms",
            "bytes" => Bytes((long)v),
            "score" => v.ToString("0.###"),
            "count" => v.ToString("0"),
            _ => v.ToString("0.##"),
        };
    }

    public static string Bytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0} KB" : $"{bytes} B";

    public static string SourceLabel(PerformanceThresholdSource source) => source switch
    {
        PerformanceThresholdSource.TargetEnvironment => "Target Environment",
        PerformanceThresholdSource.Policy => "Policy (Strict preset)",
        _ => "Default",
    };

    public static string StatusLabel(PerformanceMetricStatus status) => status switch
    {
        PerformanceMetricStatus.Good => "Good",
        PerformanceMetricStatus.NeedsImprovement => "Needs improvement",
        PerformanceMetricStatus.Poor => "Poor",
        PerformanceMetricStatus.Informational => "Informational",
        _ => "Not measured",
    };

    public static string PhaseLabel(PerformancePhase phase) => phase switch
    {
        PerformancePhase.InitialLoad => "Initial load",
        PerformancePhase.SpaNavigation => "SPA navigation",
        PerformancePhase.Runtime => "Runtime",
        _ => "Background",
    };

    public static PerformanceMetricStatus Worst(IEnumerable<PerformanceMetricStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Contains(PerformanceMetricStatus.Poor)) return PerformanceMetricStatus.Poor;
        if (list.Contains(PerformanceMetricStatus.NeedsImprovement)) return PerformanceMetricStatus.NeedsImprovement;
        if (list.Contains(PerformanceMetricStatus.Good)) return PerformanceMetricStatus.Good;
        if (list.Contains(PerformanceMetricStatus.Informational)) return PerformanceMetricStatus.Informational;
        return PerformanceMetricStatus.NotMeasured;
    }

    public static int Rank(PerformanceMetricStatus status) => status switch
    {
        PerformanceMetricStatus.Poor => 0, PerformanceMetricStatus.NeedsImprovement => 1, PerformanceMetricStatus.Good => 2,
        PerformanceMetricStatus.Informational => 3, _ => 4,
    };
}
