# BirkNext — OpenSpec som isolert behandlingsrute

**Dato:** 2026-11-27
**Gren / worktree:** `feature/openspec-isolated-route` (worktree `BirkNext-OpenSpec`), utviklet på `9af21c93` og slått sammen med `008-traceability-first` (`ed472b7a`, fast-forward + løste konflikter)
**Status:** Implementert og testet på backend og komponentnivå, committet og pushet til `ado` og `origin` etter brukerens instruks. Nettleser-/ende-til-ende-verifisering er **IKKE VERIFISERT**.

---

## 1. Sammendrag

BirkNext støtter nå to spesifikasjonsformater side om side, valgt eksplisitt per applikasjon i **Systeminnstillinger → Spesifikasjonsformat**:

- **Spec Kit (standard):** eksisterende behandling, kalt med nøyaktig de samme argumentene som før. En gyllen regresjonsfil tatt opp ved HEAD `9af21c93`, *før* noen OpenSpec-kode fantes, viser at alt er uendret: dokumenter, katalogoppføringer, evidens, kandidat-ID-er, kildeidentiteter, fingeravtrykk, antall og modulgruppering for de ekte M2LB-spesifikasjonene og en rotspesifikasjon.
- **OpenSpec (nytt):** egen rute i nye filer (`Services/Specifications/OpenSpec/`). Bare `openspec/specs/<capability>/spec.md` blir gjeldende krav og scenarioer. Aktive endringer (`openspec/changes/<id>/`) og arkiv (`changes/archive/`) parses med livssyklus og deltaoperasjoner, men brukes aldri på baseline og telles aldri i dekning eller KPI-er.

Den eksisterende Manual Testing-flaten brukes for begge formater. Discovery, Testoversikt, planlegging, testplaner og kjøring leser den lagrede, formatnøytrale evidensen som ruten produserer. Det er ikke laget noe nytt Manual Testing-grensesnitt.

Ingen srcid/2-migrering, ingen automatisk Spec Kit↔OpenSpec-kobling, ingen omskriving av lagrede testtilfeller, planer, godkjenninger eller kjøringer, og ingen eksterne skrivinger til Azure DevOps for OpenSpec-prosjekter.

**Anbefaling:** Klar for intern pilot på én ikke-produktiv applikasjon etter manuell nettleserkontroll (se §15). **Ikke produksjonsklar** før nettleserverifisering er gjort og Datakvalitet-senteret er samordnet.

## 2. Arkitektur og rutevalg

```text
Prosjektimport (Commit)
  └─ ProjectImportService.SpecificationCatalogAsync
       └─ SpecificationProcessingRouter.CaptureAsync(applikasjonsnøkkel)      ← eneste formatbeslutning
            ├─ SpecKit  → SpecificationCatalogService.CaptureAsync(...)       (uendret; samme argumenter)
            └─ OpenSpec → OpenSpecProcessingRoute
                           ├─ OpenSpecSourceDiscovery   (klassifisering etter mappestruktur)
                           ├─ OpenSpecMarkdownParser    (Requirement / Scenario / delta, linjenumre)
                           ├─ OpenSpecModelBuilder      (identitet, livssyklus, konflikter, evidens)
                           ├─ OpenSpecSourceModelStore  (kildemodell, App_Data – skrives FØR katalogen)
                           └─ SpecificationCatalogService.CaptureRoutedAsync (fingeravtrykk «os1-…»)
Lagret katalog + evidens (formatnøytral CoverageWorkspaceEvidence, bare gjeldende baseline)
  └─ ScenarioSourceBasisResolver → TestScenarioDiscoveryService → Finn og forstå / Planlegg / Testplaner
  └─ TestPlanSuggestionService.OverviewAsync → Testoversikt
  └─ OpenSpecStatusService → status, kildeendringer, avstemming (skrivebeskyttet)
```

**Én autoritativ formatkilde:** `SpecificationFormatSettingsStore` (per applikasjon, App_Data-JSON, samme mønster som de andre Systeminnstillinger-lagrene). Alle inngangspunkter spør `SpecificationProcessingRouter.FormatFor(...)`. Innholdet i dokumentene kan aldri overstyre innstillingen.

**Uforanderlig rutemerke på hver katalog:** katalogens fingeravtrykk begynner med `os1-` når OpenSpec-ruten laget den (`SpecificationFormatMarker`). Lesere tolker en katalog etter merket den fikk ved import, ikke etter dagens innstilling. Et formatbytte reinterpreterer derfor aldri historikk. Ingen databasemigrering var nødvendig.

**Ingen stille reserve:** Finnes ingen `openspec/`-mappe i en OpenSpec-applikasjon, lagres ingen katalog. Problemet lagres og vises i Testoversikt og status (`OPENSPEC_ROOT_MISSING`). Nettleserens Spec Kit-tolkning blir avvist for OpenSpec-applikasjoner, både som arbeidsutkast (HTTP 400 `openspec-working-draft-unsupported`) og som uverifisert kopi (forklart begrensning, ingen scenarioer).

### Inngangspunkter som ble revidert (Phase 3)

| Inngangspunkt | Formatbevisst nå | Hvordan |
|---|---|---|
| Prosjektimport (ZIP / Git → Commit) | Ja | `ProjectImportService` → router (test: `ProjectImportSpecificationRoutingTests`) |
| Kildeoppdatering / ny versjon | Ja | Samme commit-vei; ny import av samme applikasjon følger innstillingen |
| Spesifikasjonskatalog / kildeevidens / kildeversjoner | Ja | Katalogens merke; OpenSpec-modell per katalogrevisjon |
| Discovery (Finn og forstå, Planlegg og klargjør) | Ja | `ScenarioSourceBasisResolver` (avviser Spec Kit-tolket utkast og reserve for OpenSpec) |
| Testoversikt | Ja | Leser rutet evidens; OpenSpec-capabilities blir «Uten modultilknytning»; eget OpenSpec-panel |
| Kildeendringer | Ja | `OpenSpecStatusService.SourceChangesAsync` (baseline og forslag hver for seg) |
| Azure DevOps-overføring | Ja | Sperret for OpenSpec-prosjekter (kun forhåndsvisning/fil), avvisningen loggføres |
| `SystemGuideService.GenerateSpecificationCandidatesAsync` | Nei – kompatibilitetsinngang | Bare brukt av eldre tester/verktøy; ingen produksjonskaller funnet |
| `DeliverySnapshotParser` (Leveranserapporter) | Nei | Egen opplasting; tolker med Spec Kit-parseren; markert «Ikke verifisert» for OpenSpec |
| Nettleserens explorere (Specifications Explorer m.fl.) | Nei | Tolker i nettleseren med Spec Kit-parseren; markert «Ikke støttet» for OpenSpec |

## 3. Bevarte Spec Kit-kodebaner

Ingen endring i `SpecExplorerService`, `SpecificationEvidenceBuilder`, `SpecificationCatalogService.Read/CaptureAsync`, `SampleArtifactClassifier`/`ArtifactDocumentDiscovery` eller `ModuleStructure`.

Endringer i delt kode (alle additive og gatet):

| Fil | Endring | Hvorfor Spec Kit er uendret |
|---|---|---|
| `shared/TestCoverageContracts.cs` | Valgfri `QaTestCandidate.SourceIdentityKey` og `CoverageRequirement.SourceIdentityKey` (standard `null`) | Spec Kit-byggeren setter dem aldri; lagret Spec Kit-JSON mangler feltet → `null` |
| `TestScenarioDiscoveryEngine.FromSpecification` | Bruker `SourceIdentityKey` når den finnes, ellers eksisterende regel | `null` → byte-identisk gren (golden-test) |
| `TestPlanSuggestionService.Overview` | OpenSpec-stier ekskluderes fra modulrøtter og får «Uten modultilknytning» | Gatet på katalogmerket; for Spec Kit er settet `null` |
| `ScenarioSourceBasisResolver` | Formatoppslag og OpenSpec-vakter | For Spec Kit + Spec Kit-katalog går alle grener som før (test S01 sammenligner med og uten ruter) |
| `ProjectImportService` | Kaller ruteren; uten ruter (eldre oppsett) brukes det direkte kallet | Spec Kit-ruten kaller `CaptureAsync` med samme argumenter |
| `SpecificationCatalogService` | Nye metoder `CaptureRoutedAsync`, `AllRevisionsAsync`, `EvidenceOfAsync` | `CaptureAsync` er ikke endret |
| `AdoTestPlanTransferService` | OpenSpec-vakt i forhåndsvisning/overføring | Gatet på katalogmerket |

## 4. Nye OpenSpec-tjenester

| Komponent | Ansvar |
|---|---|
| `OpenSpecSourceDiscovery` | Klassifiserer hvert dokument i nøyaktig én kategori: gjeldende, aktivt endringsdokument, aktiv delta, arkiv, støtte, konfigurasjon, utenfor OpenSpec, ikke støttet. Gjør trygg stinormalisering, avviser duplikater og leser `config.yaml` som tekst. |
| `OpenSpecMarkdownParser` | `### Requirement:` / `#### Scenario:` / GIVEN–WHEN–THEN–AND–BUT, deltaseksjoner og RENAMED FROM/TO, med linjenumre. Kodeblokker er tekst. Har grenser og avbrytbarhet. |
| `OpenSpecIdentity` | Eget identitetsrom `openspec/1` (§6). |
| `OpenSpecModelBuilder` | Kildemodell, utelatelser, livssyklus, deltaoppløsning, konflikter og evidens/katalog for gjeldende baseline. |
| `OpenSpecSourceModelStore` | Kildemodell per prosjekt og katalogfingeravtrykk, original Markdown (begrenset) og siste ruteproblem. |
| `OpenSpecProcessingRoute`, `SpecificationProcessingRouter` | Rutingen. |
| `OpenSpecStatusService` | Status, kildeendringer med berørte testtilfeller, og uavhengig avstemming med Markdown-eksport. |
| `SpecificationFormatController` | `GET/PUT api/specification-format`, `…/impact`, `…/consumers`, `…/projects/{key}/openspec[/source-changes|/reconciliation[/export]]` |

## 5. Systeminnstillinger og lagring

**Seksjonen «Spesifikasjonsformat»** (Analysis-gruppen, `?section=specification-format`):

- **Applikasjon:** velges fra importerte applikasjoner og viser antall importer og når formatet sist ble endret.
- **Formatvalg:** kort for «Spec Kit – eksisterende format» og «OpenSpec – nytt format», med forklaring. Aktivt format vises som merke.
- **Konsekvensvisning:** vises før lagring. Har applikasjonen data, kreves eksplisitt bekreftelse; serveren håndhever dette med HTTP 409 og konsekvensene i svaret. Teksten sier at valget bare gjelder fremtidige importer, at ingenting behandles på nytt eller slettes, og at identiteter aldri kobles automatisk. Den anbefaler også å importere som ny applikasjon (isolert kildegrunnlag) hvis en ren overgang trengs.
- **Tilgang:** `PUT` krever `SystemConfigurationPermissions.Policy`. Uten tilgang vises valget skrivebeskyttet.
- **Lagring:** `App_Data/specification-format/specification-format.json`, med historikk (fra → til, hvem, når). Valget overlever omlasting fordi det er lagret på serveren. En uleselig fil gir en eksplisitt feil (503 ved discovery), aldri stille Spec Kit.
- **Modulstøtte:** tabell som viser «Støttet», «Kun forhåndsvisning», «Ikke verifisert» eller «Ikke støttet» per modul og format.
- **Datakvalitet – OpenSpec-avstemming:** kjøres bare på forespørsel, per import, og kan lastes ned som `.md`.

## 6. Kildeidentitet (OpenSpec, regelsett `openspec/1`)

1. **Navnenormalisering:** Unicode NFKC, alle mellomromsrekker blir ett mellomrom, trimmes. Sammenligningsform = normalisert navn med små bokstaver.
2. **Slug:** bokstaver og sifre i alle skript beholdes (æ/ø/å), alle andre tegnrekker blir én «-». Er sluggen over 60 tegn, kortes den til 60 + «-» + 8 hex av SHA-256. En tom slug blir «x-» + 8 hex.
3. **Capability-nøkkel:** mappestien under `specs/`, hver del slugget og sammenføyd med «.».
4. **Omfang (scope):** tomt ved én `openspec/`-rot. Ellers brukes rotmappene (uten felles arkivmappe), sluggede og med «~» bak. Arkivets egen toppmappe (`demo-main/`) er aldri del av identiteten.
5. **Kravnøkkel** `os:<scope><capability>/<krav-slug>`, **scenarionøkkel** `<kravnøkkel>/<scenario-slug>`, **endringsnøkkel** `osc:<scope>[archive/]<endringsmappe>`.
6. **Kildeidentitet i discovery:** `tss-` + hash av `specification|openspec/1|<nøkkel>`. Den er logisk og stabil ved tekstendringer; innholdsrevisjonen (`TextSha256`, kandidatens fingeravtrykk) endres separat.
7. `os:`-nøkler er BirkNext-nøkler og vises aldri som ID-er fra kilden (forklart i UI). Like kravnavn i ulike capabilities får ulike nøkler. Tittellikhet brukes aldri.
8. **Omdøping eller flytting** gir ny nøkkel. Den gamle gjenbrukes ikke. Omdøping rapporteres bare med eksplisitt evidens: en ny arkivert RENAMED-delta som nevner nøyaktig gammelt og nytt krav. Uten slik evidens rapporteres det som fjernet + nytt. Testtilfeller flyttes aldri automatisk.

## 7. Gjeldende / foreslått / arkiv

- **CurrentBaseline:** bare `openspec/specs/**/spec.md`. Dette er katalog, evidens, discovery og Testoversikt.
- **ProposedChange:** ADDED, MODIFIED, REMOVED og RENAMED parses og kobles til baseline når samme capability og samme normaliserte navn gir en entydig kobling. Oppløsningen blir `Resolved`, `NewRequirement`, `Unresolved`, `TargetExists` eller `Conflict`. Forslag brukes aldri på baseline.
- **Konflikt:** to aktive endringer på samme krav (navn eller RENAMED-mål) gir `CONFLICTING_ACTIVE_CHANGES`. Konflikten løses aldri ved rekkefølge.
- **ArchivedChange:** kun historikk (`Resolution = History`), aldri anvendt på nytt og aldri telt i gjeldende populasjon.
- **Skjema:** innebygd `spec-driven` (eller ingen config) er verifisert. Et egendefinert skjema gir `UNKNOWN_CUSTOM_SCHEMA` og avstemmingen blir IKKE VERIFISERT. Skjemafiler kjøres eller lastes aldri ned.

## 8. Krav og scenarioer

Applikasjon → capability → krav → scenario. Krav trenger ingen FR-ID, og scenarioer trenger ingen User Story (`StoryKey = null`; ingen historie oppfinnes). Et krav uten scenarioer blir en kravkandidat uten steg, med et åpent spørsmål. Flere scenarioer per krav støttes. GIVEN blir forutsetning, WHEN blir steg, THEN blir forventet resultat, og AND/BUT legges til forrige nøkkelord. Ingen steg lages som kilden ikke har. Testnivå blir aldri E2E fra OpenSpec-scenarioer (`IntendedLevel = Unknown`, type `BusinessFunctional`).

Diagnostikk (aldri stille utelatelse):

- **Struktur:** `ORPHAN_SCENARIO`, `UNSUPPORTED_HEADING`, `DUPLICATE_REQUIREMENT`, `AMBIGUOUS_REQUIREMENT_KEY`, `DUPLICATE_SCENARIO`, `MISSING_REQUIREMENT_NAME`/`MISSING_SCENARIO_NAME`, `MISSING_REQUIREMENT_STRUCTURE`, `REQUIREMENT_WITHOUT_SCENARIO`, `NO_NORMATIVE_KEYWORD`, `SCENARIO_WITHOUT_STEPS`, `UNSUPPORTED_SCENARIO_LINE`, `STEPS_OUTSIDE_SCENARIO`.
- **Delta og livssyklus:** `MALFORMED_DELTA`, `MALFORMED_DELTA_IN_BASELINE`, `UNRESOLVED_DELTA_TARGET`, `ADDED_TARGET_EXISTS`, `CONFLICTING_ACTIVE_CHANGES`.
- **Kilde og grenser:** `UNKNOWN_CUSTOM_SCHEMA`, `UNSUPPORTED_DOCUMENT`, `MULTIPLE_OPENSPEC_ROOTS`, `AMBIGUOUS_CAPABILITY_KEY`, `LINE_TOO_LONG`, `LIMIT_REACHED`.

Hvert utelatt krav og scenario telles i `Exclusions`, slik at avstemmingen kan regne ut hver overskrift.

## 9. Manual Testing-integrasjon

| Arbeidsflyt | Status | Bevis |
|---|---|---|
| Testoversikt | Støttet: teller bare gjeldende baseline; OpenSpec-panel med gjeldende tall, forslag (egen kategori, «ikke i dekning»), arkiv (historikk), diagnostikk; vises ikke for Spec Kit | `OpenSpecIntegrationTests.M01…`, `SpecificationFormatUiTests` |
| Finn og forstå | Støttet: scenarioer uten historie, kravkandidater, server-søk forbi 2 000 | M01, P01/P02 |
| Eksakt navigasjon | Støttet (`Pick` på kildeidentitet) | M02, P02 |
| Planlegg og klargjør | Støttet: evidens `openspec/specs/…#os:…`, kildens steg, ingen oppdiktede steg | M03 |
| Testplaner / revisjoner | Støttet (formatnøytralt); lenker stabile ved uendret reimport og tekstendring | M04, O09 |
| Testgjennomføring / Versjoner | Formatnøytralt; lagret innhold skrives aldri om | M09 (innhold), C06 (plan/godkjenning uendret) |
| Kildeendringer | OpenSpec-spesifikk: baseline og forslag hver for seg; berørte testtilfeller bare via verifiserte identiteter | M08, L07, O10 |
| Test Coverage / Requirements Traceability | Serverkatalog og kravkontroll formatnøytrale; nettleservisningene er ikke verifisert for OpenSpec | Se §14 |

## 10. Datakvalitet

`OpenSpecStatusService.Reconcile` gir disse kontrollene (Pass / Fail / Ikke verifisert / Ikke aktuelt):

- **Klassifisering og telling:**
  - D01a: alle dokumenter er klassifisert én gang.
  - D01: krav fra uavhengig overskriftsskanning av lagret original-Markdown = tolket + utelatt.
  - D05: samme kontroll for scenarioer.
  - D06: foreldreløse scenarioer = diagnostikk.
- **Katalog og evidens:** D01b (katalog = tolket) og D01c (evidens = tolket).
- **Livssyklus:** D02 (forslag ikke i evidens), D03 (arkiv ikke i evidens).
- **Identitet og grupper:**
  - D07a: kandidater = scenarioer + krav uten scenario. Referansen beregnes fra modellen, uavhengig av dashbordet.
  - D04: ingen dupliserte identiteter.
  - D04b: bare `os:`-nøkler.
  - D08a: hver identitet kan slås opp.
  - D07: modul = «Uten modultilknytning».
  - D08: Testoversikt leser samme katalogrevisjon.
- **Diagnostikk og kilde:** L06/L08 (konflikter og uavklarte mål er rapportert), D06b (ikke-støttede kilder synlige), S00 (skjema verifisert), V01 (kildeversjon konsistent).

En katalog uten kildemodell gir **IKKE VERIFISERT**, aldri PASS. Spec Kit-kataloger gir **Ikke aktuelt**: Spec Kit-datakvaliteten berøres ikke. Ingen tall er hardkodet.

**Datakvalitet-senteret (fra 008):** `DataQualityEngine` har fått en valgfri OpenSpec-avstemming. For en OpenSpec-katalog erstattes Spec Kit-referansekontrollene (kilde og import, kravidentiteter, dashbordtall) av OpenSpec-kontrollene (`openspec.*`). Erstatningen står eksplisitt i «Skipped» i rapporten. For en Spec Kit-katalog kjører alt som før; testen `DataQualityOpenSpecTests` viser identiske kontroller med og uten OpenSpec-koblingen. Grunnen er at senterets referanseregler (historie + nummer, `<modul>/specs`) ville gitt falske avvik for OpenSpec, blant annet modulen «openspec».

## 11. Sikkerhet og ytelse

- **ZIP-traversering:** den eksisterende arkivleseren avviser `../` (`ARCHIVE_PATH_TRAVERSAL`) før noen rute leser noe (P06). OpenSpec-ruten avviser i tillegg usikre stier (traversering, rotsti, stasjonsbokstav, kontrolltegn) og duplikatstier uavhengig av store/små bokstaver (P04).
- **Grenser:**
  - Maksimalt 5 000 dokumenter (ellers `RouteProblem`) og 2 M tegn per spesifikasjon (ellers ikke støttet med diagnostikk).
  - 20 000 tegn per linje, 5 000 krav, 50 000 scenarioer og 200 steg per scenario.
  - 2 000 diagnostikkmeldinger og 24 M tegn lagret original-Markdown.
  - Regex-uttrykkene er forankret og har tidsavbrudd på 200 ms. Ruten er avbrytbar (P07).
- **Ytelse:** 1 200 krav og 2 400 scenarioer bygges på under 1 s (P01). Server-søk når kandidater forbi klientvinduet på 2 000 (P02).
- **Isolasjon:** modellstore er nøklet per prosjekt og fingeravtrykk; formatinnstillingen er per applikasjon (P05).
- **Ingen skript, ingen nettverk:** `config.yaml` leses som tekst (bare `schema:`). Ingen Docker/Podman, ingen administratorrettigheter, ingen Internett.

## 12. Implementerte tester

**Backend – nye** (`BirkNext.Api.Tests`):
- `Services/Specifications/SpecKitRegressionBaselineTests.cs` (S01–S04, gyllen fil `Fixtures/speckit-golden-m2lb.json`, 512 kB, tatt opp ved `9af21c93` før endringer)
- `Services/Specifications/OpenSpecRouteTests.cs` (O01–O10, L01–L07, D01–D07, P01–P07, M10, identitetsregler)
- `Services/Specifications/OpenSpecIntegrationTests.cs` (midlertidig Postgres-database: C01–C08, S01–S08, M01–M09, D08, ADO)
- `ProjectImport/ProjectImportSpecificationRoutingTests.cs` (C08: ekte ZIP gjennom Preview → Commit)

**Frontend – nye** (`BirkNext.Web.Tests`):
- `Components/SpecificationFormatUiTests.cs` (C01/C02/C05/C06/C07, D, M05/M07, S05)

| Matrise-ID | Test(er) |
|---|---|
| S01–S04 | `SpecKitRegressionBaselineTests`, `OpenSpecIntegrationTests.S01…` |
| S05 | `S01…` (Testoversikt-JSON identisk med/uten ruter), `SpecificationFormatUiTests.S05…` |
| S06, S08 | `C06_C07…` (plan-JSON og revisjon byte-identiske etter formatbytte) + eksisterende suite |
| S07 | `C06_C07…` (Spec Kit-katalog beholdt) |
| O01–O10 | `OpenSpecRouteTests.O01…O10` |
| L01–L07 | `OpenSpecRouteTests.L01…L07`, `Unresolved_delta_targets…` |
| C01–C08 | `OpenSpecIntegrationTests.C01…/C05…/C06_C07…/C08…`, `ProjectImportSpecificationRoutingTests`, `SpecificationFormatUiTests` |
| M01–M09 | `OpenSpecIntegrationTests.M01_to_M08…` (inkl. M04/M09), `SpecificationFormatUiTests.M05_M07…` |
| M10 | `OpenSpecRouteTests.M10…` |
| D01–D08 | `OpenSpecRouteTests.D…`, `OpenSpecIntegrationTests.M01…` (D08 mot database) |
| P01–P07 | `OpenSpecRouteTests.P01…P07` |

## 13. Kjørte tester og resultater

Se tabellen under. Tallene er fra faktiske kjøringer 2026-11-27 i denne worktreen.

| Kjøring | Resultat |
|---|---|
| Spec Kit golden (før endringer, opptak ved HEAD) | 2/2 bestått |
| Spec Kit golden (etter alle endringer) | 2/2 bestått |
| Nye backend-tester (OpenSpec + golden + import) | 41/41 bestått |
| Nye frontend-tester | 7/7 bestått |
| Full backend-suite før sammenslåing | 7 693 bestått, 1 feilet, 20 hoppet over (7 714) |
| Spec Kit golden etter sammenslåing med 008 | 2/2 bestått (008 + OpenSpec gir samme Spec Kit-utdata som fasiten fra `9af21c93`) |
| Full backend-suite etter sammenslåing | 7 726 bestått, 4 feilet, 20 hoppet over (7 750, 13 min); de 4 består isolert, se §13a |
| Full frontend-suite etter sammenslåing | 9 941/9 941 bestått |
| Datakvalitet-tester (008 + nye) etter integrasjon | 33/33 bestått |
| Nye backend-tester etter integrasjon | 44/44 bestått |
| Full frontend-suite | 9 911/9 912 bestått; 1 ustabil (`ProjectImportMultiImportTests.Import_source_opens_import_mode…`) feilet under last og består 3/3 isolert |
| Nettleser (Playwright) | **IKKE KJØRT – IKKE VERIFISERT** |

### 13a. Full backend-suite

Den ene feilen var `ApiReviewEngineTests.CI_DEF_3_the_connect_callback_lets_an_allowed_host_through` (ApiQuality, nettverksavhengig, ingen berøring med endringene her). Hele klassen `ApiReviewEngineTests` ble kjørt på nytt isolert: 113/113 bestått, altså en ustabil test under last. De 20 som ble hoppet over er eksisterende, eksternt gatede tester. Etter at linjeskiftene var rettet tilbake ble det bygget på nytt, og de nye testene ble kjørt igjen: backend 41/41 og frontend 7/7 bestått.

**Etter sammenslåing med 008:** Fire tester feilet i fullkjøringen: `RuntimeAuthenticationTests.Unattended_StatesFollowTheValidatedLogin`, `ApiReviewEngineTests.CI_DEF_3…`, `UpdaterTests.Rollback_is_allowed_only_when_the_database_was_not_changed` og `TestHostMemoryDiagnostics.A_disposed_API_host_releases_its_memory…`. Ingen av dem er i kode endret her. Etter nytt bygg ble de kjørt isolert, og alle besto (1/1, 113/113, 1/1, 1/1): de er tidsavhengige under full last. Fullkjøringen ble startet før Datakvalitet-integrasjonen. Den endringen er dekket av Datakvalitet-testene (33/33) og de nye testene (44/44), kjørt på nytt bygg.

## 14. Uverifisert atferd og kjente begrensninger

1. **Nettleserverifisering er ikke kjørt.** Ingen Playwright-test ble startet; UI-en er bare verifisert med bUnit.
2. **Datakvalitet-senteret** bruker OpenSpec-avstemmingen for OpenSpec-kataloger. Spec Kit-referansekontrollene kjøres ikke for dem (står i rapporten).
3. **Nettleserens tolkning i Specifications Explorer, Test Coverage-visningen, Artifact Traceability m.fl.** bruker Spec Kit-parseren og er markert «Ikke støttet» / «Ikke verifisert» for OpenSpec. Finn og forstå viser ingen tilleggskontekst (FR-seksjoner, avklaringer) for OpenSpec. Kandidater og tellinger påvirkes ikke.
4. **Leveranserapporter** (`DeliverySnapshotParser`) og **System Understanding** er ikke verifisert med OpenSpec-kilder.
5. **Applikasjoner uten kildeøyeblikksbilde** (rene dokumentarkiver) står ikke i applikasjonslisten. Ruten bruker arkivets egen identitet, så formatet kan bare velges når applikasjonen har en kildeimport.
6. **Arbeidsutkast i nettleseren** er avvist for OpenSpec (ingen OpenSpec-parser i nettleseren). Endringer må importeres.
7. **Valgt applikasjon** i innstillingskortet lagres ikke i URL ved omlasting (selve formatet er lagret på serveren).
8. **Ingen ekte OpenSpec-pakke** var tilgjengelig. Testene bruker en syntetisk, dokumentert fixture etter offisiell v2-struktur. Egendefinerte skjemaer er IKKE VERIFISERT.
9. **Kravkandidaters `CandidateId`** inneholder fortsatt utvalgsomfanget (eksisterende generisk regel). Den logiske kildeidentiteten (`SourceIdentity`) er stabil.
10. **Kjent latent Spec Kit-risiko** (v1-kolliderende identiteter for rene kravkandidater, 66 i M2LB ifølge forslaget) er bevisst **ikke** endret (utenfor omfang, ingen srcid/2).

## 15. GO/NO-GO

| Akseptansekriterium | Status |
|---|---|
| A. Spec Kit standard og intakt; identiteter, relasjoner, M2LB-antall og grupper uendret | **PASS** (golden + S01 + full suite) |
| B. OpenSpec eksplisitt per applikasjon, egen rute, ID-løse krav, historieløse scenarioer, gjeldende/forslag/arkiv adskilt, ingen KPI-inflasjon, stabil identitet | **PASS** (backend/bUnit) |
| C. Manual Testing fungerer for begge formater | **PASS på tjenestenivå / IKKE VERIFISERT i nettleser** |
| D. Uavhengig avstemming, ingen stille utelatelse, ingen gjettede identiteter | **PASS** (fixture + database + Datakvalitet-senteret) |
| E. Ingen srcid/2, ingen automatisk migrering, ingen destruktive endringer, ingen ADO-skriving, ingen Docker/Podman/Internett, ingen commit/push | **PASS** |

**Beslutning:** **NO-GO for produksjon. GO for intern pilot** på én ikke-produktiv applikasjon når følgende er gjort:

1. Manuell nettleserkontroll av Spesifikasjonsformat, Testoversikt-panelet og Datakvalitet med en ekte OpenSpec-import.
2. Gjennomgang av OpenSpec-identitetsreglene (§6) av produkteier/testleder.

## 16. Endrede filer

Endret i forhold til `008-traceability-first` (15 filer; linjeskift og BOM som i HEAD), i tillegg til `Services/DataQuality/DataQualityEngine.cs`:

- `AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj`, `Program.cs`
- `AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs`
- `AIAssisted/backend/BirkNext.Api/Services/Specifications/SpecificationCatalogService.cs`
- `AIAssisted/backend/BirkNext.Api/Services/TestPlanning/{AdoTestPlanTransferService, ScenarioSourceBasisResolver, TestPlanSuggestionService.Overview, TestScenarioDiscoveryEngine}.cs`
- `AIAssisted/frontend/BirkNext.Web/BirkNext.Web.csproj`, `Program.cs`, `Pages/Admin/SystemSettings.razor`, `Pages/ManualTesting.razor`, `Services/DocumentationCatalog.cs`
- `AIAssisted/shared/TestCoverageContracts.cs`

Nye:

- `AIAssisted/shared/SpecificationFormatContracts.cs`
- `AIAssisted/backend/BirkNext.Api/Controllers/SpecificationFormatController.cs`
- `AIAssisted/backend/BirkNext.Api/Services/Specifications/{SpecificationFormatSettingsStore, SpecificationProcessingRouter}.cs`
- `AIAssisted/backend/BirkNext.Api/Services/Specifications/OpenSpec/{OpenSpecModel, OpenSpecIdentity, OpenSpecSourceDiscovery, OpenSpecMarkdownParser, OpenSpecModelBuilder, OpenSpecSourceModelStore, OpenSpecStatusService}.cs`
- `AIAssisted/frontend/BirkNext.Web/Services/SpecificationFormatApiService.cs`
- `AIAssisted/frontend/BirkNext.Web/Components/Admin/SpecificationFormatSettingsCard.razor(.css)`
- `AIAssisted/frontend/BirkNext.Web/Components/ManualTesting/OpenSpecSourcePanel.razor(.css)`
- Tester (§12), `Services/DataQuality/DataQualityOpenSpecTests.cs` og `Fixtures/speckit-golden-m2lb.json`
- Denne rapporten

## 17. Git-status

Gren `feature/openspec-isolated-route`. `008-traceability-first` (`ed472b7a`) er slått sammen (fast-forward). Arbeidet ble lagt til side i en navngitt stash og brukt på nytt; to konflikter (navigasjonsoppføringer i `SystemSettings.razor` og `DocumentationCatalog.cs`) er løst ved å beholde begge sider. Alt er committet i én commit og pushet til `ado` og `origin` etter brukerens instruks. Hovedcheckouten (`BirkNext`) er ikke rørt.

## 18. Gjenværende risiko

| Risiko | Alvor | Tiltak |
|---|---|---|
| UI ikke verifisert i nettleser | P1 for release | Manuell/Playwright-kontroll før pilot |
| Nettleserbaserte moduler tolker OpenSpec som Spec Kit | P2 (synlig merket) | OpenSpec-støtte i explorere ved behov |
| Egendefinerte OpenSpec-skjemaer | P2 | Rapporteres som IKKE VERIFISERT |
| Applikasjoner bare med dokumenter mangler i formatlisten | P3 | Utvid applikasjonslisten ved behov |

Ingen gjenværende P0-dataintegritetsproblem er kjent i det implementerte omfanget.
