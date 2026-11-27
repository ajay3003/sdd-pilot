# BirkNext — Testoversikt redesign, UX-107 to UX-111 (2026-11-27)

The brief says no commits: the changes are uncommitted in the working tree. UX-107 (the P1 follow-up) arrived as its own brief and is part of this one.

**Safety:**
- Browser checks ran only on an isolated instance (`v1env35`: ports 5097/5197, a disposable database, own App_Data, a clean source copy).
- No normal data was read or written.
- No Azure DevOps writes; no Docker or Podman.

## Current problems (audited before changing anything)
| # | Problem | Evidence |
|---|---|---|
| 1 | «Testplaner og mer innsikt» was the **last** section. Plans and scenario readiness came only after follow-up, readiness charts and module tables. | `Pages/ManualTesting.razor`: band order action → readiness → modules → (drill-down) → more |
| 2 | Opening «P1-scenarioer uten testtilfeller» inserted **every group expanded**. Each group had up to 5 links, a «Åpne alle N i Finn og forstå» button and a priority label on every row, and the list rendered **after the module section**, far from the line that opened it. | `DrillPanel`, `DrillItemsPerGroup = 5`, rendering position |
| 3 | No search or filter inside the list. | — |
| 4 | «Klargjøring av scenarioer» was a bar chart (1 vs 538) with no meaning or next step. | `chart.scenarioReadiness` |
| 5 | The plan card mixed approval («godkjent») with execution figures in one line of text. | plan card markup |

## KPI semantics (verified in code; not changed)
- **«P1-scenarioer uten testtilfeller»** (`P1Attention` and its drill-down `p1-uncovered`):
  - acceptance scenarios and documented tests whose source gives priority P1, summed per specification;
  - included only when **no saved test case** in an active plan belongs to them. Approved and unapproved cases count the same;
  - a scenario planned **without** a case is still included;
  - technical scenarios are included. Requirements without a scenario have no priority, so they are not included;
  - the count and the list use the same rule. Adding a case removes the scenario (the overview is recomputed).
  - **It does not mean every one needs a manual test case.** The panel now says so.
- **«Klargjøring av scenarioer»** (`ScenarioReadiness`):
  - unique scenario identities per status: Klar til kjøring / Trenger klargjøring / Må vurderes. They are exclusive, and optional items never count as missing;
  - the drill-down filters the same population;
  - «Må vurderes» = items not assessed or needing reassessment. It does not mean anything is missing, and it does not mean manual test cases are needed.
- **Test method (manual / technical) is not in the overview data per scenario.** It is therefore not offered as a filter, and nothing is guessed. NOT IMPLEMENTED: it would need the scenario's test-method decision in `OverviewScenario`.

## New page order (IMPLEMENTED)
1. **Nøkkeltall**: unchanged.
2. **Testplaner og mer innsikt**, moved up, directly after the key figures:
   - Testplaner and «Klargjøring av scenarioer» as two balanced panels from 1100 px, stacked below that;
   - plus «Siste endringer».
3. **Viktig å følge opp** (was «Dette bør følges opp»): the P1 lines, testability and blockers, source changes, plans with scenarios not run, failed or blocked. **A follow-up list opens directly under this section**, and a chart's own list opens next to its chart.
4. **Testberedskap**, **Moduler** (the project-level audit's «Utenfor modulene» section) and **Detaljert analyse**: the optional charts, kept and moved last.

## Implemented changes
| UX | Change | Status |
|---|---|---|
| 107 | **Follow-up workspace** replacing the inline report:<ul><li>the meaning of the list;</li><li>search (title, US/AC label, story, source identity);</li><li>module and specification filters; reset;</li><li>a live count «Viser X av Y scenarioer i Z grupper»;</li><li>groups **collapsed** when the list is large (more than 3 groups or 30 rows), with toggles that report expanded or collapsed state;</li><li>compact rows (title; label, readiness, «har testtilfelle»; one «Åpne»), 10 at first with the shared «Vis flere»;</li><li>**one «Åpne i Finn og forstå» per group**, for exactly the matching scenarios (by source identity, never by the display id);</li><li>the priority shown only when the rows differ;</li><li>the open list, its search, filters and open groups **kept for the tab and restored when the tester comes back**.</li></ul> | IMPLEMENTED; VERIFIED (component tests; browser: below) |
| 108 | Plan card: «Forberedelse» (test cases, approved, source changes) and «Gjennomføring» (passed, failed, blocked, not run bar) on separate labelled lines; an «Åpne testplan» button on each card. | IMPLEMENTED |
| 109 / 110 | «Klargjøring av scenarioer» as **cards**. Each card shows the state, the count, what it means, and one next step («Åpne scenarioer» / «Start vurdering» / «Se scenarioene»), which opens exactly that population in the follow-up workspace. From there it goes to Finn og forstå per specification. | IMPLEMENTED; VERIFIED (count = list in tests) |
| 111 | The page order above; follow-up lists no longer push plans or readiness down. | IMPLEMENTED |

## Files
- `frontend/BirkNext.Web/Pages/ManualTesting.razor`
- `Services/ManualTestingOverviewPresentation.cs` (`FollowUpFilter`, `FilterDrill`, `ScenarioLabel`, `DrillMeaning`, `ReadinessMeaning`, `ReadinessAction`)
- `Services/TabDraftStore.cs` (`OverviewFollowUpKey`)
- `wwwroot/css/manual-testing.css`, using the existing tokens: `--msb-*`, `--clr-*`
- Tests: `ManualTestingWorkspaceTests.cs`

## Tests
**Frontend, full suite: 9,899 / 9,899** (after these changes).

New:
- `UX107_the_P1_follow_up_opens_compact…` (fixture of 260 P1 scenarios over 6 specifications, one covered):
  - the KPI and the list are both 259;
  - the 6 group counts add up to 259;
  - every group starts collapsed with no rows;
  - the list renders after «Testplaner og mer innsikt»;
  - opening a group gives 10 rows, then «Vis flere» gives 30;
  - no P1 label on rows; identities unique.
- `UX107_search_and_filters…`:
  - search finds scenario no. 259 among all of them;
  - module → 99 in 2 groups; specification → 40;
  - reset gives 259 in 6 groups;
  - the project's own specifications stay their own group;
  - the filter is saved for the tab.
- `UX109_readiness_cards…`: the cards' states, meanings and actions; each card's count equals its list's count.
- `UX107_coming_back_restores…`: the open list, its filter and its open group are restored.

Updated, as intended by the brief:
- `R01_the_overview_is_ordered…`: the new order.
- `A_bar_shows_its_records…`: compact rows with «Åpne».

## Browser verification (isolated v1env35, M2LB)
PENDING: filled in from the run.

## Remaining limitations
- Test-method filter: not available per scenario in the overview (see above).
- «Forbered testtilfelle» from the follow-up list is not offered. Preparing goes through Finn og forstå, which opens the exact scenario, so a case is never created from the list.
- The follow-up workspace stays inline under «Viktig å følge opp» (compact and collapsed), not a separate page. A dedicated page would need its own route and state; not justified by the measured page height (see the browser results).
