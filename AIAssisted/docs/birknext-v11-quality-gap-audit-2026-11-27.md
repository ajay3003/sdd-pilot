# BirkNext V11 — quality gap audit (2026-11-27)

## Phase 0: baseline
- **Order:** V11 ran **after** V10 and V12, at the user's direction («run v11 after v12»).
- **Commits:**
  - V10: `402ebb50`, `73b1327e`, `9af21c93`, `afade54e`;
  - V12: `ea835190`.
- **Other sessions:** none running. The uncommitted Data Quality / SRC-001 work (other tasks, «no commits») was present and was not touched.
- **Browser infrastructure:** isolated instances `v1envN` (ports 5097/5197, disposable database and App_Data) with the M2LB package and 20 seeded plans; scripts `v1_*.py`.

**Severity:** P1 = blocks or misleads a core journey; P2 = costs time or causes errors; P3 = polish.

| Area | V11 expectation | State found | Evidence | Pages | Sev | Overlap | Regression risk |
|---|---|---|---|---|---|---|---|
| Scenario occurrence without a case is counted | Counted | **Done** | `PlanOccurrences.Of`, FUN-015 tests, browser FUN015 | Planlegg, Testplaner, Testgjennomføring | — | V10 | — |
| A case is not an occurrence | Never | **Done** | FUN-015 / DQ01 | same | — | V10 | — |
| Approval ≠ readiness | Separate | **Done** | BUG-004 (confirmation, policy), readiness panel | Planlegg | — | V10 | — |
| Blocked ≠ verified | Never | **Done in V12** | «x av y behandlet · m blokkert (ikke verifisert)» | Testgjennomføring | — | V12 | — |
| E2E candidate ≠ verified E2E | Separate | **Done** | FUN-014 (five populations), Data Quality `e2e.*` | Testoversikt, Finn og forstå | — | V10 | — |
| Canonical KPI meanings documented | Every KPI | **Done** | Data Quality `ReferenceCalculator.Contracts` + reconciliation | all | — | Data Quality task | — |
| KPI consistency across views, tested | Tests per rule | **Done** + a **defect found and fixed:** Testoversikt dropped one scenario of every «twin» pair (2 in M2LB); the fix is uncommitted with the Data Quality task | DQ26; browser Data Quality 20/20, 0 FAIL after the fix | Testoversikt | P2 | Data Quality task | Low |
| E2E filter: 11 results behind nested groups | Shown directly | **V11 fix:** a filtered result of ≤ 25 opens its groups (grouping kept, explicit close respected) | `V11_a_small_filtered_result…`, G03 updated | Finn og forstå | P2 | V12 MT-6 (partial) | Low |
| Repeated «Vis detaljer», narrow panel, duplicated «Forbered testtilfelle» | One action, row opens | **Done in V12** (MT-1, MT-2) | V12 tests | Finn og forstå | — | V12 | — |
| Readiness labels «Vurdert av tester / Oppfylt / Mangler / Ikke vurdert» | As listed | **Done** (V12 changed «Bekreftet av tester» on readiness) | `ScenarioPreparation` labels | Planlegg, Finn og forstå | — | V12 | — |
| Search beyond the initial window | Whole population | **Done** (V10 P1 server search; Discover «beyond» search) | V10 report P1; `ASP_*` tests | Finn og forstå, Legg til scenarioer | — | V10 | — |
| Backend: full single-process run exhausts memory | Investigate causes | **V11: cause proven and fixed** (see implementation report) | `TestHostMemoryDiagnostics` | tests | P2 | — | Low |
| Performance baseline | Measured | **V11:** measured (see the performance report) | `v1_perf.py` | all Manual Testing | — | — | — |
| Release criteria | Defined, evidence-based | **V11:** release-readiness report | — | — | — | — | — |
| Manual-tester acceptance | Script; results only if real | **V11:** guide delivered; exercise **NOT VERIFIED** (no testers) | — | — | — | — | — |
| No stale scenario from another project after a scope switch | Never | **Done earlier** (`S03_an_explicit_module_change_ends_the_subset…`, `_missingScenario`) | Discover tests | Finn og forstå | — | earlier | — |
| One shared filter toolbar and grouping system | Consolidated | **Not done** (V12 MT-5/MT-6 NOT IMPLEMENTED) | V12 audit | Finn og forstå | P2 | V12 | Medium |
| Direct «Legg til i testplan» from Finn og forstå with a destination suite | Explicit | **Not done** (V12 MT-9) | V12 audit | Finn og forstå | P2 | V12 | Medium |

**Not treated as defects:** UX that could be better but works (for example the three readiness counts, which measure different things; V12 MT-3) is listed as improvement, not as broken.
