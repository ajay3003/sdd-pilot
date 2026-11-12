# Manual testing — gap register (tasks 16–19)

**Priorities:** P0–P3.
**States:**
- FIXED: implemented, with a regression test;
- GUARDED: risk contained, capability not built;
- OPEN: documented, not built.

Evidence is in `birknext-manual-testing-capability-audit.md`.

| Id | Pri | Area | Gap (root cause) | Decision / change | Test | State |
|---|---|---|---|---|---|---|
| MT-1 | P1 | Menu Views | No view for functional testers; the views could only reorder catalog groups, so a workflow order (set up → understand → requirements → prepare → coverage → follow up) was impossible | Built-in `WorkspaceView.ManualTesting` with optional per-view workflow groups (`WorkspaceViewGroup`) over existing catalog rows. Same resolution model: Feature Visibility first, additions, search and "Show other modules" unchanged. Not a permission. | `ManualTestingViewTests` (5), `NavigationWorkspaceTests` theories extended | FIXED |
| MT-2 | P1 | Scenario destination | No navigation entry led to the tester-oriented scenarios (System Understanding → What to test) | View shortcut "Functional Test Scenarios" → `system-understanding?tab=tests` in Manual Testing only (a catalog row would break the "tabs stay nested" rule and the Feature Visibility inventory); follows System Understanding's visibility; no new scenario store | `ManualTestingViewTests.Functional_test_scenarios_is_a_view_shortcut…`; browser MT-L | FIXED |
| MT-3 | P2 | Help Center | The view description was outdated ("Technical Reviewer", no Developer or Architect, "only order"); there was no manual-testing guidance | New topic "Manual testing" (workflow, view ≠ permission, Azure DevOps Test Plans boundary, testing without a frontend, XSD ≠ functional, Skolenærvær example as illustration); Workspace View bullet corrected | `DocumentationCatalogTests`, `UserGuidePageTests` (guide health); browser MT-Y | FIXED |
| MT-4 | P1 | Recommended Workflow / Dashboard | Neither page knew the workspace view, so every reader saw every recommendation | Shared projection `WorkspaceFocus` over the existing `ReviewCapability` facts (same ids, routes, decisions): relevance by the view's modules, at most 3 next steps, ready/blocked/done counts, and source-baseline risks shown in every view. Panel on Recommended Workflow (full) and Dashboard (compact). Hidden in All Modules (the full pages remain). No new engine, no persistence. | `WorkspaceFocusTests` (4 + 1 theory) | FIXED |
| MT-5 | P2 | Test Data ↔ scenarios | Guide and spec scenarios have no link to Test Data assets; only consumer usages exist | Not built: needs an association model owned by Test Data | — | OPEN |
| MT-6 | P2 | Test Data lifecycle | `TestDataLifecycleState.ReviewRequired` (set by Source Change Impact) is rendered nowhere | Not built in this pass | — | OPEN |
| MT-7 | P2 | Requirements Traceability | No "reviewed by a tester" coverage state; only candidate links can be confirmed | Not built: needs a decision model on coverage links | — | OPEN |
| MT-8 | P2 | Azure DevOps Test Plans | No read or write of Test Plans, Suites or Test Cases exists | Boundary stated in the help; no claim of Test Case existence or execution. Reading requires new integration (task 20 scope). | — | OPEN (by design) |
| MT-9 | P3 | Custom views | Only built-in views; no editor or restore default (personal additions per view exist) | Not built (the briefs say not to build a complex editor) | — | OPEN |
| MT-10 | P2 | Dashboard manual summary | Requirement-review, scenario-coverage and testability metrics per view do not exist as shared facts | The panel shows only real facts (capability readiness and decisions); no synthetic percentages | `WorkspaceFocusTests` | GUARDED |
| MT-11 | P2 | Recommendation audience metadata | Capabilities have no severity or audience; relevance is derived from the view's modules | Derived relevance; severity is never invented | — | GUARDED |
| MT-12 | P3 | Labels | Views cannot rename items (for example "Critical E2E Regression" → "Functional scenarios") | Not needed: a dedicated deep-link row carries the tester label and points at the canonical page | — | CLOSED |
