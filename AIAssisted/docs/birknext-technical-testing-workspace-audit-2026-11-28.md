# Technical Testing: revisjon av arbeidsflaten (2026-11-28)

**Status:** revisjon før implementering. Kildene er kode i BirkNext (`AIAssisted/`) og det ekte M2LB-arkivet (`M2LB (9).zip`). Arkivet er bare lest, aldri bygget eller kjørt.

## 1. Navigasjon slik den var

Sidemenyen kommer fra `frontend/BirkNext.Web/Services/NavigationCatalog.cs`.

| Menypunkt | Rute | Fil | Ekte eller plassholder | Data |
|---|---|---|---|---|
| Testoversikt (Manual) | `manual-testing`, også `manual-testing/plans` | `Pages/ManualTesting.razor` | Ekte dashboard med Testplaner-fane | `ManualTestingWorkspace`, testplan-API |
| Finn og forstå | `manual-testing/discover` | `Pages/ManualTestingDiscover.razor` | Ekte | scenarioforslag, StoryGrouping |
| Planlegg og klargjør | `manual-testing/plan` | `Pages/ManualTestingPlan.razor` | Ekte | testplan-API |
| Technical Testing | `technical-testing` | `Pages/TechnicalTesting.razor` | Knutepunkt: leser lagret tilstand, kjører ingenting | `TechnicalTestingWorkspace` |
| Discover & assess | `technical-testing/assess` | `Pages/TechnicalTestingAssess.razor` → `TechnicalTestingStage` | Trinnside med områdekort og lenker | samme, pluss `TechnicalCandidateSummary` |
| Configure & prepare | `technical-testing/configure` | → `TechnicalTestingStage` | Trinnside (lenker til spesialmoduler) | samme |
| Verify & investigate | `technical-testing/verify` | → `TechnicalTestingStage` | Trinnside med registrerte kjøringer | samme |
| Integration Testing | `technical-testing/integrations` | `Pages/TechnicalTestingIntegrations.razor` | Knutepunkt (tall og lenker) | `IntegrationTestingWorkspace` |

**Utenfor menyen eller avviklet:**

- `manual-testing/execute` er en omdirigering.
- `manual-testing/plan-execution` er en kontekstside.
- `test-plan-suggestions` er en eldre side (3 097 linjer). Den nås via «Avanserte verktøy».

**Duplikater:** spesialsidene ligger både under «Quality & Testing» og i Technical Testing-visningens grupper `tt-contracts`, `tt-integration` og `tt-data`. Sidene er integration-*, api-quality-review, critical-e2e-regression og test-data.

**Konklusjon:**

- Technical Testing besto av et knutepunkt, tre trinnsider med lenker og et knutepunkt for Integration Testing.
- Sidene leste kjørehistorikk (API Quality Review, Integration Quality Review, ytelse, Critical E2E), men **ingen testplaner eller testtilfeller**.
- Det fantes ikke noe dashboard for teknisk testarbeid.

## 2. Delte data og tjenester

- **Én kandidatinventar:** `ScenarioDiscoveryResult`, brukt av både Manual og Technical (`TechnicalCandidateSummary`).
  - 8 produsenter (`TestScenarioDiscoveryEngine.cs:107-114`): specification, system-guide, ui-source, api-contract, integration-journeys, existing-tests, developer-tests, test-specification.
- **Én planlagring:** `test_plan_drafts` (`TestPlanDraftRecord`), eid av `project:{import}` med `ApplicationKey`. Planer følger applikasjonen til nyere importer når importen har kildekode (DEF-V12-01, `6c88f6b4`).
- **Critical E2E-flyter:** lagret per målmiljø (`CriticalE2EStore`, `AppData/critical-e2e`). De tilbys til planer som `ExecutableScenarioOption`.
- **Integrasjonsreiser** (deklarerte komponentkjeder) og **testsuiter:** `IntegrationJourneysDbContext` og `AppDbContext`. De kobles til kravsporbarheten via `IntegrationRequirementTrace`.

## 3. Klassifisering som fantes

| Begrep | Hvor | Merknad |
|---|---|---|
| `ScenarioOwnership` (formål, primær/sekundær arbeidsflate, spesialområde) | `shared/ScenarioOwnershipContracts.cs` | Kjenner **ikke** `test-specification` og `existing-tests`. De ble «Unclassified». |
| `ScenarioTestKind {Manual, Technical, Both}` | `shared/StoryGrouping.cs:124` | Personens beslutning per scenario. Testtilfeller har sin egen `TestKind` (bare Manual eller Technical). |
| `ScenarioTestLevel` / `TestLevelClassifier` | `shared/TestPlanStructure.cs` | BusinessE2E → «Ende til ende» (som kandidat). |
| `ScenarioCandidateType` | `shared/TestScenarioDiscoveryContracts.cs:12` | Produsentens type. |
| `ScenarioPurpose`, `SpecialistArea`, `TestingWorkspace` | `ScenarioOwnershipContracts.cs` | — |

**Det fantes ingen samlet E2E-regel.** «E2E» betydde ulike ting:

- et kandidatnivå (BusinessE2E)
- en Critical E2E-flyt (automatisert sideflyt)
- en deklarert reise (Integration Testing)

**Teknisk innhold i Manual Testing:**

- diagrammet `chart.manualTechnical` på Testoversikt
- bryteren «Manuell | Teknisk | Alle» i Finn og forstå
- «Vis teknisk innhold» i Planlegg og klargjør

## 4. Mønstre i M2LB (lest, ikke kjørt)

- **Størrelse:** 15 moduler og 70 `.csproj`, hvorav 31 testprosjekter. 788 Markdown-filer.
- **REST:** cirka 31 ruter (ingest, interne oppslag, rapportinntak, SCIM). OpenAPI per modul i `*/autodoc/openapi.yaml`. Ingen `.http`- eller Postman-filer.
- **GraphQL er en hovedflate:** HotChocolate i 5 tjenester og 31 `.graphql`-filer. Frontend (StrawberryShake) har cirka 43 operasjoner. Det finnes ett GraphQL-kall mellom tjenester (Tjeneste → Person). Ingen subscriptions.
- **Integrasjon og meldinger er den sterkeste E2E-kategorien:**
  - Service Bus (Wolverine og rå SDK) med 10 topics, 4 køer og 12 abonnementer.
  - Event Hubs (BiRK-CDC), SQL-outbox og HTTP mellom tjenester med Entra-token.
  - **10 sporbare flyter** gjennom flere tjenester, blant annet:
    - CDC → PersonAdapter → Person → `person.barn` → Tjeneste
    - Tiltak-CDC → Tjeneste → Hendelse
    - MU → Receiver → Skole (Person- og Tjeneste-oppslag) → kvittering
    - lesing → `leselogg` → Revisjon-blob
- **Tester:** cirka 3 100 testmetoder. Integrasjonstester med WebApplicationFactory og Testcontainers. Kontrakttester med skjema- og payload-baselines.
  - **Ingen ekte automatiserte E2E-tester på tvers av tjenester.** `HendelseService/.../EndToEndTests.cs` (TEST-E-01) er en stubb (`Assert.True(true, …)`).
  - Ingen Playwright.
  - Ingen kjøringsbevis (TRX) i arkivet.
- **Dokumentasjon:** testspesifikasjoner med TEST-E-01–05 (Hendelse, Revisjon), MuSimulator-scenarioer (15 valideringsscenarioer) og forretningshistorier (US-A1…).
  - **Teknisk E2E** er formulert på protokollnivå: PUT, melding på topic, GraphQL-svar, blob.
  - **Manuell E2E** er formulert som brukerhistorier pluss QA-verifisering i miljø.
- **Ingen evidens for:** GraphQL-subscriptions, Playwright/nettleser-E2E, Altinn-API-kall, Maskinporten/ID-porten, MassTransit og Kafka.

**Konsekvens for designet:**

- Teknisk E2E i M2LB er først og fremst **meldings- og integrasjonsflyter** og **dokumenterte TEST-E-tester**, ikke nettleserflyter.
- GraphQL må være en egen kategori.
- «Automatisert» må aldri telles som «kjørt», fordi arkivet ikke har kjøringsbevis.

## 5. Hva som må bevares

- Spesialmodulene og sidene deres:
  - API Quality Review, Contract Testing og GraphQL compatibility
  - Integration Topology, Journeys, Suites og Integration Quality Review
  - Critical E2E, Test Data og Performance Test Review
- Lenkekortene per område på trinnsidene.
- «Verify & investigate» (registrerte kjøringer per område).
- Integration Testing-knutepunktet med egne trinn.
