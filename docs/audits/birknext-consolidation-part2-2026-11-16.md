# BirkNext — Consolidation part 2: decisions, Manual Testing traceability, multi-tab safety (2026-11-16)

Branch `008-traceability-first`, on top of `ac201402`. Continues `birknext-consolidation-implementation-2026-11-16.md` (whose §1 assessment of R1–R13 against the code is not repeated here).

## 1. Product decisions (Priority 1)

### R1 — one authoritative requirement population

**Decision:** the population for every requirement-coverage number is the requirements of the project's **selected specification** (the focused module's selected specification in a monorepo), read exactly as Test Coverage reads them (`TestCoveragePresentation.WorkspaceEvidence`), with identifiers in the shared grammar. Three different **measures** are taken over that one population and are never presented as each other:

| Measure | Question it answers | Where |
|---|---|---|
| Specification coverage | Does the specification itself state acceptance scenarios for the requirement? | Specification Explorer |
| Test coverage | Which prepared / executed tests are linked to the requirement? | Test Coverage, Requirements Traceability |
| Relationship-analysis coverage | Does a reviewed scenario association from the last relationship analysis cover it? | Manual Testing / Test Management focus card |

**Implemented:** `RequirementPopulation` (one definition). The focus card now counts reviewed coverage **over the selected specification** ("of N in the selected specification"), reports requirements the last analysis does not reach ("Requirements not in the last relationship analysis"), and never counts analysed requirements outside the selection. Without a selected specification it keeps the analysis's own count and says so ("of N in the last relationship analysis"). Test Plan Suggestions shows associations per scenario (no competing count); the legacy Traceability page (off by default) keeps its "Of N requirements in the interpretation of <date>" wording.

### R3 — one authoritative implementation verdict

**Decision:** the implementation verdict is the **feature readiness of the stored Implementation Gap evaluation** (backend `GapReadiness` over the imported source snapshot). It is what Quality Review shows and what Implementation Review shows first.

**Implemented:** Implementation Review's per-requirement table is now "Supporting evidence per requirement"; its last column is "Evidence state" (it was "Assessment"), and a note names the one verdict: "The implementation verdict is the feature readiness above … the same verdict Quality Review shows. This table lists the evidence behind it per requirement; it is not a second verdict." The evidence texts themselves never claimed "implemented" (they say "Implementation evidence linked", "not assessed", …) and are unchanged.

### R7 — project ↔ environment resolution

**Decision:** project identity (`import:{importId}`) and Target Environment identity (profile id) are **never merged**. A project is related to an environment only through the environment's own binding (Target Environments › Project, `EnvironmentProject.ProjectImportId`). `ProjectEnvironmentResolution` gives five states: **Bound** (use it), **Unbound** (use it, labelled "not confirmed to belong to this project" — the documented fallback), **Other project** (environment results are not shown for this project; the environments bound to this project are named), **No environment**, **No project**.

**Implemented** where one screen mixes the two scopes: the Manual Testing / Test Management focus card (plans by project, reviewed coverage by environment) and Integration Testing (journeys and suites by project; topology, catalog and IQR by environment). Purely environment-scoped pages (Release Readiness, Integration Topology, IQR, FQR/AQR) show only environment facts and are unchanged.

## 2. Manual Testing workflow (Priority 2)

A read-only gap analysis traced the workflow step by step (file:line evidence; the claims marked ✔ were re-checked in code for this change):

| Step | Works? | Gap found | This change |
|---|---|---|---|
| Discover | Yes (read-only; discovery runs in Plan & prepare) | Requirement rows do not link to their cases | — |
| Prepare (saved cases, revisions) | Yes | Only the latest plan revision is kept unless a snapshot was taken; a refresh replaces generated evidence | — (documented) |
| Prerequisites | Partly | Only Preconditions are pinned on a run ✔ (`ManualTestRunService.cs` start) | — |
| Test Data | Yes | Run shows "alias (asset id)" | — |
| Execute | Yes | — | — |
| Evidence | Partly | References only (no upload); not addable after completion | — |
| Defects | Partly | Local only; no list across runs | — |
| **Retest** | Weak | A new run had no link to the failed run it retests ✔; Execute's "Last run" never said it ran an older version of the case ✔ | **Fixed**: `RetestOf` pinned at start; "Retest of an earlier failed or blocked run — open that run"; "Case changed since — run again" next to a stale last run |
| **Spec → execution traceability (R11 as asked)** | Partly | Saved cases keep their scenario (`Source.ScenarioId`, fingerprint) and spec references (inside their evidence); runs pinned requirement ids but **not** the specification file, acceptance scenario or source ✔ | **Fixed**: every new run pins `Origin` — source kind, scenario fingerprint, specification file, acceptance scenario, and each requirement's file and basis (specification evidence vs a tester's manual link) — shown on the run |
| Traceability / Coverage | Yes (shared `PreparedCaseTraceability`) | Execution text has no link to the run | — |
| **Reporting** | **Missing** ✔ | No way to hand run results to anyone | **Fixed**: "Download run results (CSV)" on Execute — every run of the project (all pages, not only the rows on screen), tester words, UTF-8 with BOM for Excel, spreadsheet-formula cells neutralised |
| Azure DevOps export | Yes (offline file) | Import file carries no scenario/spec/requirement link; ids re-typed per update export | — (documented) |

Compatibility: `Origin` and `RetestOf` are optional fields of the run JSON; runs stored before this change read unchanged (test). The case fingerprint moved to shared code for the web app and is byte-identical to the one existing runs were pinned with (test), so no existing run becomes "stale" by the move.

**R11 as originally written** (Integration Test Suites recording the declared journey id and revision) remains **deferred**: it needs an additive schema change in the suite owner and a composer change.

Remaining Manual Testing gaps, by tester impact: Azure DevOps round-trip (ids typed per export, no requirement links in the import file); defects frozen with their run and no cross-run defect list; evidence by reference only; earlier plan revisions not retrievable without a snapshot; Environment/BlockedBy prerequisites not pinned or enforced.

## 3. Technical debt (Priority 3)

### R2 — legacy traceability store (`AppDbContext.Scenarios` / `TraceLinks`)

Direct users (re-checked): `ScenarioService`, `TraceLinkService` (its API), `TraceabilitySuggestionService`, `ImpactAnalysisService` / `ImpactAnalysisRunService`, `AIChangeAuditService`, `CodeTraceabilityService`, `SpecDriftDetectionService` (GraphQL drift report feeding the AI auditor — not the Documentation Drift page), and **`SourceChangeImpactService`** (change impact) — wider than the audit listed.

**Proposed migration (not executed):**
1. Add a read adapter `ILegacyTraceabilityReader` over canonical relations (Relationship Review) returning the shapes these services read (requirement, test, covers-link).
2. Move readers one at a time behind a per-service switch, comparing results on real projects (difference report) before switching.
3. Keep stored Impact Analysis / AI Change Review runs readable (they store their own results; nothing rewritten).
4. Stop writes to `Scenarios`/`TraceLinks` only after every reader moved; keep the tables (read-only) for one release; drop later with an explicit migration.

**Compatibility risks:** change impact (`SourceChangeImpactService`) is user-visible; canonical relations are scoped by Target Environment (R7) while the legacy store is scoped by `ProjectId`; requirement ids must use the shared grammar on both sides. Rollback: the per-service switch.

### R9 — package import writing other owners' tables

`WorkspacePackageService.Apply.cs` adds rows directly to `IqrSourceSnapshots`, `SourceSchemaArtifacts`, `SourceContractDocuments`, `ParquetRuleSets`, `IntegrationSuites`, `CompositeSourceContexts`, `SourceRepositoryAssociations`, `SourceDecisions`, `IntegrationPlatforms`, `IntegrationDefinitions` (lines 208–297, re-checked).

**Proposed migration (not executed):** give each owner an `ImportAsync(record, provenance)` that applies its own rules (e.g. `SourceBaselineService` monotonic decision time and append-only decisions; integration definitions imported disabled), then switch the importer per table, oldest-risk first (source decisions, integration definitions). Existing imported rows stay as they are (no rewrite); a round-trip test (export → import → export) per owner guards each switch. Risk: medium–high (many owners); no schema change is needed for the first step.

### System Settings multi-tab overwrite — implemented

Target Environments are one browser-storage entry shared by every tab, and a save wrote the whole entry, so a second tab silently overwrote what the first had saved. Now a save re-reads storage and, when another tab saved since this one read it, merges **per environment**: changes to different environments are both kept, additions and deletions both take effect, and the **same environment changed in both tabs is refused** — nothing is written, the editor says which environment changed in another tab, and "Reload settings" shows the stored version. A local data reset is an explicit replacement and is never merged.

## 4. Quality (Priority 4) — ManagedEdgeConnectorTests

**Root cause (test infrastructure):** the TRX of the failing run shows that during the 88 s failure window 15 unrelated tests that normally take seconds took 79–160 s each, so the test host was congested — not the CPU (all 24 cores saturated by separate processes did not reproduce it: 3 of 3 passed in ~1 s) and not product behavior (100 / 100 isolated). With `xUnit.MaxParallelThreads=2` xUnit runs the continuations of **every** in-flight test on a two-thread synchronization context. The test's canned loopback server was started on that context, so its accept/read continuations queued behind dozens of database tests, while the connector's `HttpClient` (which does not capture the context) kept its 60 s timeout clock and timed out. **Fix:** the canned server runs on the thread pool (`Task.Run`); the connector, the endpoint validation and the assertions are unchanged. An intermittent failure cannot be proven gone by one passing run; the evidence and the mechanism are recorded so a recurrence can be checked against them.

### Two further failures found by this regression (investigated, fixed)

- **`ResettableSchemaResetTests` (4 failures, "Blocked" instead of "Completed").** Local data reset holds a **process-wide** gate (one reset at a time — correct product behaviour). `ResettableSchemaResetTests` and `DataResetScopeTests` run in the same test process and, under xUnit's class parallelism, a reset in one class saw the other class's reset in progress. Test isolation, not product: the reset test classes (incl. `LocalDataResetTests`) now share one non-parallel collection; assertions unchanged.
- **`TestDataXmlApprovalTrustTests` (1 failure, "Access to the path is denied" in `TestDataIdentityStore.Write`).** Each test uses its own temporary folder and the store serialises its writes, so neither a shared path nor a same-process race explains it; it passed 30 / 30 isolated. On Windows the atomic replace (`File.Move(tmp, path, overwrite: true)`) is refused while another process holds the target without delete sharing — typically antivirus scanning a file just written. The real product writes this file the same way, so the store now retries the replace briefly (5 attempts, 40–160 ms apart) and still fails loudly when the hold lasts. Tests: a 60 ms hold succeeds; a lasting hold throws.

## 5. R1–R13 status matrix

| ID | Status | Evidence | Remaining risk |
|---|---|---|---|
| R1 | **Completed** | Decision §1; `RequirementPopulation`; focus card counts over the selected specification; `RequirementPopulationTests` | The legacy Traceability page (off by default) keeps the analysis's own population (labelled) |
| R2 | **Deferred** | §3 proposal; readers re-checked (incl. `SourceChangeImpactService`) | Second store remains for change impact and off-by-default modules |
| R3 | **Completed** | Decision §1; Implementation Review supporting-evidence table; `ImplementationReviewLifecycleTests` | — |
| R4 | **Deferred** | Unreferenced views/builders re-checked | Dead code with its own grammar (no user impact) |
| R5 | **Completed** | `ac201402`; `TaskReferenceGrammarTests` | — |
| R6 | **Deferred** | Internal names only | None visible |
| R7 | **Completed** | Decision §1; `ProjectEnvironmentResolution`; focus card and Integration Testing; `ProjectEnvironmentResolutionTests` | Environment-only pages do not show a binding notice (they show only environment facts) |
| R8 | **Not applicable** (disproved in `ac201402`) — residual multi-tab risk **Completed** | §3; `TargetEnvironmentMultiTabSaveTests`; browser two-tab check | Two tabs of *different browsers* do not share storage (each its own settings) |
| R9 | **Deferred** | §3 proposal; tables re-checked | Imported rows bypass owner rules |
| R10 | **Deferred** | — | Repeated diagnostic run |
| R11 | **Completed for manual testing; Deferred for integration suites** | Runs pin `Origin`/`RetestOf`; `ManualRunOriginTests`, retest test; integration-suite journey binding unchanged | Azure DevOps import file carries no spec references; refresh replaces generated evidence |
| R12 | **Completed** | `ac201402`; `cx-accept` | M2LB template has no catalog integrations (browser checks the honest empty case) |
| R13 | **Completed** | `ac201402`; measured 13,111 → 896 ms all-engine warm median | First probe after start still ~14 s (a real container check) |

## 6. Results

Final tree (this commit). Release publish from a clean copy; fresh disposable databases from template `birknext_pts_acc_20261115` — `birknext_p2_20261116` (main sequence), `birknext_p2link_20261116` (requirement linking), `birknext_p2ado_20261116` (Azure DevOps export), each journey in the order it was written for; isolated ports 5097/5197; nothing heavy run concurrently except where noted.

| Check | Result |
|---|---|
| Frontend build (non-incremental) / suite | 0 errors / **9,171 / 9,171 passed** |
| Backend build (in-repo `bin/Verify/net8.0`) / suite, partitions 1–6 on the final tree | 0 errors / 1,052 + 1,101 + 888 + 682 + 541 + 2,599 = **6,863 passed, 19 skipped, 0 failed** |
| Earlier run of the same product code with the previous test code | 3 + 1 failures → investigated (§4: reset gate isolation, Test Data replace retry), fixed, re-run above |
| Release publish (API + Web) / web asset integrity | exit 0, 0 errors / unchanged from baseline (`assets=87 problems=1`) |
| Browser: this change (`p2-accept`) | Run results download — every run of the project as CSV with the report columns; a run shows "Prepared from LendoPlus/specs/001-library-loans/spec.md, acceptance scenario scenario-1."; two tabs editing the same Target Environment — second save refused with the plain message, the first tab's value stays stored, "Reload settings" clears it: **9 / 9** (report and origin R1–R5 in the sequence; the sequence's two-tab step stopped with a script error — the two-tab step missed opening the "Target application" tab in the script; corrected and run on the same build: 4 / 4) |
| Browser: Manual Testing journey (`utps`, `mt-accept` a / b / c with an API restart) | **106 / 106**, **23 / 23**, **48 / 48**, **7 / 7** |
| Browser: requirement linking (`link-accept` link / verify, restart between; own fresh database) | **12 / 12**, **26 / 26** |
| Browser: Azure DevOps export (`ado-accept`, own fresh database) | **38 / 38** |
| Browser: Integration Testing / topology focus (`itx`, `cx`) | **42 / 42**, **6 / 6** |
| Browser: Technical Testing / stage navigation (`tt`, `ttnav`) | **76 / 76**, **48 / 48** |
| Browser: navigation and workspaces (`nav`, `nav2`), branding (`brand`) | **41 / 41**, **32 / 32**, **17 / 17** |
| Page errors / 5xx / guard (non-allow-listed network) | none |
| Console errors | 1 (`nav-accept`): Frontend Quality Review's engine-status request cut off by the script leaving the page after 2.5 s — on a freshly started API the first Passive Web Security probe is a real container check (~14 s, R13); repeated status requests are answered from the reused probe |


## 7. Git

One local commit on `008-traceability-first` (code, tests and this report); not pushed. Unrelated working-tree changes (`appsettings.Local.json`, `AIAssisted/testdata/`, the Kode67 gap register, other audit drafts) are left untouched and uncommitted.
