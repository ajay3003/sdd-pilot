# BirkNext Hardening Pass 2 (2026-11-05)

Branch `008-traceability-first`, not pushed. NEXT42. It builds on Hardening Pass 1 (NEXT41, `813f94a1`), whose fixes were re-verified here, not redone.

> **Finding IDs.** The brief's ~160 screenshot findings were not included in the paste. This report groups what was reproduced on the M2LB copy by shared root cause (RC-n) and module. Every observation is labelled reproduced, data-dependent or not audited.

## A. Executive summary

| | |
|---|---|
| **Audited** | All 10 modules, opened on the M2LB copy (DB `birknext_gaps`, environment `ci-live`, Target Application M2LB) with a read-only probe; then code-traced |
| **Confirmed and fixed** | 5 shared root causes (RC-1 … RC-5) and 2 module defects (Pipeline structure wording; Generated Documentation freshness denominator) |
| **Clarified (expected)** | Dependency version difference; Generated Documentation disclaimers; Dependency Review readiness ≠ health; Pipeline source-only status |
| **Open** | Glossary/term reconciliation, contract count reconciliation, Requirements Traceability transition denominators on real selected artifacts, Dependency Review stored-result provenance, Pipeline gating classification depth, Generated Documentation Explorer Mermaid escaping (see H) |
| **Blocked** | None |

## B. Root-cause matrix

| RC | Cause | Modules affected | Shared service | Fix | Test |
|---|---|---|---|---|---|
| RC-1 | **Snapshot pickers showed the code analyzer's status, not the analysis outcome.** Terraform packages read "No production project found" (Source Analysis) or "Failed" (Pipeline Review) while the Snapshot Registry, Import and Relationship Review say Partial | Source Analysis, Pipeline Review (+ every `IqrSourceSnapshotSummary` consumer) | `SourceSetupOutcome.Overall` (shared); Snapshot Registry status | `IqrSourceSnapshotSummary.Outcome`: the registry fills it from its stored status, the store path from the shared rule. Pipeline sources report the outcome. Pickers label from it; older backends keep the analyzer's words | `HardeningPass2Tests`; `PipelineEvidenceFreshnessTests`; `SnapshotRegistryTests` (summary carries an outcome) |
| RC-2 | **"Latest snapshot wins" as the default source.** Pipeline Review opened on M2LB.Common (imported last), not the Target Application's own source; NEXT41 fixed the same cause in coverage | Pipeline Review (Technology Coverage, Workflow, Dashboard: fixed in NEXT41) | Project Evidence main package | Pipeline Review prefers Project Evidence's main package when it has pipelines, then the newest with pipelines. Technology Coverage's scope sentence now says which rule applies | Browser 7a |
| RC-3 | **Culture-formatted CSS numbers.** `width:{pct}%` rendered "11,1%" in a Norwegian browser: invalid CSS, so the fill took the full width (§F "11.1 % renders 100 %") | Requirements Traceability, the shared `HealthBar` and `ProgressBar` (used across modules), Schema relationship graph | New `CssUnits` (invariant, clamped) | All fractional style values go through `CssUnits`; `ProgressBar`'s `aria-valuenow` too | `CssUnitsTests` (nb-NO); browser W (no comma widths on any visited page with `locale: nb-NO`) |
| RC-4 | **The Project Evidence count differed by page.** Source Analysis said "0 of 2 required evidence roles" while Workflow said "1 of 2": the strip counted the workspace's imported packages, and Source Analysis never reads the current workspace | Source Analysis (and any page with the strip that does not read the workspace) | `ICurrentWorkspaceProjection`, `IProjectEvidenceService` | The context strip reads the shared, cached workspace projection once. It also resolves Project Evidence again after another page invalidates the cache, with a loop guard when classification is unavailable | `ProjectContextStripTests` (5); browser 1b |
| RC-5 | **Freshness categories did not add up.** The Health summary dropped `NotApplicable` modules, and Autodoc freshness was counted over modules with generated output while the headline was all detected modules (§I "16 ≠ 1 + 13") | Generated Documentation Health | `GeneratedDocumentationDiagnosticService` | `FreshnessNotApplicable` in both summaries; `ModulesWithGeneratedOutput` names the Autodoc denominator ("covers the 14 modules with generated output (of 16 detected)") | `AutodocDiagnosticsSectionTests`, `GeneratedDocumentationHealthPanelTests`; browser 4a/4b |

**Module defects:**

- **Pipeline Review structure (§L):** "Stages / jobs 0 / 14" had no units, and a 14-job pipeline was described as "Single job (no stages)". Now: "14 jobs without explicit stages" / "3 stages · 5 jobs" (`PipelineReviewPresentation.Structure`). Tested by `HardeningPass2Tests` (theory) and browser 7c.

## C. Modules

| Module | Result |
|---|---|
| Project Import | NEXT41 fixes re-verified (statuses equal the registry; strip). No new defect |
| Recommended Workflow | NEXT41 re-verified: no source-binding demand without a module; same Project Evidence count as other pages (browser 8) |
| Project Quality Dashboard | NEXT41 re-verified: rename, Review readiness, "Source: partially analyzed" (browser 9) |
| Requirements Traceability | RC-3 fixed. Without selected artifacts the page asks for a choice per role and never picks one; the canonical layer reads its counts from the interpretation (no Delivery snapshot on the copy, so requirement counts are 0, not "Not analyzed": the analysis exists). Transition denominators on selected artifacts were not exercised (needs artifact selection; see H) |
| Source Analysis | RC-1, RC-4 fixed. Coverage on the copy: 1680 files, 16 in languages without an analyzer, 35 of 58 contracts read heuristically ("approach, not a defect"), 296 analyzer limitations ("not application defects") — limitations are already kept apart from findings |
| Generated Documentation Explorer | Disclaimer present ("not source truth, runtime truth, an approved specification or implementation verification"); modules and document types listed. Mermaid/escaping not audited |
| Generated Documentation Health | RC-5 fixed. On the copy: 91 documents, 15 modules with generated docs, freshness 0 fresh / 2 potentially stale / 13 unknown; Autodoc 16 detected, 8 API-capable, 14 with output, 4 required docs missing |
| Technology & Analysis Coverage | Scope sentence corrected (one snapshot — the Target Application's main package, M2LB.zip — never a project-wide sum). Applicability ≠ readiness ≠ coverage already separated in the table |
| Dependency Review | Four evidence modes and "readiness is not a dependency-health result" present; version difference is worded "a version difference, not an incompatibility" — clarified. No run (a run stores a result) |
| Pipeline Review | RC-1, RC-2, structure wording fixed. "Source evidence only… does not prove that a pipeline ran", findings as "source-based conditions … not pipeline failures" — already correct |

## D. Data and evidence integrity

- No import, re-analysis, association change, snapshot mutation or frozen-report change.
- The browser run aborted every API write except computations that persist nothing:
  - Functional Quality Scope resolve;
  - workflow build-steps;
  - the Generated Documentation diagnostic run, which returns a computed run and saves nothing.
- Z3: no write was attempted.
- The new contract fields (`IqrSourceSnapshotSummary.Outcome`, `ModulesWithGeneratedOutput`, `FreshnessNotApplicable`) are optional; stored JSON from older versions still reads.

## E. Shared foundations

**Reused:**

- Snapshot Registry status (outcome), `SourceSetupOutcome.Overall`
- Project Evidence main package
- `ICurrentWorkspaceProjection`
- `IProjectEvidenceService`
- the existing presentation helpers

**Added:** `CssUnits`, the one formatter for computed CSS numbers.

**Not added:** a new engine, registry or vocabulary.

## F. Commands and results

| Run | Result |
|---|---|
| Full frontend suite | **8182 passed, 0 failed** |
| Full backend suite | **5908 passed, 13 skipped, 0 failed** |
| New tests | `CssUnitsTests` (2), `HardeningPass2Tests` (6), `ProjectContextStripTests` (+3 → 5), Autodoc freshness denominator (1) |
| Extended tests | Pipeline sources outcome, registry summaries outcome, Health card counts |

All runs used the scratch worktree.

## G. Browser (`scratchpad/h42-e2e.cjs`)

- **Setup:** Edge with `locale: nb-NO`, M2LB copy, environment `ci-live`.
- **Result: 18/18.**
- **Earlier runs:** 17/18 twice, both on check 1b (Source Analysis "0 of 2" vs "1 of 2"). The first fix (re-resolve after invalidation) did not cure it. A read-only debug run showed the count stable per page, which located RC-4's real cause (workspace not read). After that fix, 1b passed.

| # | Workflow | Evidence |
|---|---|---|
| 1 | Import → Source Analysis | 1a every Terraform option reads "· Partial ·" (e.g. "Terraform-iac-meldingsutveksler · 2026-11-03 09:16 UTC · Partial · 69 files"); 1b "1 of 2 required evidence roles ready" on both |
| 2 | Requirements Traceability | Opens; no culture-formatted width |
| 3–4 | Generated Documentation Explorer → Health | Disclaimer; Health cards incl. "Freshness not applicable"; "Freshness covers the 14 modules with generated output (of 16 detected)" and the categories add up |
| 5 | Technology Coverage | "Scope: one source snapshot (M2LB.zip) — the Target Application's main package …" |
| 6 | Dependency Review | Four modes; readiness ≠ health |
| 7 | Pipeline Review → Flow | Default source M2LB (was M2LB.Common); Terraform sources "Partial" (were "Failed"); "Structure 14 jobs without explicit stages" |
| 8–10 | Workflow → Dashboard → back | Same count; no binding demand; Project Quality Dashboard; back keeps context |
| W, Z | Safety | No comma-formatted CSS widths (nb-NO); no external host; no page errors; no writes |

## H. Status

| Group | Items |
|---|---|
| Fixed and browser-verified | RC-1, RC-2, RC-3 (no comma widths on visited pages; the 11.1 % bar itself needs a traceability run on selected artifacts — unit-verified under nb-NO), RC-4, RC-5, Pipeline structure wording |
| Fixed, not browser-verified | `ProgressBar` `aria-valuenow`; Schema relationship graph px |
| Expected (clarified) | Dependency version difference wording; Generated Documentation disclaimers; Pipeline source-only semantics; Dependency readiness ≠ health; Source Analysis limitations ≠ defects |
| Data-dependent | Requirements Traceability canonical counts 0 (no Delivery snapshot on the copy); the brief's baselines (29 pipelines, 2,004 files, 575 declarations …) belong to other snapshots/packages and were not all reproduced on the copy |
| Not audited / open | §G glossary 180/171/81 and contract-state reconciliation; §F transition denominators and Plan→Task on selected artifacts, matrix vs HTML export; §H Mermaid/escaped newlines and OpenAPI completeness; §K Renovate precedence/presets and stored-result provenance; §L gating classification (continueOnError, conditions, always()) and DEV→QA promotion evidence; IMP-06 (NEXT41) |
| Blocked by environment | None |

## I. Files changed

**Shared**

| File | Why |
|---|---|
| `IqrSourceContracts.cs` | `Outcome` on summaries |
| `GeneratedDocumentationContracts.cs` | Freshness N/A and denominator |

**Backend**

| File | Why |
|---|---|
| `SourceSetupAreas.Summary` | Summary with outcome |
| `IqrSourceEvidenceController` | Summaries with outcome |
| `SnapshotRegistryService.SourceAnalysisSummariesAsync` | Outcome from registry status |
| `PipelineReviewService` | Source status = outcome |
| `GeneratedDocumentationDiagnosticService` | Freshness N/A and denominator |

**Frontend**

| File | Why |
|---|---|
| `CssUnits` (new) | Invariant CSS numbers |
| `ArtifactTraceability.razor`, `HealthBar.razor`, `ProgressBar.razor`, `SchemaRelationshipGraph.razor` | Use `CssUnits` |
| `SourceAnalysisOverview` | Picker label from the outcome |
| `PipelineReview.razor`, `PipelineReviewPresentation` | Default source; structure wording |
| `ProjectContextStrip` | Workspace read; re-resolve with guard |
| `TechnologyCoverage.razor` | Scope sentence |
| `AutodocDiagnosticsSection`, `GeneratedDocumentationHealthPanel` (+ CSS) | Freshness cards and note |

**Tests:** as listed in F.

## J. Acceptance verdict

**Partially accepted.**

- **Accepted:** every shared root cause reproduced on the M2LB copy is fixed centrally, unit-tested and browser-verified, with no data mutation.
- **Not accepted:** several module-specific reconciliations in §F–§L are not audited (listed in H). The pass does not claim completion for them.
