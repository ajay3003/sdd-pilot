# Manual Testing — Testplaner v4 (2026-11-26)

Commits: `89c2ca2a` (add scenarios, empty plans), `f68012eb` (execution workspace, actions menu, template note, source-change explanation), `c18c2218` (route registration).

## B. Root cause of the empty-case dead end
- A template creates suites only (`TestPlanTemplateRules.Instantiate`), which is valid.
- But there was **no UI to add scenarios to an existing plan**. The backend already supported it: `CreateProjectAsync` with `DraftId + ScenarioOnly + SuiteId`, which refuses duplicates.
- So a new plan opened in Planlegg at «Velg et testtilfelle i listen» with nothing to select.

## C–F. Implementation
- **`AddScenariosToPlan.razor` («Legg til scenarioer»):**
  - offers the project's acceptance scenarios and documented tests (`IsClassifiable`; requirements without a scenario are not offered);
  - search plus module, test level and priority filters;
  - scenarios already in the plan are marked and disabled;
  - choice of target testgruppe, explicit confirm, then a new occurrence per scenario;
  - no case is created and no result recorded;
  - the dialog stays open to add more to another suite, and each addition names the plan revision it started from.
  - Opened from each active plan row (primary when the plan is empty) and from the empty plan in Planlegg.
- **Suite hierarchy:** unchanged (existing suite editing in Planlegg).
- **Plan list:** «Åpne testplan», «Legg til scenarioer», «Testgjennomføring»; Copy, Overfør and Arkiver are under «Flere handlinger». Empty plans say «Ingen scenarioer i testplanen ennå».
- **Template preview:** «Oppretter testgrupper uten scenarioer. Legg til scenarioer etter opprettelse …».
- **«Kilde endret»:** an accessible «Hva betyr det?» with the stored explanation.

## H–J. Execution
- `/manual-testing/plan-execution?plan=` is a dedicated page using the existing `PlanExecution` component and the same persisted events.
- Results belong to the scenario occurrence in that plan. They are never stored on a case or step, and readiness never blocks.
- Progress counts come from occurrences (Total / Passed / Failed / Blocked / Not run).

## K. Copy
Already correct and covered by `SourceDrivenPlanningTests`:
- new plan, suite and occurrence ids, with the same tree;
- every occurrence «Ikke kjørt» with no history;
- independent sprint results, the source plan unchanged;
- archived plans read-only for results.

## Tests
- `AddScenariosToPlanTests` (2): filters, membership, two suites, no case, no result.
- The updated template-note test.
- Manual Testing suites 199/199, full frontend 9,833/9,833.

## Browser
v1env18 walkthrough (`T-template`, `T-empty-row`, `T-add-two-suites`, `T-execute`, `T-copy`), see `manual-testing-browser-walkthrough-v1env18-2026-11-26.md`.

## Remaining
- Moving and removing scenarios between suites in the Testplaner view: NOT IMPLEMENTED (Planlegg has suite moves for cases).
- Readiness per occurrence: NOT IMPLEMENTED.
- Intentional repeated occurrences: not supported (duplicates are refused).
