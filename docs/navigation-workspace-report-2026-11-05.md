# Navigation & Workspace Experience: report (2026-11-05)

Branch `008-traceability-first`, not pushed.

## A. Audit

**Path traced.** User → Workspace View → Feature Visibility → available modules → groups → sidebar → active route → project context.

| Area | Finding before this change | Classification |
|---|---|---|
| Module registry | One registry: `NavigationCatalog.Sections`, with 8 groups and their rows (route, label, icon, Feature Visibility predicate, aliases, hint). Shared workspaces and the user guide read it. No second registry exists. | Kept |
| Workspace View | `WorkspaceView` with All Modules, Project Leader, Scrum Master, Test Lead and Technical Reviewer. It only re-ordered groups and collapsed groups outside its focus. It is **not** a security role: nothing in BirkNext reads it for access, and no other code reads it at all. | Kept as presentation |
| Presets | Focus was per *group*. No preset named modules, so a role saw whole groups of modules it does not use. "Technical Reviewer" overlapped the proposed Developer view. | Root cause: overcrowding, unclear view |
| Switching view | Navigated to the view's landing page and left the page the person was on. | Defect |
| "More modules" | A divider heading between focus and non-focus groups, which repeated the grouping. | Removed |
| Group expansion | Groups outside the focus started collapsed, so the same group looked different per view and, after a switch, collapsed "by itself". | Root cause: inconsistent expansion |
| Collapse | No desktop collapse. The sidebar was always 264px. | Missing |
| Mobile | Bootstrap toggler. The menu started **open** on phones (`collapseNavMenu = false`), pushed the content down and had no dialog semantics, focus handling, Escape or scroll lock. | Defect |
| Search | Found visible modules only. It did not tell the person whether a module was in the current view, and modules turned off in Feature Visibility were silently absent. No arrow-key movement. | Missing |
| Feature Visibility | One predicate per row (`NavItem.Visible`). Product availability (`FeatureAvailabilityCatalog`) is a separate badge. There is no separate duplicate visibility logic in the sidebar. | Kept; now one resolution model |
| Authorization | BirkNext has no per-module authorization. Routes have no `[Authorize]`, and MSAL is inert until Entra is configured. So "Access denied" never occurs, and the model documents that rather than inventing it. | Clarified |
| Active route | `IsCurrentLocation` handles exact, prefix and query deep links (one row per location). Correct, and kept. | Kept |
| Persistence | `birknext.navigation.v1` in localStorage was read after the first render, so a remembered preference showed the defaults for one frame. | Fixed (synchronous read) |

## B. Workspace View

- **Presentation only.** It chooses the modules a role usually needs, puts their groups first and names a suggested start page. Switching **never navigates**: the page, project, running work and page state stay.
- **Module-level presets.** Defined in `WorkspaceViews.All`, the only preset definition. Every view also keeps Dashboard, User Guide and System Settings.

| View | Modules (routes) |
|---|---|
| All Modules | Every module |
| Project Leader | Delivery Overview, Delivery Reports, Recommended Workflow, Release Readiness, Snapshots, Correlation Review, Requirements Traceability |
| Scrum Master | Delivery Reports, Delivery Overview, Recommended Workflow, Specification Explorer, Plan Explorer, Task Explorer, Task Alignment Review, Change Traceability |
| Test Lead | Requirements Traceability, Release Readiness, Delivery Overview, Test Coverage & Overlap, Test Data, Frontend / API / Integration Quality Review, Performance Test Review, Critical E2E Regression, Target Environments |
| Developer (was Technical Reviewer) | Source Analysis, Specification Explorer, Schema Studio, API Quality Review, Technology Coverage, Dependency Review, Pipeline Review, Code Risk Review, Source Management, Import Project |
| Architect | Source Analysis, Integration Topology, Integration Quality Review, Schema Studio, Data Model Explorer, Constitution Explorer, Source Management, Technology Coverage, Generated Documentation Explorer, Environment Analysis |

- **Earlier preferences stay valid.** Stored value 4 ("Technical Reviewer") reads as Developer, and its group choices are kept. Fields added later default.
- **Current module outside the view.** The page stays open and its row stays, marked "Other view". A line above the page says "Current module is outside your selected workspace view (…)", with **Add … to …** (a per-view addition, the person's own customization) and **Switch to All Modules**.
- **Show other modules (N).** Reveals the rest of the modules in their groups, marked; remembered per view.

## C. Sidebar

- **Expanded (264px).** Brand, collapse control, Workspace View with its description, search, groups with chevrons (independent, remembered per view, all start expanded), rows [icon][label][status], one active row.
- **Collapsed (64px, icon rail).**
  - Compact brand "QA" and the expand control.
  - A Workspace View button with a flyout of `menuitemradio` items.
  - A search button that opens a compact search.
  - One icon per group; the current page's group has the active indicator.
  - Hover and focus show a label, which is never sticky and is hidden while the flyout is open.
  - A group opens a flyout menu of its modules: names, icons, status badges and the active row. Arrow keys move, Escape closes and returns focus, a click outside closes, and a click navigates.
- **Toggling.** One click. Only the sidebar width changes, with a 0.18s transition that is removed under reduced motion. MainLayout re-renders only when the width changes, so pages keep their state and nothing reloads.
- **Remembered.** The collapse state is remembered in the same `birknext.navigation.v1` preference. It is read synchronously on WebAssembly before the first render, so there is no flash.
- **Mobile (<641px).** An overlay drawer:
  - a menu button (`aria-expanded`) opens it; it is a dialog (`aria-modal`) with a close button;
  - Tab is trapped in the drawer and page scroll is locked;
  - Escape or the backdrop closes it and returns focus to the menu button;
  - a link navigates and closes it;
  - the drawer always shows labels and never changes the desktop collapse preference.
- **Tablet (641–1024px).** The desktop sidebar, which can be collapsed.
- **Keyboard.** Tab reaches everything. ArrowUp/ArrowDown/Home/End move between sidebar rows and group headings, and within flyouts. Enter/Space activate. Escape closes a flyout or the drawer, or clears search. Focus rings are visible.

## D. Feature Visibility: one resolution model

`NavigationModules.Resolve` applies this precedence (one model, consumed by the sidebar, the flyouts, search and the notice):

1. **Authorization.** BirkNext has none per module, so nothing resolves to access denied.
2. **Feature Visibility.** `DisabledByFeatureVisibility`: no row in any view. Search marks it "Disabled in Feature Visibility" as text, not a link. An addition never re-shows it.
3. **Workspace View.** `HiddenByWorkspaceView`: not a row, but search, Show other modules and the URL reach it.
4. **The person's additions.** `AddedByYou`.

Product availability (`FeatureAvailabilityCatalog`) is shown on the row and in search ("Unavailable"); it is never hidden. A module hidden only by the view opens normally by direct URL.

## E. Search

- **What it searches.** Every module: labels, earlier names and aliases (e.g. "Implementation Review", "FQR", "k6"), group names and descriptions. Matching is partial and case-insensitive, with the existing ranking.
- **Result groups:**
  1. "In current workspace (View)";
  2. "Available in other workspaces", naming the views that hold the module (opening one never switches the view);
  3. "Disabled in Feature Visibility".
- **Keyboard and screen readers.** It is a combobox: `aria-activedescendant`, arrow keys move the active result, Enter opens it, Escape clears and then closes the compact search. The empty state comes with a hint, and the result count is announced (`aria-live`).
- **Collapsed sidebar.** The same component runs in the compact search, focused on open.

## F. Tests

**Automated** (`NavigationWorkspaceTests`, rewritten and extended):

| Area | What is tested |
|---|---|
| Switching views | Every view keeps the current page, including its query, and the selected project. |
| Reachability | In every view, every visible module is reachable through Show other modules and search. |
| Presets | Every preset names real modules and groups, with no duplicate presets; the role mappings above hold. |
| Focused view | Group order; no "More modules" heading; modules outside the view hidden; Show other modules reveals them. |
| Resolution precedence | Feature Visibility outranks the view; additions are per view and never show a disabled module; Unavailable stays visible. |
| Search | Workspace buckets in order; case-insensitive and partial; aliases; group names; disabled modules are marked, not links. |
| Combobox | `role`, `aria-expanded`, `aria-activedescendant`, arrow keys; Enter opens a module from another workspace without switching the view. |
| Collapsed rail | Labels; active group; flyout as a menu; click-outside and Escape close it; compact search; workspace flyout; remembered. |
| Mobile drawer | Dialog with `aria-modal`; labels even when the desktop sidebar is collapsed; Escape closes; the desktop preference is untouched. |
| Outside-view notice | Add to the view, Switch to All Modules; Dashboard is in every view. |
| Earlier preferences | The stored "TechnicalReviewer" reads as Developer. |
| Current page | Its row is kept with "Other view" and its group expanded; one active row. |

| Run | Result |
|---|---|
| Navigation-related groups (Layout, analysis navigation, cross-page workspace, Documentation, …) | 468/468 passed |
| Full frontend suite (`dotnet test frontend/BirkNext.Web.Tests`) | **8113 passed, 0 failed** |
| Layout group re-run after the final CSS fix | 108/108 ×3 |

One pre-existing timing test, `ActivityCenterTests.Cancel_AffectsOnlyThatOperation…`, failed once in an isolated Layout run and passed in the full run and in three re-runs. It is unrelated to navigation.

**Real browser** (`scratchpad/nav-e2e.cjs`):

- **Setup:** Edge on the M2LB copy (DB `birknext_gaps`, API :5099, web :5199); every non-localhost host is aborted.
- **Result: 39/39 checks passed.**

| Scenario | Checks |
|---|---|
| A, collapse | 264px → 64px, content 1176 → 1376px wide, same route; active group indicator; no overflow; hover label; flyout focus and arrow keys; Escape returns focus; a flyout link navigates; refresh stays collapsed with no flash; expand. |
| B, workspace | Project Leader shows delivery, release, snapshots and correlation, and hides source; no "More modules"; collapsed rail → workspace flyout → Test Lead; the quality flyout lists its modules; the server's current workspace and project are unchanged; the page is unchanged; view and collapse survive refresh. |
| C, search | "API Quality" lists the current workspace (Test Lead) first, and Enter opens it; collapsed → compact search is focused; "pipeline" is listed under other workspaces and opens without switching the view, and the outside-view notice appears; "change traceability" is marked "Disabled in Feature Visibility" (not a link); Add to Test Lead clears the notice. |
| D, deep links | Delivery Reports is the only active row; switching to Developer keeps the page and marks the row "Other view"; back/forward move the active row; the Target Environments query deep link is the one active row; arrow keys move between rows. |
| E, responsive | No horizontal scroll at 900px and 390px; on mobile the menu is hidden behind the menu button; the drawer is a dialog with workspace, search and groups, with focus inside and page scroll locked; focus stays trapped after 60 Tabs; Escape closes it and returns focus; a link closes it; the desktop preference is untouched; no page errors; no external hosts. |

A visual check found the collapsed rail's hover label overlapping the open flyout's title. Fixed (the label is hidden while that flyout is open) and re-verified: 39/39.

## G. Remaining limitations

1. **No per-module authorization.** BirkNext has none, so the "Access denied" state is documented and not implemented. When roles are introduced, they belong at precedence 1 of `NavigationModules.Resolve`.
2. **Customization is limited.** It covers adding a module to a view and Show other modules. Re-ordering or removing preset modules is not offered.
3. **Flyout positioning.** Flyouts are placed next to their button and shifted up to stay in the viewport. On very short windows (under about 520px), the icon rail itself does not scroll.
4. **Command-palette shortcut.** There is no global shortcut such as Ctrl+K; the compact search is the search button in the rail.
