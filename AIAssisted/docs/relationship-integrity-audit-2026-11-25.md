# BirkNext relationship integrity audit — 2026-11-25

**Mode:** static, read-only audit. No tests, services, databases or import flows were run. No application code or data was changed by this audit.

## A. Executive summary

| Area | Integrity assessment | Highest risk | Confirmed findings | Suspected findings | Recommended priority |
|---|---|---:|---:|---:|---|
| Shared canonical relationships | Strong design: typed identities, evidence, resolution, decisions and freshness are represented. | P2 | 0 | 1 | Preserve and make consumers use it consistently |
| SDD interpretation and story grouping | Previous semantic/substring/range defects are being corrected in the working tree. | P2 | 0 current confirmed in reviewed grouping path | 1 | Reconcile concurrent patch before merge |
| Requirements Traceability / legacy traceability | Two separate systems remain: reviewed canonical associations and a legacy GUID trace-link system. | P2 | 0 | 2 | P2: keep lifecycle/evidence semantics consistent across exposed summaries |
| Test Coverage | New MT-7 calculation correctly separates association, scenario review, freshness and execution. Legacy matrix labels any linked test scenario “Covered.” | P2 | 0 | 1 | P2: define the legacy metric as design linkage or route it through evidence-level states |
| Change Impact | Source snapshot comparison and canonical graph impact both preserve uncertainty in different ways. | P2 | 0 | 1 | Verify that exported/UI severity never upgrades potential links |
| Release readiness | Several purpose-built readiness services exist; no single repository-wide relationship-backed release score was established by this static pass. | — | 0 | 1 | Requires targeted runtime/data-flow review |
| Source import / snapshots | Specification evidence is fingerprinted and canonical interpretation is snapshot-scoped. | P2 | 0 | 1 | Verify invalidation and lifecycle behavior at runtime |
| Delivery / integration / test data | Separate typed models and evidence layers exist; they should not be conflated with SDD FR traceability. | P2 | 0 | 1 | Preserve source and review boundaries |

**Top five priorities**

1. **P2 — Legacy coverage semantics:** `TraceLinkService` computes `Covered` and a coverage percentage from existence of a `Covers` link to a `ScenarioKind.Test`; it does not require case approval, execution, or a passed result. The visible tooltip says “linked acceptance test,” so the observed calculation is a design-linkage metric, not execution coverage. Keep that evidence ceiling explicit wherever the GraphQL summary is presented. No false execution claim was confirmed.
2. **P2 — Legacy traceability joins on short FR IDs:** `TraceabilityModelBuilder` builds an unscoped `FR-ID → test IDs` dictionary and later uses that as a fallback. Same printed FR IDs in different feature specs can cross-link tests if multiple artifacts are supplied to one model. The view is not currently shown by a page in the repository search, so impact is latent pending confirmation of reachability.
3. **P2 — Preserve the ongoing traceability patch as a reviewed change:** the worktree includes uncommitted changes to parser, task evidence, grouping, suggestion and coverage-presentation code. Most changes appear aimed at the exact defects from the prior audit, but this audit did not execute the new tests or establish a clean final patch state.
4. **P2 — Avoid relationship-based impact promotion:** the canonical graph correctly labels paths through unestablished edges `Potential`, but the older source-change impact path joins `CodeLinks` and `TraceLinks` in a separate store. Verify UI/export labels keep these recommendations as review candidates and do not present them as confirmed functional impacts.
5. **P2 — Reconcile rule-specific freshness:** canonical guide relations carry scenario and requirement fingerprints; MT-7 also checks scenario fingerprint and current tester review. Other consumers should use the shared freshness/decision rule rather than reading `Active` or “confirmed” alone.

### Finding status

- **ALREADY FIXED / being corrected in the worktree:** the prior SDD audit's word-overlap US→FR promotion, substring FR matching, number-range US mapping, and deterministic FR→SC table are no longer present in the inspected working-tree `SpecExplorerService` / `TraceabilityModelBuilder` revisions. These files are modified and uncommitted; this is not a verified release state.
- **STILL PRESENT:** the legacy trace-link summary conflates test design links with a general “Covered” status; the legacy FR-to-test fallback keys only by short FR ID.
- **REQUIRES RUNTIME VERIFICATION:** actual route/page reachability for the legacy traceability view; current data and user-visible release-readiness outputs; persistence/invalidation behavior across reimports; concurrent patch final behavior.

The prior reports' SkoleService observations are source-specific. They do not establish a repository-wide defect in the current grouping implementation. This audit also does not treat a missing direct US–FR link as an SDD defect.

## B. Repository snapshot and scope

- Repository: `C:\Users\ajaan\source\sdd-repos\BirkNext`
- Branch: `008-traceability-first` (tracking `ado/008-traceability-first`)
- HEAD: `9e4988bff3b0514a25f18a9cf86645be01a07ab5` (expected baseline)
- Working tree: numerous modified and untracked files, including the same SDD parser, grouping, suggestion and coverage files covered by the earlier audits. A new untracked `TraceabilitySemanticsTests.cs` was observed after session recovery. No attempt was made to identify, stop, or contact the concurrent task.
- The requested report filename did not exist when checked; this report uses the requested path.
- Read prior reports: `sdd-cross-artifact-traceability-audit-2026-11-25.md`, `fr-story-grouping-and-backend-stalls-2026-11-24.md`, and the newer `spec-structure-compliance-audit-2026-11-25.md`.
- Representative real-project evidence for SkoleService is documented in the prior audit from the M2LB input. Those source fixtures are not present in this repository checkout, so this pass did not independently reopen them. Repository `rg` found no matching checked-in spec/task/contract fixtures for SkoleService, PersonService, SkoleAdapter, TjenesteService or MeldingsutvekslerReceiver.
- Static inspection only. No test execution, app startup, database access, external network, mutation scripts, migration or generated report run.

## C. Current relationship architecture

BirkNext does not have one universal relationship store. The current code has at least four distinct systems with different intended evidence:

1. **Canonical Interpretation registry:** project-scoped typed entities and typed relations, with stable identity keys, source evidence, rule IDs, confidence, resolution, review decisions, evidence fingerprints and interpretation snapshots. Main contracts: `shared/CanonicalCorrelation.cs`; persistence: `backend/BirkNext.Api/Data/CanonicalInterpretation/CanonicalInterpretationEntities.cs`; orchestration: `backend/BirkNext.Api/Services/CanonicalInterpretation/CanonicalInterpretationService.cs`.
2. **Specification / test-planning evidence:** extracted specification objects and `CoverageWorkspaceEvidence`; scenario-to-requirement identities and task evidence are assembled by `SpecificationEvidenceBuilder` and the browser `TestCoveragePresentation`. Grouping decisions are separately persisted by specification path, with decision history, in `StoryGroupingDocument`.
3. **Legacy scenario traceability:** GUID-based `Scenario` entities and `TraceLink` rows in `AppDbContext`, with `TraceLinkType.Covers`; the `TraceLinkService` matrix and percentage are separate from MT-7 canonical requirement coverage.
4. **Source-change impact and delivery reporting:** source snapshots, code-file links, delivery snapshots/work items, contracts and integration journey evidence feed their respective analyzers and reporting services. Those links are not automatically SDD requirement links.

### Canonical entity / identity inventory

`CanonicalEntityType` presently supports Project, Package, Repository, Module, System, Service, Capability, Feature, UserStory, FunctionalGroup, Scenario, Task, Requirement, SourceSymbol, ApiOperation, Contract, ContractOccurrence, ContractVersion, MessagingChannel, DataEntity, Test, TestSuite, Library, ScenarioGroup and SharedFoundationGroup (`shared/CanonicalCorrelation.cs`). There are no dedicated canonical entity types for AcceptanceScenario, SuccessCriterion, IndependentTest objective, EdgeCase, Clarification, Dependency, ImplementationTask, TestPlan, TestExecution, TestResult or Release. Some appear in semantic DTOs, evidence contracts, plan models or source snapshots, but must not be assumed to be canonical persisted entities.

Canonical identity is `(project scope, entity type, IdentityKey)`, not display name: `CanonicalKeys.EntityKey` combines type and identity; canonical source ingestion reuses records by that key. Requirement/story/test identities from feature packs are scoped by normalized module and feature in `CorrelationRules.Scoped`. Guide scenario identity is guide key + scenario ID. Source symbols include package and qualified symbol; contract occurrences include source snapshot, package and path; contract versions use content/declared identity rules. Display-name changes therefore need not create a new canonical entity, while identity-key or path changes can.

Existing relationships include `Contains`, `BelongsTo`, `Implements`, `References`, `DependsOn`, `Exposes`, `Consumes`, `Produces`, `Verifies`, `AliasOf`, `CandidateSameAs`, `SameAs`, `Supersedes`, `DerivedFrom`, `OccurrenceOf`, and `Uses`. Direction is source→target. Generic relation schema does not itself encode universal cardinality; rule-specific many-to-many behavior applies.

Canonical relation state separates `Resolution` (Explicit, Derived, Candidate, Ambiguous, Confirmed, Rejected, Unresolved), `Confidence`, `RuleId`, evidence and reviewer `ReviewState`. Decision applicability uses evidence fingerprint and can become `RevalidationRequired` or `Conflict`. Canonical interpretations are immutable snapshots; entities keep a `LastInterpretationId`, and queries scope by project.

This is a good foundation. The main architectural risk is split-brain semantics: the legacy `TraceLink` database and SDD DTO relationships do not all share canonical provenance, namespace or freshness behavior.

## D. Relationship producer inventory

| Producer | Input and matching | Relationship / evidence class | Persistence and consumers | Assessment |
|---|---|---|---|---|
| `SpecExplorerService.BuildSemanticModel` | Local Markdown headings, FR definitions and references. Current worktree uses explicit references and whole-token ID matching for story/scenario evidence. | Source-derived; explicit reference. Stories without direct FRs remain unlinked. | In-memory semantic DTO, consumed by specification explorer, test coverage presentation, grouping and plan prep. | Earlier word-overlap / substring issues are **being addressed** in modified files. Verify after concurrent work is finalized. |
| `TaskExplorerService` + `StoryTaskContext.Read` | Tagged `[USn]` task rows, phase goals and task continuation lines; FR parser. | Structural plan-intent evidence, not implementation proof. Current working tree distinguishes tagged task / phase-goal / task-detail / plan mention types. | DTOs and grouping; API discovery via `SpecificationEvidenceBuilder`. | Prior browser/API evidence drift and plan co-mention promotion are addressed in visible worktree code, not verified in tests. |
| `ScenarioPlanning.SuggestRequirements` | IDs, structural clues and wording similarity within a specification scope. | Rule-based suggestion, not confirmed relationship. | Manual test planning and grouping proposal surfaces. | Must remain a suggestion; visible contract distinguishes Explicit/Structural/Semantic/Weak levels. |
| Canonical `CorrelationRules` | Snapshot-derived source facts: declared service, API route, contract content/version, source symbols, tasks, tests, specs and guides; some structured/name heuristics produce candidates. | Explicit / derived / candidate / ambiguous relations with source evidence and rule IDs. Guide textual similarity is not used for requirement links. | Canonical interpretation snapshots and relationship review, impact, coverage, integration and source analysis consumers. | Generally evidence-aware; audit downstream gates, not just relation creation. |
| `CorrelationRules.Guides.GuideScenarios` | Published/draft guide references, optional spec file scope, explicit tests named by guide. | Published explicit ID link; draft candidate; ambiguous cross-feature short ID; scenario→test→requirement is always candidate. | Canonical registry and MT-7 requirement coverage. | Strong pattern: short IDs resolve only uniquely or with a specified module/feature; via-test is never automatic coverage. |
| `TraceLinkService.CreateAsync` | Client-provided GUID source/target and `Covers` type; checks project ID and endpoint existence only for scenario kinds, duplicate exact link. | Persisted direct link; no provenance/review state beyond `CreatedBy`/`Notes`. | GraphQL mutations, matrix, coverage summary, source-change impact. | Link is authoritative to legacy consumers immediately; no approval or execution lifecycle is joined by this producer. |
| `TraceabilityModelBuilder` | Extracted candidates and `CandidateLinkEntry`; exact FR and story token parsing, then fallback association by same FR ID, story ID or context heading. | Mix of direct links and computed associations; outputs coverage status without persisting provenance for each fallback. | Embedded `TraceabilityView`; previous audit found its parent component has no page usage. | Latent namespace risk: FR test map is keyed by printed ID alone. Reachability needs runtime verification. |
| Source change impact | Compares selected snapshots from the same source identity, then joins impacted file paths to project `CodeLinks`; requirement→test joins through `TraceLinks`. | Source-derived technical impact plus supporting legacy relationship recommendations. | `SourceChangeImpactService`, API/export/history, impact page. | Explicitly keeps technical impacts if no traceability matches; recommendations cite link paths. Ensure link status is visible downstream. |
| Delivery import / `WorkItemInterpreter` | Snapshot feature/story/task structures, phase and `[USn]` / scenario conventions. | Rule-derived task membership/reference, with rule ID and linked keys; intended relationship is plan intent. | Delivery reporting and canonical interpretation. | Task association does not establish implementation completion. `Completed` is an independent source field. |
| Test discovery / source test correlation | Static test file facts, references and test names. | Explicit source references or low-confidence candidate links, depending on rule. | Canonical interpretation and test coverage. | Relationship is not execution evidence; result import/execution evidence is separate. |
| Integration topology / contracts | Declared API contracts, operations, producer/consumer and profile declarations; candidate aliases for names. | Explicit/derived topology plus proposed aliases. | Topology coverage, contract evolution, integration journeys/readiness. | Shared service/identifier does not establish a User Story link or E2E test. |
| Test Data references | Asset IDs, aliases, consumer usage and fingerprints. | Explicit binding / candidate resolution; state can require review. | Fixture generation, test planning, readiness and source-impact review. | Asset link is not proof the data is provisioned or usable in an environment. |

No AI-generated relationship producer was found in the reviewed SDD relationship paths. The repo contains AI-based change audit features, but static inspection did not establish that they persist authoritative canonical edges.

## E. Relationship transformation inventory

- `SpecificationEvidenceBuilder.Build` converts semantic parser output to `CoverageWorkspaceEvidence`; story keys are built from spec path + stable story key, scenario parent story is the same scoped key, and scenario requirements are deduplicated. Task evidence comes from same-folder plan/quickstart/tasks files. It does not treat a missing direct US–FR link as a defect.
- Browser and API task evidence were previously different. The working tree now calls shared `StoryTaskContext.Read` from the API and distinguishes `PlanMention`; this is the right direction, but modified files and the just-added semantics tests remain unverified.
- `TraceabilityModelBuilder` transforms a mix of direct `CandidateLinkEntry` links and computed FR/story/context fallback into `TracedRequirement.LinkedTests`. The result holds a single `Status`, plus a `NeedsReviewWarning` about requirement candidate status, but no per-association evidence basis. Its fallback can upgrade co-location to the same `Covered` state as a direct saved trace link.
- `RequirementCoverageService.Build` preserves each association’s resolution, review state, rule, explanation, reviewer, decision timestamp, guide/scenario identity, scenario fingerprint and whether the interpretation is outdated. A relation confirmed on stale scenario text returns to suggested trust; a tester’s accepted scenario revision is separate. It explicitly states this is not an executed test.
- `CanonicalInterpretationService.ImpactAsync` carries the relation path, direction, resolution and depth. It treats non-established paths as Potential, limits recursion, excludes rejected edges and only marks a test impacted when the final edge is `Verifies`. This avoids equating every navigation/reference edge with test impact.
- `SourceChangeImpactService` compares source snapshots using source identity and fingerprints, produces technical impacts, then maps impacted normalized paths through `CodeLinks` and requirement/test `TraceLinks`. It returns limitations when no project or matching code traceability exists. It does not calculate a source-change risk score. It should not be described as a complete semantic FR→US→scenario impact graph.
- Delivery reports and exports aggregate typed snapshots/work items. The report includes relationship/provenance test suites by name, but this static audit did not trace every export field back to calculation in all reports; see runtime limitations.
- Test-plan and manual-testing proposal flows are based on semantic scenario DTOs. A saved test case is an authored plan artifact; its source association does not establish approval, execution, pass, or readiness. Current visible contracts keep those states separate.

## F. Relationship consumer matrix

| Consumer module | Relationship used / producer | Required evidence | Actual evidence preserved | Main risk / location |
|---|---|---|---|---|
| Requirements Traceability | Canonical review relations; legacy `TraceLink`; parsed semantic links | Source-explicit or human-confirmed for verified mapping; suggestions clearly labeled | Canonical path is rich. Legacy link stores endpoints/type/creator/notes, not a review lifecycle. | Split semantics; `TraceLinkService`, `TraceabilityModelBuilder`, `Traceability.razor` / GraphQL |
| Test Coverage | Canonical guide→requirement + scenario review; legacy Covers link | Requirement identity + verified association + current accepted scenario for prepared coverage; execution/pass separate | MT-7 preserves provenance/freshness and explicitly says not executed. Legacy summary only knows linked Test scenarios. | P2 evidence-stage label must remain visible (`RequirementCoverageService`, `TraceLinkService`) |
| Manual Testing | Parsed scenarios, story grouping, requirement links, plan draft/test case relations | Primary scenario behavior; related FR context is supporting; human edits retained | Current changed model keeps scenario and story keys; no runtime audit of reimport rewrite behavior. | Modified grouping/presentation path in concurrent work; verify case ownership and stale source behavior |
| Test Plan Suggestions | Guide associations, scenario fingerprints/reviews, candidate variants | A suggestion until reviewer accepts; accepted only for current revision | MT-7 relation trust and scenario review are distinct. | Confirm guide published state is current and consumers use `AssociationTrust.Verified` rather than existence |
| Test Design Recommendations | `ScenarioPlanning.SuggestRequirements` and source scenario text | Actual Given/When/Then behavior is primary; FR context cannot replace expected result | Suggestion levels exist; parser changes are in progress. | No runtime generation performed; recommendation provenance remains a verification gap |
| Change Impact Analysis | Source snapshot diffs; CodeLinks; legacy Covers; canonical graph impact | Exact changed source; confirmed/explicit path for confirmed impact; candidate path as potential | Canonical API preserves path/resolution. Legacy source change report exposes path reasons but does not necessarily preserve review state from `TraceLink`. | P2 presentation risk; `CanonicalInterpretationService.ImpactAsync`, `SourceChangeImpactService` |
| Release Readiness | Various readiness services / reviewed coverage outputs | Approved, current, executed/passed evidence as policy requires; unresolved risks shown | No one universal relationship-based readiness calc verified. MT-7 itself is careful. | Needs targeted review of each readiness pack and UI aggregation |
| Source Interpretation | `CanonicalEvidenceAdapter`, `CorrelationRules`, parsers | Input source revision and source evidence | Canonical facts carry rule IDs, paths/references, snapshots and fingerprints. | Verify rule-set version and exact source revision visible in every consumer |
| Source Management / import | Artifact discovery, project import, specification catalog and snapshots | Correct project/source role, path and snapshot identity | Spec catalog uses normalized path, content fingerprint, archive hash and snapshot ID; imports produce stored evidence. | Role selection, reimport preservation and cache invalidation require runtime/data verification |
| Test Data | Test case/consumer→asset refs, schema/contract/Data Model impact | Explicit asset binding + current approved revision and environment availability | Asset lifecycle distinguishes approved/draft/review-required and fingerprints; source-impact adds potential flag without editing assets. | A resolved asset reference does not prove environment provisioning |
| Integration / E2E | Contract/API/service topology; integration journey definitions; source test evidence | Declared chain or reviewed association; actual test/run evidence for E2E coverage | Candidate journey associations require review; runtime state separately represented. | Shared ID/service must not create US ownership; no end-to-end source fixture reviewed here |
| Delivery Overview | Delivery snapshots, feature/story/task relations and status | Snapshot-owned work item references; completion is source status, not implementation proof | Work item records retain snapshot and scoped feature context. | Ensure tasks do not count as FR ownership or verification |
| Delivery Reports | Delivery traceability and snapshot comparison/export | Same source/snapshot lineage; avoid double-counting entities per secondary links | Dedicated snapshot identity/provenance test files exist; not executed. | Exported aggregate semantics need report-by-report verification |

## G. Requirements Traceability findings

The current canonical relationship model handles namespace and review more safely than the legacy model. Canonical requirement IDs in feature packs are `(scope, type, module/feature/id)`, and cross-feature guide references use an explicit spec path when available. An unscoped repeated short ID becomes ambiguous rather than selecting an arbitrary feature.

The modified SDD worktree also guards `FR-001` against matching inside `UT-FR-001` / `SR-FR-001`, preserves suffixes such as `FR-015a`, and removes numeric FR range→US mapping. Those fixes are visible in `SpecExplorerService`, `TraceabilityModelBuilder`, `TraceabilitySuggestionService`, and their report, but remain uncommitted.

**Confirmed legacy risk:** `TraceabilityModelBuilder` keys `frIdToTestIds` only by FR text and uses that map for fallback association. If one model receives candidates from multiple specs, `FR-001` in one feature can share tests with `FR-001` in another. The previous SDD audit documents the legacy view as unreferenced by any routed page, so classify as **P2 / NEEDS VERIFICATION (reachability)** rather than a live production P1. No evidence establishes rejected trace-link decisions being reintroduced by import in the canonical path; its precedence layer specifically preserves rejection against heuristic links and shows conflict if source later states the edge.

## H. Test Coverage findings

**RI-01 — P2 — CONFIRMED — Legacy linked-test metric records design linkage, not execution coverage**

- **Affected modules:** GraphQL coverage summary and traceability matrix; any UI consuming these values.
- **Producer:** `TraceLinkService.CreateAsync` accepts a client-supplied link after endpoint/project checks; it does not check a Test Case approval or execution result.
- **Transformer:** `GetTraceabilityMatrixAsync` sets `CoverageStatus.Covered` when at least one linked Test scenario exists. `GetCoverageSummaryAsync` counts each requirement target with a `Covers` link from an existing `ScenarioKind.Test`, and derives a percentage.
- **Consumer:** `GraphQL/Query.cs` `CoverageSummaryAsync` publishes these counts; the legacy UI is explicit that covered means a requirement with a linked acceptance test, but downstream labels must retain that boundary.
- **Evidence:** `backend/BirkNext.Api/Services/TraceLinkService.cs` `CreateAsync`, `GetTraceabilityMatrixAsync`, `GetCoverageSummaryAsync` (around lines 53–150 and 169–252); `backend/BirkNext.Api/GraphQL/Query.cs` `CoverageSummaryAsync` (around lines 65–100). Tooltip at `frontend/BirkNext.Web/Components/TraceabilityView.razor` says “at least one linked acceptance test.”
- **Observed behavior:** link existence becomes “Covered”/part of the summary regardless of test case review, execution, result, or freshness. The visible tooltip defines “Covered” as having a linked acceptance test, so the static evidence supports a design-coverage interpretation; it does not establish that the product claims execution or release readiness.
- **Expected:** distinguish “test design linked” from prepared/approved, executed and passed coverage. Do not imply execution coverage from a saved relationship.
- **User impact example:** a requirement with one linked but unapproved or unrun test scenario contributes to the endpoint’s covered count and percentage. This could be misunderstood as execution coverage if a consumer omits the tooltip/evidence stage. No observed percentage or mislabeling is asserted here.
- **Data integrity:** no mutation/deletion shown; this is evidence inflation, not data loss.
- **Minimal correction:** keep the linked-test design label in API and every consumer, or make the DTO a staged evidence model with separate linked/approved/current execution/result counts. Do not infer execution from `TraceLink`.
- **Regression test:** direct trace link to draft, approved-but-unexecuted, passed-current and passed-stale test should produce four distinct evidence states; summary denominator and counts must be explicit.
- **Concurrent work dependency:** none of the observed modified paths obviously replaces the legacy `TraceLinkService`; recheck after active task completes.
- **Scope:** MEDIUM.

The MT-7 `RequirementCoverageService` is a positive example: it labels reviewed scenario coverage only when relationship is explicit or confirmed on current evidence **and** the tester accepted the matching current scenario revision; text explicitly says it is not an executed test. Existing `RequirementCoverageService.Build` separates `Reviewed`, `VerifiedNotReviewed`, `NeedsReview` and `NoScenario`.

## I. Manual Testing findings

The previous SDD/grouping reports cover SkoleService in detail. The current worktree changes `ManualTestingDiscover.razor`, `StoryGroupingPresentation`, `StoryRequirementLinks`, `GroupOrganizer`, `ScenarioCentricPlanning` and shared contracts. The visible design keeps suggestions separate and preserves user grouping decisions. The previous report identifies partial Planlegg integration and gaps in presentation of source context, not a confirmed case-rewrite defect.

No runtime import or save was exercised. Therefore safe propagation of source relationship changes into manually authored case content, case parent ownership, prerequisite inheritance and stale scenario behavior are **NOT VERIFIED**. Do not automatically rewrite user-authored expected results or test data from changed links; use source fingerprint and preview/review states.

## J. Test Plan Suggestions / Test Design findings

`ScenarioPlanning.SuggestRequirements` is a rule-based suggestion producer, not proof. Canonical guide scenario inference similarly makes the scenario→requirement path through a named test a candidate only. Published guide references can be explicit; draft guide references remain candidates. Requirement and scenario fingerprints make changed evidence detectable.

No generation was run. Static evidence does not confirm that every plan suggestion/export retains primary scenario provenance and source excerpt all the way to user view. Recommendations should identify source scenario/line/fingerprint, distinguish supporting FR references, and must not claim an expected result absent from Given/When/Then or another current authoritative source. **No confirmed invented expected-result defect found in this static pass.**

## K. Change Impact Analysis findings

Two engines are present and must not be conflated:

- `CanonicalInterpretationService.ImpactAsync`: bounded, cycle-safe typed graph walk. Paths are retained; non-established edges become Potential; rejected relations are excluded; tests count as impacted only when linked by `Verifies`.
- `SourceChangeImpactService.AnalyzeAsync`: exact before/after snapshots for same logical source, structured comparison, source file impacts, then separate legacy CodeLinks/TraceLinks enrichment. Source technical impact is not downgraded because traceability is missing. It does not calculate a risk score and emits limitations for incomplete traceability.

No P1 false impact propagation was confirmed. **RI-03 — P2 — NEEDS VERIFICATION:** legacy `TraceLink` and `CodeLink` enrichment has no canonical `RelationResolution`/`ReviewState` in the join, so this stage cannot distinguish reviewer-confirmed from merely present code/test links. The user-facing reason says files are linked or a requirement is covered, but not whether that association was reviewed. Check `SourceChangeImpactService.AnalyzeAsync` before treating the recommended test list as a verified functional impact set. Suggested correction: mark such recommendations as “review,” include provenance and link basis, and reserve confirmed impact for evidence meeting policy. Regression: rejected/candidate relation excluded from confirmed set but retained as potential if appropriate.

Renames/moves and wording-vs-behavior semantics are handled by structured source-specific comparison code; this audit did not execute fixture comparisons across all supported domains. No claim is made that every source move is safely distinguished.

## L. Release Readiness findings

The repository has multiple readiness systems (delivery health, QA readiness, integration readiness, API/quality/service-specific readiness), not one shared canonical release calculation. No confirmed relationship-based readiness inflation was found in the files reviewed. The `RequirementCoverageService` explicitly reports no execution; legacy `TraceLink` coverage is only linkage. Thus any readiness consumer that maps this older `CoveragePercent` to release readiness without additional review/execution evidence is a **P1 risk requiring call-path verification**, not a confirmed current release defect.

Recommended evidence states remain separate: mapped; case drafted; prepared/approved; executed; passed; current for source revision; known risk; unresolved conflict. Release readiness should consume only the level required by its policy and display unknown/stale/conflict distinctly.

## M. Source Interpretation / Import findings

Specification import classifies Markdown artifact roles, stores specification content SHA and catalog revision, and builds `StoredSpecificationEvidence` with normalized path and content SHA. It scopes same-folder plan/task documents to that specification path. This is a concrete improvement over matching globally by printed IDs.

Canonical source interpretation reads selected immutable Source Analysis snapshots and a Delivery snapshot; it does not reparse arbitrary current workspace archives during a canonical query. It stores rule set, interpretation lineage, source refs and relation evidence. Reimport behavior for preserving/revalidating reviewer decisions is governed by `CorrelationPrecedence` and evidence fingerprints.

**Needs verification:** whether all source role choices, app ownership, stale evidence caches and imported user decisions are invalidated/revalidated correctly in every import route. No evidence of cross-application contamination was observed in the inspected scope, but no import was executed.

## N. Snapshot / source freshness findings

Canonical source relations carry `SourceSnapshotId` evidence where supplied; guide relations carry scenario and requirement fingerprints; interpretation snapshots have `SupersedesId`, rule-set and evidence fingerprints. A changed confirmation can become `RevalidationRequired`; an explicit source contradiction becomes `Conflict`. This satisfies the intended model for reviewed canonical relationships.

Legacy `TraceLink` records have no source snapshot, relationship fingerprint or freshness field, so their existence alone cannot tell consumers whether the linked test scenario still matches current source. The source-change analyzer reports snapshots in its output, but direct stored trace links do not become version-aware by that fact. Mark stale-link treatment in legacy consumers **NEEDS VERIFICATION**.

## O. Integration / E2E relationship findings

The canonical vocabulary includes systems, services, API operations, contracts and versions, messaging channels, data entities, tests and journeys via dedicated journey contracts. Contract occurrence identity includes source snapshot/package/path; versions and `Supersedes` links are distinct. Integration journey candidates from component-chain matching are explicitly “needs review” and count for nothing until confirmed.

No available real project source fixtures were found in the checkout, so the named service chains (Altinn/Meldingsutveksleren/SkoleService/BiRK etc.) could not be validated as actual codebase edges here. No E2E path should be inferred from common `FR-001`, shared service name or contract similarity. Missing E2E traceability is a runtime/source-evidence gap, not proof of a spec defect.

## P. Test Data relationship findings

Contracts model immutable asset IDs, alias, revision fingerprint, source of consumer link and resolution state; lifecycle can mark review-required on schema/data model change. Source impact adds a potential test-data impact and states it does not mutate assets. Fixture UI text differentiates saved draft from approved/current fixture. This is good separation of relationship from provisioning.

Unresolved: a resolved asset link still does not prove the target environment contains the data, nor that sensitive identifiers are safe for a particular source requirement. No fixture generation, data provisioning or environment check was performed. No concrete false prerequisite was confirmed.

## Q. Delivery Overview / Reports findings

Delivery has snapshot-owned feature, user story, task, phase and requirement structures; tasks retain source `Completed` state and are classified through `WorkItemInterpreter`. Snapshot-specific identity and delivery relationship tests exist. This audit did not run report tests or trace every aggregate in Delivery Overview, export, management summary and readiness dashboard. It cannot attest that all secondary relationship links are deduplicated or that every report excludes stale history. Treat that as **NOT VERIFIED**, not as a defect.

## R. Cross-service source evidence samples

Prior report contains a detailed SkoleService sample: 8 stories, 65 FRs, story/scenario/task references, namespace collisions and historical labels. This audit relied on it and the newer structure-compliance report; it did not re-open unavailable M2LB fixture files. Checked-in repository search found no actual `spec.md`, `tasks.md`, `plan.md`, contracts, data model, research or quickstart for the five priority applications. Consequently:

- `FR-001` is demonstrably a local feature-pack ID, not global identity; canonical scope solves this in the canonical path.
- `FR-015` / `FR-015a` are distinct canonical IDs; the modified parser report says suffix matching is guarded.
- Historical contract naming and superseded banners are source evidence; no alias may be inferred from display-name similarity alone.
- Scenario number reuse is safe only with parent story + specification scope; current semantic scenario key uses spec path and parent story/number in audited path.
- Cross-service FR ownership, contracts and E2E routes remain source-verification questions, not inferred mappings.

## S. Invariant compliance matrix

| Rule | Assessment | Evidence / qualification |
|---|---|---|
| 1 inferred ≠ source-explicit | **PASS in canonical path; partial elsewhere** | Resolution values exist; legacy trace view loses fallback basis. |
| 2 source reference ≠ primary business ownership | **PASS / worktree change** | Grouping roles distinguish story/scenario/task; review current uncommitted code. |
| 3 task reference ≠ implementation | **PASS by semantics** | Delivery task completion is separate; grouping report says tasks are plan intent. |
| 4 implementation reference ≠ coverage | **PASS in canonical/MT-7; partial legacy** | Test reference produces candidate; legacy `Covers` summary only means linked test scenario. |
| 5 saved case ≠ execution | **PASS in MT-7; legacy label ceiling** | Separate review/execution DTOs; legacy does not join execution. |
| 6 historical pass ≠ current readiness | **PASS where freshness-aware; otherwise not verified** | `RequirementTestExecutions` has current/history separation; all readiness call paths not inspected. |
| 7 navigation ≠ functional impact | **PASS canonical; needs verification legacy** | Canonical typed impact edges and paths; code-link trace stage cannot see review state. |
| 8 shared FR ≠ primary group | **PASS in visible modified grouping path; concurrent** | Range mapping removed, ties/suggestions separated per current reports. |
| 9 cross-service IDs scoped | **PASS canonical; latent legacy short-ID join** | Module/feature scope; `TraceabilityModelBuilder` short-ID map. |
| 10 snapshots and decisions traceable | **PASS canonical; partial legacy** | Snapshot/evidence fingerprint/decision history; legacy trace links lack source revision. |
| 11 ungrouped stories/scenarios discoverable | **PASS per current grouping reports; not runtime checked** | Prior report records permanent ungrouped view; modified UI. |
| 12 link changes do not rewrite authored cases | **NOT VERIFIED** | No import/save runtime run. |
| 13 source-explicit references immutable | **PASS canonical model; verify SDD persistence** | Canonical source fact separate from human dispute; grouping source links may be disputed, not erased per report. |
| 14 supporting/context links not primary behavior | **PARTIAL / concurrent** | Current grouping evidence kinds distinguish support; check consumers. |
| 15 transitive ≠ direct | **PASS canonical impact path; partial other reports** | Path/depth retained in graph; legacy joins can flatten basis. |
| 16 no secondary-link double count | **PARTIAL** | Several paths dedupe by GUID/canonical key; report-wide aggregates not verified. |
| 17 missing explicit US–FR not SDD defect | **PASS** | No direct mapping required by spec template; links may be absent. |
| 18 grouping never blocks testing | **PASS per current report; runtime not verified** | Manual flow displays ungrouped stories; concurrent changes. |

## T. Findings and risk classification

### RI-01 — P2 — CONFIRMED — Legacy linked-test metric records design linkage, not execution coverage

Full required finding detail is in section H. A requirement linked to a test scenario can become “Covered” and count toward the summary with no approval, run, result, or freshness evidence. This is a confirmed evidence-level behavior; whether a consuming release-readiness dashboard misuses the number is not verified.

### RI-02 — P2 — NEEDS VERIFICATION — Unscoped FR short-ID fallback in legacy traceability model

- **Affected modules:** embedded/legacy traceability view, if reachable.
- **Producer:** `TraceabilityModelBuilder.Build` reads all supplied candidates and explicit requirement/test links.
- **Transformer:** dictionary `frIdToTestIds` is keyed on `frId` only; fallback at `linkedTests` uses that ID without spec path/module.
- **Consumer:** `TraceabilityView.razor` renders requirements, coverage and percentages. Repository search found no routed page rendering its parent `ExtractionReviewList`, so active reachability is not established.
- **Evidence:** `frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs` around lines 163–206; `Models/TraceabilityModels.cs`; `Components/TraceabilityView.razor` around lines 20–50 and 508; parent `ExtractionReviewList.razor` around line 499.
- **Observed behavior:** if multiple feature specifications enter one Build call, same short FR ID can share the same linked-test ID set. Candidate IDs themselves are GUIDs, but the fallback key discards their source scope.
- **Expected:** scope requirement identity by specification path + ID; if origin cannot be resolved, report ambiguity and do not mark covered.
- **User impact example:** separate feature packs each defining `FR-001` could have one pack’s linked test appear on the other pack’s FR in this model. This is a static consequence, not observed output.
- **Data integrity:** display/calculation contamination only; no cross-project write shown.
- **Minimal correction:** use scoped `SpecItemKey`; prohibit FR-ID fallback across specs or return a suggestion/ambiguous state.
- **Regression test:** two specs with same FR ID and separate test candidates remain isolated; unresolved duplicate is not covered.
- **Concurrent work:** `TraceabilityModelBuilder.cs` and tests are modified; inspect final changes before implementation.
- **Scope:** SMALL.

### RI-03 — P2 — NEEDS VERIFICATION — Legacy change-impact enrichment cannot retain review state

See section K. The source change stage joins CodeLinks/TraceLinks, which do not carry canonical relation review/freshness fields; output reasons can list a test as recommended due to direct file or Covers link. It may be entirely appropriate as “review this test,” but this static review did not establish presentation of “potential” versus confirmed. Do not call it a confirmed impact defect.

### RI-04 — P2 — NEEDS VERIFICATION — Legacy traceability view's computed fallback links have no evidence badge

`TraceabilityModelBuilder` can associate tests by FR ID, story ID or same context heading after direct links are absent. It sets `TraceCoverageStatus.Covered` uniformly and stores no association basis for each computed link; only a generic `CoverageReason` records a string. If reachable, a reviewer cannot reliably distinguish saved direct links from derived grouping. Since its parent appears unreferenced, keep this latent until route verification.

### No confirmed defects found in the following reviewed paths

- Canonical identity and relationship review state model.
- Canonical guide scenario → requirement correlation rules and ambiguity handling.
- MT-7 current reviewed scenario coverage gate.
- Canonical graph impact classification of candidate paths and verified test edges.
- Current visible SDD explicit-ID and grouping semantics, pending uncommitted review.

## U. Severity-ranked remediation backlog

| Priority | Work | Reason |
|---|---|---|
| P2 | Define legacy `CoverageSummary` evidence label/shape and ensure consumers cannot interpret linked test design as executed/passed coverage. | Confirmed evidence ceiling; no execution-coverage mislabel confirmed. |
| P2 | Namespace every legacy FR/story/scenario fallback by specification/source identity; expose ambiguity. | Prevent cross-feature collision if model is used with multiple specs. |
| P2 | Preserve resolution/review/freshness provenance in source-impact recommendations or explicitly label them potential review items. | Avoid converting navigation into confirmed impact. |
| P2 | Finish and review current SDD worktree as one coherent patch; reconcile API/browser rules; do not duplicate its fixes. | Changes overlap active task and previous audit. |
| P3 | Remove/retire dormant legacy traceability view or keep it aligned with canonical semantics, after verifying route and consumers. | Reduce latent competing model. |

## V. Minimal architecture changes

1. **Source truth:** retain canonical `(scope, type, identity)` and include source path/feature identity in all remaining noncanonical FR/story keys. Never normalize IDs into a global namespace.
2. **Semantics:** require each relationship DTO to carry at minimum: source key, target key, typed relation, resolution/evidence class, provenance reference, source fingerprint, review state and direct/transitive indicator where relevant. Do not flatten to `Linked=true` for decision consumers.
3. **Consumer gates:** navigation may show candidates; coverage requires actual case relationship and prepared/approved state as labelled; execution coverage needs a current execution result; readiness applies policy to current approved/passed evidence; impact may include candidates as “review,” but cannot label them confirmed.
4. **Compatibility:** legacy GUID TraceLink rows have no provenance/freshness. Avoid destructive migration or silently upgrading them. Introduce compatibility views that label their evidence as “manually linked test design, freshness/execution unknown”; gather runtime consumers before schema migration.
5. **Safe order:** complete concurrent parser/grouping work; resolve remaining namespace drift; align legacy coverage semantics; align impact labels; then update Manual Testing/Test Data/Delivery/report consumers and retire unused legacy paths.

## W. Regression tests recommended

No tests were run or added by this audit.

1. Same `FR-001` in two feature specifications never shares tests, stories, aliases or coverage.
2. `FR-015` and `FR-015a` stay distinct; `UT-FR-001`, `SR-FR-013` and local `FR-013` do not collide.
3. Published explicit guide reference can be verified; draft or via-test relation remains a candidate; duplicate IDs without spec scope are ambiguous.
4. Confirmed/rejected relation after unchanged, changed, removed and contradicted source evidence follows `CorrelationPrecedence` and retains decision history.
5. Test-design linked, draft, approved, executed, passed and stale-result states produce distinct outputs; no draft or unrun case contributes to execution readiness.
6. Source impact through candidate, rejected and confirmed edges preserves Potential/Excluded/Direct classifications and source paths in HTML/Markdown/UI exports.
7. Reimport with moved/renamed contract or changed source path does not silently rewrite test case ownership or preserve stale confirmation.
8. Delivery task completion never creates implementation or test coverage without corresponding evidence.
9. Duplicate relations and scenario reuse across stories do not inflate unique requirement/scenario totals.
10. Manual edits survive reimport; changed source is shown for review with a diff and no automatic rewrite.

## X. Dependencies on ongoing work

The worktree already changes `SpecExplorerService`, `SpecificationSemanticModel`, `SpecificationEvidenceBuilder`, `TaskExplorerService`, `TestCoveragePresentation`, `TraceabilityModelBuilder`, `StoryGroupingPresentation`, shared relationship contracts, grouping services/UI and tests. The new traceability semantics test was observed after recovery. These likely overlap the prior audit’s recommended fixes. Do not reimplement or overwrite them. This report has not modified those files. Reassess RI-02/04 and SDD/grouping status after the working tree is finalized and tests are run by the implementation owner.

## Y. Known limitations and unanswered questions

- Static code review only; no runtime output, database values, imported plans, saved decisions, test runs or actual readiness reports were inspected.
- No checked-in source fixtures for requested service examples; the prior audit's M2LB source sample was reused as reported evidence, not independently revalidated.
- The current worktree is active and uncommitted. Code may have changed after inspection.
- Repository search did not find a routed use of `ExtractionReviewList`; absence from source search is not proof a dynamically composed host cannot use it.
- This pass sampled shared relationship architecture and high-risk consumers, not every one of BirkNext's many domain-specific readiness and reporting products. Release readiness, all report aggregate deduplication, complete import lineage and E2E source truth remain runtime/focused-audit questions.
- No claim is made that all derived links are defects. Candidate relations are useful when provenance and uncertainty remain visible.

## Z. Acceptance status

| Acceptance | Result |
|---|---|
| AC01–AC02 major producers/consumers inventoried | PASS for shared traceability, canonical, SDD, coverage, impact, source, test data, integration and delivery paths; not every feature-specific relationship in the product |
| AC03–AC06 identity, provenance, direct/transitive, roles | PASS for canonical model; split legacy limitations identified |
| AC07–AC14 core consumer review | PASS with stated unverified areas |
| AC15–AC18 imports, snapshots, integration, test data | PARTIAL; static code only, no source fixtures/runtime |
| AC19–AC21 evidence and severity | PASS for confirmed findings; suspected risks labeled |
| AC22 minimal remediation | PASS |
| AC23 ongoing work not disturbed | PASS |
| AC24 no code/config/database/user data changed | PASS; only this report was created |
| AC25 no tests/commits/pushes/destructive operations | PASS |
| AC26 complete evidence-backed report | PASS within static audit limitations above |
