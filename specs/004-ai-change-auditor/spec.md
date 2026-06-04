# Feature Specification: AI Change Auditor

**Feature Branch**: `004-ai-change-auditor`  
**Created**: 2026-06-04  
**Status**: Implemented  
**Depends on**: `001-create-scenario`, `002-traceability-coverage`, `003-delta-impact-analysis`

---

## Purpose

Help a test lead answer: **"What did this change affect and what should I test?"**

When a developer describes a change (a feature, a fix, a refactor), the test lead needs to know:

- Which requirements are plausibly affected
- Which existing tests may be impacted
- What the test coverage gaps are for those requirements
- What the overall risk level is
- Which tests to run in regression

---

## User Story 1 — Submit a Change Description (P1)

A test lead enters a plain-English description of a software change and clicks "Analyze Change."
The AI identifies affected requirements and tests from the project library and presents a structured audit report.

**Independent Test**: Enter "Added duplicate scenario validation" and click Analyze. Confirm the report appears with requirements, tests, risk level, and regression recommendation.

**Acceptance Scenarios**:
1. **Given** a change description is entered, **When** Analyze Change is clicked, **Then** the AI returns a report with Affected Requirements, Affected Tests, Coverage Gaps, Risk Level, and Regression Recommendation.
2. **Given** the API key is missing, **When** Analyze Change is clicked, **Then** a clear error message appears — no crash.
3. **Given** the change description is empty, **When** the button is clicked, **Then** validation prevents submission.

---

## User Story 2 — View Structured Audit Report (P2)

The test lead reads the AI-generated report:
- KPI cards: overall risk, requirements impacted, tests impacted, regression test count
- Affected Requirements list with risk badges and AI relevance reasons
- Affected Tests list with AI relevance reasons
- Coverage Gaps for affected requirements with no linked tests
- Regression Recommendation: deterministic list of tests to run (from ImpactAnalysisService)
- AI Reasoning: Claude's explanation of the analysis

**Acceptance Scenarios**:
1. **Given** the report is shown, **Then** each affected requirement shows its risk level, linked test count, and the AI's reason.
2. **Given** an affected requirement has no linked tests, **Then** it appears in Coverage Gaps and the regression recommendation shows "High Risk — manual testing required."
3. **Given** the regression recommendation is non-empty, **Then** each entry shows the test title and reason ("Directly covers ...").

---

## Risk Levels

| Coverage | Risk |
|---|---|
| 0 linked tests | High |
| 1 linked test | Medium |
| 2+ linked tests | Low |

Same thresholds as Delta Impact Analysis (reused from ImpactAnalysisService).
Overall risk = highest risk across all affected requirements.

---

## AI Integration

- Claude is called via the Anthropic Messages API with tool use (structured output).
- The tool enforces a JSON schema: affected requirement IDs, affected test IDs, per-item reasons, coverage gaps, regression scope, and reasoning.
- ImpactAnalysisService computes formal risk levels for requirements Claude identifies — no risk logic in Claude's output.
- Claude's role: **semantic matching**. ImpactAnalysisService's role: **formal risk computation**.

---

## Assumptions

- Change description is free text, 1–2000 characters.
- Project data (requirements, tests, trace links) is loaded fresh on each analysis call.
- Audit reports are not persisted in v1 — computed on demand.
- Anthropic:ApiKey must be set in backend configuration. Without it the mutation returns a structured error.
- Only `Covers` trace links feed into regression recommendations, same as Impact Analysis.

---

## Future Extension Points

The `ChangeAuditRequest` model is designed for future input types:

| Input type | Status |
|---|---|
| Change description (free text) | **Implemented (v1)** |
| Git commit hash | Extension point (v2) |
| Pull request URL | Extension point (v2) |
| Changed file list | Extension point (v2) |
| AI coding session context | Extension point (v3) |
| Spec Drift Detection payload | Extension point (v3) |
