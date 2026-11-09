# BirkNext — Performance Test Review: baselines & regression — implementation (2026-11-09)

Branch `008-traceability-first` (local only). Audit: `docs/audits/birknext-performance-baseline-audit-2026-11-09.md`. Built on the existing Performance
Test Review, k6 provider, Podman runtime and run store — no second engine, no second result store.

## Shared rules — `shared/PerformanceBaselineRegression.cs` (backend authoritative, UI preview)

- **Comparability** (`PerformanceRegressionRules.Comparability`): per-dimension differences, each Blocking / Warning / Missing → Comparable,
  ComparableWithWarnings, NotComparable, InsufficientEvidence. Blocking: environment, classification, target, target type, operation set, scenario
  details (what is sent; step ids and discovery bookkeeping ignored), workload type, VUs / arrival and start rates, phases, test data profile,
  authentication, metrics source. Warning: application version, load-generator version, runtime image, generator host capacity, definition version
  with the same measurement, dropped iterations, generator ceiling, maximum duration. Missing: a run without complete metrics.
- **Regression** (`Evaluate`): keys p50, p90, p95, p99, throughput, error rate, GraphQL error rate, dropped iteration rate, requests. Tolerance =
  the definition's drift policy for the metric (relative % and/or absolute); none → NotAssessed (shown, not judged). Several runs → median of the
  per-run values (never pooled). ConfirmedRegression: ≥ 3 comparable runs, median beyond tolerance and ≥ ⅔ of runs beyond. PotentialRegression:
  beyond but not confirmed. Inconclusive: not comparable (whole report exploratory), metric missing from some run, too few requests for the
  percentile (P50 2, P90 10, P95 20, P99 100), runs spreading more than twice the relative tolerance, zero baseline with only a relative tolerance.
  Improved: every run better than the baseline by more than the tolerance. Throughput is not judged under an arrival-rate workload. Thresholds of
  every current run are listed beside the verdict. Only metrics with a tolerance shape the overall verdict.
- **GraphQL error rate**: a GraphQL run whose script counted (status counters present) and has no GraphQL-error counter had 0 errors (k6 omits
  counters without samples); REST runs and older scripts stay "not reported". Found by the live acceptance (PB-33).
- **Eligibility** (`Eligibility`, `EligibilityProblem`): blocking — completed, complete metrics, at least one request; needing explicit acceptance —
  enough samples for P95, no dropped iterations, required thresholds met.
- **Trend** (`Trend`): runs of a definition oldest first, baseline run marked, comparability against the baseline run per point, excluded count.

## Baseline lifecycle — `PerformanceTestStore`

`ProposeAsync` (Proposed, eligibility recorded, accepted warnings flagged, proposer, source run's version label and definition version) →
`ApproveAsync` (Active, approver, decision note; previous Active → Superseded, serialised per scope) or `RejectAsync` (reason required, kept).
`PromoteAsync` keeps the one-step path for existing clients (propose + approve by the same person, recorded). Approved records are immutable:
only the status moves on (Superseded, Historical). `DeleteRunAsync`: one finished run, explicit confirmation, refused for the source run of any
proposed or approved baseline (active, superseded or historical). Fix found by the existing concurrency test: the scope is re-read with a cleared
change tracker inside the scope lock (EF returned stale tracked rows from the proposal step, so a concurrent approval could leave two Active).
No migration: status values and approval data live in the existing JSON document (status column fits).

## API — `PerformanceTestsController`

| Endpoint | Permission |
|---|---|
| `POST runs`, `POST runs/{id}/cancel` | **execute** (`ActiveEventExecute`) — previously none |
| `POST baselines` (one step), `POST baselines/proposals`, `POST baselines/{id}/approve`, `POST baselines/{id}/reject`, `POST baselines/{id}/archive`, `POST runs/{id}/delete` | **configuration write** |
| `GET regression`, `GET regression/export`, `GET trends`, `GET compare` | read |

A proposed or rejected baseline is refused as a regression reference. The export is JSON: definition, environment, baseline (with approval),
baseline and current run summaries (workload, data profile, authentication mode, metrics, thresholds), the report, units, completeness note.

## UI — Performance Test Review (same design)

History: "Propose as baseline" with the eligibility review and an explicit acceptance of warnings, "Propose for approval" and "Propose and approve
now"; Baselines table with an Approval column (proposed/approved/rejected by whom), Approve and Reject (reason required); Delete with a second
confirming click (protected runs explain why). Compare: new `PerformanceRegressionPanel` — approved baselines only, choice of runs to judge,
verdict and comparability first, differences table, metric table (Metric · Baseline · Current (range) · Abs Δ · % Δ · Tolerance · Runs beyond ·
Verdict with reason), thresholds per run, derivation and limitations, export; trend table with a P95 sparkline (baseline line, text equivalent)
and not-comparable runs hidden until asked.

## Tests

Backend: `PerformanceRegressionRulesTests` (21: comparability dimensions, potential/confirmed/agreement, noise, threshold vs regression, zero
baseline, missing metric, sample minimums, no tolerance, throughput by workload type, exploratory, derived rates, GraphQL clean baseline,
eligibility, trend), `PerformanceTestReviewTests` (+3: approval lifecycle and immutability, protected deletion, repeated-run confirmation through
the store), `PerformanceEndpointSecurityTests` (12). Frontend: `PerformanceRegressionPanelTests` (4), `PerformanceTestReviewPageTests` (+1: propose
→ reject needs reason → approve → protected delete).
