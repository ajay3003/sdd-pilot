# BirkNext — implementation gap detection — gap register (2026-11-09)

Branch `008-traceability-first` (local only). Initial SHA `a541e20e`; final SHA = the commit carrying this file.
Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED.

**Overall: PASS for the brief's definition of done on the supplied archive, PARTIAL for coverage beyond it.**

| # | Definition of done | Status | Evidence |
|---|---|---|---|
| 1 | What Skolenærvær specifies for Bufdata Parquet delivery | PASS | contract `bufdata-leveranse.md` linked to the feature; requirement ids from contract + tasks |
| 2 | Which implementation tasks are planned | PASS | T119–T122 (+ T105 tests) with exact lines, unchecked |
| 3 | Whether ADR-028 approval evidence exists | PASS | PENDING: required by `tasks.md:38`, absent from the register (last ADR-027) |
| 4 | Which expected source artifacts exist in the archive | PASS | none; `Skole/src`, `Skole/tests` absent |
| 5 | Whether equivalent implementation exists elsewhere | PASS | archive-wide type/package/file search: none; unrelated Blob code not attributed |
| 6 | Whether relevant tests are present | PASS | T105 test file absent: Test not found |
| 7 | Whether runtime execution is verified | PASS | Not assessed (no runtime evidence) — never a runtime PASS |
| 8 | What is missing, blocked or unassessed | PASS | Blocked by ADR-028; implementation not evidenced in supplied archive; tests absent; runtime not assessed |
| 9 | Which requirements are affected | PASS | feature requirement ids (contract + tasks) |
| 10 | Whether delivery readiness is justified | PASS | No — Blocked, with explanation |

| Capability / limitation | Status | Note |
|---|---|---|
| Evaluation at Project Import, persisted inputs + report, re-evaluation, review | PASS | acceptance GA-02, GA-23, GA-30 |
| Positive control reaches VERIFIED only with recorded executions | PASS | synthetic executions, labelled synthetic |
| Re-evaluation with TRX executions from the workspace in the browser | **NOT RUN** | the browser positive control stops at "implemented, not verified"; Verified was shown through the API with synthetic executions |
| Clause-level contract behaviour (determinism, manifest written last, idempotent upload, prohibited identifiers) | NOT SUPPORTED (static) | requires implementation or runtime evidence; reported as not evidenced, clause-level NOT ASSESSED |
| Runtime verification input | PARTIAL | the engine accepts runtime observations; no producer feeds them yet |
| Task ↔ test association | PARTIAL | explicit task-id references only ("after T119", "covers T022–T023"); implicit association not inferred by design |
| In-progress task state | PARTIAL | `[~]`, `[/]`, `[-]` recognised; Spec-Kit itself only uses `[ ]`/`[x]` |
| Features | PARTIAL | decision-gated features and user stories; no explicit feature catalogue |
| Non-decision gates (dependencies) | PASS | UNKNOWN unless a resolution is recorded; they block their tasks but form no feature |
| Findings across the whole monorepo | PARTIAL | 600+ findings on M2LB outside Skole (path drift, completed tasks without the named file, …) are produced by the same rules but were sampled, not reconciled one by one |
| Re-evaluation when source text is needed again | NOT SUPPORTED | source text is not retained after analysis; a changed tasks.md needs a new import |
| Real Azure / external systems | NOT RUN | none contacted (not required) |

## Defects (all fixed with regression tests)

GAP-DEF-1 … GAP-DEF-12 (engine, on real data — see the audit) and UI-1 … UI-3 (acceptance). GAP-DEF-1–5 were seen on the real archive before the fix
and their synthetic regression test was written with the fix; the others were run red first or found by the oracle reconciliation.

## Regression

Clean tree = `a541e20e` + exactly the files of this commit; sequential partitions under the memory guard.

| Suite | Result |
|---|---|
| Backend partitions 1–4, 6 | 1031/0/2, 1098/0/5, 830/0/0, 652/0/1, 2178/0/8 (passed/failed/skipped) — partition 6 without timeouts |
| Backend partition 5 | first run 519/**1**/3: `ActiveEventObservationTests.Architecture_OneSendPath_…` — a real regression of this change (the extractor's framework-type list contained the text `EventHubProducerClient`, which the one-producer architecture guard greps for). Name removed; partition 5 rerun **520/0/3** |
| Backend total | **6309 passed, 0 failed, 19 skipped** |
| Frontend | 8520 passed, **1 failed**: `SpecExplorerPanelKeyboardTests.ClickingTreeItem_UpdatesTabindex` rendered `tabindex="−1"` (U+2212). Pre-existing race: `ContractTestingPageTests` / `ExecutionResultsPageTests` set the process-wide `DefaultThreadCurrentCulture` to nb-NO with U+2212 while other classes render an int tabindex (known GAP-EE-4, components with int tabindex). Not touched by this change; the class reruns **12/12** in isolation. Not relaxed. |
| Gap tests | backend `ImplementationGapTests` 42/42; frontend `ImplementationGapExplorerTests` 6/6 + Task Explorer deep link |

## Scratch resources (not committed)

Session scratch `gap/` (oracle, controls, harness, results, screenshots), `wtg/`, `webpub-gap/`, DB `birknext_gap_20261109`. Deletion awaits approval.
