# BirkNext SDD workflow audit

Audit date: 2026-10-02  
Starting commit: `58db98ebca972413225fccbb6ea86e12a271a029`  
Branch: `008-traceability-first`

## Executive summary

BirkNext has useful SDD building blocks: a shared workspace artifact repository, deterministic Markdown extractors, a derived `ReviewContext`, Spec-Kit-oriented explorers, artifact-chain traceability, source/code traceability, workflow review/approval state, and saved workspaces. Ordinary navigation between pages does not require each page to own a separate copy of the documents. Workspace artifacts are autosaved through the workspace persistence API and restored into the shared repository.

It is not yet a reliable generic SDD lifecycle manager. The semantic chain is largely a fixed Constitution → Specification → Plan → Tasks → Data Model set. The workspace stores one current artifact per fixed artifact kind; it has no persisted authority, baseline, supersession, lifecycle, or historical-version relation. Extractors recognize some alternate Markdown shapes, but artifact role selection and parts of traceability still depend on conventional kinds, headings, and ID prefixes. Clarifications are extracted, but do not carry explicit resolution status, owner, or decision linkage. Decisions and exceptions are not a general authority model. Current traceability reports gaps, but its coverage health can turn missing or inapplicable chain elements into error-like findings.

One small gap was fixed in this change: specification requirements and references can now use arbitrary hyphenated project IDs (for example `JIRA-123`, `ABC-4`, `US-A1`) in addition to the existing compact/numeric Spec-Kit forms. A regression verifies both definition and table-reference extraction. This does not solve arbitrary role mapping, authority, history, or all downstream FR-only assumptions.

The direct answer is: BirkNext can support a Spec-Kit-shaped project and partial document workflows, and can retain a shared current workspace across page navigation. It cannot yet reliably preserve multiple artifact baselines and resolve conflicting documents, or prove a complete current path from arbitrary requirements through verified implementation and tests. A code-only project can use Source Analysis and other review pages independently, but the SDD-specific views have no first-class `NotApplicable`/`NotAvailable` model throughout. The `/implementation-review` page is currently a placeholder; task/spec alignment and code traceability are separate capabilities and must not be mistaken for a general implementation verification result.

## Repository and scope

The initial checkout was `008-traceability-first` at `58db98ebca972413225fccbb6ea86e12a271a029`. The only pre-existing dirty item was the untracked `AIAssisted/docs/birknext-project-technology-independence-audit.md`; it was preserved and is not part of this change.

The audit inspected `WorkspaceArtifactRepository`, `WorkspaceSessionRestoreService`, `WorkspacePersistenceApiService`, `ReviewContextProvider`, `ArtifactParserService`, the Spec/Plan/Task/Constitution/Data Model explorers, `ArtifactTraceabilityService`, `TraceabilityModelBuilder`, `ImplementationReview`, `TaskToSpecAlignment`, `CodeTraceability`, `ImplementationTraceability`, `RecommendedWorkflow`, and their shared models/tests.

## Current architecture and ownership

`WorkspaceArtifactRepository` is the in-session owner for artifacts of the fixed `WorkspaceArtifactType` set: Constitution, Specification, Plan, Tasks, DataModel, and Research. Explorer pages write to that repository. `WorkspaceAutoSaveService` sends the repository’s current artifacts to the persistence API, and `WorkspaceSessionRestoreService` restores saved artifacts and rebuilds `ReviewContext`. `ReviewContextProvider` is the central builder for semantic models and cross-artifact links. This is a sound single-current-workspace pattern for page navigation; the local semantic models and component state are derived caches rather than canonical artifact stores.

The saved workspace persists artifact type, filename, original path, content, content hash, encoding, parse version, and timestamps. The storage and in-memory records do not contain stable semantic artifact IDs, a configurable semantic role, authority/lifecycle status, a source version history, `supersedes`/`derivedFrom` relations, or a project baseline manifest. Replacing a role replaces its current artifact. Consequently, navigation persistence is present, but historical artifact identity and stale-result detection are not.

There are legacy `WorkspaceSessionService` and compatibility interfaces alongside the active `WorkspaceArtifactRepository`; code comments mark the former as superseded. `ArtifactParserService` delegates to domain extractors, and `ReviewContextProvider` independently invokes the same extractors to build the shared semantic context. That is parallel orchestration of the same domain parsers, rather than an independent Markdown grammar; retain one canonical parser/extractor path as the code evolves. The pages do still have local UI state and some legacy import paths, which should remain presentation/input state rather than an alternate canonical artifact store.

## Artifact roles, dialects, and parsing

The current artifact-kind enum is a useful starting role list, but it is closed and mixes semantic roles with one expected workspace slot. `SpecExplorerService`, `PlanAnalysisService`, `TaskExplorerService`, `ConstitutionAnalysisService`, and `DataModelAnalysisService` each produce domain-specific models. The Spec and Task parsers tokenize Markdown through `MarkdownTokenizer`; Plan and Constitution have their own specialized extraction logic. Current data structures preserve many headings, content blocks, user stories, acceptance scenarios, assumptions, edge cases, clarification Q/A, architecture decisions, phases, task hierarchy, and entities.

Spec-Kit support is the strongest path: conventional Constitution, Specification, Plan, Tasks, Research, and Data Model artifacts are first-class explorer concepts. Some headings and inline formats are flexible. There is no general artifact-role detector that combines explicit metadata, filename, and content with confidence, and no project mapping UI for `requirements.md`, `technical-design.md`, ADR folders, backlog exports, or other dialects. Research is a workspace artifact kind but is not a peer in the central `ReviewContext` semantic chain. TestSpecification and ImplementationEvidence are not general artifact roles.

The fixed requirement identifier set was a concrete false-genericity point in `SpecExplorerService`: starts and inline references previously accepted only FR/NFR/SC/US/UC/AC/TS/REQ/TC followed by numeric IDs. The parser now additionally accepts an uppercase prefix plus hyphen plus alphanumeric suffix, preserving custom IDs and keeping legacy numeric padding for known prefixes. Unknown hyphenated identifiers are conservatively classified as requirements. Requirement identifier recognition elsewhere is not uniformly generic: `ArtifactTraceabilityService` and `TraceabilityModelBuilder` still have FR-specific extraction/fixture assumptions, so this parser change does not imply all source links or historical fixture cross-links accept every ID scheme.

## Authority, decisions, conflicts, and open questions

The repository has no artifact authority/lifecycle model for Draft, Baseline, Approved, Superseded, RejectedAlternative, Historical, or OpenQuestionSource. It cannot determine a current authoritative artifact when multiple versions are loaded, because it stores one artifact per kind and its workspace identity is not a logical SDD baseline. Content hashes support identity/integrity metadata but do not establish authority.

The specification model represents clarifications as question/answer pairs and can parse Q/A and decision-session forms. It does not have a durable question state (`Unresolved`/`Resolved`), owner, resolution date, source link, or explicit Question → Resolution → Decision → affected-item relation. A parsed answer is evidence that text contains an answer, not an approved decision. Decision nodes are extracted in a particular document context, not as general cross-artifact decisions with authority/supersession links. No generic exception record links a governing rule, scope, rationale, approver, expiry, and evidence.

Constitution analysis parses rules and derives coverage/compliance-style reports. Presence of the Constitution artifact is not itself an approval record, and I found no general precedence model for parent/global, organization, project, or component rules. Exceptions and resolved-versus-active document conflicts are not modeled. Existing text/traceability differences may therefore be shown as gaps without a reliable way to decide whether they are active conflicts, resolved history, or superseded alternatives.

## Requirements, acceptance criteria, and readiness

The specification semantic model includes requirements, user stories, success criteria, acceptance scenarios, assumptions, clarifications, security considerations, and edge cases. Requirement type/category is partly inferred by the extractor. Acceptance scenarios and success criteria are separate extracted elements, but testability is not a first-class assessment of each criterion (for example, measurable outcome, precondition, boundary, expected error path). Nonfunctional concerns can be extracted from content/sections but are not a complete, consistently typed requirement taxonomy.

`ArtifactTraceabilityService` builds Constitution→Specification, Specification→Plan, and Plan→Tasks chains from `ReviewContext` links and plan/task structure. It reports missing and orphaned links. These are useful traceability observations, not proof that an artifact is defective. Some health rendering computes aggregate percentage and escalates large gap counts to `Error`; that conflates coverage evidence with lifecycle readiness and is unsafe when a project intentionally omits an artifact or is still in discovery/specification. Plan coverage is based on architecture-decision links; it is not equivalent to full design coverage. Requirement → Task is available through semantic links, but the report is not a complete generic evidence graph spanning acceptance criteria, implementation files, executed tests, runtime, and release.

The existing Requirement Traceability feature reports linked/missing tests, stories, and success criteria, and should be interpreted as design/link coverage. The presence of a test link does not mean the test is implemented, executed, or passed. The separate Impact Analysis requirement mode also has its own test-link semantics; it is not a general SDD verification ledger.

## Implementation and source evidence

`ReviewContext` and `TaskToSpecAlignment` can compare task/spec content; `CodeTraceability` and `ImplementationTraceability` provide additional code/work-item evidence. These capabilities are distinct and their evidence should remain distinct: a task marked complete is not implementation verification; a source link is not runtime evidence; a document describing behavior is not a source implementation.

The `/implementation-review` page currently accepts pasted code but its action is explicitly a placeholder (a delay with no analysis result). It must not be reported as implemented verification. `TaskToSpecAlignment` is the more substantive existing task/spec analysis, but it does not convert task completion into source evidence. Implementation Traceability currently has an Azure DevOps provider, so the evidence source is provider-specific even though code traceability can be used locally. Persisted workspace artifacts survive save/restore, but substantive page review results are generally derived or page-local; there is no common persisted SDD review result keyed to artifact fingerprints with stale/current status.

## Change, baseline, and invalidation

There is no SDD baseline containing selected versions of all artifacts, no generic artifact-to-artifact version comparison, and no requirement-level change record for additions/removals/criterion changes/priority/question-resolution changes. Since artifact replacement overwrites the current role and no version lineage is retained, dependent traceability cannot reliably be marked stale while preserving historical links. Workspace-level artifact-set hashes do not replace per-artifact authority and baseline semantics.

`ReviewContext` is rebuilt from the current repository after artifact changes, which correctly refreshes derived semantic models without clearing unrelated Source Analysis snapshots. However, the product does not define dependency-scoped invalidation for saved analysis results because SDD results are not persisted as version-bound artifacts.

## Workflow, persistence, and page behavior

The Recommended Workflow uses backend-persisted review/approval state and correctly distinguishes `ReviewState.Reviewed` from `ApprovalState.Approved` in its model. Its static definitions are strongly Spec-Kit shaped: Constitution, Specification, Plan, Tasks, Artifact Traceability, then Implementation Review. The traceability and task/spec steps declare fixed prerequisite artifact kinds and the chain is not selected from a project workflow profile. This makes it useful as a guided Spec-Kit path but not yet as a generic adaptive SDD workflow. Other review routes remain independently accessible; the SDD workflow is not the universal application entry gate.

Explorer pages consume the shared workspace when artifacts are loaded. Sample Project loading populates workspace artifacts and saved project identity is restored. Because there is one artifact slot per kind, loading two competing specifications into one workspace cannot preserve both as current/historical evidence. The workspace restore path may classify legacy sample projects for safe restoration; that behavior is persistence policy and should not become artifact authority.

Dashboard and WorkflowReadiness surface artifact presence/status, but they do not provide an SDD maturity model with `NotApplicable`, `NotAvailable`, `Partial`, `NeedsAttention`, and `NotAssessed` at each evidence layer. Some readiness calculations use counts/approvals and fixed SDD steps. A missing artifact must therefore be understood as missing evidence in that view, not automatically as failed quality or failed implementation.

## Regression scenarios and observed behavior

| Scenario | Current result / limitation |
|---|---|
| Spec-Kit artifacts | Best-supported path; shared repository and deterministic extractors work. |
| Custom names with equivalent content | Can be manually assigned to a fixed slot, but no semantic role mapping/detection workflow. |
| Document-only project | Documents can be explored and traced; absence of source should not be described as implementation. NoSourceAvailable is not uniform across all SDD views. |
| Code-only project | Source/review pages can work independently; SDD is not required to load code, but SDD-specific status is not consistently NotApplicable. |
| Partial SDD (Specification + Tasks, no Plan) | Models can be built from available artifacts; traceability can report gaps, but gap health may overstate concern and cannot express applicability. |
| Conflicting old/new approved documents | Cannot resolve deterministically: authority and supersession metadata are absent. |
| Requirement with unresolved question | Q/A extraction exists; explicit unresolved status is not retained as a first-class decision state. |
| Completed task with no code | Task completion remains task evidence; `/implementation-review` does not verify code. |
| Custom IDs | Parser now retains hyphenated custom IDs; some downstream traceability utilities remain FR-oriented. |
| Requirement → plan/task/source/test path | Partial paths exist across separate tools; no unified path with link confidence and artifact version. |

## Changes made and verification

Changed `SpecExplorerService` to accept and preserve custom hyphenated alphanumeric IDs in specification item declarations and inline/table references while retaining existing built-in numeric ID normalization. Added a focused regression for `JIRA-123` and `US-A1` declaration/reference matching.

Focused verification passed: 109 tests across Spec Explorer, table references, Artifact Traceability, and Task Explorer (`0 failed`, `0 skipped`). After tightening the custom-ID pattern to avoid matching ordinary hyphenated prose, 44 Spec Explorer, table-reference, and document-rendering regression tests passed.

Full frontend verification completed with 4,702 passed and 5 failed (4,707 total). The failures were outside SDD in browser trust configuration, authenticated-testing UI event handling, and active target-environment UI timing. The full backend test run built and discovered tests, then stopped producing output for several minutes; it was interrupted and has no valid suite total. The backend did report a timeout in `ManagedEdgeConnectorTests.AdvertisedWebSocketMustRemainAtValidatedEndpoint` before it stalled. These suite issues are not part of the parser diff. Frontend solution Debug and Release builds both passed; each includes the backend API project. `git diff --check` passed for tracked changes.

## Prioritized remediation

### P0 — correctness and authority

1. Add stable artifact identity/version/fingerprint records and a project SDD baseline; retain historical versions rather than overwriting evidence.
2. Add authority and lifecycle metadata (`Draft`, `Baseline`, `Approved`, `Superseded`, `RejectedAlternative`, `Historical`) and explicit relations (`supersedes`, `derivedFrom`, `clarifies`, `implements`, `references`).
3. Preserve open questions as unresolved until an explicit resolution/decision record exists; model exceptions separately from violations.
4. Replace the `/implementation-review` placeholder or relabel it so it cannot imply code verification.
5. Bind traceability/review outputs to exact artifact fingerprints and show stale/history state after dependent artifacts change.

### P1 — generic SDD evidence graph

1. Make artifact roles extensible/configurable, with Spec-Kit names as a dialect mapping rather than required slots.
2. Normalize requirements and acceptance criteria, then connect Specification→Plan→Task→Source→Test through typed, confidence-bearing relations.
3. Separate plan, task, implementation, test-designed, test-executed, test-passed, runtime, and release coverage dimensions.
4. Add conflict classification (`ActiveConflict`, `ResolvedByDecision`, `Superseded`, `NeedsClarification`, `HistoricalDifference`) and explainable evidence paths.
5. Make readiness phase/applicability aware and avoid aggregate score penalties for absent or inapplicable artifacts.

### P2 — extraction and workflow

1. Add project-level role mapping for custom files and content-based suggestions with confidence.
2. Add first-class NFR/constraint/assumption/dependency/success-criterion and acceptance-criteria testability review.
3. Adapt Recommended Workflow to available roles and project workflow choice; keep every unrelated review independently usable.
4. Extend custom ID handling through constitution, task, source, and test traceability rather than only specification parsing.
5. Export artifact inventory, authority, questions, separate coverage dimensions, limitations, and historical evidence.

## Remaining gaps

No P0 authority/history or review-result persistence work was attempted in the original audit commit. The follow-up implementation below addresses a first lifecycle slice; it does not erase the original findings or claim the full target graph is complete.

## Follow-up implementation (2026-10-02)

The current implementation adds a versioned `SddLifecycleState` to saved workspaces and captures immutable content revisions when the shared repository receives changed artifact content. Revisions retain a SHA-256 fingerprint, filename/source reference, capture time, role, and explicit authority state. Revision order does not set authority. A baseline can be explicitly assigned; assigning a new baseline to the same role moves the previous baseline to Historical. Supersession is a separate explicit action linking revisions. Autosave persists this state in `saved_workspaces.sdd_lifecycle_json`; no credentials or external calls are involved.

The `/implementation-review` placeholder was replaced with a deterministic evidence view over the shared `ReviewContext`: it displays current requirement-to-plan and requirement-to-task links, separately counts registered implementation and test evidence, and records fingerprint-bound review runs. It explicitly reports source implementation as Not assessed because no shared Source Analysis-to-SDD evidence adapter currently exists. Acceptance scenarios are recorded as `Designed`, never as executed/passed. Revisions, open clarification state, decisions, traceability links, implementation/test evidence, requirement snapshots, changes, and review runs are represented in the persisted lifecycle document.

Clarification text and parsed answers do not resolve questions. The user must provide a resolution and an authoritative reference; the action creates an Accepted decision record and links it to the question. Requirement changes are compared by stable explicit ID and deterministic content/acceptance-scenario fingerprint when a review is recorded. Changed requirements mark associated links and evidence PotentiallyStale; removed requirements move associated links/evidence to Historical. Unchanged requirement snapshots are retained without generating a new content revision.

This is a bounded implementation, not completion of the target architecture. Remaining P0/P1 work includes connecting implementation evidence to exact Source Analysis snapshots and persisted CodeLinks, an explicit current-vs-authoritative baseline manifest across all artifact roles, editing/general decision and exception workflows, artifact conflict classification, deleted-requirement comparisons across arbitrary spec dialects, acceptance-criterion-level identity beyond extracted scenario IDs, source snapshot invalidation hooks, test execution adapters, and sharing the normalized graph with the Requirements Traceability and Quality Review pages. Artifact role slots and extractors remain the current fixed repository dialect; custom IDs remain supported by Specification Explorer as previously recorded.

The original full-suite baseline above predates this follow-up. Follow-up focused and full verification results must be read from the implementation task report/commit rather than inferred from the audit's earlier totals.

## Evidence graph integration follow-up (2026-10-02)

This follow-up connects the lifecycle workspace to Source Analysis snapshot metadata and persisted CodeLinks, adds a provider-neutral executed-test evidence record/import boundary, and changes Requirements Traceability and the SDD portion of Quality Review to consume the shared lifecycle projection. Implementation Review now presents plan, task, source-reference, designed-test, and executed-test evidence as separate dimensions. Source evidence retains the Source Analysis snapshot ID, archive fingerprint, analyzer version, analysis time, and limitations. CodeLink provenance is preserved; because Source Analysis does not retain a complete file inventory, CodeLink target resolution remains `NotAssessed` and does not claim behavior or implementation completeness. On snapshot change, prior evidence remains in history and is marked `PotentiallyStale` with an explicit reason; no source evidence is deleted.

Executed test records are separate from designed scenarios. Import records distinguish execution state (`Completed`, `ExecutionFailed`, etc.) from result (`Passed`, `Failed`, etc.), retain provider/result source and optional requirement, acceptance-criterion, environment, build, source snapshot, and timestamp references, and deduplicate by provider result ID or record fingerprint. Requirement/acceptance-criterion links are created from explicit references only. Requirement or acceptance-criterion changes mark only associated evidence potentially stale; source snapshot changes mark only executions explicitly bound to a different source fingerprint potentially stale. Prior executions and results remain available.

Requirements Traceability now uses the same lifecycle projection as Implementation Review and shows plan, task, implementation, designed-test, and execution evidence independently, with gap/orphan sections. Quality Review synchronizes and consumes the same graph, adds deterministic SDD findings, and persists a graph-fingerprint-bound review run; missing source/execution evidence is `NotAssessed`, while unresolved questions and stale evidence remain explicit findings. Graph synchronization does not reconfirm stale links merely because a page was opened. Existing non-SDD review packs and their parsers remain separate and unchanged. These integrations are a first shared-consumer slice, not a complete conflict/exception workflow or full lifecycle graph editor.

Remaining limitations include: source target re-resolution is unavailable without a retained file/symbol inventory; executed-test ingestion currently uses the generic structured JSON boundary and has no JUnit/TRX/pipeline provider; graph links are workspace JSON rather than a normalized database graph; conflict and exception workflows remain limited; role inference/custom role assignment remains incomplete; and the graph still projects the active `ReviewContext` selection rather than reconstructing a non-selected authoritative baseline revision. Authority metadata is visible in lifecycle history, but authoritative-baseline-driven graph projection remains P0 follow-up.

Verification for this follow-up: focused lifecycle/Implementation Review/Requirements Traceability tests passed 18/18; full frontend passed 4,731/4,731; full frontend solution Debug and Release builds passed; backend solution Debug and Release builds passed; `ManagedEdgeConnectorTests` passed 10/10 in isolation. The full backend suite reproduced `AdvertisedWebSocketMustRemainAtValidatedEndpoint` failing with `TaskCanceledException` instead of `ArgumentException`, then stalled without a suite total and was stopped. That test passed 3/3 in isolation. At 1440/1100/768/390 px the three changed routes showed no page-level horizontal overflow. Axe scoped to the Implementation Review and Requirements Traceability content regions reported no WCAG 2.1 A/AA violations. A full-page axe run also reported low-contrast shared navigation section labels; Quality Review's empty state reported two pre-existing instructional-text contrast findings. The empty-workspace browser smoke does not render the conditional shared-SDD panel on Quality Review, so that populated state was covered by component/build checks rather than axe in this run.
