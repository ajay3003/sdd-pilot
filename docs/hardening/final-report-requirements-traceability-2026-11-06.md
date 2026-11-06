# Requirements Traceability — hardening (2026-11-06)

## 1. Confirmed defects

1. **A mixed-denominator total in the dashboard export.**
   - `ReportExportService.ExportDashboardSummary` printed "X% across assessed transitions" from `Health.CoveragePercentage`, which is (Const→Spec + Spec→Plan + Plan→Tasks covered) / (their combined totals). Three different populations went into one rate.
   - The page itself says "no combined traceability score".
2. **Unknown rendered as zero.** Without an interpreted Delivery snapshot (no feature packs), the Requirement → Code → Test panel (`CanonicalTracePanel`) showed every count as **0**, including "tasks with implementation evidence" and "requirements with explicit tests". That reads as "measured and empty".
3. **The disabled "Show feature packs" button had no explanation.** It is `disabled` when `Features == 0`; there was no `aria-describedby` and no visible reason.
4. **"66 informational observations" was one number over three populations:** Constitution rules not cited by the specification, requirements without a plan reference, and tasks without a requirement/SC reference. It is the same count; it was just never broken down.

## 2. Count and denominator definitions (source verified)

All stage counts are computed in the browser from the four documents of the selected **module + artifact pack**.

| Metric | Numerator | Denominator |
|---|---|---|
| Constitution → Specification (2/18) | Rules cited by a specification requirement | Citable authored Constitution rules (reference-only and non-citable excluded) |
| Specification → Plan (15/33) | Requirements with an architecture-decision or plan-location reference | Specification requirements |
| Plan → Tasks | **Not assessed.** Plan entries carry action text, not stable task IDs. | — |
| Tasks → Requirements/SC (33/65) | Tasks with an FR or SC reference | All task nodes |
| No explicit document reference (66) | Informational observations from the three populations above, now listed per population | — (never a rate) |
| Requirement → Code → Test | Canonical interpretation for the Target Environment project (separate scope) | "Not assessed" until a Delivery snapshot is interpreted |

## 3. Changes

- **Guided steps** (`TraceabilityWorkflow`): Select evidence scope → Constitution → Specification → Specification → Plan → Plan → Tasks → Tasks → Requirements/SC → Review gaps and evidence.
  - A transition step is Done when it was **assessed**, with its own x / y. It is never Done because coverage is high. Each step notes that "an explicit document reference is evidence of intent, not proof that work was implemented or tested".
  - **Plan → Tasks** is Not applicable / "Not assessed", with the reason and what is needed (task IDs in the plan). It is never 0 %.
  - **Gaps:** informational unless a configured policy requires the trace; "Links are never created to raise coverage". The policy-required count is the only thing that makes this step Current.
  - Step actions only switch tabs.
- **Observation breakdown** (`at-observation-breakdown`, `ArtifactTraceabilityReport.NoExplicitReferenceBreakdown`): "16 Constitution rules not cited … · 18 requirements without a plan reference · 32 tasks without a requirement/SC reference (different denominators, never one rate)".
- **Dashboard export:**
  - KPI: "N of 4 transitions assessed".
  - "Analyses run" line: per transition ("Constitution → Specification: 2 / 18 … · Plan → Tasks: Not assessed …"), "(per transition; no combined score)".
- **CanonicalTracePanel:**
  - With nothing interpreted, every count reads "Not assessed" (`data-interpreted="false"`). With an interpretation, 0 is a measured zero.
  - The disabled "Show feature packs" button now explains itself (`ct-features-why`): it points to Relationship Review → Analyze relationships and says nothing is invented.

## 4. Traceability authority (unchanged)

These sources are kept separate and not merged:
- **Document references:** this page, computed on the client.
- **Canonical relationships / Requirement → Code → Test:** the shared interpretation engine, per project.
- **Shared SDD evidence graph:** `/traceability`.

Candidate links are not counted as established (the canonical rule: Explicit, Derived or Confirmed).

## 5. Files changed

- `frontend/BirkNext.Web/Models/ArtifactTraceabilityModels.cs`: `NoExplicitReferenceBreakdown`.
- `frontend/BirkNext.Web/Services/TraceabilityWorkflow.cs` (new).
- `frontend/BirkNext.Web/Services/ReportExportService.cs`: dashboard traceability KPI and line.
- `frontend/BirkNext.Web/Pages/ArtifactTraceability.razor`: steps and breakdown.
- `frontend/BirkNext.Web/Components/CanonicalTracePanel.razor`: Not assessed, and the feature-pack reason.
- Tests:
  - `Pages/RequirementsTraceabilitySemanticsTests` +3: steps; Plan → Tasks reason; breakdown; dashboard export without a combined %.
  - `Components/CanonicalTracePanelTests` +2: Not assessed versus a measured zero; disabled-button reason.

## 6. Tests and results (actual runs, 2026-11-06)

Filter Traceability*, CanonicalTrace*, ReportExport* and Dashboard*: **305/305 passed**.

## 7. Browser verification

Run together with the Frontend Quality Review batch; see that report.

## 8. Remaining limitations

- **No URL state** for the tab, search or filters.
- **The matrix** still starts from Constitution rows. Its filter acts on the Const→Spec status only, and its Task edge is always Not assessed. The derived/candidate/confirmed statuses the brief lists do not exist in this document-reference model; they live in the canonical interpretation, and this page does not merge them.
- **The page HTML export** still omits module/pack provenance and the canonical panel counts.
- **`Health.CoveragePercentage` is still computed** in the model for compatibility. It is no longer shown anywhere.
