# Manual Testing — performance, chart drill-down and final regression hardening (2026-11-26)

This follows your assessment of the 2026-11-25 round: no new large rebuild, but fixing the slow Testoversikt and the non-clickable charts, checking the last source links, and running the full regression. Branch `008-traceability-first`, committed locally only (nothing pushed). There was no data reset or database cleanup, and both stashes are kept. Every browser check ran in an isolated BirkNext with its own database and App_Data, with M2LB.zip imported; your database and settings were not used.

## 1. Testoversikt speed

**Measured before the change** (old build, isolated M2LB, three test plans with 120 cases):

| Opening | Time to key figures |
|---|---|
| Reload of `/manual-testing` | 11.05 s |
| From Finn og forstå inside the app | 1.42 s and 0.99 s |

**Where the time went.** It was in the browser, not on the server: the overview request itself took 0.18–0.26 s. On a reload:
- about 6.7 s is BirkNext's general start (app load, workspace bootstrap and documents). This is common to every page, not specific to Manual Testing;
- about 2.5 s went to the browser reading the selected specification's full scenario discovery (2,000 scenarios on M2LB). Testoversikt did not use it; only the Testplaner tab needs it;
- the overview, the dashboard settings and the execution setting were requested one after another, and only after the discovery.

**Change** (`Pages/ManualTesting.razor`):
- Testoversikt reads the light workspace (the project and its plans, without discovery or requirements);
- the overview, dashboard settings, execution setting and shared selection are read at the same time;
- switching to Testplaner reads the full workspace once, so «Ta med scenarioer valgt i Finn og forstå» still works there (tested).

**Measured after the change** (same data volume, new build):

| Opening | Time to key figures |
|---|---|
| Reload of `/manual-testing` | 2.65 s and 2.61 s |
| From Finn og forstå inside the app | 0.37 s and 0.40 s |

**Honest caveat.** The general start (bootstrap and documents) was also faster in the second run, because the machine was less loaded (the M2LB import took 120 s instead of 447 s). The part this change removed can be shown directly in the request timelines:
- the 2.5 s gap after `discover/last` (that request is no longer made);
- the sequential chain of three requests.

The remaining reload time is BirkNext's general start, not Testoversikt.

## 2. Chart drill-down

Every bar and every «Viktig å følge opp» line can now be clicked (button, keyboard and screen-reader name such as «P1: 325. 325 scenarioer. Vis hvilke.»). It opens a panel with **exactly the records the bar counts**. The panel uses the same definitions as the bar (`ManualTestingOverviewPresentation.DrillDown`), so they cannot disagree.

**Scenario bars** (Testdekning, Scenarioer etter prioritet, Manglende testdekning, P1-scenarioer uten testtilfeller):
- the scenarios are listed per specification, with module and specification named;
- each group has «Åpne alle N i Finn og forstå»; each scenario can also be opened on its own.

Opening does three things:
1. chooses the scenarios' module and **exact specification** in the shared selection (the same one the picker sets);
2. hands over the scenario identities as Finn og forstå's filter;
3. opens Finn og forstå, which shows «Utvalg fra Testoversikt: P1 uten testtilfeller · 5 av 5 scenarioer» and «Fjern utvalget».

If a scenario of the selection is not in the current proposals, it is counted and said («… finnes ikke i forslagene for denne spesifikasjonen nå»), never hidden. If the specification cannot be chosen, **nothing opens** and the reason is shown. A general page without the right selection is never opened (tested).

**Coverage bars** also offer «Vis oversikten for <modul/spesifikasjon>», which narrows Testoversikt to that module or specification.

**Case bars** (Testforberedelse, Testbarhet og blokkeringer, Kildeendringer, Manuelle og tekniske, Godkjenning per modul, Oversettelser):
- the cases are listed per test plan;
- each case links to Planlegg og klargjør **on that case** (`?draft=…&case=…`).

## 3. «Barnet finnes ikke» and «Uten modultilknytning»

The scenario is a guide scenario: no user story and no `spec.md#…` reference, so its specification was not known. Its stored evidence (read from the isolated database) does cite a file **inside a specification folder**: `SkoleService/specs/001-skoleservice/contracts/rapportinntak.md`.

New shared rule `shared/SpecificationFolderEvidence.cs`:
- a cited file inside a Spec Kit specification folder names that folder's `spec.md`;
- all cited specification folders must agree;
- source-code and test paths never decide it, and the title is never used.

It is used for attribution only: the module on Testoversikt and «Inneholder testtilfeller fra SkoleService (001-skoleservice)» in Planlegg. It is never used for source comparison: a scenario that was not read from the specification is not compared against it. The scenario's real references are a regression test.

## 4. Translation when opening a plan

**Opening is not blocked.** The plan and its first case are shown at once (within 5 s, including page load, in the isolated run). Missing Norwegian text is translated in the background, and «Oversetter …» is shown meanwhile.

**Defect found and fixed.** While measuring, translation of the 20- and 40-case plans failed with «Kunne ikke oversette» («The offline translator returned a different number of texts»).

Root cause:
1. A request was cancelled after it was sent. In the walkthrough, the browser closed while «Oversetter …» ran; for a tester, this happens when they leave or reload the page.
2. The engine stopped waiting but left the helper running.
3. The helper then wrote its late answer, and the next request read that answer as its own.
4. Every later translation failed until the helper was restarted.

Fix (`OfflineTranslationEngine.RequestAsync`): a cancelled request now stops the helper, so its late answer is never read; the next request starts the helper again.

New test `A_cancelled_translation_never_hands_its_late_answer_to_the_next_one`, run with the real packaged model:
- fails without the fix with exactly the error seen in the browser;
- passes with it.

A direct reproduction outside BirkNext confirmed that the helper itself returns the right number of texts.

**Not re-verified in the browser:** the fix was found after the walkthrough build was published, so it is verified by the real-model test, not by a second browser run.

## 5. Tests

**Frontend: 9,742 passed / 0 failed** (full suite). New tests:
- every bar and follow-up line opens exactly as many records as it shows, at all three scope levels;
- Testoversikt does not read the discovery, but Testplaner does;
- a bar opens Finn og forstå on the exact module, specification and scenario identities;
- when the specification cannot be chosen, nothing opens;
- a case bar links each case;
- an overview selection in Finn og forstå shows exactly its scenarios;
- the guide-scenario source line.

**Backend:**
- Test planning and test evidence: **580 passed / 3 skipped / 0 failed**.
- Offline translation, with the real model: 19 / 19.
- New tests: `SpecificationFolderEvidenceTests`, and the cancelled-translation test.
- The full suite: see section 7.

## 6. Isolated browser walkthrough (new build)

Run on a fresh database with M2LB imported and three plans of 60, 40 and 20 cases.

| Check | Result |
|---|---|
| D1 «P1 uten testtilfeller»: the list has as many scenarios as the bar (325 = 325, 36 specifications) | PASS |
| D2 Finn og forstå opens on the group's exact specification with «Utvalg fra Testoversikt … 5 av 5» (1.8 s) | PASS |
| D3 Finn og forstå shows exactly the 5 scenarios | PASS |
| D4 «Fjern utvalget» returns to the normal view | PASS |
| D5 «Opprettet»: 120 = 120 cases, each a link to its case | PASS |
| D6 The case link opens Planlegg on that case | PASS |
| D7 Keyboard: Enter on a focused bar opens it, and focus moves to the list | PASS |
| D8 390 px: no horizontal scroll with the panel open | PASS |
| Q No browser console errors after the launch code is entered (the four 401s before it are the sign-in page itself) | PASS |
| R No API 5xx | PASS |

## 7. Full backend regression

Run alone, from the test build, with at most two tests in parallel. The test infrastructure opens only temporary databases with a random suffix (`TestPostgres`) and refuses the configured `birknext` database.

**Full suite (final code of this delivery): 7,530 passed / 10 failed / 20 skipped, 7,560 tests, 18 min 55 s.**

The 10 failures are all outside Manual Testing:
- controller authorization (Active Event, Altinn testing);
- local session and local trusted execution;
- the local deployment boundary;
- the Skolenærvær one-click flow;
- the source-upload pipeline;
- the API review connect callback;
- the HTTPS proxy;
- GraphQL performance request handling.

All of them **pass when rerun on their own** (the same build: 14/14 test cases including theory rows, 12 s). They depend on local HTTP listeners and timing, and failed only under the full suite's parallel load. They are not regressions from this change, which touches none of those areas.

The skipped tests are opt-in real-archive and real-model tests (`[SKIP]` without their environment variables). The offline-translation real-model tests were run separately with the packaged model: 19/19.

Memory stayed above 4 GB free throughout (sampled every 30 s).

## 8. Changed files

- **Frontend:**
  - `Pages/ManualTesting.razor`: light loading, requests in parallel, drill-down panel, narrowing;
  - `Components/DeliveryBarChart.razor`: optional selectable bars; other dashboards are unchanged;
  - `Services/ManualTestingOverviewPresentation.cs` (`DrillDown`);
  - `Services/ManualTestingDrillNavigation.cs` (new);
  - `Services/ScenarioBrowserPresentation.cs` (`Identities` / `Selection` filter);
  - `Pages/ManualTestingDiscover.razor` (selection line);
  - `Services/PlanPreparePresentation.cs`;
  - CSS.
- **Shared:** `shared/SpecificationFolderEvidence.cs` (new; linked in both projects).
- **Backend:**
  - `TestPlanSuggestionService.Overview.cs` (attribution);
  - `OfflineTranslation/OfflineTranslationEngine.cs` (the fix).
- **Tests:** `ManualTestingWorkspaceTests`, `PlanRevisionDiffTests`, `SpecificationFolderEvidenceTests`, `EmbeddedOfflineTranslationTests`.

## 9. Remaining limitations

- **General start:** a reload's remaining ~2.6 s is BirkNext's general start (workspace bootstrap and documents), shared by every page.
- **Drill-down when proposals differ:** it opens the scenarios that Finn og forstå's proposals for the specification contain. If discovery has not been run for that specification yet, Finn og forstå runs it once (as before) and then shows the selection.
- **Translation fix:** verified with the real model in a test, not in a second browser run.
