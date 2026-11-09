# BirkNext — Performance Test Review: baselines & regression — gap register (2026-11-09)

Branch `008-traceability-first` (local only). Initial SHA `190ce1b0`; final SHA = the commit carrying this file.
Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED. Acceptance IDs refer to `birknext-performance-baseline-acceptance-2026-11-09.md`.

**Overall: PASS for baselines, comparability, regression, trends, export, protection and the UI (verified with real k6 runs); PARTIAL against the
whole brief** — authentication patterns per VU, Release Readiness consumption of the new verdicts, scheduled retention and configurable tolerances
for derived metrics are not done.

| § | Capability | Status | Verified by |
|---|---|---|---|
| 1 | Audit and capability matrix | PASS | audit report |
| 2 | Run evidence identity (existing) used for comparability (definition version, fingerprint, provider, image digest, version label, host, metrics source) | PASS | PB-09, PB-14, rules tests |
| 3 | Workload model (existing); fixed VU ≠ arrival rate in comparisons; dropped iterations qualify comparisons | PASS | rules tests, PB-14 |
| 4 | REST and GraphQL scenarios (existing); HTTP 200 + GraphQL errors visible as a separate rate | PASS | PB-02, PB-30…PB-33 |
| 5 | Test Data row policies (Sequential / Random seeded / Unique per VU / per iteration, exhaustion) | **NOT RUN** | existing Test Data binding unchanged; no new row policies in this task |
| 6 | Authentication per VU (patterns A–D) | **NOT SUPPORTED** (unchanged) | existing attended/unattended reference only; Authentication is a comparability dimension |
| 7 | Readiness (existing) | PASS | PB-01, PB-31 |
| 8 | Metrics actually emitted, units explicit, never fabricated | PASS | export units; "not reported" kept distinct from 0 |
| 9 | Baselines: reference to run evidence, propose / approve / reject / supersede / archive, approver, immutable approved record | PASS | PB-03…PB-06, PB-15, store tests |
| 10 | Comparability gate (4 levels, per-dimension differences, exploratory when not comparable) | PASS | PB-09, PB-14, PB-B7 |
| 10 | Manual override of a NOT COMPARABLE gate with justification | **NOT SUPPORTED** | deltas stay exploratory; no override path |
| 11 | Regression engine (p50/p90/p95/p99, throughput, error rate, request count, dropped rate; per-metric tolerance; threshold vs regression separate) | PASS | PB-07…PB-12 |
| 11 | Configurable tolerance for GraphQL error rate, dropped iteration rate, request count | **NOT SUPPORTED** | shown, Not assessed (drift policies exist only for the core metrics) |
| 12 | Statistics: repeated runs, median, agreement, sample minimums, noise → Inconclusive | PASS | rules tests, PB-10 |
| 12 | Confidence intervals | **NOT SUPPORTED** | runs keep summary percentiles only (stated in every report's limitations) |
| 13 | Trends with baseline marker and comparability; not comparable runs off by default | PASS | PB-18, PB-B8 |
| 13 | Trend filters by project / build / date range / auth mode / dataset | **PARTIAL** | per definition (one environment); other filters not built |
| 14 | Results Explorer: compare table Metric · Baseline · Current · Abs Δ · % Δ · Tolerance · Verdict, drill-down, export | PASS | PB-B4…PB-B6 |
| 15 | Execution Evidence integration (bounded request/response samples of load runs) | **NOT RUN** | load runs keep metrics, status codes and transport errors; no samples |
| 16 | Difference Analysis / Release Readiness consumption of regression verdicts | **NOT RUN** | the run's own drift (existing) is still what Release Readiness reads |
| 17 | Baseline source run protected; controlled deletion | PASS | PB-16, PB-17, PB-B3 |
| 17 | Scheduled retention of performance runs | **NOT SUPPORTED** (by design) | deletion is manual and confirmed |
| 18 | Sanitized export with units, definitions, limitations | PASS | PB-19, PB-20, PB-B6 |
| 19 | Security: execute permission for load, write permission for baseline decisions and deletion | PASS | `PerformanceEndpointSecurityTests` |
| 21 | Isolated acceptance with real k6, REST + GraphQL, restart | PASS | 29/29 API checks |
| 22 | Browser (5 widths, keyboard, no external requests) | PASS | 11/11 |

## Defects found and fixed

| ID | Found by | Defect | Fix |
|---|---|---|---|
| PB-DEF-1 | Existing concurrency test (`Concurrent_baseline_promotions…`) | Proposal + approval on one EF context re-read stale tracked rows inside the scope lock; a concurrent approval could leave two Active baselines | scope re-read with a cleared change tracker |
| PB-DEF-2 | Live acceptance PB-33 | A clean GraphQL run stores no GraphQL-error count (k6 omits counters without samples), so a clean baseline could not anchor the GraphQL error rate | a counted GraphQL run without the counter = 0 errors (rule-level; stored documents unchanged) |
| PB-DEF-3 | Audit | Starting / cancelling a load run had no permission check | execute permission |

## Regression (clean build of the final source, memory-guarded, sequential partitions)

| Suite | Result |
|---|---|
| Backend P1 · P2 · P3 · P4 · P5 · P6 | 1021/0/2 · 1098/0/5 · 830/0/0 · 652/0/1 · 520/0/3 · 2108/0/8 |
| **Backend total** | **6229 passed, 0 failed, 19 skipped** |
| **Frontend** | **8499 passed, 0 failed** |

## Scratch resources (not committed)

`scratchpad/pbval/` (target, acceptance and browser scripts, results, screenshots, exports), DB `birknext_pb_20261109`, container `bn-pb-target`
(:18090), published web `scratchpad/webpub-pb`. Kept until cleanup is approved.
