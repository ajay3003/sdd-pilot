# BirkNext: Reset Everything dialog UX fix (2026-11-27)

## Problem and confirmed cause

In System Settings → Maintenance → Reset data, choosing Reset everything displays the backend reset plan, including long removed and preserved lists, consequences, confirmation and actions.

The dialog already used the shared `AccessibleDialog`, which supplies the modal role, Escape handling, focus trap and focus return. The layout defect was in `DataResetPanel.razor.css`: `.drp-modal` applied `overflow-y: auto` to the whole panel. The panel used a grid with every child in the same scrolling flow, so the long plan pushed Cancel and Reset below the visible area and the user had to scroll the entire dialog to reach them. Its `max-height` used `100vh` but did not use border-box sizing, so panel padding was outside the height limit. The lists were rendered fully open, making this more likely.

## Changes

- Kept the existing shared `AccessibleDialog`; added optional `DescribedBy` support and associated the concise reset warning with the dialog using `aria-describedby`.
- Restructured Reset Data as a fixed header, an independently scrollable content region and a fixed footer. The panel uses a border-box dynamic viewport height limit with a `100vh` fallback and a single-column footer on very narrow screens.
- Added keyboard-operable disclosure sections for data removed, data preserved and important consequences. Full backend plan contents remain available. Counts, notes, manual-test-plan warnings and confirmation remain in the dialog.
- Made the warning visible before scope selection. The selected full-reset title/action are “Tilbakestill alt”; cancel is “Avbryt”.
- Kept the existing typed `RESET EVERYTHING` confirmation, opt-in/manual-plan behavior, busy-state close prevention and API request path unchanged. No backend files or reset policy changed.

## Files changed

- `AIAssisted/frontend/BirkNext.Web/Components/DataResetPanel.razor`
- `AIAssisted/frontend/BirkNext.Web/Components/DataResetPanel.razor.css`
- `AIAssisted/frontend/BirkNext.Web/Shared/Components/Dialog/AccessibleDialog.razor`
- `AIAssisted/frontend/BirkNext.Web.Tests/Components/DataResetPanelTests.cs`
- `AIAssisted/frontend/BirkNext.Web.PlaywrightTests/Tests/SystemSettingsPlaywrightTests_PreStarted.cs`

## Accessibility and safety

The visible heading labels the modal. The warning is associated as its description. The shared modal continues to trap focus, restore it to the Reset data trigger, close with Escape when not busy, and ignore Escape/backdrop close while a reset is in progress. Native `<details>/<summary>` controls expose the long lists to keyboard and assistive technology. Cancel and the destructive action remain ordinary `type="button"` controls; opening, scrolling, expanding and cancelling cannot trigger the reset. No destructive request was issued during this work.

## Test results

- `dotnet test AIAssisted/frontend/BirkNext.Web.Tests/BirkNext.Web.Tests.csproj --filter "FullyQualifiedName~DataResetPanelTests|FullyQualifiedName~AccessibleDialogTests" --no-restore`: **26 passed** (20 Reset Data tests and 6 shared dialog tests).
- A new component test supplies 80 removed items and 60 preserved items, checks the summary and complete list rendering, verifies the dialog description and footer controls, and confirms cancel sends no reset API request.
- The Playwright regression test was added for 1920×1080, 1366×768, 1280×720, 1024×600, 390×844 and reduced CSS viewports approximating 125%, 150% and 200% zoom. It opens the live settings UI, reads the reset plan, expands both lists, measures panel/content/footer bounds, cancels with Escape and watches for reset POST requests.
- Playwright execution was **skipped**, not passed: the runner requires configured isolated BirkNext frontend/backend URLs or its opt-in disposable database setup. No isolated browser environment was configured. Although a development frontend answered on port 5173, it was not used because its database isolation could not be established. Therefore actual browser/zoom verification remains outstanding.
- No backend reset semantics or stored data were changed.

## Browser evidence and remaining limitations

No before/after screenshot was captured. Browser acceptance, actual 200% browser zoom, screen-reader behavior and visual regression checks remain unverified until the Playwright test is run against an isolated instance. The dialog layout and keyboard disclosure behavior are covered at component level; the shared `AccessibleDialog` already has its own focus behavior tests.

## Working tree at report time

Branch: `008-traceability-first`; HEAD: `ed472b7a732a3e214dd7283b3c992dccb26d81c1`.

The starting working tree already contained unrelated modifications in `AIAssisted/backend/BirkNext.Api.Tests/Diagnostics/TestHostMemoryDiagnostics.cs` and an untracked `AIAssisted/docs/birknext-v12-final-verification-2026-11-27.md`; those were preserved. A separate `v12-ux-bundle` worktree was present and clean when inspected. The OpenSpec worktree was left untouched. Commit `1ac27177` contains only the six files listed above; nothing was pushed. After commit, the two unrelated starting changes remain unstaged/untracked.
