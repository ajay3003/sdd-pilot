# BirkNext — persistent evidence, effective Delivery scope and end-to-end traceability

**Date:** 2026-11-14
**Branch:** `008-traceability-first`
**Base:** HEAD `97e116c5` (follows `birknext-canonical-identity-and-evidence-closure.md`)
**Commits:** local only, listed at the end

No push and no deployment. No remote environment was contacted: no TT02, m2lbdev, Altinn, MU, MM, Azure DevOps or production. No external browser, API, performance or E2E test was executed.

The following were not touched:
- `appsettings.Local.json`
- `AIAssisted/testdata/`
- the preserved databases `birknext_scopes_acc_20261113`, `birknext_gap_acc_20261114` and `birknext_final_acc_20261114`

Acceptance used new disposable clones: `birknext_persist_acc_20261114` while iterating, and `birknext_persist_final_20261114` for the final run.

## Gap closure

| # | Gap | Confirmed root cause | Fix (reused infrastructure) | Status |
|---|---|---|---|---|
| 1 | Task Alignment lost on reload | `TaskAlignmentSessionService` held the report in browser memory only | Immutable run store (own schema `task_alignment`, Scope-Registry pattern); page and Feature Readiness restore the latest compatible run | **Verified fixed** |
| 2 | Feature Readiness back to Not assessed | Same | `ImplementationGapExplorer` reads the persisted run and classifies it current / stale / none, including a run from an earlier import of the same project | **Verified fixed** |
| 3 | Delivery scope did not filter | The selection was recorded as provenance only, and the panel narrowed the view only | Explicit **Scope context** (default, unchanged behaviour) vs **Filter by scope**. Membership is resolved on the server from approved revisions (`ScopeRegistryRules.Select`) at freeze or generation time, with pinned revision ids, and both numerators and denominators are filtered | **Verified fixed** |
| 4 | Journey and Test Data not verified end to end | No way to declare a project Integration Journey; Test Coverage ignored Test Data's own consumer references | A declared-journey store and form; Test Coverage merges Test Data bindings (asset id, bound revision, usage state) | **Verified fixed** |
| 5 | Critical E2E gate used legacy semantics | `CriticalE2EReleaseRules` was criticality-only | Versioned gate `critical-e2e-gate/2` that uses test type; `/1` kept for builds evaluated before the change | **Verified fixed** |
| 6 | No end-to-end report-generation test | The in-memory fixture lacked report-source rows | `DeliveryScopeFilterDatabaseTests`: real Postgres (TestPostgres), real ZIP import, generate, persist, reload, metrics, provenance and immutability | **Verified fixed** (automated) |
| 7 | No report-vs-report comparison | Not implemented | `DeliveryReportComparer` and `GET …/reports/compare`; History view at `?tab=history&compare=a,b` | **Verified fixed** |
| 8 | Unscoped M2LB requirement ids | A source-only interpretation names `Requirement\|FR-x` without a module or feature | Relationship Review identity panel: Delivery-snapshot candidates, ambiguous with no default, confirm or reject with rationale, append-only history. Confirmed ids resolve in the scope catalog and member checks | **Verified fixed** (the workflow); the M2LB data stays unresolved until a person confirms (see limitations) |

### Defects found during browser acceptance and fixed
- **Task Alignment lineage:** the project name was read from `ICurrentWorkspaceProjection.Current` before the projection had computed, so runs were stored without it. It now awaits `GetAsync()`.
- **Delivery scope tab:** `?scope=` carries both the Functional Quality Scope key and the shared-scope GUIDs. A GUID was read as a missing Functional Quality Scope ("The scope … is not available"). GUIDs are now never treated as Functional Quality Scope keys.
- **New records without a selection:** reports and snapshots made after scope support existed, but without a selection, said "created before scope support". The server now stamps "No shared scope selected — whole Delivery population". Only genuinely old records say "not recorded".
- **Wording:** "1 feature contain in-scope items" now reads "1 feature contains in-scope items".
- **Relationship Review identity panel:** it widened the page at 1280 px and below, because a `<fieldset>` sizes to its min-content and long canonical keys did not wrap. Fixed in CSS.
- **Report header and export appendix:** they still printed the "not recorded" text for new reports without a selection. They now use the same description: "No shared scope selected — whole Delivery population".
- **Navigation:** the new `/integration-journeys/declare` page is classified as a contextual page, opened from Test Coverage; the navigation consistency test flagged it.

## Task Alignment persistence

**Table `task_alignment.run`** (insert-only; migration `20261114120000_InitialTaskAlignmentRuns` creates a new schema and changes no existing table):

| Group | Columns |
|---|---|
| Identity | `id`, `project_key`, `project_name`, `module`, `feature_key` |
| Documents | `tasks_path`, `tasks_hash`, `spec_path`, `spec_hash`, `source_snapshot_id` |
| Provenance | `rules_version`, `design_inputs_key`, `record_version` (`task-alignment-run/1`), `completeness`, `analyzed_at` (server time) |
| Counts | `total_tasks`, `finding_count`, `counts_json` |
| Results | `tasks_json` (per-task results) |

- Document bodies and task lines are not stored.
- Failed, refused or partial runs are rejected (400) and nothing is written.

**Compatibility:**

| State | When |
|---|---|
| Current | Same project key, same full path, same Tasks fingerprint and rules version |
| Stale | Same project and path, but the fingerprint or rules changed. Shown as "Stale — inputs changed since …" and never counted |
| Earlier import | When the current import has no run of its own: the latest run with the same project name and full path from an earlier import. Stale unless the Tasks fingerprint is identical; labelled "analyzed in an earlier import of this project" |
| Historical | Older runs, listed only as history |
| No result | Another project, or another Tasks document with the same file name |
| Incomplete | Stored task results do not match their count; never used |

**Feature Readiness:**
- Task Alignment remains traceability. The text says so, and gate precedence is unchanged; the DPO gate still blocks T004 in the fixture.
- Task Alignment has no human-review model, so none was invented.

## Delivery scope

| Mode | What it does |
|---|---|
| Scope context (default) | Records the selected approved revisions; items and metrics stay the whole population |
| Filter by scope | Keeps only items whose canonical key (type, module, feature, id) is a confirmed member of the pinned approved revisions. Acceptance criteria and test cases follow their parent. Records included, excluded, unresolved and not-applicable counts |

- Existing snapshots and reports are never mutated; a membership change needs a new freeze or report.
- Delivery records persist the provenance additively in JSON (`DeliveryScopeProvenance.Mode`, `Filtered`, `Counts`, `FilterBasis`); there is no schema change.
- Task Alignment's run store is a new, additive schema; nothing existing is altered.

**Measured on the TaFixture Payments Functional Quality Scope (6 items):**

| Selection | Included | Excluded |
|---|---|---|
| Whole population | 6 | 0 |
| Scope "Payments FR-001+002" | 2 | 4 |
| Both overlapping scopes (FR-002 shared) | 3 | 3 |

The FR-002 member is counted once. The Billing population with a Payments scope says "Scope has no applicable items in this Delivery population" instead of 0%.

**Report calculations:**
- **Filtered at item level:** requirements, stories, scenarios, story and criterion changes, and tasks linked to in-scope stories (numerator and denominator alike).
- **Filtered at feature level:** labelled "features containing in-scope items".
- **Not scope-resolvable:** tasks completed, added, removed or reopened; shared and foundational tasks; unresolved references; report-level attention. These are listed under "Not scope-resolvable" and never shown as whole-project numbers.
- **Real report:** "2 included · 8 excluded".

## Report-vs-report comparison

- Like-for-like requires all of these to match:
  - project
  - report kind
  - calculation and content version
  - interpretation and quality rule sets
  - Source/Functional scope
  - scope mode
  - for filtered reports, the same scope revisions
- Otherwise the mismatch is named and only raw counts are shown, without percentages.
- Browser checks:
  - two filtered reports on the same revision: like-for-like;
  - whole vs filtered: "Not like-for-like: raw counts only, no percentages. Different scope modes …", 0 percentages;
  - Back returns to the previous comparison.

## Journey identity

**Fixture:** `journey-fixture.zip`, plus three journeys declared through `/integration-journeys/declare`.

| Declared journey | Candidate | Decision |
|---|---|---|
| Order notification | **Equal** (exact chain) | Confirmed with rationale |
| Order audit trail | **Contains** | Rejected |
| Order archive | None (chain differs) | — |

- Decisions are stored in `ManualReviewDecisionStore` (module `journey-association`) and persist across a reload.
- Two overlapping scopes naming the journey give "1" in scope, counted once. The rejected association shows `data-reason=Rejected` and is excluded.

## Test Data

- **Asset:** a JSON asset is created and approved; its immutable id (`tda_…`) is read from the API, never hard-coded.
- **Binding:**
  1. Test Coverage → "Request or link Test Data" (JSON payload).
  2. Test Data offers a "Possible match", which is confirmed, then bound.
- **Readiness states:** PlaceOrder **Linked** (asset id, "revision cd406c19…", Resolved), CancelOrder **RequiredButMissing**, ListOrders **NotAssessed**.
- **Overlap:** two scopes sharing the asset give "1" in scope; identity is unchanged after a reload.
- **Deprecating the asset:** PlaceOrder becomes **LinkNeedsReview** ("Unresolved — The asset is deprecated."), and the scope count falls to 0.

## Critical E2E release gate

**Policy `critical-e2e-gate/2`:**

| Flow | Result |
|---|---|
| UI smoke | Never counts toward Business E2E |
| UI functional | Functional coverage only |
| Integration/API | Integration coverage only |
| Business E2E | Counts only when Critical, release-ready and passed on the selected build; otherwise "no credit" |
| Legacy (Unknown) | "Legacy classification requires review" (new disposition ReviewRequired, never Ready) |

- Criticality never overrides test type.
- A failing required flow of any type still blocks (product decision, kept conservative).

**Historical compatibility:**
- Verdicts are recomputed from stored runs, never stored.
- A build whose runs all lack a recorded policy version is evaluated with policy `/1`, the exact legacy branch.
- New runs record `GatePolicyVersion`.
- Stored flows are never reclassified.

**Browser:**
- Eligibility: smoke `never`, functional `functional-only`, business `not-ready` (no credit), legacy `legacy-review`.
- After a person classifies the legacy flow as Business E2E in the editor, it moves to `not-ready`.
- No run was started.

## Requirement identity (Relationship Review)

The ta-identity fixture (Payments and Billing both define FR-001…003, and a Payments test names FR-002) was imported as a project and as two Delivery snapshots.

1. Analyze relationships showed the panel with unscoped ids.
2. Analyzing with the Delivery snapshot made FR-001, FR-002 and FR-003 **ambiguous**, with no default target.
3. FR-002 was confirmed as the Payments requirement with a rationale. The confirmation persists across a reload.
4. The scope catalog now offers only scoped FR-002 keys and no longer offers `Requirement|FR-002`.

The reviewer recorded is the authenticated identity ("local-user"), the existing Relationship Review rule.

## Verification on the final code

Every test result below is from the final code. Earlier iterations are not counted.

- **Builds:**
  - Backend and frontend test projects: 0 errors.
  - Release publish of the API and web app: succeeded.
- **Backend focused** (Task Alignment, Delivery, Functional Quality Scope): 194 passed, 1 skipped, 0 failed.
- **Full frontend regression:** 8935 passed, 1 failed of 8936.
  - The failure was `ActivityCenterTests.Cancel_AffectsOnlyThatOperation_…` (a bUnit wait timeout under load). It is unrelated to this task and passed 6/6 in three isolated reruns.
- **Backend partitions** (foreground, sequential, same build):

  | Partition | Passed | Skipped | Failed |
  |---|---|---|---|
  | 1 | 1052 | 2 | 0 |
  | 2 | 1099 | 5 | 0 |
  | 3 | 881 | 0 | 0 |
  | 4 | 663 | 1 | 0 |
  | 5 | 531 | 3 | 0 |
  | 6 | 2500 | 8 | 1 |
  | **Total** | **6726** | **19** | **1** |

  - The partition 6 failure is the load-sensitive timing test `ScenariosQueryTests.…WarmedP95CompletesWithinTwoSeconds`: p95 3106 ms, against a 2000 ms limit, with a 4438 ms outlier.
  - It does not touch code changed here. It passed 5/5 in two isolated reruns.
  - On the previous build of this task (identical except for the export appendix text), partition 6 passed fully: 2501 passed, 8 skipped, 0 failed.
  - It also failed on load once earlier in the task, so it is recorded as a failure in the full run, not as a pass.
  - First attempt: partition 6 was stopped by the memory guard and rerun after memory recovered.
- **Browser acceptance (Edge, final Release build):** **91/91**.
  - Setup: API :5097 on the fresh disposable `birknext_persist_final3_20261114`; web :5197.
  - Coverage: workflows A–G, no horizontal overflow on 9 pages at 1440, 1280, 1024 and 768 px, and browser Back/Forward.
  - No page errors, no 5xx responses, no external request attempted, no Critical E2E run started.
  - One console error was logged by the app: "Error auto-saving workspace … Failed to fetch". The script uses full page navigations, so it is most likely an auto-save request cut off by navigation. It is pre-existing behaviour and was not investigated further.
  - Screenshots: session scratchpad `acc/shots-persist/` (31 images).
- **Preserved databases:** `birknext_scopes_acc_20261113`, `birknext_gap_acc_20261114` and `birknext_final_acc_20261114` exist and contain no `task_alignment` schema, so the new migration never ran against them.

## Remaining limitations (honest)

- **Unscoped ids are per canonical scope (Target Environment).** Acceptance analysed TaFixture inside the M2LB environment, so FR-002 was also "named by" an M2LB test. Confirming an identity applies to the id in that environment. Projects that share an environment share unscoped ids.
- **M2LB itself:** its unscoped requirements stay unresolved until a person analyzes with an M2LB Delivery snapshot and confirms each candidate. Nothing is auto-confirmed.
- **Release Readiness:** the Critical E2E dimension is still hard-coded as Not assessed and does not read the new gate.
- **Feature-level Delivery metrics** in a filtered report count features that contain in-scope items, not items; they are labelled so. Coverage and test-evidence metrics do not exist in the weekly report, so there was nothing to filter.
- **Task Alignment:** `source_snapshot_id` is not filled from the browser. Task Alignment has no human review, so no review history exists.
- **Declared journeys** are stored in a workstation-local JSON file (`App_Data/integration-journeys/declared-journeys.json`), not in the database. The declare page has no sidebar entry; it is opened from Test Coverage.
- **Local Data Reset** does not include the `task_alignment` schema, as with the existing Scope Registry schema.
