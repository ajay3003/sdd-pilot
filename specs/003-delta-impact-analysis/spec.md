# Feature Specification: Delta Impact Analysis

**Feature Branch**: `003-delta-impact-analysis`  
**Created**: 2026-06-04  
**Status**: Implemented  
**Depends on**: `002-traceability-coverage` (TraceLink model, TraceLinkService)

---

## Purpose

Help a test lead answer: **"What should I test now?"**

When a requirement is under review or about to change, the test lead needs to know:
- Which tests currently cover it
- Whether the coverage is sufficient
- What regression test suite to run
- How risky it is to accept the change

---

## User Story 1 — View Impact Summary for All Requirements (P1)

A test lead opens the Impact Analysis page and immediately sees all requirements ranked by risk level. High-risk requirements (no tests) are surfaced first, followed by medium-risk (single test), then low-risk (well-covered).

**Independent Test**: Navigate to `/impact-analysis`. Confirm summary cards show correct risk counts and the requirement table groups by risk.

**Acceptance Scenarios**:
1. **Given** a project has requirements, **When** the test lead opens Impact Analysis, **Then** they see High/Medium/Low risk counts and a full list of requirements with their risk labels.
2. **Given** a requirement has 0 linked tests, **Then** it appears as High Risk.
3. **Given** a requirement has exactly 1 linked test, **Then** it appears as Medium Risk.
4. **Given** a requirement has 2 or more linked tests, **Then** it appears as Low Risk.

---

## User Story 2 — Analyse a Single Requirement (P2)

A test lead selects a requirement and sees its full impact detail: linked tests, risk level, and a deterministic regression recommendation listing exactly which tests to run and why.

**Independent Test**: Select any requirement. Confirm the detail panel shows correct linked tests, risk badge, and regression list with reasons.

**Acceptance Scenarios**:
1. **Given** the test lead selects a requirement with 2 linked tests, **When** the detail panel appears, **Then** both tests appear in the regression recommendation with "Directly covers this requirement" as the reason.
2. **Given** the test lead selects a requirement with 0 linked tests, **When** the detail panel appears, **Then** the regression recommendation shows "No tests linked — manual testing required."
3. **Given** the test lead selects a requirement, **When** they view the summary cards in the detail panel, **Then** they see Linked Tests, Accepted Tests, Missing Coverage, and Risk Level.

---

## Risk Calculation Rules

| Linked Test Count | Risk Level |
|---|---|
| 0 | High |
| 1 | Medium |
| 2+ | Low |

These thresholds are deterministic and version-controlled. No AI or randomness involved.

---

## Regression Recommendation Rules

- Include all Tests linked to the requirement via `Covers` link type
- For each test, include: test display label, title, reason
- Reason: "Directly covers this requirement"
- When no tests: "No tests linked — manual testing required"
- Order: by test title alphabetically (stable, reproducible)

---

## Assumptions

- All Scenarios in the database are valid artifacts; no acceptance/rejection filtering needed at this stage.
- The page is scoped to a single project (same pattern as all other BirkNext pages).
- No AI suggestions, no file-change tracking, no commit-level links in this version.
- Risk thresholds (0=High, 1=Medium, 2+=Low) are fixed for v1 and not user-configurable.
- `RelatedTo` link type does not affect risk or regression recommendations; only `Covers` counts.
