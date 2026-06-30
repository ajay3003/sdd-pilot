# Tasks: Tjenestemodul M01

**Input**: Design documents from `/specs/001-tjenestemodul-m01/`
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/ ✓

**Tests**: Included — all 5 user stories have acceptance scenarios that map to automated tests (PP-09 constitution gate).

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)
- Exact file paths are included in all task descriptions

---

## Phase 1: Setup

**Purpose**: Create the solution, project scaffolding, and package references.

- [X] T001 Create .NET 10 solution `M2LB.Tjeneste.sln` with five projects (Api, Domain, Infrastructure, Unit, Integration) and all cross-project references per `specs/001-tjenestemodul-m01/quickstart.md`
- [X] T002 [P] Add NuGet package references to `src/M2LB.Tjeneste.Api/M2LB.Tjeneste.Api.csproj`: HotChocolate.AspNetCore 15.*, HotChocolate.Authorization 15.*, Microsoft.Identity.Web 3.*, Serilog.AspNetCore 9.*, Serilog.Sinks.ApplicationInsights 4.*, WolverineFx.Http 5.* (WolverineHttp 3.* does not exist; actual package is WolverineFx.Http)
- [X] T003 [P] Add NuGet package references to `src/M2LB.Tjeneste.Infrastructure/M2LB.Tjeneste.Infrastructure.csproj`: Microsoft.EntityFrameworkCore.SqlServer 10.*, Microsoft.EntityFrameworkCore.Design 10.*, Azure.Messaging.ServiceBus 7.*, Azure.Messaging.EventHubs.Processor 5.*, Azure.Storage.Blobs 12.*, Azure.Identity 1.*, WolverineFx.EntityFrameworkCore 5.*, WolverineFx.AzureServiceBus 5.*, WolverineFx.SqlServer 5.*, Microsoft.Extensions.Http.Resilience 9.*
- [X] T004 [P] Add NuGet package references to `tests/M2LB.Tjeneste.Integration/M2LB.Tjeneste.Integration.csproj` (Testcontainers.MsSql 4.*, Testcontainers.ServiceBus 4.*, Microsoft.AspNetCore.Mvc.Testing 10.*, FluentAssertions 7.*, xunit.runner.visualstudio 3.*) and to `tests/M2LB.Tjeneste.Unit/M2LB.Tjeneste.Unit.csproj` (FluentAssertions 7.*, xunit.runner.visualstudio 3.*)
- [X] T005 Create `src/M2LB.Tjeneste.Api/appsettings.json` and `appsettings.Development.json` with configuration sections: ConnectionStrings:TjenesteDb, ServiceBus:Namespace, EventHubs:Namespace, EventHubs:ConsumerGroup, BlobStorage:AccountName, Personmodulen:BaseUrl, BarnLinkage:DeadlineHours, BarnLinkage:PendingAlertThreshold, AzureAd (for Microsoft.Identity.Web), AzureAd:SystemIdentityAudience (expected audience claim value for managed-identity callers of the internal endpoint — used by T032), Serilog

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain model, persistence layer, authentication, outbox wiring, and integration test infrastructure. MUST be complete before any user story work begins.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T006 [P] Create `src/M2LB.Tjeneste.Domain/Enums/BarnLinkageStatus.cs` with values Pending=0, Linked=1, PermanentlyUnresolved=2 and one-way state machine documented in XML doc comments per `specs/001-tjenestemodul-m01/data-model.md`
- [X] T007 [P] Create `src/M2LB.Tjeneste.Domain/Entities/Tjeneste.cs` with all 13 fields: Id (Guid), BirkTiltakKey (string), BarnId (Guid?), TjenesteTypeId (Guid), StatusId (Guid), AvslutningsarsakId (Guid?), PlanlagtInnflyttingsdato (DateOnly?), AktuelInnflyttingsdato (DateOnly?), PlanlagtUtflyttingsdato (DateOnly?), AktuelUtflyttingsdato (DateOnly?), BarnLinkageStatus (BarnLinkageStatus), OpprettetTidspunkt (DateTimeOffset), OppdatertTidspunkt (DateTimeOffset)
- [X] T008 [P] Create `src/M2LB.Tjeneste.Domain/Entities/TjenesteType.cs` with Id (Guid), BirkTypeKey (string), Navn (string), NivaaPath (string), OpprettetTidspunkt (DateTimeOffset), OppdatertTidspunkt (DateTimeOffset)
- [X] T009 [P] Create `src/M2LB.Tjeneste.Domain/Entities/TjenesteStatus.cs` with Id (Guid), BirkStatusKey (string), Kode (string), Navn (string), OpprettetTidspunkt (DateTimeOffset)
- [X] T010 [P] Create `src/M2LB.Tjeneste.Domain/Entities/Avslutningsarsak.cs` with Id (Guid), BirkArsakKey (string), Kode (string), Navn (string), OpprettetTidspunkt (DateTimeOffset)
- [X] T011 Create `src/M2LB.Tjeneste.Domain/Exceptions/TjenesteDomainException.cs` for domain invariant violations: re-link attempt on already-linked Tjeneste, null BarnId on TjenesteOpprettet publish
- [X] T012 Create `src/M2LB.Tjeneste.Domain/Services/ITjenesteRepository.cs` with methods: GetByBarnIdAsync(Guid), GetByIdAsync(Guid), GetByBirkTiltakKeyAsync(string, bool ignoreFilter), GetPendingByBirkTiltakKeysAsync(IEnumerable<string> birkTiltakKeys), UpsertAsync(Tjeneste)
- [X] T013 Create `src/M2LB.Tjeneste.Infrastructure/Persistence/TjenesteDbContext.cs` with DbSets for Tjeneste, TjenesteType, TjenesteStatus, Avslutningsarsak; apply HasQueryFilter on Tjeneste (BarnLinkageStatus == Linked) to enforce FR-003 and FR-004 globally; SQL schema: `tjeneste`
- [X] T014 Create `src/M2LB.Tjeneste.Infrastructure/Persistence/BirkStagingDbContext.cs` as a second DbContext for schema `birk_staging`; staging entity registrations added in Phase 5 (US4)
- [X] T015 [P] Create `src/M2LB.Tjeneste.Infrastructure/Persistence/Configurations/TjenesteConfiguration.cs` with unique constraint on BirkTiltakKey, DateOnly properties mapped to `date` column type, BarnLinkageStatus stored as int
- [X] T016 [P] Create `src/M2LB.Tjeneste.Infrastructure/Persistence/Configurations/TjenesteTypeConfiguration.cs` with unique constraint on BirkTypeKey
- [X] T017 [P] Create `src/M2LB.Tjeneste.Infrastructure/Persistence/Configurations/TjenesteStatusConfiguration.cs` with unique constraint on BirkStatusKey
- [X] T018 [P] Create `src/M2LB.Tjeneste.Infrastructure/Persistence/Configurations/AvslutningsarsakConfiguration.cs` with unique constraint on BirkArsakKey
- [X] T019 Create `src/M2LB.Tjeneste.Infrastructure/Persistence/Repositories/TjenesteRepository.cs` implementing ITjenesteRepository; use `ExecuteUpdate` upsert by BirkTiltakKey for idempotent CDC writes (FR-012); GetByBirkTiltakKeyAsync must call `IgnoreQueryFilters()` when ignoreFilter=true so TiltakLookupHandler can access non-Linked records (US3); all other queries honour the named query filter
- [X] T020 Run EF Core migration: `dotnet ef migrations add InitialCreate -p src/M2LB.Tjeneste.Infrastructure -s src/M2LB.Tjeneste.Api` to generate `src/M2LB.Tjeneste.Infrastructure/Migrations/` for the `tjeneste` schema (birk_staging migration added in T036)
- [X] T021 Create `src/M2LB.Tjeneste.Infrastructure/Auth/AutorisasjonsmodulClient.cs` with IAutorisasjonsmodulClient interface; calls platform authorization eval API per GL-08; register with AddHttpClient and AddStandardResilienceHandler; test double swapped in TjenesteWebApplicationFactory
- [X] T022 Create `src/M2LB.Tjeneste.Api/Middleware/CorrelationIdMiddleware.cs` reading X-Correlation-Id request header (or generating UUID v4 if absent), adding to ILogger scope as `correlation_id`, and echoing in response header
- [X] T023 Write `src/M2LB.Tjeneste.Api/Program.cs` skeleton: AddMicrosoftIdentityWebApiAuthentication, AddGraphQLServer().AddTypes().AddAuthorization().AddProjections(), AddDbContext<TjenesteDbContext> and AddDbContext<BirkStagingDbContext> with connection string, UseWolverine with UseAzureServiceBus().UseTopicAndSubscriptionRouting(), subscription for BarnRegistrert: `opts.ListenToAzureServiceBusSubscription("personmodulen", "tjeneste-barnregistrert")` with Wolverine routing to BarnRegistrertConsumer (I2), and PersistMessagesWithSqlServer(schemaName: "wolverine"), UseAuthentication/UseAuthorization, UseMiddleware<CorrelationIdMiddleware>, MapGraphQL("/graphql"); stub MapGet for internal endpoint and MapHealthChecks("/health") — details wired in user story phases
- [X] T023a Create `src/M2LB.Tjeneste.Api/GraphQL/Authorization/TjenesteAuthorizationHandler.cs` implementing ASP.NET Core `IAuthorizationHandler`; for each `IAuthorizationRequirement` carrying a Tjeneste policy name (e.g. "Tjeneste:HentTjenesterForBarn"), call `IAutorisasjonsmodulClient.EvaluateAsync(userId, policy)`; on any exception or non-success response call `context.Fail()` — never `context.Succeed()` on error (GL-25 fail-closed); register in `src/M2LB.Tjeneste.Api/Program.cs` via `builder.Services.AddSingleton<IAuthorizationHandler, TjenesteAuthorizationHandler>()` (I1)
- [X] T024 Create `tests/M2LB.Tjeneste.Integration/Fixtures/TjenesteWebApplicationFactory.cs` extending WebApplicationFactory<Program> with IAsyncLifetime; spin up MsSql and Azure Service Bus emulator via Testcontainers sharing across test classes with IAssemblyFixture; override ConnectionStrings:TjenesteDb and ServiceBus:Namespace in ConfigureTestServices; replace IAutorisasjonsmodulClient with a permissive stub that approves all requests

**Checkpoint**: Domain model, EF Core, Wolverine, auth, and integration test factory are ready — user story work can begin.

---

## Phase 3: User Story 1 — Saksbehandler ser tjenesteoversikt for et barn (Priority: P1) 🎯 MVP

**Goal**: Case workers can query the full placement history for a child via GraphQL, sorted by most recent first, with an audit event published via the outbox after every read.

**Independent Test**: Seed three placements for a child (two Linked, one Pending) via TjenesteDbContext directly; call `tjenesterForBarn(barnId)` via HttpClient; verify two placements returned in correct sort order, correct field values, correct LeseloggHendelse published.

- [X] T025 [P] [US1] Create `src/M2LB.Tjeneste.Domain/Events/LeseloggHendelseEvent.cs` record with fields: HendelsesId (Guid), HendelsesTidspunkt (DateTimeOffset), BrukerId (Guid), BarnId (Guid), OperasjonNavn (string), Tjenestenavn (string), KorrelasjonId (Guid) per `specs/001-tjenestemodul-m01/data-model.md`
- [X] T026 [P] [US1] Create `src/M2LB.Tjeneste.Api/GraphQL/Types/TjenesteStatusType.cs` as ObjectType<TjenesteStatus> exposing only Kode and Navn; exclude BirkStatusKey per MP-03
- [X] T027 [P] [US1] Create `src/M2LB.Tjeneste.Api/GraphQL/Types/AvslutningsarsakType.cs` as ObjectType<Avslutningsarsak> exposing only Kode and Navn; exclude BirkArsakKey per MP-03
- [X] T028 [US1] Create `src/M2LB.Tjeneste.Api/GraphQL/Types/TjenesteType.cs` as ObjectType<Tjeneste> exposing: id, tjenesteNavn (resolved from TjenesteType.NivaaPath), status (TjenesteStatusType), avslutningsarsak (AvslutningsarsakType?), planlagtInnflyttingsdato (Date scalar), aktuelInnflyttingsdato (Date scalar), planlagtUtflyttingsdato (Date scalar), aktuelUtflyttingsdato (Date scalar); bind UUID and Date custom scalars; exclude BirkTiltakKey and BarnId per MP-03
- [X] T029 [US1] Create `src/M2LB.Tjeneste.Api/GraphQL/Queries/TjenesterQuery.cs` as QueryType with tjenesterForBarn(barnId: UUID!): [Tjeneste!]! annotated with [Authorize(Policy: "Tjeneste:HentTjenesterForBarn")]; sort results by AktuelInnflyttingsdato ?? PlanlagtInnflyttingsdato descending with null-date records last (FR-001); after authorization confirmed, publish LeseloggHendelseEvent via Wolverine outbox with BrukerId from JWT `sub` claim and KorrelasjonId from IHttpContextAccessor (FR-006a, GL-32)
- [X] T030 [US1] Write `tests/M2LB.Tjeneste.Integration/GraphQL/TjenesterForBarnQueryTests.cs` covering 7 scenarios: two Linked placements returned sorted most-recent-first, child with no placements returns empty list `[]`, Pending placement excluded from results, authenticated user lacking Tjeneste:HentTjenesterForBarn claim receives error, unauthenticated request returns 401, IAutorisasjonsmodulClient throws HTTP exception → access denied not passed (GL-25 fail-closed, F3), seeded 50-record dataset returned in under 500ms measured with Stopwatch (SC-001, F5)

**Checkpoint**: `tjenesterForBarn` is fully functional and independently testable without any BiRK sync infrastructure.

---

## Phase 4: User Story 3 — Hendelsestjenesten slår opp barn for et BiRK-tiltak (Priority: P1)

**Goal**: Hendelsestjenesten can call the internal REST endpoint with a BiRK Tiltak key and receive the resolved child and placement identifiers, or a clear status code for pending/permanently-unresolved/not-found cases.

**Independent Test**: Seed one Linked, one Pending, and one PermanentlyUnresolved placement directly via BirkStagingDbContext; call `GET /v1/internal/tiltak/{key}` for each; verify 200/409/410/404 responses per contract.

- [X] T031 [US3] Create `src/M2LB.Tjeneste.Api/Internal/TiltakLookupHandler.cs` static handler method: call ITjenesteRepository.GetByBirkTiltakKeyAsync(key, ignoreFilter: true); return 200 `{barnId, tjenesteId}` for Linked, 409 `{kode:"BARN_ID_IKKE_KOBLET"}` for Pending, 410 `{kode:"TILTAK_PERMANENT_UKOBLET"}` for PermanentlyUnresolved, 404 `{kode:"TILTAK_IKKE_FUNNET"}` when not found — per `specs/001-tjenestemodul-m01/contracts/internal-lookup.md`
- [X] T032 [US3] Register route in `src/M2LB.Tjeneste.Api/Program.cs`: `app.MapGet("/v1/internal/tiltak/{birkTiltakKey}", TiltakLookupHandler.Handle).RequireAuthorization("SystemIdentity")`; configure "SystemIdentity" policy in AddAuthorization to require managed identity audience claim matching `AzureAd:SystemIdentityAudience` from configuration (FR-021, A3)
- [X] T033 [US3] Write `tests/M2LB.Tjeneste.Integration/Internal/TiltakLookupTests.cs` covering all 4 acceptance scenarios: Linked placement returns 200 with barnId+tjenesteId, unknown key returns 404 TILTAK_IKKE_FUNNET, Pending returns 409 BARN_ID_IKKE_KOBLET, request without system identity token returns 401

**Checkpoint**: Internal lookup endpoint fully functional with seeded data; US1 and US3 deliver complete P1 value without BiRK sync.

---

## Phase 5: User Story 4 — Synkronisering fra BiRK holder tjenesteoversikten oppdatert (Priority: P1)

**Goal**: BiRK CDC changes ingested via Event Hubs are written to `birk_staging`, translated to domain entities in `tjeneste`, and queryable within 1–2 minutes; full import runs on first startup.

**Independent Test**: Publish Debezium CDC events to the Event Hub emulator; verify staging rows created/updated; verify translated domain Tjeneste records created via idempotent upsert; verify duplicate events produce no duplicates.

- [X] T034 [P] [US4] Create `src/M2LB.Tjeneste.Infrastructure/BiRK/BirkDebeziumEnvelope.cs` using System.Text.Json to deserialize `payload.before`, `payload.after`, and `payload.op`; op routing: "c"→insert, "u"→upsert, "d"→status transition (look up the BiRK-side termination status by BirkStatusKey and update Tjeneste.StatusId — do NOT delete the row; PP-05 prohibits hard deletes) (A2)
- [X] T035 [P] [US4] Create `src/M2LB.Tjeneste.Infrastructure/BiRK/BirkFieldMappings.json` with per-table whitelist arrays and field name translation dictionaries for: birk_tiltak, birk_tiltakstype, birk_statustype, birk_avslutningsarsaktype, birk_oppdrag; unknown fields are silently dropped by the adapter (FR-008, FR-009)
- [X] T036 [P] [US4] Create staging entity classes in `src/M2LB.Tjeneste.Infrastructure/Persistence/`: BirkTiltakStaging.cs, BirkTiltakstypeStaging.cs, BirkStatustypeStaging.cs, BirkAvslutningsarsakstypeStaging.cs, BirkOppdragStaging.cs — each with string PK `birkkey`, whitelisted fields (matching BirkFieldMappings.json), `_ingestert_tidspunkt` (DateTimeOffset), `_oppdatert_tidspunkt` (DateTimeOffset); register all in BirkStagingDbContext; run `dotnet ef migrations add AddBirkStaging -p src/M2LB.Tjeneste.Infrastructure -s src/M2LB.Tjeneste.Api` for birk_staging schema
- [X] T037 [P] [US4] Create `src/M2LB.Tjeneste.Infrastructure/Messaging/IPersonmodulClient.cs` and `src/M2LB.Tjeneste.Infrastructure/Messaging/PersonmodulClient.cs` calling Personmodulen child lookup by BirkBarnKey; register in `src/M2LB.Tjeneste.Api/Program.cs` with AddHttpClient<IPersonmodulClient, PersonmodulClient>.AddStandardResilienceHandler() pointing to Personmodulen:BaseUrl (FR-014)
- [X] T038 [US4] Create `src/M2LB.Tjeneste.Infrastructure/BiRK/BirkTiltakAdapter.cs`: load BirkFieldMappings.json at startup; filter CDC payload to whitelist fields only (FR-008); translate BiRK field names to M2LB names (FR-009); call IPersonmodulClient to resolve BarnId — on 404 or retries-exhausted store with BarnId=null and BarnLinkageStatus=Pending (FR-014); call ITjenesteRepository.UpsertAsync for idempotent write (FR-012); on missing lookup record (TjenesteType/TjenesteStatus/Avslutningsarsak), defer Service Bus message by sequence number and publish self-directed scheduled retry after configurable interval (default 30 s) per FR-012a
- [X] T039 [US4] Create `src/M2LB.Tjeneste.Infrastructure/BiRK/BirkImportService.cs` IHostedService: on startup check `birk_import_complete` flag in SQL; if absent, call BiRK snapshot endpoint to load tables in order: oppdrag → tiltakstype → statustype → avslutningsarsaktype → tiltak (FR-010), processing each row through BirkTiltakAdapter; set flag on success; if flag present and Event Hubs checkpoint valid, skip to incremental mode (FR-011); if checkpoint expired, re-run full import
- [X] T040 [US4] Create `src/M2LB.Tjeneste.Infrastructure/BiRK/BirkCdcProcessorService.cs` IHostedService: use EventProcessorClient with BlobContainerClient checkpoints (one container per Event Hub + consumer group, must be pre-provisioned); route Debezium events to BirkTiltakAdapter by table partition; checkpoint after every 50 events (FR-011); on processing error, allow Service Bus MaxDeliveryCount dead-lettering; emit `logger.LogError` with structured fields `{EventName: "MessageDeadLettered", TableName, PartitionId}` — Azure Monitor alert rules target this event name for operational alerting (FR-013, A1); also emit `logger.LogInformation` per batch with `{TableName, MessagesProcessed, WritesSucceeded, WritesFailed}` for FR-026 per-table metrics (F4)
- [X] T041 [US4] Create `src/M2LB.Tjeneste.Api/Health/BirkSyncHealthCheck.cs` implementing IHealthCheck: query pending and permanently-unresolved linkage counts using `IgnoreQueryFilters()` — the named query filter returns only Linked records and must be bypassed here (F2); report Degraded when pending count exceeds BarnLinkage:PendingAlertThreshold; report estimated CDC lag from EventProcessorClient partition properties (FR-025, FR-026)
- [X] T042 [US4] Write `tests/M2LB.Tjeneste.Unit/Infrastructure/BirkTiltakAdapterTests.cs` covering: non-whitelisted field silently dropped, BiRK field name translated to M2LB name per BirkFieldMappings.json, BarnId stored as null when PersonmodulClient returns 404 after retries
- [X] T043 [US4] Write `tests/M2LB.Tjeneste.Integration/Sync/BirkCdcProcessorTests.cs` covering 5 acceptance scenarios: new placement insert creates domain record with correct fields, update overwrites existing record without creating duplicate, duplicate CDC message produces no duplicate row, missing lookup record triggers deferral, transient failure retried exponentially and eventually dead-lettered (FR-013)

**Checkpoint**: BiRK synchronization pipeline is functional; all three P1 stories (US1, US3, US4) deliver end-to-end value.

---

## Phase 6: User Story 2 — Saksbehandler ser detaljer for én tjeneste (Priority: P2)

**Goal**: Case workers can look up a single placement by its internal UUID; null returned for non-existent or pending-linkage placements.

**Independent Test**: Seed one Linked placement and one Pending placement; query `tjeneste(id)` for each and for an unknown UUID; verify correct responses.

- [X] T044 [US2] Create `src/M2LB.Tjeneste.Api/GraphQL/Queries/TjenesteQuery.cs` as QueryType with tjeneste(id: UUID!): Tjeneste annotated with [Authorize(Policy: "Tjeneste:HentTjeneste")]; returns null when not found or BarnLinkageStatus != Linked (FR-004); publishes LeseloggHendelseEvent via Wolverine outbox using BarnId from query argument and BrukerId from JWT (FR-006a, GL-32)
- [X] T045 [US2] Write `tests/M2LB.Tjeneste.Integration/GraphQL/TjenesteQueryTests.cs` covering 3 acceptance scenarios: Linked placement returns all fields including optional null fields as null, unknown UUID returns null result (not error), Pending placement returns null result

**Checkpoint**: Both GraphQL queries fully functional; User Stories 1 and 2 independently testable.

---

## Phase 7: User Story 5 — Asynkron kobling av barn til tjeneste (Priority: P2)

**Goal**: Pending placements are automatically linked when `BarnRegistrert` arrives; `TjenesteOpprettet` published via transactional outbox; placements past the configurable deadline flagged as permanently unresolved.

**Independent Test**: Seed a Pending Tjeneste with a known BirkBarnKey; publish a BarnRegistrert Wolverine message; verify Tjeneste transitions to Linked, appears in `tjenesterForBarn`, and `TjenesteOpprettet` is relayed to Service Bus by the Wolverine outbox relay.

- [X] T046 [P] [US5] Create `src/M2LB.Tjeneste.Domain/Events/TjenesteOpprettetEvent.cs` record with fields: HendelsesId (Guid), HendelsesTidspunkt (DateTimeOffset), TjenesteId (Guid), BirkTiltakKey (string — justified exception to PP-08, required by Hendelsestjenesten per plan.md), BarnId (Guid — NEVER null, FR-017), TjenesteNavn (string), OpprettetTidspunkt (DateTimeOffset)
- [X] T047 [US5] Create `src/M2LB.Tjeneste.Domain/Services/BarnLinkageService.cs`: LinkBarnIdAsync(Tjeneste, Guid barnId) — throw TjenesteDomainException if BarnId already set (write-once guard, FR-018); set BarnId and transition BarnLinkageStatus to Linked; call ITjenesteRepository.UpsertAsync within the same Wolverine outbox transaction; publish TjenesteOpprettetEvent only after BarnId confirmed non-null (FR-017, GL-33); Wolverine outbox guarantees at-least-once retry on publish failure (FR-016)
- [X] T048 [US5] Create `src/M2LB.Tjeneste.Infrastructure/Messaging/BarnRegistrertConsumer.cs` Wolverine message handler consuming BarnRegistrert from Personmodulen topic subscription; first query BirkStagingDbContext to find all birk_tiltak staging rows where the child BiRK key field matches BarnRegistrert.BirkBarnKey — extracting a list of birkkey values; then call ITjenesteRepository.GetPendingByBirkTiltakKeysAsync(birkkeys) to retrieve matching Pending Tjeneste records; call BarnLinkageService.LinkBarnIdAsync for each (F1 — two-step staging lookup avoids adding BirkBarnKey to the domain entity); if all already Linked, return without action — silent no-op (FR-015, GL-22); Wolverine handles outbox atomicity across all linked placements
- [X] T049 [US5] Create `src/M2LB.Tjeneste.Infrastructure/Messaging/BarnLinkageDeadlineService.cs` IHostedService: on configurable polling interval, find Pending Tjeneste records where OpprettetTidspunkt < now − BarnLinkage:DeadlineHours; transition each to PermanentlyUnresolved via ITjenesteRepository.UpsertAsync (FR-019a); when pending count exceeds BarnLinkage:PendingAlertThreshold, write structured warning log for alerting (FR-019)
- [X] T050 [US5] Write `tests/M2LB.Tjeneste.Unit/Domain/BarnLinkageServiceTests.cs` covering: write-once guard throws TjenesteDomainException on re-link attempt, TjenesteOpprettetEvent.BarnId is always non-null (no null sneaks through), calling LinkBarnId when already Linked is rejected not silently accepted
- [X] T051 [US5] Write `tests/M2LB.Tjeneste.Unit/Domain/TjenesteSortingTests.cs` covering sort key logic applied in TjenesterQuery: AktuelInnflyttingsdato descending, fallback to PlanlagtInnflyttingsdato when actual is null, placements with both dates null sorted to the end (FR-001, clarification §1)
- [X] T052 [US5] Write `tests/M2LB.Tjeneste.Integration/Messaging/BarnRegistrertConsumerTests.cs` covering 3 scenarios: Pending placement becomes Linked and visible in `tjenesterForBarn` after BarnRegistrert, already-Linked placement is unaffected (no state change, no error), multiple Pending placements for same child are all linked and each triggers a TjenesteOpprettet outbox message

**Checkpoint**: Full async linkage lifecycle functional; all 5 user stories independently testable end-to-end.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Operational readiness, startup registration, observability wiring, and final cross-cutting validation.

- [X] T053 Create `src/M2LB.Tjeneste.Infrastructure/Messaging/OperationsRegistrationService.cs` IHostedService: on startup, publish two operation descriptors (Tjeneste:HentTjenesterForBarn, Tjeneste:HentTjeneste) to platform shared operations registration Service Bus queue so the authorization module can register them (FR-027, PS-06, GL-09)
- [X] T054 Register health checks in `src/M2LB.Tjeneste.Api/Program.cs`: AddDbContextCheck<TjenesteDbContext>(), AddCheck<BirkSyncHealthCheck>("birk-sync"), MapHealthChecks("/health") — unauthenticated endpoint, no sensitive data (FR-024, FR-025, PS-08)
- [X] T055 Configure Serilog in `src/M2LB.Tjeneste.Api/Program.cs`: UseSerilog(ctx, cfg => cfg.ReadFrom.Configuration(ctx.Configuration).Enrich.WithCorrelationId()) using the correlation ID added to ILogger scope by CorrelationIdMiddleware; structured output to Application Insights (PS-08)
- [X] T056 [P] Register all hosted services in `src/M2LB.Tjeneste.Api/Program.cs`: OperationsRegistrationService, BirkImportService, BirkCdcProcessorService, BarnLinkageDeadlineService; verify registration order ensures BirkImportService starts before BirkCdcProcessorService (FR-010)
- [X] T057 [P] Final cross-cutting validation: grep codebase to confirm BirkTiltakKey and BirkBarnKey never appear in `src/M2LB.Tjeneste.Api/GraphQL/` type or query files (MP-03); confirm TjenesteOpprettetEvent.BarnId has non-nullable Guid type and BarnLinkageService guard prevents null (FR-017); confirm LeseloggHendelseEvent outbox publish call present in both TjenesterQuery and TjenesteQuery (FR-006a)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 — BLOCKS all user story phases
- **User Stories (Phases 3–7)**: All depend on Foundational phase completion
  - US1 (Phase 3), US3 (Phase 4), and US4 (Phase 5) can run in parallel once Foundational is done
  - US2 (Phase 6) depends on Foundational and reuses HC types created in US1
  - US5 (Phase 7) depends on Foundational; end-to-end CDC→linkage test path benefits from US4 completion
- **Polish (Phase 8)**: Depends on all user story phases

### User Story Dependencies

| Story | Priority | Depends On | Can Run In Parallel With |
|-------|----------|------------|--------------------------|
| US1 | P1 | Phase 2 | US3, US4 |
| US3 | P1 | Phase 2 | US1, US4 |
| US4 | P1 | Phase 2 | US1, US3 |
| US2 | P2 | Phase 2 + US1 HC types (T026–T028) | US5 |
| US5 | P2 | Phase 2 | US2 |

### Within Each User Story

- Domain events/records ([P] tasks) before services
- Services before resolvers and handlers
- Resolvers and handlers before integration tests
- Parallel [P] tasks within a phase can start simultaneously

### Parallel Opportunities

- T002–T004 (package references): all three in parallel
- T006–T010 (domain entities): all five in parallel
- T015–T018 (EF configurations): all four in parallel
- T025–T027 (HC types for US1): all three in parallel
- T034–T037 (BiRK infrastructure primitives for US4): all four in parallel
- T046 (TjenesteOpprettetEvent) in parallel with other US5 prep work
- T056–T057 (Polish tasks): both in parallel

---

## Parallel Example: User Story 1

```bash
# Step 1 — launch parallel:
Task T025: LeseloggHendelseEvent (src/M2LB.Tjeneste.Domain/Events/)
Task T026: TjenesteStatusType (src/M2LB.Tjeneste.Api/GraphQL/Types/)
Task T027: AvslutningsarsakType (src/M2LB.Tjeneste.Api/GraphQL/Types/)

# Step 2 — sequential (depends on T026, T027):
Task T028: TjenesteType

# Step 3 — sequential (depends on T025, T028):
Task T029: TjenesterQuery

# Step 4 — sequential (depends on T029):
Task T030: Integration tests
```

## Parallel Example: User Story 4 (BiRK Sync)

```bash
# Step 1 — launch parallel:
Task T034: BirkDebeziumEnvelope
Task T035: BirkFieldMappings.json
Task T036: Staging entity classes + migration
Task T037: IPersonmodulClient + PersonmodulClient

# Step 2 — sequential (depends on T034, T035, T036, T037):
Task T038: BirkTiltakAdapter

# Step 3 — launch parallel (depends on T038):
Task T039: BirkImportService
Task T040: BirkCdcProcessorService
Task T041: BirkSyncHealthCheck
Task T042: Unit tests for BirkTiltakAdapter

# Step 4 — sequential:
Task T043: Integration tests for BirkCdcProcessor
```

---

## Implementation Strategy

### MVP First (User Stories 1 + 3 — both P1 read paths)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL — blocks everything)
3. Seed test data directly into TjenesteDbContext
4. Complete Phase 3: US1 (GraphQL placement history)
5. Complete Phase 4: US3 (internal REST lookup)
6. **STOP and VALIDATE**: Both P1 read endpoints working against seeded data — demonstrable to Hendelsestjenesten team
7. Deploy MVP

### Incremental Delivery

1. Setup + Foundational → project compiles, DB schema created
2. US1 → case worker can query placement history (seeded data)
3. US3 → Hendelsestjenesten can resolve Tiltak→Child (seeded data)
4. US4 → BiRK CDC auto-populates data; seeded data no longer needed
5. US2 → single placement lookup added (low effort, reuses US1 types)
6. US5 → async child linkage completes the full end-to-end flow
7. Polish → operational readiness for production

### Parallel Team Strategy

After Phase 2 completes:
- **Developer A**: US1 — GraphQL placement history + LeseloggHendelse
- **Developer B**: US3 — Internal REST lookup handler
- **Developer C**: US4 — BiRK CDC pipeline

All three deliver independently testable increments. US2 and US5 follow in the next sprint.

---

## Notes

- [P] tasks operate on different files with no dependencies — safe to parallelize
- [USN] labels map each task to its user story for traceability
- Seed placements directly via TjenesteDbContext in integration test fixtures — US1/US3 do not require US4 to be implemented first
- TjenesteWebApplicationFactory (T024) replaces IAutorisasjonsmodulClient with a permissive test stub
- `IgnoreQueryFilters()` is required in TjenesteRepository when called from TiltakLookupHandler and BarnRegistrertConsumer — the named query filter must only be bypassed explicitly, never globally
- Wolverine's `PersistMessagesWithSqlServer` creates the `wolverine` outbox schema automatically at startup
- Event Hubs Blob Storage checkpoint containers must be pre-provisioned before deploying BirkCdcProcessorService (per `specs/001-tjenestemodul-m01/research.md` §4)
- BiRK terminology (BirkTiltakKey, BirkTypeKey, BirkStatusKey, BirkArsakKey) must never appear in `src/M2LB.Tjeneste.Api/GraphQL/` files — enforced by T057
