# Tasks: Person Module Core

**Input**: Design documents from `specs/001-person-module/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tech stack**: C# / .NET 10, Hot Chocolate 15, EF Core 10, Azure SQL, Azure Service Bus, Polly 8, Serilog, xUnit 3 + TestContainers

**Tests**: Included — explicitly required by spec success criteria SC-003 through SC-007

**Organization**: Tasks grouped by user story priority (P1 → P5) per spec.md. Each story is independently testable with seeded DB data.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no shared state)
- **[Story]**: Which user story this task belongs to (US1–US6)

---

## Phase 1: Setup (Solution & Project Initialization)

**Purpose**: Create the .NET 10 solution with all projects and shared configuration. No user story work can begin until this is done.

- [X] T001 Create .NET 10 solution `PersonService.sln` with 4 src projects (`PersonService.Api`, `PersonService.Domain`, `PersonService.Application`, `PersonService.Infrastructure`) and 4 test projects (`PersonService.Domain.Tests`, `PersonService.Application.Tests`, `PersonService.Integration.Tests`, `PersonService.Contract.Tests`) at repository root
- [X] T002 Configure NuGet package references: `HotChocolate.AspNetCore` 15.x → Api; `Microsoft.EntityFrameworkCore.SqlServer` 10.x → Infrastructure; `Azure.Messaging.ServiceBus` → Infrastructure; `Microsoft.AspNetCore.OpenApi` → Api; `Microsoft.Extensions.Http.Resilience` (Polly 8) → Infrastructure; `Serilog.AspNetCore` → Api; `xunit` + `Testcontainers.MsSql` + `Shouldly` + `NSubstitute` + `Microsoft.AspNetCore.Mvc.Testing` → test projects
- [X] T003 [P] Configure `src/PersonService.Api/appsettings.json` with connection string sections (`ConnectionStrings:PersonDb`, `ServiceBus:ConnectionString`, `ServiceBus:OutboxPollingIntervalSeconds`, `AutorisasjonModule:BaseUrl`, `MicrosoftGraph:BaseUrl`) and `appsettings.Development.json` with local overrides per quickstart.md
- [X] T004 [P] Implement `src/PersonService.Api/Middleware/KorrelasjonsIdMiddleware.cs` — reads `X-Korrelasjon-Id` header or generates UUID v4; stores in `HttpContext.Items` and `Serilog.Context.LogContext` (PS-08); writes back in response header
- [X] T005 Configure Serilog JSON structured logging in `src/PersonService.Api/Program.cs` with `Destructure.ByIgnoring` masking for `Foedselsnummer`, `Navn`, `DUFNummer` fields (FR-026, PS-08); output to stdout + Application Insights sink placeholder

**Checkpoint**: Solution builds (`dotnet build`), all projects compile with zero warnings

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain entities, EF Core persistence, outbox infrastructure, operation registration, and health endpoint. ALL user stories depend on this phase.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Domain Entities

- [X] T006 [P] Implement `src/PersonService.Domain/Entities/Person.cs` — record/class with all columns from data-model.md (PersonId, EksternId, Navn, Foedselsnummer, UsikkerFoedselsnummer, DUFNummer, Foedselsdato, UsikkerFoedselsdato, KjønnTypeId, ErAktiv, OpprettetTidspunkt, OpprettetAv, EndretTidspunkt, EndretAv, Kilde); domain invariants: PersonId is sole primary identifier, never physically deleted (PP-05)
- [X] T007 [P] Implement `src/PersonService.Domain/Entities/BarnIAndrelinjeBarnevern.cs` — all columns from data-model.md (BarnRegistreringId, PersonId, BirkId, BarnTypeId, BarnStatusTypeId, SikkerhetsnivaaTypeId, KommuneNr, audit columns); invariants: 1:1 with Person, BirkId unique, SikkerhetsnivaaTypeId mandatory (default Nivå 0), never physically deleted
- [X] T008 [P] Implement `src/PersonService.Domain/Entities/BarnStatusHistorikk.cs` — append-only entity (HistorikkId, BarnRegistreringId, ForrigeBarnStatusTypeId (nullable), NyBarnStatusTypeId, ErForventetOvergang, Tidsstempel, UtfoertAv, Kilde); invariant: rows never deleted (PP-05), written in same transaction as status update

### Reference Data Entities

- [X] T009 [P] Implement `src/PersonService.Domain/ReferenceData/KjoennType.cs` — columns: KjoennTypeId, Verdi, Beskrivelse, ErAktiv, SorteringsRekkefoelge; seed values: Gutt, Jente, Ukjent
- [X] T010 [P] Implement `src/PersonService.Domain/ReferenceData/BarnType.cs` — columns: BarnTypeId, Verdi, Beskrivelse, ErAktiv, SorteringsRekkefoelge; seed values: Ordinaer, EMA, Ufodt
- [X] T011 [P] Implement `src/PersonService.Domain/ReferenceData/BarnStatusType.cs` — columns: BarnStatusTypeId, Verdi, Beskrivelse, ErAktiv, SorteringsRekkefoelge; seed values: Bestilling/Under Behandling (1), ReservertTiltak (2), UavklartTiltak (3), ITiltak (4), Avsluttet (5), Ukjent (99)
- [X] T012 [P] Implement `src/PersonService.Domain/ReferenceData/SikkerhetsnivaaType.cs` — columns: SikkerhetsnivaaTypeId, Nivaa (INT, UNIQUE — critical for security comparisons), Verdi, BiRKKode, ElementsKode, Beskrivelse, KreverGradertTilgang, ErAktiv, SorteringsRekkefoelge; fixed seed: Nivaa 0 (Ingen), 1 (SkjultAdresse), 2 (Kode7, KreverGradertTilgang=true), 3 (Kode6, KreverGradertTilgang=true)
- [X] T013 [P] Implement `src/PersonService.Domain/ReferenceData/Kommune.cs` — columns: KommuneNr (PK, 4-char), Navn, ErAktiv; invariant: deactivated not deleted (PP-05)

### Domain Services

- [X] T014 [P] Implement `src/PersonService.Domain/Services/SecurityClassificationService.cs` — methods: `KreverGradertTilgang(SikkerhetsnivaaType): bool` (Nivaa >= 2), `ErSynligForBruker(SikkerhetsnivaaType nivaa, IEnumerable<Guid> grantedChildIds, Guid barnRegistreringId): bool` (Nivaa < 2 OR BarnRegistreringId IN grants); this logic is the foundation of PP-04/FR-003
- [X] T015 [P] Implement `src/PersonService.Domain/Services/BarnStatusTransitionService.cs` — method `ErForventetOvergang(BarnStatusType fra, BarnStatusType til): bool` using the known transition graph from data-model.md: Bestilling→Reservert, Bestilling→Uavklart, Reservert→ITiltak, Uavklart→ITiltak, Uavklart→Avsluttet, ITiltak→Avsluttet (FR-021)

### Domain Exceptions & Events

- [X] T016 [P] Create `src/PersonService.Domain/Exceptions/PersonNotFoundException.cs` and `AuthorisasjonException.cs` (thrown on fail-closed auth, FR-031)
- [X] T017 [P] Create domain event payload records in `src/PersonService.Domain/Events/`: `PersonOpprettetEvent.cs`, `PersonOppdatertEvent.cs`, `BarnRegistrertEvent.cs`, `BarnStatusEndretEvent.cs` (includes ErForventetOvergang), `SikkerhetsnivaaEndretEvent.cs` (includes ForrigeNivaa/NyttNivaa ints), `BarnKommuneEndretEvent.cs`, `BarnTypeEndretEvent.cs`, `RevisjonshendelseEvent.cs` — all matching the envelope schema in `contracts/events.md`; Data fields contain only UUIDs and metadata (FR-026)

### Application Interfaces

- [X] T018 [P] Create `src/PersonService.Application/Interfaces/IAutorisasjonClient.cs` — methods: `EvaluerOperasjon(brukerId, operasjonId, barnRegistreringId): Task<bool>`, `HentGradertBarntilganger(brukerId): Task<IReadOnlyList<Guid>>` (batch fetch for search, R-03); throws `AuthorisasjonException` on failure (FR-031)
- [X] T019 [P] Create `src/PersonService.Application/Interfaces/IPersonRepository.cs` — methods matching the queries needed by all services: `HentPersonAsync`, `SoekBarnAsync` (with filter params + security filter params), `HentBarnProfilAsync`, `LagrePersonAsync`, `LagreBarnAsync`
- [X] T020 [P] Create `src/PersonService.Application/Interfaces/IMicrosoftGraphClient.cs` — method: `HentBrukervisningsnavn(userId: Guid): Task<string>` (used by US3 access list display)

### Infrastructure — EF Core

- [X] T021 Implement `src/PersonService.Infrastructure/Persistence/PersonDbContext.cs` — EF Core 10 `DbContext` with `DbSet<>` for all entities; entity configurations: all UUIDs use `ValueGeneratedNever()` (PS-04), query filters for `ErAktiv` soft-delete pattern, index configurations matching data-model.md (IX_Person_Foedselsnummer filtered, IX_BarnIAndrelinjeBarnevern_PersonId UNIQUE, IX_Barn_Search composite, IX_OutboxMessage_Status_CreatedAt, etc.); `EnableSensitiveDataLogging = false`
- [X] T022 [P] Implement `src/PersonService.Infrastructure/Persistence/Repositories/PersonRepository.cs` implementing `IPersonRepository`; `SoekBarnAsync` MUST apply security filter `WHERE (s.Nivaa < 2 OR b.BarnRegistreringId IN @grantedChildIds)` in the base SQL query (never post-filter, PP-04); use EF Core compiled queries for search (R-10)
- [X] T023 [P] Implement `src/PersonService.Infrastructure/Persistence/Repositories/BarnRepository.cs` — upsert (create or update) for `BarnIAndrelinjeBarnevern` and append-only insert for `BarnStatusHistorikk` in same `SaveChangesAsync()` call (FR-012)
- [X] T024 Create initial EF Core migration `InitialCreate` in `src/PersonService.Infrastructure/Persistence/Migrations/` using `dotnet ef migrations add InitialCreate`; migration must: create all tables with correct constraints and indexes, add full-text index on `Person.Navn` via `migrationBuilder.Sql("CREATE FULLTEXT INDEX ...")` (FR-001), seed all reference data using `HasData()` with fixed UUIDs for SikkerhetsnivaaType rows 0–3 and BiRK-standard values for other tables

### Infrastructure — Outbox & Service Bus

- [X] T025 [P] Implement `src/PersonService.Infrastructure/Outbox/OutboxMessage.cs` entity and EF Core configuration — all columns from data-model.md (MessageId PK, TopicName, SessionId, Subject, Payload, Priority, CreatedAt, PublishedAt, Attempts, Status); index on (Status, CreatedAt)
- [X] T026 Implement `src/PersonService.Infrastructure/ServiceBus/ServiceBusEventPublisher.cs` — writes `OutboxMessage` rows to DB (NOT directly to Service Bus); sets `SessionId = entity UUID`, `Subject = event type name`, `Priority = "High"` and `Subject = "SikkerhetsnivaaEndret_CRITICAL"` for `SikkerhetsnivaaEndretEvent` (FR-025); serializes event envelope to JSON with no personal data; called within `SaveChangesAsync()` transaction. **Also handles audit events (FR-028)**: writes `RevisjonshendelseEvent` rows to OutboxMessage with `TopicName = "person.audit"`, `SessionId = entity UUID`; `FoerTilstand`/`EtterTilstand` snapshots serialized as JSON with field names and UUID values only — never raw personal data; the single publisher method accepts a `topicName` parameter so all mutation handlers can write audit events starting from Phase 5 (US3) onwards
- [X] T027 Implement `src/PersonService.Infrastructure/Outbox/OutboxPublisherHostedService.cs` — `IHostedService` polling every 1–2 seconds: SELECT TOP 50 Pending OutboxMessages ordered by CreatedAt; publish each to Azure Service Bus with SessionId and Priority; UPDATE Status=Published on success; UPDATE Attempts++ on failure, Status=Failed after 5 attempts; log failures without personal data (FR-026)

### Infrastructure — Operation Registration & Health

- [X] T028 Implement `src/PersonService.Infrastructure/OperasjonsRegistrering/OperasjonsRegistreringHostedService.cs` — `IHostedService` that sends exactly 7 operations to Service Bus queue `operasjonsregistrering` at startup (FR-029, PS-06): `Person:SoekBarn` (Generell), `Person:SeBarnGrunnprofil` (Generell), `Person:SeBarnProfil` (Barnespesifikk), `Person:SeFullIdentitet` (Barnespesifikk), `Person:SeGradertBarn` (Barnespesifikk), `Person:AdministerGradertBarntilgang` (Barnespesifikk), `Person:SeRevisjonslogg` (Generell); sets health flag when complete (SC-007)
- [X] T029 [P] Implement `src/PersonService.Api/Rest/Drift/HelseEndpoint.cs` — `GET /api/person/v1/helse`; checks: EF Core DB connectivity, Service Bus connectivity, operation registration completion; returns `{ "status": "sunn"|"syk", "operasjonsregistrering": "fullfoert"|"venter" }` per SC-007

### API Bootstrap

- [X] T030 Configure `src/PersonService.Api/Program.cs` — DI registration: EF Core, repositories, application services, infrastructure services (`AutorisasjonClient`, `MicrosoftGraphClient`, `ServiceBusEventPublisher`), hosted services (OutboxPublisher, OperasjonsRegistrering); Hot Chocolate 15 GraphQL server registration with queries/mutations/types to be added per story; minimal API route registration; `KorrelasjonsIdMiddleware` pipeline; health endpoint; OpenAPI/Swagger for REST surface

**Checkpoint**: `dotnet run` starts without errors; `GET /api/person/v1/helse` returns 200; `dotnet ef database update` applies migration successfully

---

## Phase 3: User Story 1 — Search for Children (Priority: P1) 🎯 MVP

**Goal**: A caseworker can search for children by name, national ID, DUF number, or BirkID. Kode 6/7 children are completely invisible without explicit `Person:SeGradertBarn` grant.

**Independent Test**: Run `dotnet test --filter "Category=Security"` — zero false positives (Kode 7 child visible to unauthorised user) and zero false negatives (Kode 7 child invisible to authorised user) must pass. Seed test data directly into TestContainers DB; no ingestion endpoint needed.

### Tests for User Story 1

- [X] T031 [P] [US1] Implement `tests/PersonService.Domain.Tests/SecurityClassificationServiceTests.cs` — xUnit tests with `[Trait("Category", "Security")]` covering: `KreverGradertTilgang` returns true for Nivaa >= 2, false for Nivaa < 2; `ErSynligForBruker` returns false for Kode6/7 child when BarnRegistreringId NOT in grants; returns true when in grants; returns true for Nivaa < 2 regardless of grants
- [X] T032 [P] [US1] Implement `tests/PersonService.Application.Tests/BarnSearchServiceTests.cs` — NSubstitute mocks for `IAutorisasjonClient` and `IPersonRepository`; tests: search returns only authorised children, Kode7 child absent from results for user without grant, address-protection flag set for Nivaa >= 1, pagination parameters forwarded correctly, `AuthorisasjonException` from auth client propagates as HTTP 503

### Implementation for User Story 1

- [X] T033 [US1] Implement `src/PersonService.Infrastructure/Http/AutorisasjonClient.cs` — typed `HttpClient` implementing `IAutorisasjonClient`; calls `POST /api/autorisasjon/v1/evaluer`; Polly 8 resilience: 2 retries (50ms/100ms exponential), 500ms per-attempt timeout, circuit breaker after 5 failures in 30s; throws `AuthorisasjonException` on any failure (fail-closed, FR-031); no caching of auth decisions; register with `AddHttpClient().AddResilienceHandler()`
- [X] T034 [US1] Implement `src/PersonService.Application/BarnSearch/BarnSearchService.cs` — `SoekBarnAsync(kriterier, brukerId, side, sideStoerrelse)`: (1) fetch `grantedChildIds = await _autorisasjonClient.HentGradertBarntilganger(brukerId)`, (2) call repository with security filter params (Nivaa < 2 OR IN grants), (3) set `ErAdressebeskyttet = true` for Nivaa >= 1 results (FR-004), (4) paginate (FR-005), (5) return `BarnSoekResultat` list; log search request with user identity, criteria, timestamp, and classified child IDs included (FR-007)
- [X] T035 [US1] Define GraphQL output types in `src/PersonService.Api/GraphQL/Types/BarnSoekResultatType.cs` — fields: PersonId, Navn, Foedselsdato, BirkId, BarnStatusType, BarnType, Kommune, ErAdressebeskyttet, SikkerhetsnivaaKode (FR-006); `BarnSoekSideType.cs` with results + totaltAntall + harFlere
- [X] T036 [US1] Implement `src/PersonService.Api/GraphQL/Queries/BarnSoekQueryResolver.cs` — Hot Chocolate 15 `[QueryType]` with `soekBarn(kriterier: BarnSoekKriterierInput!, side: Int, sideStoerrelse: Int): BarnSoekSide`; validates caller has `Person:SoekBarn` operation before delegating to `BarnSearchService`; use `[UseOffsetPaging]` for pagination; register in `Program.cs`
- [X] T037 [US1] Implement `src/PersonService.Api/GraphQL/Authorization/GraphQLAuthMiddleware.cs` — Hot Chocolate 15 field middleware that reads validated EntraID claims from `HttpContext.User` (set by YARP proxy); calls `IAutorisasjonClient.EvaluerOperasjon()` for the required operation; throws `UnauthorizedAccessException` → maps to GraphQL error (404-not-403 for direct lookups per research.md §3)
- [X] T038 [US1] Implement `tests/PersonService.Integration.Tests/SecurityFilterIntegrationTests.cs` — TestContainers SQL Server; seeds: 2 Nivaa-0 children, 1 Nivaa-1, 1 Kode7 child; asserts: search by user WITH Kode7 grant returns all 4; search by user WITHOUT grant returns 3 (Kode7 absent, no count difference visible); `[Trait("Category", "Security")]` (SC-003)

**Checkpoint**: `dotnet test tests/PersonService.Domain.Tests tests/PersonService.Application.Tests` passes; `SecurityFilterIntegrationTests` pass with TestContainers

---

## Phase 4: User Story 2 — View Child Profile (Priority: P2)

**Goal**: A caseworker can view a child's full profile including status history. National ID is masked unless `Person:SeFullIdentitet` is held. Kode 6/7 profiles return 404 without `Person:SeGradertBarn`.

**Independent Test**: Seed a child with Kode7 classification. Confirm: user WITH `Person:SeBarnGrunnprofil` + `Person:SeGradertBarn` sees full profile with national ID masked; user WITH `Person:SeFullIdentitet` sees unmasked national ID; user WITHOUT `Person:SeGradertBarn` gets 404 (no indication child exists).

### Implementation for User Story 2

- [X] T039 [P] [US2] Implement `src/PersonService.Application/BarnProfile/BarnProfileService.cs` — `HentBarnProfilAsync(barnRegistreringId, brukerId)`: (1) eval `Person:SeBarnGrunnprofil` or `Person:SeBarnProfil`; (2) fetch child including `SikkerhetsnivaaType`; (3) if Nivaa >= 2, additionally eval `Person:SeGradertBarn` — throw `PersonNotFoundException` (not `AuthorisasjonException`) if missing (FR-008, 404-not-403 per PP-04); (4) return `BarnProfil` with all fields, `UsikkerFoedselsnummer`/`UsikkerFoedselsdato` marked provisional (FR-011), and status history from `BarnStatusHistorikk`
- [X] T040 [US2] Implement `src/PersonService.Api/GraphQL/Types/BarnProfilType.cs` — Hot Chocolate 15 type with: all profile fields (FR-009), `foedselsnummer` field resolver that calls `EvaluerOperasjon(Person:SeFullIdentitet)` and returns `null` if not authorised (FR-010, national ID masking), `usikker` boolean flags for provisional fields (FR-011), `statusHistorikk: [BarnStatusHistorikkType!]!` resolver
- [X] T041 [US2] Implement `src/PersonService.Api/GraphQL/Queries/BarnProfilQueryResolver.cs` — Hot Chocolate 15 query `hentBarn(barnRegistreringId: UUID!): BarnProfil`; delegates to `BarnProfileService`; maps `PersonNotFoundException` to GraphQL `null` result (not an error — child does not exist from caller's perspective); register in `Program.cs`
- [X] T042 [US2] Define `src/PersonService.Api/GraphQL/Types/BarnStatusHistorikkType.cs` — fields: tidsstempel, forrigeBarnStatusType, nyBarnStatusType, erForventetOvergang, utfoertAv (UUID), kilde (FR-012)

**Checkpoint**: GraphQL `hentBarn` returns correct data for authorised user; national ID masked without `Person:SeFullIdentitet`; Kode7 child returns null for unauthorised user

---

## Phase 5: User Story 3 — Manage Access to Kode 6/7 Children (Priority: P3)

**Goal**: An authorised user holding `Person:AdministerGradertBarntilgang` can grant time-limited access to a Kode 6/7 child. Self-assignment is rejected. Grants appear in the access list.

**Independent Test**: Seed a Kode6 child. Confirm: user WITH `Person:AdministerGradertBarntilgang` can call `tildelGradertBarntilgang` mutation → grant created in Auth module + audit event published; same user attempting self-assignment receives error; access list query shows the grant.

### Implementation for User Story 3

- [X] T043 [P] [US3] Implement `src/PersonService.Infrastructure/Http/MicrosoftGraphClient.cs` — typed `HttpClient` implementing `IMicrosoftGraphClient`; calls Microsoft Graph `/v1.0/users/{id}` to resolve display names for the access list; Managed Identity authentication (PS-02); Polly resilience; returns `"[ukjent]"` on failure (non-critical)
- [X] T044 [US3] Implement `src/PersonService.Application/GradertBarntilgang/GradertBarntilgangService.cs` — `TildelGradertBarntilgangAsync(grantingBrukerId, targetBrukerId, barnRegistreringId, utloeper?, aarsak?)`: (1) eval `Person:AdministerGradertBarntilgang` for grantingBrukerId; (2) confirm child exists and Nivaa >= 2; (3) reject if grantingBrukerId == targetBrukerId (FR-015); (4) call `IAutorisasjonClient` to create grant; (5) publish `RevisjonshendelseEvent` via outbox (FR-016, FR-028); `HentGradertBarntilgangAsync` returns access list with display names from `IMicrosoftGraphClient`
- [X] T045 [US3] Implement `src/PersonService.Api/GraphQL/Mutations/GradertBarntilgangMutation.cs` — Hot Chocolate 15 `[MutationType]` with `tildelGradertBarntilgang(input: TildelGradertBarntilgangInput!): TildelGradertBarntilgangPayload`; maps `AuthorisasjonException` and self-assignment validation errors to structured GraphQL errors; register in `Program.cs`
- [X] T046 [US3] Implement `src/PersonService.Api/GraphQL/Queries/GradertBarntilgangQueryResolver.cs` — query `hentGradertBarntilgang(barnRegistreringId: UUID!): [GradertBarntilgangOppfoering!]!`; returns current and historical grants with grantedBy, grantedTo (display name), validFrom/to, timestamp (FR-016)

**Checkpoint**: `tildelGradertBarntilgang` mutation succeeds for authorised non-self grantee; self-assignment returns error; access list query returns grant

---

## Phase 6: User Story 4 — Reference Data (Priority: P4)

**Goal**: API consumers can read all active reference data values. Deactivated values remain on historical records but are excluded from new-registration options.

**Independent Test**: GraphQL query for all 5 reference data types returns active values only; historical record still shows deactivated value correctly.

### Implementation for User Story 4

- [X] T047 [P] [US4] Implement `src/PersonService.Application/ReferenceData/ReferenceDataService.cs` — methods: `HentKjoennTyperAsync()`, `HentBarnTyperAsync()`, `HentBarnStatusTyperAsync()`, `HentSikkerhetsnivaaTyperAsync()`, `HentKommunerAsync()` — all return only `ErAktiv = true` rows (FR-017); no auth check required for reference data (public metadata)
- [X] T048 [US4] Implement `src/PersonService.Api/GraphQL/Queries/ReferenceDataQueryResolver.cs` — Hot Chocolate 15 queries: `kjoennTyper`, `barnTyper`, `barnStatusTyper`, `sikkerhetsnivaaTyper`, `kommuner` — each returns the active list from `ReferenceDataService`; define corresponding GraphQL output types in `src/PersonService.Api/GraphQL/Types/` (KjoennTypeType.cs, BarnTypeType.cs, etc.); register in `Program.cs`

**Checkpoint**: All 5 reference data GraphQL queries return seeded data; deactivated record excluded from active list

---

## Phase 7: User Story 5 — Data Ingestion from BiRK (Priority: P5) + User Story 6 — Domain Events (Priority: P5)

**Goal (US5)**: The ingestion REST API accepts Person and child records idempotently, publishes domain events, and exposes metrics. **Goal (US6)**: Domain events on Service Bus contain only UUIDs and metadata; publication is atomic with the mutation; session ordering guaranteed per entity.

**Independent Test (US5)**: POST same `PersonOpprettet` payload twice → exactly 1 `Person` row in DB, exactly 1 `PersonOpprettet` in OutboxMessage table (SC-005). **Independent Test (US6)**: After ingestion, OutboxMessage payload for all event types contains no `string` fields with personal data (SC-006).

### Tests for US5 + US6

- [X] T049 [P] [US5] Implement `tests/PersonService.Application.Tests/InnmatingServiceTests.cs` — NSubstitute mocks; tests: idempotent person upsert (same PersonId → 1 row), idempotent barn upsert (same BarnRegistreringId → 1 row), `ErForventetOvergang = false` for Bestilling→ITiltak (unexpected), `ErForventetOvergang = true` for Bestilling→ReservertTiltak (expected); validation failure for one record does not stop others (FR-023); `[Trait("Category", "Unit")]`
- [X] T050 [P] [US6] Implement `tests/PersonService.Contract.Tests/EventPayloadTests.cs` — deserializes all 7 event types from JSON; asserts: `Data` object has no `string` property value longer than 36 chars (UUID length); no property named `Navn`, `Foedselsnummer`, `DUFNummer`; `ErForventetOvergang` is bool not string (SC-006); `[Trait("Category", "Contract")]`
- [X] T051 [US5] Implement `tests/PersonService.Integration.Tests/PersonIngestionTests.cs` — TestContainers; sends Person + Barn upsert; asserts: DB row count = 1; OutboxMessage count = expected events; sends same payload again; asserts: DB row count still = 1, OutboxMessage count unchanged (SC-005); `[Trait("Category", "Integration")]`
- [X] T052 [US6] Implement `tests/PersonService.Integration.Tests/OutboxPatternTests.cs` — TestContainers; after ingesting 1 person + 1 barn update: assert mutation count == OutboxMessage count for that entity's events (SC-004); assert `SessionId` on OutboxMessage rows matches entity UUID (FR-027); assert `SikkerhetsnivaaEndret` OutboxMessage has Priority="High" (FR-025); `[Trait("Category", "Integration")]`

### Implementation for US5

- [X] T053 [US5] Implement `src/PersonService.Application/Innmating/InnmatingService.cs` — `InnmatingPersonAsync(innmatingPersonDto)`: upsert Person by PersonId (UPDATE if exists, INSERT if not — idempotent, FR-022); detect DUF→Foedselsnummer upgrade — UPDATE in-place, retain DUF (FR-032); publish `PersonOpprettetEvent` or `PersonOppdatertEvent` via `ServiceBusEventPublisher` within `SaveChangesAsync()` transaction; `InnmatingBarnAsync(innmatingBarnDto)`: upsert `BarnIAndrelinjeBarnevern`; on status change: write `BarnStatusHistorikk` row + publish `BarnStatusEndretEvent` with `ErForventetOvergang` from `BarnStatusTransitionService`; on security level change: publish `SikkerhetsnivaaEndretEvent` (FR-025); auto-create unknown reference data values (FR-018); log validation failures without stopping (FR-023); call `context.InnmatingMetrikkRecord.ExecuteUpdateAsync()` to increment DB metrics row atomically within the same transaction (see T057 for schema — no in-process counters, PS-09)
- [X] T054 [US5] Implement `src/PersonService.Api/Rest/Innmating/PersonerInnmatingEndpoint.cs` — `PUT /api/person/v1/innmating/personer` minimal API endpoint accepting `InnmatingPersonRequest` (domain format, not BiRK format per FR-020); request includes optional `BirkEndringstidspunkt: DateTimeOffset?` for latency metric calculation (FR-024); validates request shape; delegates to `InnmatingService`; returns 204 on success, 422 on domain validation failure; register in `Program.cs`
- [X] T055 [US5] Implement `src/PersonService.Api/Rest/Innmating/BarnInnmatingEndpoint.cs` — `PUT /api/person/v1/innmating/barn` accepting `InnmatingBarnRequest`; includes optional `BirkEndringstidspunkt: DateTimeOffset?` (same as T054); same error handling pattern as T054; register in `Program.cs`
- [X] T056 [US5] Implement `src/PersonService.Api/Rest/Innmating/BatchInnmatingEndpoint.cs` — `POST /api/person/v1/innmating/batch` accepting `InnmatingBatchRequest` (list of person and barn records); processes each record independently so one failure does not stop others (FR-023); returns `{ "behandlet": N, "feil": [...] }`; register in `Program.cs`
- [X] T057 [US5] Add `InnmatingMetrikk` entity to `PersonService.Infrastructure` — columns: `MetrikkId` (PK UUID), `Periode` (UTC hour truncated), `Behandlet` (INT), `Feil` (INT), `TotalLatensMsSum` (BIGINT), `AntallMedLatens` (INT); `InnmatingService` calls `context.InnmatingMetrikkRecord.ExecuteUpdateAsync()` atomically within the ingestion transaction per record (PS-09: no in-process counters). Implement `src/PersonService.Api/Rest/Drift/MetrikkerEndpoint.cs` — `GET /api/person/v1/innmating/metrikker?perioder=24` queries last N period rows and returns: `behandlet`, `feil`, `gjennomsnittLatensMs` (TotalLatensMsSum / AntallMedLatens); add DB index `IX_InnmatingMetrikk_Periode` on `Periode` DESC. Add migration for this table alongside `InitialCreate` or as a second migration `AddInnmatingMetrikk`

### Implementation for US6

- [X] T058 [US6] Wire audit event calls into `InnmatingService` (US5 mutations) — call `ServiceBusEventPublisher` with `RevisjonshendelseEvent` for every Person upsert and Barn upsert/status-change/security-level-change in `InnmatingService.cs` (FR-028); audit infrastructure was established in T026 (Phase 2); this task ensures the ingestion path is complete alongside the domain event path

**Checkpoint (US5)**: `PersonIngestionTests` pass; double-sending same record produces 1 DB row; `InnmatingServiceTests` pass with all idempotency scenarios

**Checkpoint (US6)**: `EventPayloadTests` pass; `OutboxPatternTests` confirm mutation-event atomicity and SessionId correctness

---

## Phase 8 (Final): Polish & Cross-Cutting Concerns

**Purpose**: Remaining test coverage, SC-007 health validation, performance and contract verification.

- [X] T059 [P] Implement `tests/PersonService.Domain.Tests/BarnStatusTransitionServiceTests.cs` — tests: all 6 known expected transitions return `true`; 5 unexpected transitions (e.g. ITiltak→Bestilling, Avsluttet→ITiltak) return `false`; `[Trait("Category", "Unit")]`
- [X] T060 [P] Implement `tests/PersonService.Contract.Tests/GraphQLSchemaTests.cs` — using `Microsoft.AspNetCore.Mvc.Testing` + Hot Chocolate 15 test harness: validate `soekBarn`, `hentBarn`, `tildelGradertBarntilgang`, `hentGradertBarntilgang`, `hentRevisjonslogg`, all reference data queries are present in schema; validate `foedselsnummer` field is nullable on `BarnProfil` type (masking contract); validate `RevisjonsloggSide` and `RevisjonsloggOppfoering` types are present (T064); `[Trait("Category", "Contract")]`
- [X] T061 Validate SC-007 operation registration: confirm `OperasjonsRegistreringHostedService` sets completion flag after publishing all 7 operations; `HelseEndpoint` returns `operasjonsregistrering: "venter"` until flag is set; add integration test in `tests/PersonService.Integration.Tests/` confirming all 7 operations are published on startup
- [X] T062 [P] Review EF Core query plans for `SoekBarnAsync`: confirm composite index `IX_Barn_Search` is used for filtered searches and full-text index on `Person.Navn` for name searches; add any missing indexes as a new EF Core migration if needed (SC-002 p95 < 2s SLA)
- [X] T063 Run developer setup validation per `specs/001-person-module/quickstart.md`: `dotnet restore` → `docker run mcr.microsoft.com/mssql/server` → `dotnet ef database update` → `dotnet run` → confirm health endpoint returns `sunn` + operation registration complete → `dotnet test` all pass
- [X] T064 [P] Implement audit log read path for `Person:SeRevisjonslogg` (FR-029): Create `src/PersonService.Application/Interfaces/IRevisjonsloggClient.cs` — method `HentRevisjonsloggAsync(entityId: Guid, side: int, sideStoerrelse: int): Task<RevisjonsloggSide>`. Implement `src/PersonService.Infrastructure/Http/RevisjonsloggClient.cs` — typed `HttpClient` calling the platform Audit service API (base URL from config `AuditService:BaseUrl`); Managed Identity auth (PS-02); Polly 8 resilience matching T033 pattern. Add `hentRevisjonslogg(barnRegistreringId: UUID!, side: Int, sideStoerrelse: Int): RevisjonsloggSide` GraphQL query resolver in `src/PersonService.Api/GraphQL/Queries/RevisjonsloggQueryResolver.cs`: (1) eval `Person:SeRevisjonslogg` (general); (2) if child has `KreverGradertTilgang = true`, additionally eval `Person:SeGradertBarn` per FR-029 — throw `PersonNotFoundException` if missing (PP-04); (3) proxy to Audit service; (4) return paginated `RevisjonsloggOppfoering` list (actor UUID, action, entity UUID, timestamp, kilde — no personal data, FR-026). Define `RevisjonsloggOppfoering` and `RevisjonsloggSide` GraphQL output types. Register client, service, resolver, and types in DI and `Program.cs`; add `AuditService:BaseUrl` to `appsettings.json`

---

## Dependencies & Execution Order

### Phase Dependencies

```
Phase 1 (Setup)
  └── Phase 2 (Foundational) ← BLOCKS all user stories
        ├── Phase 3 (US1 - Search, P1)        ← MVP delivery point
        ├── Phase 4 (US2 - Profile, P2)        ← independent of US1
        ├── Phase 5 (US3 - Access Mgmt, P3)   ← independent; needs AutorisasjonClient from US1
        ├── Phase 6 (US4 - Reference Data, P4) ← fully independent
        └── Phase 7 (US5 + US6 - Ingestion, P5) ← independent
              └── Phase 8 (Polish)
```

### User Story Dependencies

- **US1 (P1)**: Depends only on Phase 2. Independently testable with seeded DB data.
- **US2 (P2)**: Depends only on Phase 2. Shares domain entities with US1 but independently testable.
- **US3 (P3)**: Depends only on Phase 2. `AutorisasjonClient` (T033) built in US1 phase should be extracted to shared infra or US3 implements it independently.
- **US4 (P4)**: Depends only on Phase 2 seed data. Simplest story, fully independent.
- **US5 (P5)**: Depends only on Phase 2. Provides data for US1 in production but not needed for US1 tests (seeded directly).
- **US6 (P5)**: Outbox infrastructure built in Phase 2 (T025–T027); US6 adds audit events and verifies end-to-end behavior.

### Within Each User Story

- Tests written alongside or before implementation (SC-003 tests must be written)
- Application layer before API layer
- Infrastructure clients (AutorisasjonClient, MicrosoftGraphClient) before application services that call them

### Parallel Opportunities

- All Phase 2 domain tasks T006–T020 run in parallel (different files, no dependencies)
- US1, US2, US3, US4 can all start in parallel after Phase 2 completes
- US5 and US6 are explicitly parallel (same P5 priority, same infrastructure tier)
- T049–T052 (test files for Phase 7) run in parallel

---

## Parallel Execution Examples

### Phase 2 — Domain (all in parallel after T001–T002)

```
Task: T006 — Person entity
Task: T007 — BarnIAndrelinjeBarnevern entity
Task: T008 — BarnStatusHistorikk entity
Task: T009 — KjoennType reference data
Task: T010 — BarnType reference data
Task: T011 — BarnStatusType reference data
Task: T012 — SikkerhetsnivaaType reference data
Task: T013 — Kommune reference data
Task: T014 — SecurityClassificationService
Task: T015 — BarnStatusTransitionService
Task: T016 — Domain exceptions
Task: T017 — Domain event records
Task: T018 — IAutorisasjonClient interface
Task: T019 — IPersonRepository interface
Task: T020 — IMicrosoftGraphClient interface
```

### Phase 3 — US1 Tests (parallel before implementation)

```
Task: T031 — SecurityClassificationServiceTests
Task: T032 — BarnSearchServiceTests
```

### Phase 7 — US5/US6 Tests (parallel)

```
Task: T049 — InnmatingServiceTests
Task: T050 — EventPayloadTests
Task: T051 — PersonIngestionTests
Task: T052 — OutboxPatternTests
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (T001–T005)
2. Complete Phase 2: Foundational (T006–T030) — **CRITICAL: blocks everything**
3. Complete Phase 3: US1 Search (T031–T038)
4. **STOP and VALIDATE**: `dotnet test --filter "Category=Security"` — all pass
5. **Demo**: GraphQL `soekBarn` returns paginated results; Kode 7 child invisible to unauthorised user

### Incremental Delivery

1. Foundation ready → US1 search → **MVP demo**
2. US2 profile → caseworkers can view child details
3. US3 access management → classified child access grants
4. US4 reference data → consuming services can read lookup values
5. US5/US6 ingestion + events → BiRK data flows in; downstream services react to changes
6. Polish → performance validated, all success criteria verified

### Parallel Team Strategy

After Phase 2 completes:

- **Developer A**: US1 (Phase 3) — highest user value, most critical security path
- **Developer B**: US5 + US6 (Phase 7) — enables production data flow from BiRK
- **Developer C**: US2 + US3 (Phase 4–5) — profile and access management

---

## Success Criteria Traceability

| Criterion | Task(s) |
|-----------|---------|
| SC-001 — find child in ≤ 3 interactions | T034–T036 (search endpoint) |
| SC-002 — p95 < 2s search SLA | T033 (Polly 500ms timeout), T022 (compiled queries), T062 (query plan review) |
| SC-003 — zero Kode 6/7 false pos/neg | T031, T032, T037, T038 (security tests) |
| SC-004 — mutation count = audit event count | T052 (OutboxPatternTests) |
| SC-005 — idempotent ingestion | T049, T051 (ingestion tests) |
| SC-006 — no personal data in events | T050 (EventPayloadTests) |
| SC-007 — operation registration at startup | T028, T029, T061 |
| SC-008 — Person module is single source of truth | Enforced by PP-01/PP-06 (no cross-service JOINs) |

---

## Notes

- Norwegian character transliteration applies to all file names and identifiers: `ø→oe`, `æ→ae`, `å→aa` (plan.md constraint)
- Security filter (`Nivaa < 2 OR BarnRegistreringId IN @grants`) MUST be in the base SQL query — never post-filter (PP-04, FR-003)
- All UUID PKs use `ValueGeneratedNever()` — generated client-side (PS-04)
- Outbox writes MUST be in the same `SaveChangesAsync()` transaction as the domain mutation (FR-027 atomicity)
- `EnableSensitiveDataLogging = false` must remain in all environments (FR-026)
- `[Trait("Category", "Security")]` on SC-003 tests enables `dotnet test --filter "Category=Security"` for fast CI gate
- See `specs/001-person-module/quickstart.md` for EF Core migration commands and local Docker setup
