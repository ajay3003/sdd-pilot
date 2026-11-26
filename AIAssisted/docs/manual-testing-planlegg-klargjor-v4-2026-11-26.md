# Manual Testing — Planlegg og klargjør v4 (+ UX redesign) (2026-11-26)

Commits: `ff6bdc69` (layout, step editor, sticky toolbar, preview), `3205eacc` (proposal relevance, step warnings, Klargjør AC), `89c2ca2a` (empty plan), `ea9d3aca` (compact context).

This report also covers the earlier brief «Planlegg og klargjør — complete UX redesign» (`manual-testing-planlegg-klargjor-ux-redesign-2026-11-26.md` was not written separately).

## K/L. Suggestion relevance — your first priority — FIXED
- **Root cause:**
  - `TestDesignAdvisor.SecurityVariants`, `Repetitions` and `StateSequences` matched on `ScenarioFacts.All`, which includes the case's composed and edited steps and its title.
  - A word like «Kode 6/7» or «tilgang» anywhere there produced «… skjermet barn (Kode 6/7)», and the rationale claimed «Kilden omtaler skjerming».
- **Fix:**
  - proposals fire only on the scenario's own source words (`ScenarioFacts.SourceText`, Given/When/Then; `Boundaries` already did this);
  - the rationale quotes the triggering source sentence («Scenarioets kilde sier: «…»»);
  - rejected and deferred proposals keep their decision store (unchanged).
- **Tests:** `R01_a_proposal_needs_the_scenarios_own_source_words…`, using the Barn/Person example; all 82 advisor tests still pass, including the source-stated access proposals.

## E/F. Step quality — IMPLEMENTED
- Generation no longer writes «Trigger and observe» (`AcceptanceScenarioInterpreter`, earlier).
- For saved cases, there is a per-step non-destructive warning «Teststeget bør konkretiseres» with reasons:
  - generic action;
  - source states no trigger;
  - missing action or expected result;
  - result not observable.
- Steps are never rewritten and approval is untouched (`PlanPreparePresentation.StepWarning`, `StepQualityWarningTests`).

## H/I. Readiness and approval
«Klargjør AC» (adaptive readiness) is separate from case approval; the old «2 / 6» is gone. See the readiness report.

## C/D. Layout
- Wider master–detail (up to 110 rem).
- Step editor with «Handling» and «Forventet resultat» side by side on desktop (stacked under 1,100 px), plus insert, duplicate, reorder, remove and Ctrl+Enter.
- Sticky action toolbar with an «Ulagrede endringer» indicator, and a compact readiness chip list.
- Compact scenario context: neutral surface with a thin accent, smaller title, two-line description, «Vis scenariodetaljer».
- Technical analysis «Vis teknisk analyse» is collapsed by default.

## O. Preview
- **Root cause of the broken preview:** the modal CSS was missing, so it rendered inline.
- Also fixed:
  - a false «unsaved» state for partial translations (`SameList`);
  - prepare-later used the current discovery instead of the plan's.
- **Browser (v1env17):**
  - `C-preview-open` PASS (fixed modal, focus, unsaved title shown);
  - `C-preview-escape` PASS (edit kept);
  - `C-discard` PASS;
  - C3/C4 prepare-later PASS (one case, no duplicate).

## Empty plan — FIXED
«Velg et testtilfelle» was a dead end. Now:
- «Ingen scenarioer i testplanen ennå» with «Legg til scenarioer / Finn scenarioer / Gå til Testplaner»;
- or «Ingen testtilfeller … Testtilfeller er valgfrie».

## Browser layout (v1env17)
- `C-steps` PASS (1 → 4 via duplicate, insert, Ctrl+Enter).
- `C-sticky-toolbar` PASS.
- No overflow at 1920/1440/1280/1024/390.
- The script's side-by-side measurement reported false, but the screenshot `c-step-editor-1440.png` shows the two columns. The measurement was a script artifact.

## Tests
Planlegg suites 106/106, full frontend 9,833/9,833.

## Remaining
- Test data and verification have no structured fields beyond the readiness evidence text (PARTIAL).
- Manual vs. technical guidance is unchanged.
- Performance of keystrokes was not measured (NOT MEASURED).
