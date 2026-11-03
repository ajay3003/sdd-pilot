using System.Text.Json.Serialization;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.PerformanceTests;

// ── Endpoint evidence of a discovered step (Run page + backend readiness) ───────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceEndpointEvidenceState { Current, NewerAvailable, Historical, Stale, NotInHistory, Unknown }

/// <summary>How a discovered step's reviewed evidence compares with the persisted Endpoint Discovery history (stable identity, then time).</summary>
public sealed record PerformanceEndpointEvidenceInfo(
    PerformanceEndpointEvidenceState State, string Identity, DateTimeOffset? ReviewedAgainst, DateTimeOffset? LatestObserved, string? LatestRunId,
    bool AuthenticationRequirementChanged);

public static class PerformanceEndpointEvidence
{
    /// <summary>Observations newer than the reviewed one by less than this are the same observation (clock rounding), not newer evidence.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    public static PerformanceEndpointEvidenceInfo Of(HttpPerformanceStep step, ObservedOperationHistory? latest, EndpointDiscoveryRunSummary? latestRun, DateTimeOffset now)
    {
        var identity = PerformanceScenarioRuntime.ObservationIdentity(step);
        var reviewed = step.Source?.ObservedLastSeen;
        if (latest is null) return new(PerformanceEndpointEvidenceState.NotInHistory, identity, reviewed, null, null, false);
        var authChanged = latest.AuthObserved && step.Source?.ObservedAuthentication is null;
        var state = reviewed is { } r && latest.LastObservedAt > r + Tolerance ? PerformanceEndpointEvidenceState.NewerAvailable
            : latestRun is not null && latest.LastRunId == latestRun.RunId ? PerformanceEndpointEvidenceState.Current
            : now - latest.LastObservedAt <= TimeSpan.FromHours(24) ? PerformanceEndpointEvidenceState.Current
            : now - latest.LastObservedAt <= TimeSpan.FromDays(30) ? PerformanceEndpointEvidenceState.Historical
            : PerformanceEndpointEvidenceState.Stale;
        return new(state, identity, reviewed, latest.LastObservedAt, latest.LastRunId, authChanged);
    }

    /// <summary>
    /// One line for a step's endpoint evidence, the same on the step card and in Readiness: "Observed 2× · last observed 09:27 UTC · Follow latest
    /// observation", "Newer observation available · …", or "Not in Endpoint Discovery history".
    /// </summary>
    public static string Summary(PerformanceEndpointEvidenceInfo info, int? count = null, string? tracking = null)
    {
        var observed = count is > 0 ? $"Observed {count}×" : "Observed";
        var last = info.LatestObserved is { } at ? $" · last observed {at.ToUniversalTime():HH:mm} UTC" : "";
        var follow = tracking is { Length: > 0 } t ? $" · {t}" : "";
        return info.State switch
        {
            PerformanceEndpointEvidenceState.NotInHistory => Label(info.State),
            PerformanceEndpointEvidenceState.NewerAvailable => $"Newer observation available{last}{follow}",
            PerformanceEndpointEvidenceState.Current => $"{observed}{last}{follow}",
            _ => $"{observed} · {Label(info.State).ToLowerInvariant()}{last}{follow}",
        };
    }

    /// <summary>"Reviewed against discovery 09:27 UTC · Latest discovery 09:27 UTC · No changes" — what the reviewed values were based on.</summary>
    public static string Comparison(PerformanceEndpointEvidenceInfo info) => info.LatestObserved is not { } latest ? ""
        : $"Reviewed against discovery {(info.ReviewedAgainst is { } r ? $"{r.ToUniversalTime():HH:mm} UTC" : "not recorded")} · Latest discovery {latest.ToUniversalTime():HH:mm} UTC · "
          + (info.State == PerformanceEndpointEvidenceState.NewerAvailable ? "Newer observation available — review its values before running" : "No changes");

    public static string Label(PerformanceEndpointEvidenceState state) => state switch
    {
        PerformanceEndpointEvidenceState.Current => "Observed",
        PerformanceEndpointEvidenceState.NewerAvailable => "Newer evidence available",
        PerformanceEndpointEvidenceState.Historical => "Historical",
        PerformanceEndpointEvidenceState.Stale => "Stale",
        PerformanceEndpointEvidenceState.NotInHistory => "Not in Endpoint Discovery history",
        _ => "Unknown",
    };
}

// ── Execution evidence: what was actually observed about a run (never inferred from orchestration) ──────────────────────────────────

/// <summary>Lifecycle stage — separate from the terminal outcome (<see cref="PerformanceRunState"/>). Only ever advances.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceRunStage { Queued, Preparing, StartingContainer, StartingK6, LoadingScenario, Running, Finalizing, CollectingResults, Finished }

/// <summary>Why a run stopped before completing. Always the observed cause — an external stop is never reported as the user's.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceStopReason { None, CancelledByUser, Watchdog, StartupTimeout, ExecutionTimeout, ProviderFailure, UnexpectedProcessExit, ContainerStoppedExternally, ApplicationShutdown, StartupFailure, Unknown }

/// <summary>Request/iteration telemetry availability. None of these is the number 0.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceTelemetryState { NotInstrumented, NotYetReceived, Receiving, Collected, Unavailable, Failed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceObservedState { Unknown, NotInstrumented, NotStarted, Running, Exited, Gone }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceResultArtifactState { Pending, Parsed, Missing, ParseFailed, NotProduced }

public sealed record PerformanceTimelineEvent(DateTimeOffset At, string Event, string? Detail = null);

/// <summary>
/// One run's execution evidence, persisted with the run and kept after cleanup. Every timestamp/count is a real observation: container
/// state from the container runtime, k6 activity from k6's own JSON output stream (http_reqs / iterations / vus samples), exit code and result
/// artifact from the provider. A poll is not a heartbeat; a heartbeat is not load; an estimate is never a count. Null count = not known.
/// </summary>
public sealed record PerformanceExecutionEvidence
{
    public PerformanceRunStage Stage { get; init; } = PerformanceRunStage.Queued;
    public DateTimeOffset? StageChangedAt { get; init; }
    /// <summary>When the workload is expected to end (start + configured duration); finalization starts after it.</summary>
    public DateTimeOffset? ExpectedWorkloadEndAt { get; init; }

    public PerformanceObservedState Container { get; init; } = PerformanceObservedState.Unknown;
    public string? ContainerId { get; init; }
    public DateTimeOffset? ContainerStartedAt { get; init; }
    public DateTimeOffset? ContainerObservedAt { get; init; }

    public PerformanceObservedState K6Process { get; init; } = PerformanceObservedState.Unknown;
    /// <summary>k6 opened its output stream: the script was loaded and the test initialised (the stream is created only after initialisation).</summary>
    public DateTimeOffset? ScriptInitializedAt { get; init; }
    public DateTimeOffset? K6ExitedAt { get; init; }
    public int? ExitCode { get; init; }

    public PerformanceTelemetryState Telemetry { get; init; } = PerformanceTelemetryState.NotInstrumented;
    public string? TelemetrySource { get; init; }
    public int? RequestsObserved { get; init; }
    public int? FailedRequestsObserved { get; init; }
    public int? IterationsObserved { get; init; }
    public DateTimeOffset? FirstRequestAt { get; init; }
    public DateTimeOffset? LastRequestAt { get; init; }
    public DateTimeOffset? FirstIterationAt { get; init; }
    public DateTimeOffset? LastIterationAt { get; init; }
    /// <summary>The k6 engine's own periodic sample (vus) — process liveness, not load.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    /// <summary>BirkNext asked the provider/runtime for status (a poll — never proof of execution).</summary>
    public DateTimeOffset? LastProviderPollAt { get; init; }

    public PerformanceResultArtifactState ResultArtifact { get; init; } = PerformanceResultArtifactState.Pending;
    public string? ResultArtifactDetail { get; init; }

    public PerformanceStopReason StopReason { get; init; }
    public DateTimeOffset? StopRequestedAt { get; init; }
    public string? StopDetail { get; init; }
    /// <summary>Container cleanup, recorded separately from the outcome: null = not yet, true = removed, false = not confirmed removed.</summary>
    public bool? ContainerRemoved { get; init; }

    public bool AppearsStuck { get; init; }
    public string? StuckDetail { get; init; }
    /// <summary>Bounded, redacted tail of the provider output (no tokens, headers, cookies or bodies).</summary>
    public string? LogTail { get; init; }
    public List<PerformanceTimelineEvent> Timeline { get; init; } = [];

    /// <summary>Latest genuine execution activity (request or iteration observed) — never a poll.</summary>
    [JsonIgnore] public DateTimeOffset? LastActivityAt => new[] { LastRequestAt, LastIterationAt }.Where(t => t is not null).Max();
}

/// <summary>Evidence-derived milestones (several may apply), the strongest one, and whether load generation was confirmed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PerformanceExecutionLevel
{
    LegacyUnavailable, NotStarted, InfrastructureOnly, ProcessStarted, ScenarioInitialized, ExecutionActivityObserved, HttpTrafficObserved, PartialResults, FinalResults,
}

public sealed record PerformanceExecutionMilestone(string Key, string Label, string Status, DateTimeOffset? At = null);

public static class PerformanceExecutionAssessment
{
    public const string Confirmed = "Confirmed", NotObserved = "Not observed", Unknown = "Unknown", NotInstrumented = "Not instrumented";

    /// <summary>The strongest milestone the persisted evidence supports. A run is "HTTP traffic observed" only with positive, attributable evidence.</summary>
    public static PerformanceExecutionLevel Level(PerformanceTestRun run)
    {
        if (run.ExecutionEvidence is not { } e) return run.Metrics is { RequestCount: > 0 } ? (run.MetricsPartial ? PerformanceExecutionLevel.PartialResults : PerformanceExecutionLevel.FinalResults) : PerformanceExecutionLevel.LegacyUnavailable;
        if (run.Metrics is not null && run.State == PerformanceRunState.Completed && !run.MetricsPartial) return PerformanceExecutionLevel.FinalResults;
        if (run.Metrics is not null && run.MetricsPartial) return PerformanceExecutionLevel.PartialResults;
        if (e.RequestsObserved is > 0 || e.FirstRequestAt is not null) return PerformanceExecutionLevel.HttpTrafficObserved;
        if (e.IterationsObserved is > 0 || e.FirstIterationAt is not null) return PerformanceExecutionLevel.ExecutionActivityObserved;
        if (e.ScriptInitializedAt is not null) return PerformanceExecutionLevel.ScenarioInitialized;
        if (e.K6Process is PerformanceObservedState.Running or PerformanceObservedState.Exited || e.ExitCode is not null) return PerformanceExecutionLevel.ProcessStarted;
        if (e.ContainerStartedAt is not null || e.Container is PerformanceObservedState.Running) return PerformanceExecutionLevel.InfrastructureOnly;
        return PerformanceExecutionLevel.NotStarted;
    }

    /// <summary>True only with positive evidence from this run (requests observed in k6's stream, or a summary that counted requests).</summary>
    public static bool LoadConfirmed(PerformanceTestRun run) => run.ExecutionEvidence?.RequestsObserved is > 0 || run.Metrics is { RequestCount: > 0 };

    public static string Summary(PerformanceTestRun run)
    {
        var e = run.ExecutionEvidence;
        return Level(run) switch
        {
            PerformanceExecutionLevel.LegacyUnavailable => "Legacy evidence unavailable — this run predates execution evidence; whether load was generated is unknown.",
            PerformanceExecutionLevel.FinalResults => $"Final results available — {run.Metrics!.RequestCount} request(s) recorded by k6.",
            PerformanceExecutionLevel.PartialResults => $"Partial results — {run.Metrics!.RequestCount} request(s) recorded before the run stopped; not a final result.",
            PerformanceExecutionLevel.HttpTrafficObserved => $"HTTP traffic observed — {e!.RequestsObserved?.ToString() ?? "some"} request(s) recorded by k6.",
            PerformanceExecutionLevel.ExecutionActivityObserved => "Execution activity observed — iterations recorded; HTTP request generation unverified.",
            PerformanceExecutionLevel.ScenarioInitialized => e!.Telemetry is PerformanceTelemetryState.Receiving or PerformanceTelemetryState.Collected && e.RequestsObserved == 0
                ? "Scenario initialised — k6 recorded 0 requests." : "Scenario initialised — request generation unverified.",
            PerformanceExecutionLevel.ProcessStarted => "Process started — request generation unverified.",
            PerformanceExecutionLevel.InfrastructureOnly => "Insufficient evidence — only container startup was confirmed.",
            _ => run.IsActive ? "Not started yet." : "Insufficient evidence — the workload was never observed to start.",
        };
    }

    /// <summary>"210" (measured) · "0" (k6 reported none) · "Unknown — …" · "Not started yet" — never a fabricated 0.</summary>
    public static string RequestsLabel(PerformanceTestRun run)
    {
        if (run.ExecutionEvidence is not { } e) return run.Metrics is { } m ? m.RequestCount.ToString() : "Unknown — legacy evidence unavailable";
        return e.Telemetry switch
        {
            PerformanceTelemetryState.Receiving or PerformanceTelemetryState.Collected => (e.RequestsObserved ?? 0).ToString(),
            PerformanceTelemetryState.NotYetReceived => run.IsActive ? "Not started yet" : "Unknown — no request telemetry was received",
            PerformanceTelemetryState.Failed => "Unknown — request telemetry could not be read",
            PerformanceTelemetryState.Unavailable => "Unknown — request telemetry unavailable",
            _ => "Unknown — live request metrics unavailable for this provider",
        };
    }

    public static IReadOnlyList<PerformanceExecutionMilestone> Milestones(PerformanceTestRun run)
    {
        var e = run.ExecutionEvidence;
        if (e is null) return [new("legacy", "Execution evidence", "Legacy evidence unavailable")];
        string Seen(bool seen, bool instrumented = true) => seen ? Confirmed : !instrumented ? NotInstrumented : run.IsActive ? "Not yet" : NotObserved;
        var telemetry = e.Telemetry is not PerformanceTelemetryState.NotInstrumented;
        var telemetryRead = e.Telemetry is PerformanceTelemetryState.Receiving or PerformanceTelemetryState.Collected;
        return
        [
            new("container", "Container started", e.ContainerStartedAt is not null || e.Container == PerformanceObservedState.Running ? Confirmed
                : e.Container == PerformanceObservedState.NotInstrumented ? NotInstrumented : e.Container == PerformanceObservedState.Unknown ? Unknown : Seen(false), e.ContainerStartedAt),
            new("k6", "k6 process started", e.K6Process is PerformanceObservedState.Running or PerformanceObservedState.Exited || e.ExitCode is not null || e.ScriptInitializedAt is not null ? Confirmed
                : e.K6Process == PerformanceObservedState.NotInstrumented ? NotInstrumented : Unknown, e.ScriptInitializedAt),
            new("init", "Scenario initialised", Seen(e.ScriptInitializedAt is not null, telemetry), e.ScriptInitializedAt),
            new("iteration", "First iteration completed", Seen(e.FirstIterationAt is not null, telemetry), e.FirstIterationAt),
            new("requests", "HTTP requests observed", e.RequestsObserved is > 0 ? Confirmed : telemetryRead && e.RequestsObserved == 0 && !run.IsActive ? "None (0 recorded)" : telemetry ? (run.IsActive ? "Not yet" : Unknown) : Unknown, e.FirstRequestAt),
            new("metrics", "Metrics captured", run.Metrics is null ? (run.IsActive ? "Not yet" : "None available") : run.MetricsPartial ? "Partial" : "Final"),
            new("heartbeat", "Last k6 heartbeat", e.LastHeartbeatAt is null ? (telemetry ? Unknown : NotInstrumented) : Confirmed, e.LastHeartbeatAt),
            new("cleanup", "Container cleanup", e.ContainerRemoved switch { true => "Completed", false => "Not confirmed", _ => run.IsActive ? "Not yet" : Unknown }),
        ];
    }

    public static string StopReasonLabel(PerformanceStopReason reason) => reason switch
    {
        PerformanceStopReason.CancelledByUser => "Cancelled by user",
        PerformanceStopReason.Watchdog => "Terminated by watchdog",
        PerformanceStopReason.StartupTimeout => "Startup timeout",
        PerformanceStopReason.ExecutionTimeout => "Execution timeout",
        PerformanceStopReason.ProviderFailure => "Provider failure",
        PerformanceStopReason.UnexpectedProcessExit => "Unexpected process exit",
        PerformanceStopReason.ContainerStoppedExternally => "Container stopped externally",
        PerformanceStopReason.ApplicationShutdown => "Interrupted — BirkNext stopped",
        PerformanceStopReason.StartupFailure => "Startup failure",
        PerformanceStopReason.Unknown => "Unknown cause",
        _ => "",
    };

    public static string StageLabel(PerformanceRunStage stage) => stage switch
    {
        PerformanceRunStage.StartingContainer => "Starting container",
        PerformanceRunStage.StartingK6 => "Starting k6",
        PerformanceRunStage.LoadingScenario => "Loading scenario",
        PerformanceRunStage.Finalizing => "Finalizing",
        PerformanceRunStage.CollectingResults => "Collecting results",
        _ => stage.ToString(),
    };
}
