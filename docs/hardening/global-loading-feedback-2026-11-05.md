# Global loading and progress feedback — NEXT45 (2026-11-05)

Branch `008-traceability-first`. Audit of every routed page (`@page`) and the shared components they host, then one loading system applied application-wide.

## Root causes

1. **Project Quality Dashboard showed results before they existed.** The KPIs, "Needs attention" and "Review availability" are derived from workflow readiness, which loads after the workspace. While it loaded they rendered from a null readiness: KPIs read "Not assessed" and "Needs attention" read **"Nothing needs attention right now"** — a result, not a loading state.
2. **Stuck indicator.** If readiness failed, the exception was swallowed and "Working out the recommended next step…" stayed on screen forever, with no error and no retry.
3. **Stale overwrite.** `CurrentWorkspace.Changed` started a new refresh without invalidating the running one; a slower, older refresh (project switched meanwhile) could replace the newer results.
4. **Loading looked like "none".** Recent activity showed "No activity yet" while the activity request was still running.
5. **No shared spinner in practice.** The shared `InlineLoading` / `LoadingSkeleton` / `OperationProgress` existed (NEXT34) but 45 places still used their own text (`<p class="loading-state">Loading reviews...</p>`, `⟳`, plain "Loading…"), mostly with no spinner, no delayed reveal and inconsistent `role="status"`. Plan Explorer had its own spinner (`axh-spinner`), different from the shared one. That is why Plan Explorer "had a spinner" and the Dashboard did not.
6. **Action buttons** (Test Coverage, Code Risk, Document Quality, Pipeline probe/compare, ADO load) changed text but had no spinner or `aria-busy`.

## Shared loading system

| Piece | Status | Purpose |
|---|---|---|
| `LoadingThresholds`, `DelayedPresentation` | reused | 300 ms / 2 s / 10 s wall-clock reveal (no flash on fast loads) |
| `InlineLoading` | reused, now everywhere | section/page loading: spinner + contextual text, `role=status`, delayed |
| `LoadingSkeleton` | reused | shaped placeholders (Dashboard KPIs, attention, availability) |
| `OperationProgress`, `PersistentOperationStatus` | reused | real multi-step / background progress (never a guessed %) |
| `LoadSequence` / `LoadTicket` | **new** | stale-response protection: each load takes a ticket; a newer load cancels the older token; only the current ticket may apply results or end the indicator; unmount makes all tickets stale |
| `LoadError` | **new** | load failure as an alert with a safe message and Retry (Retry disabled while it runs); distinct from empty / unavailable |
| `BusyButton` | **new** | async action: disabled while running (no duplicate submissions), spinner + busy text, `aria-busy`, released on success/failure |
| `.loading-spinner` | **changed** | now the Plan Explorer design (neutral track, primary arc) — one spinner for the whole app; Plan Explorer uses it too (`axh-spinner` removed); reduced motion respected |

## Modules audited

All routed pages from NavigationCatalog sections — Workspace, Specs & Planning, Traceability & Impact, Source & Architecture, Quality & Testing — plus Admin/System Settings, and the shared panels they host (Test Data, Integrations, Topology, Security, Source domains, Project Import, Target Environment panes, Explorer host).

Already correct and kept: Explorer host (`ArtifactExplorerHost`, versioned loads), Relationship Review / API Quality Review (`ReviewAssessment` generations + `OperationProgress`), Source Analysis, Release Readiness, Delivery pages, Performance Test Review and Pipeline Review (shaped skeletons), FQR/IQR (`OperationProgress`), long operations via `BackgroundOperationService`.

## Changes

- **Dashboard:** readiness-derived sections show skeletons while loading (never "Not assessed"/"Nothing needs attention"); next step has `aria-busy`; failure → `LoadError` "Could not work out the review status" with Retry; "Needs attention" says "Not known" on failure; an earlier readiness stays visible if a background refresh fails; workspace refresh, activity and readiness are guarded by `LoadSequence`; activity shows loading instead of "none"; tickets disposed on unmount.
- **45 ad-hoc loading texts → `InlineLoading`** in 42 files (pages: System Settings ×2, API Quality Review, Azure Environment Analysis, Code Traceability, Compare Reviews, Compare Review Detail, Critical E2E, Dashboard, Delivery Reporting, Generated Documentation Explorer, Integration Quality Review, Schema Studio, Traceability Suggestions; components: Active Event Testing, Data Reset, Frontend Analysis Settings, FQR browser evidence, FQR engine settings, Functional Quality Scope, Integration Journeys, Integrations pane, Message Flow, Message Verification, Performance Scenario Builder, Performance engine status, Artifact candidates, Runtime authentication, Security projection, Setup Library, Source Change Impact, Spec Changes, Target Environment project pane, Test Data asset/candidates ×2/readiness/tester readiness, Topology connections, Unified Impact, Workspace documents gate, Workspace manager, Delivery summary, Source contract evidence ×2). Classes and `data-testid`s preserved.
- **Skolenærvær Testing:** "Loading readiness…" vs "Readiness is not available" and "Loading rule results…" vs the rules error are now distinct states (an error is `role=alert`, not a loading text).
- **Action buttons:** Test Coverage, Code Risk, Document Quality, Pipeline probe and compare, ADO "Load from Azure DevOps", and FQR readiness-pending: shared spinner + `aria-busy`; all were already disabled while running.
- **Source Management:** the initial-load "No source packages yet" (shown while sources were still loading) is fixed in the NEXT46 redesign.

## Tests

- New `GlobalLoadingFeedbackTests` (10):
  - LoadSequence: stale ticket and cancellation; only the newest load ends loading; unmount.
  - BusyButton: duplicate clicks and busy state; released after failure.
  - LoadError: alert and single-submission retry.
  - InlineLoading: delayed `role=status`.
  - Dashboard: loading instead of default results while readiness is pending.
  - Dashboard: a readiness failure shows an error and Retry, not a stuck indicator, and Retry recovers.
- Frontend full suite: **8213 passed, 0 failed**.

## Browser verification (M2LB copy, `birknext_gaps`, nb-NO, Edge, localhost only) — 9/9

1. The Dashboard settles with KPIs and no visible indicator remaining.
2. The spinner computes identically on the Dashboard and in Plan Explorer.
3. With every API call delayed 2.5 s, loading is shown.
4. Under that delay, "Nothing needs attention" never appears before readiness is known.
5. Under that delay, every indicator is removed after success.
6. Rapid navigation across seven pages ends on a settled Dashboard.
7. The Code Risk run button is disabled, shows the spinner and `aria-busy` while running.
8. The button is released after the run.
9. No page errors and no external host.

## Limitations

- Pages that already guarded their loads (generation counters / `ReviewAssessment`) were not migrated to `LoadSequence`; the new type is for new code and the Dashboard. Same semantics.
- Cancellation tokens are passed only where the API service accepts one; elsewhere an obsolete response is ignored, not aborted.
- Page-specific shaped skeletons (Pipeline Review, Performance Test Review) were kept; they are equivalent to `LoadingSkeleton`.
