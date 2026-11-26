# Manual Testing — Testoversikt visual redesign v2 (2026-11-26)

Commit: `ff6bdc69` (built and browser-verified before the later v3/v5 passes).

## What changed
- **Charts:**
  - the legacy HTML bars (`.dd-bars`) are replaced by `Components/DashboardBarChart.razor`, a hand-written SVG horizontal bar chart;
  - gridlines and «nice» axis ticks (1, 2, 2.5, 5, 10 × 10ⁿ), with labels and values outside the plot;
  - a visually hidden table alternative;
  - every bar is a button with the same drill-down test ids as before, so the existing drill tests still pass.
- **Action lists:** «Dette bør følges opp» rows with counts and drill-down.
- **Plans with remaining scenarios:** «Sprintplaner med gjenstående AC-er» shows plan cards with an execution bar (passed / failed / blocked / not run) from real scenario-occurrence events (`OverviewPlan.Execution`, backend: the latest result per plan and occurrence, one query).
- **Module table:** sortable (`aria-sort`); KPI cards gained icons.

## Browser (isolated v1env14, M2LB)
- No legacy bars at 1920/1440/1280/1024/390; 4 SVG charts.
- KPI columns 4/4/4/2/1; no horizontal overflow.
- Plan cards exec 1/1/1/2 and 0/1/0/2 (matching the seeded events).
- Chart drill-down → Finn og forstå works; table sort works.

## Since then
- v3 (`3205eacc`, `c18c2218`): priority reconciliation, share policy, honest plan states, module table «Dekning».
- v5 (`3c9b43fb`): customization mode, KPI row by count.

## Tests
`ManualTestingWorkspaceTests` V01–V05 (v2) plus later D/T tests. Full frontend 9,833/9,833.
