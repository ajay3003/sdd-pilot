# Feature Specification: Spec Drift Detection

**Feature Branch**: `003-delta-impact-analysis` (added here)  
**Created**: 2026-06-04  
**Status**: Implemented  
**Depends on**: `001-create-scenario`, `002-traceability-coverage`, `003-delta-impact-analysis`, `004-ai-change-auditor`

---

## Purpose

Help a test lead answer: **"Has implementation drifted away from the specification?"**

When a codebase evolves over time, requirements accumulate without tests, tests become unlinked, and coverage regresses without anyone noticing. Spec Drift Detection runs deterministic rules against the live traceability graph and surfaces drift immediately — no manual audit required.

---

## Feature Chain

```
Specification
    ↓ (QA Artifact Library — Scenario Management)
Traceability
    ↓ (Traceability & Coverage — trace links)
Coverage
    ↓ (Impact Analysis — risk per requirement)
Impact Analysis
    ↓ (AI Change Auditor — semantic change analysis)
AI Change Auditor
    ↓ (Spec Drift Detection — drift rules over live data)
Spec Drift Detection
```

---

## User Story 1 — View Drift Dashboard (P1)

A test lead opens Spec Drift Detection and immediately sees the project's drift health: how many requirements are at risk, how many tests are orphaned, what the overall coverage is, and a risk level.

**Acceptance Scenarios**:
1. **Given** requirements exist with no linked tests, **Then** the Coverage Gaps count is non-zero and Overall Risk is High.
2. **Given** all requirements are covered by 2+ tests, **Then** Overall Risk is Low.
3. **Given** tests exist that are not linked to any requirement, **Then** Orphan Tests count is non-zero.

---

## User Story 2 — Review Findings and Recommended Actions (P2)

The test lead reads the detailed findings list and recommended actions. Each finding explains which rule fired and what to do about it.

**Acceptance Scenarios**:
1. **Given** CoverageGap finding fires, **Then** Requirements at Risk list shows those requirements with "High Risk" badge.
2. **Given** OrphanTest finding fires, **Then** Orphan Tests list shows the test titles with a link to Traceability.
3. **Given** no drift is detected, **Then** a healthy "All requirements covered" message appears.

---

## Drift Rules (v1)

| Rule | Category | Condition | Severity |
|---|---|---|---|
| R1 | CoverageGap | Requirement has 0 linked tests | High |
| R2 | PartialCoverage | Requirement has exactly 1 linked test | Medium |
| R3 | OrphanTest | Test not linked to any requirement | Medium |
| R4 | LowCoverage | Overall coverage < 50% | High |

---

## Overall Drift Risk

| Condition | Overall Risk |
|---|---|
| Any CoverageGap OR coverage < 25% | High |
| Any PartialCoverage OR orphan tests OR coverage < 75% | Medium |
| All requirements covered + no orphans + coverage ≥ 75% | Low |

---

## Reuse

- `ImpactAnalysisService.GetImpactSummaryAsync()` — requirement risk levels (no recalculation)
- `RiskLevel` enum — consistent severity vocabulary
- `Scenario` model — requirements and tests shared directly

---

## Assumptions

- No database changes — everything is computed from existing `scenarios` and `trace_links` tables.
- Report is computed on demand; not persisted (same pattern as AI Change Auditor).
- Only `Covers` trace links affect coverage; `RelatedTo` links are ignored.
- Single project scope (same pattern as all other pages).

---

## Future Extension Points

The `SpecDriftDetectionService` is designed with commented hooks for:
- Git commit / file-change integration
- Specification version history comparison (R5: coverage regression across versions)
- Repository scanning for undiscovered tests
- AI-assisted drift explanation (via AIChangeAuditService)
