# BirkNext V11 — manual-tester acceptance guide (2026-11-27)

**Status: NOT VERIFIED.** This is the script. No real testers have run it yet, and no results are invented here.

**Setup** (test lead):
- an isolated BirkNext with the M2LB package imported and one empty test plan «Akseptanse V11»;
- not the testers' normal data.

**Participants:** 2–3 manual testers who have not used BirkNext's Manual Testing before (or only briefly).

**Observer:** writes down time, steps, hesitation and questions. The observer does **not** help unless the tester is stuck for more than 2 minutes; such help is recorded as «assistanse».

## Tasks (read each task aloud; do not show the screen path)
| # | Task given to the tester | Done when | Expected path (observer only) |
|---|---|---|---|
| 1 | «Finn et scenario om kvittering fra meldingsutveksleren.» | The scenario's details are open | Finn og forstå → search «kvittering» → click the row |
| 2 | «Hva skal skje når dette testes? Forklar med egne ord.» | The tester states the expected result correctly | Detail panel: «Forventet resultat» |
| 3 | «Lag et testtilfelle for scenarioet.» | A case exists with the scenario as its source | «Forbered testtilfelle» → Planlegg og klargjør → Lagre |
| 4 | «Legg scenarioet i testplanen ‹Akseptanse V11›.» | The plan has the scenario as an occurrence | Testplaner → «Legg til scenarioer» (or via Planlegg og klargjør) |
| 5 | «Du får ikke testet fordi miljøet mangler. Registrer det.» | Result «Blokkert» with a comment is saved | Testgjennomføring → open the scenario → Blokkert → comment → «Lagre resultat» |
| 6 | «Finn igjen det du nettopp registrerte, fra Testoversikt.» | The tester reaches the blocked scenario from Testoversikt | Testoversikt → «Viktig å følge opp» / plan progress → open |

## What to record per task
| Field | How |
|---|---|
| Completed | yes / with assistance / no |
| Time | from the task being read to «done» (stopwatch) |
| Unnecessary steps | clicks or pages that did not move the task forward |
| Confusion points | where the tester hesitated more than ~10 s, or said «hvor…?» / «hva betyr…?» |
| Assistance | what the observer had to say |
| Defects | anything that behaved wrongly (with a screenshot) |
| Improvement proposals | the tester's own words |

## Result template (fill in after the session)
| Tester | Task | Completed | Time | Unnecessary steps | Confusion | Assistance | Defects | Proposals |
|---|---|---|---|---|---|---|---|---|
| T1 | 1 | | | | | | | |

## Acceptance criteria (proposal)
- **Tasks 1–6:** each completed without assistance by at least 2 of 3 testers.
- **No tester** believes «Blokkert» means «testet OK» (ask after task 5: «Er dette testet nå?»).
- **No defect** of severity P1.
