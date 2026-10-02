# Resource Stability (Performance Test Review)

Resource Stability answers one question, inside Performance Test Review:

> Under this controlled workload, in this environment, for this long — did the approved components' memory and CPU stabilize, stay bounded, or show sustained growth compared with an explicit policy or baseline?

It is **not** a memory-leak detector. There is deliberately no "Memory leak detected" state, finding or score.

## Why BirkNext does not claim generic memory-leak detection

| Observation | Why it is not a leak verdict |
|---|---|
| Memory increased | Caches, JIT, connection pools and lazy loading grow memory legitimately, especially early. |
| Heap increased | The managed heap rises between collections and drops after them (sawtooth). A peak says nothing about retention. |
| Working set / container memory increased | The runtime reserves memory and returns it lazily. Container memory is not the managed heap. |
| GC occurred, or no GC occurred | Collection timing is nondeterministic; neither is a problem by itself. |
| Short-run growth | It does not prove a trend, and a short stable run does not prove there is no leak. |
| Browser JS heap | It does not represent Blazor/.NET WASM managed memory. Detached DOM nodes do not prove managed event-handler retention. |
| A policy is exceeded | That is a **resource regression for this scenario**, not a proven leak and not a known root cause. |

Universal leak thresholds do not exist, so every policy is project- and environment-specific. Without a policy the evidence is descriptive only.

## Architecture

```
Performance Test Definition (ResourceObservation)
        │
Workload: k6 provider ──► Podman runtime ──► target            in parallel:  ResourceObservationSession (low-frequency timer)
                                                                                   │  IResourceObservationProvider per approved component
                                                                                   ▼
                                                    bounded samples ─► ResourceStabilityAnalyzer
                                                    (warm-up exclusion, steady-state windows, medians, trend, policies)
                                                                                   ▼
                                                    ResourceStabilityAssessment on the run (+ ResourceDrift vs the run's baseline)
```

| Piece | Where |
|---|---|
| Contracts (samples, configuration, policies, summaries, states, drift) | `shared/PerformanceResourceContracts.cs` |
| Providers, registry, session, shutdown | `backend/.../PerformanceTests/Resources/ResourceObservation.cs` |
| Analysis | `backend/.../PerformanceTests/Resources/ResourceStabilityAnalyzer.cs` |
| Runtime stats/inspect | `IContainerExecutionRuntime.StatsAsync` / `InspectAsync` / `ControllersAsync` |
| UI | Resources tab (configuration); Run, Results, Compare and History sections; System Settings → Performance Test Engines |

Generic performance models carry only the provider-neutral `ResourceStabilityAssessment`. Podman and .NET specifics stay in providers.

## Providers

| Provider | Status | What it measures |
|---|---|---|
| `resource.podman` | Implemented | Container memory, memory limit, CPU % of **approved** containers and the run's own k6 container (fixed `inspect` fields; never environment, mounts or commands) |
| `resource.dotnet.runtime` | Implemented — **BirkNext's own API process only** | GC heap, post-GC heap floor, LOH, allocation rate, Gen 0/1/2 counts, GC pause, working set, private bytes, CPU, threads, handles (in-process; no attach) |
| `resource.browser` | Unsupported by design | — |
| `resource.opentelemetry`, `resource.appinsights` | Not implemented | Future adapters for exported runtime metrics of other services; Resource Stability does not depend on Azure |

**.NET runtime metrics of other applications are not available.**
- BirkNext has no exported runtime-metrics pipeline (no OpenTelemetry metrics or App Insights runtime counters are consumed).
- Attaching to another process or container (dotnet-counters/diagnostics IPC) would need privileged or shared-namespace access, which the deployment model does not allow.
- For any target other than BirkNext itself, the .NET provider therefore reports Unsupported. Only container-level evidence exists for those targets, and results say so.

**Rootless Podman:**
- `podman stats` measures CPU, but reports memory and PIDs as `0` when no memory/pids cgroup controller is delegated. This is the case on the reference developer machine (Podman 5.4.2, WSL, rootless).
- BirkNext treats that `0` as **Unavailable, never zero**. The provider status shows *Partial (CPU only)*.

## Targets — approved only

- **Configured targets.** An administrator lists observable components under `PerformanceTests:Resources:Targets`, each with `Id`, `DisplayName`, `Provider`, `Container` and optional `Environments`.
- **Built-in self target.** The built-in `birknext-api` target is BirkNext's own process (`EnableSelfObservation`).
- **No enumeration.** The API exposes only these targets; there is no container enumeration. A definition naming any other id is blocked: "not an approved resource target".
- **Internal names.** Internal container names are never returned to the browser.
- **Load generator.** The run's k6 container is observed separately as **load-generator health** when `ObserveLoadGenerator` is on. It is never treated as application stability; high generator CPU raises `LoadGeneratorSaturationPossible`.
- **No summing.** Each component is assessed separately. Nothing is summed across components.

## Configuration (definition → Resources tab)

| Field | Default and bounds |
|---|---|
| `Enabled` | — |
| `TargetComponentIds` | Approved ids |
| `ObserveLoadGenerator` | — |
| `SampleIntervalSeconds` | Default 10 s; bounds `MinSampleIntervalSeconds`–`MaxSampleIntervalSeconds`, 5–60 by default |
| `WarmupExclusionSeconds` | Default: workload warm-up + ramp-up |
| `CooldownSeconds` | 0 – `MaxCooldownSeconds` (default max 600) |
| `Policies` | — |
| `DriftPolicies` | — |
| `RequireResourceEvidence` | Default off: telemetry never gates the load test |

Backend options (`PerformanceTests:Resources`): `Targets`, `EnableSelfObservation`, sampling bounds, `MaxCooldownSeconds`, and `MaxSamplesPerComponent` (default 720, clamped 60–5000).

## Analysis

**Windows:**
- Warm-up `[start, start + exclusion)` is excluded from the trend.
- Steady state is `[start + exclusion, workload end]`.
- The cooldown after the workload is reported separately (post-load recovery; descriptive).

**Statistics:**
- Early steady = median of the first third of steady state; late steady = median of the last third.
- Also reported: steady-state p95, peak, start/end.
- Growth = late − early (absolute and relative).
- Least-squares slope per minute with R² confidence (High ≥ 0.7, Medium ≥ 0.4, else Low).
- Medians and p95 are used, so one GC spike does not make a regression.

**Rate metrics:**
- Gen 2 collections and GC pause per minute are deltas between samples of the same instance. A process restart is never bridged.

**GC-aware:**
- `GcHeapAfterGcBytes` is recorded only when a new GC has completed, so it is the post-GC floor.
- A sawtooth heap with a stable floor is not a regression. A rising floor beyond a policy is `ManagedHeapFloorRegression`.

**Discontinuities:**
- A change of instance (container id, start time or restart count; or process id and start time) is recorded.
- Affected metrics become **NotComparable**, and the finding `TargetRestartedDuringTest` is raised.

## States

| State | When |
|---|---|
| StableWithinPolicy | Sufficient evidence, every configured policy met, no rising trend |
| IncreasingWithinTolerance | Rising steady-state trend (medium/high confidence) still within every policy |
| PotentialRegression | An advisory policy exceeded, or growth beyond a policy on evidence shorter or sparser than that policy requires — needs investigation |
| Regression | Comparable metric, sufficient evidence (≥ 6 steady samples and the policy's minimum duration/samples), explicit **required** policy violated |
| InsufficientEvidence | Too few steady samples (warm-up consumed the run, cancelled early, provider started late), and no growth beyond a policy |
| NotComparable | Discontinuity during the workload |
| NotAssessed | Metrics available but no policy: **descriptive only — never Pass** |
| Unavailable | The provider did not report the metric (never shown as 0) |

## Policies

`ResourceStabilityPolicy` has:
- `Metric`, optional `TargetComponentId`;
- `AllowedRelativeGrowthPercent`, `AllowedAbsoluteGrowth`, `AllowedSlopePerMinute`;
- `MaxValue` (an absolute ceiling on the steady-state p95);
- `MinimumObservationSeconds`, `MinimumSampleCount`;
- `Severity` (Required / Advisory).

No defaults are presented as industry truth.

**Shared scoring:**
- Evaluable resource policy outcomes count in the run's shared `ScoreSemantics`, together with thresholds.
- Insufficient or unavailable evidence is a coverage gap, never a failure.
- Provider or telemetry failure never fails the performance run.

## Baselines and drift

- **Reuses the Performance Test baseline lifecycle.** A baseline run carries both performance and resource evidence; there is no second baseline model.
- **Drift is computed against the baseline active when the run was created**, and stored on the run (`ResourceDrift`). It is never rewritten when a later baseline is promoted.
- **Statistics compared:** late steady state, peak, growth and trend, per component and metric.
- **`ResourceDriftPolicy`** has `Metric`, `Statistic`, accepted relative/absolute change and `Severity`. Without one, drift is descriptive.
- **Absolute resource policies and drift are separate, and both are shown.** Example: a 1 GiB ceiling met at 700 MiB while drift reports +75 % against a 400 MiB baseline.
- **Compatibility:**
  - It requires the run-level performance compatibility: environment, target, comparison fingerprint (workload and scenario), and complete metrics.
  - Per component, it also requires the same provider and the same memory limit. A changed limit means sizing differs, so the comparison is NotComparable.
- **A zero baseline has no relative change**; only the absolute delta is reported.
- **Immutability:** each run stores its summaries, bounded samples and `PolicyFingerprint`. Later policy edits, runs or baselines never re-assess it.

## Lifecycle and bounds

- **Start and end.** Collection starts with the workload, with the first round immediate. Partial evidence is persisted after every round (`InProgress`), so it survives a cancel or a restart.
- **Normal stop.** Collection stops before the run is finished: after the optional cooldown, or immediately on completion, cancellation, timeout, execution failure or API shutdown.
- **Shutdown.** `PerformanceTestShutdownService` cancels active runs, so k6 containers are removed and collectors stop.
- **Bounded storage.** At most `MaxSamplesPerComponent` samples are kept. Beyond that the series is halved deterministically by averaging neighbouring samples of the same instance, never across an instance change. Peaks after downsampling are maxima of those means.
- **Persistence.** A run is one JSON document, so DB growth is bounded per run. Exports contain summaries, findings and limitations — no raw samples.

## Self-regression (BirkNext's own lifecycle)

These are lifecycle-correctness tests, not leak verdicts:
- **Workspace switching** (A → B → A … 64×):
  - Revisions are captured for new content only; switching back re-selects the existing revision. Before this change every switch stored another full copy of the content in the persisted lifecycle.
  - Superseded `ReviewContext`s become collectable.
- **Authenticated browser sessions:** 25 start/cancel cycles dispose every owned browser exactly once and retain no session.
- **k6 runs:** repeated runs leave no BirkNext-managed k6 container, no temp directory and no running collector. Cancellation, timeout and shutdown stop collectors.
- **Live soak diagnostic** (gated on Podman): a real k6 container against a throw-away container.
  - It observes the target (CPU; memory where the host can account it), the generator and BirkNext's own runtime.
  - It verifies cleanup and that the finished run is immutable.

## Limitations

- Only BirkNext's own process has .NET managed-memory evidence. Other targets have container-level evidence only, and the results state this.
- On rootless Podman without delegated cgroup controllers, container memory is Unavailable. Only CPU is measured.
- The browser is not a resource provider. JS heap figures, where they appear elsewhere, are never used for stability verdicts.
- Repeated load/idle cycle workloads are not a separate workload shape yet. The cooldown gives one post-load recovery window.
- No root cause is ever assigned. Managed heap, allocation rate, Gen 2 frequency and CPU are supporting evidence only.

## If a regression is detected — next diagnostics

1. Re-run the same scenario longer (soak) and confirm the trend is reproducible against the same baseline.
2. Compare the post-GC floor and allocation rate, not peaks. If only container memory exists, add runtime telemetry for the service: OpenTelemetry runtime metrics or App Insights.
3. Take a heap snapshot or `dotnet-gcdump` in a controlled environment where the team owns the process, and diff the retained types between the early and late steady state.
4. Check the post-load cooldown (does memory return?), caches with unbounded keys, event subscriptions, static collections and connection or pooling configuration.
