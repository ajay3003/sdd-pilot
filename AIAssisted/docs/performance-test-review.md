# Performance Test Review

## Purpose

Performance Test Review measures how a configured HTTP/API target behaves under **controlled load**. It verifies latency, throughput and errors against **explicit expectations**, and keeps a trustworthy history in which baselines change only intentionally.

It answers:
- What is tested, and is it safe?
- What workload is applied?
- Which thresholds apply, and which passed or failed?
- How does the run compare with the baseline?
- What are the limitations?

It is a generic BirkNext capability: project- and technology-independent, with no M2LB, Azure or .NET assumption. k6 is the **first provider**, not the domain model.

### Semantics

| Not the same | Why |
|---|---|
| Configured ≠ Ready ≠ Executed ≠ Completed ≠ Passed | Each is a separate state. |
| Measured ≠ Passed | A metric without a threshold is measured only. |
| Provider unavailable ≠ performance failed | A missing engine is a tool limitation. |
| Execution failure ≠ threshold failure | A crashed run produces no quality result. |
| Cancelled ≠ failed | A cancelled run is not assessed. |
| No evidence / no score ≠ 0 | Nothing assessed means no score. |
| Load-test latency ≠ single-request API latency (API Quality Review) | Separate evidence types. |
| Load-test latency ≠ browser performance (Lighthouse / Core Web Vitals) | Separate evidence types. |
| Latency measured ≠ capacity established | Nothing about capacity is inferred. |

## Architecture

```
Target Environment → Performance Test Definition (scenario, workload, thresholds, drift policy, safety policy; versioned)
  → Readiness / safety validation (backend)
  → IPerformanceTestProvider (registry, stable id) → K6PerformanceTestProvider (generated script, external k6 process)
  → structured provider output → normalized PerformanceMetrics
  → BirkNext threshold evaluation (shared ScoreSemantics) + drift against the baseline active at run time
  → immutable PerformanceTestRun → history / baselines / comparison / export
```

| Layer | Files |
|---|---|
| Contracts and pure rules | `shared/PerformanceTestContracts.cs` (`BirkNext.PerformanceTests`): definitions, workload, thresholds, drift policies, runs, baselines, comparisons; `PerformanceTestRules` for threshold evaluation, quality, deltas, compatibility, drift and timeline |
| Backend | `Services/PerformanceTests/`: `PerformanceTestSafety`, `PerformanceTestReadinessService`, `PerformanceTestExecutionService`, `PerformanceTestStore`, `K6PerformanceTestProvider` (+ `K6ScriptGenerator`, `K6SummaryParser`), `IProcessRunner` / `SystemProcessRunner`; `Controllers/PerformanceTestsController.cs` |
| Persistence | Migration `AddPerformanceTestReview`. Tables `performance_test_definitions` (current version), `performance_test_runs` (each run with its full definition snapshot), `performance_baselines` (versioned, never deleted), `performance_test_data_profiles` |
| Frontend | `/performance-test-review` (QUALITY menu), `PerformanceTestApiService`, `PerformanceTestPresentation`; System Settings → **Performance Test Engines** (installed?, version, capabilities) |
| Applicability | `ReviewCatalog` entry `performance-test-review`: Applicable with an HTTP target; NeedsConfiguration when an HTTP API is detected without a target; NotApplicable for projects without HTTP API or frontend. Never Failed. |

## Safety

Enforced in the backend on save, on readiness, and again immediately before the provider starts. The UI is never the guard.

| Rule | Behaviour |
|---|---|
| Environment | Only Local, Development, QA, Test and RC (the same allow-list as other active BirkNext automation). Production is refused, and unknown or custom environments are refused until classified. A Production definition cannot even be saved. |
| Host | A host carrying a production marker (`prod`, `prd`, `production`, `live`) is refused whatever the claimed environment, so a "QA" profile pointing at a production host is blocked. `PerformanceTests:BlockedHosts` always refuses. `PerformanceTests:AllowedHosts`, when set, allow-lists (loopback always allowed). |
| Destination | The definition stores only the scheme + host + port of the Target Environment. Steps are relative paths: must start with `/`, no `//`, scheme, `..`, query or fragment. There is no free destination field. |
| Methods | GET, HEAD and OPTIONS. POST only when explicitly marked a safe read, or as GraphQL. PUT, PATCH, DELETE, CONNECT and TRACE are blocked. |
| GraphQL | Parsed with the Hot Chocolate parser. Queries are allowed; mutations and subscriptions are blocked; multi-operation documents need an operation name. |
| Headers | Credential-carrying headers (`Authorization`, `Cookie`, `X-Api-Key` …) cannot be stored. |
| Load limits | Backend maximums (configurable) always win; a definition's own safety policy can only be stricter. Defaults: 50 virtual users, 50 req/s, 30 min (soak 60 min), 200 000 requests. Stress and soak can be disabled. The request estimate is exact for arrival rate and a conservative upper bound for virtual users (≥ 50 ms per request plus think time). |
| Concurrency | One active performance test per environment. |
| Scripts | Users cannot supply scripts. BirkNext generates the provider script from a validated definition. |

## Provider model

`IPerformanceTestProvider` has:
- `ProviderId` (stable, e.g. `performance.k6`; never a CLR type name), `DisplayName`, `Capabilities` (HTTP, GraphQL, purposes, modes, cancellation, metrics, external executable);
- `StatusAsync` → Available / Unavailable / VersionUnsupported / Misconfigured;
- `Validate` (provider-specific);
- `ExecuteAsync(input, progress, ct)` → state + normalized metrics + diagnostics.

A provider **never** decides pass/fail; BirkNext evaluates thresholds. `PerformanceTestProviderRegistry` resolves providers by id and caches status for 30 s.

### k6 (first provider)

- **Setup:** install k6 (≥ 0.45) on the BirkNext host, or set `PerformanceTests:K6ExecutablePath`. If it is missing, the provider status is Unavailable and readiness reports "k6 is not installed or configured on this BirkNext host." That is a tool limitation, never a failed test.
- **Script:** generated deterministically by `K6ScriptGenerator`.
  - Every user value (origin, paths, headers, bodies, test data, step names) enters only as a System.Text.Json literal, which escapes quotes, backslashes, `< > &` and U+2028/2029, so it cannot break out of a string.
  - Virtual users map to `ramping-vus`; arrival rate maps to `ramping-arrival-rate` with per-minute integer rates.
  - Expected status codes map to `http.expectedStatuses`, so unexpected statuses count as failed requests.
  - Each step is tagged by name; trivially-true submetric thresholds expose per-step metrics in the summary. BirkNext ignores k6's own threshold verdict.
- **Process:**
  - Launched with `ArgumentList`: no shell, no interpolation.
  - Runs in a controlled temp directory with bounded stdout/stderr capture.
  - Timeout = workload + `ProviderTimeoutGraceSeconds`.
  - On timeout or cancellation the whole process tree is killed.
  - The temp directory (script + summary) is deleted after completion, failure, timeout or cancellation.
- **Output:** a `handleSummary` JSON file, parsed by `K6SummaryParser`. Console text is never parsed. Malformed or missing output is ExecutionFailed with the reason, never zero metrics. Diagnostics are redacted (bearer tokens, sensitive values) and bounded.
- **Exit codes:** 0 and 99 count as finished; anything else is ExecutionFailed.
- **Load-generator saturation:** if k6 reports `dropped_iterations`, the run lists it as a limitation (measured throughput may reflect the generator).

## Supported target types

`RestHttp`, `GraphQlHttp` (query over POST), `GenericHttp`.

Not supported in this version: browser load, Kafka, Service Bus / Event Hubs, database load, distributed generators, Production, raw scripts, arbitrary destinations.

## Workloads

- **Purposes:**
  - **Baseline** — small reference load.
  - **Load** — expected load.
  - **Stress** — controlled load beyond expected capacity.
  - **Soak** — sustained load; has its own maximum duration.
- **Modes:** VirtualUsers, or ArrivalRate (start rate → target rate, with the virtual-user count as the generator pool ceiling). Only the modes the provider supports are offered.
- **Phases:** warm-up (a tenth of the target, at least 1), ramp-up, steady state (at least 10 s), ramp-down.
- **Preview:** the UI shows a text timeline, e.g. `00:00–00:30 Warm-up at 1 virtual users`.

## Thresholds

- **Metrics:** P50, P90, P95, P99, mean and max latency (ms); error rate (%); throughput (req/s); failed requests.
- **Operators:** `<`, `≤`, `>`, `≥`.
- **Severity:**
  - **Required** — a miss is Fail.
  - **Advisory** — a miss is Warning.
- **Quality:** shared `ScoreSemantics` over the threshold outcomes; no second scoring model.
- **Verdict:**
  - Fail if any required threshold failed; Warning if only advisory ones did; otherwise Pass.
  - NotAssessed when nothing was assessed: no thresholds, or a run that did not complete with full metrics. The UI shows "Not assessed", never 0 %.
- **Existing thresholds are not reused:**
  - Core Web Vitals and browser thresholds are a different evidence type.
  - API Quality Review's "single request latency" / "average API latency" are not P95 under load, so they are not mapped automatically.

## Authentication

Authentication reuses the Target Environment concept; no load-test credential store exists.

- The MVP runs unauthenticated tests.
- A definition with an authentication reference is readiness **NeedsAuthentication**: BirkNext does not yet issue an approved non-interactive test credential for load tests.
- Interactive or MFA logins are never simulated.
- No token, cookie, password or key is stored in a definition, run, script or export.

## Test data

- **Profile:** `PerformanceTestDataProfile` holds columns, rows and a selection strategy: RoundRobin, Sequential, Random or UniquePerVirtualUser (which needs at least as many rows as VUs).
- **Approval:** a profile must be confirmed as synthetic or approved.
- **Allowed values:** short plain tokens of letters, digits, space and `- _ . @ :`. Anything that looks like an 11-digit national identity number is refused.
- **Placeholders:** `{name}` in paths, query values, bodies and GraphQL variables resolve from the profile; path values are URL-encoded. There is no environment-variable or shell expansion.

## Execution and results

**Run states:** Queued → Preparing → Running → Completed | ExecutionFailed | TimedOut | Cancelling → Cancelled; Blocked when the last safety re-check fails. A run that BirkNext stopped mid-way (restart) becomes ExecutionFailed.

**Normalized metrics:** request count, successful and failed requests, error rate, RPS, duration, latency min/mean/P50/P90/P95/P99/max, per-step P95/P99/RPS/error rate, dropped iterations.

**Each run records:**
- definition version, fingerprint, comparison fingerprint and full snapshot (thresholds, drift policy, workload);
- provider id and version, target origin, environment type;
- created / started / finished timestamps (the observability window);
- execution host (OS, logical CPUs, BirkNext version — no host name or user);
- optional version label and the latest Source Analysis snapshot id;
- the baseline that was active when it was created.

Finished runs are immutable: saving a finished run throws.

**Polling:** the UI polls every 2 s while a run is active. The provider reports no live metrics; results appear on completion.

## Baselines, comparison and drift

**Baselines:**
- Always explicit: "Promote to baseline". The latest, fastest or passing run never becomes one automatically.
- Only Completed runs with complete metrics qualify. A run whose required thresholds failed needs explicit confirmation.
- Each promotion creates a new versioned baseline (v1, v2 …) for the scope: definition + environment + comparison fingerprint. The previous Active baseline becomes Superseded (with time and successor) and is kept.
- Archive marks a baseline Historical. Baselines are never deleted.
- A reason ("infrastructure upgraded") can be recorded.

**Run-to-baseline binding:** a run stores the baseline active at creation, and its drift assessment is computed once at completion. A later baseline change never rewrites it. Comparing a historical run with a newer baseline is labelled **ad-hoc**.

**Compatibility and the comparison fingerprint:**
- The fingerprint covers target, type, steps, workload, environment class and test-data profile.
- Thresholds, drift policy and names are excluded, so a threshold edit keeps runs comparable while a workload or scenario change does not.
- Incompatible pairs (environment, target, workload, scenario, test data, incomplete metrics) are **NotComparable**: deltas may be shown, but no authoritative drift.

**Deltas:** absolute, and relative (%) where meaningful. With a zero reference the relative change is unavailable, never infinite. Error rate shows percentage points. Direction (Improved / Unchanged within ±1 % / Worse) is descriptive, not a verdict.

**Drift policy (optional):**
- An accepted worsening per metric, relative (%) and/or absolute (ms / pp / requests).
- Without a policy, drift is NotAssessed ("comparison only"); it is never Regression.
- Per-metric states: Improved, Stable, DegradedWithinTolerance, Regression. A Regression produces a `PerformanceRegressionFinding` (metric, baseline, current, delta, policy, severity).
- A regression finding says performance changed beyond tolerance, **not why**. Changing a baseline is a lifecycle event, never a finding.

**Thresholds vs drift:** thresholds are absolute and drift is relative, so the two can disagree. Both are always shown:
- Threshold PASS with drift REGRESSION: P95 400 ms is under the 500 ms threshold but +33 % against a 300 ms baseline, beyond +20 %.
- Threshold FAIL with drift IMPROVED: P95 600 ms misses the 500 ms threshold, but the baseline was 700 ms.

## Observability

Optional enrichment. The run window (start/end) is persisted so a telemetry provider (Application Insights first; OpenTelemetry or others later) can be correlated with the same interval.

- **Status:** observability is reported "Not configured" and never affects quality.
- **No live re-query:** today's telemetry is never attached to an old run as original evidence.
- **Correlation only:** "Database dependency duration increased during the same test interval" — never "the database caused it".

## Limitations (MVP)

- Unauthenticated targets only; authenticated load testing needs an approved non-interactive credential.
- No live progress metrics from k6; elapsed time and state only.
- One load generator (the BirkNext host). Results may reflect generator limits; dropped iterations are reported when k6 detects them.
- No telemetry enrichment yet; the window is recorded.
- No automatic re-evaluation of historical runs against new thresholds or drift policies (a future feature, which would be stored as a separate derived assessment).
- The live k6 integration test is gated (skipped when k6 is not on PATH).

## Adding another provider

1. Implement `IPerformanceTestProvider`:
   - stable `ProviderId` (e.g. `performance.jmeter`) and honest `Capabilities`;
   - `StatusAsync` detecting the runtime;
   - `Validate` for unsupported modes or targets;
   - `ExecuteAsync` translating the generic definition into the engine's own format.
2. Use `IProcessRunner` (no shell, bounded output, process-tree kill) or the engine's API.
3. Parse a **structured** result format into `PerformanceMetrics` (ms, req/s, % error). Do not decide pass/fail; BirkNext evaluates thresholds and drift.
4. Register it in `Program.cs` as another `IPerformanceTestProvider`. Definitions select it by `ProviderId`.
5. Add tests with a fake process/runtime and a gated live test.

### Future fit (not implemented, no support claimed)

- **JMeter** (`performance.jmeter`): a legacy/enterprise protocol provider — HTTP, and potentially SOAP, JMS, JDBC and TCP. These would need new target types and safety rules first. It would generate a JMX plan from the generic definition and parse JTL/CSV or the JSON dashboard statistics.
- **NBomber** (`performance.nbomber`): a possible .NET-native provider. It would be an optional, separately deployed runner; BirkNext core takes no dependency and has no licensing logic.

## Next provider recommendation

JMeter, when a project needs protocols beyond HTTP (SOAP/JMS). Otherwise, extend k6 coverage first:
- authenticated tests via an approved client-credentials test identity;
- live progress via k6's JSON output stream.
