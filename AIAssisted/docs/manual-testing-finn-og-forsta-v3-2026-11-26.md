# Manual Testing — Finn og forstå v3 (2026-11-26)

Commits: `ff6bdc69`, `3205eacc`, `7585de65` (+ CRLF fix commit).

## D. What the «Scenarioer uten brukerhistorie» group really holds (M2LB, isolated v1env17, default view: 668 items)

| Kind | Count | Verdict | Change |
|---|---|---|---|
| Requirements without an acceptance scenario (FR-…) | 649 | Not scenarios | Own group «Krav uten akseptansescenario» (`ScenarioGrouping.ValueOf`, test `Requirements_without_a_scenario_…`) |
| Acceptance scenarios of HendelseService/specs/001 (### Scenario 1–6, no user stories in that spec) | 6 | Genuinely standalone ACs | Kept; no story invented |
| Acceptance scenarios of TjenesteService/specs/004 under **struck-through** User Story 3 and 5 | 7 (+2 strike-through items) | **Source interpretation defect:** the source removed them | Dropped (`SpecStoryHeadings.RemovedSections`, `SpecificationEvidenceBuilder`; tests `RemovedStoryScenarioTests`) |
| Documented TEST-E tests | 8 (6 visible under the default method filter) | Standalone documented E2E | Kept (declared E2E) |

None of the 102 / 668 were assumed to be E2E. The breakdown came from the real discovery (`v1_nostory.py`, read-only).

## E–F. Classification and Markdown E2E
See `manual-testing-ac-level-classification-and-grouping-2026-11-26.md`. Explicit documented E2E: 8. Suggested E2E: 3 candidates from other providers. ACs state no level («Uavklart»).

## G–H. Readiness and the «16/16» contradiction — FIXED
- **Root cause:** the drawer's «Testberedskap N / M — Klar/Ikke klar til kjøring» was a *source checkpoint* list, while execution readiness was judged elsewhere. Two assessments shared one name.
- **Fix:** the list is now «Kildegrunnlag — kontrollpunkter i kilden oppfylt · klar til kjøring vurderes i «Klargjør AC»», and «Klargjør AC» (the adaptive readiness engine) is the only execution readiness. Test `D01_the_drawer…` asserts that no execution claim is made.

## Default grouping and collapse — VERIFIED
Brukerhistorie → Scenariotype, everything collapsed (`A-default`), story summaries (`A-story-mixed-types`).

## Plan membership, drill-down subset, translations
These were preserved from the earlier work (turquoise active-plan rows, plan names, archived-only not highlighted, «Åpne alle N» constrains both panes), and their tests still pass.

## I–L. UX redesign (compact story navigation, row density, drawer restructure, filter toolbar)
**NOT IMPLEMENTED in this pass.** The drawer already shows one primary «Forventet resultat» (existing test D01). Technical design is under «Vis teknisk analyse».

## R. Performance
Measured in the app-wide report (route sweep).

## S. Tests
Discover suites 440/440, full frontend 9,833/9,833. Backend: `RemovedStoryScenarioTests` 2/2.

## W. Status
- **VERIFIED:** grouping defaults/collapsed, standalone scenarios, story summary.
- **IMPLEMENTED:** the 102/668 analysis, the removed-story fix, requirement grouping, the 16/16 fix.
- **NOT IMPLEMENTED:** the visual row and drawer redesign, and filter toolbar consolidation.

## Follow-up (2026-11-26): row density — IMPLEMENTED, MEASURED
- The secondary facts of a scenario row (area/priority, scenario type, requirements, plan membership) now share one wrapping line under the title instead of four stacked lines. Nothing is removed. CSS only (`.mtw-ac-row .msb-row-main`).
- Measured in the isolated browser (v1env24, M2LB, flat list, first 40 rows): average row height **110 px → 83 px**. Screenshots: `scratchpad/v1/v1env24/shots/rows-before.png`, `rows-after.png`.
- Also since v3:
  - shared «Vis flere» with «Viser X av Y» (progressive results V1);
  - search finds row 2,000 of 2,000 at once;
  - compact discovery payload (75 % of the bytes).
- **Still NOT IMPLEMENTED:** drawer restructure and filter-toolbar consolidation. Both need a design decision; the drawer already leads with «Forventet resultat» and keeps technical design collapsed.
