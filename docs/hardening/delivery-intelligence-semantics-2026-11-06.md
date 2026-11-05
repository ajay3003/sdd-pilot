# Unified Delivery Intelligence — completion semantics (NEXT52, 2026-11-06)

Branch `008-traceability-first`. Merged brief: Delivery Overview (Project Leader) + Delivery Reports (Scrum Master). **Status: partially completed** — the critical semantics (G3) are fixed and verified. The full dashboard and IA redesign (G4–G8, P3–P13) is not done in this pass.

## Audit (G1/G3)

What the two modules already did correctly (verified in `DeliveryDashboard`, `DeliveryBacklog`):

- Status labels separate the checklist from completion:
  - "Task checklist complete — not verified, tested or accepted";
  - an authored `Status: Done` is labelled **"Stated done"**, not "Accepted";
  - Implementation and QA verification are their own states.
- Quality & readiness rows: Implementation verification "Not recorded", QA verification "Not assessed" (never 0%); acceptance only from an explicit status line.
- Change bars keep inventory growth, matched checked tasks and net checked delta apart; none is called "completed".
- Trend: "a baseline and a current snapshot are two points, not a trend".

Defects against G3/G4:

| # | Finding | Fix |
|---|---|---|
| S1 | The KPI row's only completion-like number was the **task checklist percentage** (95% on the M2LB copy), next to Features. There was no "completed with verified evidence" KPI, so 95% read as delivery completion. | New KPI **"Completed with verified evidence"**, second after Features: features with implementation or QA verification only. **"Not assessed"** when none is linked — never the checklist percentage, never 0. The detail line names how many more have only a complete checklist. Info text: "A task checklist at 94% (or 100%) and an authored 'Status: Done' are progress and declarations, not delivery completion." |
| S2 | The Delivery Reports filter was labelled "All tasks **completed**". | Renamed "All tasks **checked**", consistent with the Overview's "Checked". |

## Tests

- `DeliveryDashboardTests`: the verified KPI is "Not assessed" without verification and leads the row (features, verified, then the checklist).
- Existing drill-down and scope tests unchanged and passing. Frontend suite **8233 passed, 0 failed**.

## Browser verification (M2LB copy, Delivery Overview, nb-NO) — 6/6

1. KPIs in order: Features 24 · **Completed with verified evidence: Not assessed** · Task checklist 95% · Open tasks 58 · Checklist in progress 8 · Need review 3 · Blocked 0.
2. The checklist is labelled as a checklist, separate from completion.
3. No "completed features" wording on the page.
4. Read-only.
5. No external host.
6. No page errors.

## Not done (remaining brief scope)

- G2 shared component set (DeliveryKpiCard, FeatureCard, EvidenceDrawer…) and one calculation service across both modules. The two modules already share `DeliveryWorkspace` / `DeliveryFeatureExplorer` and `DeliveryDashboard`.
- G4 full-width feature cards replacing the list + detail split; navigation Overview / Features / Risks & Quality / Technical details.
- G5/P3–P13 Scrum Master dashboard (scope-change chart, weekly summary), Change Board regrouping, milestone timeline, FQS restructuring, history and immutable report navigation.
- G10 performance work for ~1,500–2,000 work items (lists are already filtered and grouped; no virtualization added).
