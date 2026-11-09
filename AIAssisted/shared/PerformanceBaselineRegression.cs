using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Applicability;

namespace BirkNext.PerformanceTests;

// Baseline approval, comparability and regression over persisted performance runs. Pure and deterministic: the backend is authoritative, the UI
// previews with the same rules. Thresholds (absolute expectations of one run) and regressions (change against an approved baseline) stay separate.

/// <summary>Comparable = same measurement; WithWarnings = same measurement, context differs (e.g. application or runtime version); NotComparable =
/// the measurement itself differs (workload, scenario, environment, …): differences are shown, no regression verdict; InsufficientEvidence = a run
/// lacks complete metrics.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComparabilityLevel { Comparable, ComparableWithWarnings, NotComparable, InsufficientEvidence }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComparabilitySeverity { Blocking, Warning, Missing }

public sealed record ComparabilityDifference(string Dimension, string Reference, string Current, ComparabilitySeverity Severity, string Explanation);

public sealed record PerformanceComparability
{
    public ComparabilityLevel Level { get; init; }
    public List<ComparabilityDifference> Differences { get; init; } = [];
    public string Summary { get; init; } = "";
}

/// <summary>ConfirmedRegression needs repeated comparable runs that agree; a single comparison is at most PotentialRegression. Inconclusive = the
/// evidence cannot support a verdict (not comparable, too few samples, missing metric, runs vary more than the tolerance). NotAssessed = no tolerance
/// configured for the metric (the change is shown, not judged).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RegressionVerdict { ConfirmedRegression, PotentialRegression, Stable, Improved, Inconclusive, NotAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BaselineApprovalState { Proposed, Approved, Rejected }

/// <param name="Blocking">A failed blocking check makes the run ineligible; a non-blocking one needs an explicit, recorded acceptance.</param>
public sealed record BaselineEligibilityCheck(string Key, string Label, bool Passed, bool Blocking, string Detail);

public sealed record MetricRegression
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Unit { get; init; } = "";
    public bool LowerIsBetter { get; init; } = true;
    public double? Baseline { get; init; }
    /// <summary>The current value: the run's value for one run, the median of the per-run values for several (percentiles are never averaged).</summary>
    public double? Current { get; init; }
    public double? CurrentMin { get; init; }
    public double? CurrentMax { get; init; }
    public int Runs { get; init; }
    /// <summary>Runs whose value is beyond the tolerance (worse).</summary>
    public int RunsBeyondTolerance { get; init; }
    public double? AbsoluteDelta { get; init; }
    /// <summary>Null when the baseline is 0 or a value is missing.</summary>
    public double? RelativePercent { get; init; }
    public MetricChangeDirection Direction { get; init; }
    /// <summary>"+20 % or +50 ms"; null = no tolerance configured.</summary>
    public string? Tolerance { get; init; }
    public RegressionVerdict Verdict { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>How <see cref="Current"/> was derived, e.g. "median of 3 per-run P95 values".</summary>
    public string Aggregation { get; init; } = "";
}

public sealed record RunThresholdStatus(Guid RunId, PerformanceQualityVerdict Verdict, int Failed, int Warnings);

public sealed record PerformanceRegressionReport
{
    public string BaselineId { get; init; } = "";
    public int BaselineVersion { get; init; }
    public Guid BaselineRunId { get; init; }
    public BaselineApprovalState? BaselineApproval { get; init; }
    public List<Guid> CurrentRunIds { get; init; } = [];
    /// <summary>The weakest comparability of the current runs against the baseline run.</summary>
    public PerformanceComparability Comparability { get; init; } = new();
    /// <summary>True when the runs are not comparable: the deltas are exploratory, never a regression verdict.</summary>
    public bool Exploratory { get; init; }
    public List<MetricRegression> Metrics { get; init; } = [];
    public RegressionVerdict Verdict { get; init; }
    public string VerdictReason { get; init; } = "";
    /// <summary>Threshold results of the current runs — reported beside, never merged into, the regression verdict.</summary>
    public List<RunThresholdStatus> Thresholds { get; init; } = [];
    public List<string> Derivation { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
}

public sealed record PerformanceTrendPoint
{
    public Guid RunId { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? VersionLabel { get; init; }
    public int DefinitionVersion { get; init; }
    public PerformanceRunState State { get; init; }
    public bool IsBaselineRun { get; init; }
    /// <summary>Against the baseline run (null without a baseline). Only Comparable / ComparableWithWarnings points belong on the baseline's line.</summary>
    public ComparabilityLevel? Comparability { get; init; }
    public PerformanceQualityVerdict ThresholdVerdict { get; init; }
    public Dictionary<string, double?> Values { get; init; } = [];
}

public sealed record PerformanceTrend
{
    public string DefinitionId { get; init; } = "";
    public string? BaselineId { get; init; }
    public Guid? BaselineRunId { get; init; }
    public List<string> MetricKeys { get; init; } = [];
    public List<PerformanceTrendPoint> Points { get; init; } = [];
    /// <summary>Points left off the baseline line because they are not comparable or lack complete metrics (listed, never mixed in silently).</summary>
    public int ExcludedPoints { get; init; }
}

public sealed record PerformanceBaselineProposal
{
    public Guid RunId { get; init; }
    public string? Name { get; init; }
    public string? Reason { get; init; }
    /// <summary>Explicit, recorded acceptance of non-blocking eligibility failures (failed required thresholds, dropped iterations).</summary>
    public bool AcceptWarnings { get; init; }
}

public sealed record PerformanceBaselineDecision
{
    /// <summary>Required to reject; optional note when approving.</summary>
    public string? Reason { get; init; }
}

public sealed record PerformanceBaselineDecisionResult(PerformanceBaseline? Baseline, PerformanceBaseline? Superseded, string? Error);

public sealed record PerformanceRunDeletion
{
    /// <summary>Must equal 1: the run is deleted only with an explicit confirmation.</summary>
    public int Confirm { get; init; }
}

public static class PerformanceRegressionRules
{
    /// <summary>Repeated comparable runs needed before a regression is called Confirmed.</summary>
    public const int MinRunsForConfirmation = 3;
    /// <summary>Requests a run needs before its percentile is meaningful: below it the percentile is an extreme value of a handful of samples.</summary>
    public static int MinRequestsFor(string key) => key switch { "p99" => 100, "p95" => 20, "p90" => 10, "p50" => 2, _ => 1 };

    public static readonly string[] RegressionKeys = ["p50", "p90", "p95", "p99", "throughput", "errorRate", "graphqlErrorRate", "droppedIterationRate", "requests"];

    public static PerformanceMetric? MetricOf(string key) => key switch
    {
        "p50" => PerformanceMetric.LatencyP50Ms, "p90" => PerformanceMetric.LatencyP90Ms, "p95" => PerformanceMetric.LatencyP95Ms, "p99" => PerformanceMetric.LatencyP99Ms,
        "throughput" => PerformanceMetric.ThroughputRps, "errorRate" => PerformanceMetric.ErrorRatePercent, _ => null,
    };

    public static string Label(string key) => key switch
    {
        "graphqlErrorRate" => "GraphQL error rate", "droppedIterationRate" => "Dropped iteration rate", "requests" => "Requests",
        _ => MetricOf(key) is { } m ? PerformanceTestRules.Label(m) : key,
    };

    public static string Unit(string key) => key switch
    {
        "graphqlErrorRate" or "droppedIterationRate" => "%", "requests" => "requests",
        _ => MetricOf(key) is { } m ? PerformanceTestRules.Unit(m) : "",
    };

    public static bool LowerIsBetter(string key) => key is not ("throughput" or "requests");

    /// <summary>The value a run reports for a key; null = not reported (never 0).</summary>
    public static double? Value(PerformanceMetrics? m, string key)
    {
        if (m is null) return null;
        if (MetricOf(key) is { } metric) return m.Value(metric);
        return key switch
        {
            "requests" => m.RequestCount,
            "graphqlErrorRate" => m.GraphQlErrors is { } g && m.RequestCount > 0 ? Math.Round(g * 100.0 / m.RequestCount, 3) : null,
            "droppedIterationRate" => m.DroppedIterations is { } d && m.RequestCount + d > 0 ? Math.Round(d * 100.0 / (m.RequestCount + d), 3) : null,
            _ => null,
        };
    }

    /// <summary>
    /// The value of a run. k6 reports only counters that received samples, so a GraphQL run whose script counted (status counters present) and
    /// reported no GraphQL-error counter had 0 GraphQL errors; for REST runs and older scripts the rate stays not reported.
    /// </summary>
    public static double? Value(PerformanceTestRun run, string key) =>
        key == "graphqlErrorRate" && run.Metrics is { GraphQlErrors: null, StatusCodes: not null, RequestCount: > 0 } && run.DefinitionSnapshot.TargetType == PerformanceTargetType.GraphQlHttp
            ? 0 : Value(run.Metrics, key);

    private static bool Complete(PerformanceTestRun r) => r.State == PerformanceRunState.Completed && !r.MetricsPartial && r.Metrics is not null;

    // ── Baseline eligibility ─────────────────────────────────────────────────────────────────────────────────────────────

    public static List<BaselineEligibilityCheck> Eligibility(PerformanceTestRun run)
    {
        var checks = new List<BaselineEligibilityCheck>
        {
            new("completed", "Run completed", run.State == PerformanceRunState.Completed, true,
                run.State == PerformanceRunState.Completed ? "The run completed." : $"The run ended {run.State}; only a completed run can be a baseline."),
            new("metrics", "Complete metrics", run.Metrics is not null && !run.MetricsPartial, true,
                run.Metrics is null ? "The run has no metrics." : run.MetricsPartial ? "The metrics cover only part of the workload." : "Metrics cover the whole workload."),
            new("requests", "Enough requests", run.Metrics is { RequestCount: > 0 }, true,
                run.Metrics is { RequestCount: > 0 } m ? $"{m.RequestCount} requests measured." : "No request was measured."),
        };
        if (run.Metrics is { } metrics)
        {
            var p95 = metrics.RequestCount >= MinRequestsFor("p95");
            checks.Add(new("samples", "Enough samples for P95", p95, false,
                p95 ? $"{metrics.RequestCount} requests (P95 needs at least {MinRequestsFor("p95")})." : $"Only {metrics.RequestCount} requests: P95 needs at least {MinRequestsFor("p95")}; percentile comparisons against this baseline will be inconclusive."));
            var dropped = metrics.DroppedIterations is null or 0;
            checks.Add(new("dropped", "No dropped iterations", dropped, false,
                dropped ? "The load generator kept up with the planned workload." : $"{metrics.DroppedIterations} iterations were not started: the achieved workload is below the plan."));
        }
        var thresholds = run.Verdict is not PerformanceQualityVerdict.Fail;
        checks.Add(new("thresholds", "Required thresholds met", thresholds, false,
            thresholds ? "No required threshold failed." : "A required threshold failed: approving records this run as the reference anyway."));
        return checks;
    }

    /// <summary>Null = eligible; otherwise why not (blocking failure, or non-blocking failures without explicit acceptance).</summary>
    public static string? EligibilityProblem(IReadOnlyCollection<BaselineEligibilityCheck> checks, bool acceptWarnings)
    {
        if (checks.FirstOrDefault(c => c.Blocking && !c.Passed) is { } blocking) return $"Not eligible: {blocking.Detail}";
        var warnings = checks.Where(c => !c.Blocking && !c.Passed).ToList();
        return warnings.Count > 0 && !acceptWarnings ? $"Needs explicit acceptance: {string.Join(" ", warnings.Select(w => w.Detail))}" : null;
    }

    // ── Comparability ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static PerformanceComparability Comparability(PerformanceTestRun reference, PerformanceTestRun current)
    {
        var d = new List<ComparabilityDifference>();
        void Add(string dimension, string? r, string? c, ComparabilitySeverity s, string why)
        {
            if (string.Equals(r ?? "", c ?? "", StringComparison.Ordinal)) return;
            d.Add(new(dimension, string.IsNullOrEmpty(r) ? "not recorded" : r, string.IsNullOrEmpty(c) ? "not recorded" : c, s, why));
        }
        static string N(double? v) => v?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";

        if (!Complete(reference)) d.Add(new("Reference evidence", reference.State.ToString(), "", ComparabilitySeverity.Missing, "The reference run has no complete metrics."));
        if (!Complete(current)) d.Add(new("Current evidence", "", current.State.ToString(), ComparabilitySeverity.Missing, "This run has no complete metrics."));

        var a = reference.DefinitionSnapshot; var b = current.DefinitionSnapshot;
        Add("Environment", reference.EnvironmentId, current.EnvironmentId, ComparabilitySeverity.Blocking, "Different Target Environment.");
        Add("Environment classification", reference.EnvironmentType, current.EnvironmentType, ComparabilitySeverity.Blocking, "The environment is classified differently.");
        Add("Target", reference.TargetOrigin.ToLowerInvariant(), current.TargetOrigin.ToLowerInvariant(), ComparabilitySeverity.Blocking, "Different target origin.");
        Add("Target type", a.TargetType.ToString(), b.TargetType.ToString(), ComparabilitySeverity.Blocking, "REST and GraphQL scenarios are not the same measurement.");
        Add("Operations", Operations(a), Operations(b), ComparabilitySeverity.Blocking, "The scenario sends a different set of requests.");
        if (Operations(a) == Operations(b) && Json(Measured(a.Scenario.Steps)) != Json(Measured(b.Scenario.Steps)))
            d.Add(new("Scenario details", $"definition v{reference.DefinitionVersion}", $"definition v{current.DefinitionVersion}", ComparabilitySeverity.Blocking,
                "The same operations are sent with different bodies, headers, data or pacing."));
        var wa = a.Workload; var wb = b.Workload;
        Add("Workload type", Mode(wa.Mode), Mode(wb.Mode), ComparabilitySeverity.Blocking, "Fixed virtual users and arrival rate produce different load; never compared.");
        if (wa.Mode == wb.Mode)
        {
            if (wa.Mode == WorkloadMode.VirtualUsers) Add("Virtual users", N(wa.VirtualUsers), N(wb.VirtualUsers), ComparabilitySeverity.Blocking, "Different concurrency.");
            else
            {
                Add("Arrival rate", N(wa.RequestsPerSecond) + " req/s", N(wb.RequestsPerSecond) + " req/s", ComparabilitySeverity.Blocking, "Different planned arrival rate.");
                Add("Start rate", N(wa.StartRequestsPerSecond), N(wb.StartRequestsPerSecond), ComparabilitySeverity.Blocking, "Different ramp start rate.");
                Add("Generator ceiling", N(wa.VirtualUsers), N(wb.VirtualUsers), ComparabilitySeverity.Warning, "Different load-generator pool ceiling.");
            }
            Add("Phases", Phases(wa), Phases(wb), ComparabilitySeverity.Blocking, "Different warm-up, ramp, steady-state or ramp-down durations.");
            Add("Maximum duration", N(wa.MaxDurationSeconds), N(wb.MaxDurationSeconds), ComparabilitySeverity.Warning, "Different duration bound.");
        }
        Add("Test data profile", a.Scenario.TestDataProfileId, b.Scenario.TestDataProfileId, ComparabilitySeverity.Blocking, "Different test data.");
        Add("Authentication", Auth(a), Auth(b), ComparabilitySeverity.Blocking, "Authenticated and unauthenticated requests are not the same measurement.");
        Add("Metrics source", reference.MetricsSource, current.MetricsSource, ComparabilitySeverity.Blocking, "The metrics were produced differently.");
        Add("Load generator", reference.ProviderId + " " + reference.ProviderVersion, current.ProviderId + " " + current.ProviderVersion, ComparabilitySeverity.Warning, "Different load-generator version.");
        Add("Runtime image", reference.ImageDigest ?? reference.ContainerImage, current.ImageDigest ?? current.ContainerImage, ComparabilitySeverity.Warning, "Different runtime image.");
        Add("Generator host", reference.Host?.LogicalProcessors.ToString(CultureInfo.InvariantCulture), current.Host?.LogicalProcessors.ToString(CultureInfo.InvariantCulture),
            ComparabilitySeverity.Warning, "Different load-generator capacity (logical processors).");
        Add("Application version", reference.VersionLabel, current.VersionLabel, ComparabilitySeverity.Warning, "Different application versions — usually the point of the comparison.");
        if (reference.DefinitionVersion != current.DefinitionVersion && reference.ComparisonFingerprint == current.ComparisonFingerprint)
            d.Add(new("Definition version", $"v{reference.DefinitionVersion}", $"v{current.DefinitionVersion}", ComparabilitySeverity.Warning,
                "Thresholds, tolerances or names changed; what is measured did not."));
        foreach (var (run, label) in new[] { (reference, "reference"), (current, "current") })
            if (run.Metrics?.DroppedIterations is > 0)
                d.Add(new("Achieved workload", label, $"{run.Metrics.DroppedIterations} dropped iterations", ComparabilitySeverity.Warning,
                    $"The {label} run did not achieve its planned workload."));

        var level = d.Any(x => x.Severity == ComparabilitySeverity.Missing) ? ComparabilityLevel.InsufficientEvidence
            : d.Any(x => x.Severity == ComparabilitySeverity.Blocking) ? ComparabilityLevel.NotComparable
            : d.Count > 0 ? ComparabilityLevel.ComparableWithWarnings : ComparabilityLevel.Comparable;
        var summary = level switch
        {
            ComparabilityLevel.Comparable => "Comparable: the same measurement under the same conditions.",
            ComparabilityLevel.ComparableWithWarnings => "Comparable with warnings: " + string.Join(" ", d.Select(x => x.Explanation)),
            ComparabilityLevel.NotComparable => "Not comparable: " + string.Join(" ", d.Where(x => x.Severity == ComparabilitySeverity.Blocking).Select(x => x.Explanation)),
            _ => "Insufficient evidence: " + string.Join(" ", d.Where(x => x.Severity == ComparabilitySeverity.Missing).Select(x => x.Explanation)),
        };
        return new PerformanceComparability { Level = level, Differences = d, Summary = summary };
    }

    private static string Mode(WorkloadMode m) => m == WorkloadMode.VirtualUsers ? "Fixed virtual users" : "Arrival rate";
    private static string Phases(PerformanceWorkload w) => $"{w.WarmupSeconds}/{w.RampUpSeconds}/{w.SteadyStateSeconds}/{w.RampDownSeconds} s";
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    /// <summary>What a step sends: its id, discovery bookkeeping and observation source are identity/evidence, not measurement.</summary>
    private static IEnumerable<HttpPerformanceStep> Measured(IEnumerable<HttpPerformanceStep> steps) => steps.Select(s => s with { Id = "", Discovery = null, Source = null });
    private static string Operations(PerformanceTestDefinition d) =>
        string.Join(", ", d.Scenario.Steps.Where(s => !s.Disabled).Select(s => $"{s.Method.ToUpperInvariant()} {s.RelativePath}"));
    private static string Auth(PerformanceTestDefinition d) =>
        (d.AuthenticationReference is null ? "none" : "configured") + (d.Scenario.Steps.Any(s => s.Authentication != StepAuthenticationMode.Inherit)
            ? " · per-step: " + string.Join(",", d.Scenario.Steps.Where(s => !s.Disabled).Select(s => s.Authentication == StepAuthenticationMode.Override ? "override" : s.Authentication.ToString().ToLowerInvariant()))
            : "");

    private static int Rank(ComparabilityLevel l) => l switch
    {
        ComparabilityLevel.Comparable => 0, ComparabilityLevel.ComparableWithWarnings => 1, ComparabilityLevel.NotComparable => 2, _ => 3,
    };

    // ── Regression ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares one or more current runs with an approved baseline. Per metric: the tolerance configured on the definition (no universal default),
    /// per-run violation, the median of the current runs (percentiles are never averaged or pooled), the spread, and a verdict. A not comparable or
    /// incomplete pair gets exploratory deltas and Inconclusive verdicts — never a regression.
    /// </summary>
    public static PerformanceRegressionReport Evaluate(PerformanceBaseline baseline, PerformanceTestRun baselineRun, IReadOnlyList<PerformanceTestRun> currentRuns,
        IReadOnlyCollection<PerformanceDriftPolicy> tolerances)
    {
        var comparabilities = currentRuns.Select(r => Comparability(baselineRun, r)).ToList();
        var worst = comparabilities.Count == 0
            ? new PerformanceComparability { Level = ComparabilityLevel.InsufficientEvidence, Summary = "Insufficient evidence: no current run was chosen." }
            : comparabilities.OrderByDescending(c => Rank(c.Level)).First();
        var exploratory = worst.Level is ComparabilityLevel.NotComparable or ComparabilityLevel.InsufficientEvidence;
        var arrivalRate = baselineRun.DefinitionSnapshot.Workload.Mode == WorkloadMode.ArrivalRate;

        var metrics = RegressionKeys.Select(key =>
        {
            var baselineValue = Value(baselineRun, key);
            var values = currentRuns.Select(r => Value(r, key)).ToList();
            var present = values.Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
            double? current = present.Count == 0 ? null : Median(present);
            var row = new MetricRegression
            {
                Key = key, Label = Label(key), Unit = Unit(key), LowerIsBetter = LowerIsBetter(key), Baseline = baselineValue, Current = current,
                CurrentMin = present.Count > 0 ? present[0] : null, CurrentMax = present.Count > 0 ? present[^1] : null, Runs = currentRuns.Count,
                Aggregation = currentRuns.Count <= 1 ? "single run" : $"median of {present.Count} per-run {Label(key)} values (percentiles are not pooled across runs)",
            };
            if (baselineValue is { } bv && current is { } cv)
            {
                var abs = Math.Round(cv - bv, 3);
                double? rel = bv == 0 ? null : Math.Round((cv - bv) / bv * 100, 1);
                var unchanged = abs == 0 || rel is { } p && Math.Abs(p) < PerformanceTestRules.UnchangedTolerancePercent;
                var better = row.LowerIsBetter ? cv < bv : cv > bv;
                row = row with
                {
                    AbsoluteDelta = abs, RelativePercent = rel,
                    Direction = unchanged ? MetricChangeDirection.Unchanged : better ? MetricChangeDirection.Improved : MetricChangeDirection.Worse,
                };
            }
            else row = row with { Direction = MetricChangeDirection.NotAvailable };

            var policy = MetricOf(key) is { } metric ? tolerances.FirstOrDefault(t => t.Metric == metric) : null;
            row = row with { Tolerance = policy is null ? null : ToleranceText(key, policy) };

            if (exploratory) return row with { Verdict = RegressionVerdict.Inconclusive, Reason = "Exploratory only: " + worst.Summary };
            if (baselineValue is null && present.Count == 0)
                return row with { Verdict = RegressionVerdict.NotAssessed, Reason = "Not measured in these runs." };
            if (baselineValue is null || present.Count < currentRuns.Count)
                return row with { Verdict = RegressionVerdict.Inconclusive, Reason = "The metric was not reported by every run." };
            var minimum = MinRequestsFor(key);
            if (minimum > 1 && (baselineRun.Metrics!.RequestCount < minimum || currentRuns.Any(r => r.Metrics!.RequestCount < minimum)))
                return row with { Verdict = RegressionVerdict.Inconclusive, Reason = $"Too few requests for {Label(key)}: at least {minimum} per run are needed." };
            if (key == "throughput" && arrivalRate)
                return row with { Verdict = RegressionVerdict.NotAssessed, Reason = "Arrival-rate workload: throughput follows the planned rate, so it is not judged." };
            if (policy is null)
                return row with { Verdict = RegressionVerdict.NotAssessed, Reason = "No tolerance configured for this metric: the change is shown, not judged." };
            if (baselineValue == 0 && policy.AllowedAbsoluteChange is null)
                return row with { Verdict = RegressionVerdict.Inconclusive, Reason = "The baseline is 0, so a relative tolerance cannot be applied: configure an absolute tolerance." };

            var lower = row.LowerIsBetter;
            bool Beyond(double v) => Worsening(baselineValue.Value, v, lower, policy);
            bool ImprovedBeyond(double v) => Worsening(v, baselineValue.Value, lower, policy); // the baseline would be "worse" than v by more than the tolerance
            var beyond = present.Count(Beyond);
            row = row with { RunsBeyondTolerance = beyond };
            var n = present.Count;
            if (n >= 2 && policy.AllowedRelativeChangePercent is { } relTol && baselineValue != 0)
            {
                var spread = (present[^1] - present[0]) / Math.Abs(baselineValue.Value) * 100;
                if (spread > 2 * relTol)
                    return row with { Verdict = RegressionVerdict.Inconclusive, Reason = $"The runs vary by {spread:0.#} % of the baseline, more than twice the tolerance: noise, not a verdict." };
            }
            if (Beyond(current!.Value))
            {
                if (n >= MinRunsForConfirmation && beyond * 3 >= n * 2)
                    return row with { Verdict = RegressionVerdict.ConfirmedRegression, Reason = $"{beyond} of {n} comparable runs are beyond the tolerance and so is their median." };
                return row with
                {
                    Verdict = RegressionVerdict.PotentialRegression,
                    Reason = n < MinRunsForConfirmation
                        ? $"Beyond the tolerance in {(n == 1 ? "a single run" : $"{n} runs")}; {MinRunsForConfirmation} agreeing comparable runs confirm a regression."
                        : $"The median is beyond the tolerance but only {beyond} of {n} runs are.",
                };
            }
            if (present.All(ImprovedBeyond)) return row with { Verdict = RegressionVerdict.Improved, Reason = n == 1 ? "Better than the baseline by more than the tolerance (single run)." : $"All {n} runs are better than the baseline by more than the tolerance." };
            return row with { Verdict = RegressionVerdict.Stable, Reason = "Within the tolerance." };
        }).ToList();

        // Only metrics with a tolerance are judged; a metric without one never makes the overall verdict Inconclusive, Improved or Stable.
        var judged = metrics.Where(m => m.Tolerance is not null).ToList();
        var verdicts = judged.Select(m => m.Verdict).ToList();
        var (verdict, reason) = exploratory ? (RegressionVerdict.Inconclusive, worst.Summary)
            : verdicts.Contains(RegressionVerdict.ConfirmedRegression) ? (RegressionVerdict.ConfirmedRegression, Join(judged, RegressionVerdict.ConfirmedRegression))
            : verdicts.Contains(RegressionVerdict.PotentialRegression) ? (RegressionVerdict.PotentialRegression, Join(judged, RegressionVerdict.PotentialRegression))
            : verdicts.All(v => v == RegressionVerdict.NotAssessed) ? (RegressionVerdict.NotAssessed, "No tolerance is configured for a measured metric: the changes are shown, not judged.")
            : verdicts.Contains(RegressionVerdict.Inconclusive) ? (RegressionVerdict.Inconclusive, Join(judged, RegressionVerdict.Inconclusive))
            : verdicts.Contains(RegressionVerdict.Improved) ? (RegressionVerdict.Improved, Join(judged, RegressionVerdict.Improved))
            : (RegressionVerdict.Stable, "Every judged metric is within its tolerance.");

        var limitations = new List<string>();
        if (currentRuns.Count < MinRunsForConfirmation)
            limitations.Add($"{currentRuns.Count} current run(s): a regression can be at most Potential until {MinRunsForConfirmation} comparable runs agree.");
        if (worst.Level == ComparabilityLevel.ComparableWithWarnings) limitations.Add(worst.Summary);
        limitations.Add("Confidence intervals are not computed: runs report summary percentiles, not the underlying samples.");

        return new PerformanceRegressionReport
        {
            BaselineId = baseline.BaselineId, BaselineVersion = baseline.Version, BaselineRunId = baseline.RunId, BaselineApproval = baseline.ApprovalOrLegacy(),
            CurrentRunIds = [.. currentRuns.Select(r => r.RunId)], Comparability = worst, Exploratory = exploratory, Metrics = metrics, Verdict = verdict, VerdictReason = reason,
            Thresholds = [.. currentRuns.Select(r => new RunThresholdStatus(r.RunId, r.Verdict,
                r.ThresholdResults.Count(t => t.Outcome == CheckOutcome.Fail), r.ThresholdResults.Count(t => t.Outcome == CheckOutcome.Warning)))],
            Derivation =
            [
                "Tolerance: the drift policy configured on the definition for the metric (relative % of the baseline and/or absolute in the metric's unit); no default tolerance exists.",
                "Several current runs: the median of the per-run values is compared; percentiles are never averaged or pooled.",
                $"ConfirmedRegression: at least {MinRunsForConfirmation} comparable runs, the median beyond the tolerance and at least two thirds of the runs beyond it. Otherwise at most PotentialRegression.",
                "Inconclusive: not comparable, a metric missing, too few requests for the percentile (P50 2, P90 10, P95 20, P99 100), or runs spreading more than twice the relative tolerance.",
                "Throughput under an arrival-rate workload is not judged (it follows the planned rate).",
                "Thresholds are reported per run beside the regression verdict; a threshold pass does not hide a regression and vice versa.",
            ],
            Limitations = limitations,
        };
    }

    private static string Join(IEnumerable<MetricRegression> metrics, RegressionVerdict v) =>
        string.Join("; ", metrics.Where(m => m.Verdict == v).Select(m => $"{m.Label}: {m.Reason}"));

    /// <summary>True when <paramref name="current"/> is worse than <paramref name="reference"/> by more than the policy allows.</summary>
    private static bool Worsening(double reference, double current, bool lowerIsBetter, PerformanceDriftPolicy policy)
    {
        var worse = lowerIsBetter ? current - reference : reference - current;
        if (worse <= 0) return false;
        var rel = reference == 0 ? (double?)null : worse / Math.Abs(reference) * 100;
        return (policy.AllowedRelativeChangePercent is { } r && rel is { } x && x > r) || (policy.AllowedAbsoluteChange is { } a && worse > a);
    }

    private static string ToleranceText(string key, PerformanceDriftPolicy p)
    {
        var sign = LowerIsBetter(key) ? "+" : "−";
        var parts = new List<string>();
        if (p.AllowedRelativeChangePercent is { } r) parts.Add($"{sign}{r.ToString("0.#", CultureInfo.InvariantCulture)} %");
        if (p.AllowedAbsoluteChange is { } a) parts.Add($"{sign}{a.ToString("0.##", CultureInfo.InvariantCulture)} {Unit(key)}{(key == "errorRate" ? " (points)" : "")}");
        return string.Join(" or ", parts);
    }

    private static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : Math.Round((sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2, 3);

    // ── Trends ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static readonly string[] TrendKeys = ["p50", "p95", "p99", "throughput", "errorRate", "graphqlErrorRate"];

    /// <summary>Persisted runs of one definition, oldest first, each marked with its comparability against the baseline run.</summary>
    public static PerformanceTrend Trend(string definitionId, PerformanceBaseline? baseline, PerformanceTestRun? baselineRun, IEnumerable<PerformanceTestRun> runs)
    {
        var points = runs.Where(r => !r.IsActive).OrderBy(r => r.FinishedAt ?? r.CreatedAt).Select(r => new PerformanceTrendPoint
        {
            RunId = r.RunId, FinishedAt = r.FinishedAt, VersionLabel = r.VersionLabel, DefinitionVersion = r.DefinitionVersion, State = r.State,
            IsBaselineRun = baselineRun is not null && r.RunId == baselineRun.RunId,
            Comparability = baselineRun is null ? null : r.RunId == baselineRun.RunId ? ComparabilityLevel.Comparable : Comparability(baselineRun, r).Level,
            ThresholdVerdict = r.Verdict,
            Values = TrendKeys.ToDictionary(k => k, k => Complete(r) ? Value(r, k) : null),
        }).ToList();
        return new PerformanceTrend
        {
            DefinitionId = definitionId, BaselineId = baseline?.BaselineId, BaselineRunId = baselineRun?.RunId, MetricKeys = [.. TrendKeys], Points = points,
            ExcludedPoints = points.Count(p => p.Comparability is ComparabilityLevel.NotComparable or ComparabilityLevel.InsufficientEvidence || p.Values.Values.All(v => v is null)),
        };
    }
}
