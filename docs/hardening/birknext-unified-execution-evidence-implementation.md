# BirkNext — Unified Test Execution & Evidence Explorer: implementation (2026-11-10)

Branch `008-traceability-first`. Baseline `35633daf`. Audit: `../audits/birknext-unified-execution-evidence-audit.md`. No push, no deployment.

Execution Results becomes **Test Execution & Evidence**, a read-only explorer over the evidence owners BirkNext already has. It adds no store, no execution engine and no copy of a payload.

## 1. Read-only projection (C, D, E)

| Element | Implementation |
|---|---|
| Contracts | `shared/ExecutionExplorerContracts.cs`. **Enums:** `ExplorerSource`, `ExplorerCategory`, `ExplorerOutcome`, `EvidenceCompleteness`, `ExplorerCapability`, `ProviderState`. **Records:** `ExplorerRun` (light: no bodies), `ExplorerQuery`, `ExplorerPage`, `ExplorerOverview`, `ExplorerCoverageRow`, `ExplorerRunDetail`, `ExplorerTimelineEvent`, `ExplorerItem`, `ExplorerTrace`, `ExplorerComparison`, `ExplorerReport`. |
| Adapters | `Services/ExecutionExplorer/ExplorerProviders.cs`, one `IExplorerProvider` per owner. **`ExecutionEvidenceExplorerProvider`:** `execution_runs` plus `integration_suite_runs` links; it tells contract tests, chains, flat suite runs and composite E2E runs apart, and takes the composite verdict from `IntegrationSuiteService.ReadCompositeRunAsync`, the single verdict owner. **Other owners:** `JourneyExplorerProvider` (`integration_journey_runs`), `BrowserE2EExplorerProvider` (Critical E2E history), `PerformanceExplorerProvider` (`performance_test_runs`), `ParquetExplorerProvider` (`parquet_inspections`), `SecurityReviewExplorerProvider` (`security_classification_evidence` Kind `run`; Static vs Runtime). |
| Each row carries | Source and category; owner run id and display id; name; time and duration; environment; project; normalised outcome plus the owner's status text and a reason; counts with their unit; evidence completeness; protection; suite/definition and contract references; capabilities; a link to the owning module. |
| Status rules | `ExplorerStatus`, which never upgrades a result: Passed with zero passed calls → Not assessed; subflow verified or an unobserved stage → Partial; wrong correlation → Failed; k6 completed without an assessed threshold → Not assessed (a threshold warning is Partial and never a functional pass); browser Inconclusive → Not assessed; journey NotVerified → Not assessed; Parquet inconclusive-only → Not assessed; a static security review is `StaticAssessment`. Static assessments and data-file validations are never counted as executions. |
| Project | Derived only from a recorded reference: contract key → `api_contracts.ProjectKey` (unattributed when any contract has no project or the run spans several); the composite suite key's owning project; the Parquet association; the performance `ProjectId`. A project filter never claims unattributed runs; they appear only with `unattributed=true` and are labelled. BirkNext has no per-project ACL, and the explorer grants nothing. |
| Service | `ExecutionExplorerService`. **Scan:** the newest 1000 rows per owner, narrowed by environment and time in the owner's store; no body column is read. **Filter and page:** in the service, sorted by time, bounded pages (≤ 100) with the total. **Failure isolation:** an unavailable owner is reported (`ProviderState.Unavailable`) and never hides the others. **Truncation:** reported. |
| API | `GET api/execution-explorer/overview`, `runs` (server-side filters: environment, project, unattributed, sources, categories, outcomes, search, from/to, protected, evidence, static, skip/take), `runs/{source}/{id}`, `compare`, `runs/{source}/{id}/report`. All are GETs that read stored results. Protection, deletion and retention stay on the Execution Evidence endpoints and their policies. |

## 2. Details, timeline, traceability, comparison, report (H–P)

- **Run details.** A REST/GraphQL run lists its calls. A composite run lists its stages. A journey lists its steps, a browser flow its steps (with the final assertion), a performance run its thresholds and metrics (requests, error rate, p95/p99, requests/s, baseline drift), a Parquet validation its rules (rows examined, assessment scope, rule set), and a security review its checks.
- **Timeline.** Recorded events only:
  - attempt start ("Submitted …", or "Not sent") and completion ("Responded N" / "Timed out" / "Failed (connection)");
  - a data-changing call that timed out is shown as **UnknownOutcome**;
  - each event carries its `X-BirkNext-Execution-Id` correlation value;
  - in a composite run, every stage without a transmitted call is listed as **"Not observed by this run"**, with no time;
  - journey steps without a recorded capture time stay untimed.
- **Traceability.** Only references the run recorded: contract versions, chain revision, source snapshot, integration suite, contract operation, test definition revision, Test Data version, dataset row, composite correlation boundaries, journey snapshots and Test Data, Parquet file, schema fingerprint, dataset contract and requirement ids, browser flow version and evidence references, security source archives.
- **Comparison** (Execution Evidence runs). Calls are matched by definition reference (otherwise by name), and only within one environment and the same chain. The result is a list of changes per call: NewFailure, ResolvedFailure, Regressed (an assertion that passed before fails now), StillFailing, ChangedStatus (HTTP), LatencyChanged (one sample each; not a performance result), OnlyInBaseline / OnlyInCurrent. If the runs are not comparable, the reasons are returned instead. Performance keeps its own baselines.
- **Report.** Markdown generated on demand: result, scope, items, metrics, timeline, traceability, limitations. It never includes bodies, headers or resolved values. It is bounded (200 items, 1 MB), cells are sanitised (single line, no pipes, angle brackets or backticks), and the file name is safe (`execution-report-{source}-{id8}.md`).

## 3. UX (R)

`/execution-results` is still the route, and every existing deep link still works (`?run=`, `?call=`, `?tab=`, `?hq=`…).

- **Header.** "Test Execution & Evidence", with the subtitle "Actual execution results, verification outcomes and traceable evidence.", plus *View runs* and *Open tests*. There is no Run action.
- **Scope bar.** Project ("All projects in this workspace", or a derived project, with the option to include unattributed runs), Target Environment, time range, and one sentence stating the scope. It says the execution environment is recorded with the run and is not the environment the source came from.
- **Four areas** (`?area=`):
  - **Overview.**
    - Primary metrics: total, passed, failed, partially verified, not assessed, tests executed (passed + failed), latest execution.
    - Secondary REST/GraphQL metrics: logical calls, attempts, stored evidence (measured JSON), protected, retention candidates.
    - The count of static assessments, kept apart.
    - "Needs attention".
    - Results by kind, each with an explicit denominator and what is missing.
    - Notes on unavailable owners.
    - Empty state: "No execution results yet." with links to Contract Testing, Generate Integration Test Suite, Performance Test Review and Parquet Quality Review.
  - **Runs & Results.**
    - Search, evidence, protected-only, include-static, plus type and outcome chips (filters live in the URL).
    - A semantic results table, server-side paged by 20, with columns: run, type, started, duration, outcome, tests, passed, failed, not assessed, environment, evidence.
    - Assessments are labelled as such.
  - **Evidence & Traceability.** Results with Trace and Timeline links, and results by kind.
  - **Storage & Retention.** The existing run management: storage, protection, export, deletion with a typed count, dry-run retention and the audit trail. It opens with a sentence explaining stored JSON versus database allocation. Legacy `?hq=` links land here.
- **Unified run detail** (`?source=&id=`).
  - Shows the outcome with a colour-independent glyph (✓ ✕ ◐ ⊘ ? …), the owner's status, metrics, and tabs that appear only when they have content: Summary (with comparison), Tests/Stages/Steps/Rules, Timeline, Traceability, Technical details.
  - Actions: *Calls, request & response* (the existing call explorer), *Download report*, *Open in {owner}*.
- **Layout.** Compact cards and chips. The area navigation is a 4-column grid that becomes 2 columns at ≤ 900 px, and the hints hide at ≤ 480 px. Tables scroll inside their own container. Status changes are announced through `aria-busy` and `role=status`.
- **Navigation, guide and catalog.** Renamed "Test Execution & Evidence".

## 4. Tests

- **`ExecutionExplorerTests` (7).** Status normalisation; every owner listed newest first with server-side paging and static assessments apart; overview counts only executions and assessed tests; project isolation (unattributed and foreign runs); an unavailable owner never hides the others; details and reports read stored evidence only (timeline order, correlation, no bodies in reports, sanitised cells); comparison detects new failure, resolved failure and latency change, and refuses other environments and other owners.
- **`ExecutionExplorerPageTests` (6).** Empty state; overview from the backend; server-side paging and filters in the URL; composite detail with the unobserved stage, report, owner link and call explorer; comparison candidates and refusal; legacy links open Storage & Retention.
- **`ExecutionResultsPageTests` (10)** are unchanged except that run management opens under `?area=storage`.
