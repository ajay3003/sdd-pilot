using System.Globalization;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests.Resources;

/// <summary>The time windows of one run: when the workload ran, what is excluded as warm-up, and the post-load cooldown.</summary>
public sealed record ResourceWindows(DateTimeOffset WorkloadStart, DateTimeOffset WorkloadEnd, int WarmupExclusionSeconds, int CooldownSeconds)
{
    public DateTimeOffset SteadyStart => WorkloadStart.AddSeconds(WarmupExclusionSeconds);
}

/// <summary>
/// Deterministic Resource Stability analysis. Robust statistics (medians of steady-state thirds, 95th percentile) instead of single samples; warm-up
/// excluded; least-squares slope with R² confidence; explicit policies required for Regression; short or interrupted evidence is InsufficientEvidence
/// or NotComparable. It never concludes "memory leak" and never assigns a root cause.
/// </summary>
public static class ResourceStabilityAnalyzer
{
    /// <summary>Fewer steady-state points than this cannot support any trend statement (with or without a policy).</summary>
    public const int MinimumSteadySamples = 6;
    public const double LoadGeneratorSaturationCpuPercent = 90;

    public static readonly ResourceMetric[] Metrics = Enum.GetValues<ResourceMetric>();

    /// <summary>The metric value of a sample. Rate metrics (Gen 2 collections, GC pause) are deltas against the previous sample of the same instance.</summary>
    public static double? Value(ResourceSample s, ResourceMetric m, ResourceSample? previous = null)
    {
        var mem = s.Memory; var cpu = s.Cpu;
        double? PerMinute(double? now, double? before)
        {
            if (previous is null || now is null || before is null || previous.InstanceId != s.InstanceId) return null;
            var minutes = (s.At - previous.At).TotalMinutes;
            return minutes <= 0 || now < before ? null : (now - before) / minutes;
        }
        return m switch
        {
            ResourceMetric.ContainerMemoryBytes => mem?.ContainerMemoryBytes,
            ResourceMetric.WorkingSetBytes => mem?.WorkingSetBytes,
            ResourceMetric.PrivateBytes => mem?.PrivateBytes,
            ResourceMetric.ManagedHeapBytes => mem?.ManagedHeapBytes,
            ResourceMetric.GcHeapAfterGcBytes => mem?.GcHeapAfterGcBytes,
            ResourceMetric.LohBytes => mem?.LohBytes,
            ResourceMetric.AllocationRateBytesPerSecond => mem?.AllocationRateBytesPerSecond,
            ResourceMetric.Gen2CollectionsPerMinute => PerMinute(mem?.Gen2Collections, previous?.Memory?.Gen2Collections),
            ResourceMetric.GcPauseMsPerMinute => PerMinute(mem?.GcPauseMs, previous?.Memory?.GcPauseMs),
            ResourceMetric.CpuPercent => cpu?.CpuPercent,
            ResourceMetric.ThreadCount => cpu?.ThreadCount,
            _ => cpu?.HandleCount,
        };
    }

    public static List<(DateTimeOffset At, double Value)> Series(IReadOnlyList<ResourceSample> samples, ResourceMetric m)
    {
        var list = new List<(DateTimeOffset, double)>();
        for (var i = 0; i < samples.Count; i++)
            if (Value(samples[i], m, i > 0 ? samples[i - 1] : null) is { } v && double.IsFinite(v)) list.Add((samples[i].At, v));
        return list;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(p / 100 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    /// <summary>Least-squares slope (per minute) and R² over (time, value).</summary>
    public static (double Slope, double R2) Trend(IReadOnlyList<(DateTimeOffset At, double Value)> points)
    {
        if (points.Count < 2) return (0, 0);
        var t0 = points[0].At;
        var xs = points.Select(p => (p.At - t0).TotalMinutes).ToList();
        var ys = points.Select(p => p.Value).ToList();
        double mx = xs.Average(), my = ys.Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < xs.Count; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sxx += (xs[i] - mx) * (xs[i] - mx); syy += (ys[i] - my) * (ys[i] - my); }
        if (sxx == 0) return (0, 0);
        var slope = sxy / sxx;
        var r2 = syy == 0 ? 0 : sxy * sxy / (sxx * syy);
        return (slope, r2);
    }

    private static string Confidence(double r2) => r2 >= 0.7 ? "High" : r2 >= 0.4 ? "Medium" : "Low";

    /// <summary>Summary and assessment of one metric of one component.</summary>
    public static ResourceStabilitySummary Summarize(ResourceComponentObservation component, ResourceMetric metric, ResourceWindows w, IReadOnlyCollection<ResourceStabilityPolicy> policies)
    {
        var series = Series(component.Samples, metric);
        var summary = new ResourceStabilitySummary { TargetId = component.TargetId, Metric = metric, WarmupExcludedSeconds = w.WarmupExclusionSeconds };
        if (series.Count == 0)
            return summary with { AssessmentState = ResourceAssessmentState.Unavailable, AssessmentReason = $"{ResourceFormat.Label(metric)} was not reported by {component.ProviderId}; it is unavailable, not zero." };

        var loaded = series.Where(p => p.At <= w.WorkloadEnd).ToList();
        var steady = loaded.Where(p => p.At >= w.SteadyStart).ToList();
        var cooldown = series.Where(p => p.At > w.WorkloadEnd).Select(p => p.Value).ToList();
        var steadyValues = steady.Select(p => p.Value).ToList();
        var steadySeconds = steady.Count < 2 ? 0 : (steady[^1].At - steady[0].At).TotalSeconds;
        summary = summary with
        {
            ObservationStart = series[0].At, ObservationEnd = series[^1].At, SampleCount = series.Count, SteadySampleCount = steady.Count, SteadyObservationSeconds = Math.Round(steadySeconds, 1),
            StartValue = series[0].Value, PeakValue = (loaded.Count > 0 ? loaded : series).Max(p => p.Value), EndValue = (loaded.Count > 0 ? loaded : series)[^1].Value,
            CooldownValue = cooldown.Count > 0 ? Median(cooldown) : null,
        };
        if (steady.Count >= 3)
        {
            var third = Math.Max(1, steady.Count / 3);
            var early = Median(steadyValues.Take(third).ToList());
            var late = Median(steadyValues.Skip(steady.Count - third).ToList());
            var (slope, r2) = Trend(steady);
            summary = summary with
            {
                EarlySteadyValue = R(early), LateSteadyValue = R(late), SteadyP95Value = R(Percentile(steadyValues, 95)), AbsoluteGrowth = R(late - early),
                RelativeGrowthPercent = early == 0 ? null : Math.Round((late - early) / early * 100, 1), TrendSlopePerMinute = R(slope), TrendConfidence = Confidence(r2),
            };
        }

        var limitations = new List<string>();
        if (component.Downsampled) limitations.Add("Samples were downsampled (means of neighbouring samples) to stay within the storage cap; peaks are maxima of those means.");
        if (metric == ResourceMetric.ManagedHeapBytes)
            limitations.Add("Managed heap rises between collections and drops after them (sawtooth); the windows' medians are compared, never a single peak. The post-GC floor is the stronger retention signal.");
        if (metric == ResourceMetric.ContainerMemoryBytes && !component.AvailableMetrics.Contains(ResourceMetric.ManagedHeapBytes))
            limitations.Add("Only container-level memory was available. Managed .NET heap metrics were not available, so BirkNext cannot determine whether observed growth came from the managed heap.");
        summary = summary with { Limitations = limitations };

        // A component that restarted or changed instance during the loaded window is two processes, not one trend.
        var discontinuity = component.Discontinuities.FirstOrDefault(d => d.At >= w.WorkloadStart && d.At <= w.WorkloadEnd);
        if (discontinuity is not null)
            return summary with { AssessmentState = ResourceAssessmentState.NotComparable,
                AssessmentReason = $"The component changed instance during the workload ({discontinuity.Reason}); samples before and after are not one continuous process." };
        if (component.Role == ResourceComponentRole.LoadGenerator)
            return summary with { AssessmentState = ResourceAssessmentState.NotAssessed, AssessmentReason = "Load-generator health — never assessed as application resource stability." };

        var applicable = policies.Where(p => p.Metric == metric && (p.TargetComponentId is null || p.TargetComponentId == component.TargetId)).ToList();
        if (steady.Count < 3 || summary.EarlySteadyValue is null)
            return summary with { AssessmentState = applicable.Count == 0 ? ResourceAssessmentState.NotAssessed : ResourceAssessmentState.InsufficientEvidence,
                AssessmentReason = $"Only {steady.Count} sample(s) after the {w.WarmupExclusionSeconds} s warm-up exclusion: no steady-state trend can be described." };
        if (applicable.Count == 0)
            return summary with { AssessmentState = ResourceAssessmentState.NotAssessed,
                AssessmentReason = "No resource policy for this metric: the trend is shown descriptively and is neither passed nor failed." };

        var results = applicable.Select(p => Evaluate(p, summary)).ToList();
        var insufficient = results.Where(r => r.Insufficient).ToList();
        var evaluated = results.Where(r => !r.Insufficient).ToList();
        ResourceAssessmentState state; string reason;
        if (evaluated.Any(r => r.Result.Outcome == CheckOutcome.Fail))
        { state = ResourceAssessmentState.Regression; reason = "A required resource policy was violated on sufficient evidence. This is a policy violation, not a proven leak or a known root cause."; }
        else if (evaluated.Any(r => r.Result.Outcome == CheckOutcome.Warning) || insufficient.Any(r => r.WouldExceed))
        {
            state = ResourceAssessmentState.PotentialRegression;
            reason = insufficient.Any(r => r.WouldExceed)
                ? "The observed growth exceeds a policy, but the observation is shorter or sparser than that policy requires: needs investigation with a longer run."
                : "An advisory resource policy was exceeded: needs investigation.";
        }
        else if (insufficient.Count > 0)
        { state = ResourceAssessmentState.InsufficientEvidence; reason = insufficient[0].Result.Explanation; }
        else if (summary.AbsoluteGrowth > 0 && summary.TrendSlopePerMinute > 0 && summary.TrendConfidence is "High" or "Medium")
        { state = ResourceAssessmentState.IncreasingWithinTolerance; reason = "A rising steady-state trend remains within every configured policy."; }
        else
        { state = ResourceAssessmentState.StableWithinPolicy; reason = "Steady-state values stayed within every configured policy for this scenario and environment."; }
        return summary with { PolicyResults = results.Select(r => r.Result).ToList(), AssessmentState = state, AssessmentReason = reason };
    }

    private sealed record Evaluation(ResourcePolicyResult Result, bool Insufficient, bool WouldExceed);

    private static Evaluation Evaluate(ResourceStabilityPolicy p, ResourceStabilitySummary s)
    {
        var rules = new List<(string Rule, double? Observed, double Allowed)>();
        if (p.AllowedRelativeGrowthPercent is { } rel) rules.Add(($"steady-state growth ≤ {N(rel)} %", s.RelativeGrowthPercent, rel));
        if (p.AllowedAbsoluteGrowth is { } abs) rules.Add(($"steady-state growth ≤ {ResourceFormat.Format(abs, p.Metric)}", s.AbsoluteGrowth, abs));
        if (p.AllowedSlopePerMinute is { } slope) rules.Add(($"trend ≤ {ResourceFormat.Format(slope, p.Metric)} per minute", s.TrendSlopePerMinute, slope));
        if (p.MaxValue is { } max) rules.Add(($"steady-state p95 ≤ {ResourceFormat.Format(max, p.Metric)}", s.SteadyP95Value, max));
        var text = string.Join("; ", rules.Select(r => r.Rule));
        if (rules.Count == 0)
            return new(new ResourcePolicyResult { PolicyId = p.Id, Metric = p.Metric, Rule = "(no limit set)", Severity = p.Severity, Explanation = "The policy sets no limit." }, false, false);
        var exceeded = rules.Where(r => r.Observed is { } o && o > r.Allowed).ToList();
        var minSamples = Math.Max(MinimumSteadySamples, p.MinimumSampleCount ?? MinimumSteadySamples);
        var minSeconds = p.MinimumObservationSeconds ?? 0;
        var first = exceeded.FirstOrDefault();
        if (s.SteadySampleCount < minSamples || s.SteadyObservationSeconds < minSeconds)
            return new(new ResourcePolicyResult
            {
                PolicyId = p.Id, Metric = p.Metric, Rule = text, Severity = p.Severity, Outcome = CheckOutcome.NotAssessed, Observed = first.Observed, Allowed = exceeded.Count > 0 ? first.Allowed : null,
                Explanation = $"Insufficient evidence: {s.SteadySampleCount} steady-state sample(s) over {N(s.SteadyObservationSeconds)} s; the policy needs at least {minSamples} sample(s)"
                    + (minSeconds > 0 ? $" over {minSeconds} s." : "."),
            }, true, exceeded.Count > 0);
        if (rules.All(r => r.Observed is null))
            return new(new ResourcePolicyResult { PolicyId = p.Id, Metric = p.Metric, Rule = text, Severity = p.Severity, Outcome = CheckOutcome.NotAssessed,
                Explanation = "The statistics the policy needs are not available (for example a zero early value for a relative limit)." }, false, false);
        if (exceeded.Count == 0)
            return new(new ResourcePolicyResult { PolicyId = p.Id, Metric = p.Metric, Rule = text, Severity = p.Severity, Outcome = CheckOutcome.Pass,
                Explanation = $"{ResourceFormat.Label(p.Metric)} stayed within {text}." }, false, false);
        return new(new ResourcePolicyResult
        {
            PolicyId = p.Id, Metric = p.Metric, Rule = text, Severity = p.Severity, Observed = first.Observed, Allowed = first.Allowed,
            Outcome = p.Severity == ThresholdSeverity.Required ? CheckOutcome.Fail : CheckOutcome.Warning,
            Explanation = $"{ResourceFormat.Label(p.Metric)} exceeded {first.Rule} (observed {Observed(first.Observed, first.Rule, p.Metric)}).",
        }, false, true);
    }

    private static string Observed(double? v, string rule, ResourceMetric m) => rule.Contains('%') && !rule.Contains("p95") ? $"{N(v ?? 0)} %" : ResourceFormat.Format(v, m);

    /// <summary>Assesses every component separately (nothing is summed) and derives findings. Never a "MemoryLeak" finding, never a root cause.</summary>
    public static ResourceStabilityAssessment Assess(ResourceStabilityAssessment collected, ResourceObservationConfiguration config, ResourceWindows w)
    {
        var components = collected.Components.Select(c =>
        {
            if (c.CollectionState is ResourceCollectionState.ProviderUnavailable or ResourceCollectionState.TargetNotFound or ResourceCollectionState.CollectionFailed && c.Samples.Count == 0)
                return c with { Summaries = [] };
            var metrics = Metrics.Where(m => Series(c.Samples, m).Count > 0).ToList();
            var policyMetrics = c.Role == ResourceComponentRole.Target
                ? config.Policies.Where(p => p.TargetComponentId is null || p.TargetComponentId == c.TargetId).Select(p => p.Metric) : [];
            var summarized = metrics.Concat(policyMetrics).Distinct().Select(m => Summarize(c, m, w, config.Policies)).ToList();
            return c with { Summaries = summarized, AvailableMetrics = metrics, UnavailableMetrics = policyMetrics.Except(metrics).Distinct().ToList() };
        }).ToList();

        var findings = new List<ResourceFinding>();
        foreach (var c in components)
        {
            foreach (var d in c.Discontinuities)
                findings.Add(new("TargetRestartedDuringTest", "NeedsReview", c.TargetId, null,
                    $"{c.DisplayName} changed instance at {d.At:HH:mm:ss} UTC ({d.Reason}); resource evidence before and after is not compared as one process.", $"{d.PreviousInstance} → {d.NewInstance}"));
            foreach (var s in c.Summaries)
            {
                var violated = s.PolicyResults.FirstOrDefault(r => r.Outcome is CheckOutcome.Fail or CheckOutcome.Warning) ?? s.PolicyResults.FirstOrDefault(r => r.Observed is not null && r.Outcome == CheckOutcome.NotAssessed);
                if (s.AssessmentState is ResourceAssessmentState.Regression or ResourceAssessmentState.PotentialRegression && violated is not null)
                {
                    var code = s.Metric switch
                    {
                        ResourceMetric.GcHeapAfterGcBytes => "ManagedHeapFloorRegression",
                        ResourceMetric.AllocationRateBytesPerSecond => "AllocationRateRegression",
                        _ when violated.Rule.Contains("p95") => "ResourceThresholdExceeded",
                        _ => "ResourceGrowthExceededPolicy",
                    };
                    findings.Add(new(code, s.AssessmentState == ResourceAssessmentState.Regression ? "Regression" : "PotentialRegression", c.TargetId, s.Metric,
                        $"{c.DisplayName}: {violated.Explanation} {(s.AssessmentState == ResourceAssessmentState.PotentialRegression ? "Needs investigation." : "")}".Trim(),
                        $"Early steady {ResourceFormat.Format(s.EarlySteadyValue, s.Metric)}, late steady {ResourceFormat.Format(s.LateSteadyValue, s.Metric)}, trend {ResourceFormat.Format(s.TrendSlopePerMinute, s.Metric)}/min ({s.TrendConfidence ?? "n/a"}), {s.SteadySampleCount} samples."));
                }
                if (s.AssessmentState == ResourceAssessmentState.InsufficientEvidence)
                    findings.Add(new("ResourceEvidenceInsufficient", "NotAssessed", c.TargetId, s.Metric, $"{c.DisplayName}: {s.AssessmentReason}", $"{s.SteadySampleCount} steady samples"));
            }
            if (c.Role == ResourceComponentRole.LoadGenerator && c.Summaries.FirstOrDefault(s => s.Metric == ResourceMetric.CpuPercent) is { LateSteadyValue: { } cpu } && cpu >= LoadGeneratorSaturationCpuPercent)
                findings.Add(new("LoadGeneratorSaturationPossible", "NeedsReview", c.TargetId, ResourceMetric.CpuPercent,
                    $"The k6 load generator used {N(cpu)} % CPU in late steady state; generator saturation may limit the workload. This is generator health, not target behaviour.", "Late steady CPU"));
        }
        var states = components.Where(c => c.Role == ResourceComponentRole.Target).Select(c => c.CollectionState).ToList();
        var evidence = states.Count == 0 ? ResourceCollectionState.NotConfigured
            : states.All(s => s == ResourceCollectionState.Collected) ? ResourceCollectionState.Collected
            : states.Any(s => s is ResourceCollectionState.Collected or ResourceCollectionState.PartialEvidence) ? ResourceCollectionState.PartialEvidence
            : states.Distinct().Count() == 1 ? states[0] : ResourceCollectionState.ProviderUnavailable;
        var limitations = collected.Limitations.ToList();
        limitations.Add("Resource Stability is evidence for this scenario, environment and duration. Growth is not proof of a memory leak, stability is not proof of its absence, and no root cause is inferred.");
        if (components.Any(c => c.Role == ResourceComponentRole.Target && c.Summaries.All(s => s.SteadySampleCount < MinimumSteadySamples)))
            limitations.Add("Some components have too few steady-state samples for a trend; longer controlled (soak) runs give stronger evidence.");
        return collected with { Components = components, Findings = findings, EvidenceState = evidence, Limitations = limitations.Distinct().ToList(), InProgress = false,
            WarmupExcludedSeconds = w.WarmupExclusionSeconds, CooldownSeconds = w.CooldownSeconds, WorkloadStart = w.WorkloadStart, WorkloadEnd = w.WorkloadEnd };
    }

    public static readonly ResourceStatistic[] DriftStatistics = [ResourceStatistic.LateSteady, ResourceStatistic.Peak, ResourceStatistic.Growth, ResourceStatistic.SlopePerMinute];

    private static double? Stat(ResourceStabilitySummary s, ResourceStatistic st) => st switch
    {
        ResourceStatistic.LateSteady => s.LateSteadyValue,
        ResourceStatistic.Peak => s.PeakValue,
        ResourceStatistic.Growth => s.AbsoluteGrowth,
        _ => s.TrendSlopePerMinute,
    };

    /// <summary>
    /// Resource drift of a run against a baseline run, per component and metric. Requires the run-level comparison to be compatible and each component
    /// to be comparable (same provider, memory limit, no discontinuity). Without drift policies the deltas are descriptive (NotAssessed).
    /// </summary>
    public static ResourceDriftAssessment? Drift(PerformanceTestRun current, PerformanceTestRun baselineRun, string? baselineId, IReadOnlyCollection<ResourceDriftPolicy> policies)
    {
        if (current.Resources is not { Configured: true } now || baselineRun.Resources is not { Configured: true } before) return null;
        var notes = PerformanceTestRules.CompatibilityNotes(current, baselineRun);
        var deltas = new List<ResourceMetricDelta>();
        var findings = new List<ResourceFinding>();
        foreach (var c in now.Components.Where(c => c.Role == ResourceComponentRole.Target))
        {
            var b = before.Components.FirstOrDefault(x => x.TargetId == c.TargetId && x.Role == c.Role);
            if (b is null) { notes.Add($"{c.DisplayName}: not observed in the baseline run."); continue; }
            if (b.ProviderId != c.ProviderId) { notes.Add($"{c.DisplayName}: provider differs from the baseline ({b.ProviderId} → {c.ProviderId})."); continue; }
            if (b.MemoryLimitBytes != c.MemoryLimitBytes) notes.Add($"{c.DisplayName}: memory limit changed ({ResourceFormat.Format(b.MemoryLimitBytes, ResourceMetric.ContainerMemoryBytes)} → {ResourceFormat.Format(c.MemoryLimitBytes, ResourceMetric.ContainerMemoryBytes)}); sizing differs.");
            foreach (var s in c.Summaries.Where(s => s.AssessmentState is not (ResourceAssessmentState.Unavailable or ResourceAssessmentState.NotComparable)))
            {
                var r = b.Summaries.FirstOrDefault(x => x.Metric == s.Metric && x.AssessmentState is not (ResourceAssessmentState.Unavailable or ResourceAssessmentState.NotComparable));
                foreach (var st in DriftStatistics)
                {
                    var (refValue, curValue) = (r is null ? null : Stat(r, st), Stat(s, st));
                    var delta = new ResourceMetricDelta { TargetId = c.TargetId, Metric = s.Metric, Statistic = st, Reference = refValue, Current = curValue };
                    if (refValue is { } rv && curValue is { } cv)
                    {
                        var abs = R(cv - rv);
                        double? rel = rv == 0 ? null : Math.Round((cv - rv) / Math.Abs(rv) * 100, 1);
                        var unchanged = abs == 0 || rel is { } p && Math.Abs(p) < PerformanceTestRules.UnchangedTolerancePercent;
                        delta = delta with { AbsoluteDelta = abs, RelativePercent = rel, Direction = unchanged ? MetricChangeDirection.Unchanged : cv < rv ? MetricChangeDirection.Improved : MetricChangeDirection.Worse };
                    }
                    deltas.Add(delta);
                }
            }
        }
        var compatible = notes.Count == 0;
        var assessment = new ResourceDriftAssessment { BaselineRunId = baselineRun.RunId, BaselineId = baselineId, Compatible = compatible, CompatibilityNotes = notes };
        if (!compatible) return assessment with { Deltas = deltas, State = PerformanceDriftState.NotComparable };
        if (policies.Count == 0) return assessment with { Deltas = deltas, State = PerformanceDriftState.NotAssessed };
        var evaluated = deltas.Select(d =>
        {
            var policy = policies.FirstOrDefault(p => p.Metric == d.Metric && p.Statistic == d.Statistic && (p.TargetComponentId is null || p.TargetComponentId == d.TargetId));
            if (policy is null || d.Reference is not { } r || d.Current is not { } c) return d;
            var worse = c - r;
            double? relWorse = r == 0 ? null : worse / Math.Abs(r) * 100;
            var violated = (policy.AllowedRelativeChangePercent is { } rel && relWorse is { } rw && rw > rel) || (policy.AllowedAbsoluteChange is { } a && worse > a);
            var text = string.Join(" or ", new[]
            {
                policy.AllowedRelativeChangePercent is { } x ? $"+{N(x)} %" : null,
                policy.AllowedAbsoluteChange is { } y ? $"+{ResourceFormat.Format(y, d.Metric)}" : null,
            }.Where(t => t is not null));
            var state = violated ? PerformanceDriftState.Regression : d.Direction switch
            {
                MetricChangeDirection.Improved => PerformanceDriftState.Improved,
                MetricChangeDirection.Worse => PerformanceDriftState.DegradedWithinTolerance,
                _ => PerformanceDriftState.Stable,
            };
            if (violated)
            {
                var name = now.Components.First(x => x.TargetId == d.TargetId).DisplayName;
                findings.Add(new(d.Metric == ResourceMetric.GcHeapAfterGcBytes ? "ManagedHeapFloorRegression" : d.Metric == ResourceMetric.AllocationRateBytesPerSecond ? "AllocationRateRegression" : "SteadyStateMemoryRegression",
                    policy.Severity == ThresholdSeverity.Required ? "Regression" : "PotentialRegression", d.TargetId, d.Metric,
                    $"{name}: {StatLabel(d.Statistic).ToLowerInvariant()} {ResourceFormat.Label(d.Metric).ToLowerInvariant()} changed {(relWorse is { } pct ? $"{N(pct)} %" : ResourceFormat.Format(worse, d.Metric))} from the baseline and exceeded the accepted drift ({text}).",
                    $"Baseline {ResourceFormat.Format(r, d.Metric)}, current {ResourceFormat.Format(c, d.Metric)}."));
            }
            return d with { PolicyState = state, PolicyText = $"Accepted change {text}" };
        }).ToList();
        var states = evaluated.Where(d => d.PolicyState is not null).Select(d => d.PolicyState!.Value).ToList();
        var overall = states.Count == 0 ? PerformanceDriftState.NotAssessed
            : states.Contains(PerformanceDriftState.Regression) ? PerformanceDriftState.Regression
            : states.Contains(PerformanceDriftState.DegradedWithinTolerance) ? PerformanceDriftState.DegradedWithinTolerance
            : states.Contains(PerformanceDriftState.Improved) ? PerformanceDriftState.Improved : PerformanceDriftState.Stable;
        return assessment with { Deltas = evaluated, State = overall, Findings = findings };
    }

    public static string StatLabel(ResourceStatistic s) => s switch
    {
        ResourceStatistic.LateSteady => "Late steady-state",
        ResourceStatistic.Peak => "Peak",
        ResourceStatistic.Growth => "Steady-state growth",
        _ => "Trend per minute",
    };

    /// <summary>Policy outcomes that count in the shared quality score: only evaluable ones (evidence gaps are coverage, never failure).</summary>
    public static IEnumerable<CheckOutcome> QualityOutcomes(ResourceStabilityAssessment? a) =>
        a?.Components.SelectMany(c => c.Summaries).SelectMany(s => s.PolicyResults).Select(r => r.Outcome).Where(o => o != CheckOutcome.NotAssessed) ?? [];

    private static double R(double v) => Math.Round(v, 3);
    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
