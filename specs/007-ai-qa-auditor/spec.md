# Feature Specification: AI QA Auditor

**Feature Branch**: `003-delta-impact-analysis` (added here)
**Created**: 2026-06-04
**Status**: Implemented
**Depends on**: `003-delta-impact-analysis`, `004-ai-change-auditor`, `005-spec-drift-detection`, `006-code-traceability`

---

## Purpose

Provide a single executive quality dashboard that aggregates all QA quality signals into a deterministic score and an optional AI-generated narrative.

Help a test lead or project manager answer:
- **"What is the overall QA health of this project right now?"**
- **"What are the top risks I should act on before the next release?"**
- **"What would I tell a stakeholder about the current QA posture?"**

---

## Architecture Context

```
TraceLinkService
    ↓ (coverage %)
SpecDriftDetectionService
    ↓ (reuses ImpactAnalysisService)
    ↓ (requirements at risk, orphan tests, drift findings)
CodeTraceabilityService
    ↓ (code file registration stats)
AIQaAuditorService
    ↓ (deterministic score + readiness)
    ↓ (optional: Claude for executive summary)
QaAuditReport → /ai-qa-auditor page
```

No new DB tables. All data comes from existing services.

---

## User Stories

### US1 — Quality Score Dashboard

A test lead opens the AI QA Auditor page and sees an overall quality score (0–100), a readiness status badge, KPI cards, and deterministic recommended actions — all without calling Claude.

**Acceptance**:
1. Page loads fast (< 1s for projects with < 100 requirements).
2. Quality score is between 0 and 100.
3. Readiness status is one of: Ready, Review Needed, High Risk.
4. KPI cards show: Coverage%, Requirements at Risk, Drift Findings, Orphan Tests, Unlinked Code Files, High Risk Requirements.
5. Score Deductions table explains what reduced the score.

### US2 — AI Executive Summary

A test lead clicks "Generate Summary" and receives a 3-4 sentence executive overview suitable for a project manager, plus specific concerns and recommended actions from Claude.

**Acceptance**:
1. Button is visible when AI summary hasn't been generated.
2. Spinner appears while Claude is processing.
3. AI summary, concerns, and recommended actions appear on success.
4. If `Anthropic:ApiKey` is missing, a clear error message explains why AI is unavailable.

### US3 — Drill-down via Feature Chain

The feature chain diagram at the bottom shows which features feed into the audit report, so the test lead knows where to go for more detail.

---

## Scoring Model

| Condition | Deduction |
|---|---|
| Coverage < 50% | −30 pts |
| Coverage 50–74% | −15 pts |
| Coverage 75–79% | −5 pts |
| Per high-risk drift finding (max 30) | −15 pts each |
| Per medium-risk drift finding (max 10) | −5 pts each |
| Per orphan test (max 10) | −3 pts each |
| Per unlinked code file (max 10) | −2 pts each |

Minimum score: 0.

## Readiness Status

| Status | Condition |
|---|---|
| **Ready** | Score ≥ 80 AND coverage ≥ 80% AND no high-risk drift findings |
| **High Risk** | Score < 50 OR coverage < 50% OR any high-risk drift finding |
| **Review Needed** | Everything else |

---

## AI Layer

Claude is called only when `includeAiSummary = true`. The service degrades to `null` when:
- `Anthropic:ApiKey` is not configured
- The API call fails (network error, rate limit, etc.)

Claude receives the aggregated metrics and deductions. It is NOT used for scoring — only for explanation. Output fields: `executive_summary`, `concerns[]`, `recommended_actions[]`.

---

## Assumptions

- Single project scope (`birknext-demo`).
- All data comes from existing services — no new DB tables.
- AI summary is opt-in (click "Generate Summary") to avoid slow initial page load.
- DbContext is sequential (not parallel) to avoid EF Core thread-safety issues.

---

## Future Extension Points

| Extension | Notes |
|---|---|
| Score history / trends | Persist `QaAuditReport` snapshots to DB with timestamps |
| Per-requirement risk drill-down | Link from TopRisks row to Impact Analysis for that requirement |
| Scheduled weekly report emails | Fire `AIQaAuditorService` on schedule, email to stakeholders |
| CI/CD gate | Fail build if `QualityScore < threshold` or `ReadinessStatus == HighRisk` |
