# Manual Testing — Testoversikt data correctness + UX v3 (2026-11-26)

Commits: `3205eacc` (P0 data), `c18c2218` (module overview), `3c9b43fb` (KPI row / customization).

## 1. Baseline
The user's screenshot showed:
- 1,218 scenarios, but the priority bars summed to 1,175;
- «0 %» for 6 of 1,218;
- «Ende-til-ende-tester: 0»;
- «Gjennomføring: ingen scenarioer i planen» on plans with cases;
- a 4 + 1 KPI row.

## 2. KPI contract (`ManualTestingOverviewPresentation`)

| KPI | Population | Source |
|---|---|---|
| Scenarioforslag | All scenarios of the stored specification catalog in scope (acceptance scenarios + requirements without a scenario) + documented tests | `Overview.Specifications` |
| Testtilfeller | Saved cases in active plans (excluded/superseded not counted) | `Overview.Cases` |
| Godkjente | Same population, review = Accepted | — |
| Klar til testing | Same population, case testability ready (≠ approved) | — |
| E2E tile | Unique scenarios whose level is EndToEnd; declared ≠ verified | `TileScenarios` |
| Klargjøring av scenarioer | Unique scenarios by readiness status | `ScenarioPreparationEngine` |

## 3. The 43-scenario discrepancy — FIXED
**Root cause:**
- M2LB stories state P4, P5, P6 and P7 (7 stories, counted read-only in the source).
- `NormalPriority` kept any «P + digit», but the priority chart only had P1–P3 + «Ikke prioritert».
- So those stories' scenarios were in the KPI but in no bar.

**Fix:** `PriorityKeys` shows every stated priority in order, in both the priority and gap charts.

**Reconciliation test:** `D01_priority_buckets_reconcile_with_the_scenario_count_including_P4_to_P7` asserts sum(buckets) == the KPI population.

## 4. Coverage display — FIXED
One policy, `O.Share`:
- «0 %» only for 0;
- under 1 % with one decimal («0,5 %»);
- never «100 %» while something is missing («99,9 %»).

It is used in the KPI meters, the module chart values and the new «Dekning» column. Test `D02`.

## 5. E2E — explained
- Acceptance scenarios state no test level (Uavklart).
- Documented TEST-E tests were not read at all before `ff6bdc69`; M2LB now yields 8 (declared E2E).
- A zero now explains the unclassified count instead of implying «no E2E».

## 6. Plan / scenario integrity — FIXED
**Root cause:** «ingen scenarioer i planen» was shown whenever `Execution` was null. That happens when the API predates scenario-level execution (the user's frontend was newer than their API), not when the plan is empty.

**Fix:**
- «Gjennomføringsstatus er ikke tilgjengelig fra serveren» for that case;
- the empty text only for a genuinely empty plan;
- cards show «N testgrupper · N planlagte scenarioer (N uten testtilfelle) · N testtilfeller».

Test `D03`.

## 7. Follow-up panel
Already an action list with counts and drill-downs (v2). **PARTIAL:** not otherwise redesigned in v3.

## 8–10. Information architecture and KPI row
- Five KPI cards sit in one row when there is room, then 3 + 2, then 1 (`data-count` CSS).
- Per-tile menus are removed (v5).

## 11. Readiness visual
«Testforberedelse» is unchanged and still states that the milestones overlap. «Klargjøring av scenarioer» is mutually exclusive.

## 14. Module overview — IMPLEMENTED
- «Oversikt per modul» gains «Dekning» (share policy, «x av y» tooltip, sortable).
- The 0–100 % module chart duplicated it and is now **off by default**; an explicit saved choice still shows it.

## 12–13, 15
Priority drill-down accepts any stated priority. The plan cards from 6 apply.

## Tests
D01–D03, T01–T05 and the updated landing/R05 tests. Full frontend 9,833/9,833.

## Browser
- v1env17 (P0 build): tiles and E2E count 8.
- Layout at four widths: v1env18 (`C-*`), see the walkthrough report.

## Open
- Follow-up panel redesign: PARTIAL.
- Trend data: none exists, so none is shown.
- Performance: measured in the app-wide report.
