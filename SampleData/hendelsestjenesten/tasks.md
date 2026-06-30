# Tasks: Hendelsestjenesten

**Input**: Design documents from `specs/001-hendelsestjenesten/`
**Prerequisites**: plan.md âœ…, spec.md âœ…, research.md âœ…, data-model.md âœ…, contracts/ âœ…, quickstart.md âœ…

**Tests**: Included â€” mandatory per constitution GL-24/PP-09. Test tasks are derived from `docs/Hendelsestjenesten-â€”-Testspesifikasjon.md` (TEST-U-01â€“U-16, TEST-I-01â€“I-21, TEST-E-01â€“E-03). Each user story phase lists tests before implementation tasks.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1â€“US5)
- Exact file paths are included in all task descriptions

---

## Phase 1: Setup

**Purpose**: Create solution and project files from scratch â€” repository is currently empty.

- [X] T001 Create `M2LB.Hendelse.sln` and five project skeletons: `src/M2LB.Hendelse.Api/`, `src/M2LB.Hendelse.Domain/`, `src/M2LB.Hendelse.Infrastructure/`, `tests/M2LB.Hendelse.Unit/`, `tests/M2LB.Hendelse.Integration/` using `dotnet new` commands per quickstart.md project structure
- [X] T002 Add NuGet packages to each `.csproj`: EF Core 10 + SQL Server provider + tools (`Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Design`), Hot Chocolate 14 (`HotChocolate.AspNetCore`, `HotChocolate.Data`), Wolverine 3.x (`Wolverine`, `Wolverine.AzureServiceBus`, `Wolverine.EntityFrameworkCore`), Serilog (`Serilog.AspNetCore`), OpenTelemetry (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.SqlClient`, `Azure.Monitor.OpenTelemetry.AspNetCore`), Azure SDK (`Azure.Identity`, `Azure.Extensions.AspNetCore.Configuration.Secrets`), xUnit + FluentAssertions + Testcontainers for test projects
- [X] T003 [P] Create `src/M2LB.Hendelse.Api/appsettings.json` and `src/M2LB.Hendelse.Api/appsettings.Development.json` with `ConnectionStrings:HendelseDb`, `Wolverine:ServiceBusNamespace`, and `Autorisasjon:BaseUrl` keys per quickstart.md â€” no secrets in source (GL-26)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented.

**âš ï¸ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T004 [P] Create domain entity classes in `src/M2LB.Hendelse.Domain/Entities/`: `Hendelse.cs`, `HendelsesVersjon.cs`, `Involvert.cs`, `InngrepDetalj.cs`, `RommingsDetalj.cs` â€” implement all columns from data-model.md with correct C# types; include `IsAktiv bool` (default `true`) on `Hendelse.cs` per GL-18 soft-delete requirement; `HendelsesVersjon` is append-only (no delete); `BarnId` nullable UUID with locking invariant documented
- [X] T005 [P] Create reference data entity classes in `src/M2LB.Hendelse.Domain/ReferenceData/`: `HendelsesType.cs`, `HjemmelType.cs` (with `GjelderFra`/`GjelderTil` validity period), `RommingKategoriType.cs`, `TvangsProtokollStatusType.cs` â€” all include `BirkVerdi` mapping column per data-model.md
- [X] T006 [P] Define `IHendelsesRepository` interface in `src/M2LB.Hendelse.Domain/Interfaces/IHendelsesRepository.cs` with method signatures for all read/write operations needed by domain services (FindByBirkHendelsesId, Add, AddVersjon, UpdateAktivVersjon, HentHendelserForBarn, HentHendelse, HentVentende, KoblBarnId, GetReferanseData methods)
- [X] T007 Implement `HendelseDbContext` in `src/M2LB.Hendelse.Infrastructure/Data/HendelseDbContext.cs` with full EF Core 10 Fluent API configuration: unique index on `BirkHendelsesId`, clustered index on `BarnId`, partial index on `BirkTiltakPK WHERE BarnId IS NULL`; configure `IsAktiv` column with default value `1` on `Hendelse` per GL-18; enforce no `Remove()` on `HendelsesVersjon` via override of `SaveChanges`; configure all FK relationships and nullable columns per data-model.md
- [X] T008 Create initial EF Core code-first migration in `src/M2LB.Hendelse.Infrastructure/Data/Migrations/` and implement `HendelseDbContextSeed.cs` in `src/M2LB.Hendelse.Infrastructure/Data/` seeding `HendelsesType` (Inngrep, Romming, Uteblivelse, Bortforing), `HjemmelType`, `RommingKategoriType`, and `TvangsProtokollStatusType` reference tables at migration time (H-02)
- [X] T009 [P] Implement `AutorisasjonClient` in `src/M2LB.Hendelse.Infrastructure/Authorization/AutorisasjonClient.cs` using typed `HttpClient` registered via `AddHttpClient`; calls `POST /api/autorisasjon/v1/evaluer`; throws/returns HTTP 503 to caller when API unreachable (fail-closed per GL-25); no cached auth decisions
- [X] T010 Implement `WolverineSetup.cs` in `src/M2LB.Hendelse.Infrastructure/WolverineSetup.cs` with static extension method `AddWolverineInfrastructure`: configure EF Core persistence integration (`wolverine_outgoing_envelopes`, `wolverine_incoming_envelopes`, `wolverine_dead_letters`), Azure Service Bus with `DefaultAzureCredential` (no connection string), subscribe to `tjeneste.tjenester` topic with subscription `hendelsestjenesten`, publish to `hendelser.barn` topic and `revisjon.leselogg` queue, DLQ routing for persistent failures (GL-23)
- [X] T011 [P] Implement `KorrelasjonsIdMiddleware` in `src/M2LB.Hendelse.Api/Middleware/KorrelasjonsIdMiddleware.cs` that reads/propagates W3C TraceContext `traceparent`/`tracestate` headers and sets `Activity.Current` correlation
- [X] T012 Implement `Program.cs` in `src/M2LB.Hendelse.Api/Program.cs`: register Azure Key Vault config source via `DefaultAzureCredential`, configure Serilog JSON sink with `CorrelationId`/`MachineName`/`Environment` enrichers, register `HendelseDbContext` with SQL Server, call `AddWolverineInfrastructure`, register `AutorisasjonClient`, configure OpenTelemetry (tracing + metrics for ASP.NET Core, HTTP client, SQL Client â†’ Azure Monitor exporter), add bearer token authentication (EntraID OIDC), add `KorrelasjonsIdMiddleware`
- [X] T039 Define `LeseloggHendelseMessage` class in `src/M2LB.Hendelse.Infrastructure/Messaging/LeseloggHendelseMessage.cs` with all six constitution-required fields: `HendelsesId` (Guid, unique log entry ID), `BrukerId` (Guid, caller identity from bearer token), `Operasjon` (string, e.g. `Hendelse:HentHendelserForBarn`), `RessursId` (Guid, ID of the read object), `BarnId` (Guid), `Tidspunkt` (DateTime UTC); routing to `revisjon.leselogg` queue is already configured by T010 in `WolverineSetup.cs` â€” no additional setup needed here
- [X] T040 [P] Configure Testcontainers base infrastructure in `tests/M2LB.Hendelse.Integration/Infrastructure/SqlServerTestFixture.cs` (spin up `mcr.microsoft.com/mssql/server` container, run EF Core migrations, seed reference data) and create `HendelseWebFactory.cs` in the same folder â€” `WebApplicationFactory<Program>` subclass that substitutes the container connection string; this fixture is the shared base for all integration test classes

**Checkpoint**: Build passes (`dotnet build`). EF Core migration runs (`dotnet ef database update`). Testcontainers fixture compiles. Foundation ready â€” user story implementation can begin.

---

## Phase 3: User Story 3 â€” Hendelsesinnmating (Priority: P1) ðŸŽ¯ MVP

**User Story**: Som Hendelsesadapteren skal jeg kunne levere inngrep- og rÃ¸mmingshendelser til Hendelsestjenesten slik at de lagres idempotent med versjonert historikk og publiseres videre.
*(Scenario 4 from spec.md)*

**Goal**: REST intake API fully operational; BiRK adapter can PUT events; idempotent versioning works; `HendelsesRegistrert` published via Wolverine outbox.

**Independent Test**: PUT identical payload twice â†’ `204 No Content`. PUT changed payload â†’ `200 OK`, new `HendelsesVersjon` created, old version retained. Missing required field â†’ `422`. Check `wolverine_outgoing_envelopes` contains `HendelsesRegistrert` envelope with correct BarnId.

### Tests for User Story 3 âš ï¸ Write and confirm FAIL before implementing T013â€“T017

- [X] T041 [P] [US3] Unit tests for `HendelsesInnmatingTjeneste` in `tests/M2LB.Hendelse.Unit/HendelsesInnmatingTjenesteTests.cs` covering TEST-U-01â€“U-03 (Inngrep validation), TEST-U-07â€“U-09 (Romming validation), TEST-U-10â€“U-12 (versioning: identical payload â†’ 204/no new version; changed payload â†’ 200/new version appended; category change â†’ new version not new Hendelse) per `docs/Hendelsestjenesten-â€”-Testspesifikasjon.md`
- [X] T042 [P] [US3] Integration tests for intake endpoints in `tests/M2LB.Hendelse.Integration/InnmatingIntegrationTests.cs` using `HendelseWebFactory` covering TEST-I-01â€“I-06: new Inngrep with BarnId set (201 + HendelsesRegistrert in outbox), new Inngrep with BarnId=null (201, no HendelsesRegistrert), new Romming (201), BarnId without BirkTiltakPK (201), BarnId=null without BirkTiltakPK (422)

### Implementation for User Story 3

- [X] T013 Implement `HendelsesRepository` in `src/M2LB.Hendelse.Infrastructure/Data/Repositories/HendelsesRepository.cs` implementing `IHendelsesRepository`: FindByBirkHendelsesId (for idempotency), Add (insert Hendelse), AddVersjon (append HendelsesVersjon â€” never Remove), UpdateAktivVersjonId (update FK on Hendelse) â€” all operations within caller-provided DbContext transaction
- [X] T014 [P] [US3] Define intake request DTOs in `src/M2LB.Hendelse.Api/DTOs/InnmatingDtos.cs`: `InngrepsInnmatingRequest` and `RommingsInnmatingRequest` matching the field contracts in `specs/001-hendelsestjenesten/contracts/rest-intake.md`; required fields annotated with `[Required]`; `BarnId` nullable UUID; `BirkTiltakPK` nullable int
- [X] T015 [US3] Implement `HendelsesInnmatingTjeneste` in `src/M2LB.Hendelse.Domain/Services/HendelsesInnmatingTjeneste.cs`: validate required fields (`kildeId`, `hendelsestype`, `FraDato` â€” `tidspunkt` in spec.md maps to `FraDato` in data model); reject with 422 when `BarnId = null` AND `BirkTiltakPK = null` (per spec.md FR-01 and TEST-I-06); idempotency check on `BirkHendelsesId` (unique index); last-write-wins by comparing incoming source timestamp against current `HendelsesVersjon.FraDato/FraTidspunkt` (older â†’ 204, unchanged â†’ 204, newer/changed â†’ new version); append new `HendelsesVersjon`; update `AktivVersjonId`; publish `HendelsesRegistrert` via Wolverine outbox only when `BarnId` is set (not null)
- [X] T016 [US3] Implement `InnmatingController` in `src/M2LB.Hendelse.Api/Controllers/InnmatingController.cs` with route prefix `[Route("api/hendelser/v1")]`; `[HttpPut("innmating/inngrep/{birkHendelsesId}")]` and `[HttpPut("innmating/romming/{birkHendelsesId}")]`; no `AutorisasjonClient` call needed â€” Managed Identity Bearer token validated by auth middleware (T012); return `201 Created`, `200 OK`, `204 No Content`, or `422 Unprocessable Entity` with `feilkode`+`detaljer` per rest-intake.md contract
- [X] T017 [US3] Define `HendelsesRegistrertMessage` class in `src/M2LB.Hendelse.Infrastructure/Messaging/HendelsesRegistrertMessage.cs` matching the `HendelsesRegistrert` JSON contract in `specs/001-hendelsestjenesten/contracts/events.md` (BarnHendelseId, BarnId, TjenesteId, HendelsesTypeId, HendelsesTypeKode, HendelsesTypeNavn, FraDato, FraTidspunkt â€” no personal data per GL-21); configure Wolverine to publish to `hendelser.barn` topic in `WolverineSetup.cs`

**Checkpoint**: US3 fully functional â€” PUT endpoints accept intake, idempotency enforced, versions appended, outbox envelopes created.

---

## Phase 4: User Story 1 â€” Hendelsestidslinje (Priority: P2)

**User Story**: Som saksbehandler skal jeg se en paginert hendelsestidslinje for ett barn slik at jeg har oversikt over alle registrerte hendelser.
*(Scenario 1 from spec.md)*

**Goal**: GraphQL `hentHendelserForBarn` returns paginated, sorted event summary list; leselogg published after each call.

**Independent Test**: Query `hentHendelserForBarn` with valid BarnId â†’ returns `PaginertHendelseResultat` sorted newest-first; default page size 25; max page size 100; filter on `hendelsesTypeKoder` works; missing `Hendelse:HentHendelserForBarn` permission â†’ `HTTP 503`; leselogg envelope in `wolverine_outgoing_envelopes` after successful call.

### Tests for User Story 1 âš ï¸ Write and confirm FAIL before implementing T018â€“T022

- [X] T043 [P] [US1] Integration tests for GraphQL timeline in `tests/M2LB.Hendelse.Integration/HendelseTidslinjeIntegrationTests.cs` using `HendelseWebFactory` covering TEST-I-10â€“I-13: only linked events returned; filter on type works; pagination returns correct page/total/hasNextPage; sort order newest-first; leselogg envelope present in outbox after query

### Implementation for User Story 1

- [X] T018 Add `HentHendelserForBarn` method to `IHendelsesRepository` and implement in `src/M2LB.Hendelse.Infrastructure/Data/Repositories/HendelsesRepository.cs`: paginated LINQ query on `Hendelse` joined to `AktivVersjon`; filter by `BarnId` (required) and optional `HendelsesTypeId` list; `OrderByDescending(v => v.FraDato).ThenByDescending(v => v.FraTidspunkt)`; return `(IReadOnlyList<HendelseSammendrag>, int totalCount)` tuple
- [X] T019 [P] [US1] Implement `HendelsesLeseTjeneste` in `src/M2LB.Hendelse.Domain/Services/HendelsesLeseTjeneste.cs` with `HentHendelserForBarn` method: call `AutorisasjonClient.EvaluerAsync("Hendelse:HentHendelserForBarn", barnId, brukerId)`; enforce fail-closed (throw if unreachable); fetch paginated list from repository; publish `LeseloggHendelse` to `revisjon.leselogg` queue via Wolverine outbox after successful fetch (GL-32)
- [X] T020 [P] [US1] Implement GraphQL summary types in `src/M2LB.Hendelse.Api/GraphQL/Types/HendelseSammendragType.cs` and `src/M2LB.Hendelse.Api/GraphQL/Types/PaginertHendelseResultatType.cs` per graphql-read.md contract (`id`, `type.kode`, `type.navn`, `fraDato`, `sted`, `antallVersjoner`)
- [X] T021 [US1] Implement `HendelseQuery` class in `src/M2LB.Hendelse.Api/GraphQL/HendelseQuery.cs` with `hentHendelserForBarn(barnId: ID!, hendelsesTypeKoder: [String], side: Int, antallPerSide: Int): PaginertHendelseResultat!` resolver that calls `HendelsesLeseTjeneste`; extract caller BarnId and BrukerId from `IHttpContextAccessor` bearer token claims
- [X] T022 [US1] Register Hot Chocolate in `Program.cs`: `AddGraphQLServer()`, add `HendelseQuery`, configure bearer token forwarding to resolver context, map `/graphql` endpoint; confirm GraphQL playground accessible at `https://localhost:5001/graphql` per quickstart.md

**Checkpoint**: US1 fully functional â€” GraphQL timeline query works with auth, pagination, and leselogg publishing.

---

## Phase 5: User Story 2 â€” Hendelsesdetalj med tilgangsstyring (Priority: P3)

**User Story**: Som saksbehandler skal jeg Ã¥pne en enkelt hendelse og se full detalj inkludert versjonhistorikk â€” med feltene involverte, inngrepDetalj og rommingsDetalj kun synlige dersom jeg har riktig rettighet.
*(Scenarios 2, 3, 6 from spec.md)*

**Goal**: GraphQL `hentHendelse` returns full detail; restricted fields absent (not error) when permission missing; all versions visible.

**Independent Test**: Query `hentHendelse` with SeInvolverte permission â†’ `involverte` present. Same query without SeInvolverte â†’ `involverte` absent. `versjonsHistorikk` contains all versions; none deleted. `HTTP 503` when `Autorisasjon` unreachable.

### Tests for User Story 2 âš ï¸ Write and confirm FAIL before implementing T023â€“T026

- [X] T044 [P] [US2] Unit tests for field-level authorization in `tests/M2LB.Hendelse.Unit/FeltsynlighetTests.cs` covering TEST-U-13â€“U-16: `involverte` null without SeInvolverte; `inngrepDetalj` null without SeInngrepDetalj; `rommingsDetalj` null without SeRommingsDetalj; all fields present with all five operations
- [X] T045 [P] [US2] Integration tests for `hentHendelse` in `tests/M2LB.Hendelse.Integration/HendelseDetaljIntegrationTests.cs` covering TEST-I-14â€“I-16: null for unknown ID; null for unlinked event; `versjonsHistorikk` contains all versions; leselogg envelope present after query

### Implementation for User Story 2

- [X] T023 Add `HentHendelse` method to `IHendelsesRepository` and implement in `src/M2LB.Hendelse.Infrastructure/Data/Repositories/HendelsesRepository.cs`: fetch `Hendelse` with all `HendelsesVersjon` rows ordered ascending; include `Involvert`, `InngrepDetalj`, `RommingsDetalj` per version
- [X] T024 [P] [US2] Implement `HendelsesLeseTjeneste.HentHendelse` in `src/M2LB.Hendelse.Domain/Services/HendelsesLeseTjeneste.cs`: call `AutorisasjonClient` for `Hendelse:HentHendelse`; call separately for `SeInvolverte`, `SeInngrepDetalj`, `SeRommingsDetalj` (each independently); strip restricted sub-objects from result when permission denied (not an error); publish leselogg per GL-32
- [X] T025 [P] [US2] Implement GraphQL detail types in `src/M2LB.Hendelse.Api/GraphQL/Types/`: `HendelseDetaljType.cs`, `HendelsesVersjonType.cs`, `InvolvertType.cs`, `InngrepDetaljType.cs`, `RommingsDetaljType.cs` per graphql-read.md field definitions (`aktivVersjon`, `versjonsHistorikk`, `involverte`, `inngrepDetalj`, `rommingsDetalj`)
- [X] T026 [US2] Add `hentHendelse(hendelsesId: ID!): HendelseDetalj` resolver to `src/M2LB.Hendelse.Api/GraphQL/HendelseQuery.cs`; field-level authorization: `involverte`, `inngrepDetalj`, `rommingsDetalj` fields return `null` when service layer strips them (FR-05, graphql-read.md access control rule â€” absent field, not error)

**Checkpoint**: US2 fully functional â€” detail view works; field-level auth strips sensitive fields; version history visible.

---

## Phase 6: User Story 4 â€” Asynkron barnkobling (Priority: P4)

**User Story**: Som Hendelsestjenesten skal hendelser lagret uten barnIdentitet kobles automatisk til riktig barn og tjeneste nÃ¥r TjenesteOpprettet-meldingen ankommer fra Tjenestemodul.
*(Scenario 5 from spec.md)*

**Goal**: Wolverine handler receives `TjenesteOpprettet`; all matching `Hendelse` rows (same `BirkTiltakPK`, `BarnId = null`) get `BarnId`+`TjenesteId` set; `HendelsesRegistrert` published for each; idempotent; 30-day alert for stale unlinked events.

**Independent Test**: Send `TjenesteOpprettet` with matching BirkTiltakPK â†’ all matching Hendelse rows have BarnId set, HendelsesRegistrert envelope per linked event. Send same message twice â†’ no duplicate side effects (Wolverine inbox dedup). Check Hendelse rows >30 days old with BarnId=null trigger operator alert.

### Tests for User Story 4 âš ï¸ Write and confirm FAIL before implementing T027â€“T030

- [X] T046 [P] [US4] Integration tests for `TjenesteOpprettetHandler` in `tests/M2LB.Hendelse.Integration/BarnKoblingIntegrationTests.cs` covering TEST-I-07â€“I-09: BarnId set on matching rows + HendelsesRegistrert in outbox; no error when no matching rows; three pending events all linked by one message

### Implementation for User Story 4

- [X] T027 [P] [US4] Define `TjenesteOpprettetMessage` class in `src/M2LB.Hendelse.Infrastructure/Messaging/TjenesteOpprettetMessage.cs` with fields: `BirkTiltakPK`, `BarnId`, `TjenesteId` per events.md consumed events contract; configure Wolverine subscription to `tjeneste.tjenester` topic in `WolverineSetup.cs`
- [X] T028 [US4] Implement `BarnKoblingTjeneste` in `src/M2LB.Hendelse.Domain/Services/BarnKoblingTjeneste.cs`: find all `Hendelse` rows with matching `BirkTiltakPK` and `BarnId = null`; set `BarnId` and `TjenesteId` on each; enforce one-time lock (application-layer guard prevents re-setting BarnId once non-null per data-model.md invariant); return list of linked `HendelsesId` values
- [X] T029 [US4] Implement `TjenesteOpprettetHandler` in `src/M2LB.Hendelse.Infrastructure/Messaging/TjenesteOpprettetHandler.cs`: Wolverine `Handle(TjenesteOpprettetMessage msg)` method; call `BarnKoblingTjeneste`; publish `HendelsesRegistrertMessage` for each linked event via `IMessageContext.PublishAsync`; entire operation in one Wolverine-managed EF Core transaction (research.md Â§10)
- [X] T030 [US4] Implement `UkobletHendelseAlertScheduler` in `src/M2LB.Hendelse.Infrastructure/Messaging/UkobletHendelseAlertScheduler.cs`: Wolverine scheduled message handler; query `Hendelse` rows with `BarnId = null` and `OpprettetTidspunkt < UTC now âˆ’ 30 days`; publish `UkobletHendelseAlert` message (fields: `HendelsesId`, `BirkTiltakPK`, `OpprettetTidspunkt`, `DagerUkoblet`) to `operatorkontroll.varsler` queue via Wolverine outbox per `contracts/events.md`; register schedule in `WolverineSetup.cs`; also add `operatorkontroll.varsler` queue destination to `WolverineSetup.cs`

**Checkpoint**: US4 fully functional â€” async linking works end-to-end; idempotency via Wolverine inbox; 30-day alert scheduled.

---

## Phase 7: User Story 5 â€” Referansedata og helseendepunkter (Priority: P5)

**User Story**: Som Hendelsesadapteren skal jeg hente gjeldende referansedata ved oppstart og bruke dem til Ã¥ mappe BiRK-verdier â€” og infrastrukturen skal ha helsesjekk-endepunkter og registrere operasjoner ved oppstart.
*(FR-07, FR-09, PS-06 from spec.md)*

**Goal**: Reference data endpoints return all types with BirkVerdi mappings; health endpoints return correct status; all 5 operations registered at startup â€” service refuses to start if registration fails.

**Independent Test**: GET /referansedata/hjemmeltyper returns list with `birkVerdi` field. GET /helse/live â†’ 200. GET /helse/ready â†’ 200 when DB up, 503 when DB down. Removing Service Bus access at startup causes process to refuse start.

### Tests for User Story 5 âš ï¸ Write and confirm FAIL before implementing T031â€“T034

- [X] T047 [P] [US5] Integration tests for reference data and health in `tests/M2LB.Hendelse.Integration/ReferansedataOgHelseTests.cs` covering TEST-I-17â€“I-21: hjemmeltyper returns seeded values with BirkVerdi; rommingkategorier returns exactly three entries; /helse/live â†’ 200 with `status: Frisk`; /helse/ready â†’ 200 all deps up; /helse/ready â†’ degraded response when a dependency is down

### Implementation for User Story 5

- [X] T031 [P] [US5] Implement `ReferansedataController` in `src/M2LB.Hendelse.Api/Controllers/ReferansedataController.cs` with route prefix `api/hendelser/v1`; GET `/referansedata/hjemmeltyper` (returns all, including historical when `GjelderTil` is not null per FR-07); GET `/referansedata/rommingkategorier`; GET `/referansedata/tvangsprotokollstatuser`; no auth required (adapter uses Managed Identity validated at gateway level)
- [X] T032 [P] [US5] Implement `HelseController` in `src/M2LB.Hendelse.Api/Controllers/HelseController.cs` with route prefix `api/hendelser/v1`; GET `/helse/live` returns `200 OK` always with body `{ "status": "Frisk" }`; GET `/helse/ready` returns `200 OK` in all cases with body `{ "status": "Frisk"|"Degradert", "database": "Tilgjengelig"|"Utilgjengelig", "backgroundService": "Aktiv"|"Inaktiv" }` â€” `Degradert` when any dependency is unavailable, never `503` (per normative test spec TEST-I-19â€“I-21)
- [X] T033 [US5] Implement `OperasjonRegistreringClient` in `src/M2LB.Hendelse.Infrastructure/Messaging/OperasjonRegistreringClient.cs` that publishes all 5 operations (`Hendelse:HentHendelserForBarn`, `Hendelse:HentHendelse`, `Hendelse:SeInvolverte`, `Hendelse:SeInngrepDetalj`, `Hendelse:SeRommingsDetalj`) to Service Bus at startup
- [X] T034 [US5] Implement `OperasjonRegistrering` in `src/M2LB.Hendelse.Api/Startup/OperasjonRegistrering.cs` as `IHostedService`; on `StartAsync` call `OperasjonRegistreringClient`; throw (and thereby fail startup) if registration does not succeed per GL-09; register in `Program.cs` via `AddHostedService<OperasjonRegistrering>()`

**Checkpoint**: US5 fully functional â€” reference data served; health checks pass; startup refuses on registration failure.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Validate completeness, confirm invariants, add remaining reference queries.

- [X] T035 [P] Add GraphQL reference queries to `src/M2LB.Hendelse.Api/GraphQL/HendelseQuery.cs`: `hentHendelsesTyper` (all active types, valid login only), `hentHjemmelTyper(kunGjeldende: Boolean)` (filter by `GjelderTil = null` when `kunGjeldende = true`), and `helse` (health status field, no auth required) per graphql-read.md
- [X] T036 Verify all domain invariants are explicitly enforced in code: (1) no `DbSet<HendelsesVersjon>.Remove()` calls anywhere â€” confirm via grep; (2) BarnId lock in `BarnKoblingTjeneste.cs` prevents re-set; (3) `BirkHendelsesId` unique index defined in `HendelseDbContext.cs`; (4) `HendelsesTypeId` never mutated after insert
- [X] T037 [P] Confirm all controller route prefixes produce paths matching contracts: `InnmatingController` â†’ `/api/hendelser/v1/innmating/*`; `ReferansedataController` â†’ `/api/hendelser/v1/referansedata/*`; `HelseController` â†’ `/api/hendelser/v1/helse/*`; correct base URL in `appsettings.json`
- [X] T038 [P] Create `.pipeline/` CI/CD pipeline definition that runs `dotnet restore`, `dotnet build --no-restore`, `dotnet test tests/M2LB.Hendelse.Unit`, and `dotnet test tests/M2LB.Hendelse.Integration` per quickstart.md run commands
- [X] T048 [P] End-to-end integration tests in `tests/M2LB.Hendelse.Integration/EndToEndTests.cs` covering TEST-E-01â€“E-03: full Inngrep intake â†’ GraphQL visible; intake with BarnId=null â†’ not visible â†’ TjenesteOpprettet â†’ visible; leselogg event published to `revisjon.leselogg` queue after `hentHendelserForBarn`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies â€” start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 â€” **BLOCKS all user stories**
- **US3/Phase 3 (P1)**: Depends on Phase 2 â€” implement first; data must exist before it can be read
- **US1/Phase 4 (P2)**: Depends on Phase 2 + US3 (data to query)
- **US2/Phase 5 (P3)**: Depends on Phase 2 + US1 (extends HendelseQuery, HendelsesLeseTjeneste)
- **US4/Phase 6 (P4)**: Depends on Phase 2 + US3 (links events created by intake)
- **US5/Phase 7 (P5)**: Depends on Phase 2 â€” can run in parallel with US3/US4/US1/US2
- **Polish (Phase 8)**: Depends on all prior phases

### User Story Dependencies

- **US3 (P1 â€” Intake)**: Can start after Phase 2 â€” no dependency on other user stories
- **US1 (P2 â€” Timeline)**: Can start after Phase 2 â€” may start in parallel with US3 if developer capacity allows, but needs US3 data for end-to-end testing
- **US2 (P3 â€” Detail)**: Can start after Phase 2 â€” extends US1 GraphQL types and service, but independently testable with seeded data
- **US4 (P4 â€” Async linking)**: Can start after Phase 2 â€” fully independent of US1/US2
- **US5 (P5 â€” Ref data/health)**: Can start after Phase 2 â€” fully independent of US1â€“US4

### Within Each User Story

- Repository implementation before domain service
- Domain service before controller/resolver
- All tasks within a story can be implemented sequentially by one developer

---

## Parallel Execution Examples

### Phase 2: Foundational â€” parallel start

```
Developer A: T004 (entities) â†’ T007 (DbContext) â†’ T008 (migrations)
Developer B: T005 (ref entities) + T006 (IRepository) [P] â†’ T009 (AutorisasjonClient) [P]
Developer C: T010 (WolverineSetup) â†’ T011 (middleware) [P] â†’ T012 (Program.cs)
```

### Phase 3: US3 Intake â€” parallel tasks

```
Parallel: T014 (DTOs) [P]
Sequential: T013 (Repository) â†’ T015 (InnmatingTjeneste) â†’ T016 (Controller) â†’ T017 (Message type)
```

### Phase 4: US1 Timeline â€” parallel tasks

```
Parallel: T019 (LeseTjeneste) [P] + T020 (GraphQL types) [P]
Sequential: T018 (Repository method) â†’ (T019 + T020) â†’ T021 (HendelseQuery) â†’ T022 (Program.cs wiring)
```

### Phase 5: US2 Detail â€” parallel tasks

```
Parallel: T024 (LeseTjeneste extension) [P] + T025 (GraphQL detail types) [P]
Sequential: T023 (Repository method) â†’ (T024 + T025) â†’ T026 (hentHendelse resolver)
```

---

## Implementation Strategy

### MVP Scope (US3 + US1 only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (**blocks everything**)
3. Complete Phase 3 (US3): Intake â€” data can now be written to the system
4. Complete Phase 4 (US1): Timeline read â€” saksbehandler can see events
5. **STOP and VALIDATE**: PUT an event via REST, query it via GraphQL
6. Deploy/demo if ready

### Incremental Delivery

1. Setup + Foundational â†’ foundation ready
2. US3 (intake) â†’ BiRK adapter can deliver data â†’ **deploy**
3. US1 (timeline) â†’ saksbehandlere can view event list â†’ **demo**
4. US2 (detail + auth) â†’ full detail view with field-level security â†’ **deploy**
5. US4 (async linking) â†’ unlinked events resolved automatically
6. US5 (ref data + health) â†’ operational readiness complete

### Parallel Team Strategy (3 developers)

After Phase 2 is complete:
- Developer A: US3 (intake)
- Developer B: US1 (timeline) â€” seed data from US3 for testing
- Developer C: US5 (ref data + health) â€” fully independent

After US1:
- Developer B: US2 (detail)
- Developer A: US4 (async linking)

---

## Notes

- `[P]` tasks operate on different files with no shared state dependencies â€” safe to parallelize
- User story labels `[US1]`â€“`[US5]` map to spec.md scenarios: US1=Sc1, US2=Sc2/3/6, US3=Sc4, US4=Sc5, US5=FR-07/FR-09
- No hard deletes â€” `HendelsesRepository` must never call `Remove()` on `HendelsesVersjon`; verify with grep in Phase 8
- All secrets from Azure Key Vault at runtime (GL-26) â€” `appsettings.json` contains only key names, not values
- Wolverine sender daemon delivers outbox envelopes asynchronously â€” no polling loop needed in application code
- Wolverine inbox dedup (`wolverine_incoming_envelopes`) covers `TjenesteOpprettet` idempotency automatically (GL-22)
- BarnId: `null â†’ UUID` transition exactly once; application-layer guard in `BarnKoblingTjeneste` enforces this
- `HendelsesRegistrert` is only published when `BarnId` is set â€” never with `BarnId = null` per events.md contract
