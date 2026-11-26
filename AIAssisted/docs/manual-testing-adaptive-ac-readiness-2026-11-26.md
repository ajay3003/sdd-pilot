# Manual Testing — Adaptive AC readiness («Klargjør AC») (2026-11-26)

Commit: `3205eacc` (+ the «Kildegrunnlag» rename in `7585de65`).

## A–C. Audit and the root cause of the rigid «2 / 6»
- `ScenarioWorkspacePresentation.Checklist`:
  - always returns 6 items, and the denominator is `checklist.Count`;
  - every item counts as required;
  - «Testtilfellet er gjennomgått og godkjent» is one of the six.
- «Kilde og forventet atferd er bekreftet» only became Yes when the case was approved.
- Everything derives from `TestDesignAdvisor.Readiness(TestCaseDraft)` (case-based). There was no persisted per-scenario preparation and nothing a tester could edit.
- So: a scenario without a case could never be ready, an approved case looked like readiness, and «Ikke vurdert» items could not be marked irrelevant.

## D–H. New model (`shared/ScenarioPreparation.cs`, `preparation/1`)
- **Criteria:** Forventet resultat, Testdata, Kjøremåte/utløser, Verifiseringsmetode, Miljø og tilgang, Godkjent testtilfelle.
- **Three independent parts:**
  - Applicability: Påkrevd / Valgfritt / Ikke relevant / Ikke vurdert;
  - Evaluation: Oppfylt / Mangler / Ikke vurdert;
  - Evidence and reason.
- **Defaults** (basis «Standard»):
  - expected result and verification are required;
  - case review is optional, because cases are optional;
  - test data, trigger and environment are «Ikke vurdert» with a **proposal** («Foreslått: Påkrevd — Kilden beskriver en utgangssituasjon …») that only counts once a tester confirms it.
- **Calculation:**
  - only required items form the denominator;
  - a missing required item gives «Trenger klargjøring»;
  - any undecided applicability, unevaluated required item or stale decision gives «Må vurderes»;
  - otherwise «Klar til kjøring»;
  - no required items shows «Ingen påkrevde punkter» (never 0/0);
  - «Ikke relevant» is never Oppfylt.
- **Case content is evidence, never a silent Oppfylt.** An approved case or a stated observation method prompts «bekreft». This keeps the status identical in every tab, whether or not the plan's cases are loaded.
- **Stale:** a decision made on an earlier scenario text (fingerprint) is kept, shown as «Må gjennomgås på nytt», and not trusted for «Klar».

## I–L. AC-centred, cases optional, standalone, human confirmation
- Decisions are stored per scenario source identity.
- Standalone documented tests store theirs in their own test document (`DocumentKey`); no story is invented.
- «Bruk forslagene for relevans» confirms every proposal in one save; each is still the tester's own decision.

## M–N. UI and persistence
- `ScenarioPreparationPanel.razor` (titled «Klargjør AC», or «Klargjør scenario» for a standalone test):
  - status, count, «Relevans må vurderes for N punkter», «N valgfrie forbedringer»;
  - groups Påkrevde / Relevans ikke vurdert / Anbefalt før testing / Ikke relevant;
  - each incomplete item has a real action («Angi testdata», «Beskriv verifisering» …) that opens an inline editor.
- Saves go through `StoryGroupingOperationKind.SetScenarioPreparation`:
  - append-only;
  - history entries with previous → new values;
  - `ExpectedRevision` concurrency: on a conflict the panel reloads and keeps the open editor;
  - «Ikke relevant» needs a reason, and secrets such as «password: …» are refused.
- Opening or closing the panel writes nothing.

## O–R. Placement
- **Planlegg:** the panel sits above «Vis teknisk analyse», which is now collapsed by default. The old «x / 6» is removed, so there is one count. Planned scenarios without a case get «Klargjør AC».
- **Finn og forstå:** the same panel in the drawer. The old checklist is renamed «Kildegrunnlag» and no longer claims execution readiness.
- **Testoversikt:** the chart «Klargjøring av scenarioer» (Klar / Trenger klargjøring / Må vurderes) is computed by the same engine on the server, with a drill-down.
- **Testplaner:** readiness per occurrence is **NOT IMPLEMENTED**. The execution page notes that readiness is advisory and never blocks.

## S. Migration
None: an additive document field. Old documents have no decisions, so they show the defaults and proposals. Nothing from the old checklist is converted into decisions.

## T–U. Tests
- **Backend:** `ScenarioPreparationTests` (7):
  - denominators, optional and N/A, no 0/0, cases as evidence;
  - per-part newest decision, stale handling;
  - validation and history;
  - facts and document key.
- **Frontend:** `ScenarioPreparationPanelTests` (5):
  - groups, Walkthrough A, suggestions, missing → «Trenger klargjøring» → «Klar»;
  - approved case ≠ ready, cancel, standalone, concurrency.
- Old readiness tests were updated to the new behaviour, not deleted. The hindrance-is-never-hidden protection is still asserted via «Før du starter».

## V. Browser
Run in v1env18 (`K-prepare`, `K-empty-plan-state`). See the results section of the walkthrough report `manual-testing-browser-walkthrough-v1env18-2026-11-26.md`.

## X. Remaining risks
- Testplaner occurrence readiness is not shown.
- A standalone test's decisions live in its test document, while review UI elsewhere may use the open specification's document.
- Proposals use simple text signals (Given present → data; message/API verbs → trigger), so they are proposals only.

## Y. Acceptance criteria
- **IMPLEMENTED:** AC01–AC24, AC27–AC29, AC31–AC38.
- **PARTIAL:** AC25/AC26 (test data and trigger are recorded as evidence text; there are no structured fields), AC30 (Testplaner not shown).
- **Held:** AC41–AC42.
- **Not held:** AC43, because commits were requested.
