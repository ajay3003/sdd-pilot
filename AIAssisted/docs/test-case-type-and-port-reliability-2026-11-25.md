# Test case type (Manuell / Teknisk) and test port reliability — 2026-11-25

Branch `008-traceability-first`. Nothing from this task is committed (the work starts after `8a176fd7`). Both stashes are kept, no data was reset and no database was dropped.

**Status key:**
- **IMPLEMENTED** — the code is written.
- **VERIFIED** — the code is written and a test or a browser check confirmed it.
- **NOT RUN** — the check was not executed.
- **BLOCKED** — something outside the task prevented the check.
- **NEEDS REVIEW** — a person should decide.

## A. Starting state

- **Code:** HEAD was `8a176fd7` and the working tree was clean. Two stashes exist (`backup-before-final-008-excluding-NUL`, `backup-before-switch-to-final-008`); both are untouched.
- **Processes:** the app was not running. During this task I started the local instance (`start-local.ps1 -SkipContainers`) and stopped it again for backend builds.
- **Data baseline (normal database `birknext`):**
  - 6 plans: 5 from 24 November and the user's "Testplan 2026-11-25".
  - Plan fingerprint `e20566b3…`.
  - One grouping row, `48db6a24…`, at revision 5 (SkoleService 001, holding the US4/US5/US8 decisions).
  - Row counts recorded for 24 tables.

## B. The scenario's test approach (existing; unchanged)

- **Reviewed decision:** made per scenario in Finn og forstå. It is stored as `SetScenarioTestKind` in the specification's grouping document, using the shared enum `ScenarioTestKind { Manual, Technical }` (`shared/StoryGrouping.cs`).
- **Automatic recommendation:** the scenario's execution suitability, `TestDesignAdvisor.Suitability(FactsOf(...))` (`shared/TestDesignAdvice.cs`), with approaches Manuell, Manuell + teknisk, Integrasjon/E2E, Automatisering anbefalt, Teknisk and Trenger vurdering. «Manuell + teknisk» is what the brief calls «Begge». The model has no separate «Begge» or «Ikke vurdert» decision value.

## C. Test case storage (existing)

- **Where a case's own fields live:** `TestCaseDraft.Edits` (`TestCaseEdits`). The plan JSON skips null values (`WhenWritingNull`), and every save creates a revision (`test_plan_draft_revisions`).
- **Existing `TestExecutionMethod`** (NotDecided / Manual / ToolAssisted / Automated) describes *how* a case is run. Teknisk must not mean "automated", so it was not reused as the case type.

## D. Implementation (IMPLEMENTED)

**Contract**
- A new field `TestCaseEdits.TestKind : ScenarioTestKind?`. It reuses the scenario's enum, so no second classification model exists.
- Null means «Ikke angitt». Values are stored by name (`"Manual"` / `"Technical"`), never as Norwegian text.
- `AddManualTestCaseRequest` gains an optional `TestKind`; older callers are unaffected.

**Server** (`TestPlanSuggestionService` / `TestPlanSuggestionEngine`)
- The save cleaner, which rebuilds edits field by field, now carries `TestKind`. Without this a save would have silently dropped it.
- `AddManualCaseAsync` stores the chosen type and records "Test type: …" in the case's edit history.
- A duplicate keeps its source case's type unless a new one is given.
- The edit history records the field as «Manual or technical»; the plan comparison includes `TestKind`.
- **Export:** JSON gets `testKind` (left out when unset); text gets a line `Testtype: Manuell/Teknisk` only when set. It never appears inside steps or expected results.

**No migration.** The field is stored in the existing plan JSON. A case without a type writes nothing, so existing plans keep their stored form and fingerprints.

## E. Creating a case (IMPLEMENTED, VERIFIED)

- **New entry point:** «+ Nytt testtilfelle» in the list's toolbar creates a case **directly, without a scenario**. «+ Testtilfelle» under a scenario opens the same short form instead of creating at once.
- **The form:** title, **Testtype: Manuell | Teknisk**, and a line explaining the suggestion. «Legg til» stays disabled until there is a title and a type. Nothing is created before the tester clicks «Legg til».
- **Suggestions:**
  - A reviewer's decision from Finn og forstå is preselected, with who made it. The tester can override it.
  - An unreviewed scenario only shows «Anbefalt for scenarioet: …»; nothing is preselected.
  - A direct case has no suggestion.

## F. Editing a case (IMPLEMENTED, VERIFIED)

- «Testtype» (Manuell | Teknisk) sits at the top of the editor, above the title.
- An older case shows «Ikke angitt» together with the suggestion.
- Changing the type is an unsaved change, saved with the normal «Lagre»; there is no auto-save. Only the type changes; every other field is sent as it was.
- The case list shows a «Manuell» / «Teknisk» tag on cases that have a type.
- Planlegg's existing «Vis teknisk innhold» filter now reads **the case's own type first**, so it filters the right thing. The old heuristic only applies to cases without a type.

## G. Finn og forstå (unchanged)

- The scenario decision and the case type are separate. Choosing a case type never writes the scenario's decision; this is verified by tests and in the browser (grouping revision 5 before and after).
- A scenario can hold both manual and technical cases.

## H. Older plans

- A case without the field reads as «Ikke angitt»; no type is invented for it.
- A save that changes nothing about types leaves every case unset.
- No existing plan was modified (fingerprints and row counts are identical; see P).

## I. Export (VERIFIED by backend test)

- `testKind` appears in the export JSON and «Testtype: Teknisk» in the text, only when set.
- Planlegg has no import, so a round trip through import is NOT APPLICABLE.

## J. Port race: the cause

Several tests used the pattern *bind port 0 → read the port → release it → use the number later*. Between the release and the later use, any parallel test or process can take the port:

- `ManagedEdgePreflightTests.FreePort()` was used for six "nothing listens here" ports, plus one port that a fake Edge would listen on later.
- `TlsChainInspectorTests` had one "closed port" of the same kind.

**Production code with the same pattern:** `FrontendZapPassiveReviewService.FreePort()`, which picks a host port for the ZAP container. It is not changed, because containers are out of scope and production port setup was not to change. It is listed as a risk (S).

**Correction to `8a176fd7` / report section FV9 (NEEDS REVIEW):** that commit named this race as CI_DEF_3's root cause. The new diagnostics contradict that for the latest failure (see N): the test's own request was answered and there were **no stray connections**. The race is real and is fixed here, but it is **not proven** to be CI_DEF_3's cause.

## K. Port race: the fix (IMPLEMENTED, VERIFIED)

- **New helper:** `TestInfrastructure/LoopbackPortReservation` (tests only).
  - It is a loopback socket that is **bound but not listening**. Connections to it are refused, which is exactly what "closed" means, and no other bind in any process can take the port while it is held.
  - On Windows it is also bound with exclusive address use.
  - `Listen()` starts listening on the same socket, so a test's own server takes the port over without releasing it.
  - `Dispose()` releases it unless it was handed over.
- **Fake Edge server:** `FakeCdpServer` now listens on a socket it owns from the start, or on a reservation that is handed over.
- **Closed ports:** the Edge preflight's closed ports are reservations held for the whole test (`ClosedPort()`, released in `Dispose`). TlsChainInspector's closed port works the same way.
- **What was not done:** no sleeps, no longer timeouts, no change to parallelism, no process-local lock.

## L. Frontend tests (VERIFIED)

**Full frontend suite: 9,650 passed / 0 failed.**

New tests in `ManualTestingPlanCaseKindTests.cs`:
- **K01/K03/K20:** a direct case offers Manuell and Teknisk with nothing preselected, needs a choice, and nothing is created before «Legg til».
- **K04/K05/K07:** an unreviewed scenario only recommends; a reviewed one is preselected and can be overridden; grouping is never written.
- **K08/K11/K12:** changing only the type is an explicit save and every other field is unchanged.
- **K10/K13/K14:** a saved type reopens as saved; an older case stays unset; a scenario decision never overrides the case's type.
- **K15/K16:** the list filter reads the case's own type, and the type is never shown as automation or a result.
- **K06:** the suggestion texts.

Test G was updated to the two-step creation.

## M. Backend tests (VERIFIED, targeted)

New tests in `ScenarioCentricPlanningServiceTests` run on a fresh, migrated temporary PostgreSQL database that the tests create and drop:
- **B21/B23/B24:** a direct case is created with its type, stored by name and read back after a reload; the revision increments.
- **B22/B27/B32:** older cases stay unset and keep their stored form.
- **B25/B27:** a type-only change keeps all other fields and the history records the field name.
- **B24/B26:** the type survives revisions and a refresh from the source.
- **B28:** a duplicate keeps its type or takes a chosen one; one scenario can have manual and technical cases; the scenario is not duplicated.
- **B29/B30:** no grouping row is written, and a stale revision is refused with 409.
- **B18:** the export carries the type as metadata only.

**Results:**
- The class: **28/28**.
- Grouping, plan suggestion, port, Edge, TLS and CI_DEF_3 tests together: **177/178**. The one failure was a test assertion about JSON spacing, fixed and passed on rerun; product code was unaffected.

## N. Parallel reliability (VERIFIED)

- **New tests** in `LoopbackPortReservationTests`, all passing:
  - P01: a reserved port refuses connections.
  - P02: nobody else can bind it (AddressAlreadyInUse).
  - P03: 64 parallel reservations and 64 parallel port-0 listeners never share a port.
  - P04: the test's own server takes the port over with no gap, and the connection reaches it.
  - P05: release on success and on failure, and double dispose is safe.
  - P06: a handed-over listener is released by its owner.
- **Controlled parallelism:** **three separate test processes at once** ran the reservation, Edge preflight, Edge connector, TlsChainInspector and CI_DEF_3 tests, three rounds in a row. **9/9 runs passed** (58 tests each) and no test processes were left over.
- **CI_DEF_3 in the full suite (final8, aborted):** it failed again after 25.5 s. The diagnostics say "own request answered: True, stray connections: 0".
  - That run hit the machine's memory floor.
  - The pattern fits the test process stalling under memory pressure: the answer arrives but is read after the 20 s fetch timeout has fired.
  - **The cause is not established.** CI_DEF_3 stays an intermittent failure under full-suite memory pressure.
  - The brief's rule "do not merely increase the timeout" was respected; nothing was weakened.

## O. Full backend regression — BLOCKED

Two attempts (final7, final8) stopped themselves after about 130 s. Free RAM fell below the harness's 2.5 GB safety floor (2,486 MB and 2,448 MB); each run stopped only its own processes and left none behind.

- **Why:** the suite needs about 6.5–7 GB at peak. Other workloads not started by this task (WSL about 3.6 GB, other tools) left too little room.
- **What was not done:** the floor was not lowered, and nobody else's process was stopped.
- **Last complete full run:** final6 (before this task), **7,433 / 0 / 19**.
- **This task's code** is covered by the targeted, broad and parallel runs above, but **a full PASS is not claimed**.

## P. Browser walkthrough (VERIFIED, disposable data)

`scratchpad/case_walk.py` was read before it ran. It writes only into one disposable plan it creates itself («TEMP gjennomgang 104711»), with two real SkoleService scenarios from the last discovery.

**Result: 16 of 16 checks passed.**

- **C19:** a generated case shows «Ikke angitt» plus «Anbefalt for scenarioet: Teknisk», with nothing chosen.
- **A02:** the direct case form offers Manuell / Teknisk with nothing preselected.
- **A05:** created as Manuell.
- **A07:** Manuell persists after reopening.
- **A08:** changing the type is an unsaved change.
- **A10 / A10b:** Teknisk persists after save and reopen, and the list shows «Teknisk».
- **B13:** the scenario's recommendation is shown and nothing is preselected.
- **B16:** the scenario-based case keeps Teknisk after reopening.
- **B18:** the case stays under its source scenario.
- **B17:** the SkoleService 001 grouping is still revision 5.
- **C20:** the generated case keeps its content and has no type.
- **C23:** no horizontal scroll at 900 or 1280 px.
- **C24:** every write went to the disposable plan; no console or API errors.

**Timings (ms):** opening Planlegg on the plan 12,873 (includes page load and discovery read), create form 55, creating a case 763, saving the type 1,345.

**Cleanup:** `pgprobe delete-temp-plan` checked the name prefix and that the plan had no snapshots, runs or exports, then deleted 4 revisions and 1 plan in one transaction. Afterwards the **row counts in all 24 tables were identical to the baseline**, the plan fingerprint was unchanged and the grouping was still revision 5.

**Not exercised in the browser:** export (covered by backend test B18); a scenario with a *reviewed* decision, because creating one would have meant writing your grouping (covered by frontend test K04).

## Q. Changed files

**Product code**
- `shared/TestPlanSuggestionContracts.cs`
- `shared/TestScenarioDiscoveryContracts.cs`
- `backend/BirkNext.Api/Services/TestPlanning/TestPlanSuggestionService.cs`
- `backend/BirkNext.Api/Services/TestPlanning/TestPlanSuggestionEngine.cs`
- `frontend/BirkNext.Web/Services/PlanPreparePresentation.cs`
- `frontend/BirkNext.Web/Pages/ManualTestingPlan.razor`
- `frontend/BirkNext.Web/wwwroot/css/manual-testing.css`

**Tests and test infrastructure**
- `backend/BirkNext.Api.Tests/TestInfrastructure/LoopbackPortReservation.cs` (new)
- `backend/BirkNext.Api.Tests/TestInfrastructure/LoopbackPortReservationTests.cs` (new)
- `backend/BirkNext.Api.Tests/Services/ManagedEdgePreflightTests.cs`
- `backend/BirkNext.Api.Tests/Services/PerformanceTests/TlsChainInspectorTests.cs`
- `backend/BirkNext.Api.Tests/Services/TestPlanning/ScenarioCentricPlanningServiceTests.cs`
- `frontend/BirkNext.Web.Tests/Pages/ManualTestingPlanCaseKindTests.cs` (new)
- `frontend/BirkNext.Web.Tests/Pages/ManualTestingPlanTests.cs`

**Line endings:** each file keeps its own endings. `TestScenarioDiscoveryContracts.cs` has mixed endings and was edited byte-for-byte.

## R. Migrations

None.

## S. Remaining risks

1. **CI_DEF_3** still fails intermittently in full-suite runs under memory pressure; the cause is not established (N).
2. **Full backend regression** is BLOCKED by the RAM available on the workstation (O).
3. **`FrontendZapPassiveReviewService.FreePort()`** (production, ZAP container host port) still has the bind-release-reuse race (J).
4. **No «Begge» / «Ikke vurdert» scenario decision value.** «Manuell + teknisk» suitability leaves both choices open, but the brief's four-value scenario approach is NEEDS REVIEW.
5. **Planlegg hides cases saved as Teknisk** while «Vis teknisk innhold» is off; the editor keeps an open case visible. This follows the existing filter and is a possible UX question.

## T. Acceptance criteria

| AC | Status |
|---|---|
| 01 New cases support Manuell/Teknisk | VERIFIED (K01, P/A02) |
| 02 Direct cases | VERIFIED (B21, K01, P/A05) |
| 03 Scenario-derived cases | VERIFIED (K04, B28, P/B16) |
| 04 Save and reopen | VERIFIED (K08/K10, P/A07, A10) |
| 05 Revisions and reimport | VERIFIED (B24/B26, refresh from source) |
| 06 Older cases readable | VERIFIED (B22, K13, P/C20) |
| 07 Content preserved | VERIFIED (B25, K12) |
| 08 Case type does not change scenario suitability | VERIFIED (B29, K05, P/B17) |
| 09 Suggest, never force | VERIFIED (K04/K05, P/B13) |
| 10 Manual and technical cases under one scenario | VERIFIED (B28) |
| 11 Separate from execution status | VERIFIED (K16; separate from `TestExecutionMethod`) |
| 12 Coverage remains evidence-correct | VERIFIED: coverage and readiness code unchanged; the type is not used in coverage |
| 13 Export keeps type | VERIFIED (B18) |
| 14 No duplicate classification engine | VERIFIED (reuses `ScenarioTestKind` and `TestDesignAdvisor`) |
| 15 No duplicate scenario | VERIFIED (B28) |
| 16 Parallel port ownership safe | VERIFIED (P01–P06, 3×3 parallel processes) |
| 17 CI_DEF_3 and related tests under controlled parallelism | VERIFIED for the controlled runs (9/9). **CI_DEF_3 is still intermittent in full-suite runs under memory pressure.** |
| 18 Backend test data isolated | VERIFIED (temporary migrated databases; the run database is isolated by the harness) |
| 19 Targeted regression | VERIFIED |
| 20 Browser workflow | VERIFIED (16/16) |
| 21 No user plans changed | VERIFIED (fingerprints and all table counts identical) |
| 22 No old databases deleted | VERIFIED |
| 23 No commits or pushes | VERIFIED (this task) |
| 24 Report | this document |
