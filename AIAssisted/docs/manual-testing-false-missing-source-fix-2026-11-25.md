# Manual Testing — false «Kilde mangler» warnings (2026-11-25)

Branch `008-traceability-first`. Committed locally as you asked («commit ALL when done»); nothing pushed, no reset or database cleanup, both stashes kept.

**Status words used:**
- **CONFIRMED DEFECT** — reproduced in a test or traced in the code.
- **IMPLEMENTED** — built in this task.
- **VERIFIED** — checked by a test run.
- **NOT VERIFIED** — not checked yet.

## A. Starting state

- The tree was clean after `aa622ce0` (Manual Testing UX).
- The same working tree also holds the automatic-translation work (`manual-testing-automatic-translation-2026-11-25.md`), committed together with this.

## B–C. Root cause (CONFIRMED DEFECT, traced in the code and reproduced in tests)

### 1. Per-case status read the CURRENT SELECTION, not the case's own source

**What happens:**
- `TestPlanSuggestionService.FlagProject` compares every saved case with `CurrentFor(plan)`, the project's latest discovery.
- Since automatic loading (2026-11-25), a discovery runs for whichever specification is selected in Finn og forstå.
- With Frontend 002 selected, the latest discovery holds only Frontend scenarios.
- Each SkoleService case was then not found, and `Absence(...)` returned `SourceUnavailable`.
- That shows as «Kilde mangler», with the reason «spesifikasjonen … ble ikke lest i siste oppdagelse — en annen spesifikasjon var valgt».

**Result:** a status taken from the dropdown, not from the case.

### 2. The plan status inherited it, plus two more selection leaks

- **The unavailable count:** `StatusAsync` counted those cases, giving «N testtilfeller kunne ikke sammenlignes fordi kilden mangler» and the state «Kilde mangler».
- **`basisChanged`** compared basis fingerprints. The fingerprint includes the **selected specification** (`specFingerprint`), so choosing another specification also read as «Planen er laget fra en annen kildeversjon».
- **`NewScenarios`** counted **every** scenario of the latest discovery. That explains the ~560 «nye scenarioer» for a SkoleService plan while Frontend was selected.

### 3. The page added its own warning

- `ManualTestingPlan.razor` showed «Testplanen hører til en annen spesifikasjon» whenever the plan's specification differed from the one selected in Finn og forstå (`OtherSpecifications`).

## D–E. The fix: per-case source resolution (IMPLEMENTED, VERIFIED by tests)

**Each case is compared with a discovery of its OWN specification** (`OwnSpecificationDiscovery`):
- the current discovery when it read that file;
- otherwise the newest **current** discovery of the **same source version** (same member content fingerprints, same application) that read it.

**What is never used:**
- another source version;
- a historical inspection;
- a title or US/AC-number match. Identity stays `SourceIdentity` and the specification path, as before.

**Guard:** when the current discovery **failed** to read specifications, nothing is borrowed from an older one. The failure is reported as before («Kilde mangler: … kunne ikke leses»).

**Discoveries of other specifications are restored after a restart**, once per project (`ScenarioDiscoveryStore.RestoreScopesAsync`). They come from the stored results the store already keeps (5 per project), so nothing is re-parsed.

**New state `CaseSourceState.NotCompared`** («Ikke sammenlignet», neutral):
- used when no discovery of the case's specification exists for the current source version;
- it is not «Kilde mangler» and not a warning;
- it is not part of the impact analysis (Kildeendringer).

## F. Status meanings after the fix

| Situation | Case status | Plan status |
|---|---|---|
| Own specification read, scenario unchanged | Gjeldende (no label) | Klar |
| Own specification read, scenario changed | «Kildegrunnlag endret» | Må gjennomgås |
| Own specification read completely, scenario gone | «Utgått scenario» | Må gjennomgås |
| Another specification selected, own spec read earlier for this source version | compared with that read (any of the above) | as above |
| Another specification selected, own spec never read for this version | «Ikke sammenlignet» (neutral) | «Ikke sammenlignet» when all cases are, else «Klar» with «N … ikke sammenlignet» |
| Specification read failed / analyzer failed / basis incomplete | «Kilde mangler» (unchanged) | Kilde mangler |

- **«Annen kildeversjon»** now means other **source content** (`ScenarioDiscoveryStore.Content`), never another selected specification.
- **«Nye scenarioer»** counts only scenarios of the plan's own specifications.

## G. The page

- «Testplanen hører til en annen spesifikasjon» is gone.
- In its place, one neutral line: «Inneholder testtilfeller fra SkoleService (001-skoleservice).», or «… fra N spesifikasjoner: …» for a plan with several.
- `SourceLabel` shows nothing for `NotCompared`; the source panel shows «Ikke sammenlignet».

## H. Plans with several specifications (VERIFIED by a test)

- A plan holding a SkoleService case and a Frontend case resolves each against its own specification.
- The SkoleService case stayed «Gjeldende»; the changed Frontend case became «Endret».

## J. Data safety

- **Read-time flags only:** nothing is rewritten.
- **Opening a plan writes no revision:** checked in a test (revision unchanged).
- **No ownership change:** no case is remapped.
- **Refresh («Oppdater planen»):** still supersedes only on a real `Removed`; `NotCompared` cases are left as they are.

## K–L. Tests

**Backend** (`SourceDrivenPlanningTests`, PostgreSQL, a disposable database per class):
- a SkoleService plan opened while Frontend is selected: all `Current`, plan «Klar», 0 unavailable, 0 new scenarios, revision unchanged;
- a real change and a real removal in the own specification are still found while another is selected;
- no discovery of the own specification: `NotCompared`, the reason «Ikke sammenlignet …», never «Kilde mangler»;
- a plan with two specifications, each case resolved independently.

`SourceRemovalRulesTests` was updated: «another specification» is now `NotCompared`.

TestPlanning + TestEvidence + reset suites: **633 passed / 3 skipped / 0 failed**.

**Frontend:**
- new test: the plan source line, no «Kilde mangler», no «annen spesifikasjon» banner, and a real removal still labelled «Utgått scenario»;
- full suite **9,726 passed / 0 failed** (9,725 before this test, plus this one).

## M. Browser verification

**NOT VERIFIED yet.** It will be checked in the isolated walkthrough of the shared-scope change, which touches the same pages.

## O. Remaining limitations

- **Never read for this source version:** if the case's own specification was never read for the current source version, the case is «Ikke sammenlignet» until it is selected once in Finn og forstå.
- **Impact analysis:** the Kildeendringer impact analysis still compares with the current discovery only. Cases of another specification are left out of it (not reported as missing).
- **Refresh:** «Oppdater planen fra gjeldende kilde» still refreshes from the current discovery. The safer preview-first update from the Planlegg brief is not done yet.
