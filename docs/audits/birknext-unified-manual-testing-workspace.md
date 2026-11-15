# BirkNext — Unified Manual Testing workspace

Date: 2026-11-15 · Branch: `008-traceability-first` · Starting HEAD: `528048c8` (manual requirement linking) on `96485cff` (navigation progress).

## 1. Executive summary

Manual Testing is now one workspace with a home page and three stages — **Discover & understand**, **Plan & prepare**, **Execute & document** — over the services BirkNext already had. Most of what a manual tester needed existed but was spread over six sidebar groups with no home; the genuinely missing part was execution: there was no way to record a manual test run, step results, evidence or defects. That is now implemented with real persistent records (new table `manual_test_runs`, additive migration) and fed into Test Coverage and Requirements Traceability through the existing shared traceability rule, so design and execution are shown apart. Scenario discovery results are now stored, so saved work can be continued after an API restart without discovering again.

Test Plan Suggestions was not duplicated: **Plan & prepare is Test Plan Suggestions** (same page, same route `test-plan-suggestions`, same links and bookmarks), renamed in the menu, with the shared workspace header.

## 2. Initial capability audit

Classification: **A** implemented but hard to find · **B** implemented but not connected · **C** partially implemented · **D** genuinely missing · **E** unsupported.

| Capability | Existing UI | Existing backend | Persistence | Integration | Before → after |
|---|---|---|---|---|---|
| Scenario discovery | Test Plan Suggestions → Choose scenarios | `TestScenarioDiscoveryService` + 6 providers | **in memory only** (`ScenarioDiscoveryCache`, lost on restart) | TPS only | **C** → stored (`scenario_discovery_results`), restored after restart; summarized in Discover & understand |
| Requirement review | Specification Explorer, Document Quality Review | browser spec parser (`SpecExplorerService`) | workspace documents | Test Coverage, Traceability | **A** → requirements with design/execution state in Discover & understand |
| Business-rule review | System Understanding (guide rules, MT-7) | `SystemGuideService`, `RequirementCoverageService` | guide revisions | TPS (guide plans), MT-7 | **A** → linked from Discover & understand when a guide describes the project; MT-7 unchanged |
| Existing test discovery | TPS capability "existing tests"; Test Coverage | xUnit inventory provider | source snapshot | TPS candidates | **A** → shown under "What the evidence contains" |
| Test Plan creation | TPS | `TestPlanSuggestionService` | `test_plan_drafts`, `test_plan_snapshots` | Coverage, Traceability | Implemented → reused as Plan & prepare |
| Manual case editing | TPS case editor (steps, expected per step, priority, roles, dependencies, type) | same | same (human edits apart from evidence) | — | Implemented → reused |
| Requirement linking | TPS "Linked requirements" (`528048c8`) | same, catalog-validated | case JSON | Coverage, Traceability | Implemented → reused |
| Test Data assignment | TPS (asset id links), Test Data page | `TestDataCatalogService` | registry | TPS | Implemented → reused; pinned (alias + id) in runs |
| Test execution (manual) | none | none (`ExecutionRunRow` aggregates automated calls only) | none | none | **D** → implemented |
| Step results | none | none | none | none | **D** → implemented |
| Execution evidence | Test Execution & Evidence (automated runs) | Execution Evidence (automated) | automated stores | — | **D** for manual → evidence **references** (links/notes), not uploads |
| Defect registration | none (ADO read-only traceability elsewhere) | none | none | none | **D** → local defect observations with optional external reference; no ADO writes |
| Execution history | none for manual | none | none | none | **D** → run history per project/case |
| Coverage | Test Coverage, Traceability (design only, "Not executed") | `PreparedCaseTraceability` | — | both pages | **B** → executed/passed from completed manual runs, design kept apart |
| Export | TPS "Prepare for Azure DevOps" (snapshot JSON + text) | `TestPlanSuggestionEngine.Export` | snapshots | — | Implemented (unchanged; XLSX/CSV export is the next queued task) |

Tester journey before (traced in code): select project (Project Import → Use) → requirements in Specification Explorer → scenarios in TPS (discovery lost on restart) → prepare/edit/link/Test Data in TPS → save → resume via TPS draft → **no way to execute, record results, evidence, defects or history** → coverage in two other modules that always said "Not executed". Navigation spanned six sidebar groups with no home or next step.

## 3. Reused functionality

Test Plan Suggestions (planning, editing, testability, requirement links, snapshots, export), the scenario discovery engine and providers, the specification parser and `CoverageWorkspaceEvidence` (requirements), `PreparedCaseTraceability` (the one traceability rule), `PreparedCaseTracePanel` (Test Coverage / Requirements Traceability), `GuidedStepList` (stage cards), the workspace projection (project identity), project evidence (snapshot), the "Manual Tester" workspace view, NavigationLock / global navigation progress.

## 4. Newly implemented functionality

- `ManualTestRunService` + `api/manual-test-runs` (start/resume, get, list, save progress, complete, stop) — `manual_test_runs`.
- `ScenarioDiscoveryStore` — `scenario_discovery_results`; `GET discover/last` restores after a restart; preparing from a stored discovery id works after a restart.
- Pages: `/manual-testing` (home), `/manual-testing/discover`, `/manual-testing/execute` (case list, start panel, runner, history); Plan & prepare = `/test-plan-suggestions` with the workspace header and a "Run this test case" link per saved case.
- `TestingWorkspaceHeader` (generic: workspace name, stages, project context — reusable by the queued Technical Testing workspace), `ManualTestingWorkspace` (one loader for project/snapshot/plans/discovery/requirements/runs), `ManualTestingPresentation` (stages and next action from records).
- Sidebar: "Manual Testing" section (Manual Testing, Discover & understand, Plan & prepare, Execute & document); Manual Tester view lists it first and lands on it.
- Coverage/traceability: per case latest completed run (outcome, date, tester, whether it ran an earlier revision), per requirement executed/passed/failed cases, view totals.

## 5. Architecture decisions and authoritative identities

| Identity | Authority |
|---|---|
| Project | workspace projection `ProjectSlug` (= TPS `ProjectKey`, plan owner `project:{key}`) |
| Source snapshot | project evidence / imported package (`ManualTestingWorkspace.ResolveSnapshot`, now shared with TPS) |
| Requirement | id + specification file of the selected specification |
| Saved plan / case | `test_plan_drafts` id + case id (`{draft:N}/{case}`) — no copy is made for the workspace |
| Case revision in a run | plan revision + content fingerprint (title, preconditions, steps, step expectations, expected result) pinned at start |
| Run | `manual_test_runs.Id`, bound to project key; concurrency by run revision |
| Evidence / defect | ids inside the run (`ev-…`, `df-…`); who/when recorded by the server |

A case has at most one run in progress: starting again resumes it. Runs are never recomputed from current case content.

## 6. User journey

Home → (next step) → Discover & understand (requirements with design/execution state, suggestions by type, clarifications, evidence kinds) → Plan & prepare (TPS: choose, prepare, edit, link, Test Data, testability, save) → Run this test case → Start test run → per step Pass/Fail/Blocked/Not run + actual result → evidence references, defects → Save progress (leave, come back: Resume run) → Complete run → history → Test Coverage / Requirements Traceability show design and execution apart → back to the saved case.

## 7. Execution result semantics

- Run lifecycle (separate from case preparation): In progress → Completed or Stopped (abandoned, reason required).
- Step outcome: Pass, Fail, Blocked, Not run, or not decided (null). Nothing is pre-filled or generated.
- Complete is refused while any step is undecided.
- Rollup: any Fail → **Failed**; else any Blocked → **Blocked**; else any Not run → **Not run**; else **Passed**.
- A case counts as **executed** only when its latest completed run is Passed or Failed; Blocked / Not run / stopped / in-progress never count. **Passed** = latest completed run Passed.
- A completed or stopped run cannot be changed (409); a stale save is refused (409).

## 8. Evidence and defect support

- Evidence: **references only** — a Link (absolute http/https; `javascript:`, `data:`, `file:` and relative paths refused) or a Note (where it is kept), for the run or one step. No file is uploaded or stored; the UI says so.
- Defects: local observations (title, severity Critical/High/Medium/Low or not assessed, step, expected, actual, reproduction, Open/Closed) with an optional external reference (work-item number or https link). BirkNext never creates or changes Azure DevOps work items.

## 9. Cross-module integration

- Test Coverage and Requirements Traceability read runs through `PreparedCaseTraceability.Build(..., runs)` — one rule, identical rows. Executed/Passed counts come only from completed runs; MT-7 and Critical E2E calculations are untouched (manual results are never counted toward the Critical E2E release gate).
- Test Data: runs pin the case's Test Data names (alias + asset id), never values.
- Project Test Setup: not required before planning (unchanged).

## 10. Data model and migrations

`20261115163449_AddManualTestRunsAndDiscoveryResults` — additive: `manual_test_runs` (Id, ProjectKey, DraftId, CaseId, State, Outcome, Revision [concurrency], ContentJson, Tester, StartedAt, CompletedAt, UpdatedAt; indexes ProjectKey, (DraftId, CaseId)) and `scenario_discovery_results` (DiscoveryId PK, ProjectKey, SnapshotKey, RuleVersion, ContentJson, DiscoveredAt; index (ProjectKey, DiscoveredAt); newest 5 per project kept). No existing table changed. Local data reset discovers the tables from the EF model.

## 11. Security

Every run endpoint takes the project key; reads and writes of another project's run return 404 (server-side). Writes need the same permission as editing a plan. Evidence and defect references are validated (http/https only, bounded lengths, step must exist); links render with `rel="noopener noreferrer"`; imported text is rendered as text. No external call, no Azure DevOps write, no file storage.

## 12. Performance

Discovery is never re-run on navigation (stored result restored once per project); the home loads plan summaries, last discovery and run summaries once; run history is bounded (500); requirement states come from one traceability call.

## 13. Automated tests

New tests — backend `ManualTestRunServiceTests` (7, real PostgreSQL): pinning + resume + restart, decisions/rollup/finality/stale save, rollup rule, evidence and defect validation (javascript:/data:/file:/relative links refused, unknown step, severity, external reference) and who/when preservation, project isolation and unrunnable cases, coverage/traceability execution (in progress, passed, earlier revision, abandoned never replaces), discovery durability after a restart (restore, find by id, other project refused). Frontend `ManualTestingWorkspaceTests` (8): next-step rules, stage states, home, no-project home, start, runner (no pre-fill, unsaved, save request, complete gate), evidence/defect, completed read-only + overview/history.

Updated expectations (intentional product change, each re-asserted, none removed): sidebar structure (Manual Testing section; Test Plan Suggestions moved there as "Plan & prepare"; Manual Tester view lists the workspace first and lands on it), Feature Visibility counts (+3 always-shown rows, with an explicit check of the three routes), the traceability boundary text and the panel explanation now that executions exist (asserting the new statements), and the panel's "Passed" wording (now asserting the passed count is 0 for an unexecuted view).

| Suite (final code) | Before (`528048c8`) | After |
|---|---|---|
| Focused backend (TestPlanning, TestCoverage, Migration) | — | 182 / 182 |
| Focused frontend (workspace, navigation, documentation, user guide, TPS, linking, coverage, traceability) | — | 441 / 443 → both fixed (expectations above), re-run green in the full suite |
| Full frontend | 9,040 | **9,049 passed, 0 failed** |
| Backend partitions 1–6 | 6,797 passed, 19 skipped | **6,804 passed, 19 skipped, 0 failed** (1,052 / 1,099 / 888 / 676 / 539 / 2,550) |
| API / Web Release publish (clean copy) | 0 errors | 0 errors; WebAssembly hashes: 86 assets verified |

Partition 3 failed once in the full run (`CriticalE2E.SuggestedTestsTests…`: "Access to the path is denied" on a temp-file move while the publish and browser runs were busy); the class passed 7/7 alone and partition 3 passed 888/888 when re-run. No Critical E2E code was changed.

## 14. Browser acceptance

Fresh Release build (web :5197, API :5097), disposable database `birknext_mtw_acc_20261115` (template copy), guarded network (only the two test ports; 5000/5173 and external hosts blocked).

| Run | Result |
|---|---|
| Existing Test Plan Suggestions + integration + linking baseline (7 project categories, 1440–768 px) | **106 / 106** |
| Phase A — home (project, three stages, one next step, saved plan), Discover & understand (3 requirements with design state, stored suggestions, evidence kinds), Plan & prepare (header, edit step + expected result, unsaved flag, save, reload keeps edits), Run this test case → start with environment → no pre-filled result, complete refused while undecided, step result, evidence link (a `javascript:` link refused), defect with severity and external reference, save, leave with unsaved change asks first, overview offers Resume, home recommends Resume, resumed run keeps saved results and not the discarded change | **23 / 23** |
| Phase B — after a real API process restart: run and discovery survived; complete (rule-based outcome), read-only, history row (tester, environment, defects); browser Back/Forward between overview and run; Test Coverage shows "Passed — manual run … by local-user", executed 1, FR-001 "Executed: 1 case"; Requirements Traceability identical; drill-down back to the saved case; editing the case afterwards leaves the run unchanged and coverage says "an earlier revision of the case"; Opaque project shows none of LendoPlus's runs and refuses to open LendoPlus's run; home and Discover open for M2LB, Lendo (Blazor), Parcelo (API-only), Roomly (docs-only), Opaque (insufficient evidence) with project-specific next steps; no horizontal overflow at 1440 / 1280 / 1024 / 768 px on home, Discover, Execute and the runner; stage navigation reachable by keyboard | **48 / 48** |
| Phase C — a two-step case: Pass, Fail with actual observation → preview Failed → save → complete → Failed with per-step results kept; Test Coverage shows "Failed — manual run", executed 2, passed 1 | **7 / 7** (total across the four runs: **184 / 184**) |
| Browser page errors, console errors, HTTP 5xx, network-guard violations | 0 / 0 / 0 / 0 in every run |

Next steps observed per project (from stored records): M2LB, Lendo, Opaque "Run a test case"; Parcelo, Roomly "Choose scenarios to prepare". Screenshots: `scratchpad/acc/shots-mt/` (not committed). Live Azure DevOps: not contacted.

## 15. Known limitations

- Evidence is reference-only (no upload); defects are local (no Azure DevOps creation).
- The runner does not show a "case changed since the run started" banner; coverage/traceability and the pinned plan revision show it.
- Test Plan Suggestions has no navigation guard for unsaved plan edits (the runner has one).
- Discover & understand summarizes; scenario selection itself stays in Plan & prepare.
- Requirements are parsed in the browser; the server trusts the project's catalog sent with a save (unchanged trust boundary).

## 16. Remaining backlog

1. XLSX/CSV Azure DevOps Test Plans export from saved cases (queued next).
2. Technical Testing workspace reusing `TestingWorkspaceHeader` and the same stage conventions (queued).
3. Binary evidence upload with safe storage, if required.
4. "Run all approved cases of a plan" as a session.

## 17. Local commits

- Starting HEAD `528048c8`; this work is one local commit on top (hash in the final handoff and `git log`).
- Not committed (not owned by this task, untouched): `appsettings.Local.json`, the Kode67 gap register, older untracked audit drafts, `AIAssisted/testdata/`.
- Nothing pushed. Temporary database created: `birknext_mtw_acc_20261115` (template copy; not dropped). Test servers on :5097 / :5197 stopped after the runs.
