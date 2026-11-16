# BirkNext — Feature portfolio audit and consolidation (2026-11-15)

Branch `008-traceability-first`. Companion documents: **feature ownership matrix** (`birknext-feature-ownership-matrix-2026-11-15.md`, the durable reference for where a feature belongs), **overlap register** (`birknext-feature-overlap-register-2026-11-15.md`), **consolidation roadmap** (`birknext-feature-consolidation-roadmap-2026-11-15.md`).

## 1. Method

The inventory is not hand-written: it is read from the running Release build's **All modules** page (which renders `NavigationCatalog`/`NavigationModules`, the single module registry) by browser acceptance, with each module's area, Feature Visibility state and the workspaces whose sidebar lists it (`birknext-feature-inventory-2026-11-15.json`). Every route of the frontend is either a registry module or a documented contextual route (`FeatureVisibilityTaxonomy.ContextualRoutes`, enforced by the inventory test). Overlap was judged from code — inputs, rules, persistence and outputs — not from names; every substantive finding cites file and line.

## 2. Workspaces

Eight: Manual Testing (default), Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect, All Modules (last). Membership counts and the module-by-workspace table are in the ownership matrix. Workspace membership is navigation only, never authorization; every enabled module is reachable from All modules.

**Inventory (Release build, browser):** 66 registry modules, 60 enabled by default, in 10 catalog areas (quality & testing 14, getting started 9, traceability 9, source review 9, document review 8, Technical Testing workspace 5, Manual Testing workspace 4, delivery reporting 3, administration 3, extensions 2). Workspace sidebars list: Technical Testing 30, Test Management 27, Architect 22, Developer 21, Manual Testing 20, Project Manager 15, Scrum Master 13, All Modules 60 (every enabled module). Every enabled module opened at its route in the browser.

## 3. Findings (summary — details in the register)

- **No duplicate engine in Manual or Technical Testing.** Manual Testing's Plan & prepare *is* Test Plan Suggestions (one page); Test Coverage and Requirements Traceability share `PreparedCaseTraceability`; Technical Testing reads the specialist stores and runs nothing. The integration modules own different entities (derived topology, configured integrations, declared journeys, suites, IQR runs) — the problem there was **missing integration**, addressed by the Integration Testing entry (separate commit).
- **Broken handoffs (fixed):** two links sent query parameters their target never reads. A third suspected one (Project Test Setup → Implementation Review `?scope=`) was disproved: the embedded gap explorer's scope filter reads it.
- **Conflicting interpretation (2 fixed, 4 deferred):** requirement-id grammar differed in the NUnit/MSTest fallback (so `FR-1` in those tests never linked to `FR-001`) and in Documentation Drift — both now use the shared `RequirementReferenceParser`. Deferred: two "requirement coverage" populations, Task Delta grammar, Task Alignment naming, project-key vs environment scope.
- **Duplicate implementation / authoritative data (deferred):** a legacy traceability store behind Impact Analysis and AI Change Review (both off by default), two "implemented?" verdicts on Implementation Review, unreferenced specification view builders, a second writer of Target Environment profiles (Demo Projects), the package importer writing other owners' tables.
- **Navigation disposition:** two enabled modules had no workspace (Demo Projects, Generated Documentation Health) — now placed; the three remaining All-modules-only modules are explicit by design (two project extensions, Backup & Transfer) and pinned by a test.

## 4. Decisions (per module: ownership matrix, column "Disposition")

KEEP for distinct workflows; REUSE for shared capabilities (Test Data, Project Test Setup, Technology Coverage, Target Environments); INTEGRATE for the integration modules (one entry, handoffs by id); MOVE for the two modules given a workspace; INVESTIGATE where evidence shows a second store or verdict whose consolidation needs its own migration (roadmap R1–R3, R6, R8–R9). Nothing was retired: no module was found redundant enough to remove safely without a migration, and none was deleted.

## 5. Implemented changes

1. Requirement-id grammar: `TestBehaviorSourceAnalyzer` fallback (NUnit/MSTest) and Documentation Drift's identity key use `RequirementReferenceParser` (normalised ids, same upper-case source rule as xUnit discovery).
2. Handoffs: Functional Quality Scope → Test Coverage without the ignored `?q=`; → Snapshots `?type=Delivery` instead of the ignored `?module=`.
3. Workspace homes: Demo Projects (Manual Testing *Project setup*, Developer *Source*); Generated Documentation Health (Architect next to the explorer, Developer *Code quality & build*).
4. A navigation-disposition test: every registry module is in at least one specialised workspace or on an explicit, reasoned All-modules-only list; disabled-by-default modules are listed as such. The browser acceptance checks the same from the rendered sidebars (P1–P3).
5. Test reliability (found during this verification): `ManualRunPersistencePostgresTests` compares allocations with the process-wide `GC.GetTotalAllocatedBytes`, so tests running in parallel inflated it (one failure in the Integration Testing run; the class passed 11/11 alone). The class now runs in a non-parallel collection; the assertion is unchanged.

Preserved: every route and bookmark; all stored records, revisions and decisions; Critical E2E Policy 2; Feature Visibility settings; specialist pages and their depth.

## 6. Results

One source tree (this commit), Release publish from a clean copy, fresh disposable database `birknext_pf_20261116` from template `birknext_pts_acc_20261115`, isolated ports 5097/5197.

| Check | Result |
|---|---|
| Frontend build (non-incremental) | 0 errors |
| Frontend suite | **9,139 / 9,139 passed** (new: navigation disposition, Documentation Drift identity, Functional Quality Scope links) |
| Backend build (in-repo `bin/Verify/net8.0`; the user's API on :5000 locks `bin/Debug`) | 0 errors |
| Backend suite, 6 partitions | 1,052 + 1,099 + 888 + 676 + 541 + 2,594 = **6,850 passed**, 19 skipped, 0 failed (new: fallback requirement grammar). Partition 6 first ran while Windows was in modern standby (Kernel-Power 506/507, 02:36–05:13): 4 failures from dropped database connections and expired time windows after 2 h 37 min; rerun on the same build: 2,594 passed, 0 failed |
| Release publish (API + Web) | exit 0, 0 errors |
| Browser: navigation and inventory (`nav-accept`) | **41 / 41** — incl. P1 Demo Projects in Manual Testing and Developer, P2 Generated Documentation Health in Architect and Developer, P3 every enabled module has a workspace home or is All-modules-only by design; every enabled module opened at its route |
| Browser: search, open and return (`nav-accept2`) | **32 / 32** |
| Browser: project setup regression (`utps-accept`) | **106 / 106** |
| Browser: Integration Testing (`itx-accept`) | **42 / 42** |
| Browser: Technical Testing (`tt-accept`) / stage navigation (`ttnav-accept`) | **76 / 76** / **48 / 48** |
| Page errors / 5xx / guard (non-allow-listed requests) | none |
| Console errors | 2, both a request cut off by the script's own full page load: `nav-accept` leaves each module after 2.5 s while Frontend Quality Review's engine-status call takes ~16 s, and `tt-accept` once left the Dashboard during its workflow-steps call. A probe that stays on Frontend Quality Review: status 200 after 16.4 s, no console error. The 16 s status call is pre-existing slowness (backlog) |

Acceptance fixtures: the first `nav-accept`/`nav-accept2` run selected a project ("LendoPlus") that this template database does not hold (4 failures); the scripts now select M2LB, which it does, and wait for the workspace projection before reading the project header (a larger project loads after the 1.5 s the scripts allowed). The assertions are unchanged.

## 7. Remaining gaps

See the roadmap (R1–R12). The highest-value next items: one requirement population for coverage numbers (R1), retiring the legacy traceability store (R2), import through owning services (R9).

## 8. Git

One local commit on `008-traceability-first` (this document set, the inventory JSON, code and tests); not pushed. Earlier today: Unified Integration Testing `b0430dae` (register rows 1–3). Unrelated working-tree changes (`appsettings.Local.json`, `AIAssisted/testdata/`, the Kode67 gap register, other audit drafts) are left untouched and uncommitted.
