# Performance reachability automation — final report (2026-11-06)

Performance Test Review → Readiness. "Check readiness" now includes target connectivity from the load-generator container when it is eligible.
Before this change it was a separate "Check container network" click.

## 1. Confirmed root cause

The readiness evaluation was read-only by design, and the container probe was a separate POST.

- **Readiness evaluation.** `GET /api/performance-tests/definitions/{id}/readiness` → `PerformanceTestReadinessService.EvaluateAsync`. It only *reads* the last container-network check (`PerformanceTestProviderRegistry.LastReachability`). That check is valid for `PerformanceTests:Container:NetworkCheckValidMinutes` (default 15) and only under the same network/TLS fingerprint. Without one, the evaluation produces the blocking `network` item "Container reachability has not been checked".
- **Container probe.** It existed only behind `POST …/network-check`, which the page called from the "Check container network" action.
- **The "Check readiness" button.** Frontend `CheckReadinessAsync` only re-read the GET evaluation. For a saved, unchanged version it was not offered at all; only per-blocker actions were.

The existing probe is safe and reusable:

- one GET of the target origin from a k6 container;
- the same network, proxy and CA bundle as a run;
- container timeout 90 s, 256 MiB, 0.5 CPU;
- any HTTP status counts as "reachable", and 401 is classified as reachable-with-authentication.

The coordinated check therefore reuses the probe rather than adding a new one.

## 2. Before / after workflow

| | Before | After |
|---|---|---|
| New draft → Check readiness | Saved as v1, then GET readiness → "Container reachability has not been checked" → user clicks Check container network | Saved as v1 → coordinated check: definition → execution environment → **target connectivity (when eligible)** → blockers |
| Saved, unchanged version | No Check readiness button; only "Check container network" on the blocker | **Check readiness** (secondary) runs the coordinated check; the blocker's own action stays primary |
| Recent check, same configuration | — | Reused (no request); the age is shown |
| Configuration changed (target, runtime, image, CA bundle, proxy) | Stale → manual re-check | Stale → checked again by Check readiness |
| Check container network | The only way | Kept as an optional manual re-check for troubleshooting |
| Page load, render, tab switch, polling | GET only | GET only: **never** a probe |

## 3. Actual probe conditions

`PerformanceReadinessCheckService.CoordinateAsync` decides with these rules:

- **Network item missing** → `NotRun`.
- **Safety refusal** (`PerformanceTestSafety.EnvironmentBlock` or the `Origin` error, the same rules the network-check endpoint applies, e.g. Production) → `SkippedSafety`, with the reason. No request.
- **Provider/runtime/image unavailable** (network item `Optional` with no status) → `SkippedRuntimeUnavailable`. No request.
- **Network status `Not checked` or `Stale`** → **one probe** via `CheckReachabilityOnceAsync`, then readiness is evaluated again → `Checked`.
- **Otherwise** (a fresh result under the same fingerprint, success or failure) → `ReusedRecent`. No request. A fresh *failure* is not re-probed automatically; "Check container network" re-checks it on purpose.

## 4. Safety protections

- The probe is unchanged: one GET of the configured target origin. No scenario endpoint, no authenticated endpoint, no business request and no load are involved.
- SSRF/production rules are applied before any probe. They are the same rules as the explicit network check.
- **Concurrent duplicates** for the same provider + target share one in-flight probe (`ConcurrentDictionary<key, Lazy<Task>>`). The entry is removed when the probe finishes.
- **Bounds and cancellation.** The caller waits at most `ProbeBound` (150 s), which reports `TimedOut`. Cancellation stops the caller's wait. The shared probe has its own bound, the 90 s container timeout, so one cancelled caller never cancels it for another.
- No credential is logged or sent. The proxy identity (profile id + context fingerprint, never a token) is passed only so the existing credential readiness item behaves exactly as before.
- Reachability never clears other blockers. Execution credential, unmapped test data such as `brukerId`, and authentication remain their own items.

## 5. Readiness state transitions (network item)

`Not checked` → (Check readiness) → `Reachable` / `AuthChallenge` (Ready) or `DnsFailure` / `Timeout` / `ConnectionRefused` / `ConnectionFailed` / `ProxyFailure` / `RuntimeFailure` (blocking), or a TLS trust failure. A TLS trust failure is reported by Container TLS trust and is not a network fault.

Further transitions:
- After `NetworkCheckValidMinutes`, or a fingerprint change → `Stale` → checked again by the next Check readiness.
- Runtime unavailable → network item `Optional`; the step shows "Skipped — runtime unavailable".
- Safety refusal → the step shows "Skipped — safety restriction".

The page shows the four stages, each with Done / Skipped / Needs attention and its detail. For a probe made now it also shows the probe result line. A skipped or cancelled check is never shown as reachable.

## 6. Files changed

- `shared/PerformanceTestContracts.cs`: `PerformanceReadinessCheck`, `PerformanceReadinessCheckStep`, `PerformanceConnectivityOutcomes`.
- `backend/BirkNext.Api/Services/PerformanceTests/PerformanceReadinessCheckService.cs` (new): the coordination.
- `backend/BirkNext.Api/Services/PerformanceTests/PerformanceTestExecutionService.cs`: `CheckReachabilityOnceAsync` and `ReachabilityInFlight` (de-duplication).
- `backend/BirkNext.Api/Controllers/PerformanceTestsController.cs`: `POST definitions/{id}/readiness-check`. The manual network-check also goes through the de-duplicated path.
- `backend/BirkNext.Api/Program.cs`: DI registration.
- `frontend/BirkNext.Web/Services/PerformanceTestApiService.cs`: `ReadinessCheckAsync`. Its interface default falls back to stored readiness with `NotRun`.
- `frontend/BirkNext.Web/Pages/PerformanceTestReview.razor` (+ `.css`): coordinated check, progress + Cancel, stage list, explicit Check readiness button, duplicate-click guard, no double probe from "Check container network" after a fresh coordinated check.
- Tests: `backend/BirkNext.Api.Tests/Services/PerformanceTests/PerformanceReadinessCheckTests.cs` (new) and `frontend/BirkNext.Web.Tests/Pages/PerformanceTestReviewPageTests.cs` (+3).

## 7. Automated tests (actual runs, 2026-11-06)

- **New backend tests:** `PerformanceReadinessCheckTests`, **14/14 passed**. They cover:
  - healthy runtime + reachable target (one probe, re-evaluated);
  - runtime unavailable (skipped, no probe);
  - prohibited target (skipped, no probe);
  - fresh reachable and fresh DNS-failure results reused without a request;
  - stale → checked again;
  - DNS, TLS-trust, refused and timeout failures reported by cause, never Ready;
  - probe bound → TimedOut;
  - caller cancellation;
  - a reachable target does not clear a missing credential or test-data parameter;
  - concurrent duplicates share one probe, and a cancelled caller does not cancel it for the other;
  - the fake provider throws if a load test were started.
- **Backend `Services.PerformanceTests` namespace:** **285/285 passed**.
- **New frontend tests:** `PerformanceTestReviewPageTests`, +3:
  - Check readiness includes connectivity, and load, render and tab switches send no probe;
  - a duplicate click is ignored, and cancellation shows no reachable state;
  - a skipped check says why and is never shown as reachable.

  Two existing assertions now expect the coordinated `readiness-check` call next to the one explicit save.
- **Focused frontend run:** 231/231 passed. It covered every page touched in this batch, including `PerformanceTestReviewPageTests`.

## 8. Remaining runtime verification

No live probe was made: no k6 container was started against M2LB or TT02. The coordination logic is verified with mocks and fakes. The probe itself is the existing, previously used `K6PerformanceTestProvider.CheckReachabilityAsync`.
