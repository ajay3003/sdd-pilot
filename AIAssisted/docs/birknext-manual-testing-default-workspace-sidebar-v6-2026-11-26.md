# BirkNext — Manual Testing default workspace and focused sidebar (V6, 2026-11-26)

Commit: `0e9c23ca`. The brief's "NO GIT COMMITS" line was overridden by the user's standing instruction to commit; nothing was pushed.

## A. Current architecture (audited, before)
One navigation model in `frontend/BirkNext.Web/Services/NavigationCatalog.cs`:

- **The catalog.** `NavigationCatalog.Sections` lists every module row and its Feature Visibility predicate.
- **Workspace views.** `WorkspaceViews` holds the workspace views: `WorkspaceView` enum ids (stored as numbers), definitions with workflow groups, `Ordered(view)` (cached per view) and `IsCurrent`.
- **Reach.** `NavigationModules.Resolve` decides one module's reach. Feature Visibility comes first, then the view's presets, then the person's additions.
- **Choice and preferences.** `NavigationSectionState` holds the selected view, collapsed groups, additions and the collapsed sidebar. It is stored in localStorage `birknext.navigation.v1` and read synchronously before the first render.
- **Rendering.** `Layout/NavMenu.razor` renders the expanded sidebar, the collapsed rail with flyouts, and the mobile drawer. `Layout/WorkspaceScopeNotice.razor` marks a page outside the view.

Answer to the brief's A–F question: **F, a combination**.

| Source | What it decides |
|---|---|
| Module registry (catalog) | Which modules exist |
| Feature Visibility (System Settings) | Which modules are enabled |
| Workspace view (preference) | Which rows the sidebar shows |
| Person's additions (preference) | Extra rows the person added to a view |

None of these is a permission: route authorization and backend checks are separate.

Before V6, Manual Testing was already the default (`WorkspaceViews.Default`), but its sidebar showed:

- seven groups;
- the every-view rows (Dashboard, All modules, User Guide, System Settings);
- module search;
- a row for any open page outside the view.

The start page `/` always redirected to `dashboard`.

## B. Default workspace — IMPLEMENTED, VERIFIED
- Without a valid remembered choice, the workspace is Manual Testing (unchanged), and `/` now opens `manual-testing` (Testoversikt). `Pages/Home.razor` reads the remembered workspace and uses `WorkspaceViewDefinition.HomeRoute`; it never writes the preference.
- A remembered other workspace is respected: `/` → `dashboard`, as before.
- An unknown or unreadable stored view falls back to Manual Testing (existing `Restore`).

## C. Manual Testing menu registry — IMPLEMENTED, VERIFIED
`WorkspaceViews.CreateManualTesting()`, a **focused** view (`WorkspaceViewDefinition.Focused`):

| Group | Row | Route |
|---|---|---|
| Manual Testing | Testoversikt | `manual-testing` |
| | Finn og forstå | `manual-testing/discover` |
| | Planlegg og klargjør | `manual-testing/plan` |
| | Testplaner | `manual-testing/plans` (a view link to the same page as `manual-testing`, as in `FeatureVisibilityTaxonomy`) |
| Help & Settings | System Settings | `admin/system-settings` |

A focused view shows only its own groups. It has none of these:

- every-view rows;
- catalog remainder groups;
- the person's additions;
- a row for an open page outside the view;
- the «All modules» hint;
- module search.

Both groups are open by default, so there are no empty or closed headers. Labels come from view links, so the catalog keeps its own names for the other workspaces.

## D. Other workspaces — VERIFIED
- The seven other workspaces are unchanged: their groups, search, All modules and additions still work.
- `documentation-drift` was only in Manual Testing; it now lives in Test Management → «System & requirements». Every module still has a workspace home (`NavigationDispositionTests`).
- Browser check: Test Management shows its 24 links and search; switching back restores the focused sidebar.

## E. Route mapping — VERIFIED
The sidebar and the step bar use the same constants (`ManualTestingPresentation.HomeRoute` and `Stages`); test V04 pins this.

The browser check confirmed, on each of the four pages, that exactly one sidebar row and the matching step-bar tab are current.

## F. Deep links — IMPLEMENTED, VERIFIED
`/specification-explorer` opened from Manual Testing:

- the page opens and the workspace stays Manual Testing;
- the sidebar is unchanged and highlights no Manual Testing row;
- `WorkspaceScopeNotice` shows «Current module is outside your selected workspace view (Manual Testing)» with **«Tilbake til Manual Testing»** (→ Testoversikt) and «Switch to All Modules». «Add to view» is not offered, because a focused view takes no additions.

## G. System Settings — VERIFIED
It is the single row of «Help & Settings» at the bottom, with its existing route, Feature Visibility predicate and page checks. It is never opened automatically.

## H. Preferences — VERIFIED
- The preference is written only when the person changes something (existing `SaveAsync` after restore).
- Following a link writes nothing.
- A refresh keeps both the workspace and the current row.
- Back and Forward restore the highlighted row.

## I. Context isolation
- The workspace switch never navigates and never changes the project (existing contract, test V07).
- Scenario intent into Planlegg goes by `?scenario=` and wins over remembered selections; this was fixed in `20e7cd94` (cross-page hardening report).
- The unsaved-editor guards of Planlegg and of Testoversikt customisation are unchanged.

**Brief §6.2 — PARTIAL, deliberately:** a switch does not navigate to the new workspace's default route. The existing contract is that a switch never moves the person off the page they are on. `/` and the brand do lead to the selected workspace's start page.

## J. Responsive and accessibility — VERIFIED (browser)

| Viewport | Result |
|---|---|
| 1920×1080, 1440×900, 1280×800, 1024×768 | 5 links, no horizontal overflow |
| 390×844 (mobile drawer) | Same 5 links |
| Collapsed rail | Workspace, «Manual Testing» and «Help & Settings» buttons with aria-label/data-label tooltips; the flyout lists the four destinations |

- Keyboard: the focusable elements in the panel are the selector, two group buttons and five links; nothing hidden is focusable.
- The active row uses `aria-current="page"` plus a visual bar, so it does not rely on colour alone.

## K. Performance
- `Ordered(view)` is cached per view and the focused view builds 2 groups instead of 8+ catalog groups. No measurement of sidebar render time was made, so no performance claim.
- Loading: the sidebar showed exactly 5 links from its first paint (20 samples, 50 ms apart): no flash of the full menu.

## L. Automated tests
- `ManualTestingViewTests` rewritten for V6 (V01–V11). It covers:
  - default workspace and start page;
  - exact entries and no hidden groups, All modules or search;
  - highlighting and step-bar routes;
  - a deep link without sidebar pollution, and the back link;
  - modules kept registered and enabled;
  - workspace switch and back;
  - additions ignored;
  - preference restore and fallback;
  - collapsed rail and mobile drawer.
- `HomePageTests`: `/` → Testoversikt by default, → dashboard in other remembered workspaces.
- **Tests that pinned the old seven-group view were updated to the new requirement, not weakened.** Generic sidebar tests (search, drawer, navigation progress, Quality Review status) now select a non-focused view explicitly. The focus-panel tests moved from Manual Testing to Test Management, which now holds those capabilities.
- Frontend suite: 9846 passed and 1 failed in the full run. The failure was a cross-page test that relied on the default view; it was fixed and the affected classes pass in a targeted rerun.
- **Fix found by the tests:** the brand's `title` rendered `&amp;` literally once its `href` became dynamic. It is now a C# value.

## M. Browser verification — VERIFIED
Isolated v1env21 (fresh database, fresh browser profile, ports 5197/5097), script `scratchpad/v1/v1_v6sidebar.py`: **36/36 PASS**. The checks:

- fresh start at `/`;
- exact entries;
- navigation and refresh of all four pages;
- deep link;
- other workspace, preference and `/` in it;
- Back and Forward;
- keyboard focusables;
- five viewports and the collapsed rail;
- console free of errors.

Screenshots are in `scratchpad/v1/v1env21/shots/`: `v6-1440-testoversikt.png`, `v6-1440-deeplink-spec.png`, `v6-1440-testlead.png`, `v6-{1920x1080,1440x900,1280x800,1024x768,390x844}.png` and `v6-1440-collapsed.png`.

## N. Changed files
- `Services/NavigationCatalog.cs`
- `Layout/NavMenu.razor`
- `Layout/WorkspaceScopeNotice.razor`
- `Pages/Home.razor`
- Tests: `ManualTestingViewTests`, `NavigationWorkspaceTests`, `NavigationDispositionTests`, `NavigationProgressTests`, `ProductBrandingTests`, `HomePageTests`, `CrossPageWorkspaceContractTests`, `WorkspaceFocusTests`

## O. Remaining issues
- The workspace description under the selector is the earlier English text, kept as is.
- The step bar labels Testoversikt «Manuell testing» (its own existing label), while the sidebar says «Testoversikt». Same route.
- The dashboard's focus panel lists no review steps in Manual Testing, because those modules are no longer in that workspace. They are listed in Test Management.

## P. Acceptance
| Criterion | Status |
|---|---|
| Manual Testing is the default; Testoversikt is the landing page | VERIFIED |
| Only four destinations plus System Settings at the bottom | VERIFIED |
| No Project Setup / Requirements / Coverage / All Modules / empty headers | VERIFIED |
| Other modules hidden, not deleted or disabled | VERIFIED (V06, Feature Visibility unchanged) |
| Other workspaces usable; preference restored | VERIFIED |
| Deep links work without polluting the sidebar | VERIFIED |
| Consistent after refresh; no flash at start | VERIFIED |
| Sidebar and step bar on the same routes | VERIFIED |
| No data mutated (no API writes in the change) | IMPLEMENTED |
| Switch navigates to the new workspace's default route | PARTIAL (see I) |
