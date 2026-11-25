# Technical test coverage in Manual Testing — 2026-11-25

Branch `008-traceability-first`, on top of `8a176fd7`. The work is uncommitted for now; you asked for one commit after the queued tasks. Nothing was reset, no database was dropped, and both stashes are kept.

This report also covers two earlier open gaps, closed first (section 0).

**Status key:**
- **VERIFIED** — implemented and confirmed by a test, a browser check or real source.
- **IMPLEMENTED** — written and tested, but not confirmed end to end.
- **PARTIAL** — only part of the brief is done.
- **NOT RUN** — the check was not executed.
- **BLOCKED** — something outside the task prevented the check.

## 0. Gaps closed first

**ZAP container port (VERIFIED).** `FrontendZapPassiveReviewService` used to pick a free port, release it and publish the container on that number, which another process could take in between.
- Now the container is published with `-p 127.0.0.1::8080`, so the runtime chooses and binds the host port itself. The service reads it back with `<runtime> port <container> 8080/tcp` (`ParsePublishedPort`; only a loopback binding counts).
- If the port can't be read, the run fails honestly ("published port could not be determined"), and the existing cleanup still removes the container.
- **Tests:** ZAP, passive-security and readiness tests 72 passed, 6 skipped. The skipped ones need a real ZAP container, which is not allowed here. New tests cover the port parsing (9 cases) and the failure path, including that the container is removed.

**«Begge» / «Ikke vurdert» (VERIFIED).**
- `ScenarioTestKind` gains `Both`, but only as a **scenario** decision. A saved test case is still Manuell or Teknisk: the server refuses «Begge» on a new case (400 `invalid-test-kind`) and never stores it on save.
- In Finn og forstå the scenario control offers **Manuell | Teknisk | Begge**. With no decision it reads «Ikke vurdert. Automatisk forslag: …».
- A «Begge» scenario appears in both the Manuell and the Teknisk view, is counted once under «Alle», gets its own «begge» count on cards, and is never hidden as technical.
- In Planlegg, a scenario reviewed as «Begge» preselects nothing; the tester chooses the case type.
- **Tests:** frontend tests and backend B33 / V_kind_Begge. Browser check W4/W5 below.

## A. What already existed (reused)

- **Test discovery:** `DotNetXunitTestDiscoveryProvider` runs at Source Analysis upload and finds xUnit `[Fact]`/`[Theory]` methods. Test projects are recognised from project metadata, never from folder names. Each test's type comes from its project: unit, integration, contract or component.
- **Requirement references** are read from traits and comments, with a confidence level. Each test keeps a fingerprint of its method text.
- **Scenario field:** `DiscoveredScenario.ExistingTests` (`CandidateTestAssociation`) was filled only through requirement ids.
- **Test runs:** TRX results can be imported and matched to tests (`TrxTestExecutionEvidenceProvider`, `TestEvidenceCorrelation`). They are not stored where Manual Testing can read them.
- **Reviewer decisions:** each specification already has a decision document (grouping, roles, Manuell/Teknisk) with revisions, history and retry protection.

## B. What already existed for linking (reused, not duplicated)

- Requirement-based test links (`TestLinks`), the canonical Test VERIFIES Requirement rules, and the test coverage engine's overlaps.
- No second linking engine was built; the scenario link is a small addition to the same discovery and Finalize path.

## C. Gaps found

1. **No test was ever tied to an acceptance scenario.** Only requirement ids were matched, so a test saying «US5 scenarios 1–9» or named `Scenario_4_…` (the real SkoleService convention) was ignored.
2. **Scenario lists only showed a count.** The test association carried no test type, class, method, or fingerprint.
3. **No reviewer decision existed** for a test and a scenario, and no coverage extent.
4. **Older imports** could not be told apart from "no test".

## D. Changes to the data model (additive)

**Test discovery** (`shared/TestEvidenceContracts.cs`)
- `SourceTestDefinition.Scenarios` holds the scenarios a test names: story, number, confidence, reason and line.
- `SourceTestInventory.ScenarioReferenceRules` records whether scenario references were looked for (0 means an older discovery).

**Scenario discovery** (`shared/TestScenarioDiscoveryContracts.cs`)
- `CandidateTestAssociation` gains `Origin` (scenario or shared requirement), `Layer`, `Framework`, `ClassName`, `MethodName` and `SourceFingerprint`.
- `ScenarioDiscoveryResult.TestsReadForScenarios` says whether the source's tests were read for scenario references.

**Decisions** (`shared/StoryGrouping.cs`)
- `TestLinks` (`ScenarioTestLinkDecision`): review (confirmed, rejected or cleared), extent (not assessed, partial or full), the test and scenario fingerprints it was made on, note, by and at.
- New operation `SetScenarioTestLink`.

**No new database table, no migration.** Everything is in existing JSON documents; older documents read without the new fields.

## E. How a test is identified

- Only discovered xUnit tests count: a method with `[Fact]` or `[Theory]` in a test project. A production method, or any method without those attributes, never counts (test T07).
- A test's identity is its stable definition id (repository, project and full name), plus its fingerprint.

## F. How a test is tied to a scenario

**What counts as a reference to a scenario**
- **Explicit:**
  - a trait naming the scenario («US5-AS9» or «US5 scenario 9»);
  - the test's own documentation comment;
  - a method named `Scenario_N_…` / `Scenarios_N_and_M_…` whose number the class summary gives to **exactly one** story.
- **Candidate only:**
  - the class summary gives that number to several stories;
  - the scenario is mentioned in a comment inside the test body;
  - the class is about exactly one scenario and the method doesn't name a number.
- **No link:** a class about several scenarios never links all its tests to all of them.

**Scope**
- A reference applies only to the scenario with that story and number in a specification **of the test's own module** (its first folder).
- If the module has the same story and scenario number in more than one specification, every match stays a candidate.
- Scenario 5 in another story, or in another service, is never matched (tests T08, T12).

**Weaker evidence**
- A test that shares only a requirement with the scenario is shown as «Samme krav, ikke koblet til scenarioet». It is supporting evidence, never a link.

**Counting**
- One test can name several scenarios, and one scenario can have several tests. Duplicate evidence never adds a second entry (test T09).

## G. Review and history

- In the scenario's detail pane, a tester with write access to the specification's decisions can choose «Bekreft kobling», «Avvis», «Dekker deler», «Dekker hele scenarioet» and «Fjern vurderingen».
- «Dekker hele scenarioet» is only available on a confirmed link; the engine refuses an extent on a rejected link.
- Every decision is saved with who and when, in the specification's decision document with revision and history.
- Decisions never change the test, the scenario, the scenario's Manuell/Teknisk decision, or any test case (test T11).

## H. Coverage extent

- «Dekning ikke vurdert» is the default, also for an explicit or confirmed link.
- «Dekker deler av / hele scenarioet» comes **only** from a reviewer's assessment.
- When the assessment becomes stale, the stale extent is never shown.

## I. Test results

Manual Testing holds no test results, so it always shows «Testresultat ikke tilgjengelig». Nothing is ever shown as passed. **Connecting stored TRX results is NOT IMPLEMENTED** (see U).

## J. Finn og forstå (VERIFIED)

- **Scenario list:** a short tag (· automatisk test / mulig automatisk test / delvis kontrollert / kontrollert / kontroller koblingen) appears only when there is something to say.
- **Detail pane — «Hva er allerede kontrollert?»:**
  - the overall state and «Testresultat ikke tilgjengelig»;
  - per test: its type in Norwegian (Integrasjonstest …), a readable name, how it is tied to the scenario, the assessed extent, and why a review is needed;
  - «Vis tekniske detaljer» shows class, method, file:line, reason and framework;
  - honest empty texts for «ikke vurdert» (with how to fix it) and «ingen kjent automatisk test» («betyr ikke at det er risikofritt»).
- **«Flere filtre» › Automatiske tester:** ingen kjent, mulig, funnet, delvis, kontrollert (vurdert), kontroller koblingen, ikke vurdert.

## K. Planlegg og klargjør (IMPLEMENTED)

- An expandable «Hva er allerede kontrollert?» under the scenario heading of the open case: read-only, same states and labels, and the note «automatiske tester fjerner ingenting her».
- Nothing marks a case ready or executed, and existing case types and content are untouched.
- **Not browser-checked:** your plan's cases come from the same older import, so it would only show «ikke vurdert».

## L. Filters and prioritisation

- The new filter combines with the existing ones (P1/P2, queue, approach, Manuell/Teknisk).
- «Ingen kjent» never means high risk, and no score was added.

## M. Real SkoleService (VERIFIED, read-only)

An opt-in test reads `Downloads/M2LB (8)/SkoleService` when `BIRKNEXT_REAL_ARCHIVE_DIR` is set; otherwise it is reported as skipped.

**Result:**
- **401 tests** discovered; **22** name at least one scenario.
- **17** references are explicit and **7** are candidates.
- `CorrectionAndChainTests.Scenarios_2_and_8_and_9_two_reporters_are_both_flagged_and_overlap_from_one_reporter_is_flagged_only_when_dates_overlap` names US5 scenarios 2, 8 and 9 (explicit). It is an integration test in `SkoleService/tests/M2LB.SkoleService.Integration/ReportIntake/CorrectionAndChainTests.cs`.
- Every link stays in the SkoleService module.

**Other examples in the brief:**
- Unknown absence code, invalid XML, Person unavailable and dead-letter: **no SkoleService test names those scenarios**, so nothing is linked. Their requirement-based tests stay «samme krav».
- **Unknown child:** `HeldReportTests` (US4 scenarios 1, 2, 4, 5 and US2 scenarios 6, 8) and `HeldAtLockTests` (US4 scenario 3).
  - `HeldAtLockTests` names a single scenario but has no `Scenario_N` methods, so its tests become candidates only.
  - `HeldReportTests` methods without numbers get no link.
- **Not checked:** MeldingsutvekslerReceiver tests were not read, because the real-source test only covers the SkoleService folder.

## N. Re-import and freshness

- A decision stores the fingerprints of the test and the scenario it was made on. If either changes, it shows «Må vurderes på nytt» with the reason.
- A rejected link stays rejected until the test or scenario changes.
- A reviewed test that no longer exists is counted («n tidligere vurderte tester finnes ikke lenger i kilden»); the decision is kept, not deleted.
- **Older imports:** imports discovered before this change have no scenario references and show «ikke vurdert». A new import of the source fills them in. I did not re-import, because that writes new source data into your workspace.

## O. Backend tests (VERIFIED, targeted)

- New `ScenarioTestReferenceTests`: 7 passed, plus 1 real-source test that is skipped by default and **passed on opt-in**.
- Touched areas (test evidence, discovery, grouping, planning, system guide, consolidation, coverage): 227 passed, 3 skipped (the opt-in real-archive tests).
- Schema, snapshot, GraphQL and contract tests: 1,160 passed.
- Grouping, planning and ZAP: 157 passed.
- **Full backend suite: NOT RUN** in this task. The two earlier attempts stopped at the RAM safety floor while other tools held about 5 GB (see `test-case-type-and-port-reliability-2026-11-25.md`, section O).

## P. Frontend tests (VERIFIED)

**Full frontend suite: 9,661 passed / 0 failed.**

- `TechnicalCoveragePresentationTests` covers: no test versus not assessed; candidate versus found; never full without a reviewer; partial and full; never «bestått»; rejection and staleness; shared requirement; removed tests; details; filters.
- A new page test in Finn og forstå covers the tag, detail labels, details on demand, no review actions without write access, and the filter.

## Q. Browser check (VERIFIED, read-only, real workspace)

`scratchpad/tech_walk.py` only navigates, filters and opens details; it never reviews or saves. **Result: 6 of 6 checks passed.**

- **W1:** the scenario list is shown.
- **W2:** the scenario shows «Automatiske tester ikke vurdert» with the re-import note, and never «bestått».
- **W3:** the filter is offered with 8 options.
- **W4:** «Manuell | Teknisk | Begge» is offered.
- **W5:** a scenario without a decision reads «Ikke vurdert. Automatisk forslag: manuell — …».
- **W6:** no console or API errors.

Row counts in all tables were identical before and after. The only writes were the app's own session and read-only traceability calls.

**Not checked in the browser** (each would need your workspace re-imported or your decisions written): the explicit / candidate / confirmed states on real data, and the review actions. These are covered by the tests above.

## R. Performance (measured)

| Measurement | Result |
|---|---|
| Opening Finn og forstå (655 scenarios) | 10.7 s, dominated by the existing workspace load |
| Opening a scenario including the coverage section | 70 ms |
| The coverage filter | 559 ms |
| Real-source discovery (401 tests, read from disk and zipped in memory) | about 6 s |

Coverage is computed from data already loaded, with no extra API call per scenario.

## S. Changed files

**Contracts**
- `shared/TestEvidenceContracts.cs`
- `shared/TestScenarioDiscoveryContracts.cs`
- `shared/StoryGrouping.cs`

**Backend**
- `backend/BirkNext.Api/Services/TestEvidence/DotNetXunitTestDiscoveryProvider.cs`
- `backend/BirkNext.Api/Services/TestPlanning/TestScenarioDiscoveryEngine.cs`
- `backend/BirkNext.Api/Services/TestPlanning/TestScenarioDiscoveryService.cs`
- `backend/BirkNext.Api/Services/TestPlanning/TestPlanSuggestionService.cs` («Begge» guard)
- `backend/BirkNext.Api/Services/FrontendPassiveSecurity/FrontendZapPassiveReviewService.cs`

**Frontend**
- `frontend/BirkNext.Web/Services/TechnicalCoveragePresentation.cs` (new)
- `frontend/BirkNext.Web/Services/ScenarioBrowserPresentation.cs`
- `frontend/BirkNext.Web/Services/PlanPreparePresentation.cs`
- `frontend/BirkNext.Web/Pages/ManualTestingDiscover.razor`
- `frontend/BirkNext.Web/Pages/ManualTestingPlan.razor`
- `frontend/BirkNext.Web/wwwroot/css/manual-testing.css`

**Tests**
- `ScenarioTestReferenceTests.cs` (new)
- `TechnicalCoveragePresentationTests.cs` (new)
- `ScenarioTestKindTests.cs`
- `ManualTestingPlanCaseKindTests.cs`
- `DiscoverWorkspaceTests.cs`
- `PassiveSecurityContractTests.cs`
- `PassiveSecurityLifecycleTests.cs`
- `ScenarioCentricPlanningServiceTests.cs`
- `StoryGroupingServiceTests.cs`

## T. Compatibility

- All new fields are optional and nullable.
- Older inventories, discovery results and decision documents read as before, with «ikke vurdert» where the information is missing.
- Plan JSON is unchanged for cases without a type.
- `BuildContainerArguments` lost its port parameter; it is internal and its two callers are updated.

## U. Known limitations

1. **Results are not connected.** Manual Testing does not read imported TRX results, so it shows «Testresultat ikke tilgjengelig» throughout.
2. **Only xUnit is discovered.** NUnit, MSTest, Playwright and Jest are reported as limitations and never linked.
3. **Compound scenarios** are only covered through a reviewer's «dekker deler»; there is no link at test-design-variant level.
4. **«Legg til en eksisterende test»** (a reviewer adding a link the source does not state) is NOT IMPLEMENTED. Only links the discovery found can be reviewed.
5. **Your current workspace needs a re-import** to show scenario links.

## V. Remaining risks

- The `Scenario_N` naming convention is generic but convention-based. A suite with another convention gets no links, only «ingen kjent» or «samme krav» — never wrong links.
- The full backend suite still needs a run when enough RAM is free.

## W. Acceptance criteria

| AC | Status |
|---|---|
| 01 Existing discovery reused | VERIFIED |
| 02 No duplicate correlation engine | VERIFIED (same discovery/Finalize path) |
| 03 Scenario identity source-scoped | VERIFIED (module + story + number; T08/T12) |
| 04 Test identity source-scoped | VERIFIED (stable definition id + fingerprint) |
| 05 Explicit and candidate links distinct | VERIFIED |
| 06 Reviewer decisions persist | VERIFIED (engine; same store as other decisions) |
| 07 Rejected links respected | VERIFIED |
| 08 Extent independent of the link | VERIFIED |
| 09 Execution independent of the test existing | VERIFIED (never a result) |
| 10 Compound scenarios can be partial | PARTIAL (reviewer «dekker deler»; no variant level) |
| 11 Finn og forstå shows coverage | VERIFIED |
| 12 Planlegg shows coverage | IMPLEMENTED (not browser-checked) |
| 13 Evidence can be inspected | VERIFIED |
| 14 Gaps identifiable | VERIFIED (states and filter) |
| 15 Manuell/Teknisk selections intact | VERIFIED |
| 16 Saved cases intact | VERIFIED (table counts) |
| 17 Plan revisions intact | VERIFIED |
| 18 Re-import handles stale links | VERIFIED (fingerprints) |
| 19 No candidate inflates execution | VERIFIED |
| 20 No link marks a scenario ready or passed | VERIFIED |
| 21 Filters without duplicate counts | VERIFIED |
| 22 Extent from assessed assertions | VERIFIED (reviewer-only) |
| 23 Real SkoleService verified | VERIFIED (opt-in run, read-only) |
| 24 Backend tests isolated | VERIFIED |
| 25 Targeted regression | VERIFIED |
| 26 Browser walkthrough | PARTIAL (read-only; review actions on real data would need writes) |
| 27 Performance measured | VERIFIED |
| 28 No destructive operations | VERIFIED |
| 29 No commits or pushes | Uncommitted for now; commit after the queued tasks, as you asked. No push. |
| 30 Report | this document |
