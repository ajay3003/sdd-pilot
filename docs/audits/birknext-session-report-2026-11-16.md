# BirkNext session report (2026-11-16)

**Branch:** `008-traceability-first`. Local commits only; **nothing was pushed**.

**Start and end:** started at `02a6c0bd`; task 5 code is `219092e0`. This report and the task 5 document are committed on top of it.

**Environment:** all verification ran on isolated ports 5097/5197 against disposable PostgreSQL databases copied from `birknext_pts_acc_20261115`. The user's servers on 5000/5173 were never contacted. No external system was contacted: no TT02, M2LB, Altinn or Azure DevOps.

## Tasks and commits

| # | Task | Commit(s) | Detail document |
|---|---|---|---|
| 1 | Manual Testing completeness: defect follow-up after completion, file attachments, plan version history and restore, ADO Description column and remembered IDs, suite → journey reference, defect CSV | `9b02825a` | `birknext-manual-testing-completeness-2026-11-16.md` |
| 1b | "Open now" did nothing in Plan & prepare. Root cause: it re-selected the current area. Fixed by reveal + focus with an instant scroll. | `983ca27a` | (commit message) |
| 2 | Journey audit (Manual, Technical, Integration): saved project selection, project ↔ environment rule in Technical Testing, older API runs no longer open the latest report, own-snapshot preselection, superseded suite runs, suite result report, run-start preparation panel | `420916bb` | `birknext-tester-journey-audit-2026-11-16.md` |
| 3 | UI modernization of the testing workspaces: shorter sidebar, compact candidate list, area grid, calm notices | `d4611e52`, `ed6cd9af` | `birknext-testing-workspace-ui-modernization-2026-11-16.md` |
| 4 | Recommended Workflow / All modules: kept as the review-decision workspace (the only place decisions are recorded); module inventories folded; All modules area jump | `fe9fc555` | `birknext-recommended-workflow-all-modules-decision-2026-11-16.md` |
| 5 | Project isolation (server-side), requirement ↔ journey ↔ suite traceability, stored API review history, technical finding follow-up and retest, project-restore message, test isolation | `219092e0` | `birknext-project-isolation-traceability-technical-lifecycle-2026-11-16.md` |

## Verification of the final code

Final run on the task 5 code, which includes every earlier task. Each task's own document holds its own run.

| Check | Result |
|---|---|
| Frontend unit and component tests | 9204 / 9204 |
| Backend partitions 1–6 | 6885 passed, 0 failed, 19 skipped (6904) |
| Release publish (API and web) | 0 errors |
| Browser acceptance: 21 scripts on 3 fresh disposable databases (Edge, guarded network) | All passed, 687 checks; 0 page errors, 0 server errors, 0 guard violations |
| Repository Playwright real-project category | Skipped (1): the M2LB archive is not configured |

**p2 order dependence.** p2 initially scored 5/8 because it depended on an earlier step's browser session. Its report step now runs in its own session and was rerun at 10/10. This was a test-isolation fix; no product code changed.

## Bugs found by verification (not by reading code) and fixed

1. **Multi-tab merge.** The frontend treated this tab's own content as another tab's (settings, in the earlier consolidation).
2. **"Open now".** It did nothing in Plan & prepare; bootstrap's smooth scroll aimed at a stale position.
3. **Lost project selection.** It was lost when the tab closed within about 3 s, because the save was fire-and-forget.
4. **Hidden All modules link.** It disappeared once sidebar groups started closed.
5. **Keyboard order.** The All modules jump row broke Tab from search to the results.
6. **Follow-up status changes.** They were all refused (HTTP 400): the client sent text enums, and the API accepts numbers.
7. **Windows file-replace lock.** The Critical E2E suggestion store failed under an antivirus hold; it now retries briefly, like the Test Data store.

## Still open (summary; details in each document)

- **Server-side specification reading.** Requirement links (manual and integration) are checked against the browser-read specification catalog and labelled with that trust level. Independent verification needs a server-side reading of the specification.
- **Suite execution.** Executing an integration suite against a target was not exercised in the browser (no external target allowed). Finding retests used synthetic local runs.
- **Browser-only history.** Fuzzing and authorization runs and Frontend Quality Review history are still kept only in the browser.
- **Composite runs.** They don't pin test revisions, and a single stage can't be retested.
- **Playwright suite.** Its fixture-based tests start servers on 5000/5173 and were not run. The real-project category needs an external M2LB archive that isn't configured.
- **No live Azure DevOps import** of the new Description column or remembered IDs.
- **Attachments.** There is no malware scanning or retention policy.
