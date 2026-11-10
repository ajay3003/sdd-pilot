# BirkNext — Unified Test Execution & Evidence Explorer: audit (2026-11-10)

Branch `008-traceability-first`. Baseline `e6b00385` (task 4 committed on top of `54db534d`). No push, no deployment, no external system.

## 1. Scope and method

The audit read the code at HEAD, not the queued task descriptions. It covered:

- the Execution Results page and its tests;
- the Execution Evidence controller, store, run service and management;
- the shared evidence contracts;
- every module that stores a run or an assessment;
- the navigation, documentation and guide entries;
- the execution-lifecycle reports of 2026-11-07/08 (`birknext-api-graphql-execution-evidence-*`, `birknext-rest-graphql-execution-lifecycle-*`).

Nothing was run against an external system.

## 2. Existing evidence owners (capability matrix)

| Owner | Store | Shares the execution run id | Run or assessment | Outcome vocabulary | Drill-down available | Project recorded |
|---|---|---|---|---|---|---|
| Contract Testing: single tests | `execution_runs` + `execution_calls` (redacted call documents) | is the run | runtime execution | `ExecutionState`; `AssertionOutcome` per assertion | calls, attempts, request/response, assertions, contract checks | no (derivable from `ContractRefs` → `api_contracts.ProjectKey`) |
| Contract Testing: chains | same, with `ChainRef` | is the run | runtime execution | same | same, plus step outputs | same |
| Integration Suites: flat | `integration_suite_runs` link → execution run | yes | runtime execution | from the execution run | as Execution Evidence | suite key |
| Integration Suites: composite (task 4) | link + `PlanJson` | yes | runtime execution | `CompositeRunVerdict`, stage and correlation states | stages, correlation, plus Execution Evidence | owning project in the suite key |
| Integration Journeys (Skolenærvær pack and others) | `integration_journey_runs.ResultJson` | no (own id) | runtime execution | `JourneyRunState`, `JourneyStepState` | steps with capture times, correlation references, evidence context | no (source snapshots in evidence context) |
| Critical E2E Regression | files `App_Data/critical-e2e/history.json` (≤ 200 per environment) | no | runtime (attended browser) | `CriticalE2EStatus`, including Inconclusive | steps, final assertion, evidence references | no |
| Performance Test Review | `performance_test_runs.DocumentJson` | no | runtime (k6 container) | `PerformanceRunState` + `PerformanceQualityVerdict` + threshold `CheckOutcome` | thresholds, metrics, baseline drift | `ProjectId` (optional) |
| Parquet Quality Review | `parquet_inspections.DocumentJson` | optional `LinkedRunId` / correlation | **assessment of a data file** | `ParquetRuleOutcome` per rule | rules, schema fingerprint, contract association, requirement ids | association project key |
| Security Classification (Kode 6/7) | `security_classification_evidence` (Kind `run`) | no | `ReviewKind` Static = **static assessment**; Runtime/Combined = runtime verification | `ClassificationOverall`, `ClassificationState` per check | checks, findings, source archives | no |
| Messaging observations, Blob observations | imported observation sets | correlated by `X-BirkNext-Execution-Id` | imported observations | — | shown on the run's call evidence | — |

Static reviews outside this matrix (API Quality Review, Integration Quality Review, Documentation Drift and others) keep their own run tables (`integration_review_runs`, `ai_code_review_runs`…). Those runs are assessments of a target or of evidence, not test executions. They are left out of the explorer; adding them would inflate execution counts.

## 3. Confirmed gaps

| ID | Gap | Evidence at HEAD | Impact |
|---|---|---|---|
| UE-G1 | Execution Results shows only Execution Evidence runs | page calls `IExecutionEvidenceApiService.RunsAsync` only | journeys, browser E2E, performance, Parquet and security results are invisible in the results page |
| UE-G2 | No run kind on `execution_runs` | no kind or source column; contract tests, chains, suite and composite runs look alike | a composite subflow cannot be told apart from a full end-to-end run in the history |
| UE-G3 | No server-side paging of run history | `ExecutionEvidenceStore.ListAsync` has `Take` (≤ 500) and no skip; the page asks for `take=200` | the browser loads 200 runs with no next page; older runs are unreachable |
| UE-G4 | Storage metrics dominate the page and mix two quantities | storage card first; "Database tables" (`pg_total_relation_size`) shown next to "Stored" | database allocation reads as evidence size; retention controls are always visible |
| UE-G5 | No project or environment scope | `ExecutionRunFilter.EnvironmentId` is never set; no project anywhere | runs of every environment are listed together without saying so |
| UE-G6 | (checked, **not a gap** in the store) | `ExecutionEvidenceStore.CompleteAsync` sets Passed only when every planned call passed; a call without assessed assertions is NotAssessed. The other owners have their own vocabularies (k6 Completed, browser Inconclusive, journey NotVerified) that a naive merge could misread as a pass | the explorer normalises each owner with rules that never upgrade (and repeats the store's guard: Passed with zero passed calls → Not assessed) |
| UE-G7 | No overview, no "needs attention", no coverage per kind | — | a test lead cannot answer "what failed, what was not assessed" |
| UE-G8 | No timeline across stages | attempts per call only | asynchronous stages and unobserved stages are not visible as such |
| UE-G9 | No run-to-run comparison | only attempt-to-attempt within one call | regressions between two runs of one chain are not visible |
| UE-G10 | No tester report | ZIP export of raw redacted evidence only | no readable summary for a test lead |
| UE-G11 | Static assessments not separated | n/a (not shown at all) | adding them naively would inflate execution counts |
| UE-G12 | Empty state is a technical storage card | "Runs = 0, Stored = 0 B, Database tables ≈ 72 KB…" | the first-run experience does not guide the tester |

Not a gap:

- redaction before storage;
- protection, typed-count deletion, dry-run retention and the audit trail (`ExecutionEvidenceManagement`);
- server-side paging of calls inside a run;
- the request/response explorer, attempt comparison and integrity check.

All of these are kept as they are.

## 4. Screenshot findings vs code

The screenshot's zeros, and its 72 KB of database tables with 0 B stored, match the code for an empty database. The tables still take space when empty. Nothing was fabricated to populate the page.

The observations themselves were confirmed in the code (UE-G4, UE-G12):

- the large generic title;
- the storage KPIs first;
- the always-visible retention section;
- the small history filters;
- the empty area.

## 5. Decisions

- **No new store.** A read-only projection reads each owner's store through one adapter per owner (`IExplorerProvider`). There is no universal evidence table and no copy of any payload.
- **Outcome model.**
  - Outcomes are normalised into Passed, Failed, Partial, Blocked, NotAssessed, Cancelled, TimedOut, UnknownOutcome, Running, Interrupted and NotSupported.
  - The owner's own status is always kept beside the normalised outcome.
  - Rules that never upgrade a result:
    - zero assessed assertions is never Passed;
    - a subflow, or an unobserved stage, is Partial;
    - a completed k6 run is not a pass, because the threshold verdict decides;
    - Inconclusive browser E2E is NotAssessed;
    - a static review is a StaticAssessment.
- **Kinds.** Categories separate functional tests, integration tests, browser E2E, performance, data-file validation, runtime security and static assessment. Static assessments and data validations are never counted as executions.
- **Project.**
  - The project is derived only from a recorded reference: contract key → project; the composite suite key's owning project; a Parquet association; a performance `ProjectId`.
  - Otherwise the project is "not recorded".
  - A project filter never claims unattributed runs, which are listed only on request and labelled.
  - BirkNext has no per-project ACL; the explorer adds none and grants nothing.
- **Paging.** The newest window per owner (1000) is read and merged, then one bounded page (≤ 100) is returned. Truncation is reported. History reads never touch request/response bodies.
- **Areas.** Overview · Runs & Results · Evidence & Traceability · Storage & Retention, via `?area=`.
  - The existing `?run=` and `?call=` deep links, and their call explorer, are unchanged.
  - Legacy history links (`?hq=`…) open Storage & Retention, where run management (protect, export, delete, retention) now lives.
- **Comparison.** Comparison is supported for Execution Evidence runs only, and only within one environment and the same chain or definitions. Otherwise the reasons are shown. Performance keeps its own baselines.
- **Report.** A sanitized Markdown report is generated on demand from stored evidence. It never contains bodies, headers or values, and it is bounded.
