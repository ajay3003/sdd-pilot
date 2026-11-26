# BirkNext — Manual Testing complete UX and E2E hardening V9 (2026-11-26/27)

Commits:
- `5314ad7b`: Finn og forstå panel and toolbar, plus server search and pager.
- `7b1eee79` and `956ce3d7`: defects found by the browser journeys.

The user asked for commits («commit all before continuing», «continue … and commit»), overriding the brief's no-commit line; nothing was pushed.

The user's measure: **find scenario → understand the requirement → prepare a test case if needed → add the scenario to a test plan → run the test → see the result in Testoversikt**, with correct data, traceability and results throughout. The browser journeys below test exactly that.

## A–B. Inventory and baseline
Already in place before V9 (earlier reports): V6 focused sidebar, V8 scope, Planlegg V5, progressive results V1, cross-page intent (`?scenario=`), scenario occurrence execution, plan copy with reset results, the follow-up panel fix.

**Baseline at the start of V9:**
- **Frontend:** 9,865 / 9,865.
- **Backend:** NOT green. `CI_DEF_3` times out in full runs; see the backend stability report.

## C–H. Finn og forstå (`Pages/ManualTestingDiscover.razor`)

| Area | Change | Status |
|---|---|---|
| Detail panel | «Forbered testtilfelle» and «Velg» directly under the title. Then Forventet resultat, the test plans and the test cases. Classification, test type, automated tests, source text, requirements and source basis are folded into «Klassifisering, kilde og sporbarhet» (nothing removed, closed by default). One primary action, not repeated at the bottom. | IMPLEMENTED, VERIFIED (test + browser A) |
| Toolbar | Search, Prioritet and Status (preparation state) on the main row; Område and the other filters under «Flere filtre» with their count. Every active filter is a removable chip; «Nullstill filtre» stays. | IMPLEMENTED, VERIFIED (test + browser E) |
| Scenario rows | Compact rows from the earlier pass: 110 → 83 px, measured. | kept |
| Grouping | Brukerhistorie → Scenariotype, collapsed on first visit; standalone scenarios visible. | kept (earlier verification) |
| Search over a large population | Server search beyond the loaded window (see the pagination V2 report). | IMPLEMENTED, VERIFIED (tests; browser: pagination V2 report) |

**NOT IMPLEMENTED:** a three-column layout (stories / list / panel side by side). The page keeps its story navigation, list and a side panel. A larger layout change was not justified against the measured flow; no defect was found in it.

## I. Source and classification integrity
Unchanged and covered by earlier work:
- specification-scoped identities (P0);
- scenario type and test level as separate axes;
- no E2E label inferred from a title;
- developer tests shown as evidence, not as proof of coverage.

The detail panel now folds this evidence; it does not remove it.

## J–M. Planlegg og klargjør, case lifecycle, translation, readiness
Audited through the journey, and the V5 report's items hold:
- one primary action;
- the readiness count says it can still change;
- no «Start test» in Planlegg;
- the step cards.

**Defects found and fixed in V9:**
1. **HTTP 500 on concurrent plan writes.** Planlegg's automatic translate-all (6–12 s) and a case creation wrote the same plan revision. The unique (plan, revision) index stopped the second write as an unhandled `DbUpdateException`, and the page showed «Kunne ikke oversette».
   - Now it is the normal **409 revision conflict**; nothing is overwritten.
   - Test: two concurrent writes from the same revision give exactly one save and one 409. Without the fix the test fails with `DbUpdateException`.
2. **The automatic translation gave up after that 409.** The race-path message didn't match `PlanPreparePresentation.IsStale`.
   - Same wording as the concurrency conflict now; `PlanStaleContractTests` pins all three server messages. The automatic translation retries on the new revision.
3. **A newly created case did not open.** A scenario's «Klargjør» view stayed in front after «Nytt testtilfelle lagt til … Skriv teststegene og lagre».
   - `Open(case)` now closes the preparation view. Test X05, which fails without the fix.

**Also seen, not changed:**
- **«Scenarioet har ingen kilde å lagre klargjøringen i»** for a scenario whose specification document cannot be resolved to a grouping document. It is reported honestly; NOT INVESTIGATED further.
- **Per-document size limit:** `ArtifactExplorerContext.MaxImportBytes` is 1 MB per document. A larger specification is not read as a specification; found with the synthetic package.

## N–P. Test plans, execution, copying
Verified in the browser:
- adding to a plan (scenario only, no case);
- results per occurrence (Passed, Blocked);
- the copy starts at NotRun and the original keeps its result;
- move/remove between suites: earlier commit `f7a29923`, component and backend tests.

## Q. Testoversikt
The result shows on Testoversikt after recording it (journey A). The follow-up fix (`fcf828d0`: an open plan execution counts as follow-up and links to its own execution) has a component test.

## R. Population completeness
See the pagination V2 report:
- the complete population is stored;
- only 10,000 are sent at once;
- server search reaches the rest;
- the page says when the population is not fully loaded.

## S. Design system
- US/AC/case tokens from V5.
- Pager and «Vis flere» are shared components.
- No new palette.

## T–U. Context and navigation
- V6 sidebar and V8 scope are unchanged.
- Journey D: the handoff to scenario B never shows A's case; a module change clears the specification; an explicit choice is restored.

## V. Performance
- No new measurements in V9, apart from the earlier ones (Testplaner about 2.6–3.2 s; compact discovery 75 %).
- The journey's translate-all took 6–12 s in the background; it blocks nothing, and conflicts now retry.

## W. Responsive and accessibility
- Finn og forstå at 1920 / 1440 / 1024 / 390 px: no horizontal overflow (browser E).
- Pager and ShowMore: aria-current and aria-live.
- Chips are buttons with «Fjern filter: …».

## X. Automated tests
- **Frontend:**
  - full suite 9,872 / 9,872 (after the Finn og forstå and pager changes);
  - plus `PlanStaleContractTests` (3) and X05, passing in their classes.
- **Backend:**
  - `SourceDrivenPlanningTests` 26/26, including the concurrent-write test;
  - discovery classes 45 passed, 1 skipped.
- **Backend full suite: NOT green.** See the stability report.

## Y–Z. Browser journeys
Isolated v1env28/v1env29, M2LB plus a seeded plan; script `scratchpad/v1/v1_v9_journeys.py`.

### First run: 15/18
It found defect 1 (the 500) and a script fault (it created the case under the wrong scenario).

### Second run (with fixes 1 and 3): 33/35

| Journey | Result |
|---|---|
| A — no case → case → plan → Passed → Testoversikt | PASS (17 checks). Search, detail panel order and folding, the handoff to the right scenario, no automatic case, added to the plan, **the new case opens in the editor**, the step saved and reopened through `?scenario=`, the occurrence recorded Passed, the result on Testoversikt. |
| B — scenario only | PASS: no phantom case, recorded Blocked. The check «no case shown» FAILED because the script closed the open row (script fault, fixed). |
| C — copy | PASS: the copy is created, starts NotRun, the original keeps Passed. |
| D — context | PASS: B never shows A; a module change clears the specification; the explicit choice is restored. |
| E — search and batches | PASS: «Viser 40 av 566», «Vis flere» keeps the selection, search finds a row beyond the batch, the filter chip, four viewports without overflow. |
| Console | FAIL: two 409s from the automatic translate-all, which led to defect 2 (fixed in `956ce3d7`). |

Screenshots: `scratchpad/v1/v1env29/shots/v9-*.png` (finn-detail, finn-filters, finn-1920x1080 … 390x844, planlegg-editor, planlegg-readiness, testplaner-execution, testoversikt).

### Final rerun
See the section appended below.

## AA. Changed files
- `Pages/ManualTestingDiscover.razor`
- `Pages/ManualTestingPlan.razor`
- `Services/TestPlanning/TestPlanSuggestionService.cs` (backend)
- plus the pagination V2 files
- tests: `DiscoverScopeAndActionTests`, `DiscoverWorkspaceTests`, `ManualTestingPlanIntentTests` (X05), `PlanStaleContractTests`, `SourceDrivenPlanningTests`

## AB. Remaining issues
1. The backend full suite is not green (the `CI_DEF_3` timeout).
2. «Scenarioet har ingen kilde å lagre klargjøringen i» for some scenarios (not investigated).
3. The three-column Finn og forstå layout is not implemented.
4. Server search does not apply the page's filters (priority/type) to scenarios beyond the window.

## AC. Acceptance (brief §37)
| | Criterion | Status |
|---|---|---|
| A | Discovery fast and understandable | PARTIAL (measured earlier; the V9 layout is verified functionally) |
| B | Search beyond the initial batch | VERIFIED (E; 12,000: pagination V2 report) |
| C | Source context correct | VERIFIED (A, D) |
| D | Case preparation practical | VERIFIED (A, after fix 3) |
| E | Test cases optional | VERIFIED (B) |
| F | Readiness and approval separate | VERIFIED (V5 tests) |
| G | Scenario-only plan items | VERIFIED (B) |
| H | Execution per occurrence | VERIFIED (A, B, C) |
| I | Copied plans do not inherit results | VERIFIED (C) |
| J | Dashboard reflects execution | VERIFIED (A) |
| K | Never the wrong case | VERIFIED (A, D) |
| L | Explicit selections persist | VERIFIED (D) |
| M | Large lists do not block | PARTIAL (2,015 verified; 12,000 see V2) |
| N | No history corrupted | VERIFIED (C; the 409 fix prevents overwrites) |
| O | Both suites green | **NOT MET**: the backend full suite is not green |

## Final rerun (isolated v1env30, all fixes up to `956ce3d7`)
**Main run, 21/23:** journeys A (up to the step check), D and E pass.
- **A-step-persisted:** a script fault. The automatic translation now succeeds, so the editor opens in «Norsk», where the step field has another test id. Re-checked in «Original» view: PASS.
- **Console 409s:** the automatic translate-all now **retries and succeeds** (API log: 409 → 409 → **200**, then the tester's save 200). The two console lines are the browser logging those handled responses; nothing is lost and no error is shown.

**Follow-up on the same instance** (`v1_v9_followup.py`):

| Check | Result |
|---|---|
| A-step-persisted (Original view) | PASS |
| A occurrence recorded Passed | PASS |
| A result on Testoversikt | PASS |
| C copy created | PASS |
| C copy starts NotRun | PASS |
| C original keeps Passed | PASS |
| Console | no errors |

**Defect 4, found by journey B in this rerun and FIXED (`6ba944a6`):**
- After journey D switched module, Finn og forstå for the first module said «Ingen anbefalinger: oppdagelsen er gjort på en eldre kildeversjon enn den gjeldende» and recommended nothing, although no source had changed.
- **Cause:** the work queue counted any newer discovery of the application as newer source. Discoveries are made per selection.
- **Now:** a discovery is outdated only by other source versions (repository and snapshot) or by a newer result for the same selection.
- Backend test; it fails without the fix.
- Journey B's scenario-only flow was verified in the second run (no phantom case, Blocked recorded). It was not re-run in a browser after this fix.

### Summary of defects found by the V9 browser journeys (all fixed, each with a test that fails without the fix)
1. 500 on concurrent plan writes → 409 conflict (`7b1eee79`)
2. New case hidden behind «Klargjør» → opens in the editor (`7b1eee79`)
3. Automatic translation gave up after a conflict → retries (`956ce3d7`; browser: 409 → 409 → 200)
4. False «eldre kildeversjon» after a module switch → per-selection currency (`6ba944a6`)
