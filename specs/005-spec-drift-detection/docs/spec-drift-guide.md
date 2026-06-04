# Spec Drift Detection Guide

## What is Spec Drift?

Spec drift happens when the implementation gradually diverges from the specification:
- A requirement is added but never covered by a test
- A test is created but never linked to a requirement
- Coverage that existed in a previous sprint has quietly disappeared

Spec Drift Detection runs **deterministic rules** against your live traceability graph and surfaces drift the moment it appears — no manual audit, no AI required.

---

## Feature Chain

Spec Drift Detection is the final layer in the BirkNext analysis chain:

| Step | Feature | Answers |
|---|---|---|
| 1 | Specification Review | What are the requirements? |
| 2 | QA Artifact Library | Which tests exist? |
| 3 | Traceability & Coverage | Which tests cover which requirements? |
| 4 | Impact Analysis | How risky is each requirement right now? |
| 5 | AI Change Auditor | What did this specific change affect? |
| **6** | **Spec Drift Detection** | **Has drift accumulated over time?** |

---

## Drift Rules

The service runs four deterministic rules on each page load:

| Rule | What it detects | Risk |
|---|---|---|
| **R1 — Coverage Gap** | Requirement with 0 linked tests | High |
| **R2 — Partial Coverage** | Requirement with exactly 1 linked test | Medium |
| **R3 — Orphan Test** | Test not linked to any requirement | Medium |
| **R4 — Low Coverage** | Overall requirement coverage < 50% | High |

---

## Dashboard Cards

| Card | What it shows |
|---|---|
| **Drift Risk** | Overall health: High / Medium / Low |
| **Total Requirements** | All requirement scenarios in the library |
| **Requirements at Risk** | Requirements that are uncovered or partially covered |
| **Coverage Gaps** | Requirements with zero linked tests |
| **Orphan Tests** | Tests not linked to any requirement |
| **Coverage %** | Percentage of requirements with at least one test |

---

## How it reuses existing features

- **ImpactAnalysisService** — `GetImpactSummaryAsync()` provides requirement risk levels (High/Medium/Low) computed from trace link counts. `SpecDriftDetectionService` calls this directly; it does not recalculate risk thresholds.
- **Scenario model** — requirements and tests are read as existing `Scenario` records.
- **TraceLink model** — orphan test detection queries `TraceLinks` directly using the same `Covers` / `SourceKind` filters used by Impact Analysis and Traceability.

---

## How to test manually

### Prerequisites

- Full stack running (`podman compose up` then `dotnet run`)
- At least one requirement and one test in the QA Artifact Library

### Steps

1. Click **Spec Drift Detection** in the left sidebar (under Analysis, last item).
2. The page loads immediately — no input required.
3. Read the six KPI cards at the top. **Drift Risk** summarises overall health.
4. **Drift Findings** section lists which rules fired, with severity badges.
5. **Requirements at Risk** shows each uncovered or partially covered requirement with its reason.
6. **Orphan Tests** shows tests not linked to any requirement, with a link to Traceability to fix them.
7. **Recommended Actions** gives specific, actionable steps.
8. The **Feature Chain** diagram at the bottom shows how Spec Drift sits in the full analysis pipeline.

### Creating test data for drift

To exercise High Risk drift:
1. Add a requirement via the QA Artifact Library — do not link any test to it.
2. Reload Spec Drift Detection — the requirement appears in Coverage Gaps.

To exercise Orphan Test:
1. Add a test via the QA Artifact Library — do not link it to any requirement.
2. Reload Spec Drift Detection — the test appears under Orphan Tests.

To exercise Low Coverage:
1. Add many requirements without linking tests.
2. Overall coverage drops below 50% — the LowCoverage finding appears.

---

## Known limitations (v1)

- Coverage regression (comparing this sprint vs last sprint) is not yet implemented. Only the current point-in-time state is analysed.
- Git commit / file-change integration is not implemented. Drift is based solely on trace links, not code changes.
- Risk thresholds are fixed (same as Impact Analysis): 0 = High, 1 = Medium, 2+ = Low.
- Single project scope.
- Report is not persisted; it is computed fresh on each page load.

---

## Future extension points

| Extension | When | Notes |
|---|---|---|
| Coverage regression (R5) | v2 | Compare coverage counts across time-stamped snapshots |
| Git integration | v2 | Detect drift introduced by specific commits |
| AI drift explanation | v2 | Call AIChangeAuditService for natural-language drift summary |
| Repository scanning | v3 | Discover tests in code that are not in the library |
| Spec version history | v3 | Detect requirements that changed but tests did not update |
