# BirkNext — response to «M2LB (9).zip — kildeanalyse for BirkNext» (2026-11-27)

**Source:** `M2LB_9_kildeanalyse_BirkNext_2026-11-27.md`. It is a static analysis of the archive, and it separates source observations, hypotheses and verification needs. This document checks those claims against BirkNext's code and data and fixes what the evidence supports.

**Status values:**
- IMPLEMENTED: changed, with tests.
- VERIFIED: checked against code or data; no change needed.
- PARTIAL
- OPEN
- REQUIRES DECISION

**Safety:**
- No commits or pushes.
- Nothing was migrated, reclassified, merged or deleted.
- The archive was read in memory (never extracted), with BirkNext's own parser run read-only.
- Imports ran only on the isolated instance.

## 1. The «Prosjektnivå 8 / 92» question (SRC-004, open questions 1–3)

**What BirkNext's parser reads from `M2LB (9).zip`** (SHA-256 `bbc3ec2b…1eea`, 3,209 entries, 45 `spec.md`).
- Method: the server's own `SpecificationEvidenceBuilder`, run with `dotnet fsi` against the built API assembly, over the six project-level specs read in memory.

| Specification | Numbered Given lines | Acceptance scenarios parsed | Requirements | Requirements without a scenario |
|---|---:|---:|---:|---:|
| `specs/001-platform-contract-rename` | 20 | 20 | 25 | 24 |
| `specs/002-operation-display-name` | 15 | 15 | 23 | 23 |
| `specs/003-birkid-partial-search` | 12 | 12 | 8 | 6 |
| `specs/003-cross-service-contract-translation` | 16 | 16 | 24 | 23 |
| `specs/004-search-barn-tiered-detail` | 17 | 17 | 15 | 14 |
| `specs/005-school-report-read-api` | 26 | 26 | 28 | 22 |
| **Total** | **106** | **106** | 123 | **112** |

- **VERIFIED: the parser reads every project-level scenario.** All 106 numbered Given lines are acceptance scenarios inside «Acceptance Scenarios» blocks; none are lost or merged.
- «Krav uten scenario» = requirements that no acceptance scenario of the same document names. For the project level of `M2LB (9)` that gives 112, not 92.
- **«8 / 92» matches the old «Prosjektnivå» composition without the project's own specs.** The project-level audit of the same day (on `M2LB.zip`) found 8 documented TEST-E tests, 50 module documents filed under the wrong group and 42 `docs/` documents, and 50 + 42 = 92. That points to a dashboard over **an import whose catalog has no project-level specs**: an older archive (the report's own table shows `M2LB(20261124…)`, `M2LB(20261125…)` and `M2LB-language(3)` have none), or a project opened from an older import of the application. It could also be a build from before the project-level fix of 2026-11-27.
- **OPEN:** confirming this on the tester's machine needs the screenshot's import (archive name, SHA-256, import time). Two new tools now give that answer directly (§2).

**Isolated reproduction**
- `M2LB.zip` (= `M2LB (8).zip`, 4 project-level specs): «Prosjektnivå» 63 scenarios / 76 requirements without a scenario. These are exactly the parser's numbers for those 4 specs (20+15+12+16 / 24+23+6+23). «Uten modultilknytning» shows 0 / 42.
- `M2LB (9).zip` as «Ny versjon av eksisterende applikasjon»: **not completed.** The import form kept «Import project» disabled after the application choice; the blocking step was not identified within this task. **OPEN.**

## 2. Changes

| Item | Status | Change | Evidence |
|---|---|---|---|
| **SRC-001** «show the actually imported source/version/hash» | **IMPLEMENTED** | Testoversikt shows «Kildegrunnlag: {archive} · importert {time} · SHA-256 {12}» for the project's import. When a **newer import of the same application** exists, it warns: «Tallene her bygger på den eldre importen. Velg den nyere importen som prosjekt …». It never switches by itself, and it never compares other applications or imports without a recorded application. | `ManualTestingSourceBaselineTests` (3) |
| **SRC-004** reconcile the counts with the parser's real extraction | **IMPLEMENTED** (tool) | System Settings → **Data Quality** checks a project in three stages: source → stored, stored → API, API → UI. It reports the baseline (catalog revision, time, archive hash), flags a stale baseline, and lists per-document differences. See `birknext-system-settings-data-quality-2026-11-27.md`. | backend `DataQualityTests` (28), frontend `DataQualityCenterTests` (8) |
| **SRC-002 / SRC-009** «Prosjektnivå» is a source location, not a module or E2E; shown separately | **IMPLEMENTED** earlier the same day | «Utenfor modulene» section with «Prosjektnivå» (root `specs/`) and «Uten modultilknytning» (`docs/`), each with its own explanation and drill-down. Module documents are in their module. | project-level audit; dashboard browser run (PLA-checks pass) |
| **SRC-003** FR/US ids repeat across specifications | **IMPLEMENTED** (analysis) / **REQUIRES DECISION** (migration) | Nothing matches on a display id alone. Testoversikt coverage is path + identity. The Data Quality identity dry run lists shared v1 identities, ambiguous relationships and a simulated source-aware v2. A persistent migration is not done. | `birknext-requirement-identity-migration-proposal-2026-11-27.md`; DQ13–DQ17 |
| **SRC-007** E2E filter/KPI must separate acceptance, candidates, planned and verified | **IMPLEMENTED** (FUN-014) | The E2E tile counts documented E2E tests and opens exactly those. Candidates, plan membership, prepared cases and executions are separate. | V10 report «FUN-014» |
| **SRC-011** no data/ID migration before mapping, impact analysis and rollback | **VERIFIED** | No migration was made. Proposal plus a read-only dry run. | — |
| **SRC-012** project-wide scenarios searchable beyond the first 2k | **IMPLEMENTED** (V10 P1) | Server search in «Legg til scenarioer» covers every eligible scenario. | V10 report P1 |
| **SRC-005 / SRC-006** superseded / Draft specifications must not give «verified coverage» | **OPEN** | The spec's own status line (`Status: Draft`, «Delvis overstyrt av …») is not read or stored by the parser today, so BirkNext cannot show it. **Proposal:** store the document status from the spec header in the catalog evidence, show it on the specification and its scenarios, and refuse «approved» without a confirmation for a Draft or superseded source. This is a parser and catalog change; it is not done in this task. | — |
| **SRC-008** traceability from docs and module specs without duplicate scenarios | **VERIFIED** (no duplicates) / **OPEN** (docs as E2E sources) | `docs/analysis` documents are listed under «Uten modultilknytning» as requirements, never as scenarios. Using them as E2E-candidate sources (report §6) is not implemented. | project-level audit |
| **SRC-010** E2E suggestions must show missing environment, data, observation, clarifications | **PARTIAL** | Scenario readiness («Klargjør AC») already records expected result, verification method, test data, trigger and environment/access as separate criteria. Nothing E2E-specific was added. | V5 readiness |

## 3. The report's open questions (§10)
| # | Answer |
|---|---|
| 1 | Not answerable from here: the screenshot's import is on the tester's machine. **Now visible** in Testoversikt («Kildegrunnlag») and in Data Quality (baseline + stale warning). |
| 2 | **All 106** project-level acceptance scenarios are parsed (table §1). |
| 3 | In `M2LB (9)` the project level has 112 requirements without a scenario, all from the six root specs. 92 matches the old composition from `docs/` + misattributed module documents (§1). |
| 4 | By specification path + source identity, never by display id. Repeated ids are listed, and shared v1 identities and ambiguous relationships are reported by the Data Quality dry run. |
| 5 | `docs/analysis` and `docs/operations` documents are read as requirement documents («Uten modultilknytning»). `Draft` / superseded status is **not** read (SRC-005, OPEN). |
| 6 | E2E tile = documented E2E tests (FUN-014). 8 vs 11 = 8 documented + 3 candidates under a level filter (fixed: the tile opens its exact 8). |
| 7–8 | Outside BirkNext's code (test environment). Corrections never rewrite plan occurrences or revision history: every change is a new revision. |

## 4. Not done in this task
- The `M2LB (9)` import as a new version on the isolated instance (blocked in the form; §1).
- Draft / superseded specification status (SRC-005/006).
- `docs/analysis` / `docs/architecture` as E2E-candidate sources (SRC-008, report §6).
