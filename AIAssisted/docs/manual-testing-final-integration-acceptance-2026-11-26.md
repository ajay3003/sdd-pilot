# Manual Testing — final integration and browser acceptance — 2026-11-26

## 1. Commit and working-tree state

- **Branch and HEAD:** branch `008-traceability-first`, HEAD `2c1a3d78` (on `a784ac5d`).
- **Before this task:** only the three unrelated `shared-*-audit-2026-11-26.md` docs were untracked; there were 2 stashes.
- **During this task:** nothing was reset, stashed, committed or pushed.
- **Working-tree changes from this task (uncommitted, per the brief):**
  - `frontend/BirkNext.Web/Components/DataResetPanel.razor`: reset blocker fix, §7;
  - `frontend/BirkNext.Web.Tests/Components/DataResetPanelTests.cs`;
  - this report.

## 2. Frontend suite

**Full frontend suite after `a784ac5d`:** 9,804 passed / 0 failed / 0 skipped (run on the commit's tree; recorded in `2c1a3d78`).

**After the reset fix:**
- reset-related tests: 110 / 110 (`DataResetPanelTests` and every test matching «Reset»);
- the full suite is in §7 once rerun.

## 3. Backend regression

`TestPlanning`, `TestEvidence` and `ImplementationGap` on temporary PostgreSQL databases: **681 passed / 3 skipped / 0 failed**. The run includes:
- the Sprint 12 → 13 test;
- scenario-only plans;
- membership;
- translation reuse.

## 4. Browser acceptance matrix

**Environment:**
- an isolated instance `v1env13` published from HEAD;
- a disposable database `birknext_adoverify_20261126_122925`;
- ports 5097/5197;
- M2LB imported (380 s).

**How it was run:**
- the script was `scratchpad/v1/v1_acceptance.py`; its data was created through the normal API in the disposable database;
- screenshots are in `scratchpad/v1/v1env13/shots/acc-*.png`;
- the first run had 4 script errors, all corrected (§6), and was then **rerun in full**. The matrix below is the second run.

| ID | Workflow / check | Result | Observed |
|---|---|---|---|
| A1 | Finn og forstå row title in Norsk | PASS | `data-language=nb`, «NB: All 19 routed tables …» (a stored translation) |
| A2 | Drawer expected result in Norsk | PASS | «NB forventet: …» |
| A3 | Translation origin labelled | PASS | «Oversatt av en person» |
| A4 | Original view | PASS | «AC 2 — All 19 routed tables …» (no NB) |
| A5 | Sammenlign | PASS | the original shown beside it |
| A6 | Language kept from Finn og forstå to Planlegg | PASS | the editor opens in `Norwegian`; heading «AC 2 — NB: …» |
| A7 | US title in Norsk (Planlegg) | PASS | «NB brukerhistorie: …» |
| A8 | US description in Norsk | PASS | «NB beskrivelse» |
| B1 | «Uavhengig test» as steps | PASS | 1 step (action → expected) |
| B2 | No unsupported «dekket» | PASS | none |
| B3 | No meaningless fragments | PASS | no item under 3 words |
| B4 | «Ikke utviklertestdekning» note | PASS | present |
| B5 | Developer-test evidence accessible | PASS | «Hva er allerede kontrollert?» present |
| D1 | Preview shows unsaved editor values | PASS | «Ulagret tittel fra aksepttest» plus «Ulagret utkast» |
| D2 | No execution controls in the preview | PASS | none |
| D3 | Closing the preview keeps the edit | PASS | the field still holds the edit |
| D4 | The preview wrote nothing | PASS | revisions: Created, «1 scenario lagt til uten testtilfelle», Saved (setup translation), «Translated to Norwegian (bokmål)» (automatic translation on open, §5) — none from the preview |
| C1 | Scenario-only item listed | PASS | «… Scenario – ikke klargjort» |
| C2 | No case created by planning | PASS | no case for the planned scenario |
| D5 | Scenario-only preview | PASS | «Scenario – ikke klargjort. Ikke klart til kjøring.» |
| C3/C4 | Prepare a case later; no duplicate | **FAIL (open)** | «Forbered testtilfelle» was **disabled** at the moment of the check, so prepare-later and the duplicate check were not reached in the browser. They are covered by the backend test (one case, the item kept, no duplicate). See §5. |
| E1 | No execution outside Testplaner | PASS | no result controls in Finn og forstå, Planlegg or the preview |
| E2 | Counts in Testplaner | PASS | «3 scenarioer · 0 bestått · … · 3 ikke kjørt» |
| E3 | A case is an instruction | PASS | «Tilhørende testtilfeller (instruksjoner, ingen egen gjennomføring)»; no step checkboxes |
| E4 | Record Bestått at AC level | PASS | status Passed; «1 bestått … 2 ikke kjørt» |
| E5 | Results per plan | PASS | the same scenario: P1 = NotRun, P2 = Failed |
| E6 | Recording writes no plan revision | PASS | revision 4 → 4 |
| F1 | Copy preview and reset statement | PASS | «… 1 testgrupper · 3 scenarioer (1 uten testtilfelle) · 2 testtilfeller», «Testresultater kopieres ikke …» |
| F2 | New ids, same tree | PASS | suite ids disjoint, names equal |
| F3 | Scenarios and cases copied | PASS | 2 cases, 1 scenario-only item |
| F4 | All Ikke kjørt | PASS | 3/3 NotRun, no history |
| F5 | Source sprint result kept | PASS | Sprint 12 AC still Passed |
| F6 | No approval copied | PASS | none Accepted |
| G1 | «Åpne alle N» selects exactly N | PASS | «Åpne alle 5 i Finn og forstå» → 5 rows |
| G2 | Left story list matches the subset | PASS | 1 story in the list = 1 story in the rows |
| G3 | Grouping keeps the subset | PASS | subset kept |
| G4 | Explicit module change clears it | PASS | AutorisasjonService → CdcReplay: subset gone |
| H1 | Turquoise for active membership | PASS | both plan scenarios turquoise |
| H2 | Real names, several plans | PASS | the plan line with «+N» |
| H3 | Neutral without a plan | PASS | «Ikke i testplan» |
| H4 | Archived only | PASS | neutral, «Kun i arkivert testplan» |
| H5 | The copied plan is counted | PASS | the plan line present |
| K1 | Keyboard group toggle | PASS | Enter → `aria-expanded=true` |
| L | No horizontal overflow (Finn og forstå and Testplaner at 1440 and 390) | PASS ×4 | `scrollWidth` = viewport |
| Console | Errors after login | PASS | 0 (no 401s in the second run) |

**Summary (second run): 46 PASS / 1 FAIL (C3/C4).** The specification-change part of G is covered by bUnit S04, not repeated in the browser. Workflows A, B and D–H: **VERIFIED in the browser.**

## 5. Findings

1. **C3/C4: «Forbered testtilfelle» disabled (OPEN, investigating).**
   - The button is disabled while the editor has unsaved changes or the page is busy (`disabled="@(_busy || Dirty)"`).
   - In the run, the same case had just received a stored partial Norwegian translation (title and «Then» only, via the setup save), and the automatic translation completed afterwards. The leading hypothesis is that the editor considered the case changed after that. Confirming it needs the data again: the reset reproduction in §7 emptied the disposable database.
   - It will be re-run on a fresh import and fixed only if confirmed.
2. **Automatic translation on open writes a plan revision (existing, by design, NOT changed).**
   - Opening Planlegg translates *missing* Norwegian texts with the configured offline translator and saves them («Translated to Norwegian (bokmål)»). This was delivered deliberately earlier («Manglende oversettelser lages automatisk når testplanen åpnes»).
   - It conflicts with the later translation brief's «no revision on page load». **This needs a product decision** and is not changed here.

## 6. Script errors corrected (not product defects)

- **A preview check (first run):** it filled the original-language title field while the editor was in Norsk mode. It now selects Original first.
- **Membership checks (first run):** the search text from A1 is kept for the session by design and hid the rows. The script now clears it.
- **Plan names:** made unique per run, so the copy lookup cannot pick an earlier run's plan.

## 7. BLOCKER (reported during this task): «Reset everything does not delete test plans»

- **Reproduced on the isolated instance, 8 plans:**
  - «Reset everything» without the box ticked: **Completed, 8 plans remained**;
  - with «Also remove manual test plans» ticked: **Completed, 0 plans**, with a backup written first.
- **Root cause:** `DataResetPanel.razor` started the opt-in **unticked**, so «Reset everything» silently kept every plan unless the tester noticed the box. The server side (backup, reviewed-count check) works.
- **Fix:** in «Reset everything» the box starts **ticked** («N manual test plans will be removed. A backup is written first.»). The tester can untick it to keep them. Other reset modes always keep plans. The server-side rules are unchanged.
- **Tests:** `DataResetPanelTests` updated to the new rule (default removal in «Reset everything», untick → plain request, scope change returns to each scope's default); **110 / 110** reset-related tests pass.
- **Not committed** (this brief: no commits).

## 8. Remaining blockers

- C3/C4 (§5.1), under investigation.

## 9. Known future gaps (unchanged)

- per-checkpoint developer-test assertion links;
- execution filters beyond status and a selected-sprint dashboard widget;
- preview from Testplaner;
- adding scenario-only items to an *existing* plan from Finn og forstå, with a suite choice (your recommended next step);
- per-row «Testplankobling må avklares»;
- dark theme in BirkNext's own theme switch.

## 10. Recommendation

**Conditional GO** for the verified workflows: A, B, D–H in the browser; C by backend tests.
- **Before full GO:** C3/C4 must be confirmed in the browser (re-run pending).
- **Product decision needed:** automatic translation on open (§5.2).
