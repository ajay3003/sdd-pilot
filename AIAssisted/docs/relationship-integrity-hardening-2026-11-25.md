# Relationship integrity hardening: coverage, change impact, readiness — 2026-11-25

- **Branch:** `008-traceability-first`, uncommitted on top of `9e4988bf`. No commit, push, reset or stash change by this work.
- **Primary input:** `relationship-integrity-audit-2026-11-25.md` (RI-01…RI-04).
- **Related:**
  - `sdd-cross-artifact-traceability-audit-2026-11-25.md`;
  - `spec-structure-compliance-audit-2026-11-25.md`;
  - `sdd-traceability-semantics-hardening-2026-11-25.md`;
  - `test-isolation-and-failsafe-grouping-2026-11-25.md`.

## A. Executive summary

**The central rule:** linked ≠ implemented ≠ prepared ≠ executed ≠ passed ≠ ready.

| Finding | Status before | Outcome |
|---|---|---|
| RI-01 legacy coverage | CONFIRMED CURRENT (API level) | **IMPLEMENTED.** Coverage is explicitly test *design linkage* (`evidenceBasis = TestDesignLinkage`, `executionEvidence = NotEvaluated`, GraphQL descriptions). Compatible: no field removed or renamed. |
| Readiness consumer of linkage (audit §L "P1 risk to verify") | **CONFIRMED CURRENT** | **IMPLEMENTED.** `AIQaAuditorService.DetermineReadiness` returned **Ready** when ≥ 80 % of requirements merely had linked tests (and score ≥ 80). Now it never returns Ready, because it reads no test results. |
| RI-02 unscoped FR fallback | LATENT / UNREACHABLE | Not changed (see E). |
| RI-03 change-impact evidence level | CONFIRMED (presentation + DTO) | **IMPLEMENTED.** Each test to review carries `EvidenceLevel` (LinkedToChangedSource / Potential) and `LinkReview = Unknown`. The panel and both exports say "… — review (link not reviewed; not executed)". |
| RI-04 legacy fallback provenance | LATENT / UNREACHABLE | Not changed (see G). |
| (found while verifying) test App_Data anchor | CONFIRMED | **IMPLEMENTED.** Tests built to another output folder no longer write the real App_Data (see K). |

## B. Initial repository status

- **Git:** branch `008-traceability-first`, HEAD `9e4988bf`, both stashes intact.
- **Uncommitted work:** the parser, grouping, semantics and isolation changes of today (listed in the companion reports).
- **Running app** (not started by me):
  - API PID 17888 and dev server PID 2904, launched at 07:52:18 via `start-local.bat`.
  - The API locks `backend/BirkNext.Api/bin`.
- **Activity in that app while this task ran:**
  - 07:55:20 — a **Local Data Reset** (epoch 66, "FullIncludingProtected"; plans kept);
  - 07:56 — a new project import;
  - 08:08:09 — a new plan "Testplan 2026-11-25" (`POST …/project-drafts`, then translate-all).
- **User data check:** the original 5 plans and 24 revisions are **byte-identical** to this morning's fingerprints (`73f6ba31…`, `6148b734…`), computed excluding the new plan.

## C. Concurrent work reconciliation

- **Already in the working tree** (from my own earlier tasks today, verified by tests):
  - explicit-only story → FR links (no word overlap);
  - namespace-guarded id regexes (including `TraceabilityModelBuilder.FrIdRe`);
  - removal of the hard-coded FR-range → story map and the fixed FR → SC table in `TraceabilityModelBuilder`;
  - relationship roles;
  - incidental-reference rule;
  - test database and App_Data isolation.
- **Not repeated, not overwritten.** This task touched only the files in U.

## D. RI-01: legacy coverage semantics

- **Where:**
  - `TraceLinkService.GetCoverageSummaryAsync` / `GetTraceabilityMatrixAsync` count a requirement as Covered when one `Covers` link from a Test scenario exists.
  - Published via GraphQL `coverageSummary` / `traceabilityMatrix` (`Query.cs`).
- **Reachability:** **no BirkNext page calls these operations.** The generated client operations `GetCoverageSummary`, `GetTraceabilityMatrix` and `GetQaAuditReport` are unused. The only UI consumer, `TraceabilityView`, is unreachable (see G). Impact is limited to external API consumers.
- **Correction (compatible):**
  - `CoverageSummary.EvidenceBasis = "TestDesignLinkage"` and `ExecutionEvidence = "NotEvaluated"`, as new GraphQL fields;
  - type and field descriptions state "design linkage only; not prepared, approved, executed or passed";
  - the `CoverageStatus.Covered` documentation says the same.
  - No lifecycle states were fabricated: legacy links carry none.
- **Schema snapshot:** `ScenariosSchemaTests.Schema_MatchesSnapshot` was updated. The content diff was reviewed: only the added descriptions and the two added fields.

## E. RI-02: unscoped FR fallback (`TraceabilityModelBuilder`)

**Status: LATENT / UNREACHABLE.**

- **No caller.** `TraceabilityView` is rendered only by `ExtractionReviewList`. That component has no route, is never used as an element, and there is no dynamic rendering of it (`grep` over the `.razor` and `.cs` files).
- **Single-spec input.** `Build(specMarkdown, candidates, links)` takes one specification. `ExtractionCandidate` carries no source-document field, so specs mix only if a caller passes mixed input, and none exists.
- **The FR-id fallback is intentional.** It is the sub-bullet de-duplication of fragments of one FR.
- **Already fixed in this builder today:** no FR-range story inference, namespace-guarded `FrIdRe` (UT-FR / SR-FR no longer match).
- **Not changed:** the scoping. Reviving dormant code to fix it adds risk without user benefit.
- **Recommendation:** retire `ExtractionReviewList` and `TraceabilityView` together, or scope the fallback by specification path if they are ever routed again.

## F. RI-03: change-impact evidence

**Where.** `SourceChangeImpactService.AnalyzeAsync` builds "recommended tests" in two ways:

- a test's own code file linked to a changed source;
- changed source → code-linked requirement → `Covers` → test.

These are legacy CodeLink and TraceLink rows, with no review or freshness state.

**Before:** `ImpactAnalysisRunService` already classified them `RelatedOnly` with "Review this linked test; not executed". But the shared DTO had no level, and `SourceChangeImpactPanel` and both exports listed them as plain "Recommended tests".

**After:**

- **The DTO.** `ImpactTestRecommendation` gains `EvidenceLevel` (`LinkedToChangedSource` for the test's own file link; `Potential` for the requirement chain only) and `LinkReview = Unknown`. Both are additive, with defaults.
- **The panel.** The section is now "Tests to review", with a basis line ("no review or freshness state, no test executed … not a confirmed functional impact") and a badge per test.
- **Both exports.** Markdown and HTML say "Linked to changed source — review" or "Potential impact — review" plus "(link not reviewed; not executed)".

Unchanged:

- direct technical impacts keep their level (the "Direct" component impact in the existing test stays Direct);
- the canonical graph impact engine;
- `ImpactAnalysisRunService` classification;
- nothing useful was removed.

## G. RI-04: legacy fallback provenance

**Status: LATENT / UNREACHABLE** (same reachability as E). Not changed, for the same recommendation.

## H. Canonical architecture preserved

Not modified:

- `CanonicalInterpretationService` and `CorrelationRules` / `CorrelationPrecedence`;
- MT-7 `RequirementCoverageService`, which already separates Reviewed, VerifiedNotReviewed, NeedsReview and NoScenario, and states "not executed";
- `ImpactAnalysisRunService` classification;
- the canonical impact.

## I. Legacy compatibility

- No GraphQL field was removed or renamed; two fields and descriptions were added.
- `QaReadinessStatus.Ready` stays in the enum, but this audit no longer returns it.
- Additive record parameters have defaults.
- No database migration.
- Legacy TraceLink and CodeLink rows are unchanged and never upgraded.

## J. Evidence levels used

| Level | Meaning |
|---|---|
| Test design linkage | a saved Covers link |
| Reviewed scenario coverage | MT-7, unchanged |
| Executed / passed | only from execution evidence; nothing here infers it |
| Change impact: Direct technical / Linked to changed source — review / Potential — review | per F |
| Review state Unknown | for every legacy link |

## K. Namespace and identity verification, plus the App_Data anchor fix

- **Identifiers:** whole-id, suffix-aware and namespace-guarded (see the semantics report, C).
- **Cross-spec isolation:**
  - Grouping validates links against the specification's own catalog (V03).
  - `SourceScenarioReferences` resolves `USn-k` only within its own specification.
- **A new defect, found while verifying:**
  - `TestAppDataIsolation` located the API folder by walking up from the test binaries. When the tests are built to another output folder (needed here, because the running app locks `bin/`), it found nothing. Hosts then still used the **real** content root, so they would have written the real App_Data and login code.
  - **Fixed:** the anchor is now `MvcTestingAppManifest.json`, the same content root `WebApplicationFactory` uses.
  - **Proven:** the new `TestAppDataAnchorTests` failed before and passes after. All host isolation tests (I01–I10, 16 tests) pass from the scratch build, and the real `local-session/launch-code.txt` is unchanged.

## L. Coverage semantics: before and after

| | Before | After |
|---|---|---|
| `coverageSummary.coveredRequirements` | "covered", undefined | "requirements with at least one linked test scenario (design linkage only)" |
| Readiness verdict (QA audit API) | Ready possible from linkage | never Ready; ReviewNeeded or HighRisk; action text names what is missing |

## M. Change impact: before and after

See F. "Recommended tests" became "Tests to review", with a per-test level, in the panel and in both exports.

## N. Release readiness consumers

- **Confirmed defect fixed:** `AIQaAuditorService.DetermineReadiness` (GraphQL `qaAuditReport`) took Ready from link-derived coverage (`SpecDriftReport.CoveragePercent`, "requirements have at least one linked test").
- **Not affected:** the frontend's own readiness consumers do not use that API.
  - `DeliveryReadinessService` reads only `audit.Findings` / `audit.Health` of the frontend rule-based `QaAuditReport`.
  - Quality Review renders the frontend audit's findings.
  - MT-7 coverage explicitly excludes execution.
- **Not rewritten:** no universal readiness system was built.

## O. Real M2LB source verification

- **Source:** `C:\Users\ajaan\Downloads\M2LB (8)` is accessible, read-only (39 `spec.md` files). The SkoleService spec is byte-identical to the test fixture.
- **Verified with real data (tests):**
  - US3/US8 → FR-009 and US6 → FR-041: visible, never decisive;
  - FR-015 / 015a / 015b / 015c distinct;
  - "UT-FR-001..005" is not FR-001;
  - "[SR-FR-0xx]" is not local.
- **Convention, read-only:** `USn-k` is used in MeldingsutvekslerReceiver and SkoleAdapter.
- **Cross-service:** SkoleAdapter's spec names SkoleService's "FR-038..FR-040e" without a prefix (SDD audit). It is never joined automatically; links validate only against the owning specification's catalog.
- **Not done:** a per-service grouping run for Person, Tjeneste and MeldingsutvekslerReceiver. Person 001 has no FR areas, so functional grouping cannot apply there.

## P. Backend targeted results

| Run | Result |
|---|---|
| New `RelationshipIntegrityTests` (RI-01, readiness gate) plus `SourceChangeImpactServiceTests` (evidence levels) plus `TraceLinkServiceTests` | **38 / 0** |
| Host isolation tests from the scratch build | **16 / 0** |
| Schema snapshot tests | **6 / 0** |
| Broad affected-area run from the scratch build (GraphQL, schema, contract, impact, trace link, coverage, readiness, audit, traceability, grouping, structure) | 1468 passed / 24 failed: the schema snapshot (then updated, now passing) and **23 location-only failures** (tests that find `AIAssisted/xsd`, migrations or fixtures by walking up from the output folder). The same tests passed in the in-repository full run at 07:44. |

## Q. Frontend results

Full suite **9,632 / 0**, including the new export test (the uncertainty is kept in Markdown and HTML).

## R. Full regression status

| Run | Result |
|---|---|
| Last full backend (07:44, in-repo build, before this task's changes) | 7,424 passed / 1 failed (intermittent `CI_DEF_3`, passes alone) / 19 skipped |
| Full backend **after** this task's changes | final3: 7,431 / 1 (CI_DEF_3) / 19; final4: **7,432 / 0 / 19** (Final verification, FV1) |

Why the full run after this task's changes was not run:

- your app locks the in-repository build output, and I did not stop it;
- with it running, free RAM was about 6.6–7.6 GB, while a full run peaks around 6.5–6.8 GB;
- so a full run would have risked memory pressure on your live session.

The changes since 07:44 are covered by the targeted and broad runs above.

## S. Browser walkthrough

**BLOCKED.** The running app's workspace was reset at 07:55 and re-imported at 07:56. A fresh browser has no module or discovery selected. Running discovery, or changing the selected specification, would alter that live workspace, so nothing was done. No browser steps were executed in this task.

**Note:** building `BirkNext.Web.Tests` also rebuilt `BirkNext.Web`. The running dev server serves the new frontend on the next page load; it includes only this branch's changes.

## T. Performance

Not measured in the browser (see S). No new per-request traversal was added. The evidence level is set in the existing loop, and the readiness rule is O(1).

## U. Modified files (this task)

| Area | Files |
|---|---|
| Backend | `Services/TraceLinkService.cs`, `GraphQL/CoverageSummaryObjectType.cs`, `GraphQL/TraceabilityMatrixRowObjectType.cs`, `Services/AIQaAuditorService.cs`, `Services/SourceChangeImpactService.cs` |
| Shared | `shared/SourceChangeImpactContracts.cs` |
| Frontend | `Components/SourceChangeImpactPanel.razor`, `Services/SourceChangeImpactExport.cs` |
| Tests | `Unit/RelationshipIntegrityTests.cs` (new), `Services/SourceChangeImpactServiceTests.cs`, `Infrastructure/TestIsolationTests.cs` (`TestAppDataAnchorTests`), `Utilities/TestAppDataIsolation.cs`, `Contract/__snapshots__/ScenariosSchemaTests.Schema_MatchesSnapshot.snap`, `frontend/BirkNext.Web.Tests/Services/SourceChangeImpactExportTests.cs` |

## V. Migrations

None.

## W. Known limitations

1. **Full backend after this task:** run in the final verification; green in final4, CI_DEF_3 failed in final3 (FV1).
2. **Browser walkthrough:** run in the final verification, 41/42 (FV6).
3. **RI-02 and RI-04:** left latent in unreachable code.
4. **No execution lifecycle in legacy links:** distinct "linked / drafted / approved / executed / passed" states are not produced for legacy TraceLinks, because the data does not exist. MT-7 remains the evidence-aware path.
5. **`CI_DEF_3`:** an intermittent failure under the full parallel suite, cause unexplained.

## X. Remaining risks

- **External consumers of `qaAuditReport`** that expected `READY` now get `REVIEW_NEEDED` at most. This is intended.
- **About 130 legacy temporary databases** remain (no ownership marker).

## Y. Acceptance criteria

| AC | Result |
|---|---|
| 01 concurrent state reconciled | PASS (B, C) |
| 02 no completed corrections duplicated | PASS |
| 03 design coverage not shown as execution | PASS (D) |
| 04 GraphQL consumers compatible | PASS (additive; snapshot reviewed) |
| 05 FR matching scoped by source | PASS for reachable paths; RI-02 latent (E) |
| 06 duplicate short FR ids don't cross-link | PASS (reachable); latent path documented |
| 07 ambiguous references not silently resolved | PASS (`USn-k` resolver, grouping) |
| 08 computed associations not promoted | PASS (F; roles) |
| 09 legacy UI reachability established | PASS (E/G: unreachable) |
| 10 direct technical impacts retained | PASS (F) |
| 11 potential vs confirmed distinct | PASS (F) |
| 12 canonical impact unchanged | PASS |
| 13 readiness never uses linkage as passed tests | PASS (N) |
| 14 MT-7 preserved | PASS |
| 15 no duplicate engine | PASS |
| 16 reviewer decisions preserved | PASS (none existed then; US4/US5/US8 decisions applied later through the reviewer UI, FV7) |
| 17 plans and cases unchanged | PASS (original fingerprints identical; one new plan was created by the user in the app) |
| 18 namespace-safe identities | PASS |
| 19 real source verified | PARTIAL (SkoleService in depth; others by convention audit) |
| 20 backend tests only with isolation | PASS (and the App_Data anchor hole fixed) |
| 21 targeted tests | PASS |
| 22 browser verification | PASS (FV6; 41/42, F45 explained) |
| 23 no destructive DB operation by this work | PASS |
| 24 no commit, push, reset or stash change | PASS |
| 25 evidence-backed report | PASS |

## Z. Recommended follow-up

1. Run the full backend suite once the app is stopped, through the harness (`scratchpad/run_suite.ps1`).
2. Run the browser walkthrough (`scratchpad/grp_walk2.py`) once you confirm the workspace may be used.
3. Retire `ExtractionReviewList` / `TraceabilityView`, or scope them.
4. Optionally clean up the legacy temporary databases after confirming the list.

## Final verification (2026-11-25, 09:00–09:20 local)

This section supersedes R (full backend after this task: now run), S (browser walkthrough: now run) and W.1/W.2, and completes follow-ups 1 and 2 in Z.

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
