# Sprint-based scenario execution and test plan duplication — 2026-11-26

**Safety:**
- no migration (an existing dormant table is reused);
- no change to existing plans, cases, approvals, revisions or source data;
- no Azure DevOps write;
- tests run on temporary databases.

**Status keys used:** IMPLEMENTED · VERIFIED (tests) · NOT VERIFIED (browser) · NOT IMPLEMENTED.

## A–C. State and audit

- **Builds on:** the optional-cases work (commit 70910367): `TestPlanDraft.PlannedScenarios` (scenario-only plan items) is used here.
- **Existing execution infrastructure:** `manual_test_runs` (`ManualTestRunRecord`).
  - It is case-level (plan id + case id), with outcomes Passed/Failed/Blocked/NotRun, a concurrency revision and JSON content.
  - It has been **dormant since 2026-11-19** (manual execution moved to Azure DevOps); nothing reads or writes it.
  - There was no scenario-level execution and no plan duplication.

## D–F. The execution model (IMPLEMENTED)

- **The unit:** a **scenario occurrence in one plan** (`ScenarioOccurrence`).
  - **The key:** `scenario:{scenario id}` (each case of the scenario and the scenario-only item map to it), or `case:{case id}` for a hand-written case without a scenario. Excluded and superseded cases are not executed.
  - **No global result:** a scenario has no global result; the same scenario in two plans has two independent occurrences.
- **Statuses:** `ScenarioExecutionStatus` — Ikke kjørt (default) · Bestått · Feilet · Blokkert. There is no case-level or step-level execution.
- **Storage:** one completed row per recorded result in `manual_test_runs`.
  - `DraftId` = the plan; `CaseId` = `occ:{occurrence key}` (never clashes with the old case runs); `Outcome` = the status; `ContentJson` = the event (id, status, optional comment, defect reference, actor, time).
  - **No migration.** Local Data Reset already clears this table.
- **History:** every result is a new event; the current status is the newest event. **Viewing writes nothing**, and recording a result **does not write a plan revision**.
- **Concurrency:** `RecordScenarioResultRequest.ExpectedLatestEventId` must equal the newest event; otherwise the write is refused with 409 `stale-result`. The client keeps the comment and reloads.
- **Archived plan:** read-only for results (409 `plan-archived`).

## G–H. Testplaner UX (IMPLEMENTED)

**Per active plan, «Testgjennomføring»** (component `PlanExecution`):
- **The summary:**
  - «N scenarioer · x bestått · y feilet · z blokkert · w ikke kjørt»;
  - a segmented bar with a text label;
  - «Gjennomført: (bestått + feilet + blokkert) / alle %».
- **A status filter:** Alle / Ikke kjørt / Feilet / Blokkert / Bestått.
- **Scenarios grouped by their saved suite path.** Each row shows the AC identity, title, status, and last time and person.
- **Opening a scenario shows:**
  - the expected result (or «Ikke oppgitt i kilden – avklar …»);
  - the cases as «Tilhørende testtilfeller (instruksjoner, ingen egen gjennomføring)» links, or «Ingen testtilfelle er forberedt. Scenarioet kan likevel gjennomføres ut fra kilden.»;
  - one result choice, an optional comment and defect reference, «Lagre resultat», and the history (on demand).
- **No execution controls elsewhere:** none in Finn og forstå, Planlegg og klargjør or the preview.

## I–N. Duplication (IMPLEMENTED)

**«Kopier testplan»** on each active plan:
- **Inputs:** a name (proposed «… (kopi)») and an optional sprint label.
- **Preview:** «Fra «X»: N testgrupper · M scenarioer (K uten testtilfelle) · C testtilfeller», and **«Testresultater kopieres ikke. Alle scenarioer starter som Ikke kjørt.»** It also states how many approved cases must be approved again.
- **Created only on «Opprett kopi»;** a retry reuses the request id.

**`TestPlanSuggestionService.DuplicateAsync`:**
- **What is new:** a new plan id and revision history (revision 1, «Kopiert fra «X» (versjon N)»), and **new suite ids** with the same names, nesting, order and level; **empty suites are kept**.
- **What is copied:**
  - scenario-only items, with mapped suites;
  - cases as **independent copies** of their content: steps, translations, test data links, source links and source snapshot references stay as they were. A later edit in one sprint never changes the other.
- **What is not copied:**
  - **Approvals:** an approved case becomes «under arbeid» in the copy, and the review history is not carried over.
  - **Execution events:** they belong to the source plan id.
  - **Azure DevOps mappings:** they are keyed by the source plan, never by its content.
- **Atomic:** one transaction; the source plan is never changed.

## O–P. Source versions and approval

- **Source:** the copy keeps the source references of the original. Nothing is upgraded to a newer source silently; the existing source comparison flags changes.
- **Independence:**
  - approval and execution are independent;
  - a passed scenario never approves a case, and an approved case never passes a scenario.

## Q–S. Testoversikt, Finn og forstå, ADO

- **Testoversikt:** unchanged (no cross-sprint result aggregation).
- **Finn og forstå:** membership counts both sprint plans; results never change the turquoise colour.
- **ADO:** no remote run or result is created.

## W–X. Tests

- **Backend:** `SourceDrivenPlanningTests.Sprint_12_results_stay_when_copied_to_Sprint_13_which_starts_not_run_and_each_sprint_keeps_its_own_history` (temporary database) covers the product owner's example:
  - Sprint 12 with AC1 and AC2 as cases (AC1 approved), AC3 scenario-only, and an empty «Ytelse» suite;
  - recorded results AC1 Feilet → Bestått, AC2 Bestått, AC3 Blokkert;
  - a stale write → 409, history newest first, no plan revision from results;
  - the duplicate preview counts;
  - Sprint 13: new id, revision 1, new suite ids, the same tree including the empty suite, no approvals, the scenario-only item kept, **all three «Ikke kjørt» with no history**;
  - AC1 Feilet in Sprint 13 while Sprint 12 stays Bestått, and the source plan unchanged;
  - an archived plan is refused.
  - **Passed.**
- **Frontend:**
  - `PlanExecutionTests` X01–X03: counts add up; a scenario without a case is executed at scenario level with no step execution; save → notice and counts; a case is an instruction link; history; a conflict keeps the comment and the write names the latest event; an archived plan has no controls;
  - `ManualTestingTestplanerTabTests.Copying_a_plan_…`: the preview text, the reset statement, nothing copied before confirmation, name and sprint sent, a request id.
  - **Passed.**
  - **Full frontend suite after this delivery: 9,804 / 9,804.**

## Y. Browser

**NOT VERIFIED** in the browser in this commit.

## AA. Remaining

- **Not yet available:**
  - suite, priority, type and level filters in the execution view (status filter only);
  - a selected-plan widget in Testoversikt;
  - a source-update action for a copied plan;
  - attaching evidence files to scenario results (the old case-run attachments are not wired).
- **Hand-written cases without a scenario** are executed as their own occurrence.
