# SDD traceability semantics hardening: FR relationship roles and primary grouping — 2026-11-25

- **Branch:** `008-traceability-first`, uncommitted on top of `9e4988bf`. No commit or push.
- **Inputs:**
  - `sdd-cross-artifact-traceability-audit-2026-11-25.md` (read-only audit);
  - `spec-structure-compliance-audit-2026-11-25.md`;
  - `test-isolation-and-failsafe-grouping-2026-11-25.md`.

**The rule implemented:** a source reference proves that a relationship exists. It does **not** prove primary business ownership, functional responsibility, implementation, test coverage, test execution or test success.

## A. Existing architecture reused

| Concern | Existing component |
|---|---|
| Spec parsing | `SpecExplorerService` (canonical) |
| tasks.md | `TaskExplorerService` (browser) and `StoryTaskContext` (shared reader) |
| Story → FR evidence | `CoverageUserStory.NamedRequirements` / `TaskRequirements`, `StoryTaskRequirement.Kind` |
| Relations, provenance, grouping, reviewer decisions | `StoryGroupingEngine` (`StoryLinkBasis`), the `story_groupings` document |
| UI | «Tilknyttede krav» / «Administrer kravkoblinger» (`StoryRequirementLinks`), «Administrer grupper», «Gjennomgå gruppering» |

No new parser, traceability engine, graph or page was added.

## B. Hard-coded inference removed

`frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs`:

- **`InferUserStoryId`** mapped FR numbers to stories for *every* specification: FR-001..007 → US1, 008..012 → US2, 013..016 and 033 → US3, 017..019 → US4, 020..024 and 032 → US5, 025..028 → US6, and 029..031 → "Cross-cutting / Platform".
  - **Removed.** A story now comes only from an explicit story id in the item's context.
- **`AddDeterministicScLinks`** linked fixed FR ranges to SC-001…SC-008.
  - **Removed.** Requirement → SC links now exist only where the success criterion names the requirement.
- **Earlier today:**
  - `SpecExplorerService.LinkUserStoriesToRequirements` stopped linking on two shared title words (US1 had 58 of 65 FRs); links are explicit only.
  - The backend `tasks.md` reader stopped treating any line that mentions a story as a task of that story.
- **Persisted data:** these were computed views, never stored, so no saved links needed recomputation. No reviewer decision was touched.
- **Tests:**
  - T02 (the source contains neither path any more);
  - `TraceabilityModelBuilderTests` rewritten: the story column is only explicit ids, and SC links are only what the source states. The old tests asserted the fabricated table.

## C. Strict identifier parsing

Whole identifiers, suffix-aware, namespace-safe: `(?<![\w-])FR-?\d+[a-z]?\b`.

- **FR-015, FR-015a and FR-015b** are distinct. FR-0151 is not FR-015.
- **Prefixed references are not this document's FRs:** `UT-FR-001`, `SR-FR-017a`, and `US-B1` for stories.
- **References inside code spans and Markdown links** are read.
- **Several ids in one paragraph** are all found.
- **Casing:** the canonical id (FR-027A) is used for identity; the source spelling (FR-027a) for display.

Applied in:

- `SpecificationEvidenceBuilder.NamedRequirements`
- `TestCoveragePresentation.NamedRequirements`
- `TaskExplorerService` FR references
- `SpecExplorerService.CanLinkByReference` and `FrIdRe`
- `TraceabilityModelBuilder.FrIdRe`
- `TraceabilitySuggestionService.IdPattern`
- `StoryTaskContext`

Tests: T03–T06, S16, R-whole-identifier.

**Not changed:** persisted canonical keys. Cross-service collisions (SkoleService FR-001 versus Person FR-001) are already prevented by the catalog scoping per specification path in the grouping service (V03 from 24 November). Validation uses only the specification's own catalog.

## D. Provenance model (how a link is known)

`StoryLinkBasis`: SourceExplicit, HumanConfirmed, ScenarioExplicit, Structural, Suggested. Each relation keeps every piece of evidence with its **kind**:

| Kind | Meaning |
|---|---|
| `story` | the story's own text names it |
| `scenario` | an acceptance scenario's own text names it |
| `task` | a tagged task line (`[USn]`) |
| `phase-goal` | the «Goal» of the story's own tasks.md phase |
| `task-detail` | an indented sub-bullet or «As built» note under a tagged task |
| `plan-mention` | a plan.md / quickstart.md line naming the story and the FR together (added today, from audit finding 3) |
| `manual` / `confirmed` / `plan` | a person added or confirmed it, here or on a test plan |
| `suggestion` | the existing rule comparison; never a link |

## E. Role semantics (what a link is for), independent of provenance

`StoryLinkRole`:

| Role | Meaning |
|---|---|
| **Unknown** | the relationship is real, but its role is not established |
| **Primary** | the FR defines the story's central behaviour; reviewer only |
| **Supporting** | a prerequisite, constraint or exception |
| **CrossCutting** | a control many stories pass through |
| **Contextual** | comparison or background; reviewer only |

- **Derived role, where the evidence shows it:**
  - CrossCutting from breadth (FR or area).
  - Supporting when the only evidence is task details or plan mentions.
  - Otherwise **Unknown**. Nothing is guessed from words.
- **Reviewer role:**
  - set through the new `SetLinkRole` operation, stored in `StoryGroupingDocument.Roles` (latest per story and requirement), with history ("Endret kravrolle");
  - "Uavklart rolle" returns the relationship to derived;
  - a role never changes the link, its decision or its source evidence (T18/T29).
- **Shown in the UI:** in «Tilknyttede krav» each link shows its role ("Uavklart rolle", "Støttende", "Tverrgående kontroll", "Kontekst", "Primær atferd"), marked "(vurdert)" when a reviewer set it. «Administrer kravkoblinger» has a role selector per link; previews state "rollen til FR-x blir …".
- **Independence:** provenance and role are separate dimensions (T07/T08/T13). For example, a SourceExplicit link can be reviewed as Supporting.

## F. Incidental references

**Generic rule:** a requirement named **only** inside an acceptance scenario's text (or only in supporting lines) has *no responsibility evidence*.

- It stays visible as a related area, with role Unknown.
- It **never decides** a primary area.
- It **never makes another suggestion "thin"**.
- A reviewer can mark it Primary, and then it decides (T-reviewer test).

Responsibility evidence is: the story's own text, a tagged task, the story's phase goal, or a person's link. There are no story-specific rules.

| Real case | Before (structural fixes only) | Now |
|---|---|---|
| US3 → FR-009 ("counting option in FR-009") | could tie with Period lock | visible (tverrgående); **Period lock** decided by FR-024/FR-026 tasks (T15) |
| US6 → FR-041 ("clean-up list (FR-041 (b))") | Expected population was "thin" because of it | visible; **Expected population and coverage** decided by FR-027a (T16) |
| US8 → FR-009 | could co-decide Classification | visible; does not decide. US8 stays **Trenger gjennomgang** (T17) |

## G. Primary grouping decision rules (in order)

1. A person's placement (confirmed group or area, or «bevisst uten gruppe») is never moved silently. A changed suggestion is flagged «Trenger gjennomgang».
2. A reviewer's **Primary** role decides; Supporting, CrossCutting and Contextual never decide.
3. Unreviewed relationships decide only with responsibility evidence, and not when they are cross-cutting or supporting-only.
4. The strictly most distinct deciding FRs in one known area gives a suggestion. A tie gives «Flere mulige grupper». A single task-line FR while other own (deciding or supporting) areas exist gives «Trenger gjennomgang». No deciding evidence gives «Uten gruppe» with candidates.
5. **Never decides:** raw link count from incidental references, shared security FRs (cross-cutting), dependencies, nearby contracts, plan co-mentions, similar words, or numeric ranges.

## H. Cross-artifact evidence

**Implemented:** `tasks.md` tagged lines, phase goals and task details; `plan.md` and `quickstart.md` co-mentions as supporting evidence. Each piece keeps its file, line and text, shown under «Se kilde».

**Not implemented (recommendations from the audit, unchanged):**

- **The upstream reference document as supporting evidence.** `docs/analysis/skolenaervaer-reference.md` ties US2 [US-B4] → SR-FR-007/008/009 → FR-008/009.
- **Contract-mediated two-hop evidence.** US8 T140 → `graphql-skole.graphql` → FR-041.

Both would be *paths* of supporting evidence, never direct links. Neither is created today, and no transitive inference exists.

## I. `USn-k` scenario references

The convention is confirmed in the source:

- `US5-2`, `US4-7` … in MeldingsutvekslerReceiver and SkoleAdapter artifacts;
- "US2 scenarios 2–3" and "User Story 3 scenario 3" in SkoleService `tasks.md`.

`SourceScenarioReferences.Parse` reads them verbatim and expands ranges. `TT-US1-2` style ids are ignored. `Resolve` resolves them **only within one specification**: "US3-14" from another feature does not resolve to SkoleService US3, which has 6 scenarios. Unresolved references keep their reason (T25/T26).

**Gap:** the parser and resolver exist and are tested, but are not yet shown in the UI or used as evidence.

## J. tasks.md relationships

| Evidence | Effect |
|---|---|
| Task tagged with the story | evidence that the story is responsible (Structural) |
| Phase goal | the same |
| Detail line | supporting only |
| Task in another story's phase that mentions this story ("Processing is US1's", "US2 scenarios 2–3") | not a task of this story (T21–T23) |

**A task link is traceability only.** Relations carry no implemented, covered, tested or passed state (T24). Neither task existence nor task completion is treated as implementation or test evidence anywhere in the grouping or traceability path.

## K. Historical contract names

**Verified, not changed:**

- Identity never depends on names. Story identity is file plus story number; scenario identity is positional within a file; FRs use their canonical id.
- Renaming `SkolenaervaersperiodeLukket` to `SkolenaervaerPeriodClosed` in a scenario changes neither its story, its number nor its links (T27/T28).

**Across imports:** scenarios are matched by file plus Given and When text. A renamed contract in Given or When would therefore show as a changed scenario in source-change analysis, never as a silent relink.

**No alias table is implemented:** the authoritative mapping (SKO-021, `wire-changes.md`) is not read yet. That remains a recommendation.

## L. SkoleService US1–US8 evidence matrix (no reviewed decisions applied)

| Story | Suggested primary | Deciding evidence | Related (role) | State |
|---|---|---|---|---|
| US1 | Quality control (accept and flag) | FR-014, FR-015c, FR-016 tasks | Corrections FR-018/022 (Unknown), Intake FR-004/007, Validity FR-011/013, Period lock FR-026a | Gruppert (foreslått) |
| US2 | Classification | FR-008a phase goal (tasks.md US2 phase) | — | Gruppert (foreslått) |
| US3 | Period lock | FR-024, FR-026 tasks | FR-009 scenario 4 (CrossCutting); Delivery, Corrections, Expected | Gruppert (foreslått) |
| US4 | — (candidate Period lock) | FR-026a, one metrics task | Intake FR-006/007, Access FR-046, Classification FR-009 (Supporting, task details) | Trenger gjennomgang |
| US5 | — (Corrections / Access) | FR-022 and FR-045b (one task each) | FR-020/021 (Supporting, task details) | Flere mulige grupper |
| US6 | Expected population and coverage | FR-027a task | FR-041 scenario 10 (Unknown, incidental); FR-027 and FR-035 plan mentions (Supporting); FR-009 (CrossCutting) | Gruppert (foreslått) |
| US7 | Education master data (Utdanning) | FR-039, FR-040b/c/d | — | Gruppert (foreslått) |
| US8 | — (candidate Classification) | FR-008a, one task | FR-009 scenario 3 (CrossCutting); FR-011 (Supporting, As-built) | Trenger gjennomgang |

**Human confirmation state:** none. The user database has 0 grouping documents.

## M. Existing decisions preserved

No grouping document exists in the user database, and the walkthrough creates and removes only its own row. Roles are additive, with an empty list by default. Old documents deserialize unchanged.

## N. Regression results

| Suite | Result |
|---|---|
| `TraceabilitySemanticsTests` (new) | 24/24 |
| Grouping, structure and sections targeted | 87/87 |
| Full frontend | **9,631 / 0** |
| Full backend after these changes | **7,424 passed / 1 failed / 19 skipped**; the failure is the intermittent `CI_DEF_3` loopback timeout, unrelated, which passes 3/3 alone (see the isolation report, G) |

## O. Browser verification

**Run in the final verification (FV6): 41/42 checks passed.** Earlier: blocked. A Local Data Reset and a new project import were performed in the running app by a browser session at 07:55–07:56. The import was still committing, so a fresh browser had no project. No disposable records were written. See the isolation report, section U.

## P. Changed files

- `shared/StoryGrouping.cs`: roles, `SetLinkRole`, `Roles`, responsibility evidence, deciding rule, thin rule.
- `shared/TestCoverageContracts.cs`: `PlanMention`.
- `shared/RequirementSections.cs`: `SourceScenarioReferences`.
- `frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs`: ranges and SC table removed, guarded regex.
- `TestCoveragePresentation.cs`, `SpecExplorerService.cs`, `StoryGroupingPresentation.cs`.
- `Components/ManualTesting/StoryRequirementLinks.razor`: role display and selector, evidence kinds.
- `backend/BirkNext.Api/Services/Specifications/SpecificationEvidenceBuilder.cs`, `TraceabilitySuggestionService.cs`.
- Tests:
  - `backend/BirkNext.Api.Tests/Services/TestPlanning/TraceabilitySemanticsTests.cs` (new);
  - `frontend/BirkNext.Web.Tests/Services/TraceabilityModelBuilderTests.cs` and `StoryGroupingPresentationTests.cs` (expectations of removed fabrication updated).

## Q. Remaining limitations

1. **Not yet surfaced:** `USn-k` references, upstream-document chains and contract two-hop paths (H, I).
2. **No historical contract-name alias table** (K).
3. **Primary and Contextual roles are reviewer-only.** The engine never claims them.
4. **US1's plurality area** (Quality control) may not match a tester's view of its business purpose. The engine reports the evidence; confirming a business group is a human decision.

## R. Unresolved review decisions (for a test lead)

- **US4:** Period lock (one metrics task) or Intake (task details), or keep it ungrouped?
- **US5:** Corrections or Access?
- **US8:** Classification, or confirm FR-041/FR-042 as Follow-up lists links (not stated by US8's own artifacts)?
- **Optional:** mark US3/US8 → FR-009 and US6 → FR-041 as **Contextual**, to record the judgement explicitly.

## AC01–AC20

| AC | Result |
|---|---|
| 01 FR-range inference removed | PASS (B, T02) |
| 02 no keyword-overlap links | PASS (T01, R27) |
| 03 namespace-safe ids | PASS (C) |
| 04 provenance and role distinct | PASS (E, T13) |
| 05 incidental references never decide | PASS (F, T14–T17) |
| 06 supporting and cross-cutting stay visible | PASS (T14) |
| 07 ambiguous roles require review | PASS (T11/T12) |
| 08 confirmed grouping stable | PASS (T18) |
| 09 cross-artifact evidence keeps its path | PARTIAL (tasks/plan yes; upstream and contract paths not implemented) |
| 10 two-hop evidence not shown as direct | PASS (not created) |
| 11 `USn-k` with namespace | PARTIAL (parser and resolver tested; not wired into the UI) |
| 12 task ≠ implementation or test success | PASS (T24) |
| 13 names don't change identity | PASS (T27/T28) |
| 14 SkoleService reviewed with evidence | PASS (L) |
| 15 ungrouped stories accessible | PASS |
| 16 plans and cases unchanged | PASS (fingerprints) |
| 17 targeted tests | PASS |
| 18 browser verification | BLOCKED (O) |
| 19 no duplicate architecture | PASS |
| 20 no destructive operations, commits or pushes by this work | PASS |

## Final verification (2026-11-25, 09:00–09:20 local)

This section supersedes N (full backend) and O (browser verification, previously blocked), and records how the unresolved review decisions in R were settled (FV7).

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

### FV10. Correction (2026-11-25, 10:55): CI_DEF_3

FV9 named a stray client on a reused loopback port as CI_DEF_3's root cause. The next full run that failed (final8, which stopped at the memory floor) contradicts that: the test's **own request was answered and there were no stray connections**, yet the client still timed out after 25.5 s.

- **What is known:** the cause is **not established**. The evidence fits the test process stalling under memory pressure; the answer arrives but is read after the 20 s timeout fires.
- **The port fix still stands:** the race is real and closed, and is verified (see `test-case-type-and-port-reliability-2026-11-25.md`). It is not proven to be CI_DEF_3's cause.
- **Status:** CI_DEF_3 is still an intermittent failure under full-suite memory pressure.
