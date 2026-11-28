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
