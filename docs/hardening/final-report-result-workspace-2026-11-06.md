# Result workspace improvements — final report (2026-11-06)

Branch `008-traceability-first`. The work ran from baseline `546584f1` to `9b21d8ca`, as 13 local commits. Nothing was pushed or deployed. No external environment was contacted.

`appsettings.Local.json`, `testdata/` and the untracked `docs/audits/` are someone else's and were left untouched.

Acceptance states: **DONE** · **PARTIAL** · **BLOCKED** · **NOT IMPLEMENTED** · **RUNTIME UNVERIFIED**.

## 1. Baseline and scope

- **Baseline:** `546584f1` (Master Hardening report). The prior gap register, with 24 entries, left three open items:
  - **MH-17:** FQR post-run redesign.
  - **MH-19:** Topology Evidence & Gaps grouping.
  - AQR per-tab redesign, recorded as a limitation.
- **Scope:** usability only for Integration Topology Evidence & Gaps, the five FQR result tabs and the eight AQR result tabs.
  - The following were not changed: business rules, security semantics, evidence calculations, execution behaviour and persistence contracts.
- **Phase 1 audit:**
  - Two read-only agents inventoried the FQR and AQR result workspaces: rendering sources, shared versus page-only logic, test ids and their test files, the accessibility contracts, filter state and invariants. FQR has about 900 test references across 24 files; AQR has about 950 across 16.
  - I audited Topology directly.
  - The findings are summarised in §2 and §3.

## 2. Existing behaviour preserved

**Shared computation was not modified** (it is presentation projection plus resolver output):
- `FrontendQualityResultPresentation.Build`, all of `FrontendQualityPostRunPresentation`, `FrontendQualityRunSemanticsProjection`, `FrontendQualityReviewHistory.Entry`;
- `ApiReviewPresentation`, `ApiReviewEvidencePresentation`, `ApiReviewScoring`;
- the topology resolver and `MissingEvidenceGroups`.

**One exception:** `ApiReviewPostRunPresentation.Headline` gained an early return for runs that executed nothing. This fixes a confirmed misleading statement (§3, P-6); every other headline is unchanged.

**Invariants kept, and checked by component and browser tests:**
- **FQR issues and coverage:**
  - one card per logical issue;
  - derived and informational issues stay apart from actionable ones;
  - only an issue-policy blocker blocks an issue;
  - the optional engine never blocks;
  - WCAG findings are evidence for a criterion, not its assessment;
  - mandatory manual assessment is kept apart from recommended verification;
  - authenticated surfaces are distinct from public ones.
- **History:** an opened history run shows the stored report and is never recomputed.
- **AQR states:**
  - Blocked is not Failed;
  - Not assessed is not Passed;
  - zero executed is not success;
  - authentication enforcement is not authorization verified;
  - the authentication mode is fixed at request time and never switched silently;
  - manual decisions never change automated results;
  - the single-request latency versus Performance Quality ownership split is kept.
- **Topology:**
  - only resolver fields are used;
  - no relationship is invented or confirmed;
  - the Utdanning/SkoleAdapter conflict stays under Relationship Review.
- **Existing test ids and deep links:** all retained, including `?tab=` on FQR, AQR, Topology and Traceability. No test was removed, skipped or weakened.

## 3. Confirmed usability problems (and defects)

| ID | Module | Problem | Class |
|---|---|---|---|
| P-1 | FQR History | After a reload, stored runs were unreachable: the result workspace (including History) renders only with a run from this session | FUNCTIONAL GAP |
| P-2 | FQR History | With no session run, "Return to latest run" loaded the newest stored report and dropped its "Previous run" label, so a historical result read as current | **CONFIRMED DEFECT** |
| P-3 | FQR Summary/Coverage | In-panel links that switch view hid the focused control, so focus was lost | UX GAP (a11y) |
| P-4 | FQR Issues | A domain filter applied from Coverage was silent, with no count of what was shown | UX GAP |
| P-5 | FQR Evidence | "Grouped into" was plain text; the literal `^`/`v` sort glyph was announced by screen readers; there was no `aria-sort` and no filter count | UX GAP (a11y) |
| P-6 | AQR Overview | A run blocked before execution read "Automated runtime review completed for 0/N … No automated issues were identified"; Overview said "No automated issues identified" whenever there were no findings, even when nothing ran | **CONFIRMED DEFECT** |
| P-7 | AQR frame | Arrow keys changed the tab but focus stayed on a `tabindex=-1` button; links into another area lost focus; the tab panel had no `tabindex` | UX GAP (a11y) |
| P-8 | AQR Overview | A result restored from browser history looked identical to a fresh run | UX GAP |
| P-9 | AQR REST/GraphQL | There was no way to find failing, blocked or not-executed operations | UX GAP |
| P-10 | AQR Contracts / Security / Errors / Performance / Findings | Drift said "(see Findings)" as plain text; the security summary didn't link to its sections; Errors had no heading, intro or empty state; Performance never stated ownership; Findings had no search or sort and didn't say when evidence was absent | UX GAP |
| P-11 | Topology | The unresolved list was flat; the summary groups weren't navigable | UX GAP |

## 4. Changes per module

**Integration Topology — Evidence & Gaps** (63ee0980)
- **Grouping:** unresolved relationships form collapsible `<details>` groups, by fix action plus source role or by the stated owner, switched with a radio pair.
  - Groups by fix action use the same key and order as the existing summary, so counts always agree. They show "N unresolved" and a "Next:" line naming the owner page.
  - Groups by owner show "No owner stated" where the resolver states none.
- **Summary lines:** these are now links that focus their group.
- **Unchanged:** per-item markup, test ids and the one action per item. There is no group-level action, and Resolved connections remain a separate section.

**Frontend Quality Review**
- **History** (18b38504):
  - A pre-run "Previous runs (N)" entry opens the newest stored run without running a review.
  - The opened-run banner says "Historical — not current readiness".
  - With no session run, "Close stored run" returns to setup (fixes P-2).
- **Summary** (23a9840f): "Review issues/coverage" and the other in-panel view switches move focus to the opened panel.
- **Issues** (90118e1e):
  - Active filters state "Showing N of M logical issues", plus the domain, in a `role=status` region.
  - The Coverage domain link focuses Issues.
  - Full issue cards carry one anchor id; compact Summary copies carry none.
- **Coverage:** no code change was needed. The search, impact filter, sort and counts added in 87913a35 were verified in the browser (§7).
- **Evidence** (2d761316):
  - "Grouped into" opens and focuses the issue's one card. Derived and observation rows say "listed below" and are not linked.
  - Sort headers expose `aria-sort`; the glyph is `aria-hidden`.
  - A findings filter count is shown.
  - The controls have test ids.

**API Quality Review**
- **Overview and frame** (138d0402):
  - The headline and the empty state are honest for zero-executed runs (fixes P-6).
  - A stored result shows "Stored result … historical, not current readiness".
  - Arrow keys move focus with the selection; links into areas focus the panel, which now has `tabindex=0`.
- **REST/GraphQL** (3c0ff130):
  - Search and an "All / Needs attention / Not executed" view, with "Showing N of M operations".
  - Rows carry `data-executed` / `data-attention`. A not-executed operation is never "attention".
  - "Not fully reviewed" jump buttons focus the service cards, which stay visible while operations are filtered.
- **Contracts** (ff2d3715):
  - A "Contract evidence: X of N services had a usable contract or schema …" line.
  - Drift links to its findings.
  - Section headings have ids.
- **Security** (52bea22f):
  - Summary items link to their sections.
  - An evidence-kind line: runtime observations only, no static analysis, authentication observed is not authorization verified.
  - A pointer to the one manual decision for authorization in Overview, with no duplicate form.
- **Errors** (199438a5):
  - Heading and intro ("Not tested — never a failure and never a pass").
  - Counts: observed failures, warnings, no issue observed, not tested.
  - The empty state is "Not assessed".
- **Performance** (60d37e71): an ownership statement.
  - Single-request latency is owned here.
  - Load testing, workload, API response Warning/Poor thresholds and acceptance criteria belong to Performance Test Review and Performance Quality, which the tab links to.
  - "Nothing on this tab implies load-test acceptance."
- **Findings** (9b21d8ca):
  - Text search and a display-only sort (report order, highest severity first, service).
  - "No evidence recorded" when evidence is absent; "+N more not shown" past 8 items.
  - "View its checks in {area}".

## 5. Files modified

**Production (frontend only; no backend, contract or migration change):**
- `Pages/IntegrationTopologyPage.razor`, `.razor.css`
- `Services/TopologyPresentation.cs`
- `Pages/FrontendQualityReview.razor`, `.razor.css`
- `Components/FrontendQualityLogicalIssues.razor`
- `Pages/ApiQualityReview.razor`, `.razor.css`
- `Services/ApiReviewPostRunPresentation.cs`

**Tests:**
- `Pages/IntegrationTopologyPageTests.cs` (+2)
- `Pages/FrontendQualityPostRunViewsTests.cs` (+3)
- `Pages/FrontendQualityResultDensityTests.cs` (+4)
- `Pages/ApiQualityReviewSimplificationTests.cs` (+8)
- `Services/ApiReviewAuthenticationOutcomeTests.cs` (+1)
- `Fixtures/SyntheticResultFixtures.cs` and `Fixtures/SyntheticResultFixturesTests.cs` (+6, new)

## 6. Synthetic fixture design (0ba70089)

`BirkNext.Web.Tests/Fixtures/SyntheticResultFixtures` builds deterministic stored results from the real model types and writes them through the real browser stores (a captured `birkNextStorage`). The stores used are:
- `FrontendAnalysisSettingsService`: one Target Environment, `synthetic-fixture-0001`, "SYNTHETIC FIXTURE (localhost only)".
- `FrontendQualityReviewHistory`: four runs.
  - Review required: partial coverage, WCAG 1.4.3 evidence, derived and informational items, a manual item.
  - Blocked: a required engine did not complete, plus an issue-policy blocker.
  - No automated block detected.
  - Preflight unreachable: nothing executed.
- `ApiReviewHistoryService`: two runs.
  - Latest: REST partially completed, with one 500, one latency warning and one unexecuted POST; GraphQL completed; drift compared; an errors Fail and a NotTested.
  - Older: GraphQL blocked.

**Safety:**
- The target is `http://127.0.0.1:9/synthetic-fixture/` (the loopback discard port). Nothing listens there, there's no DNS lookup and no external host.
- Every title is marked `[synthetic]`.
- There is no credential, token or personal data. A test enforces this, along with "only loopback hosts besides the WCAG registry's documentation links".
- The run ids, which the store normally makes random, are fixed, so the fixture is byte-for-byte deterministic (tested).
- The fixture is exported only when `BIRKNEXT_SYNTHETIC_FIXTURE_OUT` is set.

**Browser seeding (`rw-fixture.cjs`, scratchpad):**
- The fixture is written to the localStorage of a fresh, disposable Playwright context only. It never touches the backend database or anyone's browser profile.
- The AQR manual decisions (one current and one superseded for "Write operations", one "Issue found" for "Authorization", the third obligation undecided) are served read-only from the fixture through route fulfilment.
- A decision POST, or any other state-changing request, is recorded as unsafe and aborted.
- Four POST-for-query status endpoints are allow-listed by exact path after reading their controllers: they persist nothing. They are `runtime-authentication/resolve`, `authenticated-review/capabilities`, `frontend-quality-engines/status` and `browser-companion/status`.
- **Harness fix found during this work:** the port-5000 rewrite ran before the unsafe-request guard, so writes via port 5000 had been unguarded in my scripts. The guard now runs first.

## 7. Browser coverage (localhost only, published Release build, `birknext_gaps`)

| Script | Result | Covers |
|---|---|---|
| `rw-topo-e2e.cjs` (new) | **11/11** | Accurate group counts that sum to the total; summary links in matching order; no duplicate group actions; resolved section kept apart; keyboard: link focuses group, Enter collapses it; by-owner grouping; 390px layout; guards |
| `rw-fqr-e2e.cjs` (new) | **39/39** | History H1–H8, keyboard K1–K2, Summary S1–S5, Issues I1–I5, Coverage C1–C5, Evidence E1–E5, 390px on all 5 tabs, guards Z1–Z4 |
| `rw-aqr-e2e.cjs` (new) | **46/46** | Overview O1–O9 (stored label, four facts, keyboard, manual decisions with history, no Passed), REST/GraphQL P1–P5, Contracts CT1–CT4, Security SE1–SE5, Errors EH1–EH2, Performance PF1–PF3, Findings FI1–FI6, 390px on all 8 tabs, guards |

Every new script also asserts:
- no state-changing request;
- no external or synthetic-target request;
- no page errors;
- no 5xx API responses.

Run is never clicked.

## 8. Regression results (final source `9b21d8ca`)

- **Frontend full suite:** **8,438 / 8,438 passed**, 0 failed, 0 skipped. This was run against `9b21d8ca` as built in the scratch worktree; before this task it was 8,415.
- **Focused suites during the increments** (all passed):
  - Topology 35;
  - FQR 800 → 806;
  - AQR 353 → 359;
  - fixtures 6.
- **Backend:** unchanged by this task; no backend file is in the diff.
  - The last full backend run covered `546584f1` (which includes the last backend change, 69486540). It found 5,972 passed, 13 skipped and 0 failed across 6 partitions.
  - It was not re-run against `9b21d8ca`.
- **Cross-module browser regression (Phase 6), against the final build:**

| Script | Result |
|---|---|
| `aqr-e2e` (AQR ↔ Authentication) | 8/8 |
| `fqr-trace-e2e` (FQR ↔ Authentication, Traceability) | 12/12 |
| `topo-e2e` (Topology ↔ Relationship Review) | 14/14 |
| `iqr-e2e` (IQR ↔ Integration Catalog) | 10/10 |
| `cs-catalog-e2e` | 15/15 |
| `m01-snt-e2e` | 11/11 |
| `mh-e2e` | 12/12 |
| `sec-e2e` (localhost security) | **11/12 on the first run, then 12/12 on four consecutive re-runs** |

- **The `sec-e2e` failure:**
  - **What failed:** check 4 ("right code connects and the page loads its data") rendered the Test Data error state once.
  - **Investigation:** the API log has no 5xx at all. The path (local-session connect → Test Data overview) is untouched by this task.
  - **Status:** recorded as an unreproduced intermittent failure (1 of 5). The check now prints the error text if it recurs.
  - **Manual decisions ↔ History and Result tabs ↔ URL navigation:** covered by O7–O8, K2 and O5.

## 9. Remaining limitations

- **Filters not in the URL:** FQR Issues/Evidence/Coverage filters and AQR operation and finding filters are page-local. Only the tab is in the URL.
- **No run comparison:** FQR History offers no side-by-side comparison of two runs, because no comparison model exists. Runs are opened one at a time.
- **No server-side storage:** FQR/AQR history and the Topology layout remain browser-local, by existing design.
- **AQR result headline:** it still doesn't state which authentication mode was used, because `ApiReviewReport` doesn't record it. Adding that is a contract change, out of scope.
- **Topology groups by system or severity:** not offered, because the model has neither a stable system id nor a severity.
- **No external verification:** nothing here verifies an external system. All results are synthetic or local, so any statement about real targets is **RUNTIME UNVERIFIED**.

## 10. Residual risks

- **Page size and test coupling:**
  - `ApiQualityReview.razor` (about 2,000 lines) and `FrontendQualityReview.razor` (about 2,070) grew further. Later changes still depend on heavy test-id coupling.
  - The new tests assert behaviour via test ids rather than markup structure, to limit that.
- **Synthetic data shape:** the fixture reflects the current model shape. A model change would surface as a failing fixture test, which is intended.
- **Browser scripts:** they live in the session scratchpad, as before, not in the repository. Their content is described here, and the fixture itself is committed.

## 11. Commit references

| Commit | Change |
|---|---|
| 63ee0980 | Topology: Evidence & Gaps grouped by fix action or owner |
| 0ba70089 | Tests: deterministic synthetic FQR/AQR stored-result fixture |
| 18b38504 | FQR History: stored runs reachable, labelled historical; close never shows a stored run as current |
| 23a9840f | FQR Summary: view links move focus to the opened panel |
| 90118e1e | FQR Issues: filter counts, domain-link focus, one anchor per issue card |
| 2d761316 | FQR Evidence: links to the one issue card, derived rows not linked, aria-sort, counts |
| 138d0402 | AQR Overview: zero executed is never "completed / no issues", stored label, focus |
| 3c0ff130 | AQR REST/GraphQL: attention / not-executed views, counts, jump to services |
| ff2d3715 | AQR Contracts: contract-evidence coverage, drift link, labelled sections |
| 52bea22f | AQR Security: section links, evidence kind, manual decision pointer |
| 199438a5 | AQR Errors: heading, intro, counts, Not assessed empty state |
| 60d37e71 | AQR Performance: ownership statement |
| 9b21d8ca | AQR Findings: search, sort, evidence absence, area link |

## 12. Acceptance matrix

| Item | Component tests | Synthetic browser | Status |
|---|---|---|---|
| Phase 1: audit of contracts and invariants | — | — | DONE (§2, §3) |
| Phase 2: synthetic fixtures (deterministic, safe, all states, history, manual decisions) | 6/6 | used by 85 checks | DONE |
| Topology: grouped Evidence & Gaps | 35/35 | 11/11 (+ topo-e2e 14/14) | DONE |
| FQR Summary | pass | S1–S5 | DONE |
| FQR Issues | pass | I1–I5 | DONE |
| FQR Coverage | pass (no change) | C1–C5 | DONE |
| FQR Evidence | pass | E1–E5 | DONE |
| FQR History: historical vs current, immutable | pass | H1–H8 | DONE |
| FQR History: comparison between runs | — | — | NOT IMPLEMENTED (no comparison model) |
| AQR Overview | pass | O1–O9 | DONE |
| AQR REST / GraphQL | pass | P1–P5 | DONE |
| AQR Contracts | pass | CT1–CT4 | DONE |
| AQR Security | pass | SE1–SE5 | DONE |
| AQR Errors | pass | EH1–EH2 | DONE |
| AQR Performance | pass | PF1–PF3 | DONE |
| AQR Findings | pass | FI1–FI6 | DONE |
| Phase 6: cross-module regression | — | 7 suites passed; sec-e2e 1 unreproduced intermittent | DONE (intermittent recorded) |
| Phase 7: full frontend at the final commit | 8,438 / 8,438 | — | DONE |
| Phase 7: full backend | not re-run (backend unchanged; last run at 546584f1) | — | PARTIAL (not re-executed at 9b21d8ca) |
| Real targets / external systems | — | — | RUNTIME UNVERIFIED |
