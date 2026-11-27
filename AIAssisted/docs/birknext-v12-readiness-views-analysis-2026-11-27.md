# BirkNext V12 — the three readiness views: analysis (2026-11-27)

**Decision (user, 2026-11-27): ANALYSIS ONLY.** The three models are not merged, and nothing in their semantics was changed.

## 1. The three models
| | A. «Kildegrunnlag» (source checkpoints) | B. «Klargjør AC» (scenario readiness) | C. «Klar til testing» / «Før godkjenning» (test-case readiness) |
|---|---|---|---|
| **Belongs to** | a scenario's stored evidence (and its case, when there is one) | a **scenario** (with or without a test case) | a **test case** |
| **Where shown** | Finn og forstå, detail panel («Kildegrunnlag n / 6», collapsed) | Finn og forstå and Planlegg og klargjør (panel «Klargjør AC»); Testoversikt readiness cards; Testgjennomføring tag «Klargjøring: …» | Testoversikt KPI «Klar til testing»; Planlegg og klargjør «Før godkjenning»; Overfør (export «Klar») |
| **Inputs** | `TestDesignAdvisor.Readiness`: stored checks (Kildegrunnlag, Forventet resultat, Testdata, Utløser, Verifiseringsmetode, Tilgang og miljø, Gjennomgang, Hindring) | the scenario's facts (`ScenarioPreparationEngine.FactsOf`) + the testers' **recorded decisions** per criterion (applicability and evaluation, append-only, tied to the scenario text's fingerprint) | the case's `Testability`, computed on the server (`TestPlanSuggestionEngine`) from the case's content: expected behaviour known, test data known, steps present, verification observed, no unresolved conflict, no recorded hindrance |
| **Rule** | 6 questions, each Yes / No / Unknown (worst of its checks); «source confirmed» needs the case to be reviewed | 6 criteria (forventet resultat, testdata, utløser, verifisering, miljø og tilgang, gjennomgang). Each is Påkrevd / Valgfritt / Ikke relevant / Ikke vurdert, evaluated Oppfylt / Mangler / Ikke vurdert. Status: **Mangler** (a required criterion missing) → «Trenger klargjøring»; else undecided / unevaluated / stale → «Trenger vurdering»; else **Klar** | `Ready = Blockers.Count == 0`. Blockers: prerequisite blocked, expected behaviour not established, test data not known, no steps, verification not identified, unresolved conflict |
| **A person decides?** | no (derived only) | **yes**: «Vurdert av tester» per criterion | no (derived from the case); approval is separate (BUG-004) |
| **Answers the question** | «What does the source and the stored data already establish?» | «Is this **scenario** ready to be executed, as judged by the tester?» | «Is this **test case** complete enough to run and to export?» |

## 2. Overlap
- **The same six themes in all three:** expected result, test data, trigger, verification, environment/access, review. The names differ slightly («Utløser» / «kjøremåte»).
- **Different subjects:**
  - A and C read the **case's** evidence;
  - B is the **scenario's** readiness and exists without any case. A scenario-only plan item has B, but no C.
- **Different authority:**
  - only B records a person's decision («Vurdert av tester»);
  - A and C are computed;
  - a B decision does not change C, and C does not set B.
- **They can legitimately disagree.**
  - A says «5 / 6» while B says «Trenger vurdering»: the tester has not assessed relevance yet.
  - C says «Klar» while B says «Trenger klargjøring»: the case is complete, but the tester recorded that the environment is missing.
  - Each is correct for its own question.

## 3. Risks of merging (why not now)
1. One status would hide **whose** judgement it is: a recorded human assessment (B) versus derived completeness (A, C).
2. A scenario without a case would lose its readiness (C does not exist for it), or a case would inherit a scenario decision it was not made for.
3. Exports (C) and execution guidance (B) would change meaning silently. The Data Quality KPI contracts (`metric.ready` = C) would no longer match.

## 4. Would a common presentation help? Recommendation
**Yes, a common presentation, not a common model.** One compact summary block wherever readiness is shown, with three labelled lines:
- «Kilden: 5 av 6 kontrollpunkter»;
- «Scenarioet: Trenger vurdering (2 av 5 påkrevde oppfylt) — vurdert av tester»;
- «Testtilfellet: Klar til testing / mangler steg».

Each line keeps its own rule and its own drill-down.

**Prerequisites before building it:**
- (a) agree the labels (the glossary already fixes «Klar til testing» for the case);
- (b) decide whether A should stay visible to testers at all, or move under «Teknisk»;
- (c) write tests that the three values are unchanged by the presentation.

**No code was changed by this analysis.**
