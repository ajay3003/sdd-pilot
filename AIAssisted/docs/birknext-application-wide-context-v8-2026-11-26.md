# BirkNext — application-wide context (Project / Module / Specification), V8 (2026-11-26)

Commit: `8a384ad6`. The brief's "NO GIT COMMITS" line was overridden by the user's standing instruction to commit; nothing was pushed.

## A–B. Inventory and existing context (audited)
**There already is one canonical scope.** It is part of the workspace, saved by the workspace auto-save to the backend, so it survives refresh, browser restart and BirkNext restart. No localStorage is involved.

| Level | Canonical state | Written by | Read by |
|---|---|---|---|
| Project | `SddLifecycle.CurrentProjectImportId`, `CurrentApplicationId` (sample: `ProjectName`) | `ProjectSelector` → `ProjectSelection.SwitchAsync` (asks before discarding unsaved work, clears record query parameters); Project Import «Use as current» | `ArtifactExplorerContext.CurrentScope`, `ManualTestingWorkspace`, every page through the explorers |
| Module | `SddLifecycle.ModuleFocus`, one per project scope | `SpecificationScopePicker` / `ArtifactExplorerHost` → `SelectModuleAsync` | the explorers, Manual Testing |
| Specification | `SddLifecycle.PackFocus` (pack) and `ExplorerSelections` (document), per scope and module | `SelectPackAsync`, `Select` | the same |

- **Project change:** the page is re-created (`ProjectScopedContent @key`). Module and specification are keyed by project scope, so nothing leaks between projects.
- **Shared selectors:** `ProjectSelector` in `PageHeader`, `TestingWorkspaceHeader`, Source Management and others, plus `SpecificationScopePicker` (Manual Testing) and `ArtifactExplorerHost` (explorers). Both write the same state.

### Per-page scope contract

| Page | Project | Module / Specification | Needs a specification |
|---|---|---|---|
| Testoversikt | yes | picker (overview) | no (whole project allowed) |
| Finn og forstå | yes | picker | yes, for requirements |
| Planlegg og klargjør | yes | — | no (plan-scoped) |
| Testplaner | yes | — | **no — plans are project-scoped and span modules** |
| Specification Explorer | yes | explorer host | yes |
| Requirements Traceability | yes | via explorers | yes |
| Test Coverage | yes | label | optional |
| Source Management | yes | — | no |
| Dashboard | yes | label only | no |
| Test Data | read-only strip | — | no |

**Own domain contexts (kept; not the shared scope):**
- Delivery Overview/Reporting: the backend «Delivery project».
- Documentation Drift: import version, module and area as page filters.
- Generated Documentation: documentation lineage.

## C–D. Shared scope and identity
No second system was introduced. The identities are canonical:
- project = import or application id;
- module = module key;
- specification = pack key plus artifact id.

Display names and list positions are never used as identity.

## E–F. First use and dependent selectors
| Rule | Status |
|---|---|
| No project is chosen by itself | **IMPLEMENTED**. `ApplicationContexts.Ensure` used to make the application with the newest import (or the first) current; now only the current import's application is restored. Test: `ApplicationContextsTests`. |
| Labels «Prosjekt» / «Velg prosjekt …» | IMPLEMENTED (`ProjectSelector`) |
| Module empty until chosen («Velg modul …») | VERIFIED (browser B) |
| Specification visible and empty after the module («Velg spesifikasjon …») | VERIFIED (browser C, module with several packs) |
| A module change clears the specification | VERIFIED (browser H) |
| **A module with ONE specification pack resolves it without a choice** | **PARTIAL — deliberately kept.** See the decision under U. |
| A newly imported project becomes current | Kept: importing is an explicit act of the person. |

## G–H. Persistence and restoration — VERIFIED
The isolated browser (v1env23, M2LB) kept the chosen module and specification across:
- the four Manual Testing pages and Specification Explorer;
- a workspace switch;
- refresh;
- a **new browser context with no browser storage**, which shows the choice is stored on the backend.

Restoration validates against the current project's modules and packs: an unknown module or pack is ignored (existing `names.Contains` checks) and never replaced by another one.

## I. Race protection
Existing:
- `ArtifactExplorerHost` (`_loadVersion` plus scope check);
- Finn og forstå (`_loadGeneration`);
- Testoversikt (`_overviewGeneration`);
- Planlegg (`_planSeq`, from the P0 fix);
- the review-context rebuild coalescing (`RebuildAsync`) and `WhenCurrentAsync` (the performance pass).

**NOT VERIFIED:** sequencing in ArtifactTraceability, QualityReview, Dashboard, DocumentationDriftPanel and DeliveryOverview loads.

## J. Deep links
- Explicit scenario intent (`?scenario=`) wins over remembered selections; verified in the P0 report.
- `ProjectSelector` clears record query parameters on a project switch.
- Following a link never switches the workspace (V6).

## K. Manual Testing
- Plans stay project-scoped, across modules and specifications. Nothing restricts plan membership to the selected specification (Testplaner reads no specification).
- «Forbered testtilfelle» uses `?scenario=` (P0 fix `20e7cd94`).

## L–O. Other modules
They reuse the same project and module state through the explorers. Domain contexts (Delivery project, Documentation Drift import version, Generated Documentation lineage) stay independent by design. Their own «first/latest» defaults are domain defaults (for example the newest import version), not project, module or specification choices; listed under U.

## P. Permissions and isolation
- Unchanged.
- The backend validates plan and discovery ownership by project (P0 report).
- The UI scope is not authorization.

## Q. Performance
- No change in data loading; no claims.
- The shared state reads no extra module or specification lists at start-up.

## R. Automated tests
- Updated `ApplicationContextsTests`: without a current import, no application is current.
- Updated `ProjectSelectorTests`: first use shows «Prosjekt» / «Velg prosjekt …».
- Full frontend run with the strict single-pack variant: 42 explorer, traceability and review tests depend on the single-pack resolution (see U). With the variant committed: the affected classes pass (178/178 in the targeted run).

## S. Browser evidence (isolated v1env23; `scratchpad/v1/v1_v8scope.py`)
**Run 1 (first use after import):** 17/19.
- PASS: labels; module empty; specification visible and empty after a 5-pack module; navigation to Testoversikt, Testplaner, Planlegg and Specification Explorer; workspace switch; refresh; new browser context; module change clears the specification; 4 viewports without overflow; no console errors.
- The two failures were script faults: the first pack had no spec document of its own, and the page was read before it rendered.

**Run 2 (script fixed, same instance):** D (specification chosen: «Translate Source Code to English») and E (back on Finn og forstå with the same scope) pass. B and C «fail» only because run 1's choices were correctly remembered.

Screenshots: `scratchpad/v1/v1env23/shots/v8-*.png`.

## T. Changed files
- `Services/ProjectImport/ApplicationContexts.cs`
- `Components/ProjectSelector.razor`
- Tests: `ApplicationContextsTests`, `ProjectSelectorTests`

## U. Remaining issues and decisions needed
1. **Single-pack resolution (decision needed).** Removing it makes every module with one specification ask for it. That breaks a deliberate, tested contract in the explorers, traceability, implementation review and task alignment (42 tests). It was **kept**, so V8's "no automatic first specification" is PARTIAL for single-pack modules. Recommended if wanted: remove it together with an explorer-wide "Velg spesifikasjon" state, in its own task.
2. **A module whose specifications are module-level documents** (CdcReplay: 4 documents, no packs) showed state «chosen» right after the module change, without a «Dokument» choice. Probably an authoritative or shared-document resolution in `ArtifactExplorerContext`; NOT INVESTIGATED further.
3. **Clearing a project** (back to «Velg prosjekt»): NOT IMPLEMENTED (no clear action exists; samples and the Target Environment binding complicate it).
4. **Domain defaults outside the shared scope:** Documentation Drift newest import, Generated Documentation first module or lineage, Delivery first project, System Understanding first guide. Left as they are (domain-specific), listed for a later pass.

## V. Acceptance
| Criterion | Status |
|---|---|
| One canonical scope, no competing system | VERIFIED (audit; no new system) |
| No automatic project | IMPLEMENTED (tested) |
| Module appears after project, empty | VERIFIED |
| Specification appears after module, empty | VERIFIED (multi-pack); PARTIAL (single pack) |
| Persists across navigation, workspace, refresh, restart | VERIFIED |
| Module change clears the specification | VERIFIED |
| Test Plans project-scoped across modules | VERIFIED (code: no specification filter) |
| Explicit scenario intent wins | VERIFIED (P0) |
| Page filters independent | IMPLEMENTED (unchanged; page filters are separate state) |
