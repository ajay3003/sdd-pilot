# BirkNext — «Prosjektnivå» and module classification audit (2026-11-27)

The brief says no commits: the changes are uncommitted in the working tree. All data came from an isolated instance (`v1env34`, a disposable database) with the real M2LB package; nothing normal was read or written.

## 1. What «Prosjektnivå» meant (confirmed, before any change)
- It was **not a domain entity** and not a stored classification. It was a **display name produced by a path rule** in two places: `TestPlanSuggestionService.ModuleOf` (Testoversikt) and `PlanStructure.ModuleOf` (shared).
- The rule: a document is in module X only if its path is `X/specs/<feature>/…`. **Every other path** became «Prosjektnivå».
- BirkNext already had a **canonical** module resolver, `ArtifactModules` (explorers, Import Project, Documentation Drift):
  - A module root is a folder with Spec-Kit structure, and a document belongs to the deepest root it lies under.
  - Only documents under no root are project level.
- Testoversikt did **not** use the canonical resolver.

## 2. Where the «Prosjektnivå» scenarios came from
Measured with the real M2LB package on the isolated instance, via `GET project/overview`. M2LB differs from the tester's «M2LB-language» package, so the totals differ from the screenshot (100); the composition is what matters.

«Prosjektnivå» held **239 entries from 21 documents**: 168 requirements without an acceptance scenario, and 8 documented tests.

| Group | Documents | Entries | Correct category |
|---|---|---|---|
| Project root `specs/<feature>/spec.md` (Spec-Kit, project-wide features: platform contract rename, operation display name, BirkId partial search, cross-service contract translation) + their supporting files | 6 | 139 | **Project-wide**: explicitly the project's own features |
| `HendelseService/docs/…`, `PersonAdapter/docs/…`, `PersonService/docs/…`, `RevisjonService/docs/…`, `SkoleService/docs/…`, `TjenesteService/docs/…` (requirement documents, constitutions) | 8 | 50 | **The module**: the documents lie in the module's folder |
| `HendelseService/docs/…Testspesifikasjon.md`, `RevisjonService/docs/…Test-Specification.md` (documented TEST-E/TEST-I tests without a user story) | 2 | 8 | **The module** |
| `docs/analysis/…`, `docs/operations/…` | 5 | 42 | **No module association**: outside every module folder |

So «Prosjektnivå» combined three different things:
- explicitly project-wide specifications;
- module documents misattributed by the simple rule (**a functional defect**);
- documents with no module association.

It did **not** contain «cross-module / E2E scenarios» as such. The documented E2E tests in it belonged to a module.

## 3. Other findings
- **«Scenarioer» per module include requirements without an acceptance scenario.** For example, AutorisasjonService's 93 = 40 acceptance scenarios + 53 requirements; Frontend's 225 = 112 + 113. This is the existing KPI definition (requirements that still need a scenario). It was not changed in this audit. **Decided afterwards (2026-11-27, decision 3):** the column now counts acceptance scenarios only, and «Krav uten scenario» is a separate column (see §6).
- **Requirement identities are not specification-scoped (open finding).**
  - A requirement without a scenario gets `SourceIdentity = hash("requirement:{module}|{id}")`; in the overview the module is empty, so for example every `FR-001` gets the same identity.
  - M2LB: **66 identities occur in more than one document, up to 39 documents.**
  - Discovery scopes the *candidate id* on collision, but by design never the source identity.
  - **Impact checked:** counts are summed per document (not deduplicated), so nothing is lost or double-counted in the module table, and a drill-down works within one specification's discovery.
  - **Risk:** anything keyed by the source identity across specifications (preparation decisions are stored per document, so they are not affected) could mix them.
  - Changing stored identities was out of scope; it needs a migration decision.
- **The same TEST-E display ids in two modules** (`TEST-E-01…03` in HendelseService and RevisjonService) are different scenarios with different source identities (`testspec:<document>#TEST-E-01`). The identity is not inferred from the display id.
- The «Prosjektnivå» row was **never a link**: the drill-down only offered modules the explorer lists, and the project level has an empty key.

## 4. Changes (uncommitted)
| File | Change |
|---|---|
| `shared/ModuleStructure.cs` (new, linked into Api and Web) | The canonical module rule (`StrongRoot`, `RootsOf`, `ModuleOf`), moved from the explorers so there is **one implementation**. `OverviewModule`: the module root, else **«Prosjektnivå»** for the project root's own `specs/`, else **«Uten modultilknytning»**. `WhyOverviewModule`: the reason in Norwegian. |
| `frontend/…/Explorers/ArtifactModules.cs` | Delegates `RootsOf`/`StrongRoot`/`ModuleOf`/`Segments` to the shared rule (behaviour unchanged). |
| `backend/…/TestPlanSuggestionService.Overview.cs` | Testoversikt uses the canonical rule for every specification, documented test and case. Module roots come from the catalog's documents, the current discovery's specifications and a case's own path. |
| `frontend/…/ManualTestingOverviewPresentation.cs`, `Pages/ManualTesting.razor` | «Oversikt per modul»: real modules first. Below them, a separate section **«Utenfor modulene»** with «Prosjektnivå» and «Uten modultilknytning», each with a one-line explanation. Sorting orders the real modules and the section stays below. Both rows open the explorers' project level (drill-down). |
| `frontend/…/ManualTestingDrillNavigation.cs` | `ExplorerModule`: both groups map to the explorers' project level. |

Not changed:
- source data, identities, plans, cases and execution history;
- `PlanStructure.ModuleOf`, used by the plan structure's «module» grouping and the «Legg til scenarioer» module filter. It sees only Spec-Kit specification paths, where both rules agree; documented tests use their area there.

## 5. Tests
| Test | Covers (brief numbering) | Result |
|---|---|---|
| `ModuleStructureTests` (real M2LB paths) | 1 genuine module; 2 explicit project-wide (+ its supporting file); 3 no module association; 4 nested module / a file name never decides; 5 TEST-E without a user story → its module; 10 every document in exactly one group | pass |
| `SourceDrivenPlanningTests.PLA_a_modules_documented_tests…` | 5 + 9 + 10: TEST-E document → HendelseService, identity kept, counted once | pass |
| Existing overview tests (`The_overview_attributes_each_case…`, user story status, overview classes) | 6 dashboard counts unchanged for real modules | pass (23 / 23 in the run) |
| `ManualTestingWorkspaceTests.V04_…` | sorting with the separate section (updated: sorting orders the real modules; the outside section stays below) | pass (43 / 43 in the class) |

**NOT VERIFIED in a browser yet:** the «Utenfor modulene» section, its drill-down and module filtering in Finn og forstå (7, 8). These go into the dashboard redesign's browser run (UX-107–111), which touches the same page.

## 6. Open questions
1. ~~Should «Scenarioer» keep including requirements without an acceptance scenario?~~ **Decided 2026-11-27 and IMPLEMENTED.**
   - «Scenarioer», «Med testtilfeller» and the coverage chart (with its drill-down) count acceptance scenarios and documented tests only.
   - Requirements are kept and shown in a new column, «Krav uten scenario».
   - Test: `DEC3_requirements_without_a_scenario…`.
2. Specification-scoped identities for requirements without a scenario: **REQUIRES DECISION.** The proposal is in `birknext-requirement-identity-migration-proposal-2026-11-27.md`. Nothing was migrated.
3. A cross-module scenario (one acceptance scenario touching several modules) has no representation in the source model. Only its document's folder decides, and that was kept.
