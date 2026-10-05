using BirkNext.PerformanceTests;

namespace BirkNext.Web.Services;

/// <summary>Semantic role of a control-center element. Maps onto shared pill tones plus page accents; status text is always shown beside it.</summary>
public enum PtTone { Ready, Attention, Blocked, Info, Running, Baseline, Neutral, Resource }

public sealed record PtStatus(string Label, string Glyph, PtTone Tone, string? Reason = null)
{
    /// <summary>Shared <c>sd-pill</c> tone (Technology Coverage / Source Analysis / Pipeline Review).</summary>
    public string PillClass => Tone switch
    {
        PtTone.Ready => "sd-pill-complete",
        PtTone.Attention => "sd-pill-partial",
        PtTone.Blocked => "sd-pill-attention",
        _ => "sd-pill-muted",
    };

    public string ToneClass => Tone.ToString().ToLowerInvariant();
}

public sealed record PtCheck(string Key, string Label, PtStatus Status, string? Detail);

public sealed record PtCheckGroup(string Name, IReadOnlyList<PtCheck> Checks);

public sealed record WorkloadPhase(string Key, string Label, int Seconds);

public sealed record NextStep(string Text, PtTone Tone, string? Action, string? ActionLabel);

/// <summary>
/// Pure presentation of the Performance Test Review control center. Every state comes from the definition, the backend readiness, the
/// provider status, the runs and the baselines — never invented:
///   saved ≠ executable · ready ≠ running · completed ≠ passed · no run ≠ failed · no baseline ≠ blocked · resource observation off ≠ failure ·
///   provider unavailable = tool limitation (never a performance result) · Production = blocked.
/// </summary>
public static class PerformanceTestReviewDashboard
{
    private static readonly HashSet<PerformanceReadinessState> Tooling =
        [PerformanceReadinessState.ProviderUnavailable, PerformanceReadinessState.RuntimeUnavailable, PerformanceReadinessState.ImageMissing];

    private static readonly HashSet<PerformanceReadinessState> Hard =
        [PerformanceReadinessState.UnsafeEnvironment, PerformanceReadinessState.Blocked, PerformanceReadinessState.InvalidScenario];

    public static bool IsProduction(string? environmentType) =>
        environmentType?.Trim().Equals("Production", StringComparison.OrdinalIgnoreCase) == true || environmentType?.Trim().Equals("Prod", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Overall status of the selected definition. Readiness comes from the backend; "Draft"/"Unsaved" only describe the editor.</summary>
    public static PtStatus PageStatus(bool isNew, bool dirty, PerformanceTestReadiness? readiness, PerformanceTestRun? activeRun, string? environmentType)
    {
        if (activeRun is { } run)
            return run.State == PerformanceRunState.Cancelling ? new("Cancelling", "◌", PtTone.Running) : new("Running", "▶", PtTone.Running);
        if (IsProduction(environmentType))
            return new("Blocked", "✕", PtTone.Blocked, "Performance tests cannot run against Production.");
        if (isNew) return new("Needs configuration", "!", PtTone.Attention, "The definition must be saved before execution.");
        if (dirty) return new("Needs configuration", "!", PtTone.Attention, "Unsaved changes: save to re-evaluate readiness.");
        if (readiness is null) return new("Unknown", "○", PtTone.Neutral, "Readiness could not be loaded.");
        if (readiness.Ready) return new("Ready", "✓", PtTone.Ready);
        var blocking = readiness.Items.Where(i => i.Blocking).ToList();
        if (blocking.Any(i => Hard.Contains(i.State)))
            return new("Blocked", "✕", PtTone.Blocked, blocking.First(i => Hard.Contains(i.State)).Detail);
        if (blocking.Count > 0 && blocking.All(i => Tooling.Contains(i.State)))
            return new("Blocked by tooling", "!", PtTone.Attention, "A tool limitation, not a performance result: " + blocking[0].Detail);
        return new("Needs configuration", "!", PtTone.Attention, blocking.FirstOrDefault()?.Detail ?? readiness.Blockers.FirstOrDefault());
    }

    /// <summary>Definition state of the editor: Draft (never saved), Unsaved changes, or Saved vN.</summary>
    public static PtStatus DefinitionState(bool isNew, bool dirty, int version) =>
        isNew ? new("Draft · not saved", "✎", PtTone.Attention)
        : dirty ? new("Unsaved changes", "✎", PtTone.Attention)
        : new($"Saved · v{version}", "✓", PtTone.Info);

    /// <summary>Environment safety. The backend guard decides; the type alone never proves safety.</summary>
    public static PtStatus EnvironmentSafety(string? environmentType, PerformanceTestReadiness? readiness)
    {
        if (IsProduction(environmentType)) return new("Blocked", "✕", PtTone.Blocked, "Production is refused for performance tests.");
        var item = readiness?.Items.FirstOrDefault(i => i.Key == "environment");
        if (item is null && readiness is not null) return new("Not reported", "○", PtTone.Neutral, "The backend readiness did not report the environment guard.");
        if (item is null) return string.IsNullOrWhiteSpace(environmentType)
            ? new("Unknown", "?", PtTone.Attention, "No environment classification.")
            : new("Checked on save", "○", PtTone.Neutral, "The backend production guard evaluates the saved definition.");
        return item.State == PerformanceReadinessState.Ready
            ? new("Allowed", "✓", PtTone.Ready, "Non-production; the backend production guard allows it.")
            : new(PerformanceTestPresentation.Label(item.State), "✕", Hard.Contains(item.State) ? PtTone.Blocked : PtTone.Attention, item.Detail);
    }

    public static string Classification(string? environmentType) =>
        IsProduction(environmentType) ? "Production" : string.IsNullOrWhiteSpace(environmentType) ? "Unclassified" : "Non-production";

    public static PtStatus Check(PerformanceReadinessState s) => s switch
    {
        PerformanceReadinessState.Ready => new("Ready", "✓", PtTone.Ready),
        PerformanceReadinessState.Optional => new("Optional", "○", PtTone.Neutral),
        _ when Hard.Contains(s) => new(PerformanceTestPresentation.Label(s), "✕", PtTone.Blocked),
        _ => new(PerformanceTestPresentation.Label(s), "!", PtTone.Attention),
    };

    private static readonly HashSet<string> SafetyKeys = ["environment", "limits", "tls"];

    /// <summary>
    /// Readiness checks grouped as Required / Safety / Optional, from the backend items. "Definition saved" is the one editor-side requirement
    /// (the backend evaluates only saved definitions). Optional items (baseline, observability, resource observation when off) never block.
    /// </summary>
    public static IReadOnlyList<PtCheckGroup> Groups(bool isNew, bool dirty, PerformanceTestReadiness? readiness)
    {
        var required = new List<PtCheck>
        {
            new("saved", "Definition saved", isNew || dirty ? new("Save required", "!", PtTone.Attention) : new("Saved", "✓", PtTone.Ready),
                isNew ? "Save the definition so the backend can evaluate it." : dirty ? "Save the changes to re-evaluate readiness." : null),
        };
        var safety = new List<PtCheck>();
        var optional = new List<PtCheck>();
        foreach (var item in readiness?.Items ?? [])
        {
            var check = new PtCheck(item.Key, item.Label, Check(item.State), item.Detail);
            if (item.State == PerformanceReadinessState.Optional && !item.Blocking) optional.Add(check);
            else if (SafetyKeys.Contains(item.Key)) safety.Add(check);
            else required.Add(check);
        }
        return new List<PtCheckGroup> { new("Required", required), new("Safety", safety), new("Optional", optional) }.Where(g => g.Checks.Count > 0).ToList();
    }

    /// <summary>"n of m required checks ready" over Required + Safety (Optional never counts).</summary>
    public static (int Ready, int Total) RequiredCounts(IReadOnlyList<PtCheckGroup> groups)
    {
        var checks = groups.Where(g => g.Name != "Optional").SelectMany(g => g.Checks).ToList();
        return (checks.Count(c => c.Status.Tone == PtTone.Ready), checks.Count);
    }

    /// <summary>Workload phases actually configured (zero-length phases are not shown — nothing is invented).</summary>
    public static IReadOnlyList<WorkloadPhase> Phases(PerformanceWorkload w) =>
        new List<WorkloadPhase>
        {
            new("warmup", "Warm-up", w.WarmupSeconds),
            new("rampup", "Ramp-up", w.RampUpSeconds),
            new("steady", "Steady state", w.SteadyStateSeconds),
            new("rampdown", "Ramp-down", w.RampDownSeconds),
        }.Where(p => p.Seconds > 0).ToList();

    /// <summary>Text equivalent of the timeline: "Warm-up 30 s, Steady state 3 min" (start–end offsets in the visible labels).</summary>
    public static string PhasesText(PerformanceWorkload w) =>
        string.Join(", ", Phases(w).Select(p => $"{p.Label} {PerformanceTestPresentation.Duration(p.Seconds)}"));

    public static IEnumerable<(WorkloadPhase Phase, int Start, int End)> PhaseWindows(PerformanceWorkload w)
    {
        var at = 0;
        foreach (var p in Phases(w)) { yield return (p, at, at + p.Seconds); at += p.Seconds; }
    }

    public static PtStatus Thresholds(IReadOnlyList<PerformanceThreshold> thresholds) => thresholds.Count == 0
        ? new("None — measured only", "○", PtTone.Neutral, "Without thresholds a run is measured, not assessed.")
        : new("Configured", "◆", PtTone.Info, $"{thresholds.Count(t => t.Severity == ThresholdSeverity.Required)} required · {thresholds.Count(t => t.Severity == ThresholdSeverity.Advisory)} advisory");

    /// <summary>Latest run: execution and quality stay separate. No run is neutral; red only for an actual threshold failure.</summary>
    public static PtStatus LatestRun(PerformanceTestRun? run)
    {
        if (run is null) return new("No run yet", "○", PtTone.Neutral);
        if (run.IsActive) return new(PerformanceTestPresentation.Label(run.State), "▶", PtTone.Running);
        return run.State switch
        {
            PerformanceRunState.Completed => run.Verdict switch
            {
                PerformanceQualityVerdict.Pass => new("Completed · Pass", "✓", PtTone.Ready),
                PerformanceQualityVerdict.Fail => new("Completed · Fail", "✕", PtTone.Blocked, "Required thresholds failed."),
                PerformanceQualityVerdict.Warning => new("Completed · Warning", "!", PtTone.Attention),
                _ => new("Completed · Not assessed", "○", PtTone.Info, "No thresholds were assessed."),
            },
            PerformanceRunState.Cancelled => new("Cancelled", "◌", PtTone.Neutral),
            PerformanceRunState.TimedOut => new("Timed out", "!", PtTone.Attention, run.StateReason),
            PerformanceRunState.ExecutionFailed => new("Execution failed", "!", PtTone.Attention, "A tool/run error — not a performance result."),
            PerformanceRunState.Blocked => new("Blocked", "✕", PtTone.Blocked, run.StateReason),
            _ => new(PerformanceTestPresentation.Label(run.State), "○", PtTone.Neutral),
        };
    }

    /// <summary>Resource observation is optional: Disabled is neutral, never a warning.</summary>
    public static PtStatus Resources(ResourceObservationConfiguration? config) => config is { Enabled: true } c
        ? new("Enabled", "✓", PtTone.Resource, $"{c.TargetComponentIds.Count} target{(c.TargetComponentIds.Count == 1 ? "" : "s")} · every {c.SampleIntervalSeconds} s")
        : new("Disabled · optional", "○", PtTone.Neutral, "Turn it on in Resources to observe memory/CPU during the run.");

    public static PtStatus Baseline(PerformanceBaseline? baseline) => baseline is null
        ? new("None selected", "○", PtTone.Baseline, "Promote a completed run from History to create the reference for drift comparison.")
        : new($"Baseline v{baseline.Version}", "◆", PtTone.Baseline);

    /// <summary>Execution engine status (k6 + runtime): a tool limitation never reads as a performance problem.</summary>
    public static PtStatus Engine(PerformanceProviderStatus? provider) => provider is null
        ? new("Unknown", "?", PtTone.Neutral)
        : provider.Availability == ProviderAvailability.Available
            ? new("Ready", "✓", PtTone.Ready)
            : new(PerformanceTestPresentation.Label(provider.Availability), "!", PtTone.Attention, "Tool limitation — see System Settings → Performance Test Engines.");

    /// <summary>Workload against the backend limits. "Within policy" is a preview; the backend enforces the limits.</summary>
    public static PtStatus WorkloadPolicy(PerformanceWorkload w, PerformanceSafetyLimits? limits)
    {
        if (limits is null) return new("Limits unknown", "○", PtTone.Neutral);
        var over = new List<string>();
        if (w.VirtualUsers is { } vus && vus > limits.MaxVirtualUsers) over.Add($"{vus} VUs > {limits.MaxVirtualUsers}");
        if (w.Mode == WorkloadMode.ArrivalRate && w.RequestsPerSecond is { } rps && rps > limits.MaxRequestsPerSecond) over.Add($"{rps:0.##} req/s > {limits.MaxRequestsPerSecond:0.##}");
        var max = w.Purpose == WorkloadPurpose.Soak ? limits.MaxSoakDurationSeconds : limits.MaxDurationSeconds;
        if (w.TotalSeconds > max) over.Add($"{PerformanceTestPresentation.Duration(w.TotalSeconds)} > {PerformanceTestPresentation.Duration(max)}");
        if (w.Purpose == WorkloadPurpose.Stress && !limits.AllowStress) over.Add("stress tests are not allowed");
        if (w.Purpose == WorkloadPurpose.Soak && !limits.AllowSoak) over.Add("soak tests are not allowed");
        return over.Count == 0 ? new("Within policy", "✓", PtTone.Ready) : new("Exceeds policy", "✕", PtTone.Blocked, string.Join("; ", over));
    }

    /// <summary>What to do next, in priority order.</summary>
    public static NextStep Next(PtStatus page, bool isNew, bool dirty, PerformanceTestReadiness? readiness, IReadOnlyList<PerformanceTestRun> runs, PerformanceBaseline? baseline)
    {
        if (page.Tone == PtTone.Running) return new("A performance test is running. Results appear when it finishes.", PtTone.Running, "run", "Open run");
        if (page.Label == "Blocked") return new($"Execution blocked. {page.Reason}", PtTone.Blocked, "readiness", "View readiness");
        if (isNew || dirty) return new(isNew ? "Save this test definition to complete readiness." : "Save your changes to re-evaluate readiness.", PtTone.Attention, "save", "Save definition");
        if (readiness is { Ready: false })
        {
            var count = readiness.Items.Count(i => i.Blocking);
            return new($"Resolve {count} required item{(count == 1 ? "" : "s")} before running. {page.Reason}", PtTone.Attention, "readiness", "View readiness");
        }
        if (readiness is not { Ready: true }) return new("Readiness could not be evaluated.", PtTone.Neutral, "readiness", "View readiness");
        if (runs.Count == 0) return new("Everything is ready. Run the first test.", PtTone.Ready, "run", "Run test");
        if (baseline is null && runs.Any(r => r.State == PerformanceRunState.Completed && r.Metrics is not null && !r.MetricsPartial))
            return new("Promote a completed run to baseline to enable drift comparison.", PtTone.Baseline, "history", "Open history");
        return new("Ready to run again.", PtTone.Ready, "run", "Run test");
    }

    /// <summary>Supported target types from the provider capabilities, as chips.</summary>
    public static IReadOnlyList<string> TargetTypes(PerformanceProviderCapabilities? c) =>
        c is null ? [] : [.. (c.Http ? new[] { "REST", "HTTP" } : []), .. (c.GraphQl ? new[] { "GraphQL queries" } : [])];

    public static string TargetLabel(PerformanceTargetType t) => t switch
    {
        PerformanceTargetType.GraphQlHttp => "GraphQL over HTTP",
        PerformanceTargetType.GenericHttp => "Generic HTTP",
        _ => "REST over HTTP",
    };

    public static string Host(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var u) ? u.Host : origin;
}
