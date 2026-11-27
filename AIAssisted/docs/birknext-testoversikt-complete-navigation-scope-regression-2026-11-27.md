# Testoversikt: scope, navigasjon og regresjonsvern

## Sammendrag

Arbeidet retter konkrete scope-feil i Testoversikt, gjør scope-dropdownene til eneste scopekontroller og legger til regresjonstester for prosjekt-, modul-, spesifikasjons- og dokumentvalg. «Alle»-valg blir stående etter at et valg er gjort. En kildeidentitet som ikke finnes i valgt scope gir nå tomt resultat/diagnostikk, ikke data fra et bredere scope.

Dette er **ikke en fullført godkjenning av hele den etterspurte V12-matrisen**. Reell browser E2E mot en isolert instans ble ikke kjørt, og mange interaksjon × scope-kombinasjoner mangler fortsatt eksplisitte tester. Anbefaling: **NO-GO for full release acceptance** inntil disse hullene er lukket.

## Baseline og avgrensning

- Arbeidsgren: `008-traceability-first`, HEAD ved inspeksjon: `a4432a84`.
- Separat `v12-ux-bundle`-worktree og `feature/openspec-isolated-route` ble ikke endret.
- Endringene er frontendpresentasjon, scopevalg og frontendtest. Ingen backendreset, testplan, sak, godkjenning, historikk eller kildeidentitet ble skrevet.
- Denne endringen kan checkpoint-committes uten releasegodkjenning. Full Testoversikt-aksept er fortsatt NO-GO; browsermatrise og uavhengig kontroll × scope-avstemming gjenstår.

## Bekreftede feil og rettelser

1. `SpecificationScopePicker` viste i enkelte tilstander bare en placeholder; etter valg forsvant valget for å gå tilbake til «Alle». Valglogikken kunne også automatisk velge et unikt artefakt igjen etter at bruker hadde tømt valget. Picker/context har nå eksplisitt tomt valg for modul, spesifikasjonspakke og dokument, med reset av avhengige barn og bevaring av gyldige foreldre.
2. Dashboardets `_level` var en egen tilstand ved siden av dropdownvalget. Endring av dokument/pakke kunne derfor gi utdatert scope. Dropdown-endring leses nå på nytt og bestemmer nivået; de parallelle «Hele prosjektet / Valgt modul / Valgt spesifikasjon»-knappene og duplisert scopeetikett er fjernet.
3. Eksakt spesifikasjon ble filtrert etter bane uten å avskjære en motstridende modulforelder; ugyldige kombinasjoner kunne vise feil populasjon. Filtreringen bruker nå snittet av valgt modul og spesifikasjonsidentitet. Manglende/stale spesifikasjon blir tomt resultat med forklaring.
4. Prosjektnivå hadde tom modulnøkkel som kunne forveksles med fravær av scope. Tom modulnøkkel mappes nå til eksisterende prosjektomfattende gruppe.
5. KPI-/drilldownidentiteter ble deduplisert på tvers av dokumenter med samme identitetsstreng. Nøklene inkluderer nå kildebane der disse aggregatene bygges. Prioritets- og manglende-dekningstellinger/drilldowns utelater krav-only-kandidater.
6. P1-listen viste lokale modul-/spesifikasjonsfiltre som kunne gi inntrykk av å kunne utvide foreldrescope, og viste for mange grupper samtidig. Filtrene arver scope, gruppene starter lukket, filtrering skjer før paginering og gruppeseksjonene har eksplisitt sidekontroll. Flere follow-up-lister har «vis flere» i stedet for stille `.Take(...)`-avkutting.

## Scope- og KPI-kontrakter som er dekket

- Prosjekt alene: alle records i prosjektets API-svar.
- Modul: records hvis modulnøkkel samsvarer med den valgte modulen.
- Spesifikasjon: eksakt kildebane og eventuell modulforelder må begge samsvare.
- Spesifikasjonspakke: modul og feature-folder må begge samsvare.
- Ugyldig forelder + barn eller manglende spesifikasjonsidentitet: tomt resultat.
- Tømming av modul: tømmes spesifikasjon og dokument; prosjektpopulasjon gjenopprettes.
- Tømming av spesifikasjon: modul beholdes, dokument tømmes.
- Tømming av dokument: modul og pakke beholdes.
- `Godkjent` og `Klar til testing` forblir separate case-mål. Scenarioforslag, testtilfeller og planforekomster er separate domener. Ingen backend-KPI-kontrakt ble endret.
- Prosjektplaner vises fortsatt som prosjektomfattende planer; dashboardets plan-KPI er planer med saker i valgt slice. Dette blandede plan-/source-scope-grensesnittet er ikke fullstendig løst eller browser-validert.

## Interaksjonsinventar og varig kontroll

`AIAssisted/frontend/BirkNext.Web.Tests/TestOverview/interaction-inventory.json` registrerer eksakte sett av stabile `data-testid`-verdier i Testoversikt-siden og scope-picker. Regresjonstesten sammenligner ID-sett (ikke bare antall), avviser duplikate inventar-ID-er og krever felter for komponent, type, oppførsel, overgang, relevante scope, parametere og testmapping for oppførte kontrollkontrakter. Dette dekker **ikke ennå** alle interaktive markup-elementer: inventaret har 10 eksplisitte kontrollkontrakter, mens sidens dynamiske kontroller ikke alle har kontrakt eller egen scope-mapping. En ny interaktiv kontroll uten `data-testid` oppdages heller ikke automatisk. Guardens dekning er derfor reell, men delvis.

### Scopekombinasjoner

| ID | Kombinasjon | Status | Hva som finnes |
|---|---|---|---|
| S01 | Prosjekt alene | NOT VERIFIED | BUnit viser prosjekt-KPI og modulrader; ingen uavhengig API-/browseridentitetskontroll. |
| S02 | Prosjekt + modul | NOT VERIFIED | BUnit kontrollerer KPI/søylediagram og modulrader for syntetisk utvalg. |
| S03 | Prosjekt + modul + spesifikasjon | NOT VERIFIED | BUnit kontrollerer grupper og et testtilfelleantall for eksempelutvalg. |
| S04 | Fullt hierarki med dokument | NOT VERIFIED | Service-/picker-tester dekker dokumentidentitet/rydding, ikke API og destination records. |
| S05 | Prosjekt + spesifikasjon | NOT VERIFIED | Presentasjonsservice har test for eksakt kildebane uten valgt modul. |
| S06 | Prosjekt + dokument | NOT VERIFIED | Ingen eksplisitt UI/API-kombinasjonstest. |
| S07 | Prosjekt + modul + dokument | NOT VERIFIED | Ingen eksplisitt UI/API-kombinasjonstest. |
| S08 | Prosjekt + spesifikasjon + dokument | NOT VERIFIED | Ingen eksplisitt UI/API-kombinasjonstest. |
| S09 | Prosjektnivåspesifikasjon | NOT VERIFIED | Tom modulnøkkel er mappet til eksisterende prosjektgruppe; ingen hel brukerreise. |
| S10 | Uten modultilknytning | NOT VERIFIED | Gruppeetiketter/visning har unitdekning, ikke eksakt browser-populasjon. |
| S11 | Bytte applikasjon | NOT VERIFIED | Ingen kontrollert API/browsertest med to applikasjoner. |
| S12 | Gyldig scope uten treff | NOT VERIFIED | Manglende spesifikasjon gir tom diagnostikk; null-resultat i full scope er ikke ende-til-ende verifisert. |
| S13 | Ugyldig kryssprosjekt-/kildekombinasjon | NOT VERIFIED | Frontend service-test bekrefter tomt snitt ved motstridende modul; API-avvisning er ikke implementert/verifisert. |
| S14 | Stale/fjernet kildeversjon | NOT VERIFIED | Stale-baseline banner finnes, men identitets-/drilldownkonsekvens er ikke ende-til-ende verifisert. |
| S15 | Prosjekt uten hierarkinivå | NOT VERIFIED | Separate discovery-tester finnes; Testoversikt-prosjektmatrisen er ikke kjørt. |

Ingen rad er merket PASS fordi aksept krever eksakte identiteter gjennom API, UI og drilldown.

### Kontroll × scope

En full generert kontroll × scope-matrise er fortsatt **ikke levert**. JSON-filen dekker 10 sentrale kontrollkontrakter, men dekker ikke alle 152 stabile ID-er som funksjonelle kontroller, og gir ikke evidens for alle 15 scopes per relevant kontroll. Derfor finnes ingen gyldig total på antall verifiserte kontroll/scope-par.

## Endrede filer

- `AIAssisted/frontend/BirkNext.Web/Pages/ManualTesting.razor`
- `AIAssisted/frontend/BirkNext.Web/Components/SpecificationScopePicker.razor`
- `AIAssisted/frontend/BirkNext.Web/Services/Explorers/ArtifactExplorerContext.cs`
- `AIAssisted/frontend/BirkNext.Web/Services/ManualTestingOverviewPresentation.cs`
- `AIAssisted/frontend/BirkNext.Web/wwwroot/css/manual-testing.css`
- `AIAssisted/frontend/BirkNext.Web.Tests/Components/SpecificationScopePickerTests.cs`
- `AIAssisted/frontend/BirkNext.Web.Tests/Pages/ManualTestingWorkspaceTests.cs`
- `AIAssisted/frontend/BirkNext.Web.Tests/Services/MultipleDocumentsPerFeatureTests.cs`
- `AIAssisted/frontend/BirkNext.Web.Tests/Pages/TestOverviewInteractionInventoryTests.cs`
- `AIAssisted/frontend/BirkNext.Web.Tests/TestOverview/interaction-inventory.json`
- `AIAssisted/docs/birknext-testoversikt-complete-navigation-scope-regression-2026-11-27.md`

## Tester og resultater

- Målrettet scope-/picker-/oversiktstest: **79 bestått, 0 feilet** (`SpecificationScopePickerTests`, `ManualTestingWorkspaceTests`, `MultipleDocumentsPerFeatureTests`).
- Aktuell målrettet kjøring i checkpoint-forberedelsen: **80 bestått, 0 feilet, 0 hoppet over**, inkludert inventarvakten.
- Inventarregresjonstest: **1 bestått, 0 feilet**.
- Samlet kjøring av målrettede frontendtester, inventarvakt og de to repository-guardene: **82 bestått, 0 feilet**.
- Hele frontendtestprosjektet, to siste fullkjøringer etter traversal-endringen: **9 945 bestått, 0 feilet, 0 hoppet over per kjøring**. Tidligere kjøringer hadde to feil, `Production_source_has_no_acceptance_or_dataset_special_cases` og `Repository_never_contains_an_external_dataset_archive`; en senere kjøring hadde bare den første feilen. Tidligere kjøringer brukte quiet logger, som kun viste testnavn og ikke underliggende assertion-/exception-tekst. Repository-guardene traverserte hele genererte byggtrær før de filtrerte ut `bin`/`obj`; de traverserer nå mapper med tidlig ekskludering av `bin`, `obj`, `artifacts`, `node_modules`, `.git` og `App_Data`. Det var en reell effektivitet-/robusthetsfeil, og begge guardene ble raskere. Det er likevel ikke bevist at dette var årsaken til de opprinnelige røde resultatene: minst én feilet kjøring kom også etter første traversalendring. De opprinnelige detaljmeldingene ble ikke fanget, og den nøyaktige feilkilden er fortsatt usikker. Begge testene passerer nå sammen, og siste fullkjøring var grønn.
- Relevante backend `Services.TestPlanning` tester: **607 bestått, 0 feilet, 0 hoppet over**. De opprettet og fjernet tilfeldige testdatabaser. Før dette feilet en build fordi `BirkNext.Api.dll` var låst av en annen prosess; den prosessen ble ikke stoppet. Kjøringen med `--no-build` passerte.
- Testprosjektet dekker ikke backend API-validering for alle dokumentkombinasjoner eller uavhengig datakilde-rekonsiliering. Ingen forventede ID-sett for alle scopekombinasjoner er sammenlignet mellom kanonisk kilde, API, UI og drilldown.

## Browserverifisering

**BLOCKED / NOT VERIFIED.** Ingen `BIRKNEXT_PLAYWRIGHT_*`-konfigurasjon var til stede. Eksisterende self-start-fixture bruker faste porter 5000/5173 og setter ikke `BIRKNEXT_APP_DATA_DIR`; en Playwright-instans kunne dermed ikke oppfylle kravene om egne porter og separat App_Data. Den lokale PostgreSQL-containeren `birknext-studio-local_postgres_1` er den vanlige utviklingscontaineren og ble ikke brukt til browser-kjøring. Standardutviklingsinstansen eller dens data ble ikke åpnet eller endret. Ingen skjermbilder eller browser-logg foreligger.

### Ny infrastrukturkontroll

Kontrollen 2026-11-27 fant ingen `postgres`, `initdb` eller `pg_ctl`-binær i PATH. Playwright-prosjektets eksisterende fixture kan koble til en forhåndsstartet backend/frontend eller starte prosesser på faste utviklingsporter; den oppretter ikke en isolert database/App_Data. API-testene har EF InMemory-støtte, men bruker `WebApplicationFactory`/TestServer in-process. Det finnes ennå ingen loopback-bro eller felles browser-host som eksponerer denne testserveren til en faktisk Playwright-side. Prosjektet `BirkNext.Web.PlaywrightTests` refererer API-prosjektet uten compile-time output og har ikke `Microsoft.AspNetCore.Mvc.Testing`/EF InMemory-avhengighetene. Derfor kunne jeg ikke bevise at en browser-tilgjengelig API bruker bare en disposable database og midlertidig App_Data. FAIL CLOSED: ingen browser-host ble startet, ingen browserreise ble kjørt, og den vanlige PostgreSQL-containeren/utviklingsinstansen ble ikke brukt.

Aktuell målrettet frontendkjøring i denne fortsettelsen: **80 bestått, 0 feilet, 0 hoppet over** (scope-picker, ManualTesting workspace, flere dokumenter og inventory guard). Dette er ikke full frontendregresjon og oppfyller ikke browser- eller kontroll × scope-kravene.

Dermed er ikke viewport/zoom, faktisk fokusrekkefølge, back/forward, raske scopebytter, destination records eller full B01–B24-matrise verifisert i browser.

## Gjenstående arbeid og begrensninger

- Bygg deterministiske isolerte API-fixtures med to applikasjoner, flere kildehierarkier, dupliserte synlige ID-er og baselinevarianter.
- Inventer alle faktiske interaktive kontroller, inkludert dynamisk viste kontroller, med stabile ID-er og forventet record-populasjon.
- Generer og kjør hver relevant kontroll × scope-kombinasjon; verifiser eksakte kanoniske ID-sett, totalsummer, URL-parametere og destination records.
- Valider ugyldige/stale API-kombinasjoner eksplisitt. Gjeldende frontendoversikt hentes prosjektvis og slices klientbasert; dokumentfilteret er ikke en egen API-forespørsel.
- Legg til uavhengige KPI-referanseberegninger og source-import-rekonsiliering.
- Kjør isolert browsermatrise B01–B24, inkludert keyboard, 200 % zoom, responsive mål, empty/error states og race conditions. Bekreft at alle slike tester er mock/disposable og ikke skriver mot vanlig `App_Data`.
- Skaff detaljert testresultatlogg dersom policytestene feiler igjen; de tidligere stille kjøringene lagret ikke den konkrete assertion-/exception-meldingen.

## Git-status ved rapportering

Arbeidskatalogen inneholder også eksisterende, ikke-relaterte endringer: `AIAssisted/backend/BirkNext.Api.Tests/Diagnostics/TestHostMemoryDiagnostics.cs` og den tidligere utrackede `AIAssisted/docs/birknext-v12-final-verification-2026-11-27.md`. De er ikke del av denne oppgaven og må ikke stages med den. Endringsfilene ovenfor er ikke committed. Ingen push.

**Releasevurdering: NO-GO** for full V12-aksept inntil komplett kontroll × scope-matrise, uavhengig forventnings-orakel/reconciliation og browser B01–B24 i fullt isolert runtime foreligger. Full frontend og relevant backend testkjøring er grønne per siste kjøring, men tidligere policytestfeil mangler fanget detaljert feilmelding.
