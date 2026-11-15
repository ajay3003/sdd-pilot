# BirkNext — Workspace & navigation architecture redesign

Date: 2026-11-15 · Branch: `008-traceability-first`.

## 1. Executive summary

The workspace selector now starts with the three testing workspaces — **Manual Testing (default), Technical Testing, Test Management** — followed by the other existing workspaces (Project Manager, Scrum Master, Developer, Architect), with **All Modules always last**. No module can disappear because of the selected workspace any more: every workspace keeps an **All modules** row that opens a searchable catalog of every module, built from the one navigation registry and the sidebar's own resolution model. Integration Journeys, previously reachable only from other pages, is now a module of its own. Saved preferences keep working: only the order and labels changed, never the stored workspace ids.

## 2. Original bug reproduction (code-traced)

- Selecting a specialised workspace removed every module outside its preset from the sidebar. `NavigationModules.Resolve` returns `ModuleReach.HiddenByWorkspaceView` and the sidebar renders only `InView` / `AddedByYou` rows. The earlier "Show other modules" choice had been removed (`NavigationSectionState.Stored.ShowOther`: "still read … but ignored"), so the only ways back were the sidebar search or switching to All Modules.
- The default workspace for a person without a saved choice was `WorkspaceView.AllModules` (enum value 0, first in the selector).
- The selector listed All Modules first and the testing workspaces fourth to sixth.
- Integration Journeys (`/integration-journeys`) had no sidebar entry by design (opened only from Integration Quality Review and Test Coverage).

## 3. Root causes

1. Workspace membership acted as sidebar *visibility* with no permanent route to the rest (search only).
2. The default and the selector order came from list order (`WorkspaceViews.All`) with All Modules first.
3. No catalog page existed; the All Modules *view* was the only complete list, and it replaces the workspace.

Not causes: Feature Visibility (applied first, unchanged), authorization (BirkNext has no per-module authorization; backend authorization is unchanged), route registration (every page route is a catalog module or a documented contextual route — `FeatureVisibilityTaxonomy.ContextualRoutes`, checked by the inventory test).

## 4. Existing workspace inventory

Read from the browser (Release build): the sidebar rows of each workspace, in selector order. Every workspace has the `all-modules` row.

| # | Workspace | Sidebar rows | First rows |
|---|---|---|---|
| 1 | Manual Testing (default) | 18 | `manual-testing`, `manual-testing/discover`, `test-plan-suggestions`, `manual-testing/execute`, `project-import`, `sources` … |
| 2 | Technical Testing | 22 | `sources`, `system-understanding`, `api-quality-review/contracts`, `api-quality-review`, `schema-studio`, `integration-suites` … |
| 3 | Test Management | 25 | `dashboard`, `getting-started`, `release-readiness`, `system-understanding`, `specification-explorer`, `quality/document` … |
| 4 | Project Manager | 15 | `dashboard`, `getting-started`, `delivery-snapshots`, `delivery-overview`, `delivery-reporting`, `release-readiness` … |
| 5 | Scrum Master | 13 | `delivery-snapshots`, `delivery-reporting`, `delivery-overview`, `specification-explorer`, `plan-explorer`, `task-explorer` … |
| 6 | Developer | 19 | `sources`, `source-analysis`, `project-import`, `specification-explorer`, `task-explorer`, `task-alignment` … |
| 7 | Architect | 21 | `system-understanding`, `integration-topology`, `integration-quality-review`, `sources`, `source-analysis`, `technology-coverage` … |
| 8 | All Modules | 55 | `dashboard`, `all-modules`, `getting-started`, `system-understanding`, `project-import`, `sources` … |

## 5. Canonical module inventory

Machine-readable: `birknext-workspace-navigation-inventory.json` next to this report (browser acceptance output; one entry per module: route, label, area, Feature Visibility state, workspaces whose sidebar lists it, opened). 61 modules; 55 enabled in the acceptance database, 55 of them opened at their route without an error; 6 turned off by Feature Visibility (listed as off, not linked).

| Area | Module | Route | Enabled | Workspaces (besides All Modules) | Opened |
|---|---|---|---|---|---|
| getting-started | Project Quality | `/dashboard` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | yes |
| getting-started | All modules | `/all-modules` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | yes |
| getting-started | Recommended Workflow | `/getting-started` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | yes |
| getting-started | System Understanding | `/system-understanding` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Architect | yes |
| getting-started | Import Project | `/project-import` | yes | Manual Testing, Developer | yes |
| getting-started | Source Management | `/sources` | yes | Manual Testing, Technical Testing, Project Manager, Developer, Architect | yes |
| getting-started | Demo Projects | `/demo-projects` | yes | — (All modules only) | yes |
| getting-started | Project Test Setup | `/project-scopes` | yes | — (All modules only) | yes |
| getting-started | Target Environments | `/admin/system-settings?section=target-environments` | yes | Test Management, Project Manager | yes |
| delivery-reporting | Delivery Snapshots | `/delivery-snapshots` | yes | Project Manager, Scrum Master | yes |
| delivery-reporting | Delivery Overview | `/delivery-overview` | yes | Project Manager, Scrum Master | yes |
| delivery-reporting | Delivery Reports | `/delivery-reporting` | yes | Project Manager, Scrum Master | yes |
| document-review | Specification Explorer | `/specification-explorer` | yes | Manual Testing, Test Management, Scrum Master, Developer | yes |
| document-review | Constitution Explorer | `/constitution-explorer` | yes | Architect | yes |
| document-review | Data Model Explorer | `/data-model-explorer` | yes | Architect | yes |
| document-review | Plan Explorer | `/plan-explorer` | yes | Scrum Master | yes |
| document-review | Task Explorer | `/task-explorer` | yes | Scrum Master, Developer | yes |
| document-review | Document Quality Review | `/quality/document` | yes | Manual Testing, Test Management | yes |
| document-review | Task Alignment Review | `/task-alignment` | yes | Scrum Master, Developer | yes |
| document-review | Documentation Drift | `/documentation-drift` | yes | Manual Testing | yes |
| traceability | Requirements Traceability | `/artifact-traceability` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Architect | yes |
| traceability | Change Traceability | `/implementation-traceability` | off | — (All modules only) | — |
| traceability | Impact Analysis | `/impact-analysis` | off | — (All modules only) | — |
| traceability | Relationship Review | `/correlation-review` | yes | Test Management, Project Manager | yes |
| traceability | Release Readiness | `/release-readiness` | yes | Test Management, Project Manager, Scrum Master | yes |
| traceability | Snapshots | `/snapshots` | yes | Project Manager, Architect | yes |
| traceability | Traceability & Coverage | `/traceability` | off | — (All modules only) | — |
| traceability | Traceability Suggestions | `/traceability/suggestions` | off | — (All modules only) | — |
| traceability | Code Traceability | `/code-traceability` | off | — (All modules only) | — |
| source-review | Source Analysis | `/source-analysis` | yes | Developer, Architect | yes |
| source-review | Schema Studio | `/schema-studio` | yes | Technical Testing, Developer, Architect | yes |
| source-review | Generated Documentation Explorer | `/generated-documentation-explorer` | yes | Architect | yes |
| source-review | Generated Documentation Health | `/generated-documentation-health` | yes | — (All modules only) | yes |
| source-review | Technology Coverage | `/technology-coverage` | yes | Developer, Architect | yes |
| source-review | Dependency Review | `/dependency-review` | yes | Developer, Architect | yes |
| source-review | Pipeline Review | `/pipeline-review` | yes | Developer | yes |
| source-review | Environment Analysis | `/azure-environment` | yes | Architect | yes |
| source-review | AI Change Review | `/ai-change-auditor` | off | — (All modules only) | — |
| manual-testing-workspace | Manual Testing | `/manual-testing` | yes | Manual Testing, Test Management | yes |
| manual-testing-workspace | Discover & understand | `/manual-testing/discover` | yes | Manual Testing | yes |
| manual-testing-workspace | Plan & prepare | `/test-plan-suggestions` | yes | Manual Testing, Test Management | yes |
| manual-testing-workspace | Execute & document | `/manual-testing/execute` | yes | Manual Testing, Test Management | yes |
| quality-testing | Test Coverage & Overlap Review | `/test-coverage-review` | yes | Manual Testing, Technical Testing, Test Management | yes |
| quality-testing | Code Risk Review | `/code-risk-review` | yes | Technical Testing, Developer | yes |
| quality-testing | Test Data | `/test-data` | yes | Manual Testing, Technical Testing, Test Management | yes |
| quality-testing | Contract Testing | `/api-quality-review/contracts` | yes | Technical Testing, Test Management, Developer, Architect | yes |
| quality-testing | Integration Test Suites | `/integration-suites` | yes | Technical Testing, Test Management | yes |
| quality-testing | Test Execution & Evidence | `/execution-results` | yes | Technical Testing, Test Management, Developer | yes |
| quality-testing | Parquet Quality Review | `/parquet-viewer` | yes | Technical Testing | yes |
| quality-testing | Frontend Quality Review | `/frontend-quality-review` | yes | Test Management | yes |
| quality-testing | API Quality Review | `/api-quality-review` | yes | Technical Testing, Test Management, Developer, Architect | yes |
| quality-testing | Integration Topology | `/integration-topology` | yes | Technical Testing, Architect | yes |
| quality-testing | Integration Quality Review | `/integration-quality-review` | yes | Technical Testing, Test Management, Architect | yes |
| quality-testing | Integration Journeys | `/integration-journeys` | yes | Technical Testing | yes |
| quality-testing | Performance Test Review | `/performance-test-review` | yes | Technical Testing, Test Management | yes |
| quality-testing | Critical E2E Regression | `/critical-e2e-regression` | yes | Technical Testing, Test Management | yes |
| extensions | M01 – Gradert tilgang (Kode 6/7) | `/security-classification-review` | yes | — (All modules only) | yes |
| extensions | Skolenærvær Testing | `/extensions/skolenaervaer` | yes | — (All modules only) | yes |
| admin | User Guide | `/user-guide` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | yes |
| admin | System Settings | `/admin/system-settings` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | yes |
| admin | Backup & Transfer | `/workspace-transfer` | yes | — (All modules only) | yes |

## 6. Old versus new behaviour

| | Before | After |
|---|---|---|
| Selector order | All Modules, Project Manager, Scrum Master, Test Lead, Manual Tester, Technical Tester, Developer, Architect | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect, All Modules |
| Default (no / invalid saved choice) | All Modules | Manual Testing |
| Module outside the workspace | gone from the sidebar (search only) | reachable from the **All modules** row in every workspace, from the help line under the selector, from search, and at its own address |
| Complete list | the All Modules *view* only | the All modules **page** (any workspace) and the All Modules view |
| Integration Journeys | no sidebar entry | module (Quality & Testing; Technical Testing workspace) |

## 7–9. Final workspaces, default, All Modules

Order and labels as above (ids unchanged: AllModules 0, ProjectLeader 1, ScrumMaster 2, TestLead 3, Developer 4, Architect 5, ManualTesting 6, TechnicalTester 7). Default `WorkspaceViews.Default = ManualTesting`. The All modules page (`/all-modules`) lists every registry module once, grouped by the registry's areas, with search (the sidebar's ranking: labels, earlier names, descriptions, group names), what the current workspace means for each module (*In this workspace*, *Outside this workspace · in …*), Feature Visibility state (*Turned off …*, not linked) and product availability.

## 10–11. Feature Visibility and authorization

Precedence unchanged: Feature Visibility decides whether a module exists (a turned-off module is no row anywhere and is listed as off, not linked, on the All modules page); the workspace only orders and emphasises. Workspace membership is never a permission; no backend authorization changed and no route was opened that was not already reachable.

## 12. Shared registry

One registry: `NavigationCatalog.Sections` (modules), `WorkspaceViews` (memberships, order, default), `NavigationModules` (resolution and search), `FeatureVisibilityTaxonomy.ContextualRoutes` (pages without a sidebar entry, with reasons). The sidebar, search, flyouts, Feature Visibility inventory, documentation drift check and the All modules page all read these; no second list was added.

## 13. Preference migration

Stored preference `birknext.navigation.v1` keeps the workspace as its enum number; numbers are unchanged, so a saved choice keeps its meaning (a saved "Test Lead" opens as "Test Management"). Missing, unreadable or unknown values fall back to Manual Testing. Per-workspace collapsed groups and additions are kept.

## 14. Deep links

No route changed. All existing aliases and redirects (`spec-drift`, `sample-projects`, `compare`, …) stay as documented contextual routes. A page outside the current workspace still opens and keeps one temporary sidebar row while open.

## 15–17. Responsive, tests, browser acceptance

All runs below are on the final code of this commit (no result was carried over from an earlier revision).

| Run | Result |
|---|---|
| Focused navigation/workspace/All modules tests (`Nav`, `Workspace`, `AllModules`, plus the analysis/applicability sidebar tests) | **615 / 615** |
| Full frontend suite (`BirkNext.Web.Tests`) | **9,062 passed, 0 failed, 0 skipped** |
| Backend partitions 1–6, sequential (`BirkNext.Api.Tests`) | **6,817 passed, 0 failed, 19 skipped** (1,052+2 · 1,099+5 · 888 · 676+1 · 539+3 · 2,563+8). The change is frontend-only; the run used this production code. |
| API Release publish / Web Release publish | exit 0 / exit 0, 0 errors; SRI check: 87 assets, the one known baseline item |
| Existing acceptance baseline (7 project categories) on this build | **106 / 106** |
| Workspace acceptance, pass 1 (`nav-accept.cjs`) | **38 / 38** |
| Workspace acceptance, pass 2 (`nav-accept2.cjs`) | **32 / 32** |
| Browser page errors · HTTP 5xx · network-guard violations (5000/5173/external blocked) | 0 · 0 · 0 |

Release build, web :5197, API :5097, fresh disposable database `birknext_nav_acc_20261115` (template copy), Edge via Playwright.

What the two passes verified: no saved choice → Manual Testing selected; selector order exactly Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect, All Modules; every workspace's sidebar has rows and the All modules row; Technical Testing lists the technical modules including Integration Journeys; Test Management lists coverage, readiness, manual runs and Critical E2E; the All modules page lists every registry module once (61), marks modules outside the workspace instead of hiding them, finds a module by what it does, and lists the 6 modules turned off by default Feature Visibility as off without a link; **every one of the 55 enabled modules opened at its own route** without "nothing at this address" or a page error (`nav-inventory.json`); the chosen workspace survives a reload; switching workspace and reloading keep the selected project (LendoPlus); an invalid stored workspace falls back to Manual Testing; a module outside the workspace opens, gets a temporary sidebar row and does not switch the workspace; Back/Forward; the three Manual Testing stages open from the shared header, highlight their sidebar row and keep the workspace; All modules search → open Release Readiness → Back → Back keeps the project; six rapid workspace switches end on the last choice without navigating; three rapid sidebar clicks end on the last page with one highlighted row and no lingering loading indicator; the selector is labelled and keyboard operable (arrow key moves to the next workspace); Tab from the search reaches a module link with a visible focus indicator; the turned-off module has no sidebar row in any workspace; collapsed sidebar workspace menu lists all eight; no horizontal overflow on Manual Testing and All modules at 1440, 1280, 1024, 768 and 390 px (screenshots `shots-nav/`).

Found and handled during acceptance (scripts only, no product change): the first run checked the project in a fresh browser profile where none had been chosen (now selected first), waited for the sidebar to be *visible* at 390 px where it sits behind the menu button (now waits until it exists), and assumed which workspace was selected before the arrow-key check. One console line in a run ("Failed to fetch frontend quality engine status") was the script leaving the page mid-request: the API log shows that request ended **499 (client closed)** while the other two returned 200; the API log has no errors.

Six frontend tests failed in the first full run because they rendered the sidebar with the default workspace and asserted the full catalog; they now select All Modules explicitly (as the other catalog-layout tests already did). No expectation was loosened.

Feature Visibility note: a module turned off is not listed in any sidebar and is not linked from All modules, but its address still opens — Feature Visibility hides navigation and is documented as "not access control"; this change keeps that behaviour.

## 18. Known limitations

- Technical Testing has no stage pages yet (queued task); its workspace lists the existing technical modules.
- Six enabled modules belong to no specialised workspace and are reached through All modules, search or their address: Demo Projects, Project Test Setup, Generated Documentation Health, Security Classification Review, the Skolenærvær extension and Workspace Transfer. Project Test Setup belongs in the testing workspaces; that membership is added with the Technical Testing workspace.
- Feature Visibility hides navigation only; a turned-off module's address still opens (unchanged, documented behaviour).
- "Test Management" is the former Test Lead workspace (same id, wider description and two Manual Testing rows); no new test-management pages were invented.

## 19. Local commits

- Starting HEAD `1f4dc6a0`; this work is one local commit on top (see `git log`). Nothing pushed.
- Not committed (other work, untouched): `appsettings.Local.json`, the Kode67 gap register, older audit drafts, `AIAssisted/testdata/`, and the in-progress Technical Testing workspace files (separate task).
- Temporary database (template copy, not dropped): `birknext_nav_acc_20261115`. No existing database was changed.
