# Manual Testing UX consolidation — implementation

Audit: `docs/audits/birknext-manual-testing-ux-consolidation.md`. Acceptance: `docs/audits/birknext-scenario-navigation-acceptance.md`.

| Area | Change |
|---|---|
| `NavigationCatalog` (built-in Manual Testing view) | The "Functional Test Scenarios" `WorkspaceViewLink` was removed. "Test preparation" = Test Plan Suggestions, Test Data. `TestScenariosRoute` remains, as the documented deep link to What to test |
| `TestPlanSuggestions.razor` Discover | Compact rows grouped by guide stage (`TestPlanSuggestionsPresentation.ByStage`). The selection bar has "Select visible (n)", "Clear selection" and "N hidden by filters — still selected". One primary sticky action, "Prepare Test Plan (n)" / "Add n to proposal". "Business rule" filter. One "Evidence" link per row. The card grid was removed |
| CSS | New `.tps-groups/.tps-row*` rows and a sticky action bar. `.tps-k`, `.tps-eyebrow`, `.su-eyebrow`, `.su-tag`, `.su-ev` and `.su-sha` raised to 12 px. The What to test checkbox gets a 28 px target |
| Help Center | "Getting started" Manual Testing workflow: System Understanding (learn, What to test, Prepare) vs Test Plan Suggestions (prepare, review, Test Data, snapshots, compare, export); Azure DevOps for formal plans and execution |
| Tests | `ManualTestingViewTests` (alias gone in every view, deep link highlights System Understanding, groups); `TestPlanSuggestionsPageTests` (grouping, hidden selections, select visible, clear, one primary action, empty hint, "Other" group) |

**Not changed:**
- scenario identities and the guide;
- the What to test tab and its prepare actions;
- drafts, review lifecycle, snapshots, comparison and export;
- Dashboard and Workflow facts;
- authorization and Feature Visibility;
- the backend.

**Remaining gaps:**
- The view's display name is still "Manual Testing". The rename to "Manual Tester" belongs to the queued Workspace Views task.
- The selection is not kept after leaving the page (client state by design).
