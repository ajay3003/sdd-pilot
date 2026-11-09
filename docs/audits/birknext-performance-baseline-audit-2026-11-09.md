# BirkNext — Performance Test Review: baselines & regression — audit (2026-11-09)

Branch `008-traceability-first` (local only). Baseline commit `190ce1b0`. Scope: the brief "Performance Test Review — complete baseline &
regression management". This audit records what existed before the change, so later reports do not assume earlier requests were implemented.

## Capability matrix (before → owner → required change)

| Capability | Before | Owner | Risk | Required change | Acceptance criterion |
|---|---|---|---|---|---|
| Scenarios REST / GraphQL from contracts, Test Data, workload, thresholds | **Existing** (Smart Scenario Engine, central Test Data aliases, workload VU / arrival rate, thresholds, k6 in Podman) | `shared/PerformanceScenario*`, `Services/PerformanceTests` | — | none (reused) | runs complete with real k6 |
| Run evidence identity (definition snapshot + version, comparison fingerprint, provider/runtime/image digest, version label, host, metrics, status codes, transport errors, GraphQL errors) | **Existing** | `PerformanceTestRun` | — | none | persisted and reused by comparisons |
| Planned vs achieved workload | **Partial** (dropped iterations recorded; not used to qualify comparisons) | rules | comparisons of an under-achieved run | dropped iterations → comparability warning and eligibility warning | PB rules tests |
| Baselines | **Partial**: one-step "promote" to Active, Superseded/Historical, no approval, no eligibility record, no approver | `PerformanceTestStore` | an arbitrary run silently becomes the reference | propose → approve / reject, eligibility review recorded, approver, immutable approved records | PB-03…PB-06, PB-15 |
| Comparability | **Partial**: yes/no notes (environment, target, fingerprint, completeness) | `PerformanceTestRules.CompatibilityNotes` | incompatible runs judged; no "with warnings" | graded gate Comparable / With warnings / Not comparable / Insufficient evidence, per-dimension differences (workload type and parameters, operations, data, authentication, metrics source, runtime, app version, generator capacity) | PB-09, PB-14 |
| Regression engine | **Partial**: drift per policy on one run (Regression / Degraded / Improved / Stable) | `PerformanceTestRules.Drift` | a single noisy run reported as "Regression" | per-metric tolerance (definition's drift policies, no default), several runs (median of per-run values), Confirmed only with ≥ 3 agreeing comparable runs, Potential otherwise, Inconclusive for noise / missing metric / too few samples / zero baseline, Not assessed without tolerance, throughput not judged under arrival rate | PB-07, PB-08, PB-10, PB-12 |
| Threshold vs regression | **Existing** separation on the run | rules | — | reported side by side in the regression | PB-11 |
| Statistics | **Missing** | rules | false confirmation | repeated-run rule + spread check; CIs not computable (summary percentiles only) — stated as a limitation | rules tests |
| Trends | **Missing** (aggregates only) | — | — | per-definition trend with comparability against the baseline, not-comparable runs excluded unless asked | PB-18, PB-B8 |
| Export | **Partial** (single-run HTML) | — | — | sanitized JSON regression export (identity, runs, workload, units, derivation, limitations) | PB-19, PB-20, PB-B6 |
| Run deletion / retention / baseline protection | **Missing** (runs never deleted) | store | — | confirmed single-run deletion; a baseline's source run protected; no background retention | PB-16, PB-17 |
| Authorization | **Missing** on run start/cancel and baseline writes | controller | unauthenticated load start | execute permission on run start/cancel; configuration-write permission on baseline decisions and deletion | `PerformanceEndpointSecurityTests` |
| GraphQL 200 + errors | **Existing** counter, but a clean run stores no count (k6 omits empty counters) | rules | a clean GraphQL baseline cannot anchor the GraphQL error rate | treat a missing counter as 0 for GraphQL runs whose script counted | found by PB-33; rules test |
| Authentication per VU (patterns A–D) | **Existing**: attended credential reuse via the proxy / unattended references; no per-VU identities | — | — | not changed in this task | gap register |
| Delivery / Release Readiness integration of regressions | **Existing** for thresholds/drift on runs | — | — | not changed in this task | gap register |

## Metric definitions (what is compared)

P50/P90/P95/P99 and mean latency (ms, k6 `http_req_duration` summary), throughput (req/s, `http_reqs` rate), error rate (% of failed HTTP
requests, `http_req_failed`), request count, dropped iteration rate (%), GraphQL error rate (% of requests whose body had `errors`). Per-run
summary percentiles only: the underlying samples are not kept, so percentiles are never pooled across runs and confidence intervals are not
computed.
