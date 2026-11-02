using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// The live execution rules, pure and deterministic: one observation tick (container observation from the runtime + the provider's own
/// telemetry stream) applied to a run's evidence, plus the watchdog decision. A poll only sets <see cref="PerformanceExecutionEvidence.LastProviderPollAt"/>;
/// stages advance only on real signals; nothing is inferred from elapsed time except "finalizing" after the configured workload end.
/// </summary>
public static class PerformanceLiveMonitor
{
    public sealed record WatchdogStop(PerformanceStopReason Reason, string Detail);

    public static (PerformanceExecutionEvidence Evidence, WatchdogStop? Stop) Apply(PerformanceExecutionEvidence e, DateTimeOffset now, DateTimeOffset providerStartedAt,
        PerformanceContainerObservation? container, bool containerInstrumented, PerformanceLiveTelemetry? telemetry, PerformanceTestOptions options)
    {
        e = e with { LastProviderPollAt = now };
        // Container (the container's main process is the k6 process itself).
        if (!containerInstrumented) e = e with { Container = PerformanceObservedState.NotInstrumented, K6Process = e.K6Process == PerformanceObservedState.Unknown ? PerformanceObservedState.NotInstrumented : e.K6Process };
        else if (container is { Exists: true, Running: true })
        {
            if (e.ContainerStartedAt is null) e = Event(e, now, "Container observed running", container.ContainerId is null ? null : $"container {container.ContainerId}");
            e = e with { Container = PerformanceObservedState.Running, ContainerId = container.ContainerId ?? e.ContainerId, ContainerStartedAt = e.ContainerStartedAt ?? now, ContainerObservedAt = now,
                K6Process = PerformanceObservedState.Running };
            e = Advance(e, PerformanceRunStage.StartingK6, now);
        }
        else if (container is { Exists: false } && e.Container == PerformanceObservedState.Running)
            e = Event(e with { Container = PerformanceObservedState.Gone, K6Process = PerformanceObservedState.Exited, K6ExitedAt = now, ContainerObservedAt = now }, now, "Container no longer present");
        else if (container is { Exists: true, Running: false } && e.Container == PerformanceObservedState.Running)
            e = Event(e with { Container = PerformanceObservedState.Exited, K6Process = PerformanceObservedState.Exited, K6ExitedAt = now, ContainerObservedAt = now }, now, "k6 process exited");

        // Telemetry: k6's own stream. Not opened = not yet received (never 0); opened = counts are real.
        if (telemetry is { } t)
        {
            if (t.StreamOpened)
            {
                if (e.ScriptInitializedAt is null) e = Event(e, now, "Scenario initialised", "k6 opened its output stream after loading the script");
                e = e with
                {
                    ScriptInitializedAt = e.ScriptInitializedAt ?? t.StreamOpenedAt ?? now, Telemetry = PerformanceTelemetryState.Receiving,
                    RequestsObserved = t.Requests, FailedRequestsObserved = t.FailedRequests, IterationsObserved = t.Iterations,
                    FirstRequestAt = t.FirstRequestAt, LastRequestAt = t.LastRequestAt, FirstIterationAt = t.FirstIterationAt, LastIterationAt = t.LastIterationAt,
                    LastHeartbeatAt = t.LastHeartbeatAt ?? e.LastHeartbeatAt,
                    // A live k6 stream proves the k6 process ran even where the container itself is not observable.
                    K6Process = e.K6Process is PerformanceObservedState.Unknown or PerformanceObservedState.NotInstrumented ? PerformanceObservedState.Running : e.K6Process,
                };
                e = Advance(e, PerformanceRunStage.LoadingScenario, now);
                if (t.Requests > 0 && e.Stage < PerformanceRunStage.Running) e = Advance(Event(e, now, "First request observed", t.FirstRequestAt?.ToString("u")), PerformanceRunStage.Running, now);
            }
            else if (t.Error is not null) e = e with { Telemetry = PerformanceTelemetryState.Failed };
            else e = e with { Telemetry = PerformanceTelemetryState.NotYetReceived };
        }
        if (e.ExpectedWorkloadEndAt is { } end && now > end && e.Stage is >= PerformanceRunStage.StartingK6 and < PerformanceRunStage.Finalizing)
            e = Advance(e, PerformanceRunStage.Finalizing, now);

        return Watchdog(e, now, providerStartedAt, telemetry is not null, options);
    }

    /// <summary>
    /// Startup: an instrumented run whose scenario was not initialised within <see cref="PerformanceTestOptions.StartupTimeoutSeconds"/> is stopped.
    /// During the workload: heartbeat silence beyond <see cref="PerformanceTestOptions.HeartbeatStaleSeconds"/> "appears stuck" (shown, not acted
    /// on) and beyond <see cref="PerformanceTestOptions.WatchdogTerminateSeconds"/> is terminated — only when heartbeats were being received, so
    /// a provider without live telemetry is never called stuck. After the workload end, silence is expected (finalizing); the provider timeout bounds it.
    /// </summary>
    public static (PerformanceExecutionEvidence, WatchdogStop?) Watchdog(PerformanceExecutionEvidence e, DateTimeOffset now, DateTimeOffset providerStartedAt, bool instrumented, PerformanceTestOptions options)
    {
        if (e.StopReason != PerformanceStopReason.None) return (e, null);
        if (instrumented && e.ScriptInitializedAt is null && now - providerStartedAt > TimeSpan.FromSeconds(options.StartupTimeoutSeconds))
            return (e with { AppearsStuck = true, StuckDetail = $"k6 did not initialise the scenario within {options.StartupTimeoutSeconds} s." },
                new(PerformanceStopReason.StartupTimeout, $"k6 did not initialise the scenario within {options.StartupTimeoutSeconds} s of the provider start."));
        var inWorkload = e.ExpectedWorkloadEndAt is not { } end || now <= end;
        if (inWorkload && e.LastHeartbeatAt is { } beat)
        {
            var silent = now - beat;
            if (silent > TimeSpan.FromSeconds(options.WatchdogTerminateSeconds))
                return (e with { AppearsStuck = true, StuckDetail = $"No k6 heartbeat for {silent.TotalSeconds:0} s." },
                    new(PerformanceStopReason.Watchdog, $"No k6 heartbeat for {silent.TotalSeconds:0} s (limit {options.WatchdogTerminateSeconds} s) during the workload."));
            if (silent > TimeSpan.FromSeconds(options.HeartbeatStaleSeconds))
                return (e.AppearsStuck ? e : Event(e with { AppearsStuck = true, StuckDetail = $"No k6 heartbeat for {silent.TotalSeconds:0} s." }, now, "Run may be stuck", $"no heartbeat for {silent.TotalSeconds:0} s"), null);
            return (e with { AppearsStuck = false, StuckDetail = null }, null);
        }
        return (e, null);
    }

    public static PerformanceExecutionEvidence Advance(PerformanceExecutionEvidence e, PerformanceRunStage stage, DateTimeOffset now) =>
        stage <= e.Stage ? e : Event(e with { Stage = stage, StageChangedAt = now }, now, PerformanceExecutionAssessment.StageLabel(stage));

    public const int MaxTimelineEvents = 80;

    public static PerformanceExecutionEvidence Event(PerformanceExecutionEvidence e, DateTimeOffset at, string name, string? detail = null) =>
        e.Timeline.Count >= MaxTimelineEvents ? e : e with { Timeline = [.. e.Timeline, new PerformanceTimelineEvent(at, name, detail)] };
}
