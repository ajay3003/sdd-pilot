# BirkNext — V10 Manual Testing hardening (evidence-based), 2026-11-27

**Sources:**
- `BirkNext_Sluttrapport_Manuell_E2E_2026-11-27.docx` (findings)
- `BirkNext_Testguide_Manuelle_Testere_2026-11-27.docx` (MT-01 … MT-44)

**Commits** (the user asked for commits during the work, overriding the brief's no-commit rule; nothing pushed):
- `7d08f3a3` BUG-005 (launcher, see its own report)
- `402ebb50` V10

The earlier commits of the day (`b44c98d5` … `add3ea3e`) are in `birknext-manual-testing-search-preparation-layout-v10-2026-11-27.md`.

**Safety:**
- No database reset.
- The normal TesterNative data, the repository's `App_Data` and the reset backups were not touched.
- Browser tests ran only on isolated instances: ports 5097/5197, a disposable `birknext_adoverify_*` database, own App_Data, published from a clean source copy.
- No Azure DevOps writes; no Docker or Podman.

**Classification:**
- FIXED: code changed, with a test that fails without the change.
- VERIFIED-NOT-A-BUG
- OPEN-NEEDS-DECISION
- NOT-TESTED

## Phase 0 — root causes (recorded before changing code)

| Finding | Root cause (file) |
|---|---|
| FUN-015 «1 scenario» vs «2 scenarioer» | Planlegg (`PlanPreparePresentation.ScenarioCount`) and Testplaner (`TestPlanSuggestionService.Summary`) counted **scenarios that have a test case**. Testgjennomføring counts **occurrences**, including scenario-only items (`Occurrences`). Three rules for one number. |
| FUN-011 AC4 «moved» to «Module AutorisasjonService» | Preparing a case for an already planned scenario (`CreateProjectAsync` append) gave the new case its **area suite** (`ProjectSuites`). Execution then resolved the scenario's suite from **the case**, not from its planned item. The scenario's membership itself never changed in the data. The tester's move back changed the planned item, and the results stayed because they are keyed by scenario. |
| FUN-016 «Ingen endringer i testtilfellene» for revision 8 | `PlanRevisionDiff.Compare` compared plan name, groups and **test cases** only. `PlannedScenarios` (scenario-only) were not compared. |
| BUG-001 dialog closes on a backdrop click | `AddScenariosToPlan`: `@onclick="CloseAsync"` on the backdrop; Escape closed unconditionally. The same backdrop pattern was on `KpiTileEditor`. |
| FUN-017 «549 nye scenarioer» | The number means **«in the plan's current source but not in this plan»**, not «new since the previous import». The text called them «nye». Both computations also counted scenarios planned **without** a case as «new» (`Sources.cs` impact and status). |
| BUG-003 «Vis teknisk innhold» off after F5 | The toggle was page state only. It was switched on automatically only for a `?case=` link to a technical case, not for an open case restored another way. |
| BUG-004 approved with a step warning | **Product policy as implemented:** approval = exportable content. Every step needs an action and an expected result; there must be no unresolved conflict and no source change. The step-quality warning is documented as «advice, never a block», and scenario readiness is assessed separately (V5). The policy is consistent; the approval simply did not say so. |
| BUG-002 preview followed by a case and module group | **Not reproducible as causation.** The preview dialog has only «Skriv ut» and «Lukk», and `OpenPreview` changes page state only. The one action that creates a case in that row is «Forbered testtilfelle», right beside «Forhåndsvis» and «Klargjør AC». It creates on one click and, before the FUN-011 fix, put the case in a module group, which matches revision 3. **Not proven.** |
| FUN-018 export unclear | The export tab said «Eksporter til fil». No single summary showed occurrences, exportable cases, omitted scenarios and reasons. |
| P1 picker limited to 2,000 | `AddScenariosToPlan` listed `Discovery.Candidates`, the window a page receives (2,000 since `b44c98d5`). Scenarios beyond it could not be found or chosen. |

## Changes and verification per finding

| ID | Status | Change | Automated evidence (fails before → passes after) |
|---|---|---|---|
| P1 «Legg til scenarioer» | **FIXED** | The dialog searches the scenarios not loaded **on the server**: the same project and the discovery's own module/specification selection (`SelectionScope`), the dialog's module, level, priority and search, and only plannable scenarios (`ScenarioSearchFilter.Module` / `ClassifiableOnly`). Paged (40, «Vis flere»). Choices are kept by id across searches and pages. Rows already listed are not repeated. «Allerede i testplanen» is marked. Nothing is fetched without words or a filter. The unloaded scenarios' modules and priorities are offered. A different discovery id says so instead of mixing. | Frontend `ASP_*` (3): **failed on the old dialog, pass now**. Backend: `ASP_server_search_honours…`, `ASP_a_scenario_beyond_the_window…can be planned by its id` (no duplicate membership). |
| FUN-015 | **FIXED** | One shared definition, `PlanOccurrences.Of` (shared): Planlegg header, Testplaner summary, execution, export summary and revision diff all use it. A scenario-only item counts; a scenario with two cases counts once; a case is never an occurrence of its own scenario. | Backend `FUN015_FUN011_…` (summary 2, cases 1, execution 2, copy 2 with NotRun): **failed before**. Frontend `FUN015_the_plan_header…` («1 scenario» before → «2 scenarioer»). |
| FUN-011 | **FIXED** | (1) A case prepared for a planned scenario goes into that scenario's suite, with no new «Module …» suite. (2) An occurrence's suite is the planned item's suite, which corrects existing plans written before the fix. No migration; history untouched. | Backend `FUN015_FUN011_…`, `FUN011_an_existing_plan…`: **failed before**. Blocked result kept. |
| FUN-016 | **FIXED** | The diff names scenario occurrences added (with/without case, which suite), removed and moved (before → after), and «testtilfelle forberedt for scenarioet», with «US1 · AC 4 — title» or the test's own id. Output grouped Testplanen / Scenarioer / Testgrupper / Testtilfeller. The empty text says scenarios, groups and cases. The panel says results are not part of a revision. Historical revisions are read only; nothing is rewritten. | `FUN016_*` (2). The old code had no scenario comparison at all (by construction). Existing diff tests updated for the clearer case wording. |
| BUG-001 | **FIXED** | Backdrop never closes. Lukk/Escape close at once without unsaved work and **ask first** with it («N valgte scenarioer er ikke lagt til. Lukke uten å legge til?»; Escape on the question keeps the dialog). Focus goes to the dialog on open, to «Fortsett å velge» on the question, and back to the opener on close. Same backdrop rule for «Tilpass flis». The read-only preview keeps its backdrop close. | `BUG001_*` (2): **failed before** (backdrop closed; focus not returned). |
| FUN-017 | **FIXED** (wording + count) | Scenario-only items no longer count as «not in the plan». The text reads «N scenarioer i gjeldende kilde er ikke i denne testplanen ennå», plus whether the source changed since the plan was made. Legacy page «Nye scenarier» → «Ikke i testplanen». | `FUN017_*`: **failed before** (`{"b","c"}` instead of `{"c"}`). |
| BUG-003 | **FIXED** | The explicit toggle is kept per tab and project (session storage). An open technical case hidden by the list is explained, with one-click «Vis teknisk innhold». | `BUG003_*` (2). |
| BUG-004 | **VERIFIED-NOT-A-BUG** (policy) + **FIXED** (explanation) | Policy unchanged. With step warnings, «Godkjenn» asks first, lists the warnings, says that approval does not make the scenario ready, and records «Godkjent med N advarsel(er) …» in the review history. Without warnings it approves at once. Approval never changes readiness or results. | `BUG004_*`. The existing approval test now answers the question (intended behaviour change). |
| BUG-002 | **IMPLEMENTED** (UX, decision 2 of 2026-11-27; causation still unproven) | Preview and «Klargjør AC» are proven read-only. They now sit in their own group, «Kun visning:» (dashed frame, `role=group`, «Se uten å endre (lagrer ingenting)»), with tooltips saying nothing is saved and no case is created. «+ Forbered testtilfelle» is a separate button (not a link) in its own group, after a divider, with the visible hint «Oppretter et testtilfelle». It creates on the explicit click. **No confirmation dialog**: the action is not destructive. | `BUG002_previewing…never_writes`; new `BUG002_looking_and_creating_are_separate_groups…` (groups, roles, tooltips, button vs link, no dialog, exactly one create call on the explicit click). Browser: preview and readiness write nothing (network trace). |
| FUN-018 | **FIXED** (wording/summary) | «Eksporter testtilfeller til fil». A summary lists occurrences, exportable cases, scenario-only items omitted («Azure DevOps tar imot testtilfeller, ikke scenarioer») and excluded cases. File content was not changed. | `FUN018_*`. |
| FUN-014 E2E KPI 0 → 8 / 11 = 8 AC + 3 others | **IMPLEMENTED** (see «FUN-014» below) | The tile opens exactly the scenarios it counts, in the follow-up workspace. It shows a breakdown («i alt · med testtilfelle · uten testtilfelle») and a definition. Before, it opened Finn og forstå with only a level filter. | `TileDrill`; T03 updated (tile → its own list, count = tile, meaning shown). |
| Discover UX items (E2E list without nested groups, canonical identity display for repeated TEST-E ids, KPI basis, selection action bar, readiness form compactness, 100 % registered ≠ passed, dashboard «1 blokkert») | **NOT-TESTED / not implemented** in V10 | They are UX redesign items. The user decided to check V10 against the test guide before larger UX work. | — |
| Server search with status, scenario type and test method filters (brief: synthetic 11,999) | **PARTIAL**; test method **OPEN** (decision 5) | Server search applies area, type, story, requirement, priority, test level and module (and plannable). **Status (preparation), scenario type (reviewer decisions) and test method are page state.** They are not applied to unloaded matches, and the page names them («Ikke brukt på disse treffene: …»). | `DiscoveryCapTests`, Discover tests. |

## Decisions of 2026-11-27 (user) and what was done

| # | Decision | Result |
|---|---|---|
| 1 | BUG-005 stays PARTIALLY VERIFIED until a CI package passes on an authorized tester PC | **OPEN**: tester instructions are in the BUG-005 report. No security control was bypassed. |
| 2 | BUG-002: a UX separation, not a confirmation dialog | **IMPLEMENTED + VERIFIED** (bUnit). See the BUG-002 row. |
| 3 | Requirements without an acceptance scenario stay in the data but are not counted as scenarios | **IMPLEMENTED + VERIFIED** (bUnit). Details below. |
| 4 | The 66 shared requirement identities: a proposal, no migration | **REQUIRES DECISION**: `birknext-requirement-identity-migration-proposal-2026-11-27.md`. Nothing persistent was changed. |
| 5 | Test-method filter only from trustworthy canonical data through a shared selector | **OPEN** (not implemented). Details below. |
| 6 | FUN-014: one consistent E2E definition and a correct drill-down | **IMPLEMENTED + VERIFIED** (bUnit). Details below. |

### Decision 3: requirements are a separate figure
- «Oversikt per modul»: «Scenarioer» and «Med testtilfeller» count **acceptance scenarios and documented tests only**. A new column, «Krav uten scenario», shows the requirements that still need an acceptance scenario. They stay in the data, the traceability and Finn og forstå.
- The coverage chart and its drill-down use the same rule, so the drill-down opens what the bar counts.
- Project-wide scenarios («Prosjektnivå») and scenarios without a module («Uten modultilknytning») are separate rows under «Utenfor modulene» (project-level audit).
- Example, M2LB AutorisasjonService: 93 = 40 acceptance scenarios + 53 requirements. The table now shows 40 in «Scenarioer» and 53 in «Krav uten scenario».
- Tests:
  - `DEC3_requirements_without_a_scenario…`: table, coverage and drill-down;
  - landing-page row expectation updated (intended change: a new column).

### Decision 5: test-method filter
- What exists:
  - a reviewer's per-scenario decision (`ScenarioTestKindDecision`, Manuell / Teknisk / Begge), stored in **each specification's** grouping document and read by `StoryGrouping.TestKindDecision(doc, id)`;
  - a heuristic suggestion;
  - a case's own test kind.
- What does not exist: a **cross-specification** selector that the server search could apply to scenarios not loaded. Most scenarios have no decision («Ikke vurdert»), and the suggestion is not canonical.
- Result: the filter is **left out of the server search**. The page still names it among the filters not applied to unloaded matches. Adding it needs a shared selector over every specification's decisions; that is new work, not V10.

### FUN-014: the E2E KPI
**Definition** (shown on the tile's list):
- The E2E tile counts **documented E2E tests**: tests in the source's test specifications that declare themselves end-to-end (for example TEST-E), unique by source identity, across the project.
- The level is **declared by the document, not verified**.

**Root causes:**
- **«0 → 8».** Documented tests are read from the stored discovery. Before the discovery that reads the test specifications had run, there were none to count. This is not a counting defect.
- **«8 → 11».** The tile opened Finn og forstå with only a level filter. There, that filter applies to the selected module or specification **and to every suggestion of that level**: in M2LB, 8 documented tests + 3 E2E candidates (integration-flow suggestions).

**Fix:** the tile now opens **exactly its own population** in the follow-up workspace (count = tile), with the definition and the breakdown.

**The five E2E populations, kept apart:**

| Population | Where it is shown |
|---|---|
| Documented E2E acceptance scenarios / tests | The E2E tile and its list |
| E2E candidates (suggestions) | Finn og forstå's level filter, marked as suggestions; **not** in the tile |
| E2E in test plans | Per plan in Testplaner. The overview has no per-scenario plan membership, so the tile does not claim it. |
| Prepared E2E cases | «med testtilfelle» in the tile's breakdown. Prepared ≠ run. |
| Executed / verified E2E | Testgjennomføring, per plan. **Not** on the overview tile. |

## Test results

| Suite | Result |
|---|---|
| Frontend, full | **9,895 / 9,895** passed (2026-11-27, after every V10 change) |
| Backend TestPlanning (V10 changes) | **607 / 607** |
| Backend launcher + offline translation (BUG-005) | **97 / 97** |
| Backend, complete, partitioned (committed `test-backend-partitioned.sh`, 4 threads, per-partition peak memory) | **PENDING** (running; result below) |
| Browser, isolated v1env33 | **PENDING** (running; result below) |

### Backend memory (measured today; V11 continues the investigation)
- One test host per **folder**: 68 units, 7,651 tests, **0 failed**, 20 skipped. Peaks:

  | Folder | Peak | Tests |
  |---|---|---|
  | `Services/TestPlanning` | 2.37 GB | 602 |
  | `Integration` | 2.09 GB | 54 |
  | `Services` root | 1.99 GB | 631 |
  | `Controllers` | 1.21 GB | 196 |

  Everything else ≤ 0.84 GB.
- One host per **class** (119 classes): the heaviest are
  - `WorkspaceDocumentBodiesTests`: 1.31 GB / 8 tests (deliberately writes 5–75 MB workspaces);
  - `ScenariosBatchMutationTests`: 1.21 GB / 13;
  - `ScenariosMutationTests`: 0.92 GB;
  - `ScenariosQueryTests`: 0.74 GB.

  The last three are integration tests that each start a complete in-process API with PostgreSQL, some up to three times.
- Partitions peak at about the **sum** of their folders: memory accumulates within one process and is not returned.
- `DOTNET_GCConserveMemory=9`: 4,169 → 3,000 MB on the heaviest partition, 1,601 passed both times, **one measurement each**. This is not proof of a cause; it is a lead for V11.
- **A single-process full run is still not shown to fit** on this machine; the partitioned run is the verified strategy.

## Browser verification (isolated)
PENDING: filled in from `v1env33` below.

## Mapping to the test guide (MT-01 … MT-44)
Covered by automated tests and/or the isolated browser run. «Browser» rows are filled in from the results.

| MT | Topic | Evidence |
|---|---|---|
| 01–03 | Project / module / specification choice and restore | Earlier V8 tests + journey D (module switch clears spec; explicit choice restored) |
| 04–07 | Dashboard and Finn og forstå drill-down, type filter | FUN-014: tile → exact population (bUnit); dashboard browser run (below) |
| 08 | Changing active AC | Journey F (column follows the row; detail follows) |
| 09 | Search beyond the batch with filters | Synthetic 12,000: search + filter-only story; **PARTIAL** (status / scenario type / test method are not server filters) |
| 10 | Vis flere / pagination | Journey E; Pager tests |
| 11 | Standalone E2E without US | Not specifically re-run → **NOT-TESTED** this pass |
| 12–13 | Prepare from an AC; without a plan | Journey A; preparation without a specification (P checks) |
| 14 | Plan from template (4 suites) | Not re-run → **NOT-TESTED** this pass |
| 15, 17 | Scenario without a case; counts everywhere | FUN-015 tests + browser FUN015 |
| 16 | Dialog interruption | BUG-001 tests + browser BUG001 (backdrop, Escape, focus, 800×500) |
| 18 | Revision diff for scenario-only | FUN-016 tests + browser FUN016 |
| 19 | Case for an AC does not move the suite | FUN-011 tests + browser FUN011 |
| 20 | Cancel new case | Earlier tests |
| 21 | Technical visibility | BUG-003 tests + browser BUG003 |
| 22 | Test type saved | Earlier tests |
| 23 | Vague step and approval | BUG-004 tests + browser BUG004 |
| 24–28 | AC readiness counters | Earlier V5 tests; journey G / P (saved and read back) |
| 29 | Approved ≠ ready | BUG-004 (confirmation text, policy) |
| 30–34 | Blocked execution, history, move with result, standalone E2E, filter | Journeys A/B/C; FUN-011 test (blocked kept) |
| 35 | Counts after F5 | Browser FUN015 (Planlegg F5, Testplaner, execution) |
| 36 | Dashboard after result | Journey A (result on Testoversikt) |
| 37 | Source changes wording | FUN-017 tests + browser FUN017 |
| 38 | File export preview: 1 case, 1 scenario omitted | FUN-018 test + browser FUN018 |
| 39–41 | Export validation, file content, direct ADO | **NOT-TESTED** this pass (no file downloaded; no ADO) |
| 42 | Accessibility of dialogs, 200 % zoom | BUG-001 (focus); browser BUG001 at 800×500. Full audit **NOT-TESTED** |
| 43 | Plan copy | Journey C; FUN-015 test (copy counts, results reset) |
| 44 | Archive / restore | **NOT-TESTED** this pass |

## Release risks
1. **BUG-005** needs a CI package that passes the start → use → stop → start → `--validate` test on an authorized tester PC. Not done; see its report.
2. **BUG-002** cause unproven; the UX separation (decision 2) is implemented and covered by tests.
3. Server search does not apply status, scenario type or test method (said on the page).
4. Discover UX redesign items are not done (deliberately deferred until checked against the test guide). FUN-014 is resolved (definition + exact drill-down).
5. Backend: a single-process full run is not shown to fit in memory; the cause of the accumulation is not proven.
6. Export file content (MT-39/40) and archive/restore (MT-44) were not re-verified in this pass.
7. Requirement identities shared across documents (66 in M2LB): a migration proposal awaits a decision; nothing persistent was changed.
