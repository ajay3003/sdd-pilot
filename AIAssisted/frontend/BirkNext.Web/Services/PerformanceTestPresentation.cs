using System.Globalization;
using System.Net;
using System.Text;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;
using BirkNext.Web.Models;
using PerformanceReadinessState = BirkNext.PerformanceTests.PerformanceReadinessState;
using PerformanceMetric = BirkNext.PerformanceTests.PerformanceMetric;
using PerformanceThreshold = BirkNext.PerformanceTests.PerformanceThreshold;

namespace BirkNext.Web.Services;

/// <summary>Labels and tones for Performance Test Review. Neutral states (not assessed, optional, provider missing) never use the failure tone.</summary>
public static class PerformanceTestPresentation
{
    public static PerformanceTestDefinition NewDefinition(FrontendAnalysisProfile profile) => new()
    {
        Id = "", EnvironmentId = profile.Id, Name = "Baseline check", TargetType = PerformanceTargetType.RestHttp,
        TargetOrigin = Uri.TryCreate(profile.TargetUrl, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "",
        EnvironmentType = profile.EnvironmentType.ToString(), EnvironmentName = string.IsNullOrWhiteSpace(profile.Name) ? profile.Id : profile.Name,
        Scenario = new PerformanceScenario { Name = "Main scenario", Steps = [new HttpPerformanceStep { Name = "home", Method = "GET", RelativePath = "/", ExpectedStatusCodes = [200], ThinkTimeMs = 500 }] },
        Workload = new PerformanceWorkload(),
        Thresholds =
        [
            new PerformanceThreshold { Metric = PerformanceMetric.LatencyP95Ms, Operator = ThresholdOperator.LessThan, Value = 500, Severity = ThresholdSeverity.Required },
            new PerformanceThreshold { Metric = PerformanceMetric.ErrorRatePercent, Operator = ThresholdOperator.LessThan, Value = 1, Severity = ThresholdSeverity.Required },
        ],
    };

    public static string Tone(PerformanceReadinessState s) => s switch
    {
        PerformanceReadinessState.Ready => "complete",
        PerformanceReadinessState.Optional => "muted",
        PerformanceReadinessState.UnsafeEnvironment or PerformanceReadinessState.Blocked => "attention",
        _ => "partial",
    };

    public static string Label(PerformanceReadinessState s) => s switch
    {
        PerformanceReadinessState.NeedsConfiguration => "Needs configuration",
        PerformanceReadinessState.NeedsAuthentication => "Needs authentication",
        PerformanceReadinessState.NeedsTestData => "Needs test data",
        PerformanceReadinessState.ProviderUnavailable => "Provider unavailable",
        PerformanceReadinessState.UnsafeEnvironment => "Blocked — unsafe environment",
        PerformanceReadinessState.InvalidScenario => "Invalid scenario",
        PerformanceReadinessState.RuntimeUnavailable => "Runtime unavailable",
        PerformanceReadinessState.ImageMissing => "Image missing",
        PerformanceReadinessState.NetworkUnavailable => "Network unavailable",
        _ => s.ToString(),
    };

    public static string Label(ProviderAvailability a) => a switch
    {
        ProviderAvailability.RuntimeUnavailable => "Runtime unavailable",
        ProviderAvailability.ImageMissing => "Image missing",
        ProviderAvailability.VersionUnsupported => "Version unsupported",
        _ => a.ToString(),
    };

    public static string Label(PerformanceRunState s) => s switch
    {
        PerformanceRunState.ExecutionFailed => "Execution failed (tool/run error — not a performance result)",
        PerformanceRunState.TimedOut => "Timed out",
        _ => s.ToString(),
    };

    public static string Tone(PerformanceRunState s) => s switch
    {
        PerformanceRunState.Completed => "complete",
        PerformanceRunState.Running or PerformanceRunState.Preparing or PerformanceRunState.Queued or PerformanceRunState.Cancelling => "partial",
        _ => "muted",
    };

    public static string Label(PerformanceQualityVerdict v) => v switch
    {
        PerformanceQualityVerdict.NotAssessed => "Not assessed",
        _ => v.ToString(),
    };

    public static string Tone(PerformanceQualityVerdict v) => v switch
    {
        PerformanceQualityVerdict.Pass => "complete",
        PerformanceQualityVerdict.Warning => "partial",
        PerformanceQualityVerdict.Fail => "attention",
        _ => "muted",
    };

    public static string Label(CheckOutcome o) => o switch
    {
        CheckOutcome.NotAssessed => "Not assessed",
        _ => o.ToString(),
    };

    public static string Tone(CheckOutcome o) => o switch
    {
        CheckOutcome.Pass => "complete",
        CheckOutcome.Warning => "partial",
        CheckOutcome.Fail => "attention",
        _ => "muted",
    };

    public static string Label(PerformanceDriftState s) => s switch
    {
        PerformanceDriftState.DegradedWithinTolerance => "Degraded within tolerance",
        PerformanceDriftState.NotComparable => "Not comparable",
        PerformanceDriftState.NotAssessed => "Comparison only (no drift policy)",
        _ => s.ToString(),
    };

    public static string Tone(PerformanceDriftState s) => s switch
    {
        PerformanceDriftState.Regression => "attention",
        PerformanceDriftState.DegradedWithinTolerance => "partial",
        PerformanceDriftState.Improved or PerformanceDriftState.Stable => "complete",
        _ => "muted",
    };

    public static string Delta(PerformanceMetricDelta d)
    {
        if (d.AbsoluteDelta is not { } abs) return "—";
        var sign = abs > 0 ? "+" : abs < 0 ? "−" : "±";
        var unit = d.Metric == PerformanceMetric.ErrorRatePercent ? " pp" : d.Metric == PerformanceMetric.ThroughputRps ? " req/s" : " ms";
        var absText = $"{sign}{Math.Abs(abs).ToString("0.##", CultureInfo.InvariantCulture)}{unit}";
        return d.RelativePercent is { } rel ? $"{absText} ({(rel > 0 ? "+" : rel < 0 ? "−" : "±")}{Math.Abs(rel).ToString("0.#", CultureInfo.InvariantCulture)} %)" : $"{absText} (relative change unavailable)";
    }

    public static string Duration(int seconds) => seconds >= 3600 ? $"{seconds / 3600} h {seconds % 3600 / 60} min" : seconds >= 60 ? $"{seconds / 60} min {seconds % 60} s" : $"{seconds} s";

    public static string Short(Guid id) => id.ToString("N")[..8];

    public static string Utc(DateTimeOffset? at) => at is { } t ? t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "—";

    /// <summary>The run's primary memory summary for history columns: container memory, else managed heap, else working set (first target component).</summary>
    public static ResourceStabilitySummary? ResourceMemory(PerformanceTestRun r) =>
        r.Resources?.Components.Where(c => c.Role == ResourceComponentRole.Target).SelectMany(c => c.Summaries)
            .Where(s => s.Metric is ResourceMetric.ContainerMemoryBytes or ResourceMetric.ManagedHeapBytes or ResourceMetric.WorkingSetBytes && s.LateSteadyValue is not null)
            .OrderBy(s => s.Metric == ResourceMetric.ContainerMemoryBytes ? 0 : s.Metric == ResourceMetric.ManagedHeapBytes ? 1 : 2).FirstOrDefault();

    /// <summary>Worst per-component state among target components (precise states, never "leak"); "Not configured" / evidence state otherwise.</summary>
    public static string ResourceStatus(PerformanceTestRun r)
    {
        if (r.Resources is not { Configured: true } a) return "Not configured";
        var order = new[] { ResourceAssessmentState.Regression, ResourceAssessmentState.PotentialRegression, ResourceAssessmentState.NotComparable, ResourceAssessmentState.InsufficientEvidence,
            ResourceAssessmentState.IncreasingWithinTolerance, ResourceAssessmentState.StableWithinPolicy, ResourceAssessmentState.NotAssessed, ResourceAssessmentState.Unavailable };
        var states = a.Components.Where(c => c.Role == ResourceComponentRole.Target).SelectMany(c => c.Summaries).Select(s => s.AssessmentState).ToList();
        if (states.Count == 0) return ResourceFormat.Collection(a.EvidenceState);
        var worst = order.First(states.Contains);
        return worst == ResourceAssessmentState.Unavailable ? "Unavailable" : ResourceFormat.State(worst);
    }

    public static string Load(PerformanceWorkload w) => w.Mode == WorkloadMode.VirtualUsers ? $"{w.VirtualUsers} virtual users" : $"{w.RequestsPerSecond?.ToString("0.##", CultureInfo.InvariantCulture)} req/s";

    /// <summary>HTML export of one run: definition, workload, thresholds, metrics, outcomes, drift, limitations, provenance. No credential exists in a run.</summary>
    public static string Export(PerformanceTestRun r)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Performance Test Review</title><style>body{font-family:system-ui,sans-serif;margin:2rem;max-width:960px}table{border-collapse:collapse;width:100%;margin:.5rem 0}td,th{border:1px solid #cbd5e1;padding:.3rem .5rem;text-align:left}</style></head><body>");
        sb.Append($"<h1>Performance Test Review — {E(r.DefinitionSnapshot.Name)}</h1>");
        sb.Append($"<p>Run {E(Short(r.RunId))} · {E(Utc(r.StartedAt ?? r.CreatedAt))} · {E(r.DefinitionSnapshot.EnvironmentName)} ({E(r.EnvironmentType)}) · target {E(r.TargetOrigin)}</p>");
        sb.Append($"<p>Execution: <strong>{E(Label(r.State))}</strong> · Quality: <strong>{E(Label(r.Verdict))}</strong>{(r.Quality?.QualityPercent is { } q ? $" ({q:0.#} % among {r.Quality.Denominator} assessed threshold(s))" : "")}</p>");
        sb.Append($"<h2>Workload</h2><p>{E(r.DefinitionSnapshot.Workload.Purpose.ToString())}, {E(Load(r.DefinitionSnapshot.Workload))}</p><ul>");
        foreach (var t in PerformanceTestRules.Timeline(r.DefinitionSnapshot.Workload)) sb.Append($"<li>{E(t)}</li>");
        sb.Append("</ul>");
        if (r.Metrics is { } m)
        {
            sb.Append("<h2>Metrics</h2><table><tr><th>Metric</th><th>Value</th></tr>");
            sb.Append($"<tr><td>Requests</td><td>{m.RequestCount} ({m.SuccessfulRequests} successful, {m.FailedRequests} failed)</td></tr>");
            foreach (var metric in Enum.GetValues<PerformanceMetric>()) sb.Append($"<tr><td>{E(PerformanceTestRules.Label(metric))}</td><td>{E(PerformanceTestRules.Format(m.Value(metric), metric))}</td></tr>");
            sb.Append("</table>");
        }
        if (r.ThresholdResults.Count > 0)
        {
            sb.Append("<h2>Thresholds</h2><table><tr><th>Metric</th><th>Measured</th><th>Expected</th><th>Result</th><th>Severity</th></tr>");
            foreach (var t in r.ThresholdResults)
                sb.Append($"<tr><td>{E(PerformanceTestRules.Label(t.Metric))}</td><td>{E(PerformanceTestRules.Format(t.Measured, t.Metric))}</td><td>{E(PerformanceTestRules.Symbol(t.Operator))} {E(PerformanceTestRules.Format(t.Expected, t.Metric))}</td><td>{E(Label(t.Outcome))}</td><td>{E(t.Severity.ToString())}</td></tr>");
            sb.Append("</table>");
        }
        if (r.Drift is { } d)
        {
            sb.Append($"<h2>Baseline comparison (v{d.BaselineVersion})</h2><p>{E(Label(d.State))}</p>");
            foreach (var n in d.CompatibilityNotes) sb.Append($"<p>{E(n)}</p>");
            sb.Append("<table><tr><th>Metric</th><th>Baseline</th><th>This run</th><th>Change</th><th>Policy</th></tr>");
            foreach (var x in d.Deltas)
                sb.Append($"<tr><td>{E(PerformanceTestRules.Label(x.Metric))}</td><td>{E(PerformanceTestRules.Format(x.Reference, x.Metric))}</td><td>{E(PerformanceTestRules.Format(x.Current, x.Metric))}</td><td>{E(Delta(x))}</td><td>{E(x.PolicyState is { } s ? Label(s) : "—")}</td></tr>");
            sb.Append("</table>");
        }
        // Resource Stability: providers, components, windows, summaries, policy outcomes, drift and limitations — summaries only, never raw samples.
        if (r.Resources is { Configured: true } res)
        {
            sb.Append($"<h2>Resource Stability</h2><p>Evidence: {E(ResourceFormat.Collection(res.EvidenceState))} · observed {E(Utc(res.ObservationStart))} – {E(Utc(res.ObservationEnd))} every {res.SampleIntervalSeconds} s · warm-up {res.WarmupExcludedSeconds} s excluded{(res.CooldownSeconds > 0 ? $" · cooldown {res.CooldownSeconds} s" : "")} · policy {E(res.PolicyFingerprint)}</p>");
            foreach (var c in res.Components)
            {
                sb.Append($"<h3>{E(c.DisplayName)} ({E(c.Role == ResourceComponentRole.LoadGenerator ? "load generator health" : "application component")}, {E(c.ProviderId)})</h3><p>{E(ResourceFormat.Collection(c.CollectionState))} · {c.RawSampleCount} sample(s){(c.Image is null ? "" : $" · {E(c.Image)}")}{(c.MemoryLimitBytes is { } lim ? $" · memory limit {E(ResourceFormat.Format(lim, ResourceMetric.ContainerMemoryBytes))}" : "")}</p>");
                if (c.Summaries.Count == 0) continue;
                sb.Append("<table><tr><th>Metric</th><th>Early steady</th><th>Late steady</th><th>Peak</th><th>Growth</th><th>Trend / min</th><th>Assessment</th></tr>");
                foreach (var s in c.Summaries)
                    sb.Append($"<tr><td>{E(ResourceFormat.Label(s.Metric))}</td><td>{E(ResourceFormat.Format(s.EarlySteadyValue, s.Metric))}</td><td>{E(ResourceFormat.Format(s.LateSteadyValue, s.Metric))}</td><td>{E(ResourceFormat.Format(s.PeakValue, s.Metric))}</td><td>{E(s.AbsoluteGrowth is null ? "—" : ResourceFormat.Format(s.AbsoluteGrowth, s.Metric))}</td><td>{E(s.TrendSlopePerMinute is null ? "—" : $"{ResourceFormat.Format(s.TrendSlopePerMinute, s.Metric)} ({s.TrendConfidence})")}</td><td>{E(ResourceFormat.State(s.AssessmentState))} — {E(s.AssessmentReason)}</td></tr>");
                sb.Append("</table>");
            }
            foreach (var f in res.Findings) sb.Append($"<p><strong>{E(f.Code)}</strong> ({E(f.Severity)}): {E(f.Message)} {E(f.Evidence)}</p>");
            foreach (var l in res.Components.SelectMany(c => c.Summaries.SelectMany(s => s.Limitations).Concat(c.Limitations)).Concat(res.Limitations).Distinct()) sb.Append($"<p>{E(l)}</p>");
            if (r.ResourceDrift is { } rd)
            {
                sb.Append($"<h3>Resource drift</h3><p>{E(rd.State.ToString())}</p>");
                foreach (var n in rd.CompatibilityNotes) sb.Append($"<p>{E(n)}</p>");
                sb.Append("<table><tr><th>Component</th><th>Metric</th><th>Statistic</th><th>Baseline</th><th>This run</th><th>Policy</th></tr>");
                foreach (var x in rd.Deltas)
                    sb.Append($"<tr><td>{E(x.TargetId)}</td><td>{E(ResourceFormat.Label(x.Metric))}</td><td>{E(x.Statistic.ToString())}</td><td>{E(ResourceFormat.Format(x.Reference, x.Metric))}</td><td>{E(ResourceFormat.Format(x.Current, x.Metric))}</td><td>{E(x.PolicyState is { } s ? Label(s) : "—")}</td></tr>");
                sb.Append("</table>");
            }
        }
        sb.Append("<h2>Limitations</h2><ul>");
        foreach (var l in r.Limitations.Prepend(r.StateReason ?? "").Where(l => l.Length > 0)) sb.Append($"<li>{E(l)}</li>");
        sb.Append("</ul>");
        sb.Append($"<h2>Provenance</h2><p>Runtime {E(r.RuntimeId)} {E(r.RuntimeVersion)} · image {E(r.ContainerImage)} {E(r.ImageDigest)} · provider {E(r.ProviderId)} {E(r.ProviderVersion)} · definition v{r.DefinitionVersion} ({E(r.DefinitionFingerprint)}) · window {E(Utc(r.StartedAt))} – {E(Utc(r.FinishedAt))} · metrics {E(r.MetricsSource)} · observability {E(r.Observability)}</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
