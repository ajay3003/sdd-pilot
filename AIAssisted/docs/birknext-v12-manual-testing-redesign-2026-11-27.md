# BirkNext V12 — Manual Testing redesign (2026-11-27)

**Basis:**
- the audit `birknext-v12-major-ux-audit-2026-11-27.md` (finding ids MT-*, APP-*);
- branch `008-traceability-first` after `9af21c93`.

**Status values:** IMPLEMENTED, VERIFIED, PARTIAL, BLOCKED, NOT IMPLEMENTED.
**Principle:** no business rule, KPI definition, source identity or stored data was changed. Every change is presentation or interaction.

## 1. Design decisions
1. **One scenario, one place to look.** A click anywhere on a scenario row opens it. With three columns the detail column shows it; narrower, the detail panel opens.
   - The list keeps keyboard focus: arrows, Space and Enter.
   - «Vis detaljer» still moves focus into the panel.
   - Closing returns focus to the row.
2. **One primary action per context.** In the scenario's details that is «Forbered testtilfelle» (or «Lag enda et testtilfelle»); everything else is secondary. The plan block no longer repeats the action.
3. **Row states are independent and never colour-only:**
   - **opened:** accent bar;
   - **selected for a bulk action:** dashed outline plus the ticked checkbox;
   - **in an active test plan:** tint plus the plan line's text.
4. **Creating and editing are separate steps.** While «Nytt testtilfelle» is open, the case editor is hidden. Its unsaved values are kept and return on «Avbryt».
5. **The hierarchy is visible where it matters:** Plan → Testgruppe (nested, indented by depth, with a scenario count) → Scenarioforekomst → valgfritt Testtilfelle («1 testtilfelle» / «uten testtilfelle»).
6. **Honest progress.** «x av y behandlet · n bestått · m blokkert (ikke verifisert)» replaces «Gjennomført: z %», which counted blocked as done.
7. **Assessment wording.** A tester's recorded readiness assessment is «Vurdert av tester». «Bekreftet» read as «oppfylt» even when the tester recorded a criterion as missing. A tester confirming a requirement **link** keeps «Bekreftet av tester», because that is a confirmation.

## 2. Implementation
| Finding | Status | Change | Tests |
|---|---|---|---|
| MT-1: details hidden below 1,344 px; a row click only highlighted the row | **IMPLEMENTED** | `OpenRowAsync`: the row opens its details at every width; Enter opens the active row. | `DiscoverWorkspaceTests` (updated: row click opens, close returns focus to the row) |
| MT-2: two primary «Forbered testtilfelle» in one panel | **IMPLEMENTED** | The plan block's action is secondary; no duplicate prepare action. | `ScenarioWorkQueuePageTests` (exactly one `.btn-primary` in the panel) |
| MT-13: Enter and focus return | **IMPLEMENTED** (partial a11y) | Enter opens; Escape/«Lukk» return focus to the row. **Not done:** focus trap in the panel; interactive controls inside `role=option`. | as above |
| MT-7: selected = bold only | **IMPLEMENTED** | Dashed outline for «selected». | browser check FF-selected-state-visible |
| MT-8: create and edit forms together | **IMPLEMENTED** | The editor is hidden while «Nytt testtilfelle» is open; its values are kept. | `V12_creating_a_case_hides_the_editor…` |
| MT-10: «Gjennomført: x %» counts blocked | **IMPLEMENTED** | «x av y behandlet …». | `V12_progress_says_how_many_are_handled…` |
| MT-4: no visible Plan → Suite → Occurrence → Case hierarchy | **PARTIAL** | Execution: nested groups indented by depth with counts, and a case tag per occurrence. **Not done:** a hierarchy tree in the Testplaner list (`SavedPlans`). | `V12_the_plan_hierarchy_is_visible…` |
| MT-3: three readiness models, inconsistent wording | **PARTIAL** | «Vurdert av tester» for recorded assessments. **Not done:** merging «Kildegrunnlag n/6», «Klargjør AC» and «Før godkjenning» into one summary. That needs a decision: they measure different things, and the source-checkpoint count is not readiness. | label covered by the full suite |
| MT-5: filters spread over six places | **NOT IMPLEMENTED** | A filter-toolbar consolidation (primary: Search, Priority, Status, Test method; the rest in «Flere filtre») changes many tested flows. Test method has no cross-specification server filter (V10 decision 5). | — |
| MT-6: competing grouping systems; groups start collapsed | **PARTIAL** | V10 P1: groups holding a Testoversikt selection open automatically. **Not done:** one grouping system; E2E as a direct list. | V10 P1 tests |
| MT-9: no direct «Legg til i testplan» from Finn og forstå | **NOT IMPLEMENTED** | Needs the «Legg til scenarioer» dialog with a pre-selection and an explicit destination suite (brief §14). The bulk bar still hands over to Planlegg og klargjør, which creates cases. | — |
| MT-11 / MT-12: wording, loading and empty-state variants | **NOT IMPLEMENTED** | Inventoried in the audit. | — |

## 3. Test results
- Frontend full suite **9,917 / 9,917** after the Finn og forstå, Plan and wording changes. Later targeted runs:
  - plan tests 119 + 1 new;
  - execution 5 → 6;
  - explorer / traceability / execution 110 / 110.
- Backend: not affected by V12 (no backend change).

## 4. Browser verification
See `birknext-v12-browser-ux-verification-2026-11-27.md`.
