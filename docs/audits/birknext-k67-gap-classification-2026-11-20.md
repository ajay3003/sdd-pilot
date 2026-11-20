# K67-gap mot M2LB-kode, spesifikasjoner og tester — klassifisering (2026-11-20)

**Kilde:** `M2LB (8).zip` (18.11.2026), pakket ut og lest uten å bygge eller kjøre noe. Stier er relative til arkivets rot.
**Omfang:** K67-01…12 fra Integration suites, G-02/G-03/G-04 i `birknext-auto-integration-kode67-gap-register.md`, funnene fra M01-gjennomgangen
(september) og nye funn i samme område.
**Metode:** tre parallelle lesinger (PersonAdapter/CDC, PersonService/Autorisasjon, Skolenærvær-kjeden). Hovedpåstandene er kontrollert
direkte mot koden; det som er utledet og ikke lest, er merket *(utledet)*.

**Kategorier:** **1** Allerede avklart i spesifikasjon · **2** Implementert i kode, men ikke verifisert · **3** Delvis implementert ·
**4** Manglende implementasjon i M2LB · **5** Reell faglig beslutning mangler · **6** Testbarhetsproblem (testinngang eller observasjon mangler).
Hvert gap har én hovedkategori, og eventuelt en sekundær i parentes.

## Sammendrag

| Id | Gap | Kat. | Hva som faktisk gjenstår | Eier |
|---|---|---|---|---|
| A1 | CDC satte sikkerhetsnivå til konstant 0 | 2 (6) | Fikset i koden. Mangler en ekte Debezium-melding med nivå 2/3 og replay-fixturer med nivå ≠ 0 | PersonAdapter |
| A2 | Avvise eller videresende Kode 6/7 i adapteren (P-03 mot DEP-09) | **5** (6) | Arkitektgodkjenning av DEP-09, rydding i speckit-dokumentene, ende-til-ende-test av worker | Løsningsarkitekt |
| A3 | Ukjent nivå blir «Ingen» | 3 (5) | Fail closed for ukjente verdier, og test | PersonAdapter |
| A4 | Terminologi «nivå 2 (Kode 6)» | 3 | Rette spec- og tasks-teksten | PersonAdapter |
| A5 | Omklassifisering ved kilden | 2 (6) | Tester, «nyeste vinner» for Barn ved retry, backfill etter DEP-09 | PersonAdapter / Person |
| A6 | Logging av identitet og nivå | 3 (2) | Fjerne `birkId`+nivå fra loggen ved hver Barn-post, rette TS-10.2, ekte varsling | PersonAdapter |
| A7 | Retry/replay utenom vakten | 2 | Test av at avviste hendelser aldri havner i feilkøen | PersonAdapter |
| A8 | Avvist Barn (nivå mangler) går tapt | **4** (5) | En gjenopprettingsvei eller en beslutning om at tapet er akseptert | PersonAdapter / arkitekt |
| A9 | Vakten kjører før sletting forkastes | 3 | Rekkefølgen i ruteren | PersonAdapter |
| B1 | Profilen skjuler alle graderte barn, også for den som har tilgang (K67-07) | **3** (5) | SQL-filter som tar hensyn til tilgang, og tester med og uten tilgang | PersonService |
| B2 | eksternId-resolveren uten tilgangskontroll | **4** (5) | Policy, eller fjerning til fordel for REST-maskinoppslaget | PersonService |
| B3 | Tildeling/tilbakekalling via Person, og nødtilgang | **4** (5) | Flyt eller beslutning (FR-033), manglende endepunkt, kontroll av hvem som aktiverer nødtilgang | Person / Autorisasjon |
| B4 | Lekkasje via antall og eksistens | 2 (6) | Test på resolvernivå: gradert mot ikke-eksisterende. To latente lekkasjer når B1 rettes | PersonService |
| B5 | Tilgangskontroll på revisjonsloggen | 3 (2) | Kan ikke nås før B1 er rettet | PersonService |
| B6 | Leselogg feiler lukket | 2 (5) | Integrasjonstest; konflikt med GL-32 (outbox); getAuditLog/getGradertBarntilgang skriver ingen leselogg | PersonService |
| B7 | SikkerhetsnivaaChanged uten mottakere | **5** (2) | Hvem som skal reagere, og om `BirkId` hører hjemme i meldingen | Domene / arkitekt |
| B8 | Søk sjekker ikke ViewGradertBarn | **4** | Batch-sjekk for `ViewGradertBarn` *(utledet)* | PersonService |
| B9 | Søk logger ikke hvilke graderte barn som kom med | **4** | FR-007 | PersonService |
| B10 | Seed-data med et Kode 7-barn i basismigreringen | **5** | Om seed-data skal nå alle miljøer *(utledet)* | PersonService |
| B11 | Syntetiske Kode 6/7-personer (G-02) | **6** | Kode 6 finnes ikke i seed-data; Local gir all tilgang; DEV/QA trenger API-nøkkel og relasjon | Testleder / Person |
| K67-01/02/04 | Kode 6/7-rapport ved mottak | **1** | Ingenting i M2LB (FR-008). BirkNext merker den fortsatt som «beslutning mangler» (D2) | — |
| K67-03 | MU/MM-grensen | 1 (2) | Eventuelt én mottakertest med et Kode 6-barn | MU |
| K67-05 | Kode 6/7 i Bufdata-tall | 1 (5) | Avslag uten barn telles i leverte tall: et spørsmål for personvernombudet | Personvern |
| K67-08 | BirkNext-eksport uten identifikatorer | — | Ikke et M2LB-gap; se A6 for adapterens logg | BirkNext |
| K67-09 | Periodelås og publisering | 1 (**5**) | OQ-26: om tilbakeholdte rader noen gang skal leveres | Domeneeier |
| K67-10 | Korrigering og replay | 1 (2) | Tester av replay og tilbaketrekking for gradert barn | SkoleService |
| K67-11 | Nivået endres før lås | 1 (2) | Tester av nivå ned før lås og av endring etter lås | SkoleService |
| K67-12 | Retry og gjenoppretting | 2 | Test av avbrutt lås med gradert barn i perioden | SkoleService |
| K67-FL | Synlighet i oppfølgingslister | 1 | Spec-teksten bruker fortsatt det gamle operasjonsnavnet | SkoleService |
| OBS | Observasjon per melding ved mottak | **6** | Bevisst designvalg; det kan bare observeres etter lås eller i databasen | BirkNext / test |
| D1 | M01-proben brukte gammelt skjema, og et avvist kall ble lest som bestått | — | **Rettet i BirkNext** (se D1) | BirkNext |
| D2 | Suite-generatoren kjenner ikke avgjørelsene i spec | 6 | Besvar review-punktene med FR-referanser | Testleder |

**Konklusjon:**
- Av de fem «beslutning mangler»-punktene i G-03 er fire allerede avklart i SkoleService-spec (FR-008, FR-008a/b, FR-009). Den reelle åpne beslutningen er **OQ-26**.
- De alvorligste gjenværende punktene ligger ikke i Skolenærvær, men i **Person og adapteren**:
  - B1: autorisert gradert tilgang er umulig.
  - B2, B3 og B8: hull i tilgangskontrollen.
  - A2: avvise eller videresende er ikke godkjent.
  - A6: identitet og nivå logges.

---

## A. PersonAdapter / CDC (BiRK → Person)

**Hovedendring siden september:** P-03 er snudd. Koden videresender nå nivå 2 og 3 (DEP-09). Vakten avviser bare en Barn-post uten nivå.

### A1. CDC satte nivå 0 — kategori 2 (6)
- **Kode:** `PersonAdapter/src/M2LB.PersonAdapter.Worker/Workers/CdcProcessorWorker.cs:342-378`, `Deserialize`. Linje 371: `Sikkerhetsnivaa: GetOptionalInt(record, "Sikkerhetsnivå")`. `CdcEvent.Sikkerhetsnivaa` er `int?` (`Domain/Events/CdcEvent.cs:8`). Endringen står i `PersonAdapter/autodoc/CHANGELOG.md:53`.
- **Tester:** `tests/M2LB.PersonAdapter.Unit/Workers/CdcEnvelopeDeserializeTests.cs`:
  - `Deserialize_BarnEvent_ReadsSikkerhetsnivaaFromPayload`
  - `Deserialize_DeleteOperation_ReadsClassificationFromBeforeState`
  - `Deserialize_TableWithoutTheColumn_YieldsNullSikkerhetsnivaa`
  - `Deserialize_MissingPayloadWrapper_ReturnsNull`
- **Gjenstår:**
  - Feltnavnet `Sikkerhetsnivå` er «utledet fra Barn.sql, ikke observert» (`Domain/Security/SecurityClassificationGuard.cs:46-49`).
  - Ingen ekte BiRK/Debezium-melding med nivå 2/3 er lagret.
  - Alle replay-fixturene har nivå 0: `CdcReplay/tests/M2LB.CdcReplay.Tests/Baselines/wire-Barn.json` (7 rader) og `CdcReplay/src/M2LB.CdcReplay/SampleFixtures.cs:26`.

### A2. Avvise eller videresende Kode 6/7 — kategori 5 (6)
- **Kode:** `SecurityClassificationGuard.Evaluate` (`SecurityClassificationGuard.cs:32-64`). Linje 32-33: «DEP-09: every classification level, Kode 6/7 included, is forwarded». Ruteren kaller vakten først (`Domain/Routing/CdcRouter.cs:58-60`).
- **Spesifikasjonene er i konflikt.**
  - Sier fortsatt *avvis*:
    - `PersonAdapter/.specify/memory/constitution.md` §III (l. 79/84): «MUST NEVER reach this adapter… Reject the record immediately».
    - `PersonAdapter/specs/001-birk-person-adapter/spec.md` FR-006 (l. 206-208): «MUST reject any record with security level 2 or 3».
    - `data-model.md:98` og `quickstart.md:106`.
    - `specs/002-batch-cdc-self-heal/plan.md:41`.
  - Sier *videresend*:
    - `PersonAdapter/docs/BiRK-Person-adapter-—-Konstitusjon.md` P-03 «revidert oktober 2026, DEP-09» (l. 133-139). Versjonstabellen (l. 266) sier v0.2 «Venter på løsningsarkitekt».
    - `docs/person-birk-adapter-func-requirements-no.md` FK-3.1/3.2.
    - `docs/person-birk-adapter-test-spec-no.md` TS-4.1/4.2.
  - Åpne avkrysningsbokser i `SkoleService/docs/avhengigheter-bestilling.md:74-77`: arkitektgodkjenning, bekreftelse på at BiRK/CDC ikke filtrerer Kode 6/7, og at mottakerne av `person.barn` tåler Kode 6/7.
- **Tester:**
  - `tests/M2LB.PersonAdapter.Unit/Security/SecurityClassificationGuardTests.cs`: `Evaluate_BarnWithClassification_ReturnsAllowed` (nivå 0-3) og `Evaluate_BarnWithoutClassification_ReturnsRejected`.
  - `tests/M2LB.PersonAdapter.Integration/SecurityClassificationTests.cs`: `Kode67Barn_IsDeliveredWithItsClassification` og `BarnWithoutClassification_IsRejected_NoHttpCallToPersonModule_CheckpointAdvances`.
  - Alle bygger `CdcEvent` direkte. Sjekkpunkt-assertet i den siste kaller `AdvanceAsync` selv (l. 92-93) og observerer ikke workeren.
- **Gjenstår:**
  - Arkitekten godkjenner DEP-09.
  - Constitution, FR-006/007 og data-model justeres etter det.
  - Én test fra melding via `Deserialize` og ruter til worker-utfall.

### A3. Ukjent nivå blir «Ingen» — kategori 3 (5)
- **Kode:** `PersonAdapter/src/M2LB.PersonAdapter.Infrastructure/Mapping/ChildRegistrationMapper.cs:103-110`, `MapSikkerhetsnivaa`. Nivå 0-3 mappes riktig, men `_ => Ingen` står igjen.
- **Konsekvens:** Vakten fanger bare `null`, så verdier som 4 eller -1 lagres som ubeskyttet. Det strider mot Konstitusjon l. 137, «Adapteren skal aldri gjette et nivå».
- **Tester:** `ChildRegistrationMapperTests.Map_Sikkerhetsnivaa_MapsToCorrectGuid` dekker 0-3 og låser fast `null → Ingen`.
- **Gjenstår:** En beslutning (avvis, eller behandle som Kode 6), implementasjon og test.

### A4. Terminologi — kategori 3
- **Rett:** kode (`ChildRegistrationMapper.cs:107-108`), seed (`PersonService/.../Migrations/20261030141215_InitialCreate.cs:352-353`) og TS-4.1.
- **Fortsatt omvendt:** `PersonAdapter/specs/001-birk-person-adapter/spec.md:85` («security level 2 (Kode 6)»), `:91`, `:314` og `tasks.md:97`.
- **Gjenstår:** Rette teksten.

### A5. Omklassifisering ved kilden — kategori 2 (6)
- **Kode:**
  - Adapteren er tilstandsløs og sender nytt nivå videre (`CdcRouter.cs:103-111`, *utledet*).
  - Person: `PersonService/src/M2LB.PersonService.Application/Ingest/IngestService.cs:469-500`, `HandleSikkerhetsnivaaChangeAsync`.
  - Spec: Konstitusjon l. 135 («også … endringer av nivå») og FK-3.1.
- **Tester:** Ingen funnet i adapteren.
- **Risiko:**
  - Feilkø-retry av Barn har ingen «nyeste vinner»-sjekk (`Worker/Workers/FaultQueueProcessor.cs:107-160`). Kommune-postene har en slik sjekk (`CdcProcessorWorker.cs:248`, `:286`).
  - `BirkChangedAt` leser `Endringstidspunkt` (`ChildRegistrationMapper.cs:61`), mens meldingen har `EndretDato` (`ChildRegistrationMapperTests.cs:246`). Feltet blir dermed trolig alltid null.
  - Barn som ble levert før DEP-09 og senere fikk Kode 6, beholder sitt gamle nivå (Konstitusjon l. 141-142). Ingen plan for backfill er funnet.

### A6. Logging og observabilitet — kategori 3 (2)
- **Lekkasje:** `ChildRegistrationMapper.cs:36-38` logger `birkId`, `Sikkerhetsnivå` og `PersonFK` på Information-nivå for hver Barn-post. Under DEP-09 kobler loggen dermed Kode 6/7-barns identitet til nivået, i strid med Konstitusjon l. 139: «Sikkerhetsnivået og andre verdier fra posten logges aldri».
- TS-10.2 (`docs/person-birk-adapter-test-spec-no.md:454-461`) forventer fortsatt den gamle loggmeldingen.
- **Varsling:** `IAlertService` er implementert av `Infrastructure/Observability/LoggingAlertService.cs`, som er en stub (l. 6). Telleren `birk.kode67.rejections` finnes, men testen (`TelemetryNamesTests.The_guard_publishes_its_counter_on_the_pinned_meter`) sjekker bare at instrumentet eksisterer.
- **Gjenstår:** Fjerne identifikatorene fra loggen, rette TS-10.2 og koble på ekte varsling.

### A7. Retry og replay utenom vakten — kategori 2
- **Replay:** går via Event Hubs og passerer vakten (`CdcReplay/src/M2LB.CdcReplay/Program.cs:145-168`).
- **Feilkøen:** lagrer den mappede posten *etter* vakten (`CdcProcessorWorker.cs:221-236`). Gjenleveringen (`FaultQueueProcessor`, `AdminController.TriggerReprocessing`, `AdminController.cs:14`) kjører ikke vakten på nytt. Under DEP-09 er nivået bevart, så dette er ingen omgåelse.
- **Gjenstår:** Test av at avviste hendelser aldri havner i feilkøen.

### A8. Avvist Barn går tapt — kategori 4 (5)
- **Kode:** Ved nivå `null` logges hendelsen og sjekkpunktet flyttes (`CdcProcessorWorker.cs:136-139`), men ingenting legges i kø.
- **Konsekvens:** Barnet, eller omklassifiseringen av det, er tapt for godt.
- **Gjenstår:** En gjenopprettingsvei, eller en eksplisitt beslutning om at tapet er akseptert.

### A9. Vakten kjører før sletting forkastes — kategori 3
- **Kode:** `CdcRouter.cs:59` kommer før `:79`.
- **Konsekvens:** En sletting av Barn uten nivåfelt utløser et Kode 6/7-varsel.

---

## B. PersonService / Autorisasjon

GraphQL-navnene er nå engelske: `getBarn`, `searchBarn`, `getAuditLog`, `getGradertBarntilgang` og `getBarnRegistreringIdFromEksternId` (`PersonService/autodoc/schema.graphql:31-56`). Operasjonene heter nå `PersonService:*`.

### B1. Profilen skjuler alle graderte barn — kategori 3 (5)
- **Kode:** `PersonService/src/M2LB.PersonService.Infrastructure/Persistence/Repositories/PersonRepository.cs:97-106`, `GetBarnProfileAsync`, har `.Where(b => b.SikkerhetsnivaaType != null && !b.SikkerhetsnivaaType.RequiresGradertAccess)`. `GetPersonAsync` (l. 15-30) gjør det samme.
- **Konsekvens:** Tilgangssjekken i `BarnProfileService.GetBarnProfileAsync` (`Application/BarnProfile/BarnProfileService.cs:57-64`) kan aldri nås. Søk viser barn som brukeren har tilgang til (`PersonRepository.cs:57`), men profil og revisjonslogg gjør det ikke.
- **Spec:**
  - `PersonService/specs/001-person-module/spec.md` FR-008: «Kode 6/7: additionally requires `Person:SeGradertBarn` for that child».
  - SC-003: «zero false negatives (child invisible to authorised user)».
  - Dagens oppførsel er dokumentert som fakta, ikke som en beslutning: `autodoc/schema.graphql:21-22` sier «return null for classified children regardless of grants (PP-04, 404-not-403)».
  - `SkoleService/specs/001-skoleservice/contracts/avhengigheter/person-maskinoppslag.md:13` bygger på denne oppførselen.
- **Tester:**
  - `SecurityFilterIntegrationTests.GetBarnProfile_WithKode7_ReturnsNull` og `GetBarnProfile_WithKode6_ReturnsNull` låser fast oppførselen.
  - `BarnProfileEvaluatedOperationNameTests.GetBarnProfile_ForAGradertBarn_EvaluatesExactlyTheViewGradertBarnName` består bare fordi repository er mocket.
- **Gjenstår:** Avklare konflikten mellom FR-008/SC-003 og «regardless of grants». Deretter et filter som tar hensyn til tilgang, og tester for lesing med og uten tilgang.
- **Betydning for test:** Den autoriserte positive kontrollen i M01 (K67-07) **vil feile mot ekte M2LB**, og det er et reelt funn, ikke en testfeil.

### B2. eksternId-resolveren — kategori 4 (5)
- **Kode:**
  - `PersonService/src/M2LB.PersonService.Api/GraphQL/Queries/BirkIdQueryResolver.cs:13-21`, `GetBarnRegistreringIdByEksternIdAsync` (`getBarnRegistreringIdFromEksternId`). Kommentaren sier: «No Autorisasjon check here — this is meant to be used by Geir».
  - Repository har heller ikke noe graderingsfilter (`PersonRepository.cs:108-112`).
  - Endepunktet krever nå innlogging: `Api/Program.cs:77`, `MapGraphQL().RequireAuthorization("AuthenticatedUser")`.
- **Konsekvens:** Enhver innlogget bruker får UUID-en til et Kode 6/7-barn.
- **Spec:** SkoleService DEP-08 (`SkoleService/specs/001-skoleservice/spec.md:1049`): «Today's `hentBarnRegistreringIdFraEksternId` has no access check».
- **Tester:** `EksternIdQueryTests` dekker bare repository.
- **Maskinoppslaget:** `Api/Rest/Internal/BarnMachineLookupEndpoints.cs:25-27` krever app-rollen `Barn.Lookup` og returnerer Kode 6/7 med nivå. Det er spesifisert (`person-maskinoppslag.md:34`) og testet (`MachineLookupRepositoryTests.Kode6_IsReturnedWithSikkerhetsnivaa`). I Local gir `DevAuthHandler.cs:48` alle `Barn.Lookup`.
- **Gjenstår:** Policy på resolveren, eller fjerning av den til fordel for REST-maskinoppslaget.

### B3. Tildeling, tilbakekalling og nødtilgang — kategori 4 (5)
- **Kode, Person:**
  - Ingen mutasjon (`autodoc/schema.graphql:25`: «the unused grant mutation types were deleted in spec 003»).
  - `Application/GradertBarntilgang/GradertBarntilgangService.cs` er skrivebeskyttet.
  - `Application/Interfaces/IGradertBarntilgangClient.cs:12-14` har en TODO: endepunktet `/api/autorisasjon/v1/accesses/barn` «does not exist in the Autorisasjon service today».
- **Kode, Autorisasjon:**
  - Tildeling og tilbakekalling finnes: `ChildRelationMutationType.cs`, `createBarnRelation`/`revokeBarnRelation`.
  - Nødtilgang: `EmergencyAccessMutationType.ActivateEmergencyAccess` har ingen policy (`GraphQLFieldGuard.cs:15-20`), og `ActivateEmergencyAccessCommand.cs:87-92` sammenligner aldri den som aktiverer med den som får tilgang. Dette er kjent i `AutorisasjonService/specs/006-default-roles-admin-enforcement/design.md:53-56`: «any logged-in user can activate emergency access for anyone».
- **Spec:** PersonService FR-013–016 og FR-033: «The presentation layer MUST call only the Person module».
- **Gjenstår:** Flyt via Person, eller en beslutning om å droppe FR-033. Endepunktet som mangler, og kontroll av hvem som aktiverer nødtilgang.

### B4. Lekkasje via antall og eksistens — kategori 2 (6)
- **Antall:** Totalen beregnes etter graderingsfilteret (`PersonRepository.cs:57,75`). Testet i `SecurityFilterIntegrationTests.SearchBarn_WithNoGrants_ReturnsOnlyNonClassifiedChildren` og `SearchBarn_WithGrantForKode7_Kode7BecomesVisible_Kode6StillAbsent`.
- **Profil:** Et gradert og et ikke-eksisterende barn gir begge `null` (`BarnProfileQueryResolver.cs:48-53`).
- **Gjenstår:**
  - Test på resolvernivå som sammenligner gradert mot ikke-eksisterende.
  - To latente lekkasjer når B1 rettes *(utledet)*:
    - `AuditLogQueryResolver.cs:55` kaster `PersonNotFoundException`, og unntaket mappes ikke (`GraphQLExtensions.cs:26-35`).
    - `EvaluateAsync` kjøres bare for graderte barn, så når Autorisasjon er nede, får bare de `AUTHORIZATION_UNAVAILABLE`.

### B5. Revisjonsloggen — kategori 3 (2)
- **Kode:** Sjekken i `AuditLogQueryResolver.cs:47-56` kan ikke nås, fordi den går via `GetBarnProfileAsync` (B1).
- **Spec:** FR-029.
- **Tester:** Bare `EvaluatedOperationNameTests.GetAuditLog_ForAGradertBarn_EvaluatesExactlyTheViewGradertBarnName`, med mock.

### B6. Leselogg feiler lukket — kategori 2 (5)
- **Kode:** `BarnProfileService.cs:66-74`, `Infrastructure/AuditLog/LeseloggPublisher.cs:27-32` og `MessagingExtensions.cs:37` («fail fast»). Maskinoppslaget gjør det samme (`MachineLookupService.cs:55-61`).
- **Tester:** `BarnProfileServiceTests.GetBarnProfile_ThrowsAndReturnsNoData_WhenPublishFails` og `MachineLookupServiceTests.FindByIdentifier_ReadLogFails_ReturnsNoData`, begge med mock.
- **Gjenstår:**
  - Konflikten med `docs/M2LB-Utviklingsretningslinjer.md` GL-32 (outbox).
  - `getAuditLog` og `getGradertBarntilgang` skriver ingen leselogg.
  - Ingen integrasjonstest.

### B7. SikkerhetsnivaaChanged — kategori 5 (2)
- **Publisering:** Bare ved innlesing: `IngestService.HandleSikkerhetsnivaaChangeAsync` (`IngestService.cs:469-500`), via outbox med emnet `SikkerhetsnivaaChanged_CRITICAL`.
- **Tester:** `OutboxPatternTests.SikkerhetsnivaaChanged_OutboxMessage_HasHighPriority` og baseline-filen `Contract.Tests/Events/Baselines/SikkerhetsnivaaChanged_CRITICAL.json`.
- **Mottakere:** Ingen funnet. `docs/architecture/messaging.md:141` filtrerer bare `BarnRegistered` og `BarnEksternIdChanged`.
- **Spec:** FR-025.
- **Gjenstår:**
  - En beslutning om hvem som skal reagere (Autorisasjon-cache, SkoleService, tilganger som må fjernes).
  - Meldingen inneholder `BirkId`, mens FR-026 tillater «only UUID identifiers and metadata».

### B8. Søk sjekker ikke ViewGradertBarn — kategori 4 *(utledet)*
- **Kode:** `Application/BarnSearch/BarnSearchService.cs:39-41` bruker `GetBarnWithAccessAsync`, som returnerer alle barn med en barnespesifikk rolle eller nødtilgang (`AutorisasjonController.cs:120-136`).
- **Kontrakten:** `PersonService/specs/001-person-module/contracts/auth-integration.md:58-70` forventer en batch-sjekk for `SeGradertBarn`.
- **Konsekvens:** En rolle uten `ViewGradertBarn` gjør Kode 6/7-barnet synlig i søk.

### B9. Søk logger ikke hvilke graderte barn som kom med — kategori 4
- **Spec:** FR-007: «MUST additionally log which classified children were included».
- **Kode:** `BarnSearchService.cs:46-48` logger bare antall tilganger.

### B10. Seed-data i basismigreringen — kategori 5 *(utledet)*
- **Kode:** `PersonService/src/M2LB.PersonService.Infrastructure/Persistence/PersonDbContext.cs:418-510` (`HasData`), uten miljøsperre. Det inkluderer et Kode 7-barn (B999-9002).
- **Gjenstår:** En beslutning om seed-dataene skal nå alle miljøer.

### B11. Syntetiske Kode 6/7-personer (G-02) — kategori 6
- **Seed-data:** Ett Kode 7-barn, ingen Kode 6.
- **Local:** Autorisasjons `DevDataSeeder` (Local) gir både DevUser1 og DevUser2 full tilgang, inkludert `ViewGradertBarn`. Dev-modus i Person «grants everything» (`AdminAuthorizationExtensions.cs:13`). «Uten tilgang» kan derfor ikke observeres lokalt.
- **I DEV/QA må testleder:**
  1. Opprette syntetisk Person og Barn på nivå 2 og 3 via innlesing med API-nøkkel (`Api/Rest/Ingest/BarnIngestEndpoint.cs`, `ApiKeyPolicy`).
  2. Skaffe to Entra-testidentiteter med `SearchBarn` og `ViewBarnProfile`.
  3. Opprette `createBarnRelation` for bare én av dem, gjort av en administrator.
- **Merk:** Selv da er profil og revisjonslogg `null` (B1).

---

## C. Skolenærvær-kjeden (K67-01…12)

`SS` står for `SkoleService`. `spec` står for `SS/specs/001-skoleservice/spec.md`.

### K67-01/02/04. Kode 6/7-rapport ved mottak — kategori 1
- **Spec:**
  - FR-008 (spec:594): «Reports for Kode 6/7 children MUST be processed like any other report».
  - US2, scenario 1 (spec:124-126).
  - FR-002 (spec:563-564): et svar uten nivå holdes tilbake, og behandles aldri som «ugradert».
- **Kode:** `SS/src/M2LB.SkoleService.Api/Rest/ReportIntake/ReportIntakeHandler.cs:54-56, 106-115` (`HandleAsync`) og `SS/src/M2LB.SkoleService.Infrastructure/Person/PersonLookupClient.cs:43-47`.
- **Tester:** `GradertBarnIntakeTests`:
  - `A_report_for_a_Kode_6_or_7_child_is_accepted_stored_and_versioned_like_any_other`
  - `No_column_holds_a_gradering_except_the_one_frozen_at_lock`
  - `A_rejection_for_B_carries_Bs_id`
  - `A_Person_answer_without_sikkerhetsnivaa_is_never_stored_as_unclassified`
- **Gjenstår:** Ingenting i M2LB. Premisset i scenariet («lagre, holde tilbake eller avvise?») er allerede besvart.

### K67-03. MU/MM-grensen — kategori 1 (2)
- **Spec:** `MeldingsutvekslerReceiver/specs/001-meldingsutveksler-receiver/spec.md` FR-015 (l. 463-465): «MUST NOT expect, infer or record … anything about the child's classification». Antakelsene i l. 669-671 sier det samme.
- **Kode:** Svaret er identisk, `{"outcome":"Received"}` (`ReportIntakeHandler.cs:157-158`).
- **Tester:** Ingen med gradert barn.

### K67-05. Kode 6/7 i Bufdata-tall — kategori 1 (5)
- **Spec:**
  - FR-009 (spec:616-618): konfigurerbart, av som standard.
  - FR-035 (spec:799-804): den leverte fordelingen utelater Kode 6/7.
  - FR-037.
- **Kode:**
  - `SS/src/M2LB.SkoleService.Domain/Skolenaervaer/Dekning/ExpectationCalculator.cs:80-102` (`Freeze`).
  - `DekningCalculator.cs:63-112` (`Calculate`).
  - `SS/src/M2LB.SkoleService.Infrastructure/Delivery/ParquetWriters.cs:103-110`.
  - `DekningStatisticsReader.cs:32`.
- **Tester:**
  - `DekningStatisticsTests.With_the_option_off_at_lock_variant_DELIVERED_equals_the_delivered_breakdown_and_unknownBarn_is_null`
  - `DekningStatisticsTests.With_the_option_on_at_lock_variant_FULL_counts_Kode_6_children_and_the_unknown_children`
  - `DekningCalculatorTests.UnknownBarn_appears_only_in_Full_and_only_with_the_option_on`
- **Gjenstår:** `rejectionsWithoutBarn` telles også i den leverte varianten (`DekningCalculator.cs:109`), og kan omfatte Kode 6/7-barn som ikke finnes i Person. DPIA-en påpeker dette (`docs/analysis/skolenaervaer-code-review-dpia.md:168`). Spørsmålet går til personvernombudet.

### K67-08. BirkNext-eksport — ikke et M2LB-gap
Dette er BirkNexts egen redigering av bevis og eksport, og den er dekket av BirkNext. Logger i målsystemet blir ikke vurdert. Se A6: adapterens logg kobler i dag `birkId` og nivå.

### K67-09. Periodelås og publisering — kategori 1 (5)
- **Spec:** FR-008b (spec:612-615): «Rows for children who are Kode 6/7 at lock MUST NOT be part of the Bufdata dataset; their reports MUST stay stored». `SS/specs/001-skoleservice/contracts/hendelser.md:53-56`: hendelsen er `Locked`, enten raden leveres eller holdes tilbake.
- **Kode:**
  - `SS/src/M2LB.SkoleService.Infrastructure/Locking/PeriodLocker.cs:171-182` (`FreezeAsync`).
  - `DeliveryRowBuilder.cs:44`.
  - `Report.cs:93-100`.
  - `DeliveryExport.cs:109-115`.
- **Tester:** `GraderingAtLockTests` (4 stk.), `DeliveryRowBuilderTests.Reports_for_children_who_are_Kode_6_or_7_at_lock_give_no_row` og `PeriodLockEndToEndTests`.
- **Gjenstår:** **OQ-26** (spec:512 og 612-615): «Whether and how they are delivered later is open». Dette er den reelle åpne faglige beslutningen i kjeden.

### K67-10. Korrigering og replay — kategori 1 (2)
- **Spec:** FR-008, FR-018 (idempotens), US3 scenario 3 (korrigering etter lås avvises) og FR-006/FR-020.
- **Kode:** `ReportIntakeHandler.cs:44-46` (replay) og `:124-141` (`CloseChainAsync`). Ingenting av dette avhenger av nivå.
- **Tester:** Korrigering for Kode 6/7 dekkes av `GradertBarnIntakeTests`.
- **Gjenstår:** Tester av identisk replay og tilbaketrekking for et gradert barn.

### K67-11. Nivået endres før lås — kategori 1 (2)
- **Spec:** FR-008a (spec:602-611): «At lock, classification MUST be read from Person for every child in the period and frozen». I tillegg FR-009a, US2 scenario 7 og et grensetilfelle i spec:428-433.
- **Kode:** `SS/src/M2LB.SkoleService.Infrastructure/Dekning/BasisCollector.cs:63-79` (`GetExternalAsync`). Ingen egen kopi av nivået lagres.
- **Tester:**
  - `GraderingAtLockTests.A_child_Person_makes_Kode_7_before_the_lock_is_withheld_with_no_event_involved`
  - `FollowUpListAccessTests.Changing_the_level_in_Person_between_two_reads_changes_visibility_on_the_next_read_with_no_event`
- **Gjenstår:** Tester av nivå ned før lås (rapporten skal da leveres) og av endring etter lås (FR-025).

### K67-12. Retry og gjenoppretting — kategori 2
- **Kode:**
  - Innsamlingen kjører utenfor transaksjon: hvis Person feiler, kastes et unntak (`PeriodLocker.cs:65`, `:100`), og retry gjør alle oppslag på nytt.
  - Tilbakeholdte rapporter får et siste oppslag; mangler nivået, fryses det som Kode 6 (`PeriodLocker.cs:96-98`).
- **Tester:** `LockRecoveryTests` (3 stk.) har ikke gradert barn. `HeldReportTests.A_held_report_that_resolves_as_Kode_6_is_processed_and_the_hold_deleted`.
- **Gjenstår:** Test av en avbrutt lås med et gradert barn i perioden.

### K67-FL. Synlighet i oppfølgingslister — kategori 1
- **Spec:** FR-008 (spec:595-601).
- **Kode:** `SS/src/M2LB.SkoleService.Domain/Shared/GraderingRule.cs:9-17` (`IsVisible`), `FollowUpService.cs:86-91` og `AutorisasjonFilter.cs:13,36`.
- **Tester:** `FollowUpListAccessTests` (15 stk.) og `GraderingRuleTests` (5 stk.).
- **Gjenstår:** Spec-teksten sier fortsatt `Person:SeGradertBarn`, mens koden bruker `PersonService:ViewGradertBarn`.

### OBS. Observasjon per melding ved mottak — kategori 6
- **Designvalg:** Ingen svart-boks-observasjon per melding, og det er bevisst (FR-006, DEC-60, MU FR-015).
  - Svaret er `Received` for alle.
  - Loggen og metrikkene inneholder bare utfall og årsak.
  - Hendelsen har ingen meldings-id.
- **Når utfallet kan observeres:** Etter lås, via `dekning_basis_barn.sikkerhetsnivaa_at_lock`, `report.status = 4` (Withheld) og de leverte filene. Ellers bare i databasen.
- **Testinngang:** `SS/src/M2LB.SkoleService.Api/local/person-stub.json` har bare ett gradert barn (nivå 3), ingen Kode 7 og ingen uten nivå.
- **Gjenstår:**
  - Legge Kode 7-barn og barn uten nivå til i stuben.
  - I BirkNext: modellere observasjonen «etter lås» i stedet for «per melding» (D3).

### Dokumentkonflikt om DEP-09 i SkoleService — kategori 1 (5)
- `person-maskinoppslag.md:87` sier at adapteren nå videresender Kode 6/7.
- Disse beskriver fortsatt at Kode 6/7 avvises:
  - grensetilfellet i spec:432-433 («today PersonAdapter drops it»)
  - `docs/operations/skolenaervaer-produksjonsplan.md:195`
  - DPIA-en (`skolenaervaer-code-review-dpia.md:81`, `:90`)

---

## D. Funn i BirkNext

### D1. M01-proben brukte gammelt skjema, og et avvist kall ble lest som bestått — rettet
- **Feil 1, gammelt skjema:** Proben sendte `hentBarn`, `soekBarn` og `hentRevisjonslogg`, men Person bruker nå `getBarn`, `searchBarn` og `getAuditLog`.
- **Feil 2, avvist kall lest som bestått:** Et kall som endepunktet avviste, ble lest som «ingen data». Det gjaldt feil uten `data`, 401 og svar som ikke var JSON. Dermed ble alle kontrollene for uautorisert identitet registrert som bestått, selv om spørringen aldri ble kjørt.
- **Rettet i `ClassificationLiveProbe.cs`:**
  - Skjemaet oppdages én gang per kjøring: gjeldende engelsk først, deretter det tidligere norske.
  - Et svar teller bare når `data` inneholder det spurte feltet.
  - Avviste kall gir «ikke besvart», aldri «bestått».
  - Hvis ingen av skjemaene virker, vurderes ingenting.
- **Tester:**
  - Nye backend-tester: `TheCurrentEnglishPersonSchemaIsDetectedAndAssessedLikeTheEarlierOne`, `AQueryTheEndpointRejectsIsNeverAPass` og `AnEndpointThatRejectsEveryCallAssessesNothing`.
  - Stand-in-endepunktet i nettlesertesten bruker nå det engelske skjemaet.

### D2. Suite-generatoren kjenner ikke avgjørelsene i spesifikasjonen — kategori 6
- **Problem:** `IntegrationSuiteComposer.Security` merker K67-01/02/04 med «No approved rule is recorded» og «Needs domain decision». K67-05 og K67-10/11 får spørsmål som spec allerede besvarer (FR-008, FR-008a/b, FR-009).
- **Årsak:** BirkNext leser ikke M2LB-spesifikasjonens beslutninger. Avgjørelsen registreres som svar på review-punktet (stabile nøkler, for eksempel `security:intake-rule`).
- **Anbefaling:** Testleder besvarer `security:intake-rule`, `security:bufdata-aggregate`, `security:correction` og `security:reclassification` med «Acceptable» og FR-referansene over. Bare `security:period-lock` (OQ-26) forblir åpen.

### D3. «Observert per melding» — kategori 6
- **Problem:** `IntakeObservedPerMessage` er `false`, og det stemmer med M2LB, der det er et bevisst valg. K67-01/02/04 kan dermed bare verifiseres ved observasjon etter lås.
- **Anbefaling:** BirkNext bør få en observatør for «utfall ved lås», i stedet for å vente på en per-melding-observatør som ikke kommer.

### D4. Oppdatering av gap-registeret (G-02/G-03/G-04)
- **G-02:** Gjelder fortsatt, men veien er nå kjent (B11): innlesing med API-nøkkel og `createBarnRelation`. Seed-dataene har ingen Kode 6.
- **G-03:** Fire av fem punkter er allerede avklart i spec. Reelt åpent:
  - OQ-26
  - avslag uten barn i leverte tall
  - A2 (DEP-09 ikke godkjent)
  - A3 (ukjent nivå)
  - B7 (mottakere av SikkerhetsnivaaChanged)
- **G-04:** Kan nå kjøres (D1 og K67-06/07-koblingen er rettet i `9356e1e7`). Mot ekte M2LB vil den autoriserte positive kontrollen **feile** på grunn av B1, og det er forventet og riktig.

## Prioritert neste steg

1. **M2LB, sikkerhet:**
   - B2: eksternId uten tilgangskontroll.
   - B3: hvem som helst kan aktivere nødtilgang.
   - B8: søk uten `ViewGradertBarn`.
   - A6: identitet og nivå i adapterloggen.
2. **M2LB, funksjon:** B1 (gradert tilgang i profil og revisjonslogg), så B5 og de latente lekkasjene i B4.
3. **Beslutninger:**
   - A2: godkjenne DEP-09.
   - A3: ukjent nivå.
   - A8: tapte poster.
   - B7: mottakere av SikkerhetsnivaaChanged.
   - OQ-26.
   - Personvern: avslag uten barn i leverte tall.
4. **Tester i M2LB:**
   - ende-til-ende-test av worker (A2)
   - nivå ned før lås, og endring etter lås (K67-11)
   - avbrutt lås med gradert barn (K67-12)
   - replay og tilbaketrekking (K67-10)
   - resolvernivå: gradert mot ikke-eksisterende (B4)
5. **BirkNext:**
   - besvare review-punktene etter D2
   - observatør etter lås (D3)
   - kjøre M01 mot DEV/QA når B11 er på plass
