# BirkNext — Project Quality & Recommended Workflow: acceptance (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `d86bf326`.
- **Systems used:** Microsoft Edge with a Release frontend, a real local API and a disposable database. Every non-localhost request was aborted.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## 1. Automated tests

| Suite | Result |
|---|---|
| Affected frontend tests (Dashboard, Recommended Workflow, cross-page, review decisions, workflow readiness, project inputs, Help Center, documentation guard, Project Quality) | **323/323 PASS** |
| First run after the changes | 6 failures, all assertions of the old semantics or keys, updated deliberately with reasons (see implementation), plus one legacy term ("Delivery Readiness") caught by the documentation guard and fixed in the content |

## 2. Browser acceptance

Data: a synthetic SDD project (`pq-synth.zip`, built for the test: 1 feature with spec, plan and tasks, plus 1 source file) imported through the real Import Project page; then the real `M2LB-language.zip` (read only).

| ID | Check | Result |
|---|---|---|
| PQ-01 | No project: onboarding, no verdict, no zero-as-health | PASS |
| PQ-02 | Import confirmation is small and says importing is not a quality result | PASS |
| PQ-03 | Execution evidence: "No recorded runs" for the active environment `PQ local (synthetic)` | PASS |
| PQ-04 | No "Healthy" verdict on an unassessed project | PASS |
| PQ-05 | Same next action on both pages: key, title, reason, Learn how | PASS |
| PQ-06 | Same project on both pages | PASS |
| PQ-07 | Five stages: tests "No recorded runs", readiness "Not assessed here", nothing recorded as complete | PASS |
| PQ-08 | Keyboard: a stage opens with Enter and explains itself | PASS |
| PQ-09/10 | Learn how opens the matching article; Back returns with the same project | PASS |
| PQ-11 | The next action opens its module; Back keeps the project | PASS |
| PQ-12 | Project Quality's help link opens its own article | PASS |
| PQ-13 | Test Execution & Evidence unavailable (synthetic 503): card says Unavailable, not zero | PASS |
| PQ-14 | Switch to M2LB-language through Import | PARTIAL: imported (source snapshot stored, M2LB application found), but the browser keeps the current project; see PQ-G5 |
| PQ-15/16/17 | No "Healthy" verdict; detailed analysis collapsed; both pages agree | PASS |
| PQ-L1 | No page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 px on both pages | PASS |
| PQ-20 | After an API restart: same project and the same shared next action | PASS |
| PQ-Z | No request left localhost; no page or console error (all phases) | PASS |

**Totals:** 20 PASS, 1 PARTIAL.

### Run history

| Run | Result | Why |
|---|---|---|
| 1 | 1/2, harness | The harness waited for a live "imported" element. The Import page shows the current project only on a fresh visit. |
| 2 | 1/2, harness | Same cause with a different wait; the harness file was also damaged by a scripted edit and was rewritten |
| 3 | 13/14 | The synthetic project passed; the M2LB import did not become current (PQ-G5) |
| visual / after-restart | 5/5, 2/2 | — |

## 3. Visual review

At 1280 px, Recommended Workflow shows:
- the context strip;
- three input cards;
- the next-action card ("Configure Target Application": why, what it produces, unlocks, Learn how);
- the context sidebar.

The generic outcome text seen in that screenshot ("Moves the recommended workflow forward") was replaced by specific outcomes for the Configure Project and Bind Project Evidence steps, with a test.

## 4. Full regression

| Step | Result |
|---|---|
| Backend partitions 1–6 (sequential, memory-guarded) | **6,462 passed, 19 skipped, 0 failed** (1,031 / 1,098 / 840 / 652 / 520 / 2,321) |
| Frontend full suite | **8,612 passed, 0 failed** |
| Release frontend publish | PASS (used for the browser acceptance) |
