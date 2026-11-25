# Manual Testing workspace completeness — 2026-11-25

Branch `008-traceability-first`. The code is in commit `4731e5fd`; the hindrance-in-checklist fix and this report follow in the next commit. Nothing was pushed or reset, no database was dropped, both stashes are kept, and your plans and grouping are unchanged (fingerprints and table counts verified).

**Status key:**
- **IMPLEMENTED AND VERIFIED** — built and confirmed by tests or a browser check.
- **ALREADY PRESENT** — existed before this task and was reused.
- **PARTIAL** — some of it works or is shown.
- **NOT IMPLEMENTED** — does not exist.
- **NOT VERIFIED** — not checked.
- **BLOCKED** — something outside the task prevented it.

## A. Executive summary

The workspace already had most building blocks. The gaps were mostly things that existed but couldn't be **reached or seen**. This task made them usable:
- «Fra spesifikasjonen»;
- «Før du starter», so «Klar til kjøring» can be reached and a recorded hindrance blocks with its reason;
- test data approval and environment;
- named and openable related scenarios;
- case history;
- a read-only notice and retry;
- «hva mangler» counts with denominators;
- technical test coverage (`manual-testing-technical-coverage-2026-11-25.md`);
- Manuell/Teknisk on cases (`test-case-type-and-port-reliability-2026-11-25.md`).

**Test execution** was deliberately retired on 2026-11-19. You have now decided both options are wanted, and execution in BirkNext will be optional. The plan is in `manual-test-execution-plan-2026-11-25.md`; **it is not implemented in this task**.

## B. Starting state

- **Code:** HEAD `8a176fd7` at the start; the work was committed as `4731e5fd`.
- **App:** my local instance, started and stopped for builds.
- **Database:** the normal `birknext` database, read only by the walkthroughs' safety checks. Backend tests ran on temporary databases.

## C. Capability matrix

The audit was read-only: the code plus its tests.

| Capability | Status before | Existing implementation | Gap | Action |
|---|---|---|---|---|
| Scenario discovery, search, filters, grouping | EXISTS | `ManualTestingDiscover.razor`, `ScenarioBrowserPresentation` | — | Kept |
| Source identity, story/FR evidence | EXISTS | discovery, `StoryRequirementLinks` | — | Kept |
| Edge cases, clarifications, success criteria, dependencies, out of scope | EXISTS BUT NOT USABLE (extracted by `SpecExplorerService.BuildSemanticModel`, not shown) | `SpecificationSemanticModel` | Not in Manual Testing | **Exposed** («Fra spesifikasjonen», G) |
| Technical source tests | PARTIAL (count only, FR-linked) | `ExistingTests` | No scenario link, no evidence | **Implemented** (other report) |
| Manuell/Teknisk: scenario and case | PARTIAL | grouping decisions | Not on cases | **Implemented** (other report) |
| Case creation and editing, preconditions, steps, expected results | EXISTS | `PlanPreparePresentation.CaseForm`, Planlegg | — | Kept |
| Test data | PARTIAL | `TestDesignAdvisor.TestData`, picker | No approval or environment shown; no hint when none exist | **Improved** (I) |
| Test design variants | EXISTS | `ScenarioPlanning.Variants`, `TestDesignPanel` | No compound split | Kept; gap listed |
| Readiness | EXISTS BUT NOT USABLE | `TestDesignAdvisor.Readiness` | «Klar til kjøring» unreachable: no verification-method or access fields; a hindrance never blocked | **Fixed** (H) |
| Execution, results, evidence, defects, history | **Retired 2026-11-19** (dormant tables, contracts kept) | `ManualTestRunContracts`, `manual_test_runs` | No execution in BirkNext | **Plan written** (L); not implemented here |
| Change impact on saved cases | EXISTS | `PlanSourceChangesPanel`, `CaseSourceState` | Plan-wide refresh only | Kept; gap listed |
| Related/duplicate scenarios | PARTIAL (count line) | `ScenarioConsolidation`, `PossibleDuplicates` | Not named, not openable | **Implemented** (M) |
| E2E / journey links | EXISTS BUT NOT USABLE (legacy page) | `ScenarioCorrelation` | Not in Manual Testing | NOT IMPLEMENTED (AA) |
| Regression and packages | EXISTS BUT HIDDEN (legacy page) | `TestPlanSuggestionEngine.Recommend`, snapshots | Not in Planlegg | NOT IMPLEMENTED (AA) |
| Test lead overview | PARTIAL | landing `mt-counts` | No readiness split | **Improved** (Q) |
| Exports | EXISTS | `AdoExportWizard`, `AdoTestPlanExporter` | — | Kept |
| History | PARTIAL (stored, not shown) | `EditHistory`, `ReviewHistory`, `PlanVersionsPanel` | Case history invisible | **Implemented** (K) |
| Permissions | EXISTS on the server | write policy | No read-only notice in Planlegg | **Notice added** |
| Empty, loading and error states | EXISTS | all pages | No retry in Planlegg | **Retry added** |
| Navigation | EXISTS (two stages) | `ManualTestingPresentation.Stages` | — | Kept |

## G. «Fra spesifikasjonen» (IMPLEMENTED AND VERIFIED)

- **New:** `SpecContextPresentation` presents the **existing** semantic model.
- **Direct link only by an id the item names.** An item counts as directly related only when it names one of the scenario's requirement ids (FR-023 is not FR-023a; no prefix is stripped) or its story. Everything else stays under «Generelt for spesifikasjonen».
- **Labels:** «Spesielle situasjoner», «Avklaringer», «Hva løsningen skal oppnå», «Avhengigheter», «Dette inngår ikke».
- **Source markers are kept:**
  - clarifications: Gjeldende / Endret / Erstattet, with the source's note;
  - dependencies: Åpen / Lukket i spesifikasjonen, or Trukket. A dependency is never shown as a live blocker.
- **Where:** in Finn og forstå (the scenario's detail pane) and in Planlegg, when the scenario's specification is the one loaded.
- **Tests:** 5, on the real SkoleService specification fixture (27/48/11/13/7 items).
- **Browser:** a scenario showed «1 spesielle situasjoner · 5 avklaringer · 1 avhengigheter», and all five kinds were reachable at specification level. Planlegg showed 7 directly related items.

## H. Readiness (IMPLEMENTED AND VERIFIED)

- **«Før du starter» in the editor:**
  - «Slik ser du resultatet» (verification method);
  - «Tilgang og miljø»;
  - «Hindring (stopper testen nå)».
- **Saving:** these are person-owned edits with the normal save. Nothing is filled in for the tester; the source's wording is only a placeholder.
- **One rule added to the existing readiness:** a recorded hindrance means «Blokkert», with the hindrance as the reason.
- **The reason is visible.** It is the first question in the readiness checklist. This was found by the browser walkthrough: the page first said «Blokkert» without showing why, and that was fixed.
- **Never inferred.** A specification dependency never blocks a case by itself.

## I. Test data (IMPLEMENTED AND VERIFIED)

- **The picker** shows each asset's approval («godkjent» / «ikke godkjent») and environment («miljø: QA» / «miljø ikke oppgitt»). That is what the catalogue records; it is not proof the data exists in QA.
- **With no assets**, Planlegg links to «Registrer testdata».
- **Gap:** «beskrevet / generert / tilgjengelig i QA / verifisert» as separate states needs catalogue data that doesn't exist (AA).

## J. Technical coverage

See `manual-testing-technical-coverage-2026-11-25.md`.

## K. Test case preparation and history (IMPLEMENTED AND VERIFIED)

- **Case history:** «Historikk» lists edits and reviews, newest first, with field names in plain Norwegian.
- **Read-only:** a reader sees «Du har lesetilgang…».
- **Errors:** a load error offers «Last inn på nytt».

## L. Test execution and results (NOT IMPLEMENTED; plan written)

- **Your decision (2026-11-25):**
  - keep both options;
  - BirkNext execution is optional and off by default;
  - Azure DevOps stays the main place for now.
- **The plan:** `manual-test-execution-plan-2026-11-25.md`.
- **Kept untouched:** the dormant tables and contracts.

## M. Related scenarios (IMPLEMENTED AND VERIFIED by tests)

- «Relaterte scenarioer» names each related scenario with its relation and reason, and can open it. Possible overlaps (the same requirement from another source) are named too.
- Nothing is merged; each keeps its own identity.
- **Not checked in the browser:** the real scenarios checked had no relations.

## N. Change impact

ALREADY PRESENT (`PlanSourceChangesPanel`, per-case source state). Unchanged.

## O–P. E2E and regression packages

NOT IMPLEMENTED (AA). Both exist only on the legacy page.

## Q. Test lead overview (IMPLEMENTED AND VERIFIED)

- **New row on the landing page,** «Hva mangler før testene kan starte», with each count «n av N»:
  - «Mangler forventet resultat»;
  - «Uten koblet testdata»;
  - «Ikke klar til kjøring»;
  - «Gjennomført / bestått: Registreres i Azure DevOps».
- **The counts are the saved plans' own counts.** The labels say exactly what is counted: the summary's «Blocked» means not ready, so it is labelled «Ikke klar til kjøring».
- **Never shown:** an execution or pass number.

## R. Language and UX

- Plain Norwegian throughout.
- Technical details sit behind «Vis tekniske detaljer» (technical coverage).
- The specification context and history are collapsed until opened.

## S–T. Persistence and freshness

- **No new table and no migration.** The new fields use existing `TestCaseEdits` fields (`ObservationMethod`, `EnvironmentPrerequisites`, `BlockedBy`).
- **The specification context is read from the loaded specification.** A changed specification reloads it.

## U. Frontend regression

**9,673 passed / 0 failed** (full suite). New tests:
- W01–W05: «Før du starter» saved alone; hindrance blocks and is shown first; test-data labels and hint; history; read-only and retry;
- the landing counts;
- related scenarios;
- the specification context on the real spec.

## V. Backend regression

- Targeted test design / readiness / plans / manual tests: **747 passed / 0 failed**.
- Coverage-related tests: 227 passed / 0 failed / 3 skipped (opt-in).
- Schema and contract tests: 1,160 passed / 0 failed.
- **Full backend suite: BLOCKED.** Two attempts stopped at the RAM safety floor while other tools (WSL etc.) held about 5 GB. A full run is not claimed.

## W. Browser walkthrough (VERIFIED, disposable plan)

`scratchpad/comp_walk.py` was read before running. It wrote only into one disposable plan («TEMP gjennomgang 132905»), which was deleted afterwards by id and name. All table row counts were identical before and after; plan fingerprints `e20566b3…` and grouping revision 5 were unchanged.

**Result: 10 checks, 8 PASS, 2 FAIL as scripted, both resolved:**

| Check | Result |
|---|---|
| F1–F2: specification context, direct items named, five kinds at specification level | PASS |
| F3: source markers | FAIL as scripted — the chips are lowercased by CSS. The markers «endret», «gjeldende», «åpen i spesifikasjonen» were shown. |
| P1: «Før du starter» persists after save and reopen | PASS |
| P2: Testtype stays Manuell | PASS |
| P3: «Blokkert» shows why | FAIL as scripted — **a real defect**: «Blokkert» was shown without the hindrance among the checks. Fixed (the hindrance is now the first checklist question), unit-tested; **not re-run in the browser.** |
| P4: case history visible | PASS |
| L1: landing counts with denominators, no execution number | PASS |
| S1: plan writes only to the disposable plan | PASS |
| S2: no console or API errors | PASS |

**An incident in this session, corrected:** an earlier read-only check switched the selected specification and its switch back wasn't auto-saved. Your workspace was left on «Avhengigheter for SkoleService før go-live». It was restored to SkoleService 001 through the UI and verified after a fresh reload. Every walkthrough now waits for the auto-save before closing.

## X. Performance (measured, this walkthrough)

| Step | Time |
|---|---|
| Open Planlegg (plan + workspace) | 23.5 s |
| Save a case | 1.6 s |
| Reopen Planlegg | 23.1 s |
| Landing page | 14.5 s |
| Opening a scenario including the coverage section (coverage walkthrough) | 70 ms |

The 23 s opening time is dominated by the existing workspace and discovery load, not by this task's additions. It deserves its own investigation (see Z).

## Y. Modified files

See commit `4731e5fd`, plus the follow-up commit:
- `ScenarioWorkspacePresentation.cs` (hindrance in the checklist);
- `ManualTestingPlanCompletenessTests.cs`;
- this report;
- `manual-test-execution-plan-2026-11-25.md`.

## Z. Known limitations

- The full backend suite is BLOCKED by RAM.
- Opening Planlegg is slow (about 23 s; existing load path).
- Test data has no QA-availability states.
- E2E, journey and regression packages are not in Manual Testing.
- There is no per-case acceptance of a source change.
- Compound scenarios are not split.

## AA. Remaining gaps, in priority order

1. Execution in BirkNext (optional, phase 1 of the plan).
2. Fetching Azure DevOps results (phase 2).
3. Opening time of Planlegg and Manual Testing.
4. E2E / journey coverage in Finn og forstå.
5. Regression selection in Planlegg.
6. Test data availability states.

## AB. Acceptance criteria

| AC | Status |
|---|---|
| 01 All capabilities audited against code | IMPLEMENTED AND VERIFIED (C) |
| 02 Working features reused | VERIFIED |
| 03 No duplicate subsystem | VERIFIED |
| 04 Scenario-first | VERIFIED |
| 05 Specification context, direct vs general | IMPLEMENTED AND VERIFIED |
| 06 Special situations and clarifications in workflows | IMPLEMENTED AND VERIFIED |
| 07 Dependencies and success criteria without jargon | IMPLEMENTED AND VERIFIED |
| 08 Readiness understandable | IMPLEMENTED AND VERIFIED |
| 09 Prerequisites can be reviewed | IMPLEMENTED AND VERIFIED |
| 10 Test data availability not invented | VERIFIED |
| 11 Technical tests with correct evidence status | IMPLEMENTED AND VERIFIED |
| 12 Candidates do not imply coverage | VERIFIED |
| 13 Case type preserved | VERIFIED |
| 14 Saved cases not overwritten | VERIFIED |
| 15 Preparation has steps, data and expected results | ALREADY PRESENT + «Før du starter» |
| 16 Execution reused and completed | NOT IMPLEMENTED — your decision 2026-11-25; plan written |
| 17 Results and history durable | NOT IMPLEMENTED (plan) |
| 18 Prepared / executed / passed / blocked distinct | PARTIAL (prepared and not-ready shown; execution in Azure DevOps) |
| 19 Change impact does not rewrite cases | ALREADY PRESENT |
| 20 Related scenarios do not merge identities | IMPLEMENTED AND VERIFIED |
| 21 E2E links need evidence | NOT IMPLEMENTED (gap) |
| 22 Regression selections preserved | NOT IMPLEMENTED (gap) |
| 23 Test lead overview uses real evidence | IMPLEMENTED AND VERIFIED |
| 24 Plain Norwegian | VERIFIED |
| 25 Technical details on demand | VERIFIED |
| 26 No duplicate coverage or readiness engine | VERIFIED |
| 27 No data reset or destructive migration | VERIFIED |
| 28 Regression tests run safely | VERIFIED (targeted); full backend BLOCKED |
| 29 Browser walkthrough | VERIFIED (8/10; both failures resolved, one fixed defect not re-run in the browser) |
| 30 Performance assessed | VERIFIED (measured) |
| 31 Commits | Committed as you asked; no push |
| 32 Report | this document |
