# BirkNext — Multi-source end-to-end integration testing: implementation (2026-11-10)

Branch `008-traceability-first`. Baseline `54db534d`. Local commits: `0ed3fe56` (backend), `e6b00385` (UI), reports commit (final SHA in the
session report). No push, no deployment.

The Integration Suite Generator is extended, not replaced: a suite is still a manifest of references to existing owners (Source Analysis
snapshots, Contract Registry/Testing, Test Data, the Skolenærvær journey pack, M01 Security Classification, Execution Evidence). What is new is a
**Composite Source Context** that lets one suite draw on several imported snapshots of different projects, and a **composite run** that executes
an approved cross-stage plan as one continuous, correlated transaction through the existing chain runner.

## 1. Composite Source Context (C, D)

| Element | Implementation |
|---|---|
| Model | `shared/CompositeSuiteContracts.cs`: `CompositeSourceContext` (ContextKey = scope + owning project, Revision, Snapshots, StageMappings, Compatibility, Changes, Fingerprint, CreatedAt/UpdatedAt/CreatedBy); `CompositeSnapshotAssociation` (snapshot id, project key, archive name + SHA-256, analysed-at, analysis status, stages, folders per stage, evidence, confidence, approval Proposed/Approved/Rejected with who/when/reason) |
| Store | table `composite_source_contexts` (migration `20261110160044_AddCompositeSourceContexts`): one immutable row per revision (unique ContextKey + Revision), JSON definition. References only — no contract, schema or source text is copied; no second snapshot store |
| Service | `CompositeContextService`: `DiscoverAsync`, `SaveAsync` (next revision; `ExpectedRevision` conflict guard; unchanged decisions → `Unchanged`, no new revision), `GetAsync` (newer snapshots of associated projects proposed with their stages, never applied), `ListAsync`, `RevisionsAsync` |
| Cross-project rules | each snapshot keeps its project key (`ContractRegistryService.ProjectKeyOf`); a snapshot that is not recognised for a stage needs a written reason; decisions keep reviewer/time while unchanged. BirkNext has **no per-project ACL** (single local workspace) — the context grants nothing: saving needs `IntegrationConfigurationWrite`, approving a plan the same, running `ActiveEventExecute`; generation and runs use **approved** associations only |
| Memory | snapshot facts (folders, project/component names, contract facts, channels, dataset contracts — no source text) are extracted one snapshot at a time and cached by id (≤ 64), bounded to the newest 60 snapshots |

## 2. Stage-to-source discovery and compatibility (E, F)

* Stage model = the existing `skolenaervaer` scope stages (ids unchanged: `altinn`, `mu`, `mm`, `skole-intake`, `person-tjeneste`, `period-lock`,
  `bufdata`), each now carrying a `StageSourceRequirement` (tokens, exclusion tokens, built-in reference, dataset-contract evidence, channel to the
  next stage, correlation field names, action words).
* `CompositeSourceRules.Discover`: a folder is a stage's source when a word of its name or of a project/component in it equals a token and no word
  equals an exclusion token (`ArchitectureRuleEvaluator.MatchesAny`, the journey packs' matcher). Newest snapshot per project competes; older ones
  are history. CONFIRMED (approved, or a built-in reference only) / CANDIDATE (one project) / AMBIGUOUS (several projects) / MISSING. IaC-only and
  library snapshots are listed as supporting evidence, never as alternatives.
* `CompositeSourceRules.Compatibility` per consecutive transition: **Source version**, **Message schema (XML)** (namespace family and exact
  version of the stage folders' XSDs; disjoint versions of one family = INCOMPATIBLE), **API contract** (receiver declares; Compatible only when the
  sender's code references it), **Messaging channel** (publisher ∩ consumer of the stage folders, declared ≠ observed), **Dataset contract**
  (SPECIFIED_BUT_NOT_RUNTIME_VERIFIED), **Correlation fields**, **Authentication** and **Environment** (NOT_ASSESSED — never inferred from
  source). Overall = worst assessed. A reviewer may *accept* a non-compatible transition with a reason: recorded beside the assessment, the state
  is never rewritten.

## 3. Readiness, generation, plan (G–K, N, P)

* Level A **Source coverage** (from the context: Blocked by an unaccepted incompatible transition, Partial when a mandatory stage has no source,
  Needs review when a stage is candidate/ambiguous). Snapshot analysis status is shown separately per source.
* `IntegrationSuiteGenerateRequest.CompositeContextId` (+ `AllowPartial`): generation refuses a non-ready context unless a partial draft is
  explicitly requested (then labelled `PartialDraft`, status Draft). `GenerateLockedAsync` now takes a list of sources and processes them one at a
  time (contracts → Contract Registry with project-scoped continuation; tests and Test Data through Contract Testing; XSDs; Kode 6/7 source facts);
  each contract reference carries snapshot, project and stages. Suite key = scope + owning project + options: a new context revision becomes the
  next suite revision with drift (a contract no longer provided is named in the drift).
* `CompositePlanner`: entry = first stage with a create operation; later stages are **observed** through a read operation whose path parameter is
  a documented response field of an earlier step (exact name, or the `id` convention); action-word stages (period lock) get their state-changing
  operation. Observers wait boundedly (≤ 5 reads × 2 s on 404/409/425, the execution engine's own limit) — GET only; data-changing calls are
  never re-sent. The plan becomes a **Draft** Contract Testing chain (`ContractChainStep.Observe`, additive) that a test lead approves
  (`POST {id}/plan/approve`).
* Level B **Stage execution** (a stage is Ready when its plan step's test has no blocking gap — review state is a limitation, the Target
  Environment is chosen per run, a chained input's own Test Data gap does not block); level C **Complete E2E** (A and B Ready, plan approved,
  every mandatory stage in the plan, no unaccepted incompatibility).
* Blockers carry stage, reason, missing prerequisite, evidence, action, owner (Tester / Test lead / Domain owner / Operator / Platform), severity
  (Blocking / Limiting) and review status; no action performs an external write.
* Coverage rows: complete E2E, stages, contract operations, Kode 6/7 scenarios — Generated → Configured → Executable → Executed → Verified with
  "why not verified".

## 4. Execution, asynchronous stages, correlation (L, M)

* `PreflightAsync` / `ExecuteCompositeAsync`: approved plan, a **prefix** of its stages (anything less than every mandatory stage, or a partial
  draft, is a SUBFLOW_TEST), an approved non-production Target Environment, explicit authorisation, confirmation of the listed data-changing
  operations, approved sources, no unaccepted incompatible transition inside the run. The run is `ContractTestService.RunChainAsync` (new
  `StepIds` subflow filter) → one Execution Evidence run with `StopOnFirstFailure`.
* `CompositeRunReader`: stage states WAITING / OBSERVING / SUCCEEDED / FAILED / TIMED_OUT ("not observed", never "failed") / UNKNOWN_OUTCOME (an
  unanswered data-changing call, never re-sent) / NOT_OBSERVABLE / BLOCKED / NOT_RUN; correlation per boundary from the recorded output and the
  resolved input of the next call — CONFIRMED when the downstream response echoes the same value, CARRIED when the record was found by it,
  WRONG_CORRELATION when the echo differs, MISSING / NOT_OBSERVED otherwise. Time coincidence is never used. Verdict FULL_E2E_VERIFIED only for a
  full-E2E run where every mandatory stage succeeded and every boundary is confirmed or carried; separate runs are never combined.
* Runs are linked in `integration_suite_runs` with the executed plan, kind and exact source versions (`PlanJson`), so a run reopens with stage
  meaning after a restart.

## 5. UX (O)

`/integration-suites?area=` **1 Integration & Sources** (stage strip; auto-discovery; one source per stage with matched names and artifacts;
ambiguous stages never preselected; approve per source; manual association with a reason; supporting evidence collapsed; compatibility with
accept-by-reason; newer-snapshot proposals; technical details with full SHA) · **2 Generate & Review** (composite draft, partial only by choice;
single-snapshot generation with a searchable list instead of the large dropdown; existing detail views) · **3 Resolve Readiness** (three levels,
stage executor/observer, blockers filtered by owner) · **4 Execute & Evidence** (plan with identifier mappings and approval, preflight, authorised
run, verdict, stage timeline, correlation, exact source versions, coverage, period lock and Bufdata notes).

## 6. Period lock, Bufdata, Kode 6/7

* Period lock: a named prerequisite (prerequisite, operation, owner, environment scope, expected result, downstream trigger, evidence). With
  real sources it is operator-only — BirkNext never locks a real period; only a synthetic local lock operation can be part of an approved plan.
* Bufdata: SPECIFIED_BUT_NOT_RUNTIME_VERIFIED unless a delivery read operation is in the plan; files are verified separately in Parquet Quality
  Review; no Azure Blob is read live.
* Kode 6/7: unchanged reuse of the M01 consumer and K67-01…12 (E2E-05, DATA-03); open questions appear as Domain-owner blockers
  (NEEDS_DOMAIN_DECISION); Kode 6 and Kode 7 synthetic children stay separate approved-Test-Data roles, never generated.

## 7. Tests

`CompositeSuiteTests` (9): discovery across four separate synthetic projects (exclusions, ambiguity, history), context revisions/decisions/conflict,
incompatible schema blocked until accepted, one continuous synthetic transaction verified against an independent ledger (loopback integration with
asynchronous hops), failure modes (subflow, failed handoff → TIMED_OUT, wrong correlation, unknown outcome sent once), missing observer/stage and
partial drafts, changed snapshot → next revision without duplicate contracts, endpoint policies, real-shaped MU/MM namespaces. Page tests:
`IntegrationSuitesCompositePageTests` (5) + the updated `IntegrationSuitesPageTests` (7).

## 8. Fixes from acceptance (same reports commit)

| Defect | Change |
|---|---|
| MS-D1 Stage execution Partial for a ready plan | `BlockingGaps` ignores the "Changes data" warning (the preflight confirms data-changing calls per run); an unbound authentication still blocks |
| MS-D2 Correlated identifier not visible | the run view renders the carried value (`its-correlation-value`) |
| MS-D3 Terraform-only archive missing | `CompositeSourceRules.Discover` lists a Failed snapshot with Infrastructure-as-Code evidence as supporting (status shown, never associable) |
| MS-D4 Stale context link → 409 on save | the URL follows the opened revision after load/save; an older revision says so and offers "Open revision N"; the page exposes `aria-busy` |
| MS-D5 v2 stage without its v1-bound test | the planner uses the newest positive test of the contract at or below the stage version (`IntegrationSuiteService.VersionOf`) |

`IntegrationSuiteService.ReadCompositeRunAsync` (static) now reopens a composite run from its link, so Test Execution & Evidence reuses the one
verdict owner instead of re-implementing it.
