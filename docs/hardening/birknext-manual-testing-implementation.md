# Manual testing — implementation (tasks 16–19, first increment)

Audit: `docs/audits/birknext-manual-testing-capability-audit.md`. Gaps: `birknext-manual-testing-view-gap-register.md`.
Acceptance: `birknext-manual-testing-acceptance.md`.

## Design rules kept

- **One navigation model.** It is extended, not duplicated: the existing views enum, catalog, resolution model (Feature Visibility, then view,
  then additions), search and storage.
- **Tabs stay nested in their page.** This is an existing rule, enforced by tests. The scenario link is therefore a **view shortcut**, not a catalog
  row: it exists only in Manual Testing, opens System Understanding → What to test, and follows that page's visibility rule.
- **One fact model for next steps.** The panel projects the existing `ReviewCapability` set (same ids, routes, readiness and decisions). It
  stores nothing, invents no severity, and shows the source-baseline risk from the shared source context in every view.
- **A view is never a permission.** No feature switch, authorization, project or analysis baseline changes; verified in the browser and in tests.

## Changes

| Area | Change | Files |
|---|---|---|
| Views | `WorkspaceView.ManualTesting = 6` (stored as a number; older values unchanged). Optional per-view workflow groups (`WorkspaceViewGroup`) and view shortcuts (`WorkspaceViewLink`). `Ordered()` is cached per view; `SectionForLocation(view, …)`; `IsCurrent(view, …)` lets a current deep link claim its location. | `Services/NavigationCatalog.cs` |
| Sidebar | Rows follow the group's order and resolve shortcuts with the same resolution model; the current row and expanded group are view-aware | `Layout/NavMenu.razor` |
| Next steps | `WorkspaceFocus`: relevance by the view's modules, at most 3 steps (ready first, then the view's workflow order), ready/blocked/done counts, baseline risks | `Services/WorkspaceFocus.cs` |
| Panel | `WorkspaceFocusPanel` (full on Recommended Workflow, compact on the Dashboard); follows a view switch without reloading; hidden in All Modules | `Components/WorkspaceFocusPanel.razor(.css)`, `Pages/RecommendedWorkflow.razor`, `Pages/Dashboard.razor` |
| Help | New topic "Manual testing" (workflow, view ≠ permission, Azure DevOps Test Plans boundary, no-frontend testing, XSD ≠ functional, Skolenærvær as an illustration only); Workspace View description corrected | `Components/Guide/GuideGettingStarted.razor`, `Components/Guide/GuideGovernance.razor`, `Services/UserGuideContent.cs` |

## Tests

- `Layout/ManualTestingViewTests` (6): view and order, the shortcut, rendering and switching, Feature Visibility and search, reachability, stored and
  unknown views.
- `Services/WorkspaceFocusTests` (4 + a 4-case theory): relevance and identity, blocked/done, the baseline risk in every view, the panel following a
  view switch.
- `Layout/NavigationWorkspaceTests`: the switching theory now includes Manual Testing. The catalog-group reachability theory deliberately excludes
  it, because its rows live in workflow groups; that is covered by the Manual Testing reachability test.

## Not done in this increment

See gap register MT-5…MT-11:
- Test Data ↔ scenario links;
- rendering `ReviewRequired` for test data;
- tester-reviewed coverage state;
- Azure DevOps Test Plan reads;
- a custom-view editor;
- per-view dashboard metrics beyond capability facts.

Task 17's lifecycle states (accepted, dismissed, superseded) and task 18's custom-view-derived dashboards depend on these and stay open.
