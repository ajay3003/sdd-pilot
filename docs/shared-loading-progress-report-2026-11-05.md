# Shared Loading, Progress & Operation Lifecycle: report (2026-11-05)

Branch `008-traceability-first`, not pushed.

## 1. Audit and root causes

**What already existed and is kept:**

| Foundation | Role |
|---|---|
| `BackgroundOperationService` + Activity Center | Persistent operations (Project Import upload and import, Delivery Report generation and export, Azure DevOps import): Queued/Preparing/Running/Cancelling/Completed/Failed/Cancelled, stable ids, idempotency keys, a measured fraction only when a total is known, cancel and retry where allowed, survival across navigation. |
| `ReviewAssessment` + `ReviewStageList` | Real staged progress with a generation number (stale responses are discarded) and cancellation, used by API Quality Review, Source Analysis, Correlation Review, the Snapshot Registry, Integration Quality Review and the canonical correlation panel. |

**What was wrong:**

1. **API Quality Review "Assessing…" after every visible step was done (root cause).** After the last visible step, the assessment awaited two more loads (contract bindings, authorization-check inputs) that no stage represented. Every step read `done`, "Calculating readiness" stayed `pending`, and the heading kept spinning. On the failure path, the remaining steps stayed `pending` forever.
2. **No delayed presentation anywhere.** About 60 `Loading…` texts and 10 spinner styles (`aqr-spinner`, `sa-loading-spinner`, `bd-spinner`, …) were shown instantly, so every fast load flashed.
3. **Duplicate "slow" timers per page.** AQR and Source Analysis each had their own 4-second "Still…" timer and flag.
4. **Delivery Reports' running generation** was one line of text, with neither the operation's stage nor its state.
5. **A CSS-only delay is not reliable (found in the real browser during this task).** The first implementation delayed the reveal with a CSS animation. While WebAssembly keeps the main thread busy, the browser's animation clock lags: a probe element became visible 82 ms after insertion under load. The reveal is now a wall-clock timer (see §2).

## 2. Shared components and services (`Shared/Components/Loading`)

| Piece | What it does |
|---|---|
| `LoadingThresholds` | The one place for thresholds: **Inline 300 ms** (below it nothing is visible), **Detail 2 s** (the full step list), **Slow 10 s** ("Still working — waiting for …"). Configurable. |
| `DelayedPresentation` | Wall-clock phases measured from first render (`Task.Delay`). Disposed with its component, so removing the element (operation done) stops it, and nothing can be left stuck. Completion is never delayed. |
| `InlineLoading` | One spinner and one muted style. `role=status`, `aria-live=polite`. Present in the DOM at once (state and tests), visible from 300 ms. |
| `LoadingSkeleton` | Placeholder lines with a screen-reader status, delayed like `InlineLoading`. |
| `OperationProgress` | Multi-step progress from the operation's own `ReviewStage` list (no percentage): compact "Step n of m · current step" from 300 ms, every step from 2 s, "Still working — waiting for: <running step>" from 10 s. A failure shows its steps at once, with Retry. Optional child content (a page's skeleton). |
| `PersistentOperationStatus` | The initiating page's view of a `BackgroundOperationService` operation (the same one the Activity Center lists): state, stage, a measured progress bar only when a total is reported (never for "stages"), Cancel only where allowed, and the failure message. |
| `ReviewAssessment.Finish()` | Ends a step list: Pending becomes Skipped ("not needed"); Running becomes Skipped ("stopped"). After it, nothing reads Running. |
| CSS (`components.css`) | `.loading-delayed` (hidden) and `.loading-revealed` (visible), the spinner, skeleton, progress and persistent-status styles, and reduced-motion handling. |

## 3. Modules migrated

| Module | Presentation |
|---|---|
| API Quality Review | `OperationProgress` (8 real stages, including the two formerly hidden loads); `Finish()`; shared spinner on Run; own slow timer removed |
| Source Analysis | `OperationProgress` with its card skeleton as child content; own slow timer removed; detail fallback uses `InlineLoading` |
| Correlation Review | Interpretation run uses `OperationProgress`; project and queue loads use `InlineLoading` |
| Snapshot Registry | `OperationProgress` |
| Canonical correlation panel (Requirements, IQR, …) | `OperationProgress` |
| Delivery Reports | Generation uses `PersistentOperationStatus` (continues in Activity) |
| Delivery Overview, Release Readiness, Dashboard, Requirements Traceability, Technology Coverage, Test Data, Integration Quality Review (4), Recommended Workflow, Traceability, Task Alignment, M01, Application Messaging, Implementation Review, Azure DevOps import, Delivery / Data Model evidence panels, Canonical trace panel, Altinn setup, Architecture workspace, Browser authentication | `InlineLoading`: 27 occurrences migrated mechanically (plain `<p role="status">Loading…</p>`) |
| Project Import | Already conforming: real stage n of N, measured upload bytes, indeterminate bar for analysis, session kept across navigation. Not changed. |
| Target Environments → Target Application | The check button shows "Checking…" and ends with the result (verified); not changed. |
| Schema Studio, Performance Review, Critical E2E | Their own result and progress UIs were not migrated in this pass (see §7). |

## 4. Lifecycle corrections

- **Every awaited assessment load is a visible stage**, so "Assessing" can no longer outlast its steps.
- **Steps finish with the operation:** `Finish()` runs in AQR's `finally`; failure and early outcomes read "skipped · not needed".
- **No fabricated percentages:** `OperationProgress` has none, and `PersistentOperationStatus` shows a bar only for a measured total.
- **Persistent operations are unchanged and reused:** completion is reported only after the backend answered (verified: a 503 made the operation Failed, never Completed).

## 5. Tests

**Unit and component** (`SharedLoadingTests`, new):

| Area | What is tested |
|---|---|
| Thresholds | Values; CSS contains hidden-until-revealed and no animation-delay reveal. |
| `DelayedPresentation` | Phases advance by real time; a disposed one never reveals. |
| `InlineLoading` | Status semantics; hidden below the threshold, revealed after it; span variant. |
| `LoadingSkeleton` | Its line shape and screen-reader text. |
| `OperationProgress` | Compact current step; delayed list; no "still working" early; reveal and detail phases; no percentage; failure shown at once with Retry. |
| `Finish()` | After an early outcome and after cancellation, nothing is Pending or Running. |
| `PersistentOperationStatus` | Measured bar only; none for "stages" or an unknown total; Cancel only if allowed; failure; nothing rendered without an operation. |

**AQR** (`ApiQualityReviewReadinessLoadingTests`):

- the 8 stages, in order, including contracts and authorization;
- the panel is delayed;
- "Step 2 of 8 · Loading API evidence";
- after a failure, no Pending or Running step.

| Run | Result |
|---|---|
| Full frontend suite (`dotnet test frontend/BirkNext.Web.Tests`) | **8122 passed, 0 failed** |

## 6. Real browser

- **Script:** `scratchpad/load-e2e.cjs`.
- **Setup:** Edge against the published build on the M2LB copy (DB `birknext_gaps`, API :5099, web :5199). Slowness was produced by delaying real backend responses in the browser route. Delivery Reports generation was answered 503 after 3 s, so the throwaway DB got no new report.
- **Result: 23/23 checks passed.**

| # | Scenario | Result |
|---|---|---|
| 1 | API Quality Review readiness (evidence held 11 s) | Panel shown **300 ms** after insertion (a conservative real-time lower bound), with "Step 2 of 8 · Loading API evidence"; full list **2038 ms**; "Still working — waiting for: Loading API evidence" **10015 ms**; on completion the progress is gone, nothing Running, decision shown. |
| 3 | Source Analysis (detail held 2.5 s) | "Loading analysis of M2LB.zip…" with step and skeleton; removed on load; nothing Running. |
| 4 | Delivery Reports generation | Shared persistent status (Running, stage, "continues if you leave"); no fabricated percentage; the same operation Running in Activity; after the backend answered, the status is gone and the operation reads **Failed**, not stuck or Completed. |
| 5 | Delivery Overview, Correlation Review, Test Data, Release Readiness, Snapshots, Requirements Traceability, Technology Coverage at normal speed | No loading element shown within 300 ms of insertion; none stuck. |
| 6 | Target detection | "Checking…" while the request runs, gone with the result. |

No external hosts were contacted and there were no page errors.

During this run the CSS-only reveal measured **82 ms** under load, which is why it was replaced with the wall-clock reveal; that is now verified at 300 ms.

## 7. Gaps

1. **Refresh does not restore running operations.** `BackgroundOperationService` operations run in the browser tab (WebAssembly). A page refresh ends the work itself, so restoring its status would be fabricated. Server-side job persistence and resume are not implemented. Project Import keeps its staged archive on the server, so its retry needs no new upload; that is unchanged.
2. **Not migrated.** Loading texts that contain expressions (e.g. `@(_error ?? "Loading …")`) and per-page bespoke progress (Schema Studio, Performance Test Review, Critical E2E, Browser Discovery, System Settings tables) still use their own markup. They are candidates for the same components.
3. **Page-scoped styles.** Page classes passed to `InlineLoading` (e.g. `iqr-muted`) do not reach the shared component through CSS isolation. The shared muted style applies instead, which is consistent, but a page's own card frame around a loading line is not kept.
4. **One real long-running server job.** Only Delivery Reports generation was exercised in the browser, with a controlled 503; a real report was not generated, on purpose. Performance and E2E executions were not run (no live target).
