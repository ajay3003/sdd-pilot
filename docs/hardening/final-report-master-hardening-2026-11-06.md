# Master Hardening — final report (2026-11-06)

Branch `008-traceability-first`. Local commits only, nothing pushed or deployed. Scope: BirkNext only; the legacy BiRK WPF client, BirkBootstrapper and unrelated BiRK runtime exceptions are excluded.

Status legend: **DONE** · **PARTIAL** · **BLOCKED** · **NOT IMPLEMENTED** · **RUNTIME UNVERIFIED**.

## 1. Summary

This pass worked from the eight 2026-11-06 reports and the code to close the open items they listed. Commits `69486540..4990a4c2` deliver the following.

**M01**
- Static review and runtime verification are now separate backend operations with separate endpoints and page actions.
- Manual security assessments are durable and append-only, shared with AQR.
- A manual decision never makes a control Passed.

**Skolenærvær**
- Each journey now has a card that shows the journey's declared facts in one place.
- The Security and Verify tabs state the readiness rules.

**Traceability**
- The tab, search and status filter are in the URL.
- The HTML export states its provenance.

**AQR, FQR, Topology and IQR**
- AQR saves manual decisions per obligation and keeps the result tab in the URL.
- FQR's engine coverage table can be searched, filtered and sorted, and its result tab is in the URL.
- Topology exposes Save and Reset layout.
- IQR has one action surface for mapping confirmation, and its journey counts are labelled environment-wide.

Every item is in the gap register: [gap-register-master-hardening-2026-11-06.md](gap-register-master-hardening-2026-11-06.md). No external system was contacted, and no runtime pass was produced. Runtime-dependent items remain **RUNTIME UNVERIFIED**.

## 2. Safety constraints honoured

- **Untouched local files:** `appsettings.Local.json` and `testdata/` are modified by the user and were neither touched nor committed.
- **No external calls:** nothing called TT02, m2lbdev, production, Azure DevOps or any other external service.
  - Browser checks run on localhost and route every request to localhost only.
  - Any external host is aborted and fails the check.
  - Any state-changing request fails the check.
- **Localhost protections:** loopback-only binding, the launch-code session, CSRF controls and authorization boundaries are unchanged.
- **M01 live probe gate:** preserved, and it now runs first in the dedicated runtime operation.
  - A refusal returns the reason and stores nothing.
  - The gate's conditions are unchanged: not Production, DEV/QA matching the environment, test-lead approval, an endpoint, a valid context, at least one child, and at least one token.
- **No silent runs or credentials:** nothing runs on page load, and no credential is created or reused silently.
  - AQR never silently switches to Attended.
  - No token is copied between components.
  - Manual-decision notes and evidence references refuse secret markers ("bearer ", access_token, id_token, authorization:, password=, client_secret, JWT prefixes).
- **No invented relationships or mappings:** topology relationships and mappings are never invented or auto-confirmed.
  - The Utdanning/SkoleAdapter identity conflict stays under Relationship Review.
- **No weakened tests:** no test was removed, skipped or weakened. Assertions changed only where the behaviour changed by design, for example IQR's resolve step no longer carries a link.

## 3. Gap register

See [gap-register-master-hardening-2026-11-06.md](gap-register-master-hardening-2026-11-06.md): 24 entries (MH-01..MH-24) with class, root cause, severity, correction, verification and status.

## 4. M01 — Kode 6/7 (Gradert tilgang)

| Item | Before | After |
|---|---|---|
| Static review | Same backend run as runtime, needed a Target Environment, always called the probe | `POST api/security-classification/static-reviews`: source scope only, Target Environment optional, never calls the probe. `ReviewKind=Static`, `AnalyzerVersion`, `StaticScopeKey` (environment, or `source:{primary}`) |
| Runtime verification | Implicit when the gate allowed | `POST …/runtime-verifications`: gate first; token required; refusal = 409 with reason, nothing stored |
| Page actions | One "Run" with a mode | `sc-run-static` (primary; disabled with `sc-run-static-why` without source), `sc-run-runtime` (only when the gate allows), `sc-run` (combined, secondary); a static-only card when no Target Environment is selected |
| Matrix | Static / Runtime / Result / Residual / Next | Adds **Method** (static-only / runtime / both, with analyzer version) and **Manual assessment** (`ManualDecisionPanel`). The provenance line now includes the review kind and analyzer version |
| Manual decisions | Not persisted | `manual_review_decisions` (migration `20261106170558_AddManualReviewDecisions`): module, scope, subject, reviewer, decision, note, evidence refs, applies-to, timestamp. Append-only; current = latest per subject; full history shown |
| SEC-05 / SEC-07 | — | Audited (§4.1) |

**Rule enforced.** A control's `Result` is computed from runtime checks only:
- Passed only when all of its runtime checks pass and no source issue is open.
- Failed on any runtime failure.
- Partial otherwise.
- Not assessed with no runtime check.

A manual decision is shown beside the result and never changes it.

### 4.1 SEC-05 / SEC-07 audit

| Control | Finding | Status |
|---|---|---|
| SEC-05 Downstream protection | No shared control exists, and the static next-action is suppressed. The consuming reviews (for example Skolenærvær domain requirements) own the evidence. Runtime = Not assessed | **PARTIAL** (correct by design) |
| SEC-07 Failure and recovery | Static facts (`access-authz-fail-closed`, `access-policy-handler`, `readlog-fail-closed`, `cdc-unknown-level`) are mapped. No safe runtime fail-closed probe exists, so runtime = Not assessed and the residual risk says so | **PARTIAL** (no runtime check invented) |

## 5. Skolenærvær

The new `JourneyCard` (`JourneyCardPresentation`) is the first block on each journey tab. It shows:
- **Purpose:** the declared description, or "No purpose is declared".
- **Eligibility:**
  - "Executable after explicit confirmation";
  - "Blocked — N prerequisites not met";
  - "Not executable here".
- **Verification:** "Not run (source evidence only — not runtime verified)", "Executed, not verified", or another recorded state.
- **Expected result:** the declared steps and evidence basis.
- **Next action:** named by the first blocker's owner.
- **Blockers:** grouped by kind.
- **Prerequisites by kind:** required integrations (Configured / Configured, disabled / Not configured), source evidence, Test Data, and authentication/authorization.
- **Defined scenarios:** each is labelled "not an executable test".
- **Latest recorded run:** labelled "Historical — not current readiness".

Nothing that the pack does not declare is shown.

- **Security tab:** a reading guide says:
  - shared evidence is static evidence, not runtime verification;
  - runtime tests run only from M01, behind its gate;
  - a manual decision never makes a control Passed;
  - Not assessed is not zero.
- **Verify tab:** a scope note says one verified message does not make a journey ready, and an executed step that was not observed downstream stays "Executed, not verified".

## 6. Requirements Traceability

- **URL state:**
  - `?tab=`, `?q=` and `?status=linked|missing`;
  - written with replace navigation, and read on load and on Back/Forward;
  - unknown values fall back to the defaults;
  - running the analysis keeps the URL state instead of resetting it.
- **Export provenance:** the export lists project, module, artifact pack, artifact source, evaluation time (UTC) and policy.
  - Unknown values read **"Not recorded"** and are never inferred.
- **Not changed:** derived, candidate and confirmed matrix states were **not added**. The document-reference model has no such states, and the brief allows states only where the model supports them.
- **Unchanged rules:** no unknown-as-zero and no combined score or mixed denominators, as established in 3ee25080.

## 7. API Quality Review

- **Manual decisions per obligation:** each obligation row (area) now has a durable `ManualDecisionPanel`.
  - Module `api-quality-review`, scope = the active profile, subject = the area, applies-to = the selected targets.
  - The static "needs manual review" pill is replaced by the recorded decision and its history.
- **Result tab in the URL:** `?tab=`, with replace navigation.
- **Authentication semantics unchanged:** Blocked is not Failed, the authentication mode gates protected targets, and nothing switches silently.

## 8. Frontend Quality Review

- **Engine coverage table:**
  - search (engine, surface, prerequisite, result, reason);
  - a release-impact filter (Blocking / Satisfied / Non-blocking limitation / Not applicable, using the shared labels);
  - sort by plan order, blocking first, or engine A–Z;
  - "Showing N of M", an explicit "No engine matches these filters" state, and Clear filters;
  - the controls appear only above 3 rows.
- **Result tab in the URL:** `?tab=`; an invalid value falls back to Summary.

## 9. Integration Topology and IQR

- **Topology:** `tm-save` / `tm-reset` expose the renderer's existing layout persistence (browser-local), with an aria-live status. Zoom, fit, keyboard navigation, edge evidence and grouping are unchanged from 3365d4f8.
- **IQR:**
  - journey counts are labelled "(environment-wide)";
  - the resolve and contracts steps no longer duplicate the attention list's actions, so there is one action surface and mapping confirmation stays explicit.

## 10. Shared GuidedStep audit (Phase 9)

All consumers were audited: ApiReview, ConnectedSystems, FrontendQuality, IntegrationCatalog, IntegrationReview, SecurityReview, SkoleNaervaer, Topology and Traceability. Each `GuidedStepState.Done` is reached only from the consumer's own state.

| Invariant | Where it is enforced |
|---|---|
| Blocked is not Failed | AQR readiness, M01 runtime-context, and Skolenærvær execution are Blocked, never Failed |
| Not executed is not Passed | M01 runtime step: Waiting "Not run — start it explicitly" (**fixed in this pass**: it said "Runs with the next review", which was stale after the split) |
| Not assessed is not zero | M01 matrix and traceability transitions show "Not assessed"; the IQR "never 0 limitations" test |
| Source verified is not runtime verified | IQR "resolved is not runtime verified"; journey card "source evidence only — not runtime verified"; Skolenærvær Security guide |
| Configured is not executable | Connected Systems validate is Done only when every system is runtime-verified; the journey card shows "Configured" separately from eligibility |
| Executed is not verified | Journey card "Executed, not verified"; Skolenærvær verify step; Verify scope note |
| Historical success is not current readiness | Journey card "Historical — not current readiness" (test: a past Verified run with a current blocker stays Blocked) |

## 11. Tests added (negative and boundary cases)

- Backend:
  - `SecurityClassificationSourceScopeTests` +5: static without an environment or probe; static with an environment; no-source refusal; runtime blocked (theory); token required.
  - `ManualReviewDecisionStoreTests` (7): append-only; latest wins; validation; secret refusal; note required for IssueFound/RiskAccepted.
- Frontend:
  - `ManualDecisionPanelTests` (3);
  - `SecurityClassificationReviewTests` +3;
  - `SecurityControlMatrixTests` +1 (an approved context without a run is Waiting, not Done);
  - `JourneyCardPresentationTests` (4);
  - `FrontendQualityEngineCoverageTableTests` (3), including the empty filtered state;
  - `RequirementsTraceabilitySemanticsTests` +3, covering the deep link, unknown-value fallback, and provenance with "Not recorded";
  - `SkoleNaervaerTestingPageTests` (legend and scope assertions);
  - updated IQR tests.

## 12. Regression

All runs used the scratch worktree build, memory-guarded and sequential.

**Frontend (`BirkNext.Web.Tests`, full suite): 8,415 / 8,415 passed, 0 failed, 0 skipped.** Before this pass it was 8,398.

**Backend (`BirkNext.Api.Tests`, six partitions): 5,972 passed, 13 skipped, 0 failed.** The skips are the existing environment-conditional skips.

| Partition | Passed | Skipped | Failed |
|---|---|---|---|
| 1 SourceAnalysis/Integrations/PerformanceTests | 984 | 1 | 0 |
| 2 TargetEnvironmentDetection/TestData/LocalHttpsProxy/AltinnTesting | 1,054 | 5 | 0 |
| 3 IntegrationJourneys/CriticalE2E/ApiQuality/AuthenticatedReview | 806 | 0 | 0 |
| 4 Canonical/Contract/QualityReview/FQ engines/Dependency/Companion/Azure | 646 | 0 | 0 |
| 5 Topology/TestCoverage/SecurityClassification/… | 516 | 0 | 0 |
| 6a / 6b / 6c (the rest) | 125 / 535 / 1,306 | 0 / 0 / 7 | 0 |

**Migration:** `AddManualReviewDecisions` was applied on startup of the published API against `birknext_gaps`, and the migration-integrity test passes.

**Note on the backend run:** it ran before the last two frontend-only commits (c15b7e9d, 4990a4c2), which touch no backend code.

## 13. Browser verification (localhost only)

The published Release build ran on the local stack: API on :5099 and web on :5199, against `birknext_gaps`.
- Every script aborts any non-localhost request and fails on any state-changing request.
- The stack was stopped afterwards by the PIDs owning ports 5099 and 5199.

| Script | Result |
|---|---|
| `mh-e2e.cjs` (new: this pass's changes) | **12/12 passed**, 3 SKIP (below) |
| `m01-snt-e2e.cjs` | **11/11 passed**, after one defect was found and fixed (below) |
| `aqr-e2e.cjs` | 8/8 |
| `fqr-trace-e2e.cjs` | 12/12 |
| `topo-e2e.cjs` | 14/14 |
| `iqr-e2e.cjs` | 10/10 |
| `sec-e2e.cjs` (localhost security: loopback, launch code, CSRF) | 12/12 |

**SKIPs (not counted as passes):**
- **M3, manual assessment column:** there is no stored M01 review in `birknext_gaps`.
- **S2, historical-run label:** there is no recorded journey run.
- **R1/R2, Traceability deep link after analysis:** no artifact pack is selected in this dataset, and selecting one writes the workspace.

All three are covered by bUnit tests.

**Defect found by the browser run (MH-24):**
- **Symptom:** `m01-snt-e2e` M2 failed. With the live gate blocked, the secondary `sc-run` button read "Run combined review", promising a runtime part it would not run.
- **Fix (4990a4c2):** it now reads "Run review (static only — runtime stays unverified)", and a bUnit assertion was added.
- **Browser check:** M2 was updated to require "static only" and "runtime stays unverified" in static mode, which is stricter than before. After the fix it passes.

## 14. UI polish

- The coverage filters wrap at narrow widths.
- The journey card uses an auto-fit grid and `overflow-wrap: anywhere` for long identifiers.
- Live status is announced through aria-live (topology layout, coverage count).
- A disabled action always gives its reason (`sc-run-static-why`).

## 15. Commits

| Commit | Change |
|---|---|
| 69486540 | M01: separate static and runtime operations; durable manual review decisions |
| 38489e9a | AQR: durable manual decisions per obligation; result tab in URL |
| 87913a35 | FQR: engine coverage search/impact filter/sort with counts; result tab in URL |
| bed60723 | Topology: explicit save/reset layout controls |
| 78176ad2 | IQR: environment-wide journey count labels; one action surface |
| 3a278205 | Traceability: URL state |
| f929f37b | Skolenærvær: journey-at-a-glance card |
| 58773e75 | Traceability: export provenance |
| 5750925b | Skolenærvær: Security reading guide and Verify scope |
| c15b7e9d | M01: runtime step wording after the split ("Not run — start it explicitly") |
| 4990a4c2 | M01: combined Run button no longer promises runtime when the gate is blocked |

## 16. Remaining limitations (not done, stated plainly)

- **M01:** runtime verification of any control is **RUNTIME UNVERIFIED** here; it needs an approved DEV/QA context. SEC-05 and SEC-07 have no runtime path (§4.1).
- **Traceability:** the matrix still starts from Constitution rows, and the Task edge is Not assessed. There are no derived/candidate/confirmed states (the model does not have them). `Health.CoveragePercentage` is still computed but not shown.
- **AQR:** the Auto policy is unchanged (shared policy). The per-tab result redesign beyond decisions and URL state was not done.
- **FQR:** the Evidence tab layout, history and Summary action grouping were not redesigned. The coverage filter state is not in the URL; only the tab is.
- **Topology:** no grouping of Evidence & Gaps by severity, system or action. The layout is saved in the browser only, not on the server.
- **IQR:** the post-run result tabs are unchanged.
- **Skolenærvær:** live journeys were not run (by rule), so the journey card's verification states for executed runs are verified only by unit tests.

## 17. Acceptance matrix

| # | Requirement | Status | Evidence |
|---|---|---|---|
| 1 | Gap register | DONE | gap-register doc |
| 2a | M01 static and runtime as separate operations | DONE | 69486540, backend +5 tests |
| 2b | SEC-01..07 complete; SEC-05/07 audited | PARTIAL | §4.1: no runtime path for SEC-05/07, by design |
| 2c | Matrix: analyzer version, method, provenance | DONE | §4 |
| 2d | Durable manual assessments with audit history | DONE | store + 10 tests |
| 2e | Manual decision never makes Passed | DONE | `SecurityControlMatrix.Build`, tests |
| 2f | M01 runtime verification executed | RUNTIME UNVERIFIED | external DEV/QA context required |
| 3 | Skolenærvær Journeys/Security/Verify clarity | DONE | §5 |
| 3b | Live journey execution verified | RUNTIME UNVERIFIED | not allowed under this command |
| 4a | Traceability URL state | DONE | §6 |
| 4b | Export provenance | DONE | §6 |
| 4c | Matrix redesign with derived/candidate/confirmed | NOT IMPLEMENTED | the model does not support these states |
| 4d | Never unknown-as-zero; no mixed denominators | DONE | 3ee25080 + tests |
| 5a | AQR durable manual assessments | DONE | 38489e9a |
| 5b | AQR result-tab usability (URL) | PARTIAL | URL state done; per-tab redesign not done |
| 6a | FQR coverage search/filter/sort/drill-down | PARTIAL | search/filter/sort/counts done; drill-down stays in the existing diagnostics disclosure |
| 6b | FQR URL tab state | DONE | 87913a35 |
| 6c | FQR post-run redesign | PARTIAL | §16 |
| 7 | Topology layout/zoom/fit/keyboard/save/reset/edge evidence | PARTIAL | save/reset added; grouping not added |
| 8 | IQR dedupe + environment-wide labels | DONE | 78176ad2 |
| 9 | GuidedStep invariants | DONE | §10 (one stale label fixed) |
| 10 | Negative and boundary tests | DONE | §11 |
| 11 | UI polish | DONE | §14 |
| 12 | Full regression (frontend, 6 backend partitions, browser, security) | DONE | §12, §13 |
| 13 | This report | DONE | — |
