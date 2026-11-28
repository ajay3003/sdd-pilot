# Technical Testing: implementasjon, første trinn (2026-11-28)

Rekkefølgen følger beslutningen: **først skillet mellom arbeidsflatene, så E2E-regelmotoren.**

## 1. Hva er laget

| Del | Fil | Innhold |
|---|---|---|
| Versjonerte regler | `shared/TestWorkClassification.cs` (lenket inn i API og Web) | `E2ERules` (`e2e-rules/1`): Bekreftet, Kandidat, Ikke E2E, Må avklares, og utførelse Manuell, Teknisk eller Hybrid, med begrunnelser. `TestWorkClassifier` (`test-work/1`): kategori, overflate og standardvisning per arbeidsflate. Bygger på `ScenarioOwnership`. Ingen konkurrerende klassifisering. |
| Dashboardlogikk | `Services/TechnicalTestingDashboard.cs` | Tall over den felles inventaren og de felles planene. Hvert tall har eksakte kandidat-ID-er eller (plan-ID, case-ID). Én identitet telles én gang. Et testtilfelle valgt «Manuell» telles aldri som teknisk. |
| Teknisk testoversikt | `Components/TechnicalDashboard.razor` på `technical-testing` | Kompakte KPI-kort uten liste rett på siden. Tall for scenarioforslag åpner Finn og analyser (`?work=`). Tall for testtilfeller åpner Planlegg og klargjør med nøyaktig de tilfellene. |
| Finn og analyser | `Components/TechnicalWorkList.razor` på `technical-testing/assess?work=…` | Nøyaktig kandidatene bak tallet, med kategori, E2E-status, utførelse, overflate og «Hvorfor» (evidens). Sidevis, 25 om gangen. En ukjent nøkkel utvides aldri til en liste. |
| Testplaner (teknisk) | `Pages/TechnicalTestingPlans.razor`, `technical-testing/plans` | Samme planer som i Manual Testing (ingen kopi), sortert etter antall tekniske tilfeller. «Åpne de N tekniske testtilfellene» gir eksakt utvalg. Planens kjøring merkes «alle scenarioer». |
| Navigasjon | `NavigationCatalog.cs` (Technical Testing-visningen) | Teknisk testoversikt · Finn og analyser · Planlegg og klargjør · Testplaner · Integrasjonstesting. «Verify & investigate» er en underflyt med lenke fra oversikt og Testplaner. Ingen meny for resultater nummer to. |
| Taksonomi | `FeatureVisibilityTaxonomy.cs` | `technical-testing/plans` er klassifisert som kontekstside. |

Ingen backendendring, ingen skjemaendring, ingen migrering og ingen flytting av poster. Klassifiseringen er filtrering på visningsnivå.

## 2. Tester lagt til

`frontend/BirkNext.Web.Tests/Services/TechnicalTestingSeparationTests.cs`, 9 tester:

| Krav | Test |
|---|---|
| Bare manuell | `An_acceptance_scenario_is_manual_business_work…` |
| Bare teknisk, API og GraphQL ut fra kontrakttype (aldri ut fra tittel) | `A_contract_operation_is_REST_or_GraphQL…` |
| Hybrid, synlig i begge | `Multi_system_flows_are_confirmed_E2E_and_hybrid…` |
| Faktisk E2E versus kontrakt- og komponenttest, kandidat versus bekreftet | `Declared_or_suggested_end_to_end_tests_are_candidates…` |
| Må klassifiseres, personens valg vinner, regelversjon | `An_unknown_producer_needs_classification…` |
| Tall i dashboardet, eksakte ID-er, ingen duplikat-flyt | `Every_figure_counts_exactly_its_records…` |
| Drilldown i dashboardet (lenke, eksakt utvalg til Planlegg og klargjør) | `Teknisk_testoversikt_shows_compact_figures…` |
| Drilldown i Finn og analyser | `Finn_og_analyser_lists_exactly_the_candidates…` |
| Ugyldig nøkkel utvides ikke | `An_unknown_or_case_figure_key_is_never_widened…` |
| Navigasjon med samme form, ingen duplikat | `The_Technical_Testing_view_has_the_same_shape…` |

**Kjøring:** målrettet sett (TechnicalTestingSeparation, FeatureVisibility, Navigation, TechnicalTesting, NavMenu) ga 328 av 328 bestått. Hele frontendsuiten (`-c V12Verify`): **9 965 av 9 965 bestått.** API-et bygger med den delte regelfilen (0 feil). Backend- og nettlesersuiter er ikke kjørt for denne endringen.

## 3. Ikke gjort ennå (gjenstår)

1. **Scope:** de tekniske tallene bruker prosjektets siste scenarioforslag.
   - Modul, spesifikasjon og dokument er ikke laget.
   - Regelen for medlemskap i scope for flyter på tvers av moduler er ikke laget.
   - Ingen nettlesertest av tekniske scope.
2. **Tekniske felt på testtilfeller:** mål, inngang, autentisering, nedstrøms forventning, observerbarhet og feil/retry. I dag gjelder testtilfellets felles felt og kandidatens mal.
3. **Kjøring per teknisk testtilfelle:** planens kjøring vises for hele planen. Kjørt, bestått og feilet per teknisk tilfelle krever sammenstilling av forekomst og tilfelle.
4. **Manual Testing standardvisning (punkt H):** Testoversikt teller fortsatt alle testtilfeller, også tekniske. Det er ikke laget en egen plass for manuell E2E. KPI-semantikken ble bevisst ikke endret uten egen verifisering.
5. **Integrasjonstesting:** knutepunktet er uendret. Deklarerte reiser er allerede «Bekreftet E2E» i regelmotoren, men det finnes ingen lenke fra Integrasjonstesting til Finn og analyser `?work=e2e`.
6. **Personens valg:** `ScenarioTestKind` fra StoryGrouping leses ennå ikke inn i det tekniske dashboardet. Valget på testtilfellet (`TestKind`) brukes.
7. **Nettleser:** ingen isolert nettleserreise for Technical Testing. Playwright-testene `TechnicalTestingWorkflowTests` og `NavigationWorkflowTests` er ikke kjørt etter menyendringen.
8. **Klassifisering av testtilfeller med kontraktidentitet:** et testtilfelle uten valgt type regnes som teknisk når scenarioet med samme kildeidentitet er teknisk. Det forutsetter at prosjektets siste scenarioforslag finnes.

## 4. Dataintegritet

- **Ingen lagrede poster endres.** Klassifiseringen beregnes ved lesing.
- **Planer dupliseres ikke:** Technical Testing viser samme `test_plan_drafts`.
- Kontinuitet mellom importer arver DEF-V12-01-rettingen (`6c88f6b4`). Begrensningen for importer uten kildekode gjelder også her.

## 5. GO/NO-GO

**NO-GO for full aksept.** Skillet og E2E-reglene er på plass og testet på enhetsnivå. Dette gjenstår før aksept:

- scope-kombinasjoner
- tekniske felt
- kjøring per tilfelle
- manuell standardvisning
- nettleserverifisering

Det er neste trinn.

## 6. V2 (2026-11-28): scope, klassifisering, testtilfeller, utførelse og separasjon

### 6.1 Faktiske hull før V2 (revidert)

1. De tekniske tallene hadde ikke scope.
2. Personens valg (Manuell/Teknisk/Begge) ble ikke lest av den tekniske oversikten.
3. Manual Testoversikt talte tekniske testtilfeller med.
4. Det fantes ingen tekniske felt på testtilfeller.
5. Utførelse fantes bare per forekomst og plan.
6. E2E-regelen sjekket ikke evidens for startpunkt og utfall.
7. Integration Testing hadde ingen kobling til den tekniske oversikten.

### 6.2 Endringer

| Område | Filer |
|---|---|
| Regler v2: hva, hvordan og hvor sikkert, med evidenssjekkliste, presedens og avvik | `shared/TestWorkClassification.cs` |
| Tekniske testtilfelle-maler og vakt mot hemmeligheter | `shared/TechnicalCaseTemplates.cs` (ny, lenket inn i API og Web) |
| Kontrakter | `ManualTestingDashboardContracts.cs` (`MethodDecisions`, `OverviewCase.Execution`/`ExecutedAt`), `ManualTestRunContracts.cs` (valgfrie felt på `ScenarioExecutionEvent` og `RecordScenarioResultRequest`). Alle er bakoverkompatible. |
| Backend | `TestPlanSuggestionService.Overview.cs` (metodevalg, siste resultat per testtilfelle), `.Execution.cs` (resultat per testtilfelle, validering, hemmelighetsvakt), `TestPlanSuggestionService.cs` (`Clean`: hemmelighetsvakt, plass til 40 malverdier) |
| Manual-populasjon og manuell E2E | `ManualTestingOverviewPresentation.ForManual`, `Pages/ManualTesting.razor` (området «Manuell E2E»), `wwwroot/css/manual-testing.css` |
| Teknisk oversikt v2 | `Services/TechnicalTestingDashboard.cs`, `Services/TestScopeReader.cs` (ny), `Components/TechnicalDashboard.razor` (scope, tre grupper, ugyldig scope), `Components/TechnicalWorkList.razor` (scope, på tvers av moduler, avvik, manglende bevis) |
| Tekniske testtilfeller | `Components/TechnicalCaseDetails.razor` (ny), `PlanPreparePresentation.CaseForm` (`Technical`), `Pages/ManualTestingPlan.razor` (vises bare for type «Teknisk») |
| Utførelse per testtilfelle | `Pages/TechnicalTestingPlans.razor` (resultat per teknisk testtilfelle, registrering med miljø, faktisk resultat, bevis og feilreferanse) |
| Integration Testing | `Pages/TechnicalTestingIntegrations.razor` (lenker til felles E2E) |
| B23 | `BrowserVerification/TestOverviewBrowserJourneys.cs` (`HistoryAsync`, se 6.5) |
| Inventar | `TestOverview/interaction-inventory.json` (`mto-e2e-open`) |

Ingen skjemaendring, ingen migrering, ingen identitetsmigrering og ingen ny plan- eller testtilfellemodell.

### 6.3 Gjenbrukte eksisterende mekanismer

- **Tekniske felt:** `TestCaseEdits.TemplateValues` (rensing, revisjon, historikk).
- **Resultater:** `ScenarioExecutionEvent` (`occ:`-poster).
- **Scope:** `SpecificationScopePicker` og `O.Scope`.
- **Moduler:** `ModuleStructure`.
- **Metodevalg:** StoryGrouping `TestKinds`.
- **Drilldown:** utvalgskontrakten `mtp-cases` til Planlegg og klargjør, og identitetsfilteret til Finn og forstå.

### 6.4 Kontinuitet mellom importer (DEF-V12-01)

- **Planer, testtilfeller, godkjenninger, kjøringshistorikk og kildeendring:** følger applikasjonen når importen har kildekode. Dekket av backend `DEF_V12_01_…` (V1 → V4) og nettleserreisen STALE-02. Tekniske felt og resultater per testtilfelle ligger i samme plan og følger med.
- **Personens metodevalg følger ikke med til en ny import** (lagret per import med kandidat-ID). **T14 er ikke oppfylt.** Å rette det krever at valget knyttes til kildeidentitet og applikasjon, en endring av StoryGrouping-lagringen. Det er ikke gjort uten godkjenning.
- **Importer med bare dokumenter:** ingen applikasjonsidentitet (uendret, se testoversiktrapporten kapittel 14).

### 6.5 B23 (tilbake/fram)

- **Bevis:** feilen var «waiting for navigation until "Commit"» i Playwrights `GoBack`/`GoForward` (kjøring 25).
- **Rotårsak:** `Layout/WorkspaceSaveIndicator.razor:42` registrerer en `LocationChangingHandler` på alle sider. Da angrer Blazor et tilbake-steg i nettleseren og gjentar det etter godkjenning. Playwrights venting på den startede historikknavigeringen fullføres da ikke alltid.
  - Rotårsaken er utledet av koden og feilmeldingen. Den er ikke bevist med instrumentering.
- **Brukeropplevelse:** brukeren ender på riktig side.
- **Endring:** testene tar samme handling med `history.go` og venter på adressen. Produktkoden er ikke endret.

### 6.6 Testmatrise (T01–T35): status

| T | Dekket av | Status |
|---|---|---|
| T01–T13, T17, T21–T28, T30, T32–T34 | Frontend `TechnicalTestingSeparationTests` (28 tester inkl. teori) mot eget orakel med eksakte ID-er | Bestått |
| T17–T20 (lagring, revisjon, resultat per testtilfelle, planaggregering, hemmeligheter) | Backend `V2_technical_case_details_…` (PostgreSQL) | Bestått |
| T15, T16, T31, T35 (endret og fjernet kilde, nytt grunnlag, eldre plan) | Backend `DEF_V12_01_…`, nettleser STALE-02 | Bestått (DEF-V12-01) |
| T14 (valget overlever ny import) | — | **Ikke oppfylt** (6.4) |
| T29 (bytte applikasjon) | V12 B22 og CASE-06 (manuell) | Teknisk variant ikke testet i nettleser |
| Nettleser for teknisk oversikt, Finn og analyser, tekniske testtilfeller og resultater per testtilfelle | — | **NOT VERIFIED.** Ingen isolert nettleserreise for Technical Testing ennå. Testdataene mangler kontrakt- og reisekandidater. |

### 6.7 M2LB

Reglene er validert mot mønstre fra M2LB-revisjonen: GraphQL-operasjoner, TEST-E og TEST-I, meldingstester og REST-kontrakter. Det er gjort med syntetiske kandidater i enhetstester, ikke ved å importere M2LB.

- Ingen M2LB-spesifikke regler er lagt inn.
- 43 GraphQL-operasjoner gir 43 GraphQL-kandidater, **ikke** E2E.
- Ekte import av M2LB i isolert vert er ikke gjort. Det er **NOT VERIFIED**.

### 6.8 Faktisk kjørt i V2 (2026-11-28)

| Kjøring | Resultat |
|---|---|
| Frontend `TechnicalTestingSeparationTests` (V2) | 28 / 28 |
| Hele frontendsuiten (`-c V12Verify`), etter V2 og oppdatert inventar | **9 983 / 9 983**. Første kjøring ga 9 982 / 9 983: inventarvakten fanget de nye kontrollene, og inventaret ble oppdatert. |
| Backend TestPlanning, ProjectImport, isolasjon, Specifications, ManualTestRun og StoryGrouping (isolert PostgreSQL) | **764 / 764** |
| Isolert nettleser, hele pakken via runner-skriptet (Testoversikt med ny manuell populasjon og E2E-område, STALE-02 med p1-source) | **44 / 44**, exit 0. Bygget før B23-endringen. |
| Isolert nettleser: B23, P1-SCOPE-05–07, P1-SCOPE-12, CASE-02 med `HistoryAsync`, gjentatt | Kjøring 1: 4/4 · Kjøring 2: 4/4 · Kjøring 3: 4/4 |

### 6.9 GO/NO-GO

**NO-GO.** Dette er oppfylt:

- regler v2
- presedens og avvik
- scope i de tekniske tallene og i Finn og analyser
- separate populasjoner
- tekniske testtilfeller
- resultat per testtilfelle
- manuelt E2E-område
- koblinger fra Integration Testing
- B23-rotårsak og testendring

Dette mangler før GO:

1. Isolert nettleserverifisering av Technical Testing. Testdataene mangler kontrakt- og reisekandidater.
2. T14: metodevalg følger ikke en ny import.
3. Teknisk «Planlegg og klargjør» er den felles editoren (manual-testing/plan) med tekniske felt. Det finnes ingen egen teknisk side.
4. Ekte M2LB-import i isolert vert.
