# Tester journey audit: Manual, Technical and Integration Testing (2026-11-16)

**Base and method.** Based on `983ca27a`, which builds on `9b02825a` (Manual Testing completeness) and the "Open now" fix. Each of three journeys
was traced through the code, then the claims behind every fix were confirmed in code and in a real browser.
Integration Testing is its own journey, because for M2LB testing single APIs does not verify the flow.

**Goal used to judge readiness.** A tester starts from a specification and ends with a documented test result, without losing context, test
data or traceability on the way.

**Classification.**
- **M**: missing functionality
- **B**: broken functionality
- **W**: incomplete workflow
- **U**: poor usability
- **I**: inconsistent behaviour
- **T**: traceability gap
- **D**: data integrity or reliability risk

Within each journey, findings are ordered by impact on everyday testers. Every fix extends an existing owner. No new module, engine or
registry was added.

## Findings and fixes

| # | Journey | Class | Finding (confirmed in code / browser) | Status |
|---|---|---|---|---|
| X1 | Cross | D | Choosing a package ("Show this package") saved the workspace without waiting. Closing the tab within about 3 s lost the choice, and the next session showed an older project. Seen in acceptance, and confirmed with a probe: once saved, the selection survives new sessions and API restarts. | **Fixed**: the save is awaited, and a failed save is stated on the page. |
| X2 | Cross | I/D | Technical Testing ignored which project the active Target Environment is bound to. Integration Testing already uses the shared `ProjectEnvironmentResolution`. Another project's runtime results could be shown as this project's. | **Fixed**: the same rule is applied. An environment bound to another project contributes no results and is named. An unbound environment is labelled unconfirmed. |
| T1 | Technical | B | In Verify, every API Quality Review run linked to "Findings", which shows only the *latest* stored report. Older runs opened a report that was not theirs. | **Fixed**: only the newest run links to the report. Older runs are labelled "summary only; the full report is kept for the latest run". |
| T2 | Technical | D | API Quality Review history, fuzzing and authorization runs live only in browser localStorage, are capped (10/5), and write errors are swallowed. | Open. This needs a server-side store under the same execution-evidence owner. It is not a quick fix. |
| T3 | Technical | M | Technical findings have no defect/retest lifecycle. The lifecycle exists for manual runs only. | Open. The manual defect model is the candidate owner to extend. |
| T4 | Technical | W | Contract-test runs are not counted in the API area's Verify status, and Execution Results excludes API Quality Review, Frontend Quality Review, Integration Quality Review, fuzzing and authorization runs. | Open. |
| T5 | Technical | U | Contract Testing defaults to "All projects", and links from Technical Testing do not carry the project. Contract "projects" are repository keys, not BirkNext projects. | Open. The mapping needs a decision; it is not a label change. |
| T6 | Technical | I | Authentication is configured in two places: Target Environment, and Contract Testing credentials. | Open. |
| T7 | Technical | M | No ad-hoc request runner. API Quality Review is read-only by design. `SecurityConfigurationReviewEngine` is not wired into any page. | Open. Product decision. |
| G1 | Integration | D/U | The Integration Test Suites snapshot list holds every imported snapshot (all projects) and did not mark the project's own. Its `aria-busy` was false while the page was still loading, so scripts and assistive tech treated it as ready too early. | **Fixed**: the project's own snapshot is listed first, marked "this project" and chosen by default. Other snapshots stay available for multi-source suites. The page is busy until its first load completes. |
| G2 | Integration | B | The suite outcome counted every run of every revision. One failed call ever recorded kept the suite *Failed*, so a passing retest never cleared it. | **Fixed**: a run whose tests were all run again later is *superseded*. It stays in history, marked, but no longer decides the outcome. A partial retest leaves the earlier run counting, because calls are not split per test. |
| G3 | Integration | M | There was no documented end-to-end result to hand over. | **Fixed**: *Download result report* on the suite. It covers project, suite revision, the journey it was prepared for (latest revision), source (SHA-256), environment, stages in flow order with coverage and blockers, runs, test data, readiness and limitations, and states that nothing was executed to produce it. |
| G4 | Integration | T | Journeys and suites carry no requirement or specification references. Test Coverage and Requirements Traceability ignore suite runs. | Open. This is the main remaining gap for "specification → documented result" in M2LB. |
| G5 | Integration | D | Composite runs do not pin test revisions; single-snapshot runs do. Test data states are frozen at generation, aliases are not pinned in the run, and approvals made later are not shown until regeneration. | Open. |
| G6 | Integration | W | A single failed stage cannot be retested (preflight allows only a prefix), and the run stops on the first failure. Period lock and Bufdata are manual or unobserved, so an M2LB full E2E cannot reach *Verified* inside BirkNext. | Open. Partly environmental: there is no observer. |
| G7 | Integration | I | Two execution paths: Skolenærvær / IQR Test & Verify and composite suites. The Skolenærvær page never links to suites. | Open. |
| G8 | Integration | D | The backend does not check that a journey belongs to the caller's project; only the page does. Suite generation as a whole is not project-scoped. | Open. It is part of the larger project scoping of snapshots, contexts and suites (backend). |
| M1 | Manual | U | The run start panel showed preconditions and steps only. The environment needed, test data and a "blocked by" note appeared nowhere before starting. "Where are you testing?" was empty for every run. | **Fixed**: a "before you start" list shows preconditions, the environment needed and test data (aliases, not internal ids). A blocked case warns but can still be started. The environment is filled in from the case's last run, or else the project's last run, and says so. |
| M2 | Manual | B | "Open now" in Plan & prepare did nothing. | **Fixed** in `983ca27a`. |
| M3 | Manual | W | Discover rows do not link to the prepared case. Plan *Refresh* can replace generated text without warning (recoverable now, through version history). Internal ids are still shown in some details. Test Data changes do not mark runs stale. | Open. These are listed in the Manual Testing gap matrix. |

## Readiness

### 1. Manual Testing
- **What works:** discovery and choice of scenarios, case editing with version history, testability, saved snapshots, run execution with step results, attachments, defects with follow-up and retest, the defect list and CSV reports, Azure DevOps file export with source references and remembered ids, and the same execution in Test Coverage and Requirements Traceability.
- **What was missing:** the items above (M1, M2), plus those fixed in `9b02825a`.
- **What remains:** M3, prerequisites not enforced at run start (shown, not gated), and attachment governance (scanning and retention).
- **Evidence:** bUnit plus the browser journeys (utps, mt a/b/c with restart, lc l1/l2 with restart, j2 J2–J4, open-now).
- **Risks:** a live Azure DevOps import has not been tried.
- **Production readiness:** **ready for supervised pilot use**. Not yet for regulated evidence, which needs attachment governance and a live Azure DevOps import check.

### 2. Technical Testing
- **What works:** project and environment context with the shared binding rule; discovery and contract import; Contract Testing with editable assertions and runs; performance runs with baselines.
- **What was missing:** X2 and T1 (fixed).
- **What remains:** T2–T7. Above all, browser-only history and no defect/retest for technical findings.
- **Evidence:** bUnit plus browser j2 J5–J6, tt and ttnav.
- **Risks:** losing history when browser storage is cleared, and results that cannot be shared by link (no run id in URLs).
- **Production readiness:** **not production-ready as an everyday workspace**. It works as a guided entry point into the specialist modules, which carry the actual work.

### 3. Integration Testing
- **What works:**
  - declared journeys with revisions;
  - suites from one snapshot or a composite source context, with rich readiness;
  - the suite records the journey it was prepared for;
  - authorised runs with per-boundary correlation;
  - a downloadable result report;
  - an honest outcome after retests.
- **What was missing:** G1–G3 (fixed).
- **What remains:** G4–G8. Above all, no specification/requirement link for journeys and suites, unpinned composite runs, single-stage retest, and M2LB downstream stages that cannot be observed.
- **Evidence:** backend suite tests; bUnit; browser itx (journey → suite → chip) and j2 J7–J9 (own snapshot chosen, report downloaded).
- **Risks:** an M2LB full flow can be prepared and documented, but cannot be *verified* end to end inside BirkNext until the downstream stages are observable.
- **Production readiness:** **ready for preparing and documenting M2LB flow tests; not for claiming verified end-to-end results.**

### 4. Cross-module workflows
- **What works:** one project selection (now saved reliably); one project ↔ environment rule (Integration and Technical Testing); one source of execution truth for manual runs (Coverage and Traceability); journey ↔ suite references.
- **What remains:** the backend project scoping of snapshots, contexts and suites (G8), the T5 contract project mapping, and deep links without run ids (Technical).
- **Production readiness:** **consistent for the manual path; partial for technical and integration paths.**

## Verification

One sequential run on this revision, with fresh disposable databases copied from `birknext_pts_acc_20261115` and isolated ports 5097/5197.

**Unit and build**
- Frontend: 9191/9191.
- Backend partitions, 0 failed in each:

  | Partition | Passed |
  |---|---|
  | 1 | 1052 |
  | 2 | 1101 |
  | 3 | 888 |
  | 4 | 682 |
  | 5 | 541 |
  | 6 | 2610 |

  Partition 6 includes the new superseded-run tests.
- Release publish: 0 errors.

**Browser**

| Suite | Result |
|---|---|
| cx | 6/6 |
| utps | 106/106 |
| mt a | 23/23 |
| mt b (after an API restart) | 48/48 |
| mt c | 7/7 |
| lc l1 | 38/38 |
| lc l2 (after an API restart) | 13/13 |
| open-now | 16/16 |
| itx | 46/46 |
| tt | 76/76 |
| ttnav | 48/48 |
| nav | 41/41 |
| nav2 | 32/32 |
| brand | 17/17 |
| link | 12/12 |
| link verify | 26/26 |
| ADO | 43/43 |

**j2 (this audit's checks)**
- Full run: J1–J6 passed. That covers the selection kept after closing the tab, the environment prefilled from the last run, the "before you start" list, and the same environment notice in Technical and Integration Testing.
- J7 and J9 failed in the full run because the script hard-coded M2LB's archive. The page correctly listed the current project's own snapshot (LendoPlus `tps-mixed.zip`) first.
- After the script was fixed and re-run on the same database, J1 and J5–J9 passed. That covers the own snapshot listed first, marked and chosen, and the report downloaded with source and stages.
- J2–J4 were not re-run: the shared state file then pointed at another database.

**p2 report step**
- It failed in the full run. Diagnosis on the same database:
  - Editing the Target Environment in two tabs (p2 S) does not change the project.
  - After selecting LendoPlus, R passes 5/5.
- p2 does not select a project. It depends on what the previous script left, so it is test order, not a product fault.
- **Residual risk to check:** p2 reads the page 3 s after load and saw a different project (ScopesAcceptance) than the one saved, so a new session may briefly show a default project before the saved workspace is restored. This is checked in the navigation consolidation work (project switching and reload).
