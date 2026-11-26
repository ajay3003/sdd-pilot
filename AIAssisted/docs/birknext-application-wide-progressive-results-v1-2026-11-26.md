# BirkNext — application-wide progressive results V1 (2026-11-26)

Commits:
- `54a0bcd5`: the shared «Vis flere».
- `b511146a`: the compact discovery wire form, which lowers the cost of the largest list's data.

The brief's "NO GIT COMMITS" line was overridden by the user's standing instruction to commit; nothing was pushed.

## A–B. Inventory and existing architecture
Before V1 there were about a dozen hand-written «Show more» / «Vis flere» buttons, each with its own wording and step. All were client-side (the data was already in memory). Server paging existed nowhere.

| List | Component | Population | Search/filter before batching | Decision |
|---|---|---|---|---|
| Finn og forstå scenarios (flat) | `Pages/ManualTestingDiscover.razor` | up to 2,000 per project | yes (`ScenarioBrowserPresentation.Apply` on all) | **ShowMore**, 40 |
| Finn og forstå per group | same | group size | yes | **ShowMore** per group, 50 (distinct from «Vis alle grupper») |
| Finn og forstå requirements | same | requirements of the specification | yes | **ShowMore**, 25 |
| Legg til scenarioer | `AddScenariosToPlan.razor` | the discovery | yes | **ShowMore**, 100 |
| Testplaner plan list | `SavedPlans.razor` | the project's plans | yes | **ShowMore**, 10 |
| Plan source changes | `PlanSourceChangesPanel.razor` | changed cases | yes | **ShowMore**, 20 |
| Source Analysis configuration | `ConfigurationWorkspace.razor` | config rows | yes | **ShowMore**, 300 |
| Implementation gaps | `ImplementationGapExplorer.razor` | findings | yes | **ShowMore**, 25 |
| Test Coverage journeys / behaviours | `TestCoverageReview.razor` | journeys / behaviours | yes | **ShowMore**, 10 / 25 |

Not converted:
- Infrastructure dependencies: the same pattern, a 200 step, English UI; left for a later pass.
- Azure DevOps transfer results and history: «Vis alle» over 8 rows, which is short.
- Text clamps («Show more» on long text, `TraceabilityItemText`): not lists.
- Dashboard «Vis alle (N)» bars: a chart, deliberately all-or-first-8.

## C–D. Shared component
`Shared/Components/Results/ShowMore.razor` (the existing spinner styles, no new package):
- «Viser X av Y …» and «Vis N flere» (never more than remain). With an unknown total: «Viser X» and «Vis flere», never an invented total.
- **Client mode** (all current lists): the next batch appears in the same render. No spinner and no delay.
- **Asynchronous mode:** while the callback runs, the button shows the shared spinner and «Laster flere …», is disabled (no double click), and keeps the rows shown. On failure the list passes `Error`, and «Prøv igjen» calls the same callback.
- An aria-live «N flere vises». The button keeps its width (no layout jump).
- Hidden when everything is visible.

## E. Batch sizes
Kept each list's existing step. Changing them was not measured as beneficial.

## F. Search correctness
Every converted list filters and searches its complete population first, then shows `Take(limit)`. Verified by test P05: 500 scenarios, a search for «nummer 480» gives that one scenario in the first batch.

## G. Grouping
- Per-group «Vis flere» counts the group's full population.
- «Vis alle grupper» (expand) and «Vis flere» (batch) stay separate operations.
- Select-all on a group is unchanged (it selects the group's eligible items, as before).

## H. Selection
Selection is held by id (`_selected`), independent of the batch. Loading more never selects anything.

## I–J. Spinner, retry, stale responses
- The spinner appears only for real asynchronous work (test P04).
- There is no server paging yet, so there are no page cursors.
- The discovery request itself keeps its ETag/304 cache and the page's `_loadGeneration` guard.

## K. Accessibility
A real button with `aria-busy`, disabled while loading, an aria-live announcement, and «Prøv igjen» as a button.

## L. Performance
- **Compact discovery** (the payload behind the largest list): M2LB 2,000 candidates, 9,210,265 → 6,923,309 bytes (75.2 %), lossless (test).
- Desktop .NET read time was not lower in a single cold measurement (238 ms full vs 278 ms compact + expand, with the compact read measured first); NOT VERIFIED in WASM.
- Rendering: unchanged batches (Finn og forstå 40 rows per batch).

## M. Tests
- `ShowMoreTests` P01–P05.
- `ScenarioDiscoveryWireTests` (lossless round trip; conflicting content kept whole; real payload).
- Full frontend run: 9,854 passed, 1 failed (a proxy test, which passes alone; a known load flake).

## N. Browser
See the section appended below (isolated v1env24).

## O. Changed files
- `Shared/Components/Results/ShowMore.razor`, `_Imports.razor`, `wwwroot/css/components.css`
- The nine list call sites above
- `shared/ScenarioDiscoveryWire.cs`, `shared/TestScenarioDiscoveryContracts.cs`
- `TestPlanSuggestionsController`, `TestPlanSuggestionsApiService`, both csproj files

## P–R. Remaining
- **NOT IMPLEMENTED:**
  - server-side paging (no list has a server population that needs it yet; the discovery is still fetched whole);
  - virtualisation;
  - Infrastructure dependencies and Requirements Traceability relationship lists (Traceability uses its own table component; it was not converted in this pass).
- **Acceptance:** one shared pattern (IMPLEMENTED); search over all rows (VERIFIED by test); spinner only for real loading (VERIFIED by test); selection kept (IMPLEMENTED; browser below).
