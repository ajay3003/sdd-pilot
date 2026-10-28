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

    /// <summary>What a new draft still lacks before it can be saved (and so before readiness). Empty = it is saved automatically as v1.</summary>
    public static IReadOnlyList<string> DraftIssues(PerformanceTestDefinition d)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Name)) issues.Add("a definition name");
        if (string.IsNullOrWhiteSpace(d.TargetOrigin)) issues.Add("a target (configure the Target Environment)");
        if (d.Scenario.Steps.Count == 0) issues.Add("at least one request step");
        if (d.Scenario.Steps.Any(s => string.IsNullOrWhiteSpace(s.Name))) issues.Add("a name for every step");
        if (d.Scenario.Steps.Any(s => !s.RelativePath.StartsWith('/'))) issues.Add("relative paths that start with /");
        return issues;
    }

    /// <summary>
    /// Overall status of the selected definition. Readiness comes from the backend; an exact blocker is named ("Blocked · container TLS trust"),
    /// so a local execution problem never reads as a badly configured definition. "Draft"/"Unsaved changes" only describe the editor.
    /// </summary>
    public static PtStatus PageStatus(bool isNew, bool dirty, PerformanceTestReadiness? readiness, PerformanceTestRun? activeRun, string? environmentType, bool draftValid = true)
    {
        if (activeRun is { } run)
            return run.State == PerformanceRunState.Cancelling ? new("Cancelling", "◌", PtTone.Running) : new("Running", "▶", PtTone.Running);
        if (IsProduction(environmentType))
            return new("Blocked", "✕", PtTone.Blocked, "Performance tests cannot run against Production.");
        if (isNew) return draftValid
            ? new("Draft", "✎", PtTone.Attention, "Check readiness saves the draft as v1 automatically, then evaluates it.")
            : new("Draft · incomplete", "!", PtTone.Attention, "Complete the required fields before readiness.");
        if (dirty) return new("Unsaved changes", "✎", PtTone.Attention, "Save the changes as a new version to re-evaluate readiness.");
        if (readiness is null) return new("Unknown", "○", PtTone.Neutral, "Readiness could not be loaded.");
        if (readiness.Ready) return new("Ready", "✓", PtTone.Ready);
        var blocking = readiness.Items.Where(i => i.Blocking).ToList();
        if (blocking.Count == 0) return new("Not ready", "!", PtTone.Attention, readiness.Blockers.FirstOrDefault());
        var hard = blocking.Any(i => Hard.Contains(i.State));
        var first = blocking.FirstOrDefault(i => Hard.Contains(i.State)) ?? blocking[0];
        // Concise at the top: the full explanation is stated once, in the Readiness blocker panel.
        var count = $"{blocking.Count} required prerequisite{(blocking.Count == 1 ? "" : "s")} must be resolved before this test can run.";
        var scope = blocking.All(IsLocalExecutionPrerequisite) ? " Local execution prerequisite — not a performance, target or application result." : "";
        // The blocker named briefly: a local execution prerequisite by its headline only (its full explanation lives in the blocker panel);
        // a definition or safety blocker keeps its short reason.
        var headline = Headline(first);
        var named = IsLocalExecutionPrerequisite(first) && headline.Length > 0 ? $"{headline}."
            : headline is { Length: > 0 } h ? $"{h}. {first.Detail}" : first.Detail;
        if (Tooling.Contains(first.State)) named = "A tool limitation, not a performance result: " + named;
        if (blocking.Count == 1)
            return new($"Blocked — {Summary(first)}", hard ? "✕" : "!", hard ? PtTone.Blocked : PtTone.Attention, $"{named} {count}{scope}");
        return new($"Blocked — {blocking.Count} required prerequisites", hard ? "✕" : "!", hard ? PtTone.Blocked : PtTone.Attention, count + scope);
    }

    /// <summary>A prerequisite of running k6 on this machine (execution setup, container network/TLS trust, engine/runtime), never a property of
    /// the target or the application. Read from the backend's category/owner; nothing is inferred from wording.</summary>
    public static bool IsLocalExecutionPrerequisite(PerformanceReadinessItem item) =>
        Tooling.Contains(item.State)
        || item.Category is PerformanceReadinessCategories.ExecutionSetup or PerformanceReadinessCategories.Reachability
        || item.Owner == LocalExecutionOwner;

    public const string LocalExecutionOwner = "Local execution configuration";

    /// <summary>"Local execution prerequisite" for setup blockers; otherwise the backend category.</summary>
    public static string BlockerCategory(PerformanceReadinessItem item) => IsLocalExecutionPrerequisite(item) ? "Local execution prerequisite" : item.Category is { Length: > 0 } c ? c : "Definition";

    public const string TlsSafetyNote = "TLS verification remains enabled and must not be bypassed. Configure the trusted corporate CA bundle rather than bypassing certificate validation.";

    /// <summary>The blocker in a few words (backend summary; older backends: the label).</summary>
    public static string Summary(PerformanceReadinessItem item) => item.Summary.Length > 0 ? item.Summary : item.Label.ToLowerInvariant();

    public static string Headline(PerformanceReadinessItem item) => item.Headline.Length > 0 ? item.Headline : "";

    /// <summary>Definition state of the editor: a new draft (saved automatically before readiness when complete), unsaved changes to a saved
    /// version (saved explicitly as a new version), or Saved vN.</summary>
    public static PtStatus DefinitionState(bool isNew, bool dirty, int version, bool draftValid = true) =>
        isNew ? draftValid ? new("Draft · will be saved automatically before readiness check", "✎", PtTone.Attention)
                : new("Draft · complete required fields before readiness", "!", PtTone.Attention)
        : dirty ? new($"Unsaved changes to v{version}", "✎", PtTone.Attention)
        : new($"Saved · v{version}", "✓", PtTone.Info);

    /// <summary>The label of a readiness action (the same operation wherever it is offered).</summary>
    public static string ActionLabel(string action, PerformanceReadinessItem? item = null) => action switch
    {
        PerformanceReadinessActions.CheckNetwork => item?.Status is null or "Not checked" ? "Check container network" : "Re-check container network",
        PerformanceReadinessActions.OpenLocalExecutionSetup => "Open local execution setup",
        PerformanceReadinessActions.ConfigureTarget => "Configure target",
        PerformanceReadinessActions.EditScenario => "Edit scenario",
        PerformanceReadinessActions.EditWorkload => "Edit workload",
        PerformanceReadinessActions.EditThresholds => "Edit thresholds",
        PerformanceReadinessActions.OpenEngines => "Open Performance Test Engines",
        "check-readiness" => "Check readiness",
        "save-version" => "Save changes as new version",
        "scenario" => "Complete definition",
        "run" => "Run test",
        "history" => "Open history",
        _ => "Review blockers",
    };

    /// <summary>Actionable title of a container network check result (structured state; the raw detail stays secondary).</summary>
    public static string ProbeTitle(string state) => state switch
    {
        PerformanceProbeStates.Reachable => "Container reachable",
        PerformanceProbeStates.AuthChallenge => "Container reachable · authentication requested",
        PerformanceProbeStates.DnsFailure => "DNS lookup failed from the k6 container",
        PerformanceProbeStates.Timeout => "Connection from the k6 container timed out",
        PerformanceProbeStates.ConnectionRefused => "Connection refused",
        PerformanceProbeStates.ConnectionFailed => "Could not connect from the k6 container",
        PerformanceProbeStates.TlsTrustFailure => "Container TLS trust is not configured",
        PerformanceProbeStates.HostnameMismatch => "Certificate host name mismatch",
        PerformanceProbeStates.TlsFailure => "TLS handshake failed",
        PerformanceProbeStates.ProxyFailure => "Proxy failure",
        PerformanceProbeStates.RuntimeFailure => "Container runtime failure",
        PerformanceProbeStates.RuntimeUnavailable => "Container runtime unavailable",
        PerformanceProbeStates.ImageMissing => "k6 image missing",
        _ => "No response from the target",
    };

    /// <summary>Owner pages for link actions (null = an in-page action).</summary>
    public static string? ActionHref(string action) => ActionHref(action, null);

    /// <summary>
    /// With the definition in context, "Open local execution setup" focuses Container TLS trust and offers "Back to Performance Test Review"
    /// returning to the same definition's Readiness tab. Only ids go in the URL — never certificate data or targets.
    /// </summary>
    public static string? ActionHref(string action, string? definitionId) => action switch
    {
        PerformanceReadinessActions.OpenLocalExecutionSetup => "admin/system-settings?section=performance-test-engines&focus=container-tls-trust"
            + $"&returnTo={Uri.EscapeDataString(definitionId is { Length: > 0 } id ? $"performance-test-review?definition={id}&tab=readiness" : "performance-test-review?tab=readiness")}",
        PerformanceReadinessActions.OpenEngines => "admin/system-settings?section=performance-test-engines",
        PerformanceReadinessActions.ConfigureTarget => "admin/system-settings?section=target-environments",
        _ => null,
    };

    /// <summary>The blocking items that have a safe action (shown with that action in the readiness list and on Overview).</summary>
    public static IReadOnlyList<PerformanceReadinessItem> ActionableBlockers(PerformanceTestReadiness? readiness) =>
        readiness?.Items.Where(i => i.Blocking && i.Action is not null).ToList() ?? [];

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

    /// <summary>An item's status, using its own vocabulary where it has one (Corporate CA trust: Not required / Missing / Invalid / Not assessed).</summary>
    public static PtStatus Check(PerformanceReadinessItem item) => item.Status switch
    {
        "Not required" => new("Not required", "✓", PtTone.Ready),
        "Not assessed" => new("Not assessed", "○", PtTone.Neutral),
        "Missing" => new("Missing", "!", PtTone.Attention),
        "Invalid" => new("Invalid", "!", PtTone.Attention),
        "Not checked" => new("Not checked", "!", PtTone.Attention),
        "Stale" => new("Stale · check again", "!", PtTone.Attention),
        _ => Check(item.State),
    };

    public static PtStatus Check(PerformanceReadinessState s) => s switch
    {
        PerformanceReadinessState.Ready => new("Ready", "✓", PtTone.Ready),
        PerformanceReadinessState.Optional => new("Optional", "○", PtTone.Neutral),
        _ when Hard.Contains(s) => new(PerformanceTestPresentation.Label(s), "✕", PtTone.Blocked),
        _ => new(PerformanceTestPresentation.Label(s), "!", PtTone.Attention),
    };

    private static readonly HashSet<string> SafetyKeys = ["environment", "limits", "tls"];

    /// <summary>Readiness categories in display order: different problems with different owners (never all "Needs configuration").</summary>
    public static readonly IReadOnlyList<string> CategoryOrder =
    [
        PerformanceReadinessCategories.Definition, PerformanceReadinessCategories.ExecutionSetup, PerformanceReadinessCategories.Reachability,
        PerformanceReadinessCategories.Safety, PerformanceReadinessCategories.Optional,
    ];

    /// <summary>
    /// Readiness checks grouped by category (Definition / Execution setup / Reachability / Safety / Optional) from the backend items. "Definition
    /// version" is the one editor-side requirement (the backend evaluates only saved versions). Optional items never block.
    /// </summary>
    public static IReadOnlyList<PtCheckGroup> Groups(bool isNew, bool dirty, PerformanceTestReadiness? readiness)
    {
        var saved = new PtCheck("saved", "Definition version", isNew ? new("Draft", "✎", PtTone.Attention) : dirty ? new("Unsaved changes", "✎", PtTone.Attention) : new("Saved", "✓", PtTone.Ready),
            isNew ? "Saved automatically as v1 when readiness is checked." : dirty ? "Save the changes as a new version to re-evaluate readiness." : null);
        if (readiness?.Items.Any(i => i.Category.Length > 0) == true)
        {
            return CategoryOrder.Select(name => new PtCheckGroup(name, [
                .. name == PerformanceReadinessCategories.Definition ? new[] { saved } : [],
                .. readiness.Items.Where(i => (i.Category.Length > 0 ? i.Category : PerformanceReadinessCategories.Definition) == name).Select(i => new PtCheck(i.Key, i.Label, Check(i), i.Detail)),
            ])).Where(g => g.Checks.Count > 0).ToList();
        }
        var required = new List<PtCheck> { saved };
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

    /// <summary>"n of m required readiness checks satisfied" over every non-optional group (Optional never counts). Execution readiness — not
    /// Guided setup progress, which counts configuration steps.</summary>
    public static (int Ready, int Total) RequiredCounts(IReadOnlyList<PtCheckGroup> groups)
    {
        // Neutral checks (optional, not assessed) are not requirements: they never count as missing.
        var checks = groups.Where(g => g.Name != "Optional").SelectMany(g => g.Checks).Where(c => c.Status.Tone != PtTone.Neutral).ToList();
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

    public const string EngineAvailableMeaning = "The execution engine is available locally. Target execution readiness is evaluated separately.";

    /// <summary>Guided setup progress, worded so it cannot be read as the execution-readiness count.</summary>
    public static string SetupProgress(BirkNext.Integrations.JourneySetupView setup) =>
        $"{setup.JourneyName} · {setup.RequiredReady} of {setup.RequiredSteps} setup step{(setup.RequiredSteps == 1 ? "" : "s")} completed";

    public const string SetupVersusReadiness = "Guided setup tracks configuration steps. Execution readiness evaluates the actual prerequisites required to run this definition; setup progress does not directly determine it.";

    /// <summary>Execution engine status (k6 + runtime): a tool limitation never reads as a performance problem.</summary>
    public static PtStatus Engine(PerformanceProviderStatus? provider) => provider is null
        ? new("Unknown", "?", PtTone.Neutral)
        : provider.Availability == ProviderAvailability.Available
            // Available, never "Ready": the engine working says nothing about whether this definition can run against the target.
            ? new("Available", "✓", PtTone.Info, EngineAvailableMeaning)
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

    /// <summary>What to do next, in priority order. When the backend knows the exact blocker and it has a safe action, that action is offered
    /// directly (the same operation as in Readiness). A run is never started from here: Run opens the explicit confirmation.</summary>
    public static NextStep Next(PtStatus page, bool isNew, bool dirty, PerformanceTestReadiness? readiness, IReadOnlyList<PerformanceTestRun> runs, PerformanceBaseline? baseline, bool draftValid = true)
    {
        if (page.Tone == PtTone.Running) return new("A performance test is running. Results appear when it finishes.", PtTone.Running, "run", "View run");
        if (page.Label == "Blocked") return new($"Execution blocked. {page.Reason}", PtTone.Blocked, "readiness", "View readiness");
        if (isNew) return draftValid
            ? new("Check readiness. The prefilled draft is saved as v1 automatically first; nothing runs.", PtTone.Attention, "check-readiness", ActionLabel("check-readiness"))
            : new("Complete the required fields of the definition before readiness.", PtTone.Attention, "scenario", ActionLabel("scenario"));
        if (dirty) return new($"You changed the saved definition. Save the changes as a new version to re-evaluate readiness (earlier runs keep their version).", PtTone.Attention, "save-version", ActionLabel("save-version"));
        if (readiness is { Ready: false })
        {
            var blocking = readiness.Items.Where(i => i.Blocking).ToList();
            var actionable = blocking.Where(i => i.Action is not null).ToList();
            if (blocking.Count == 1 && actionable.Count == 1)
            {
                var item = actionable[0];
                return new($"Resolve: {(Headline(item) is { Length: > 0 } h ? h : item.Label)}.", page.Tone == PtTone.Blocked ? PtTone.Blocked : PtTone.Attention, item.Action, ActionLabel(item.Action!, item));
            }
            return new($"Resolve {blocking.Count} readiness blocker{(blocking.Count == 1 ? "" : "s")} before running. {page.Reason}", PtTone.Attention, "readiness", "Review blockers");
        }
        if (readiness is not { Ready: true }) return new("Readiness could not be evaluated.", PtTone.Neutral, "readiness", "View readiness");
        if (runs.Count == 0) return new("Ready to run. Run the baseline test — it asks for confirmation before sending real load.", PtTone.Ready, "run", "Run baseline test");
        if (baseline is null && runs.Any(r => r.State == PerformanceRunState.Completed && r.Metrics is not null && !r.MetricsPartial))
            return new("No baseline selected. Promote a completed run to baseline to enable drift comparison.", PtTone.Baseline, "history", "Open history");
        return new("Ready to run again.", PtTone.Ready, "run", "Run test");
    }

    /// <summary>"Declared + Observed · OpenAPI · Persons API" / "Modified from declared operation" / "Manual step" — provenance of a step.</summary>
    /// <summary>True for a step created from a detected operation (contract and/or Live Discovery), false for a manual step.</summary>
    public static bool FromEvidence(HttpPerformanceStep step) => step.Source is { } source && !(source.Evidence == ScenarioEvidence.Manual && source.Origin == ScenarioOperationOrigin.Manual);

    public static string StepSource(HttpPerformanceStep step) => step.Source is not { } source || source.Evidence == ScenarioEvidence.Manual && source.Origin == ScenarioOperationOrigin.Manual
        ? "Manual step"
        : source.Modified ? $"Modified from declared operation · {source.ContractName ?? source.ContractFile ?? "contract"} (no longer an exact contract match)"
        : string.Join(" · ", new[]
        {
            PerformanceScenarioEngine.EvidenceLabel(source.Evidence),
            source.Origin switch { ScenarioOperationOrigin.OpenApi => "OpenAPI", ScenarioOperationOrigin.GraphQl => "GraphQL", ScenarioOperationOrigin.Observed => "Live Discovery", _ => "Setup profile reference" },
            source.ContractName, source.ObservedCount > 0 ? $"observed {source.ObservedCount}× (last {source.ObservedLastSeen?.ToUniversalTime():yyyy-MM-dd HH:mm} UTC)" : null,
        }.Where(t => !string.IsNullOrEmpty(t)));

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The {placeholders} of a step (path, query values, body, GraphQL variables) with where they go and the data type they need.</summary>
    public static IReadOnlyList<(string Variable, string Place, BirkNext.TestData.TestDataDataType Type)> StepPlaceholders(HttpPerformanceStep step)
    {
        var found = new List<(string, string, BirkNext.TestData.TestDataDataType)>();
        void Add(string? text, string place, BirkNext.TestData.TestDataDataType type)
        {
            foreach (System.Text.RegularExpressions.Match m in PlaceholderPattern.Matches(text ?? ""))
                if (found.All(f => f.Item1 != m.Groups[1].Value)) found.Add((m.Groups[1].Value, place, type));
        }
        Add(step.RelativePath, "path", BirkNext.TestData.TestDataDataType.SimpleValues);
        foreach (var q in step.QueryParameters) Add(q.Value, "query", BirkNext.TestData.TestDataDataType.SimpleValues);
        if (step.BodyTemplate is { } body)
        {
            var whole = PlaceholderPattern.Match(body.Trim()) is { Success: true } m && m.Value == body.Trim();
            Add(body, "body", whole ? BirkNext.TestData.TestDataDataTypes.ForMediaType(step.ContentType ?? "application/json") is var t && t == BirkNext.TestData.TestDataDataType.SimpleValues ? BirkNext.TestData.TestDataDataType.JsonPayload : BirkNext.TestData.TestDataDataTypes.ForMediaType(step.ContentType ?? "application/json")
                : BirkNext.TestData.TestDataDataType.SimpleValues);
        }
        Add(step.GraphQlVariablesJson, "variable", BirkNext.TestData.TestDataDataType.SimpleValues);
        return found;
    }

    /// <summary>Test Data with the exact requirement when known (consumer, definition, operation, variable, data type, content type, environment).</summary>
    public static string TestDataHref(PerformanceTestDefinition d, HttpPerformanceStep? step, string? variable, string? place, BirkNext.TestData.TestDataDataType? type)
    {
        static string E(string? v) => Uri.EscapeDataString(v ?? "");
        var href = $"/test-data?consumer=performance-test-review{(d.Id.Length == 0 ? "" : $"&consumerId={E(d.Id)}")}&environment={E(d.EnvironmentName)}";
        if (step is null || variable is null) return href;
        return href + $"&operation={E($"{step.Method} {step.RelativePath}")}&variable={E(variable)}&in={E(place)}&dataType={type}{(step.ContentType is { } ct && place == "body" ? $"&contentType={E(ct)}" : "")}";
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
