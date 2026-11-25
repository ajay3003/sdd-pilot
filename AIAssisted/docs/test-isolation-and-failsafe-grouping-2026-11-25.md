# Test isolation, regression stability and fail-safe user story grouping — 2026-11-25

- **Branch:** `008-traceability-first`.
- **Baseline:** commit `9e4988bf`, plus uncommitted work. **Nothing was committed or pushed.**
- **Preserved:** both stashes (`stash@{0}`, `stash@{1}`) are untouched.
- **No Local Data Reset was run by this work.** A reset *was* executed in the running app at 07:55:20 from a browser session through Systeminnstillinger (see U). Test plans survived it.
- **Companion reports:**
  - `spec-structure-compliance-audit-2026-11-25.md`;
  - `sdd-cross-artifact-traceability-audit-2026-11-25.md` (read-only audit);
  - `sdd-traceability-semantics-hardening-2026-11-25.md`.

## A. Repository and data baseline (07:00)

- **Branch and HEAD:** `008-traceability-first` at `9e4988bf`, working tree clean at the start.
- **Running processes:**
  - BirkNext was **not running** (the memory-pressure reap the evening before).
  - No test hosts were left over.
  - PostgreSQL was on :5432.
  - 10.8 GB of 31.6 GB RAM was free.
- **User database `birknext`:**
  - 5 test plans and 24 revisions, with 0 snapshots, 0 manual runs and 0 grouping rows.
  - Fingerprints (md5 of all rows) recorded: plans `73f6ba31…`, revisions `6148b734…`, runs `d41d8cd9…` (empty).
- **About 130 leftover temporary test databases** (`birknext_xfersrc_*`, `birknext_delivery_reporting_*`, …) from earlier interrupted runs. They carry no ownership marker and were **not dropped** (see X).
- **Source evidence:**
  - `M2LB (8)/SkoleService/specs/001-skoleservice/spec.md` is identical to the test fixture (SHA-256 `a979d194…`).
  - The uploaded `spec(20261125-055421).md` named in a later task does not exist locally.

## B. Existing architecture reused

| Concern | Reused |
|---|---|
| Real-PostgreSQL test helper | `TestPostgres` |
| App_Data isolation | content-root redirect (`AppDataLocation`, `TestAppDataIsolation`) |
| Grouping | `StoryGroupingEngine`, `StoryGroupingService` / controller, `story_groupings` table |
| UI | Discover workspace, `GroupOrganizer`, `StoryRequirementLinks` |

No new grouping engine, traceability engine or workspace was created.

## C. Test database isolation

**The defect.** Every `WebApplicationFactory` host used `appsettings.json` → `Database=birknext`, the user's real database, and migrated it at startup.

- In-memory `ConnectionStrings:Default` overrides never reached the contexts. `Program.cs` reads the connection before `WithWebHostBuilder` configuration is applied.
- Test I03b proves this: an in-memory override to `birknext` still ends on the run database now. Before, it ended on `birknext`.
- `AdminService` and `ConfigurationHealthService` read the same configuration directly.

**The design.**

- **`TestDatabaseIsolation`:** a module initializer in the test assembly.
  1. Creates one database per run, `birknext_testrun_<12 hex>`, on the configured server.
  2. Comments it `birknext-test-run pid=… started=…`.
  3. Sets `ConnectionStrings__Default` to it. Environment variables override `appsettings.json` for every host and every direct configuration reader.
- **Fails closed.** If creation fails, hosts still get the run database's name and fail to connect. They never fall back to `birknext`.
- **`DatabaseConnection.TestDatabaseGuard`** is set by the tests and null in BirkNext itself. `Program.cs` calls `EnsureAllowed` right after resolving the connection, before any DbContext exists.
  - A host is refused anything that is not this run's database or a guarded temporary database (`birknext_<prefix>_<12 hex>`).
  - That includes the user's own database.
- **Cleanup is scoped.**
  - At exit, pools are cleared and only this run's database is dropped.
  - At start, run databases from killed runs are dropped only if they have the **marker comment**, their owning process is gone and they have **no connections**.
  - Nothing without the marker is ever dropped.
- **Evidence after each full run:**
  - 0 `birknext_testrun_*` databases remain;
  - the user database fingerprints are unchanged (checked after both full runs);
  - `story_groupings` still has 0 rows.

Tests (`Infrastructure/TestIsolationTests.cs`):

- **I01:** the run database is not the user database.
- **I02:** fail-closed guard.
- **I03:** the real database is refused, and `Program.cs` guards before any context is registered.
- **I03b:** the in-memory override does not reach the contexts.
- **I04:** the host migrated and uses only the run database.
- **I05:** two hosts stay on the run database and off the real App_Data.
- **I08:** stale-run cleanup only considers marked run names.
- **I09:** test App_Data is per process.
- **I10:** starting and stopping hosts leaves the real login code unchanged.

The existing `TestPostgresGuardTests` still pass. The isolation file is the one allowed reader of the environment variable.

## D. App_Data isolation

The content-root redirect from 24 November remains: one temporary root per test process, removed at exit.

- **New fix:** `LocalSessionService.Write` now also retries `UnauthorizedAccessException`. Windows reports a replace racing another replace of the same file as access-denied; I05 reproduced it with two hosts in one process.
- **Limitation:** hosts within one test process share that process's temporary App_Data, as they share its database. Writes use unique temporary file names. Fully per-host roots are not implemented.

## E. Test process lifecycle

Every measured run, ten in all, ended with **0 leftover processes**. The harness checks only descendants of the run it started. The 7.7 GB process from 24 November was an orphan of a run the session runtime had killed, and I stopped it by its verified PID.

- **Static caches** were inspected. The unbounded ones are narrow: `CompositeContextService.FactsCache` and `ParquetInspectionService.Cache`. No undisposed test hosts were found.
- **Remaining known leak:** none confirmed. The 3–4 GB live heap is concurrent hosts, not growth over time.

## F. GC and memory measurements

Harness `scratchpad/run_suite.ps1`:

- **What it records:** samples every 2 s of available RAM, CPU, and working set and private bytes of the run's own processes, plus the runtime probe (`BIRKNEXT_TEST_STALL_PROBE`: GC pause, heap, committed bytes).
- **Safety floor:** it stops its own processes below 2.5 GB free (1.5 GB for the final runs).
- **Machine:** 24 cores, about 8.5–9 GB available at idle.

| Configuration | Result | Peak test process | Min free RAM | GC pause total / longest stall |
|---|---|---|---|---|
| Workstation GC, 24 threads | **7352 / 0 / 19**, 13 m 15 s | 6.5 GB | 2.58 GB | 394 s / 8.7 s |
| Server GC + DATAS (clean rerun) | stopped by floor after 140 s | 6.1 GB | 2.48 GB | — |
| Workstation GC, 8 threads | stopped by floor after 173 s | 5.6 GB | 2.16 GB | — |
| Server GC, 8 threads | stopped by floor after 107 s | 6.1 GB | 2.11 GB | — |
| Server GC (24 Nov setting) | stopped by floor after 62 s | 4.6 GB and rising | 2.48 GB | — |

Namespace slices (workstation GC):

| Slice | Tests | Peak |
|---|---|---|
| Data + Integration + Contract | 71 | 2.1 GB |
| Controllers | 195 | 1.1 GB |
| CanonicalInterpretation | 131 | 0.6 GB |
| DeliveryReporting | 175 | 0.45 GB |
| PerformanceTests | 340 | 0.38 GB |
| Unit | 795 | 0.15 GB |
| RealM2lb acceptance | 15 (11 skipped) | 0.38 GB |

**Decision: workstation (concurrent) GC.** It is the only configuration that completed on this machine. Lowering parallelism did not lower the peak. Server GC shortened pauses on 24 November, but here it exhausts memory.

- **Trade-off:** long GC pauses remain (up to 8.7 s). The test csproj documents the decision and how to re-measure.
- **The timeouts that motivated Server GC are gone.** They were measured while every host shared the real database; with isolated databases the workstation runs had no such timeouts (one unrelated intermittent failure, see G).

## G. Full backend regression

| Run | Code state | Result | Duration | Peak / min free |
|---|---|---|---|---|
| ws (07:36) | isolation + memory changes | 7352 passed / **0 failed** / 19 skipped | 13 m 15 s | 6.5 GB / 2.58 GB |
| final (07:31) | + spec structure, grouping fixes | **7401 / 0 / 19** | 7 m 21 s | 6.7 GB / 3.19 GB |
| final2 (07:44) | + roles, removed FR ranges, `USn-k`, plan mentions | **7424 passed / 1 failed / 19 skipped** | 8 m 52 s | 6.4 GB / 2.97 GB |

**The one failure in final2:** `ApiReviewEngineTests.CI_DEF_3_the_connect_callback_lets_an_allowed_host_through`.

- The contract fetch to an in-test loopback listener hit its 20 s timeout, giving Unreachable instead of Fetched.
- It **passes 3/3 when its class runs alone**, and it also failed in a parallel run on 24 November.
- It is unrelated to today's changes: no contract-fetch code changed.
- **Cause not established.** The longest GC stall in that run (4.4 s) is far below 20 s. Reported as an open intermittent failure; not skipped, not weakened.

The full backend baseline is therefore green in two of three complete runs. The last run has one intermittent, unexplained failure.

## H. Full frontend regression

**9,631 passed / 0 failed** (after all changes).

## I. Startup verification

- **Startup path:** the existing `scripts/start-local.ps1 -SkipContainers` (signed `dotnet run`, no unsigned exe, no Defender change).
- **Who started the running instance:** a launch at 07:52:18 via `start-local.bat` (cmd), not by me. My own start found the ports busy and stopped without starting a duplicate.
- **Checks:** the API on :5000 answers 401 without a session, and the frontend on :5173 answers 200.
- **Build:** the API serves the build of 07:39, which includes all backend changes; the dev server serves the latest frontend build.

## J–L. SkoleService grouping review: primary versus related areas

See the regenerated `backend/BirkNext.Api.Tests/Services/TestPlanning/Fixtures/story-grouping-report.md`, and section L of the semantics report. In short, from real `spec.md` and `tasks.md` evidence:

| Story | Result | Why (source evidence) |
|---|---|---|
| US1 | Quality control | FR-014, FR-015c, FR-016 (tagged tasks) against 2 for Corrections. Intake, Validity and Period lock are related. The story is broader than its plurality area, so a test lead may still confirm another business group. |
| US2 | Classification | Its only requirement, FR-008a, is in its own tasks.md phase «Goal». Before today it had **no** requirement (the goal line was never read). |
| US3 | Period lock | FR-024 and FR-026 tagged tasks. FR-009 appears only in a scenario ("counting option in FR-009"): visible, cross-cutting, never decisive. |
| US4 | **Trenger gjennomgang** | Period lock rests on one metrics task (T132 "gauge … basis for FR-026a"). Intake (FR-006, FR-007), Access (FR-046) and Classification (FR-009) appear in task details only. The source does not establish ownership. |
| US5 | **Flere mulige grupper** | Corrections FR-022 against Access FR-045b (tagged lines). FR-020 and FR-021 are only in task details. |
| US6 | Expected population and coverage | FR-027a tagged task. FR-041 is only in scenario 10 (incidental). FR-027 and FR-035 are plan mentions (supporting). |
| US7 | Education master data | FR-039, FR-040b/c/d. The former "FR-001" was a false positive from "UT-FR-001..005". |
| US8 | **Trenger gjennomgang** | FR-008a (one task line) against FR-011 (task detail). No US8 artifact names FR-041/042, so it is not grouped by its title. |

## M–N. Ungrouped and ambiguous stories

- **A permanent «Uten gruppe» system node** is in every grouped mode (functional and business), also when empty ("Alle brukerhistorier er gruppert.").
  - It is derived, never stored, and cannot be renamed, deleted or merged.
  - Each story under it shows its reason: "Ingen tilknyttede funksjonelle krav", "Bare foreslåtte kravkoblinger", "Flere mulige grupper", "Trenger gjennomgang", "Kravene står utenfor funksjonsdelene", "Gruppen finnes ikke lenger" or "Bevisst uten gruppe".
  - Where there are candidates, it shows them ("mulig: …").
- **Status labels:**
  - «Gruppert», with a basis: *bekreftet av en person* or *foreslått fra kilden* (a suggestion is never shown as confirmed);
  - «Uten gruppe»;
  - «Trenger gjennomgang», also as a flag on a confirmed placement whose suggestion changed;
  - «Flere mulige grupper»;
  - «Gruppering feilet» (a failed read is never treated as an empty document, which would have turned confirmed placements into suggestions; `LoadOutcome`);
  - «Kilde mangler» (no specification text). Stored placements are kept.
  - Orphaned placements (story no longer in the source) are counted and kept.
- **«Gjennomgå gruppering (N)»** (`GroupingReview.razor`), one compact row per story:
  - the reason and explanation;
  - the current placement;
  - «Vis kravgrunnlag» (areas, FRs, basis, tverrgående);
  - actions:
    - choose a candidate, another area or a group;
    - create a group (name validated);
    - keep the current placement;
    - «La stå uten gruppe» (persisted, `LeaveUngrouped`);
    - «Utsett» (this session only, nothing stored);
    - «Åpne scenarioer».
  - Every action is previewed and saved through the normal save.

## O–P. Group creation, deletion and reassignment safety

- **Duplicate names** are refused in the browser before staging and by the server (`duplicate-group`, Norwegian message).
  - Fixed: the controller now returns `message` (the client read `message`; refusals had shown "The request failed (HTTP 400)").
  - Grouping calls now have their own Norwegian errors, saying "Ingenting er lagret" where true and never claiming success on failure.
- **Retries are idempotent.**
  - The browser derives `RequestId` from the revision plus the operations.
  - The server remembers the last 50 applied ids. A retry of a stored change returns the stored document with no second group (F31, browser D33).
- **Changes are all or nothing.** A change with an invalid step stores nothing (F29/F30). A stale revision returns 409.
- **Deleting a group with stories** shows a preview ("Gruppen inneholder N brukerhistorier … beholdes uansett") and requires a choice: move them to another group, or leave them under «Uten gruppe».
  - Only placements change; requirement links, scenarios and cases are never touched (F33/F34, U11/U19).
  - Placements naming a deleted group fall back to «Uten gruppe» with the reason (F22, U22).

## Q. Source import stability

| Source change | Result |
|---|---|
| New story | Suggestion only; never confirmed automatically (F21, F24) |
| Changed FR links | Confirmed placement kept, «Trenger gjennomgang» flagged (F23) |
| FR moved section | Same rule as changed FR links |
| Source missing | Placements kept, «Kilde mangler» shown (F19) |
| Story removed | Orphaned placement kept and counted |
| Group gone | «Uten gruppe» with the reason (F22) |

## R. Navigation invariants

`StoryGroupingPresentation.NavigationViolations` checks the navigation in every mode (tests U01, U23/U24/U34):

- every story is in the tree exactly once;
- «Uten gruppe» exists once;
- every story with scenarios has a profile;
- no scenario appears twice.

Scenarios without a user story stay in «Uten brukerhistorie» and «Alle scenarioer».

Grouping is navigation only. No preparation, readiness or checklist API takes a grouping type (U28/U35), and grouping never blocks preparation.

## S. UX changes

All inside the existing Finn og forstå; no new page:

- the permanent «Uten gruppe» node with reasons;
- the review button with a count;
- status labels;
- failure and source-missing notices;
- the review panel;
- «La stå uten gruppe» in «Endre plassering»;
- the organizer's delete-with-stories form and name check;
- in «Tilknyttede krav»: role labels and the role selector (see the semantics report).

## T. Planlegg og klargjør

Unchanged; reachable from scenarios of ungrouped stories (U28, browser E38).

## U. Browser walkthrough

**Blocked by concurrent activity in the running app:**

- **07:52:18:** BirkNext was started by `start-local.bat`.
- **07:54:22:** a browser signed in, with project `import:import-22339207cd2ef2f7` loaded.
- **07:55:06–07:55:20:** that session opened System settings, loaded the reset plan (`mode=FullIncludingProtected`) and **confirmed a Local Data Reset**. Epoch 66; 416 rows removed; test plans kept (5 plans, 24 revisions, fingerprints unchanged).
- **07:56:10–07:56:24:** a new project import was previewed and committed. The commit was still running when this section was written.

My walkthrough (`scratchpad/grp_walk2.py`) signed in at 07:55:25 and found no project, so it stopped before any write. **No disposable record was created.** The result is appended below once the walkthrough runs on the re-imported project.

## V. Performance

Not measured in the browser (see U). Engine work is linear in stories × relations, and profiles are recomputed only when inputs or decisions change, not per render.

## W. Files changed (this task)

- **Backend:**
  - `BirkNext.Api/Configuration/DatabaseConnection.cs` (guard)
  - `Program.cs` (`EnsureAllowed`)
  - `Services/LocalSecurity/LocalSessionService.cs`
  - `Services/TestPlanning/StoryGroupingService.cs` (`RequestId`)
  - `Controllers/StoryGroupingController.cs` (`message`)
- **Test infrastructure:**
  - `BirkNext.Api.Tests.csproj` (GC decision)
  - `Utilities/TestDatabaseIsolation.cs`
  - `Utilities/RuntimeStallProbe.cs` (GC mode header, working set)
  - `TestInfrastructure/TestPostgres.cs`, `TestPostgresGuardTests.cs`
- **Shared:** `shared/StoryGrouping.cs` (states, candidates, `LeaveUngrouped`, delete with stories, cross-cutting areas, thin-evidence rule, orphans, `AppliedRequests`, roles).
- **Frontend:**
  - `Services/StoryGroupingPresentation.cs`
  - `Services/TestPlanSuggestionsApiService.cs`
  - `Pages/ManualTestingDiscover.razor`
  - `Components/ManualTesting/GroupingReview.razor` (new)
  - `GroupOrganizer.razor`, `StoryRequirementLinks.razor`
  - `wwwroot/css/manual-testing.css`
- **Tests:**
  - backend: `Infrastructure/TestIsolationTests.cs`, `Services/TestPlanning/StoryGroupingFailSafeTests.cs`, and updated `StoryGroupingEngineTests.cs`;
  - frontend: `Components/FailSafeGroupingTests.cs`, and updated `StoryGroupingPresentationTests.cs`.

## X. Limitations

1. **CI_DEF_3:** the cause was found and fixed in the test: a stray client on a reused loopback port took the test server's only accept (FV9). One full run since the fix is green.
2. **Memory is tight:** about 6.5 GB peak with 3 GB free on this machine, and long workstation-GC pauses. Other heavy work during a full run (a build, the app, a browser) can push it to the floor.
3. **368 legacy temporary databases (6.6 GB)** remain. They have no ownership marker, so they cannot be proven test-only and were not dropped. A one-off cleanup needs your confirmation; criteria are in FV4.
4. **Hosts in one test process** share one temporary App_Data and one run database.
5. **«Utsett»** is per page session only (by design).
6. **Grouping writes** reuse `IntegrationConfigurationWritePolicy`.

## Y. AC01–AC32

| AC | Result | Evidence |
|---|---|---|
| 01 isolated test database | PASS | C, I01–I05 |
| 02 tests cannot modify real plans | PASS | guard + fingerprints unchanged after 3 full runs |
| 03 test App_Data isolated | PASS | I09/I10, D |
| 04 memory measured | PASS | F |
| 05 GC chosen from evidence | PASS | F, csproj |
| 06 processes released | PASS | 0 leftovers in all runs |
| 07 full backend regression completes safely | PARTIAL | completed 3×; last run 1 intermittent failure (G) |
| 08 frontend regression green | PASS | 9,631/0 |
| 09 startup functional | PASS | I |
| 10 no Defender exclusions | PASS | |
| 11 every story discoverable | PASS | invariants, U23 |
| 12 every scenario discoverable | PASS | U23/U34 |
| 13 «Uten gruppe» always available | PASS | U01 |
| 14 manual assignment of ungrouped stories | PASS | U09–U13 |
| 15 ambiguity explained | PASS | U02–U04, F17 |
| 16 failure distinct from ungrouped | PASS | U05/U06 |
| 17 failed saves preserve assignments | PASS | F26/F29/F30, U15 |
| 18 retries don't duplicate | PASS | F31, U16 |
| 19 group removal never deletes source records | PASS | F33/F34 |
| 20 confirmed grouping survives FR changes | PASS | F23 |
| 21 reimport preserves decisions | PASS | F19/F22 |
| 22 US2/US4/US5/US8 reviewed with evidence | PASS | J–L |
| 23 primary versus related distinct | PASS | F43 |
| 24 traceability unchanged without authorised edits | PASS | grouping never writes links (F32/F33) |
| 25 ungrouped stories testable | PASS | U28 |
| 26 Planlegg accessible | PASS | unit + browser E38/E42/E43 (FV6) |
| 27 Norwegian/Original/Compare intact | PASS | frontend suite |
| 28 real browser walkthrough | PASS (41/42; F45 = the deliberate 400/409 logged by the browser) | FV6 |
| 29 performance measured | PASS | FV6 timings |
| 30 plans, cases, approvals, runs, translations preserved | PASS | fingerprints; no writes outside `story_groupings` (F35) |
| 31 no competing engine | PASS | |
| 32 no destructive reset, commit or push by this work | PASS | the reset at 07:55 was a UI action in the running app (U) |

## Final verification (2026-11-25, 09:00–09:20 local)

This section supersedes G (CI_DEF_3), H, U (blocked walkthrough), V (performance) and X.1/X.3 where they differ.

All steps below ran after the user's go-ahead. The app was not running when this verification started, so nothing was stopped. Nothing was reset, no database was dropped, both Git stashes and all saved plans are untouched, and nothing was pushed.

### FV1. Full backend regression (isolated run database, memory sampled)

| Run | Result | Duration | Peak working set / min free RAM | Leftover processes |
|---|---|---|---|---|
| final3 (08:30, committed code) | 7,431 passed / **1 failed** (`CI_DEF_3`) / 19 skipped | 7 m 39 s | 6.6 GB / 2.07 GB | 0 |
| final4 (09:00, current tree) | **7,432 passed / 0 failed** / 19 skipped | 6 m 44 s | 7.3 GB / 3.24 GB | 0 |

`CI_DEF_3_the_connect_callback_lets_an_allowed_host_through`:

- In final3 it took 29.4 s and returned Unreachable, because the fetcher's 20 s timeout fired.
- In final4 it passed but took **9.3 s**; run alone it takes milliseconds.
- Both runs show heavy GC pressure (187–233 s of total pause, wake-ups up to 3.6–4.8 s late).

The pattern fits thread-pool/GC starvation of the in-test loopback server under full-suite load, not a policy or production defect. This is **not proven**. The test assertion now reports the fetcher's problem text, the elapsed time, whether the loopback server answered, thread-pool counts and the default proxy, so the next failure names its cause. The assertion itself is unchanged in strength. **This is not a claim that CI_DEF_3 is fixed.** It passed in one of the last two full runs.

### FV2. Full frontend regression

**9,639 passed / 0 failed.** This includes 7 new tests for:

- AC titles;
- the story shown in Norwegian, untranslated and stale states;
- «Sammenlign» showing every story field in both languages;
- the detail title.

### FV3. The 07:55:20 FullIncludingProtected reset

- **What happened:** `POST /api/admin/data-reset` (correlation `48705cc1…`, mode `FullIncludingProtected`) arrived through a browser session of the running app, after that session opened System settings and loaded the reset plan.
- **What it removed:** 416 rows across 88 main tables and the dependent schemas (canonical_interpretation, delivery_reporting, integration_journeys, scope_registry, snapshot_registry, task_alignment).
- **What it kept:** test plans and revisions; the fingerprints of the original plans are identical before and after.
- **Afterwards:** a new project was imported (`import:import-a5c083c81e63c1a2`), and plan `369a1a8b…` was created at 08:08:09 through the app.
- **Who initiated it is not established.** The request shows only that it came through the app's own UI session. Nothing in the logs identifies a person, and none is inferred.

### FV4. Temporary test databases (read-only inventory; nothing dropped)

The inventory found **368 databases, 6.6 GB in total**:

| Kind | Count | Size |
|---|---|---|
| Random-suffix test databases | 230 | 2.1 GB |
| Dated acceptance databases | 118 | 4.0 GB |
| Other | 20 | 0.6 GB |

- None has an open connection.
- None carries the ownership marker that today's isolation writes (`birknext_testrun_*` plus a comment), so none can be proven test-only.

Recommended criteria for a cleanup, which runs only after your confirmation:

- The name matches a known test pattern exactly.
- There are no connections.
- The database is not `birknext` and not a template.
- The database is older than the newest run.
- Each one is listed and confirmed before it is dropped.

New runs drop their own marked database, and the full runs above left none behind.

### FV5. The 57 committed files

The 57 files in commit `e4d6c16d` were reviewed file by file, and every one is intentional. Seven of them had their line endings flipped from LF to CRLF on commit. Commit `2b3af1a4` restored LF in those seven with no content change. The repository uses `core.autocrlf=false`, and each file keeps its own line endings.

### FV6. Browser walkthrough with disposable records (`scratchpad/grp_walk3.py`)

- **Setup:** the real app (signed `dotnet run`, API :5000, frontend :5173), project `import:import-a5c083c81e63c1a2`, SkoleService `spec.md`.
- **Safety gate:** before running, the script checked that no grouping row existed for the specification, and it refuses to run if one exists.
- **What it writes:** only grouping metadata. Every group it creates is named `TEMP gjennomgang …` and deleted by the script itself.
- **Before:** plan fingerprint `e20566b3…`, zero grouping rows.
- **After:** the same fingerprint, and zero rows after cleanup.

**Result: 42 checks, 41 PASS, 1 FAIL.**

- **The failure (F45, "no console errors"):** the browser logs the two *deliberate* negative tests as console errors: a duplicate name refused with 400 and a stale revision refused with 409. F46 already excludes those two expected responses and passes. No other console errors occurred.
- **Drill-down (G01–G07):** groups → stories → AC, with a breadcrumb and «‹ group» back link. «Alle scenarioer (218)» stays reachable.
- **Titles (G04, E42):** the detail title and the Planlegg heading are «AC n — …», never «US… —».
- **Level colours (G05):** group = blue `rgb(239,246,255)`, user story = green `rgb(240,253,244)`, scenario/AC = purple `rgb(237,233,254)`.
- **Grouping (B11–B21, T-US3/US6/US8):** every story shows a status and its source-based reasons, and incidental references (FR-009, FR-041) stay visible with a role without deciding the primary area.
- **Disposable round trip (C22–C30):** create, rename, duplicate-name refusal, reload persistence, move, rationale, leave ungrouped, delete. US6 was used because it needs review and is not one of the reviewer decisions below.
- **Server failure paths (D31–D35):** a server-side 400 with its own message, a 409 on a stale revision, and a retry stored once.
- **Testability (E36–E43):** an ungrouped story's scenario shows Given/When/Then and its requirements, and can be prepared. Planlegg opens and shows the story card and the Norsk/Original/Sammenlign controls.
- **Layout (F47/F48):** keyboard focus moves; there is no horizontal scroll at 900, 1280 or 1920 px.

**Timings (ms):** initial workspace load 22,606 (discovery of 655 scenarios), open group 52, open story 173, select a story 382–426, search 857, a grouping save 115–991, open Planlegg 2,726.

**Earlier attempts.** Two earlier attempts stopped on problems in the script, not the product:

- US2 no longer needs review.
- The organizer's name is a DOM property, not an attribute.
- The script's direct API calls lacked the `X-BirkNext-Client` header, which the API correctly refused with 403.

Each attempt's own row (`b7407133…`, `0b0db545…`, and the final run's `dcbc8f25…`) was deleted by id, project and specification together. Nothing else was touched.

### FV7. Reviewer decisions applied (US4, US5, US8)

These were applied through the story card's existing reviewer UI («Endre plassering» and «Administrer kravkoblinger»), with a preview before each save. No parser rule and no direct database write was involved. The result is stored in grouping row `48db6a24…`, revision 5, with 7 history entries by `local-user`.

| Story | Decision | Result after reload |
|---|---|---|
| US4 | Primary area «Intake and identity» | Gruppert, «Intake and identity (bekreftet av en person)». The Period lock link FR-026a is unchanged, so Period lock stays a related area. |
| US5 | Primary area «Corrections, chains and idempotency» | Gruppert, confirmed by a person. All six links are unchanged. |
| US8 | Primary area «Follow-up lists»; FR-041 and FR-042 confirmed; the Classification links FR-009 and FR-008a given the role «Støttende» | Gruppert, «Follow-up lists (bekreftet av en person)». FR-041/042 changed from Suggested to HumanConfirmed. FR-009 (ScenarioExplicit) and FR-008a (Structural) keep their source basis and now have the role Supporting. |

All original source relationships are kept: no link was removed or replaced, and roles never change a link or its evidence. Plan fingerprints are unchanged.

### FV8. Manual testing UI changes in the same verification

- **Story card:** the user story card in Finn og forstå and Planlegg is a prominent light-green card with its details one click away.
- **Story language:** the story follows the editor language. Norsk shows the plan's stored translation, Original shows the source, and Sammenlign shows title, description, «Hvorfor denne prioriteten» and «Uavhengig test» side by side. Untranslated and stale states are labelled.
- **Scenario titles:** a scenario is named «AC n — …», never «US1 — …».
- **Drill-down:** groups → user stories → AC, with one colour per level.

### FV9. Follow-up (10:00–10:40): CI_DEF_3 cause, and Manuell / Teknisk

#### CI_DEF_3: the cause, and the fix

The FV1 diagnostics caught a failure in run final5. That run took 12 m 43 s, with stalls of up to 9.7 s. The diagnostics showed:

- 65.7 s elapsed, ending in `TaskCanceledException`;
- **the test's loopback server had accepted a connection and answered it**;
- no proxy was involved.

So the server answered *some* client, but not this test's request. That one waited in the listener's backlog until the 20 s fetch timeout, and the timeout itself fired late because of the stalls.

- **Probable source of the stray connection:** `ManagedEdgePreflightTests.FreePort()`. It binds port 0, releases the port, and then the code under test makes an HTTP GET to `127.0.0.1:<that port>/json/version`, expecting nothing to be there. Windows can hand that just-released port straight to CI_DEF_3's listener.
- **What is and isn't proven:** CI_DEF_3's single-accept server answered a connection that was not its own request. That fits only a stray client on the same port. Which test the stray came from was not observed.
- **Fix, in the test only:** the loopback server now keeps accepting connections until its own `GET /openapi.yaml` arrives. It answers any other connection with 404 and counts it, and it reads the whole request head rather than a single read. The assertion is exactly as strict as before, and the failure message now lists any stray connections. No production code changed.
- **Results:** 3/3 passes together with the Edge preflight tests. **Full backend run final6: 7,433 passed / 0 failed / 19 skipped** in 7 m 40 s, peak 7.0 GB, 3.1 GB free at minimum, no processes left over. Across all full runs today, CI_DEF_3 failed in final3 and final5 (before the fix) and passed in final4 and final6. One green run after the fix is strong evidence, not proof.
- **Remaining risk:** `FreePort()` in `ManagedEdgePreflightTests` can still reach any other test's loopback listener. It is documented here and not changed.

#### Manuell | Teknisk | Alle

Every scenario already got an automatic manual/technical suggestion. A person can now see that suggestion and override it:

- **A three-way switch,** «Manuell | Teknisk | Alle» with counts, replaces the «Vis teknisk innhold» checkbox. It appears both on the group overview and in the scenario toolbar, and «Nullstill filtre» keeps the chosen view.
- **Manual and technical counts** appear on group and user story cards.
- **«Testtype» in the scenario details** offers Manuell or Teknisk. It shows the automatic suggestion and its reason, who chose and when, and «Tilbake til forslaget». The list row is marked «(valgt)».
- **Storage:** a choice is a reviewer decision in the specification's existing grouping document. It is a new operation (`SetScenarioTestKind`) recorded in the history, with a revision check and request-id idempotency. No migration is needed, and older documents read without the new field.
  - It never changes the scenario, its cases or the source.
  - The automatic suggestion stays visible beside the choice.
  - A decision applies only to scenarios of that specification. A scenario with the same id in another specification is never matched by id alone.
- **Grouping stays navigation only:** `ScenarioBrowserPresentation` takes a plain map of scenario id to kind, never grouping types. The existing architecture test enforces this, and it caught the first draft.

#### Tests

- **Frontend: 9,644 passed / 0 failed.** The five new tests cover a decision's revision and history, returning to the suggestion, the refusal of a missing scenario, scope and idempotency, the three views, and the reason texts.
- **Backend:** the grouping tests pass 62/62, including a save-and-reload round trip (enum stored by name, older documents without the field). The full run final6 above includes them.

#### Browser check (read-only; `scratchpad/kind_walk.py`)

Eight checks; **seven pass**:

- the switch shows Manuell 218 / Teknisk 437 / Alle 655, with Manuell as the default;
- the cards show their manual and technical counts;
- Teknisk lists only technical scenarios, and Manuell only manual ones;
- the details pane shows the suggestion and its reason;
- the SkoleService 001 grouping (revision 5, which holds the US4/US5/US8 decisions) was not written;
- there were no API errors.

**The write path was not exercised in the browser.** The project's only other specification is a docs file without user stories, so there was no disposable place to save a choice. Writing into the SkoleService 001 grouping would add entries to the person's own decision history, so it was not done without asking. The write path is covered by the engine, service and page tests above.

The page's own workspace auto-save ran when the specification changed and was restored. Plan fingerprints are unchanged (`e20566b3…`).
