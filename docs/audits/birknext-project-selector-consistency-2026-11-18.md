# Project selector consistency (2026-11-18)

Branch `008-traceability-first`, starting HEAD `7db017a3`. Local commits only; nothing pushed. Isolated Release instance (API :5097, web
:5197), disposable PostgreSQL databases, synthetic projects only; no external system contacted. `appsettings.Local.json`, `AIAssisted/testdata/`
and the Kode67 gap register were not touched.

## 1. Original defect

Manual Testing had no project dropdown. Its header showed the current project as text ("Project: X"), and the only way to change project
was Project Import → "Show this package" (or a Demo Project card). The same was true for every other project-scoped module: no module had a
project selector; a few had page-local pickers for something else (a package, a snapshot, a Delivery project).

## 2. Existing selector architecture (before)

| Concern | Where | Notes |
|---|---|---|
| Imported-project registry | `WorkspaceArtifactRepository.SddLifecycle`: `ProjectImports` (one record per imported package) grouped into `Applications` (`ApplicationContexts`) | Client-side, persisted with the workspace (server `workspace-persistence`) |
| Canonical identity | Import id → scope `import:{importId}` (`ProjectImportScope`); application id `app-…`; a Demo Project uses its slug | |
| What every page reads | `IArtifactExplorerContext.CurrentScope` | Precedence: selected Demo Project slug → explicit Explorer single source → active Target Environment's project (same application only) → current import |
| Switching | `ProjectImportActivation.UseAsCurrent(importId)` / `UseApplication(appId)` (no UI caller) | Followed by `IWorkspaceAutoSaveService.SaveNowAsync()` on Project Import |
| Broadcast | `WorkspaceArtifactRepository.ArtifactsChanged/ProjectSelectionChanged` → `IArtifactExplorerContext.Changed` → `ICurrentWorkspaceProjection.Changed` | |
| Restoration | `MainLayout` reads `api/workspace-persistence/current-state` at start-up and restores the lifecycle | "Loading the selected project…" in `TestingWorkspaceHeader` while the first snapshot is computed |
| Deep links | No route takes an import or application id to switch the global project; `?project=` exists only on multi-project pages (Delivery, Contract Testing, Test Execution & Evidence) with their own project concepts | |

There was one selection *state* (good) but no selection *control*.

## 3. Canonical imported-project source

The dropdown lists the workspace lifecycle's **applications** that have at least one import (one entry however many packages — source,
documentation, contracts — the application has), plus legacy imports without an application. It never lists folders and never creates a
record. A selected Demo Project is shown first (so the control never misstates what the workspace shows); other Demo Projects are chosen on
the Demo Projects page. Duplicate names get "— imported yyyy-MM-dd HH:mm"; long names are kept whole and ellipsised by CSS.

## 4. Module inventory and coverage matrix

Source: `Services/NavigationCatalog.cs` — **66 modules** in 10 sections (verified: every route has a page). Classification: **Yes** =
reads the current project; **Env** = reads the active Target Environment; **Multi** = its own or several projects; **Global** = neither.
"Reloads on switch" = before this change (the change re-creates the page on a switch, so every project-scoped page now reloads).

| # | Module | Section | Scoped | Selector before | Reloaded on switch before | Action |
|---|---|---|---|---|---|---|
| 1 | Project Quality (dashboard) | Workspace | Yes | none | yes | Add selector |
| 2 | All modules | Workspace | Global | none | — | Not applicable |
| 3 | Recommended Workflow | Workspace | Yes | display only | yes | Add selector |
| 4 | System Understanding | Workspace | Multi (guides) | local guide | — | Keep (own concept) |
| 5 | Import Project | Workspace | Yes | "Show this package" (global) | yes | Keep — it is the project management page; no second control |
| 6 | Source Management | Workspace | Yes | none | **no** | Add selector |
| 7 | Demo Projects | Workspace | Global catalogue | card Select (global) | — | Keep |
| 8 | Project Test Setup | Workspace | Yes | none | yes | Add selector |
| 9 | Target Environments | Workspace | Global (env admin) | none | — | Not applicable |
| 10–12 | Delivery Snapshots / Overview / Reports | Delivery | Multi (Delivery projects, `?project=`) | local | — | Keep multi-project semantics |
| 13–17 | Specification / Constitution / Data Model / Plan / Task Explorer | Specs & Planning | Yes | Explorer source/module/pack (package within project) | yes | Add project selector; keep source selectors |
| 18 | Document Quality Review | Specs & Planning | Yes | none | yes | Add selector |
| 19 | Task Alignment Review | Specs & Planning | Yes | none | yes | Add selector |
| 20 | Documentation Drift | Specs & Planning | Yes | local import pickers | **no** | Add selector (page now reloads on switch) |
| 21 | Requirements Traceability | Traceability | Yes | none | yes | Add selector |
| 22 | Change Traceability | Traceability | Global (ADO work items) | none | — | Not applicable |
| 23 | Impact Analysis | Traceability | Yes | snapshot pair (local) | **no** | Add selector |
| 24 | Relationship Review | Traceability | Env | none | — | Not applicable (environment) |
| 25 | Release Readiness | Traceability | Env | local module scope | — | Not applicable (environment) |
| 26 | Snapshots | Traceability | Global | text filter | — | Not applicable |
| 27 | Traceability & Coverage | Traceability | Yes | none | yes | Add selector |
| 28 | Traceability Suggestions | Traceability | Yes | none | **no** | Add selector |
| 29 | Code Traceability | Traceability | Yes | none | yes | Add selector |
| 30 | Source Analysis | Source & Architecture | Yes | source snapshot (local) | **no** | Add selector |
| 31 | Schema Studio | Source & Architecture | Yes | none for project | **no** | Add selector |
| 32 | Generated Documentation Explorer | Source & Architecture | Yes | package (local) | **no** | Add selector; keep package picker |
| 33 | Generated Documentation Health | Source & Architecture | Yes | lineage (local) | yes | Add selector; keep lineage picker |
| 34 | Technology Coverage | Source & Architecture | Yes | filters | partial | Add selector |
| 35–37 | Dependency Review / Pipeline Review / Environment Analysis | Source & Architecture | Env | snapshot (local) | — | Not applicable (environment) |
| 38 | AI Change Review | Source & Architecture | Yes | none | read on run | Add selector |
| 39–42 | Manual Testing home / Discover / Plan & prepare / Execute | Manual Testing | Yes | display only | yes | **Add selector** (workspace header; Plan & prepare's context bar) |
| 43–47 | Technical Testing home / Assess / Configure / Verify / Integration Testing | Technical Testing | Yes | display only | yes | Add selector (workspace header) |
| 48 | Test Coverage & Overlap Review | Quality & Testing | Yes | snapshot pair (local) | **no** | Add selector |
| 49 | Code Risk Review | Quality & Testing | Yes | snapshot pair (local) | **no** | Add selector |
| 50 | Test Data | Quality & Testing | Env | none | — | Not applicable (environment) |
| 51 | Contract Testing | Quality & Testing | Multi (`?project=` contract keys) | local "All projects" | — | Keep multi-project semantics |
| 52 | Integration Test Suites | Quality & Testing | Yes | scope (local) | **no** | Add selector |
| 53 | Test Execution & Evidence | Quality & Testing | Multi (`?project=`) | local | — | Keep |
| 54 | Parquet Quality Review | Quality & Testing | Multi (filter) | local | — | Keep |
| 55–58 | Frontend / API Quality Review, Integration Topology, Integration Quality Review | Quality & Testing | Env | local subject pickers | — | Not applicable (environment) |
| 59 | Integration Journeys | Quality & Testing | Yes | none | **no** | Add selector |
| 60–61 | Performance Test Review, Critical E2E Regression | Quality & Testing | Env | local | — | Not applicable (environment) |
| 62–63 | M01 Kode 6/7, Skolenærvær Testing | Project Extensions | Env | local | — | Not applicable (environment) |
| 64–65 | User Guide, System Settings | Help & Settings | Global | none | — | Not applicable |
| 66 | Backup & Transfer | Help & Settings | Multi (export choice) | local | — | Keep |

Counts: **37 project-scoped** (36 now show the shared selector; Import Project manages projects itself), **14 environment-scoped**, **8
multi-project**, **7 global**. Before: **0** project selectors; **11** project-scoped pages read the project only once and would have shown the
previous project's data after a switch (marked **no**).

## 5. Missing selectors

All 36 listed "Add selector" rows. Environment-scoped modules follow the active Target Environment (whose project binding is shown and
managed in Target Environments); a project list there would change a label, not the data, so none was added.

## 6. Duplicate selectors

None of the existing page-local pickers selects the workspace project: they choose a package, snapshot, lineage, guide or a different
project concept (Delivery, contract keys). They stay, under the shared project selector, with their own meaning. No page has two project
selectors: Plan & prepare shows the selector in its context bar and hides the header's project line while a project is loaded.

## 7. Shared selection semantics

* One mechanism: `ProjectSelection` (`Services/ProjectImport/ProjectSelection.cs`) reads the registry and switches through
  `ProjectImportActivation.UseApplication` (primary documents of the application, as after an import) — the same activation Project Import uses —
  then saves with the workspace auto-save. No second selection state, no browser-only persistence.
* One control: `Components/ProjectSelector.razor`, used by `TestingWorkspaceHeader`, `PageHeader` (`ShowProjectSelector`) and five pages with
  their own heading.
* A switch clears an Explorer single-source choice of another application (otherwise `CurrentScope` would keep the old project).
* The page is re-created after a switch (`MainLayout` keys the page content on `ProjectSelection.Generation`): every module — including the
  11 that read the project once — reloads for the new project, so the new name is never shown above the old project's data and a late
  response of the old page cannot land in the new one (the old component is gone). Only switches made with the selector re-create the page;
  an import completing on Project Import does not.

## 8. Precedence and deep links

`CurrentScope` precedence is unchanged: selected Demo Project → explicit Explorer single source → active Target Environment's project (same
application) → current import. Choosing an application leaves a Demo Project and clears a foreign single source, so the choice wins. No
route takes a project in its URL today (the `?project=` parameters belong to multi-project pages); deep links open the saved current project,
which is restored before the selector offers a choice ("Loading the selected project…", never a wrong default). No query parameter was
added that a destination would ignore.

## 9. Unsaved-change behaviour

`UnsavedWorkGuard` (scoped): Plan & prepare (unsaved plan), Execute & document (unsaved run) and Declared Integration Journeys (unsaved
declaration) register what would be lost — the same wording as their NavigationLock. A switch asks "… Switch project and discard them?";
Cancel keeps the project and the work and the control shows the project still in use.

## 10. Implementation

| File | Change |
|---|---|
| `Services/ProjectImport/ProjectSelection.cs` | New: choices, current value, switch, generation/notice; `UnsavedWorkGuard` |
| `Components/ProjectSelector.razor` (+ `.css`) | New shared control: label, select, manage link, restoring/empty/switching states |
| `Components/ProjectScopedContent.razor`, `Layout/MainLayout.razor` | Page re-created on a selector switch |
| `Components/TestingWorkspaceHeader.razor` | Project line is the selector (text fallback where no selection service exists) |
| `Shared/Components/Layout/PageHeader.razor`, `wwwroot/css/components.css` | `ShowProjectSelector` option |
| 22 pages with `PageHeader`, 5 with their own heading | Selector enabled (matrix §4) |
| `Pages/TestPlanSuggestions.razor`, `ManualTestingExecute.razor`, `DeclaredIntegrationJourneys.razor` | Selector in the context bar; unsaved-work guard |
| `Program.cs` | `ProjectSelection`, `UnsavedWorkGuard` registered |
| `Components/Guide/GuideGettingStarted.razor`, `Services/UserGuideContent.cs` | User Guide: "Choosing the project" |

## 11. Browser acceptance

Fresh Release publish (tag 50: API and web exit 0, 0 build errors; web asset integrity check: the one known pre-existing "missing hash"
note), disposable database `birknext_psel6_20261118`, API :5097 / web :5197, repository network guard on every request. Two synthetic Spec
Kit projects (PselAlpha: library loans; PselBravo: parking permits) imported through the real Project Import page. Script
`acc/psel-accept.cjs` (scratchpad).

**Result: 43 of 44 checks passed.**

| Area | Checks | Result |
|---|---|---|
| Import both projects; selector lists both (plus the template's own project); accessible "Project" label | 4 | pass |
| Manual Testing: choose A → home, Discover (A's evidence, none of B), Plan & prepare (A's scenarios), Execute & document keep A | 5 | pass |
| Switch to B from Execute: Plan & prepare, Discover show B's scenarios and evidence, none of A | 4 | pass |
| Back to A: A's data intact; reload restores A; a new tab on a direct link resolves A | 3 | pass |
| Unsaved plan: switch asks "This plan has unsaved changes. Switch project and discard them?"; Cancel keeps A and the edit; confirm switches | 4 | pass |
| Selector present with the current project on all 36 project-scoped routes | 1 | pass |
| Switch on Specification Explorer, Traceability & Coverage, Source Analysis, Integration Test Suites takes effect | 4 | pass |
| No selector on All modules, User Guide, Demo Projects, Delivery Reports, Integration Quality Review, Frontend Quality Review, Project Import | 7 | pass |
| 1440 / 1280 / 1024 / 768 / 390 px: selector visible, ≥ 120 px wide, inside the viewport, no horizontal page overflow, keyboard-focusable | 10 | pass |
| No prohibited network destination | 1 | pass |
| No console errors | 1 | **fail** — one 404 from Integration Quality Review's existing `api/integration-review/contract-relationship` for a discovered integration of the template data (environment page visited only to confirm it has no selector; backend unchanged by this task) |

Found and fixed during acceptance:

* The selector stayed at "Loading the selected project…": the first workspace snapshot raises no Changed event and a parameterless child is
  not re-rendered with its page. The selector now awaits the snapshot itself (bUnit regression test).
* After a switch, Plan & prepare kept `?draft=` of the previous project's plan; Execute keeps `?run=`/`?case=`. A switch now drops
  record parameters (draft, run, case, snapshot, import, scope, journey, suite, plan, requirement, baseline, candidate) and keeps view
  parameters (area, tab, section, view).
* Plan & prepare showed the selector twice for a moment (header while loading + context bar); the header shows it only when no project
  is loaded after loading.

## 12. Regression

On final code:

| Run | Passed | Failed | Skipped |
|---|---|---|---|
| Focused: `ProjectSelectionTests`, `ProjectSelectorTests`, header, Plan & prepare, Target Environments tests | 45 + 204 | 0 | 0 |
| Full frontend suite | 9270 | 0 | 0 |
| Backend partitions 1–6 (sequential, in-repo build) | 1052 / 1101 / 904 / 682 / 541 / 2684 = 6964 | 0 | 2 / 5 / 0 / 1 / 3 / 8 = 19 |
| Playwright VerifyPre: workflows (Manual, Technical, Integration, Recommended, Navigation), Quality Review, System Settings | 14 | 2 | 0 |
| — rerun of the 2, Lighthouse runner dependencies restored on the instance (twice) | 2 + 2 | 0 | 0 |
| API + Web Release publish (tag 50), asset integrity | exit 0 | — | — |
| Browser acceptance (§11) | 43 | 1 (pre-existing IQR 404) | 0 |

The two Playwright failures: `FrontendQualityReview_RealEnginesReachDecisionSupportExactlyOnce` — the tag-50 instance lacked the Lighthouse
runner's npm dependencies (environment prerequisite, see the recovery audit); passes with them. `FrontendQualityReview_KeyboardNavigationToTargetEnvironments`
— Enter on "Change target" did not navigate within 10 s once; passed in both reruns (intermittent; Frontend Quality Review has no selector).
Existing Playwright workflow tests now assert the **selected** project (`SyntheticProjects.ExpectSelectedProjectAsync`), not text that merely
contains the name. Backend code is unchanged by this task; the backend run confirms nothing else moved.

## 13. Remaining limitations

* No URL parameter selects a project: a link opens the saved current project. Adding one would need every destination to honour it.
* The Explorer single-source and the Target Environment binding keep their precedence inside an application (unchanged by design).
* Project isolation is server-side as before; the selector is a client control and changes no authorization (backend unchanged).
* Environment-scoped modules have no project list; their project comes from the active Target Environment's binding.

## 14. Local commits

One local commit on `008-traceability-first` (this document included); nothing pushed. Starting HEAD `7db017a3`.
