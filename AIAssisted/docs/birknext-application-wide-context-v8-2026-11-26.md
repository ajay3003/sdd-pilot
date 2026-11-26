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

---

# Final V8 decisions (2026-11-26, follow-up)

## Decision 1 — no automatic specification: IMPLEMENTED, VERIFIED
The changes in `Services/Explorers/ArtifactExplorerContext.cs`:

| Was automatic | Now |
|---|---|
| A module's only feature pack was viewed by itself | The pack (specification) is asked for, like several packs (`PackRequired`) |
| Modules or projects without packs: the only, the only authoritative or the only Spec-Kit specification was resolved | `Resolve` returns nothing for `Specification` unless there is an explicit choice |
| The repository's «current» document (set when an import settles its documents) counted as explicit | Not for a specification. Only a stored explorer choice (`ExplorerSelections`) or a Demo Project choice counts. |

**Kept, deliberately:** inside an explicitly chosen pack, its Spec-Kit `spec.md` loads. Choosing the pack is the choice. Other roles (constitution, plan, tasks, data model) keep their resolution.

Picker changes (`Components/SpecificationScopePicker.razor`):
- the specification selector is shown for a module with one pack;
- a single unchosen document is offered;
- labels «Spesifikasjon / Velg spesifikasjon …»;
- the module option says «1 spesifikasjon (N dokumenter)» instead of counting documents as specifications.

The explorer host's «Choose a feature» card already listed any number of packs; only its grammar was fixed.

**Restoring explicit choices:** a stored pack or document choice is restored per project and module. Browser D: CdcReplay → another module → back to CdcReplay keeps `001-translate-adr-029`. Deep links into Planlegg (`?scenario=`) are unchanged.

### The test impact
- 77 failures in 20 classes after the change, plus a few more once the repository-current rule was added.
- All updated to the contract:
  - **5 tests rewritten.** Each first asserts that nothing is chosen (`SelectionRequired`, `Selected == null`, `PackRequired`), then makes the choice and keeps the original assertions. Renamed:
    - `A_single_pack_is_never_selected_automatically_and_loads_once_chosen`
    - `MultipleArtifacts_OneAuthoritative_SpecificationStillRequiresChoice_OtherRolesOpenTheAuthoritativeOne`
    - `FocusedModule_WithOneCandidate_AsksForIt_ThenLoads_AndOtherModulesDoNotLeak`
    - `SimpleProject_StaysProjectLevel_AndItsSpecificationIsChosenExplicitly`
    - `A_single_feature_module_asks_for_its_spec_and_keeps_the_module_document_as_an_alternative`
  - **About 79 cases:** the fixture now makes the explicit choice a user would make (`SelectPackAsync`, `Select`, `ChooseDocument`, or a seeded `PackFocus`). Their assertions are unchanged; some gained a content assertion.
- No production code was changed to make a test pass. No test was deleted or skipped.

**Found while updating tests:** in a module with one pack and no pack viewed, choosing a module-level document (outside the feature folder) has no effect until the pack is chosen. The UI cannot reach that state (the picker and the explorer host ask for the pack first); the tests now choose the pack first. Listed under remaining issues.

## Decision 2 — CdcReplay: INVESTIGATED, FIXED, VERIFIED
**Actual layout** (M2LB.zip): `CdcReplay/specs/001-translate-adr-029/` is one Spec-Kit feature folder. It holds `spec.md`, `plan.md` and `tasks.md`, plus `baseline-counts.md`, `domain-term-inventory.md`, `verification.md` and others; there is also `CdcReplay/autodoc/*.md`. The documents are NOT outside feature folders.

**Root cause:**
1. Module and pack detection were correct: root `CdcReplay`, one pack.
2. The picker label counted specification-role **documents** («4 spesifikasjoner») for a module with one pack.
3. The only pack was viewed by itself, and its `spec.md` was then picked as the pack file. So it showed «chosen» with no action.

There were no duplicate identities and no stale navigation. Persisted preferences were not involved (it happened on first use).

**Fix:** Decision 1 plus the label «1 spesifikasjon (4 dokumenter)».

**Browser (v1env25, `v1_v8b.py`, 11/11 PASS):**
- the label;
- not chosen after the module choice (`state=feature`, «Velg spesifikasjonen du tester i CdcReplay»);
- one specification offered;
- explicit choice → `workspace:CdcReplay/specs/001-translate-adr-029/spec.md` loaded;
- the pack's specification documents listed with their own paths (Baseline Counts, Domain Term Inventory, Feature Specification …);
- MeldingsutvekslerReceiver (one pack) also asks;
- the choice is restored when coming back;
- no console errors.

**Tests:** `UnusualSourceLayoutTests`.
- One feature folder with several specification documents: nothing chosen until the pack; `spec.md` after it.
- Every document kept, with its own identity and module provenance; the import drops none.
- A module change never carries a specification over, and an explicit choice is restored.
- A project without modules and a single specification still asks.

## Scenario limit — FIXED
- **Measured:** M2LB has 2,015 candidates; the 2,000 limit dropped 15 API-contract scenarios. No specification scenario was dropped (1,467 kept).
- **Now:** `MaxCandidates` is 10,000, a safety limit. M2LB is kept whole (test `Below_the_limit_nothing_is_dropped`).
- **If the limit is ever reached:**
  - source-backed scenarios are kept first (test);
  - the result carries `NotKept`;
  - Finn og forstå shows «Ikke alle scenarioer er med: N scenarioer over sikkerhetsgrensen … Velg en modul og spesifikasjon for å se alle scenarioene der» **where the scenarios are listed** (tests).
  - Discovery is computed per selected module and specification, so a narrower scope reaches the rest.
- Coverage figures use the same discovery. With nothing cut there is no distortion; when something is cut, the page says so.
- **Browser:** the scoped discovery (CdcReplay) had 567 candidates and no limitations. The project-wide 2,015 was measured on the earlier instance; the project-wide browser run was NOT repeated with the new limit (covered by the backend test).

## Found and fixed during this work
The compact-wire commit (`b511146a`) had broken the **backend test build**: `LastDiscoveryRevalidationTests` called the endpoint without the new `compact` parameter. Only the API had been compile-checked. It is fixed, with a test that the compact form has its own ETag.

## Regression results
See the final section below.

## Final regression results (honest status)
| Suite | Result |
|---|---|
| Frontend, full | **9,865 / 9,865 passed**, 0 failed (`AuthenticatedTestingMethodTests` proxy test included; run while the backend suite was also running). The agent's earlier full run: 9,863 / 9,863. |
| Backend, full, under load (with publish and frontend runs) | 7,621 passed, **2 failed**, 20 skipped: `CI_DEF_3_the_connect_callback_lets_an_allowed_host_through` (36 s), `ObservedTrafficIsCorrelatedToItsPageByRefererEndToEnd` (19 s). Both pass alone (2 × 2 s). |
| Backend, full, quiet machine | **INCOMPLETE.** Claude Code stopped it because the system ran critically low on memory; not restarted. Before that, **CI_DEF_3 failed again**: «Expected Fetched, got Unreachable after 47.9 s; own request answered: True (RanToCompletion); threadpool threads=32 pending=1». |
| Targeted backend | `DiscoveryCapTests` and `LastDiscoveryRevalidationTests` 8/8; `SourceDrivenPlanningTests` 25/25; discovery/scenario filter 641 passed, 1 skipped |

**The backend suite is NOT green.** `CI_DEF_3` fails in full-suite runs, including on a quiet machine. Its own diagnostic shows the test server answered while the client timed out with a busy thread pool, which points to thread-pool starvation in the parallel backend suite rather than to the product code under test. NOT FIXED in this pass; recommended next: run that class non-parallel, or give its loopback server a dedicated thread, and re-verify with a full run.

## Processes
- Isolated instances v1env19–v1env25 are all stopped; ports 5097/5197/5399 are free.
- Still running and not started by this verification: the repository's own `dotnet run` BirkNext.Api and BirkNext.Web (normal development instance, ~850 MB). Left as is.

## Remaining issues
1. `CI_DEF_3` (and possibly `ObservedTrafficIsCorrelated…`) fail in full backend runs (above).
2. Single-pack module: a module-level document chosen before the pack has no effect. The UI always asks for the pack first, so it is unreachable there; worth a guard in `BuildInPack`.
3. The project-wide browser count with the new limit was not repeated (backend test covers it).
