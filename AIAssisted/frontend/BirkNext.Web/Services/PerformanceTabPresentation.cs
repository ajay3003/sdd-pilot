using System.Globalization;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;

namespace BirkNext.Web.Services;

// ── Workload ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>An inline workload problem on one field. <see cref="Safety"/>: within the definition but beyond this instance's safety limits.</summary>
public sealed record WorkloadFieldIssue(string Field, string Message, bool Safety);

/// <summary>What the workload will do: duration, peak load, the request upper bound against the safety limit, and the impact in one sentence.</summary>
public sealed record WorkloadEstimate(int DurationSeconds, string PeakLoad, long MaxRequests, long? RequestLimit, string Impact);

/// <summary>
/// The Workload tab's inline validation and estimate. A preview of the backend readiness rules (PerformanceTestSafety.WorkloadIssues stays the
/// authority; the request estimate is the same shared formula), attributed to the field that causes each problem so nothing invalid is hidden.
/// </summary>
public static class PerformanceWorkloadPresentation
{
    public static IReadOnlyList<WorkloadFieldIssue> Issues(PerformanceWorkload w, PerformanceSafetyLimits? limits, IReadOnlyList<HttpPerformanceStep> steps)
    {
        var issues = new List<WorkloadFieldIssue>();
        foreach (var (field, seconds) in new[] { ("warmup", w.WarmupSeconds), ("rampup", w.RampUpSeconds), ("steady", w.SteadyStateSeconds), ("rampdown", w.RampDownSeconds) })
            if (seconds < 0) issues.Add(new(field, "Cannot be negative.", false));
        if (w.SteadyStateSeconds is >= 0 and < 10) issues.Add(new("steady", "The steady state must last at least 10 seconds.", false));
        if (w.VirtualUsers is not { } vus || vus < 1) issues.Add(new("vus", "Set at least 1 virtual user.", false));
        else if (limits is not null && vus > limits.MaxVirtualUsers) issues.Add(new("vus", $"Above the safety limit of {limits.MaxVirtualUsers} virtual users.", true));
        if (w.Mode == WorkloadMode.ArrivalRate)
        {
            if (w.RequestsPerSecond is not { } rps || rps <= 0 || !double.IsFinite(rps)) issues.Add(new("rate", "Set a target rate above 0 requests per second.", false));
            else if (limits is not null && rps > limits.MaxRequestsPerSecond)
                issues.Add(new("rate", $"Above the safety limit of {limits.MaxRequestsPerSecond.ToString("0.##", CultureInfo.InvariantCulture)} req/s.", true));
            if (w.StartRequestsPerSecond is { } start && (start < 0 || !double.IsFinite(start) || start > (w.RequestsPerSecond ?? 0)))
                issues.Add(new("start-rate", "The start rate must be between 0 and the target rate.", false));
        }
        if (limits is not null && issues.All(i => i.Field is not ("warmup" or "rampup" or "steady" or "rampdown")))
        {
            var max = w.Purpose == WorkloadPurpose.Soak ? limits.MaxSoakDurationSeconds : limits.MaxDurationSeconds;
            if (w.TotalSeconds > max) issues.Add(new("duration", $"Lasts {PerformanceTestPresentation.Duration(w.TotalSeconds)}; the safety limit for a {w.Purpose} test is {PerformanceTestPresentation.Duration(max)}.", true));
            if (w.Purpose == WorkloadPurpose.Stress && !limits.AllowStress) issues.Add(new("purpose", "Stress tests are disabled for this BirkNext instance.", true));
            if (w.Purpose == WorkloadPurpose.Soak && !limits.AllowSoak) issues.Add(new("purpose", "Soak tests are disabled for this BirkNext instance.", true));
            if (issues.Count == 0 && PerformanceTestRules.EstimatedMaxRequests(w, steps) is var estimate && estimate > limits.MaxTotalRequests)
                issues.Add(new("requests", $"Up to ~{estimate.ToString("N0", CultureInfo.InvariantCulture)} requests; the safety limit is {limits.MaxTotalRequests.ToString("N0", CultureInfo.InvariantCulture)}. Add think time or reduce the load.", true));
        }
        return issues;
    }

    public static WorkloadEstimate Estimate(PerformanceWorkload w, PerformanceSafetyLimits? limits, IReadOnlyList<HttpPerformanceStep> steps)
    {
        var max = PerformanceTestRules.EstimatedMaxRequests(w, steps);
        var peak = w.Mode == WorkloadMode.ArrivalRate
            ? $"{(w.RequestsPerSecond ?? 0).ToString("0.##", CultureInfo.InvariantCulture)} iterations/s (up to {w.VirtualUsers ?? 0} virtual users)"
            : $"{w.VirtualUsers ?? 0} concurrent virtual users";
        var enabled = Math.Max(1, steps.Count);
        var impact = w.Mode == WorkloadMode.ArrivalRate
            ? $"About {(w.RequestsPerSecond ?? 0) * enabled:0.##} requests per second reach the target during the steady state ({enabled} request{(enabled == 1 ? "" : "s")} per iteration), for {PerformanceTestPresentation.Duration(w.SteadyStateSeconds)}."
            : $"{w.VirtualUsers ?? 0} virtual users repeat the {enabled}-step scenario for {PerformanceTestPresentation.Duration(w.SteadyStateSeconds)} at full load; at most ~{max.ToString("N0", CultureInfo.InvariantCulture)} requests in total.";
        return new(w.TotalSeconds, peak, max, limits?.MaxTotalRequests, impact);
    }

    /// <summary>Think time is set per step in the Scenario; summarised here (never a second, competing setting).</summary>
    public static string ThinkTime(IReadOnlyList<HttpPerformanceStep> steps)
    {
        var withThink = steps.Where(s => s.ThinkTimeMs is > 0).ToList();
        if (steps.Count == 0) return "No steps yet.";
        if (withThink.Count == 0) return "None: each virtual user sends its next request immediately.";
        var min = withThink.Min(s => s.ThinkTimeMs!.Value); var max = withThink.Max(s => s.ThinkTimeMs!.Value);
        return $"{withThink.Count} of {steps.Count} steps pause {(min == max ? $"{min} ms" : $"{min}–{max} ms")} after their request.";
    }

    public static string PurposeMeaning(WorkloadPurpose purpose) => purpose switch
    {
        WorkloadPurpose.Baseline => "A modest, repeatable load to establish reference numbers.",
        WorkloadPurpose.Load => "The expected production-like load.",
        WorkloadPurpose.Stress => "Beyond the expected load, to find where it degrades.",
        WorkloadPurpose.Soak => "A long run at steady load, for stability and resource trends.",
        _ => "",
    };
}

// ── Thresholds ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A threshold's evaluation in the latest run of this exact saved version: never evaluated, passed, failed, advisory missed or not assessed.</summary>
public enum ThresholdRunState { NotEvaluated, Passed, Failed, AdvisoryMissed, NotAssessed }

public sealed record ThresholdRowView(int Index, PerformanceThreshold Threshold, string? Problem, string Impact, ThresholdRunState RunState, string RunText);

/// <summary>Configured → valid → evaluated → passed / failed, counted separately: a configured threshold is never an executed or passed one.</summary>
public sealed record ThresholdLifecycle(int Configured, int Valid, int Evaluated, int Passed, int Failed, string EvaluatedIn);

public static class PerformanceThresholdPresentation
{
    /// <summary>The run whose results describe the current thresholds: the latest finished run of the same saved version. Unsaved edits or another
    /// version mean none (a run of v2 says nothing about v3's thresholds).</summary>
    public static PerformanceTestRun? EvaluatingRun(IReadOnlyList<PerformanceTestRun> runs, string definitionId, int version, bool unsaved) =>
        unsaved ? null : runs.Where(r => r.DefinitionId == definitionId && r.DefinitionVersion == version && !r.IsActive).OrderByDescending(r => r.FinishedAt ?? r.CreatedAt).FirstOrDefault();

    public static (IReadOnlyList<ThresholdRowView> Rows, ThresholdLifecycle Lifecycle) View(IReadOnlyList<PerformanceThreshold> thresholds, PerformanceTestRun? run, int version, bool unsaved)
    {
        var problems = PerformanceTestRules.ThresholdProblems(thresholds);
        var rows = thresholds.Select((t, i) =>
        {
            var result = run?.ThresholdResults.FirstOrDefault(r => r.ThresholdId == t.Id);
            var (state, text) = result is null
                ? (ThresholdRunState.NotEvaluated, unsaved ? "Not evaluated — unsaved changes" : run is null ? $"Not evaluated — no finished run of v{version}" : "Not evaluated — not in that run")
                : result.Outcome switch
                {
                    CheckOutcome.Pass => (ThresholdRunState.Passed, $"Passed · measured {PerformanceTestRules.Format(result.Measured, t.Metric)}"),
                    CheckOutcome.Fail when result.Severity == ThresholdSeverity.Required => (ThresholdRunState.Failed, $"Failed · measured {PerformanceTestRules.Format(result.Measured, t.Metric)}"),
                    CheckOutcome.Fail or CheckOutcome.Warning => (ThresholdRunState.AdvisoryMissed, $"Advisory missed · measured {PerformanceTestRules.Format(result.Measured, t.Metric)}"),
                    _ => (ThresholdRunState.NotAssessed, $"Not assessed{(result.Explanation is { Length: > 0 } why ? $" — {why}" : "")}"),
                };
            var impact = problems[i] is not null ? "Blocks readiness until fixed"
                : t.Severity == ThresholdSeverity.Required ? "Required: a miss fails the run's quality verdict (does not block running)"
                : "Advisory: a miss is a warning";
            return new ThresholdRowView(i, t, problems[i], impact, state, text);
        }).ToList();
        var evaluated = rows.Count(r => r.RunState is ThresholdRunState.Passed or ThresholdRunState.Failed or ThresholdRunState.AdvisoryMissed);
        var evaluatedIn = unsaved ? "Unsaved changes — save a version and run it to evaluate."
            : run is null ? $"No finished run of v{version} yet."
            : $"Run {PerformanceTestPresentation.Short(run.RunId)} of v{run.DefinitionVersion}{(run.MetricsPartial ? " · partial metrics (not assessed)" : "")}.";
        return (rows, new ThresholdLifecycle(rows.Count, rows.Count(r => r.Problem is null), evaluated,
            rows.Count(r => r.RunState == ThresholdRunState.Passed), rows.Count(r => r.RunState is ThresholdRunState.Failed or ThresholdRunState.AdvisoryMissed), evaluatedIn));
    }

    public static string Label(ThresholdRunState s) => s switch
    {
        ThresholdRunState.Passed => "Passed",
        ThresholdRunState.Failed => "Failed",
        ThresholdRunState.AdvisoryMissed => "Advisory missed",
        ThresholdRunState.NotAssessed => "Not assessed",
        _ => "Not evaluated",
    };
}

// ── Resources ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One stage of an execution resource. Warning is a known limitation (never promoted to Yes); NotChecked = no evidence yet.</summary>
public enum ResourceStageState { Yes, No, Warning, NotChecked, NotApplicable }

public sealed record ResourceStage(ResourceStageState State, string Detail);

/// <summary>An execution resource across Configured → Validated → Runtime available → Execution verified, each from its own evidence.</summary>
public sealed record ResourceRowView(string Key, string Title, ResourceStage Configured, ResourceStage Validated, ResourceStage Runtime, ResourceStage Verified)
{
    public bool HasWarning => new[] { Configured, Validated, Runtime, Verified }.Any(s => s.State is ResourceStageState.Warning or ResourceStageState.No);
}

public static class PerformanceResourcePresentation
{
    public static readonly string[] StageLabels = ["Configured", "Validated", "Runtime available", "Execution verified"];

    /// <param name="readinessCurrent">The readiness was evaluated for the saved version shown (not a new or edited draft).</param>
    /// <param name="lastRun">The latest finished run of this definition (any version): what an execution actually used.</param>
    public static IReadOnlyList<ResourceRowView> Rows(PerformanceProviderStatus? provider, PerformanceTestReadiness? readiness, bool readinessCurrent, PerformanceTestRun? lastRun, string targetOrigin)
    {
        PerformanceReadinessItem? Item(string key) => readinessCurrent ? readiness?.Items.FirstOrDefault(i => i.Key == key) : null;
        var notChecked = new ResourceStage(ResourceStageState.NotChecked, readinessCurrent ? "Not reported by readiness" : "Check readiness on the saved version");
        ResourceStage Validated(PerformanceReadinessItem? item) => item is null ? notChecked
            : item.State == PerformanceReadinessState.Ready ? new(ResourceStageState.Yes, item.Detail)
            : item.Blocking ? new(ResourceStageState.No, item.Detail) : new(ResourceStageState.Warning, item.Detail);
        var executed = lastRun is { ExecutionEvidence: not null } run && run.StartedAt is not null ? run : null;
        var noRun = new ResourceStage(ResourceStageState.NotChecked, "No run has executed yet");
        string RunName(PerformanceTestRun r) => $"Run {PerformanceTestPresentation.Short(r.RunId)}";

        var runtime = provider?.Runtime;
        var runtimeRow = new ResourceRowView("runtime", "k6 execution runtime",
            runtime is null ? new(ResourceStageState.NotApplicable, "The provider needs no container runtime") : new(ResourceStageState.Yes, $"{runtime.DisplayName} (container runtime)"),
            Validated(Item("runtime")),
            runtime is null ? new(ResourceStageState.NotChecked, "Provider status not loaded")
                : runtime.Availability == ProviderAvailability.Available ? new(ResourceStageState.Yes, $"{runtime.DisplayName}{(runtime.Version is { } v ? $" {v}" : "")}") : new(ResourceStageState.No, runtime.Detail),
            executed is { RuntimeId: not null } r1 ? new(ResourceStageState.Yes, $"{RunName(r1)} ran on {r1.RuntimeId} {r1.RuntimeVersion}".TrimEnd()) : noRun);

        var imageRow = new ResourceRowView("image", "Container image",
            provider?.Image is { } image ? new(ResourceStageState.Yes, $"{image}{(provider.AllowImagePull ? " · pull allowed" : " · no download at run time")}") : new(ResourceStageState.NotChecked, "No image reported"),
            Validated(Item("image")),
            provider?.Image is null ? new(ResourceStageState.NotChecked, "Provider status not loaded")
                : provider.ImagePresent ? new(ResourceStageState.Yes, provider.ImageDigest is { } digest ? $"Present locally · {digest[..Math.Min(19, digest.Length)]}…" : "Present locally") : new(ResourceStageState.No, "Not available locally"),
            executed is { ContainerImage: not null } r2 ? new(ResourceStageState.Yes, $"{RunName(r2)} used {r2.ContainerImage}") : noRun);

        var network = Item("network");
        var networkRow = new ResourceRowView("network", "Network and target reachability",
            string.IsNullOrWhiteSpace(targetOrigin) ? new(ResourceStageState.No, "No target — configure the Target Environment") : new(ResourceStageState.Yes, $"Target {targetOrigin}"),
            Validated(Item("target")),
            network is null ? notChecked
                : network.State == PerformanceReadinessState.Ready ? new(ResourceStageState.Yes, network.Detail)
                : network.Blocking ? new(ResourceStageState.No, network.Detail) : new(ResourceStageState.Warning, network.Detail),
            executed?.Reachability is { } reach ? new(ResourceStageState.Yes, $"{RunName(executed)} reached the target as {reach.ExecutionOrigin}{(reach.Network is { } n ? $" on network {n}" : "")}") : noRun);

        var ca = Item("ca-trust");
        var caRow = new ResourceRowView("certificates", "Certificate trust (TLS)",
            ca is null ? notChecked : ca.Status == "Not required" ? new(ResourceStageState.NotApplicable, "No corporate CA bundle needed") : new(ResourceStageState.Yes, ca.ConfigurationKey is { } key ? $"Configured by {key}" : "Corporate CA bundle"),
            Validated(ca),
            network is null ? notChecked : network.State == PerformanceReadinessState.Ready ? new(ResourceStageState.Yes, "TLS validated by the container network check") : new(ResourceStageState.NotChecked, "Not confirmed by a container network check"),
            executed?.Reachability is not null ? new(ResourceStageState.Yes, $"{RunName(executed)} connected over TLS") : noRun);

        var limits = Item("container-limits");
        var unapplied = executed?.Limitations.FirstOrDefault(l => l.Contains("ran without", StringComparison.OrdinalIgnoreCase));
        var limitsRow = new ResourceRowView("limits", "CPU, memory, processes and temporary storage",
            limits is null ? notChecked : new(ResourceStageState.Yes, limits.Detail.Split(". ")[0]),
            limits is null ? notChecked : new(ResourceStageState.Yes, "Clamped by the backend to the allowed ranges"),
            limits is null ? notChecked
                : limits.Status == "Enforceable" ? new(ResourceStageState.Yes, $"Enforceable{(limits.Technical is { } t ? $" · {t}" : "")}")
                : limits.Status == "Not assessed" ? new(ResourceStageState.NotChecked, limits.Detail)
                : new(ResourceStageState.Warning, $"{limits.Status}: {limits.Detail[(limits.Detail.IndexOf(". ", StringComparison.Ordinal) + 2)..]}"),
            executed is null ? noRun
                : unapplied is not null ? new(ResourceStageState.Warning, $"{RunName(executed)}: {unapplied}")
                : new(ResourceStageState.Yes, $"{RunName(executed)}: no unapplied limit reported"));

        return [runtimeRow, imageRow, networkRow, caRow, limitsRow];
    }

    /// <summary>Known runtime limitations, worded once: non-ready execution items and provider capability gaps. Never promoted to Ready.</summary>
    public static IReadOnlyList<string> Warnings(PerformanceProviderStatus? provider, PerformanceTestReadiness? readiness, bool readinessCurrent)
    {
        var warnings = new List<string>();
        if (readinessCurrent && readiness is not null)
            warnings.AddRange(readiness.Items.Where(i => i.Key is "runtime" or "image" or "provider" or "network" or "ca-trust" or "container-limits" && i.State != PerformanceReadinessState.Ready
                && !(i.Key == "ca-trust" && i.Status == "Not required")).Select(i => $"{i.Label}: {(i.Headline is { Length: > 0 } h ? h : i.Detail)}"));
        if (provider is { Availability: ProviderAvailability.Available } p && !p.Capabilities.Cancellation) warnings.Add("The provider cannot cancel a run in progress.");
        return warnings;
    }

    public static string Label(ResourceStageState s) => s switch
    {
        ResourceStageState.Yes => "Yes",
        ResourceStageState.No => "No",
        ResourceStageState.Warning => "Limited",
        ResourceStageState.NotApplicable => "Not needed",
        _ => "Not checked",
    };
}

// ── Results ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>How complete a run's metrics are and what kind of failure it shows — execution (the tool) apart from the application under test.</summary>
public sealed record RunInterpretation(string Completeness, string CompletenessDetail, string FailureKind, string FailureDetail, IReadOnlyList<string> Limitations);

public static class PerformanceResultPresentation
{
    public static RunInterpretation Interpret(PerformanceTestRun r)
    {
        var m = r.Metrics;
        var (completeness, completenessDetail) =
            r.IsActive ? ("In progress", "Metrics are not available until the run finishes.")
            : m is null ? ("No metrics", "Nothing was measured; there is nothing to assess.")
            : r.MetricsPartial || r.State is PerformanceRunState.Cancelled or PerformanceRunState.TimedOut ? ("Partial", "Metrics cover only part of the workload: thresholds are not assessed and no pass/fail conclusion is drawn.")
            : r.State == PerformanceRunState.Completed ? ("Complete", "Metrics cover the whole workload.")
            : ("Partial", "The run did not complete normally.");
        var (kind, detail) = r.State switch
        {
            PerformanceRunState.Queued or PerformanceRunState.Preparing or PerformanceRunState.Running or PerformanceRunState.Cancelling => ("Not yet known", "The run is still in progress."),
            PerformanceRunState.Cancelled => ("Cancelled", $"Stopped before the end{(r.StateReason is { } why ? $" ({why})" : "")} — not a failure of the application, and not a completed run."),
            PerformanceRunState.ExecutionFailed or PerformanceRunState.TimedOut or PerformanceRunState.Blocked =>
                ("Execution failure", $"The test tooling could not complete the run{(r.StateReason is { } why2 ? $": {why2}" : ".")} The application under test was not assessed by this run."),
            _ when m is null => ("Not assessed", "No metrics were recorded."),
            _ when m.FailedRequests > 0 => ("Application responses failed",
                $"{m.FailedRequests.ToString("N0", CultureInfo.InvariantCulture)} of {m.RequestCount.ToString("N0", CultureInfo.InvariantCulture)} requests failed ({PerformanceTestRules.Format(m.ErrorRatePercent, PerformanceMetric.ErrorRatePercent)}) — HTTP error statuses or transport failures from the target, not the test tooling."),
            _ => ("No failed requests", "Every request received a successful response."),
        };
        var limitations = r.Limitations.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return new(completeness, completenessDetail, kind, detail, limitations);
    }
}
