# Optional test cases, scenario-only plan items and preview — 2026-11-26

**Safety:**
- no database migration (the plan's stored JSON gains an optional field);
- no saved case, approval or revision changed by preview or planning;
- no Azure DevOps write.

**Status keys used:** IMPLEMENTED · VERIFIED (tests) · NOT VERIFIED (browser) · NOT IMPLEMENTED.

## A–C. Audit

**The plan model before:**
- A plan (`TestPlanDraft`, stored as JSON in `test_plan_drafts.content_json`) held `Suites` and `Cases` only.
- A scenario could not be in a plan without a test case: «Fra valgte scenarioer» and «Legg til» always created a generated case per scenario (`ProjectCase`).
- Suite membership is the case's `SuiteId`.

**Not supported before:**
- scenario-only entries;
- a preview of the editor;
- an explicit «not prepared» state.

## D–G. Design (IMPLEMENTED)

- **The new field:** `TestPlanDraft.PlannedScenarios: List<PlannedScenario>?` (`shared/TestPlanSuggestionContracts.cs`). It is **additive and optional**: older plans read as null.
- **An item holds:** the scenario id (the plan's occurrence key), source identity, title, expected behaviour as planned, story/AC identity, specification path, snapshot, suite, order, and who and when.
- **An item is not a case:** no steps, approval, readiness or execution of its own.
- **Creation:** `CreateProjectTestPlanRequest.ScenarioOnly` (and `SuiteId` for an existing plan).
  - A new plan gets items with suites built by the same structure rules as cases.
  - Adding to an existing plan skips any scenario it already holds (as an item or a case, by id or source identity): **no duplicate membership**, and no revision when nothing is new.
- **Saving:** items are kept on save; an item whose suite was removed moves to the first suite and is never dropped.
- **Membership:** `WorkQueuesAsync` counts an item as plan membership (`ScenarioPlanMembership.ScenarioOnly`, no case id) when no current case of the plan represents the scenario. The turquoise row and plan name therefore cover scenario-only items too.

## H–I. UX and the case transition

- **Testplaner → «Ny testplan» → «Fra valgte scenarioer»:** «Legg til scenarioene uten testtilfeller» («… Ingenting markeres som klart til kjøring.»).
- **Planlegg og klargjør:** «Planlagt – ikke klargjort (N)» lists the items with their suite path, «Scenario – ikke klargjort», «Forhåndsvis» and «Forbered testtilfelle».
- **«Forbered testtilfelle»:**
  - it uses the existing «add to this plan» flow (generated case, existing editor);
  - the item stays in its suite and the case is listed;
  - **no second item**, and a retry never duplicates the case (the existing identity check);
  - the case is never approved by preparing it.
- **Cancelling** (not preparing) leaves the item unchanged.

## M–N. Preview (IMPLEMENTED)

- **«Forhåndsvisning»** in the case editor and «Forhåndsvis» on items open a read-only dialog with these sections:
  - a header: plan, suite path, AC title;
  - **Scenario:** the title and expected behaviour, or «må avklares»;
  - **Testtilfelle:** title, purpose, preconditions, a step table (Steg / Handling / Forventet resultat) and the overall expected result, with «Ikke angitt» for missing values; for an item: «Ingen testtilfelle er klargjort.»;
  - **Klargjøringsstatus:** approved or not, the known gaps; for an item, «Scenario – ikke klargjort. Ikke klart til kjøring.»
  - Buttons: «Skriv ut» and «Lukk» (Escape also closes it).
- **The editor's current values:** a case preview reads the open editor form, so unsaved edits are shown and marked **«Ulagret utkast»** («Lagret versjon» otherwise).
- **Read-only:** closing returns to the editor with every edit intact; the preview never saves, approves, translates or executes; there are no execution controls.

## P. Suggested steps

**`AcceptanceScenarioInterpreter`:** for system processing, the vague «Trigger and observe: X.» is replaced by «X (the action that makes this happen in the test environment must be specified).»
- The event is stated as the source gives it.
- The missing action is said plainly, never invented.
- The existing clarification is kept.

## Q–R. Readiness, approval and ADO

- A scenario-only item is never ready and never approved.
- **The ADO export reads cases only:** items are not exported as test cases. A test asserts that a package of a mixed plan holds only the prepared case.

## U–V. Tests

- **Backend:**
  - `SourceDrivenPlanningTests.A_scenario_can_be_planned_without_a_case_prepared_later_once_and_is_never_exported_or_ready`: no case on planning; items in saved suites; re-adding is no duplicate and no revision; preparing later gives one case with the item kept; the case is not approved; save keeps the items; the export holds only the case;
  - `AcceptanceScenarioInterpreterTests` updated for the new step text.
  - Backend results were running at commit time.
- **Frontend:**
  - `ManualTestingPlanPreviewTests.P01` (the preview shows unsaved edits, «Ulagret utkast», no execution words, closing keeps the edit, never saves) and `P02` (an item listed as «Scenario – ikke klargjort»; its preview has no case and is «Ikke klart til kjøring»).
  - **Passed.**

## W. Browser

**NOT VERIFIED** in this commit.

## Y. Remaining

- **Not built:**
  - a preview in Testplaner;
  - an export-readiness summary that counts items;
  - a destination-suite chooser in the UI for adding to an existing plan (the API supports `SuiteId`);
  - per-language preview text;
  - Sammenlign in the preview.
- **Not offered:** «Legg til i eksisterende plan» as scenario-only from Finn og forstå (only «Ny testplan» offers it).
