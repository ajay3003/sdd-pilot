# Workspace Views — governance audit

**Baseline:** `0752a87d` (after MT-7 and the Manual Testing UX consolidation). Branch `008-traceability-first`; only excluded files are uncommitted.

## What exists (actual code)

- **One registry:** `NavigationCatalog.Sections` (every module row: route, label, Feature Visibility rule, aliases, hint) and `WorkspaceViews.All` (view presets).
- **Membership:** a view's preset is a list of catalogue **routes**, as plain `Modules` or workflow `Groups`. Labels, visibility and availability always come from the catalogue row. No duplicate route definitions exist.
- **Ids:** `enum WorkspaceView { AllModules = 0, ProjectLeader = 1, ScrumMaster = 2, TestLead = 3, Developer = 4, Architect = 5, ManualTesting = 6 }`.
  - `NavigationSectionState` persists the selected view in browser storage (`birknext.navigation.v1`), with per-view collapse choices, "Show other modules" and per-view **additions** (the only customization; there is no custom-view editor).
  - The view is stored by number; per-view dictionaries are keyed by name.
  - Unknown values are ignored, so the default is kept. The former "TechnicalReviewer" key is read as Developer.
- **Resolution order** (`ModuleReach`): Feature Visibility (disabled: no row in any view) → in the view → added by the person → hidden by the workspace view (reachable by search, "Show other modules", URL, All Modules). Product availability is shown on the row; it does not remove it. Retired rows stay behind `LegacyTraceabilityNavigationEnabled`. Authorization is enforced by the API, never by the sidebar.
- **Dashboard and Project Quality:** `dashboard` is the **Project Quality** page (route `dashboard`, label "Project Quality"). It is the same page, not two pages. It embeds `WorkspaceFocusPanel` (compact), and Recommended Workflow (`getting-started`) embeds the full panel.
- **View-aware projection:** `WorkspaceFocus.Of(view, capabilities, sourceContext)` filters the **shared** `ReviewCapability` list (one recommendation engine) to the view's modules and orders up to three next actions. Risks come from the shared source context and are shown in every view. No per-view calculation and no duplicate engine.
- **Switching** (`SetView`) changes only the navigation state. There is no navigation, no project change, no baseline change and no API call. A page outside the view stays open, with the notice "Current module is outside your selected workspace view (…)" and the options "Add" and "Switch to All Modules".

## Findings before the change

| # | Finding |
|---|---|
| F1 | Seven views. There was **no Technical Tester**: technical test verification (contracts, integration suites, execution evidence, E2E, performance) had no audience view |
| F2 | Labels "Project Leader" and "Manual Testing" did not match the requested names (Project Manager, Manual Tester) |
| F3 | Test Lead missed functional preparation (System Understanding, Specification Explorer, Test Plan Suggestions, Contract Testing, Integration Test Suites, Test Execution & Evidence) and listed Delivery Overview as primary |
| F4 | Project Manager landed on Delivery Overview and had no project context rows (System Understanding, Source Management, Target Environments) |
| F5 | Only Manual Testing had audience workflow groups. Other views reordered catalogue groups ("Source & Architecture" etc.) that still mixed unrelated modules |
| F6 | The baseline risk read identically in every view (correct identity, no audience guidance) |
| F7 | The functional test-preparation facts (Test Plan Suggestions, MT-7 coverage) appeared only in Manual Testing, not for the Test Lead |
| F8 | Description copy did not match the brief |

## Decisions
- **Keep the ids.** Labels change to Project Manager and Manual Tester; the enum names `ProjectLeader`/`ManualTesting` stay. Saved preferences, collapse choices and additions remain valid.
- **Add `TechnicalTester = 7`.** An append-only number, so no existing value changes.
- **Eight presets,** each as audience workflow groups over existing routes. See `birknext-workspace-views-capability-matrix.md`.
- **Selector order:** All Modules, Project Manager, Scrum Master, Test Lead, Manual Tester, Technical Tester, Developer, Architect. Descriptions follow the brief.
- **Landing routes** (used only by explicit links; switching never navigates): PM → Project Quality, SM → Delivery Reports, TL → Project Quality, MT → System Understanding, TT → Project Quality, Dev → Source Analysis, Arch → System Understanding.
- **Risks** keep one code, severity and destination. Only the leading sentence speaks to the audience (§38 wording).
- **Test Lead** also gets the functional test-preparation facts on Dashboard and Recommended Workflow (the same facts as the Manual Tester).
- **Not changed:** the backend, authorization, Feature Visibility, the recommendation engine, Project Quality calculations, the source baseline, the Test Plan Suggestions data and review decisions.
