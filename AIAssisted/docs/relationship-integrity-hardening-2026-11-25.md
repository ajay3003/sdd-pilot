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
| Full backend **after** this task's changes | **NOT RUN** |

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

1. **Full backend after this task:** not run (see R).
2. **Browser walkthrough:** not run (see S).
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
| 16 reviewer decisions preserved | PASS (none existed; nothing written) |
| 17 plans and cases unchanged | PASS (original fingerprints identical; one new plan was created by the user in the app) |
| 18 namespace-safe identities | PASS |
| 19 real source verified | PARTIAL (SkoleService in depth; others by convention audit) |
| 20 backend tests only with isolation | PASS (and the App_Data anchor hole fixed) |
| 21 targeted tests | PASS |
| 22 browser verification | BLOCKED (S) |
| 23 no destructive DB operation by this work | PASS |
| 24 no commit, push, reset or stash change | PASS |
| 25 evidence-backed report | PASS |

## Z. Recommended follow-up

1. Run the full backend suite once the app is stopped, through the harness (`scratchpad/run_suite.ps1`).
2. Run the browser walkthrough (`scratchpad/grp_walk2.py`) once you confirm the workspace may be used.
3. Retire `ExtractionReviewList` / `TraceabilityView`, or scope them.
4. Optionally clean up the legacy temporary databases after confirming the list.
