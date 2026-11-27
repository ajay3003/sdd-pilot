# Testoversikt: scope, navigasjon og regresjonsvern

Oppdatert 2026-11-27 (kveld): isolert nettleserverifisering, scope-lekkasje, P1-/klargjøringsnavigasjon og prosessvurdering.
Tidligere checkpoint: `1f12ada6` («testoverview: checkpoint scope and navigation hardening»).

## 1. Kort oppsummert

| Område | Status |
|---|---|
| Isolert nettlesertest-host (egen database, egen App_Data, egne porter) | **Bygget og bevist i kjøring** |
| Scope-lekkasje (tall for bredere utvalg enn velgeren viser) | **Rotårsak funnet og rettet**, bevist i nettleser |
| P1-oppfølging og «Start vurdering» som lange inline-lister | **Fjernet**: navigerer til Finn og forstå med eksakt populasjon |
| Uavhengig orakel (forventede ID-er beregnet fra fixture, ikke fra produksjonskode) | **Bygget**; brukt i alle nettleserreiser |
| Nettleserreiser B01–B24 + P1-SCOPE + READY | Kjørt i isolert host, se kap. 7 |
| Testplaner/testtilfeller i fixture (godkjent, klar, kildeendret, E2E-plan) | **Ikke bygget**: tilhørende kontroller er NOT VERIFIED |
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
