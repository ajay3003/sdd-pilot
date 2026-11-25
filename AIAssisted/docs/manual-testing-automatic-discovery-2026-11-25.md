# Manual Testing: automatic scenario loading, scoped discovery, default grouping, «Fra kilden» collapsed (2026-11-25)

Branch `008-traceability-first`. Builds on `66b28373`. Committed with this report at the user's request («COMMIT ALL AFTER DONE»); nothing pushed. It covers three briefs: automatic scenario loading (the blocker), «Brukerhistorier» as default grouping, and «Fra kilden (ordrett)» collapsed by default.

## A. Initial repository state

- Clean tree at `66b28373`. No other Claude session (`ListAgents`).
- Your own BirkNext API was running from `backend/BirkNext.Api/bin/Debug` (pid 33924). It was left alone; builds went to separate output folders.

## B. Existing selection architecture

- **Project:** `ICurrentWorkspaceProjection` (`ProjectSlug`).
- **Module, feature (pack), specification:** `IArtifactExplorerContext` → `ArtifactExplorerState` (`Module`, `Pack`, `Selected`), chosen in `SpecificationScopePicker`.
- **Shared loader:** `ManualTestingWorkspace.LoadAsync` serves both pages. It reads the project, the specification evidence (`TestCoveragePresentation.WorkspaceEvidence(…, specification)` → `ArtifactScope` + `SpecificationArtifacts`) and the **last discovery** (`LastDiscoveryAsync(project, snapshot)`).
- **Discovery** (`TestScenarioDiscoveryService.DiscoverAsync`) computes from the request's workspace evidence: the selected specification, or **every** specification of the project when no feature is chosen (`ScenarioSourceBasisResolver`). It stores the result in the cache and in `scenario_discovery_results` (the newest five per project).

## C. Root cause of the stale selection (proven in code)

1. **Stored and returned by project only.** Results were kept and returned only per project, and per project + snapshot: `ScenarioDiscoveryCache.Latest(project[, snapshot])`. Nothing tied a result to the module or specification it was computed from.
2. **Never recomputed.** The page did not compute a discovery for the new selection. `OnScopeChangedAsync` only reloaded the workspace, and the code said so: «Discovery is not run automatically — the scenarios shown stay those of the last discovery until the tester asks again».
3. **The result:** after SkoleService → HendelseAdapter, `LastDiscoveryAsync` returned SkoleService's discovery, and both pages showed its stories and scenarios. At most, «Finn og forstå» showed a «stale» notice asking for «Oppdag på nytt». «Planlegg og klargjør» takes its technical context from the same discovery.

Loads were already numbered against races (`_loadGeneration`); the discovery itself was not.

## D. Slow feature dropdown

- **In code:** every module, feature or specification change calls `ReviewContextProvider.RebuildAsync()` (a full rebuild of the review context in the browser) before the new state is read. During that time the selects were disabled **with no feedback** (`SpecificationScopePicker.ApplyAsync`).
- **Fixed:** a spinner «Henter spesifikasjoner …» now shows beside the dropdowns while that runs.
- **NOT MEASURED:** the rebuild's cost on your M2LB workspace. The isolated test data has a handful of specifications, so any number would be invented. The rebuild is the root-cause candidate; making it incremental is a separate change.
- The feature list itself comes from the already-resolved explorer state (no archive reparse, no discovery to fill the list).

## E. «Oppdag» / «Oppdag på nytt» audit

- Both were the same action: `ManualTestingDiscover.DiscoverAsync` → `POST /discover`.
- The server computes candidates from the project's sources and the selected specification, caches the result and stores it as **derived** data (`scenario_discovery_results`, the newest five per project; older derived rows are pruned).
- It does **not** write plans, test cases, review decisions, groupings or source snapshots, and it does not reimport anything. Automating it is therefore non-destructive.

## F. Automatic loading design

- **Scope identity.** `DiscoveryScope.Of(workspace evidence)` (`shared/TestScenarioDiscoveryContracts.cs`) = module scope `|` selected specification path, or `*` when no feature is chosen. It is derived from the **same evidence the request sends**; no second identity engine.
- **Server:**
  - Every result is stamped `SelectionScope`, and the scope is part of its `DiscoveryId` (two modules with identical scenarios are still two results).
  - `ScenarioDiscoveryCache.RememberScoped` / `LatestForScope`, `ScenarioDiscoveryStore.FindScopedAsync`, `LastAsync(…, scope)` and `GET discover/last?scope=` return only a result computed for exactly that selection.
  - Results stored before today have no scope, so they are never returned for a scoped request (they are recomputed once). The unscoped call keeps working for older callers.
- **Client:** `ManualTestingWorkspace` reads the specification evidence first, computes the scope, and asks for that scope's discovery. The browser-side revalidation cache key includes the scope.
- **«Finn og forstå»:**
  - **On a change:** a scope change drops everything of the previous selection at once (items, stories, filters, active scenario, trace, work queues), then reloads.
  - **Automatic fetch:** when there is no discovery for the selection, it is fetched once per selection, with a spinner «Henter scenarioer …».
  - **Errors:** a failure shows «Kunne ikke hente scenarioer. …» with «Prøv igjen», which repeats only the safe read.
  - **No button:** the «Oppdag» / «Oppdag på nytt» button is gone, and so is the «stale» notice.
- **«Planlegg og klargjør»:**
  - Its technical context follows the same scoped discovery.
  - A saved plan keeps showing its own cases, which belong to it.
  - When the selected specification is not among the specifications its cases were prepared from, a notice says «Testplanen hører til en annen spesifikasjon. Den er laget fra «…», ikke fra valgt «…». Testtilfellene vises slik de er lagret i testplanen.» Nothing is reassigned.

## G. Async race protection

- Loads stay numbered.
- An automatic discovery is tied to the load that started it. If another module or specification is chosen meanwhile, its late answer neither shows nor reloads the page (`generation != _loadGeneration`).
- The spinner belongs to the newest attempt (`_discoveryAttempt`), so an older attempt ending late never hides a newer spinner.
- At most one automatic attempt per project + scope, so a server that answers without a result cannot cause a loop.
- The server's results are scoped, so an old selection's result that arrives late is filed under its own scope and never shown for the new one.

## H. Shared state between the pages

- Both pages use `ManualTestingWorkspace` and the explorer selection, so the scoped discovery applies to both.
- The tab-scoped selection and filter keys are per project (unchanged). Selected scenarios are kept only when they still exist in the current list.

## I. Loading design

The shared `InlineLoading` component is used throughout:

- «Henter scenarioer …» (page actions and empty area);
- «Henter spesifikasjoner …» (picker);
- the existing skeleton «Laster scenarioer …» / «Henter scenarioer …» while the page loads.

Nothing of the previous selection is shown while the next one loads.

## J. Counts and filters

- Counts and filters are recomputed from the current scoped discovery only.
- On a selection change the old lists are cleared before loading, so no previous counts are shown as current.
- «forslag totalt» keeps its existing tooltip (all sources of the application in this discovery).

## K. Source and persistence safety

- Automatic loading uses the same non-destructive discovery as before (E).
- Plans, cases, decisions, groupings, translations and sources are not written.
- The grouping default and «Fra kilden» are display only.

## L. Frontend regression results

- `DiscoverScopeAndActionTests` (14):
  - no «Oppdag» button; the page always asks with a scope;
  - with no discovery for the selection: one automatic fetch with a spinner, no loop, selections kept;
  - a failure → «Kunne ikke hente scenarioer.», the spinner gone, «Prøv igjen» fetches again;
  - another specification's scenarios are labelled without «Oppdag»;
  - «Fra kilden (ordrett)»: collapsed with `aria-expanded=false`, opens and closes, the original Gitt/Når/Så text preserved, collapsed again for another scenario and after choosing a story;
  - «Brukerhistorier» is the default grouping.
- `StoryGroupingPresentationTests`: `DefaultMode` is «Brukerhistorier» also with business groups or FR sections.
- Eleven mocks gained the scope parameter; one fake was updated.
- Full suite: see «Final runs» below.

## M. Backend regression results

- `ScenarioDiscoveryScopeTests` (5 new):
  - the scope's form;
  - a module switch never returns the previous module's result, switching back returns its own, and the unscoped call is unchanged;
  - identical scenarios in two scopes are two results;
  - a result without a scope is never returned for a scoped request;
  - a repeated discovery of the same selection makes no duplicate.
- With the discovery, store and revalidation tests: 23/23.
- **All backend test-planning tests: 492/492.**
- The full backend suite was not run (memory limits earlier in this session).

## N. Browser verification

See «Final runs» below. It runs on an isolated publish with its own App_Data and a disposable database.

## O. Performance measurements

None. The spinner was added, and the rebuild is identified (D). No numbers are claimed.

## P. Modified files

- `shared/TestScenarioDiscoveryContracts.cs` (`DiscoveryScope`, `SelectionScope`)
- `backend/…/TestScenarioDiscoveryService.cs` (scoped cache, `LastAsync` scope, scope in id)
- `ScenarioDiscoveryStore.cs` (`FindScopedAsync`)
- `Controllers/TestPlanSuggestionsController.cs` (`scope` query)
- `frontend/…/Services/ManualTestingWorkspace.cs`, `TestPlanSuggestionsApiService.cs`, `LastDiscoveryCache.cs`, `StoryGroupingPresentation.cs`, `PlanPreparePresentation.cs` (`OtherSpecifications`)
- `Pages/ManualTestingDiscover.razor` (automatic loading, button removed, default grouping reset, «Fra kilden» accordion), `Pages/ManualTestingPlan.razor` (notice)
- `Components/SpecificationScopePicker.razor` (spinner)
- Tests: `ScenarioDiscoveryScopeTests.cs` (new), `LastDiscoveryRevalidationTests.cs`, `DiscoverScopeAndActionTests.cs`, `StoryGroupingPresentationTests.cs`, mocks in 8 frontend test files, `ManualTestingRailAndBadgesWorkflowTests.cs`

## Q. Known limitations

- The feature dropdown's review-context rebuild is not made cheaper, only visible (D).
- «Ingen spesifikasjoner funnet for denne modulen» is not added: the picker lists only modules that have specifications (`Candidates > 0`).
- «Den tidligere valgte spesifikasjonen finnes ikke lenger» is handled by the explorer's own selection state (unchanged), not re-tested here.
- Project-wide consumers that read the project's newest discovery without a scope (work queues for plans, source status) still read the newest result of any selection, as before.

## R. Acceptance criteria (automatic loading brief)

| AC | Status |
|---|---|
| AC01 Project change refreshes dependent lists | IMPLEMENTED (existing project handler, plus the default grouping reset) |
| AC02 Module change refreshes | VERIFIED (bUnit, backend) |
| AC03 Feature change refreshes | VERIFIED (scope includes the selected specification) |
| AC04 No old module scenarios | VERIFIED (backend scope tests; page clears on change) |
| AC05 Old US/AC cleared | IMPLEMENTED (active scenario, story filter and «Fra kilden» reset on change) |
| AC06/AC07 No manual «Oppdag» | VERIFIED |
| AC08 Non-destructive | VERIFIED (audit E; no user records written) |
| AC09 Dropdown performance | PARTIAL: root cause documented, spinner added, not measured |
| AC10 Shared spinner | VERIFIED (`InlineLoading`) |
| AC11/AC12 Race-safe | IMPLEMENTED (generation + attempt guards; scoped server results); rapid switching not browser-tested |
| AC13 Validated cross-page scope | IMPLEMENTED (shared scoped loader; plan notice) |
| AC14/AC15 Data unchanged | VERIFIED |
| AC16 Counts per scope | IMPLEMENTED |
| AC17/AC18 Error and retry | VERIFIED |
| AC19 Frontend tests | VERIFIED |
| AC20 Backend tests | VERIFIED (targeted 492/492); full suite not run |
| AC21 Browser | See «Final runs» |
| AC22 No reset | VERIFIED |
| AC23 No commits | Committed at the user's explicit request («COMMIT ALL AFTER DONE»); no push |
| AC24 Report | This file |

## Default grouping («Brukerhistorier»)

- `StoryGroupingPresentation.DefaultMode` always returns «Brukerhistorier»; «Funksjonelle områder» / «Forretningsområder» remain in «Grupper etter».
- No grouping preference is persisted (audit). The page resets to the default on a project, module or specification change, and a new visit (for example returning from Planlegg) starts with it.
- The selected story and AC are unchanged by switching the display. Assignments and reviewed grouping decisions are not touched.

## «Fra kilden (ordrett)»

- The same accordion as «Kildegrunnlag» (a button in the header, `aria-expanded`/`aria-controls`, a rotating chevron, the content hidden but present).
- Collapsed by default; open only for the scenario it was opened on; reset on a story, specification, module or project change.
- «Klar til kjøring?» and «Testtilfeller» stay visible as before.

## Final runs

**Browser (isolated):** a fresh Release publish, its own App_Data, a disposable database, ports 5097 and 5197. `ManualTestingRailAndBadgesWorkflowTests` and `AzureDevOpsTransferWorkflowTests`: **2/2 passed**.

The rail walkthrough now proves on «Finn og forstå»:

- the scenarios appear **by themselves**;
- **no «Oppdag» button** exists;
- «Grupper etter» starts on **«Brukerhistorier»**;
- switching to «Funksjonelle områder» shows the areas, with the colour rules from the previous report.

It also covers the plan page (US/AC badges, «Kildegrunnlag»). The Azure DevOps walkthrough passed unchanged.

Not browser-tested:

- the «Fra kilden» accordion (bUnit only);
- rapid module switching;
- the specification picker spinner, which was added after the publish used here.

**Frontend full suite:** 9694 passed, 1 failed (`ProjectImportMultiImportTests.Import_source_opens_import_mode…`). Project Import was not touched; the test passes 3/3 when run alone, so it is load-sensitive under the full suite.

**Backend:** test-planning 492/492.
