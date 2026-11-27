# BirkNext V12 — browser UX verification (2026-11-27)

**Isolation:**
- ports 5097/5197, disposable database, own App_Data;
- M2LB imported, 20 plans seeded.

The normal TesterNative data, the repository App_Data and the reset backups were not touched.

**Script:** `v1_v12.py`.
- Mode `before`: observes and screenshots only.
- Mode `after`: also asserts the V12 behaviour.

## Checks (mode `after`)
| Check | Meaning |
|---|---|
| FF-row-click-opens-details | Finn og forstå at 1,280 px (two columns): a row click opens the details (MT-1) |
| FF-one-primary-action | Exactly one visible `.btn-primary` in the detail panel (MT-2) |
| FF-close-returns-focus-to-row | After «Lukk», focus is on the scenario's row (MT-13) |
| FF-enter-opens-details | Enter on the list opens the active scenario (MT-13) |
| FF-selected-state-visible | A row selected for a bulk action has a dashed outline (MT-7) |
| PLAN-create-hides-editor | «Nytt testtilfelle» hides the editor (MT-8) |
| EXEC-progress-wording | «x av y behandlet», no «Gjennomført: %» (MT-10) |
| EXEC-hierarchy-visible | Test groups with counts; a case tag per scenario (MT-4) |
| RESP-{discover, plan, plan-execution}-{1920, 1440, 1280, 1024, 390} | No horizontal page overflow |
| DATA-unchanged | Plans and cases before = after; no plan writes |
| Z-console | No console errors |

Followed by the V10 dashboard and regression journeys on the same instance, to show that V12 broke nothing.

## Results
**PENDING:** the run had not finished when V12 was committed (the user asked not to wait).
- «Before» screenshots: `v1env38/shots/v12-before-*.png` (build `9af21c93`, without V12).
- «After» screenshots: `v1env39/shots/v12-after-*.png`.

The results are recorded here when the run ends.

## Accessibility findings (from the audit; status after V12)
| Finding | Status |
|---|---|
| Rows: Enter opens; focus returns after closing | IMPLEMENTED |
| Search-clear «×» as a real button (3 explorers) | IMPLEMENTED |
| Selected state not by weight or colour alone | IMPLEMENTED |
| Detail panel focus trap; interactive controls inside `role=option` | NOT IMPLEMENTED |
| About 8 modal families without a focus trap (outside Manual Testing) | NOT IMPLEMENTED |
