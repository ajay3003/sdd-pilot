# Testoversikt: scope, navigasjon og regresjonsvern

Oppdatert 2026-11-27 (kveld): isolert nettleserverifisering, scope-lekkasje, P1-/klargjøringsnavigasjon og prosessvurdering.
**Siste status 2026-11-28: se kap. 13.**
Tidligere checkpoint: `1f12ada6` («testoverview: checkpoint scope and navigation hardening»).

## 1. Kort oppsummert

| Område | Status |
|---|---|
| Isolert nettlesertest-host (egen database, egen App_Data, egne porter) | **Bygget og bevist i kjøring** |
| Scope-lekkasje (tall for bredere utvalg enn velgeren viser) | **Rotårsak funnet og rettet**, bevist i nettleser |
| P1-oppfølging og «Start vurdering» som lange inline-lister | **Fjernet**: navigerer til Finn og forstå med eksakt populasjon |
| Uavhengig orakel (forventede ID-er beregnet fra fixture, ikke fra produksjonskode) | **Bygget**; brukt i alle nettleserreiser |
| Nettleserreiser B01–B24 + P1-SCOPE + READY | Kjørt i isolert host, se kap. 7 |
| Testplaner/testtilfeller i fixture | **Bygget 2026-11-28** (kap. 13): 2 planer, 8 tilfeller i alle tilstander via ekte plan-API. Kildeendret tilfelle: se DEF-V12-01 |
| Oppfølging av testtilfeller (godkjenning, ikke klar, blokkert, kildeendring) | **Navigerer til Planlegg og klargjør** med eksakt (plan, testtilfelle)-utvalg (kap. 13) |
| Runner-skript | `AIAssisted/scripts/run-testoverview-browser-verification.ps1` (kap. 13) |
| Åpent avvik DEF-V12-01 | Planer/tilfeller følger ikke applikasjonen til en nyere import (kap. 13.6) |
| PostgreSQL-spesifikk dekning | Ja: hosten bruker ekte PostgreSQL (midlertidig database), ikke EF InMemory |

**Anbefaling: fortsatt NO-GO for full V12-aksept**, men nå av smale og navngitte grunner (kap. 10), ikke av manglende infrastruktur.

## 2. Test-host-arkitektur

Valgt strategi: **Option A, utvidet test-host** (ingen ny infrastruktur, ingen Docker/Podman-oppstart, ingen installasjon).

- **API:** testassemblyens egen `WebApplicationFactory<Program>`. Dermed gjelder testassemblyens eksisterende vern, som lukker ved feil:
  - `TestDatabaseIsolation`: egen database `birknext_testrun_<12 hex>` per testprosess på den lokale serveren. Enhver annen database stopper oppstarten (`DatabaseConnection.EnsureAllowed`). Databasen merkes og slettes ved avslutning.
  - `TestAppDataIsolation`: midlertidig App_Data per prosess.
  - Ekte Npgsql, ekte migreringer, ekte controllere og middleware.
- **Loopback-relé:** en Kestrel på ledig port (aldri 5000/5001/5173/5097) sender hver nettleserforespørsel uendret til in-process-serveren. Socket-peeren er loopback, og ingen `X-Forwarded-*` legges til (API-et avviser relayed requests).
- **Frontend:** BirkNext.Web-bygget serveres fra byggets `staticwebassets.runtime.json` på en egen ledig port. Ingen publisering, og ingen fil på disk endres. Bare den serverte `appsettings.json` får `BackendUrl` satt til reléet. Miljøspesifikke appsettings serveres ikke.
- **Nettleser:** Chromium via Microsoft.Playwright. Ny kontekst per reise, og alle forespørsler til andre origins avbrytes og registreres. Hver reise feiler ved konsollfeil, 5xx eller blokkert forespørsel. Ved feil lagres skjermbilde og trace.
- **Fixtures:** importeres gjennom den ekte Project Import-siden (støttet brukervei), ikke seedet bak API-ets rygg.
- **Kjøring:** `Category=IsolatedBrowser`. Uten `BIRKNEXT_ISOLATED_WEB_ROOT` rapporteres reisene som SKIPPED med forutsetningen, aldri som bestått.

Filer: `AIAssisted/backend/BirkNext.Api.Tests/BrowserVerification/` (`IsolatedBrowserHost.cs`, `TestOverviewFixture.cs`, `TestOverviewBrowserSession.cs`, `TestOverviewBrowserJourneys.cs`).

## 3. Isolasjonsbevis

`IsolatedBrowserHost.ProveIsolation()` sjekker den **faktiske** runtime etter oppstart, ikke bare miljøvariablene. Ingen nettleserstep kjører hvis én sjekk feiler:

- databasenavnet `AppDbContext` åpnet = kjøringens `birknext_testrun_<hex>`, og alle seks øvrige kontekster åpnet den samme
- databasen er ikke utviklerens database
- App_Data-roten og launch-code-filen ligger i temp og ikke under utviklerens App_Data
- portene er ikke utviklerportene
- hosting-miljøet er `Test`
- serilog-logger går til kjøringens egen mappe, ikke API-prosjektets `./logs`

Testen `I00_isolation_is_proven_from_the_effective_runtime` rapporterer det samme. Utviklerinstansen på 5000/5173 ble ikke stoppet, lest eller endret. Bygging skjedde til en egen `--artifacts-path`, slik at den kjørende instansens `bin/` ikke ble låst.

## 4. Databasebegrensninger

Hosten bruker **ekte PostgreSQL** i en midlertidig database, ikke EF InMemory. Npgsql-oversettelse og migreringer kjøres derfor ekte. Dette dekkes ikke: samtidighet/låsing mellom flere brukere, og ytelse med store reelle datamengder (fixture er ~45 scenarioer per applikasjon). Testene krever at den lokale PostgreSQL-serveren kjører. Er den nede, lukker hostene ved feil, og ingen reise kjører.

## 5. Rotårsak: scope-lekkasje (rettet)

**Symptom:** Tester så «397 P1-scenarioer / 42 grupper» fra flere moduler selv om en modul eller spesifikasjon var valgt.

**Rotårsak** (`ManualTesting.razor`): nivået (`_level`: prosjekt/modul/spesifikasjon) var en egen tilstand ved siden av det delte valget. `ReadScopeAsync()` kjører ved åpning, omlasting, tilbake-navigering og hver ny henting av oversikten. Den kunne bare **senke** nivået, aldri heve det:

1. Når Testoversikt ble åpnet med lagret modul eller spesifikasjon, viste velgeren valget, men `_level` sto på Prosjekt. **Alle** tall, diagrammer og P1-listen gjaldt hele prosjektet. Nettleserreise B23 fant dette: etter tilbake sto modulen Billing i velgeren, men tallene var prosjektets (47 i stedet for 4).
2. Med en valgt spesifikasjonspakke uten enkeltdokument (`_pack` satt, `_specPath` null) falt nivået til modul ved neste lesing. Nettleserreise B11 fant dette: 7 i stedet for 5.

**Retting:** nivået utledes ett sted, fra det delte valget som nettopp er lest (`ReadScopeAsync`). Den parallelle `SetLevel`-tilstanden er fjernet. `NarrowAsync` rapporterer nå en innsnevring som ikke ble anvendt, i stedet for å overstyre nivået. KPI-semantikk er ikke endret.

## 6. Navigasjonskontrakt (P1-oppfølging, «Start vurdering», KPI-fliser)

Inline-listene er fjernet fra Testoversikt:

- P1-linjen («N P1-scenarioer mangler testtilfeller»)
- «Klargjøring av scenarioer»: «Start vurdering» / «Klargjør»
- KPI-fliser

Ett felles prinsipp gjelder: **klikk på et tall eller en handling → arbeidsvisningen åpnes i samme scope med eksakt populasjon.**

- Destinasjonen er **Finn og forstå** (`manual-testing/discover`). Vurdering av klargjøring gjøres i scenarioets «Klargjøring»-panel der. Planlegg og klargjør håndterer testtilfeller, ikke scenarioklargjøring. Det er derfor bevisst ikke valgt som mål.
- Filteret er `Identities` = nøyaktig de kildeidentitetene metrikken talte, og `Priority = P1` for P1-linjen. Scopet endres ikke, for det delte valget er allerede felles.
- Destinasjonen anvender filteret selv. Mangler en identitet, sier siden det (`mtd-overview-selection-missing`) og viser aldri andre scenarioer.
- Filteret lagres per fane, slik at omlasting og fram/tilbake beholder det (verifisert i P1-SCOPE-12).
- Tom populasjon: knappen er deaktivert, og ingenting åpnes.
- Saksbaserte linjer (P1 som venter på godkjenning, ikke klare testtilfeller, blokkeringer, kildeendringer) beholder sin kompakte saksliste. De er ikke scenariolister og har ikke en filterkontrakt i Planlegg og klargjør ennå (kap. 10).
- Kontroller som bare ble brukt av de gamle inline-listene, vises ikke lenger for disse populasjonene. Søk og lokale filtre lever videre i diagramlistene (prioritetssøyler osv.), der de fortsatt brukes.

Mindre tekst: ingressen er kortet til én linje. Forklaringsavsnittet under «Klargjøring av scenarioer» er flyttet til verktøytips på overskriften. Den overflødige merknaden under sprintplaner og den dupliserte overskriften «Viktig å følge opp» i kortet er fjernet.

## 7. Resultater (kjørt i denne økten)

### Nettleser, isolert host (Chromium, ekte PostgreSQL-testdatabase)

> **Sluttkjøring (kjøring 3, etter commit 93896e64): 36 av 36 bestått, 0 feilet, 0 hoppet over, 6 min 18 s**: I00, B01–B24, P1-SCOPE-01…15 (sammenslåtte reiser) og READY-01. Første fulle kjøring av B01–B24 (før rettingene) ga **21 bestått og 3 feilet**: B03 (feil testforventning), B11 og B23 (reelle scope-feil, rettet i kap. 5).

### Frontend (bUnit) – målrettet

`ManualTestingWorkspaceTests`, `SpecificationScopePickerTests`, `MultipleDocumentsPerFeatureTests`, `TestOverviewInteractionInventoryTests`, `ManualTestingOverview*`, `ManualTestingDrill*`: **79 bestått, 1 feilet**.

- Feilen er `R06_the_stylesheet…`: den leser `manual-testing.css` via en fast relativ sti fra standard `bin/`-layout og finner ikke filen fra den isolerte artifacts-mappen. CSS-en er ikke endret, og testen passerte ved checkpoint. Den er altså layout-avhengig, ikke en regresjon.
- Fem tester som beskrev den gamle inline-oppførselen er skrevet om til den nye kontrakten og er strengere enn før: de sjekker overleverte identiteter mot fixturens egen konstruksjon, ikke bare antall. Det gjelder UX107 ×3, UX109 og T03.

Full frontend- og full backendsuite er **ikke kjørt etter disse endringene** i denne økten. Historiske tall (9 945 / 607) er ikke ny evidens.

## 8. Kontrollinventar og kontroll × scope

`interaction-inventory.json` har nå 12 kontrollkontrakter, inkludert `mto-tile-open`. Hver kontrakt peker på konkrete nettleser- og bUnit-tester. ID-vakten passerer.

Dekning per kontroll:

| Kontroll | S01 | S02 | S03 | S04 | Annet |
|---|---|---|---|---|---|
| `ssp-module` / `ssp-feature` / `ssp-specification` (velg, tøm, Spesifikk→Alle→Spesifikk) | B01 | B02, B09 | B03, B10 | B04, B11 | rask endring B13, P1-SCOPE-14 |
| P1-oppfølging → Finn og forstå (eksakte ID-er) | P1-SCOPE-01, B14 | 02 | 03 | 04 | tøm 05–07, app-bytte 08, duplikater 10, reload/back/fwd 12, null 13 |
| «Start vurdering» → Finn og forstå (eksakte ID-er) | READY-01 | READY-01 | READY-01 | – | – |
| Prioritet- og dekningssøyler → liste (eksakte ID-er) | B19, B24 | via ExpectFigures | via ExpectFigures | via ExpectFigures | – |
| Modultabell: sortering og radlenke | B18 | – | – | – | – |
| Paginering og «vis flere» | B20 | – | – | – | – |
| KPI «Scenarioforslag» → Finn og forstå i samme scope | – | B17, B23 | – | – | – |
| KPI-flis | B17 (tom flis = deaktivert) | – | – | – | – |
| Testplan-KPI og lenke | B16 (bare tom tilstand) | – | – | – | – |
| Prosjektnivå / uten modultilknytning | B07, B08 | – | – | – | – |

**N/A med evidens:**

- **S05/S06** (spesifikasjon eller dokument uten modul): velgeren tilbyr dem ikke. B05-B06 bekrefter at kombinasjonen er hindret.

**NOT VERIFIED:**

- **S13/S14** (ugyldig eller stale referanse): bare dekket via app-bytte (B22). En faktisk utdatert kildeversjon er ikke laget.
- Alle saksbaserte kontroller (`mto-drill-case`, `mto-drill-plan`, plankort, blokkeringer, kildeendringer, godkjent/klar-målene med verdi > 0): fixture har ingen testtilfeller.

## 9. Uavhengig avstemming

`TestOverviewFixture` beregner forventede populasjoner direkte fra fixture-modellen (oracle-identitet = spesifikasjonssti + token i scenarioteksten). Den kaller ingen produksjonsaggregering.

Følgende sammenlignes:

- KPI «Scenarioforslag» (akseptansescenarioer + krav uten scenario)
- prioritetssøyler og dekningssøyler
- modul-, spesifikasjons- og historierader
- radenes «krav uten scenario»
- P1-populasjon i destinasjonen
- klargjøringspopulasjon i destinasjonen
- søylelister

Sammenligningen gjøres som mengder eller multimengder. Like totaler med andre identiteter gir FAIL. Duplikater er bevisst med: samme US1/FR-001 i alle dokumenter, og samme tittel i to spesifikasjoner.

**Observasjon, ikke rettet:** en ZIP med én omsluttende mappe (`Aurora/…`) gjør mappen til modulrot etter den kanoniske modulregelen. Moduler vises da som «Aurora/Booking», og prosjektets egne `specs/` som modulen «Aurora», ikke som «Prosjektnivå». Regelen deles med explorerne og er ikke endret. Den bør vurderes som egen sak, fordi nedlastede arkiver ofte har en slik mappe.

## 10. Gjenstående (hvorfor fortsatt NO-GO)

1. Fixture med testplaner og testtilfeller i alle tilstander (utkast, godkjent, klar, ikke klar, kildeendret, E2E) og nettleserverifisering av de saksbaserte kontrollene. Seeding krever Finn og forstå → testplan-flyten.
2. Saksbaserte oppfølgingslinjer har ennå ikke en filterkontrakt til Planlegg og klargjør, og bruker fortsatt en kompakt inline saksliste.
3. Stale kildeversjon (S14) med ny import av endret innhold.
4. Full frontend- og full backend-regresjon etter endringene.
5. Visuell modernisering utover tekstreduksjonen (kap. 11).

## 11. Hvorfor så mange runder for én side, og hvordan unngå det

**Hvorfor:**

- **Testene bekreftet presentasjonen, ikke brukerens opplevelse.** bUnit-testene satte opp en ferdig `ManualTestingOverview` og et ferdig scope, og testet hver for seg at «modul valgt → riktig tall». Ingen test åpnet siden med et *lagret* scope (reload/tilbake). Der lå feilen. Den ble funnet først da en ekte nettleser gjorde det en tester gjør.
- **To kilder til samme sannhet.** `_level` og det delte valget kunne komme i utakt. Hver runde rettet ett symptom (knapper, picker, snitt) uten å fjerne den doble tilstanden.
- **Ingen kjørbar nettleserhost.** Tre økter stoppet på «PostgreSQL / App_Data / porter». Hosten som nå finnes, var mulig med eksisterende vern hele tiden.
- **Kravene vokste underveis.** Scope → kontroll×scope → identitetsorakel → fjerne inline-lister → navigasjonskontrakt → modernisering. Hver utvidelse åpnet nye flater.
- **Testoversikt har mange flater** (KPI-er, fliser, 8+ diagrammer, oppfølging, klargjøring, tabeller og tilpasning) med inline-lister som duplikerte arbeidsvisningene. Hver flate trengte sin egen test.

**Slik forebygger vi det:**

1. **Én tilstand per sannhet:** utled visningsnivå og filter fra den delte kilden og lagre aldri en kopi. Kodegjennomgang bør stoppe nye parallelle tilstander.
2. **Ett navigasjonsprinsipp:** tall eller handling på oversikten åpner arbeidsvisningen med eksakte identiteter. Ingen lister på dashboardet. Da trengs én kontrakt-test per kontroll, ikke én per liste.
3. **Isolert nettleserhost i den faste testpakken:** `Category=IsolatedBrowser` kjøres for hver endring i Testoversikt (≈10 min). Den lagrede-scope-reisen (B23 / P1-SCOPE-12) er obligatorisk.
4. **Orakel før implementering:** skriv forventede identiteter for et nytt kort i fixturen før kortet bygges.
5. **Avgrens oppgaven:** én oppgave = én flate med akseptkriterier og ferdig-definisjon. Release-aksept (B01–B24) er en egen oppgave.

## 12. Sluttkjøring, endrede filer og Git

Committet før sluttkjøringen, etter ønske fra brukeren. Resultatene her er det som faktisk var kjørt ved commit:

- **Isolert nettleser, kjøring 2** (etter scope-retting og ny navigasjon): **34 bestått, 2 feilet av 36**. Alle P1-SCOPE-01…15 og READY-01 besto. De to feilene lå i test-hjelperne, ikke i produktet:
  - B12: prosjektbyttet ventet på feil element.
  - B15: detaljskuffen i Finn og forstå dekket «Vis alle grupper».

  Begge hjelperne er rettet. Kjøring 3 med rettingene og de siste justeringene var ikke ferdig ved commit (tomme fliser deaktiveres, mindre tekst).
- **Frontend, målrettet** (ManualTesting, scope-velger, TestOverview, ScenarioBrowser): **266 bestått, 1 feilet** (R06, layout-avhengig sti).
- **Full frontendsuite:** **9 875 bestått, 70 feilet**. Alle 70 er `DirectoryNotFound`/`FileNotFound`, fordi testene leter etter kildefiler relativt til standard `bin/`, mens bygget gikk til en isolert artifacts-mappe for ikke å låse utviklerinstansen. Ingen er assertion-feil. De må kjøres på nytt fra standard layout for å regnes som verifisert.
- **Full backendsuite:** ikke kjørt etter endringene.

Endrede filer:

- `AIAssisted/frontend/BirkNext.Web/Pages/ManualTesting.razor`
- `AIAssisted/frontend/BirkNext.Web.Tests/Pages/ManualTestingWorkspaceTests.cs`
- `AIAssisted/frontend/BirkNext.Web.Tests/TestOverview/interaction-inventory.json`
- `AIAssisted/backend/BirkNext.Api.Tests/BrowserVerification/*` (ny)
- denne rapporten

Ikke del av denne commiten (vurderes i worktree-revisjonen):

- `TestHostMemoryDiagnostics.cs`
- `RealProjectAcceptanceFrameworkTests.cs`
- `birknext-v12-final-verification-2026-11-27.md`

Kjøring 3 ble fullført etter commit: **36/36 bestått** (se kap. 7).

Ingen push.

## 13. Oppdatering 2026-11-28: testtilfelle-navigasjon, fullstendig fixture og runner

Commits: `4bc0ab3a`, `73fa2439` og sluttcommiten med denne rapporten. Ingen push.

### 13.1 Runner-skript (løst)

Meldingen viste til `scripts/run-testoverview-browser-verification.ps1`, som ikke fantes. Løst med alternativ A: `AIAssisted/scripts/run-testoverview-browser-verification.ps1`.

- Bygger testprosjektet til en egen `--artifacts-path`, og nekter mapper inne i API- eller Web-prosjektet.
- Krever en lokal PostgreSQL-server på 5432. Mangler den, avslutter skriptet med kode 2 (NOT VERIFIED).
- Peker hosten på byggets `staticwebassets.runtime.json` og kjører `Category=IsolatedBrowser`.
- Exit-koder: 0 = alt bestått, 1 = en reise feilet, 2 = forutsetning mangler.
- Isolasjon håndheves fortsatt inne i hosten, og den lukker ved feil (kap. 3).
- Ingen Docker eller Podman, intet internett, og ingen kjørende BirkNext.

### 13.2 Fixture-poster (nye)

`TestOverviewCaseSeeding` lager testdataene gjennom de samme API-kallene sidene bruker: oppdagelse lagret av Finn og forstå, `project-drafts`, `review` og lagring av redigeringer. Kallene går via nettleserkonteksten, slik at Local Tester Mode-autorisasjonen gjelder. Hver tilstand verifiseres ved seeding.

| Plan | Testtilfelle (scenario) | Tilstand |
|---|---|---|
| Fixture A | Booking/001 AUR-BKG1-US1-S1 (P1) | godkjent + klar |
| Fixture A | Booking/001 AUR-BKG1-US1-S2 (P1) | venter på godkjenning |
| Fixture A | Booking/001 AUR-SHARED-S1 (P1) | venter (samme tittel som i Billing) |
| Fixture A | Billing/001 AUR-SHARED-S1 (P1) | godkjent, ikke klar |
| Fixture A | Billing/001 AUR-BIL1-US2-S1 (P2) | trenger avklaring |
| Fixture B | Booking/002 AUR-BKG2-US1-S1 (P1) | blokkert (registrert hindring) |
| Fixture B | specs/001-platform AUR-PLT1-US1-S1 (P1) | venter |
| Fixture B | Booking/001 AUR-BKG1-US2-S1 (P2) | godkjent + klar |
| Fixture C (Cirrus v1) | Core/001-a CIR-A-US1-S1 / S2 | godkjent / venter |

Oraklet er utvidet med disse beregningene: dekket, P1 uten testtilfelle, gap per prioritet, tilfeller i scope, godkjent, klar og planer i scope. Alt er beregnet fra fixturen, ikke fra produksjonsaggregering. Det finnes ingen E2E-plan i fixturen. E2E-flisen er bare verifisert for tom populasjon (deaktivert knapp).

### 13.3 Navigasjonskontrakt for testtilfeller

**Én regel:** et tall eller en handling åpner arbeidsvisningen med eksakt populasjon, i samme scope.

- **Testtilfeller** («venter på godkjenning», «ikke klare», «kildeendring», «Testbarhet og blokkeringer», «Kildeendringer»):
  - Testoversikt skriver `CaseSelection(label, [(planId, caseId)…])` til `sessionStorage mtp-cases.<prosjekt>` og åpner `manual-testing/plan?draft=<første plan>`.
  - Planlegg og klargjør anvender utvalget selv (`P.Rows(..., onlyCases)`). Tellede tekniske tilfeller vises, og tilfeller som er borte telles (`mtp-overview-selection-missing`).
  - Andre planer i utvalget navngis med egne knapper. Utvalget kan fjernes og overlever omlasting.
- **Scenarioer:** uendret, går til Finn og forstå (kap. 6).
- **Skillet holdes:** scenarioklargjøring (Finn og forstå) er ikke det samme som testtilfellets klarhet (Planlegg og klargjør), og godkjenning er ikke det samme som kjøring.
- Inline sakslister er fjernet fra Testoversikt. Diagramlistene for prioritet og manglende dekning er beholdt (P2, se 13.8).

### 13.4 Kontroll × scope

`interaction-inventory.json` har nå `controlScopeCoverage` (kontroll → scope → tester). En ny vakt (`Every_test_the_inventory_maps_a_control_and_scope_to_exists`) feiler hvis inventaret nevner en test som ikke finnes. Den fant straks én foreldet referanse.

**Tomme celler er kjente manglende par** (NOT VERIFIED):

- p1-uncovered S09/S14
- p1-approval S03/S14
- p1-ready S03/S04
- p1-source S01/S14
- chart.blockers S02–S04
- chart.sourceChanges S01/S14 (DEF-V12-01)
- readiness S04

### 13.5 Identitetsavstemming

**Fremgangsmåte:**

- For hver oppfølgingskategori sammenlignes oraklets forventede ID-er med tallet og med destinasjonens faktiske ID-er.
- Scenarioer sammenlignes som tokens med multiplisitet. Testtilfeller sammenlignes som (plan-ID, case-ID) fra API-et.

**Negative tilfeller som er med:**

- Samme tittel (AUR-SHARED-S1) i to dokumenter med ulik case-tilstand. Bare Booking-tilfellet er med i «venter på godkjenning».
- «US1 · AC 1» og FR-001 i over 20 dokumenter (P1-SCOPE-10).
- Tilfeller fra en annen applikasjon telles aldri (CASE-06).

### 13.6 Bekreftede avvik og observasjoner

**DEF-V12-01 (rettet for importer med kildekode 2026-11-28, se kapittel 14):**

- **Funnet:** når en applikasjon får en nyere import («Ny versjon av eksisterende applikasjon», med teksten «Planer følger applikasjonen og sammenlignes med den nye versjonen») og arbeidsflaten byttes til den, viser Testoversikt 0 testtilfeller og ingen testplan. Den eldre importen viste planen «Fixture C» med 2 tilfeller.
- **Følge:** scenarioet med testtilfelle telles som udekket, og ingen kildeendring vises.
- **Rotårsak:** antatt i eierskapet til planer (per import kontra per applikasjon), utenfor Testoversikt. Ikke rettet uten avklaring.
- **Status:** reisen `STALE_02` er SKIPPED med avviksreferansen, aldri bestått.

**Riktig oppførsel verifisert (STALE-01):**

- Etter en ny import blir arbeidsflaten på importen den viser og sier fra (`mto-source-stale`). Tallene er fortsatt den viste importens.
- Etter bytte vises en spesifikasjon som ikke lenger finnes, aldri med bredere tall.

**Observasjoner (ikke utredet):**

- **Historikk:** i én kjøring havnet et andre tilbake-steg i en kjede med flere steg på importsiden (P1-SCOPE-05–07). Tømmetrinnene navigerer nå direkte. Fram og tilbake dekkes av P1-SCOPE-12, B23 og CASE-02.
- **Lenkeklikk:** i én av fem fullkjøringer ble et klikk på «Scenarioforslag» rett etter et scope-bytte ignorert mens siden tegnet seg på nytt (B23). Testen prøver nå inntil tre ganger. En tester kan oppleve det samme.

### 13.7 Testresultater (faktisk kjørt)

| Kjøring | Resultat |
|---|---|
| Hele frontendsuiten, `-c V12Verify -p:DefineConstants="DEBUG;TRACE"` (standard mappedybde, egne bin/obj, ingen lås på utviklerinstansen) | **9 950 / 9 950 bestått.** De 70 tidligere stifeilene og R06 er løst uten testendringer. |
| Målrettede frontendtester (ManualTesting, TestOverview, ScenarioBrowser, PlanPrepare) | 251 / 251 |
| Inventarvakter | 2 / 2 |
| Isolert nettleser via runner-skriptet (kjøring 11) | 42 bestått, 1 feilet (B23, ignorert klikk, se 13.6), 1 hoppet over (STALE-02 / DEF-V12-01) |
| **Isolert nettleser, sluttkjøring via runner-skriptet** (kjøring 12) | **43 / 43 bestått, 1 hoppet over** (STALE-02 / DEF-V12-01), exit 0 |
| Backend: `Services.TestPlanning`, `ProjectImport`, `TestIsolationTests`, `TestPostgresGuardTests` (isolert testdatabase) | **733 / 733 bestått** |

**Hvorfor 70 frontendtester feilet før:** de leter etter kildefiler med fast relativ dybde fra `bin/Debug/net8.0`, og `--artifacts-path` endrer dybden. Konfigurasjonen over gir samme dybde.

### 13.8 Gjenstår og GO/NO-GO

1. DEF-V12-01: kildeendrede testtilfeller kan ikke verifiseres i nettleser før planer følger applikasjonen til en ny import.
2. De manglende kontroll × scope-parene i 13.4.
3. P2: diagramlistene for prioritet og manglende dekning åpnes fortsatt inne på siden. Navigasjon til Finn og forstå med filter er ikke laget.
4. Full backendsuite er ikke kjørt (bare relevante delsett).
5. Ustabil navigasjon i nettleser (13.6) er ikke utredet.

**Beslutning: NO-GO for full V12-aksept.** UI- og navigasjonsverifiseringen er nå omfattende og kjørbar med ett skript. Hovedhinderet er DEF-V12-01, et dataeierskapsavvik som påvirker hva testere ser etter en ny import.

## 14. Oppdatering 2026-11-28 (2): DEF-V12-01 rettet, nye avvik funnet

### 14.1 Reprodusert før retting

**Reproduksjon i backend:** `SourceDrivenPlanningTests.DEF_V12_01_…`, mot en egen midlertidig PostgreSQL-database.

- **V1:** plan P1 med tilfellene A, B og C. A og B er godkjent, og A har ett resultat.
- **V2:** ny versjon av samme applikasjon, valgt som gjeldende. Ingen har kjørt Finn og forstå på den.
- **Før retting:** `OverviewAsync("import:v2")` ga tom `Plans`. Dette er samme symptom som i nettleseren.

**Faktiske identiteter i nettleser-fixturen,** lest fra kjøringsdatabasen:

- Hver import har sin egen prosjektnøkkel: `import:import-<SHA-prefiks>`.
- Planene lagres med `GuideKey = project:<importnøkkel>` og `ApplicationKey` fra scenarioforslagets kildegrunnlag.
- Fixture-ZIP-ene med bare dokumenter hadde katalogen `SnapshotId` tom og grunnlaget `DocumentsOnly`. Applikasjonsnøkkelen ble da prosjektnøkkelen selv.

### 14.2 Rotårsak og eierskapsmodell

| Spørsmål | Svar (kodested) |
|---|---|
| A. Eies planen av applikasjonen? | Delvis. `TestPlanDraft.ApplicationKey` kommer fra scenarioforslagets `Basis.ApplicationKey` (`ScenarioSourceBasisResolver.ResolveAsync`). |
| B. Eies den av importen? | Ja, lagringseier. `GuideKey = ProjectOwner(projectKey)` og `ProjectKey = import:<id>`. |
| C. Har testtilfellet en import-ID? | Det har `Source.Owner` (prosjektnøkkel) og en kildeidentitet (`Generated.SourceIdentity`, `tss-…`) som er stabil på tvers av importer. |
| D. Filtrerer Testoversikt på import i stedet for applikasjon? | Ja, når applikasjonen er ukjent. `ProjectWorkspaceAsync` fant applikasjonen bare via prosjektets siste scenarioforslag (`discoveries.Latest(key)`). En ny import uten Finn og forstå fant derfor ingen applikasjon og ingen tidligere planer. **Dette er rotårsaken.** |
| E. Er kildeidentitetene stabile? | Ja, for samme Given/When. Tvillinger skilles med utfallssuffiks (`CandidateIndex.Find`). |
| F. Finnes scenarioet i ny versjon? | Ja, via kildeidentiteten, etter at ny versjon er sammenlignet. |
| G. Endret eller fjernet? | `FlagProject` / `Absence`: Endret, Fjernet (bare ved fullstendig sammenligning), Kilde mangler, Ikke sammenlignet. |
| H. API | `GET api/test-plan-suggestions/project/overview` og `GET …/project`. Begge går via `ProjectWorkspaceAsync`. |
| I. Persistens, spørring, identitet eller UI? | **Spørring.** Persistens og identiteter var riktige. |

### 14.3 Retting (ingen skjemaendring, ingen migrering)

**1. `ImportedApplicationAsync`** (`TestPlanSuggestionService.cs`)

- Prosjektets applikasjon hentes fra importens egen registrering: katalogens `SnapshotId` → kildeversjonens repository-identitet. Det er valget «Ny applikasjon» eller «Ny versjon av …» fra importen.
- Rekkefølgen er scenarioforslag → importregistrering → egne planer.
- Ingenting utledes fra navn, tittel eller US-, AC- eller FR-nummer.
- Planene flyttes, kopieres og skrives ikke om.

**2. Ikke sammenlignet ennå** (`TestPlanSuggestionService.Overview.cs`)

- Finnes det en nyere kildeversjon som planen ikke er sammenlignet med (`NewerSources`), vises tilfellenes «i samsvar» som `NotCompared`.
- Endret, Fjernet og Kilde mangler beholdes.
- Ny rad i Kildeendringer: «Ikke sammenlignet ennå». Den åpner nøyaktig de tilfellene i Planlegg og klargjør.

**3. DEF-V12-02, pakkevalg med underforstått modul** (`ManualTestingOverviewPresentation.Of`)

- `null` (alle moduler) ble lest som «Prosjektnivå» i pakkegrenen.
- Med to dokumenter i samme pakke meldte siden derfor «finnes ikke i kildegrunnlaget».
- Nå gjelder samme regel som i stigrenen.

**4. DEF-V12-04, kryssapplikasjonslekkasje i Finn og forstå** (`ManualTestingWorkspace`)

- En import med bare dokumenter tok målmiljøets hovedpakke som kildeversjon. Hørte den pakken til en annen applikasjon, leste serveren den applikasjonens spesifikasjon.
- **Bevis:** STALE-01 fikk Dorados `DOR-…`-scenarioer under Cirrus' nøkkel.
- **Nå:** miljøprosjektets pakker brukes bare for prosjektets egen import, eller for en import som ligger i samme prosjekt i prosjektlisten.

**Livsløp før og etter:**

| | Før | Etter |
|---|---|---|
| Ny versjon, ikke sammenlignet | 0 planer, 0 tilfeller | Planen og alle tilfeller vises, som «Ikke sammenlignet ennå» |
| Etter Finn og forstå på ny versjon | Planen vises | Uendret, Endret og Fjernet per tilfelle |
| Eldre import | Planen vises | Samme plan og samme revisjon |

### 14.4 Åpne punkter

- **DEF-V12-03 (UX):** dokumentvelgeren viser V1- og V2-kopien av samme dokument med identisk tekst.
- **Dokumentimporter uten kildekode:** de får ingen applikasjonsidentitet på serveren, og valget «Ny versjon» vises ikke. Planer følger derfor ikke en ren dokumentimport. Å rette det krever at importens applikasjonsvalg lagres uten kildeversjon. Det er en skjema- eller produktbeslutning.
- **Kravsporbarhet:** `PreparedCaseTraceability.Build` leser bevisst bare importens egne planer. Ikke endret, fordi det endrer dekningssemantikk.
- **Eldre versjon med kildekode:** «Show this package» viste fortsatt V2. P er derfor bare verifisert i backend.
- **Testverktøy:** importbyttet navigerte før autolagringen var ferdig. Verktøyet venter nå på lagringen.

### 14.5 Automatiske tester

| ID | Hvor |
|---|---|
| DEF-V12-01-A, B, C, D, E, F, G, H, I, M, P | Backend `DEF_V12_01_plans_and_cases_follow_the_application_…` (V1 → V2 → V3 → V4 + applikasjon B, eksakte plan- og case-ID-er, persisterte rader) |
| A, B, C, D, E, K, L, M, N, O, Q (+ p1-source) | Nettleser `STALE_02_DEF_V12_01_…` (Dorado med kildekode, Ny versjon) |
| R | STALE-01 |
| J | N/A i fixturen (én underforstått modul). Modulscope er dekket av B02, B09 og P1-SCOPE-02. |
| Ikke sammenlignet ennå, pakke uten modul | Frontend `DEF_V12_01_cases_not_compared_…`, `A_pack_scope_with_an_implied_module_…` |
| DEF-V12-04 | Frontend `ManualTestingWorkspaceSnapshotIsolationTests` (3) |

Kontroll × scope: `chart.sourceChanges` og `p1-source` S01/S14 er dekket.

**Fortsatt tomme celler:**

- p1-uncovered S09/S14
- p1-approval S03/S14
- p1-ready S03/S04
- chart.blockers S02–S04
- readiness S04

### 14.6 Resultater i denne runden

| Kjøring | Resultat |
|---|---|
| Hele frontendsuiten (V12Verify), før de nye testene | 9 950 / 9 950 bestått |
| Nye frontendtester (5) | 5 / 5 bestått |
| Backend DEF-V12-01 livsløp | Bestått etter retting. Feilet før retting (reprodusert). |
| Nettleser STALE-01 + STALE-02, siste fullførte kjøring | STALE-01 bestått. STALE-02 bestått til og med Q; P er tatt ut, se 14.4. |
| Hele den isolerte nettleserpakken, og backend TestPlanning/ProjectImport/isolasjon/Specifications | **Startet, men ikke ferdig ved commit (brukeren ba om commit uten å vente). NOT VERIFIED i denne committen.** STALE-02 med p1-source-steget er ikke kjørt ennå. |

**Beslutning: NO-GO.**

- Full nettleser- og backendkjøring av denne endringen er ikke bekreftet.
- Dokumentimporter følger fortsatt ikke applikasjonen.
- Kontroll × scope-hull gjenstår.
