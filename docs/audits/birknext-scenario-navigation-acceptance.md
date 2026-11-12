# Manual Testing UX consolidation — acceptance

**Install:**
- isolated Release build, API :5496 (database `birknext_mb`, real M2LB workspace, curated Skolenærvær guide with 21 scenarios), web :5497;
- Edge driven by Playwright;
- harness `scratchpad/mb/ux-accept.cjs`.

**Guard:** every request outside localhost was aborted (0 attempted). No proposal was created or changed by this acceptance.

## Browser (real data)

| Id | Brief steps | Check | Result |
|---|---|---|---|
| UX-01 | 1–3 | Manual Testing → Test preparation = Test Plan Suggestions, Test Data; no "Functional Test Scenarios" row anywhere in the sidebar | PASS |
| UX-02 | 4–6 | The old deep link `system-understanding?tab=tests` opens What to test (21 curated scenarios) and highlights System Understanding | PASS |
| UX-03 | 7–9 | A per-scenario "Prepare in Test Plan Suggestions" opens Discover with that scenario preselected; "Nothing is approved by selecting" | PASS |
| UX-04 | 10–13 | Three scenarios selected in What to test arrive selected | PASS |
| UX-05 | 14 | Discover is grouped by the guide's stages (Innsending i Altinn, MM validerer og mapper, Skoletjenesten tar imot, Svar og kvittering, Person og holdte rapporter, Utdanning …); no card grid | PASS |
| UX-06 | 15 | Filtering keeps the selection: "3 hidden by filters — still selected" | PASS |
| UX-07 | 16–17 | Suggested Test Plan shows the proposal, not the scenario catalogue | PASS |
| UX-08 | 18–19 | Review Test Cases shows expected results and reviewer controls | PASS |
| UX-09 | 20–21 | Test Data & Testability shows the linked asset (`skolenaervaer-child-default`) | PASS |
| UX-10 | 22–23 | Saved snapshots: the history is unchanged (same 4 ids and fingerprints before and after) | PASS |
| UX-11 | 24–27 | Compare Deliveries and Prepare for Azure DevOps open | PASS |
| UX-12 | 28–29 | Dashboard shows proposal facts, no scenario list | PASS |
| UX-13 | 30–31 | Recommended Workflow actions lead to real destinations | PASS |
| UX-14 | 32–33 | Direct route and browser refresh load Discover | PASS |
| — | 34 | Console errors | 0 |

**Responsive** (Discover, What to test, Suggested Test Plan, Saved Snapshots, Compare): no horizontal overflow, no control under 24 px, no text under 12 px.
- Final result: 5/5 widths PASS (1440/1280/1024/768/390).
- Two earlier runs failed on real defects, which were fixed: 11.5–11.8 px labels, and a 17 px checkbox target on What to test.
- Measurement rules:
  - a checkbox is judged by its clickable label, as in the task 20 acceptance;
  - `aria-hidden` decoration (the "?" icon of the shared Help link) is not counted as text.
- Screenshots: `scratchpad/mb/out/ux-*.png`.

## Scenario provenance (real)

The 21 scenarios are **curated** guide content: `skolenaervaer.guide.json`, revision 1, "in review — nothing published yet". Descriptions are the guide's own text. Expected results that the guide lacks show "No expected result"; nothing is fabricated.

## Synthetic (automated)

| Fixture | Test |
|---|---|
| C, D, E (alias gone in every view, deep link kept, custom additions unaffected) | `ManualTestingViewTests.The_redundant_Functional_Test_Scenarios_alias_is_gone…`, `…orders_existing_modules…`, sidebar test |
| F, G (one or several from What to test) | `TestPlanSuggestionsPageTests.Scenarios_selected_in_What_to_test…`, `SystemUnderstandingPageTests` (prepare action) |
| I, J (selection kept while filtering, explicit clear), primary action, empty-selection hint, grouping by stage | `Discover_groups_candidates_by_the_guides_own_stages_and_keeps_hidden_selections_visible`, `Discover_groups_candidates_without_a_stage_under_Other` |
| K, L (missing expected result / test data) | Discover row chips; `Filters_narrow_candidates…` |
| M, N (no baseline / alternative snapshots) | `An_unselected_baseline_is_warned_about_and_never_combined` |
| R, S, T (snapshots, review decisions, authorization unchanged) | existing TPS service/page tests (unchanged code paths) + browser UX-10 |

**NOT RUN:**
- H (selection kept after navigating away and back): selection is client state passed by URL; leaving the page does not keep it. This is by design and unchanged.
- O (project switch).
