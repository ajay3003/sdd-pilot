# Tasks: SCIM User Synchronization Adapter

**Input**: Design documents from `/specs/004-scim-user-sync/`
**Prerequisites**: plan.md ✅, spec.md ✅, data-model.md ✅, contracts/scim-http-api.md ✅, research.md ✅, quickstart.md ✅

**Tests**: Included — required by constitution gate PP-09 (Spec + Tests).

**Organization**: Tasks are grouped by user story for independent implementation and testing.

## Format: `[ID] [P?] [Story?] Description`

- **[P]**: Can run in parallel with other [P] tasks in the same phase (different files, no blocking dependencies)
- **[Story]**: Which user story this task belongs to (US1–US4)

---

## Phase 1: Setup (Project Scaffolding)

**Purpose**: Create the two new projects and add them to the solution. No implementation yet.

- [X] T001 Create `src/Autorisasjon.ScimAdapter/Autorisasjon.ScimAdapter.csproj` per plan.md Step 2 (target net10.0, add ProjectReference to Infrastructure, PackageReferences: Azure.Extensions.AspNetCore.Configuration.Secrets 1.x, Azure.Identity 1.x, Microsoft.EntityFrameworkCore.Design 10.x Private, Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore 10.x, Polly 8.x) and add to `Autorisasjon.sln`
- [X] T002 Create `tests/Autorisasjon.ScimAdapter.IntegrationTests/Autorisasjon.ScimAdapter.IntegrationTests.csproj` (target net10.0, ProjectReference to ScimAdapter, PackageReferences: xunit 2.9.3, Shouldly 4.3.0, NSubstitute 5.x, Testcontainers.MsSql 4.x, Microsoft.AspNetCore.Mvc.Testing) and add to `Autorisasjon.sln`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: All shared infrastructure that MUST be complete before ANY user story can run.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Infrastructure changes (Autorisasjon.Infrastructure)

- [X] T003 Create `src/Autorisasjon.Infrastructure/Persistence/Entities/KjentBruker.cs` — plain C# class with properties: `Guid EntraObjectId`, `string UserName`, `string? ExternalId`, `bool IsActive`, `DateTimeOffset LastUpdated` (no private setters, no factory method — see plan.md Step 1)
- [X] T004 [P] Create `src/Autorisasjon.Infrastructure/Persistence/Configurations/KjentBrukerConfiguration.cs` — EF `IEntityTypeConfiguration<KjentBruker>`: `ToTable("KjentBrukere")`, PK on `EntraObjectId` with `ValueGeneratedNever()`, `UserName` max 256 required, `ExternalId` max 256 optional, `IsActive` default true, two indexes: `IX_KjentBrukere_UserName` and `IX_KjentBrukere_ExternalId` (see data-model.md EF Configuration section)
- [X] T005 Update `src/Autorisasjon.Infrastructure/Persistence/AutorisasjonsDbContext.cs` — add `public DbSet<KjentBruker> KjentBrukere => Set<KjentBruker>();` and wire `KjentBrukerConfiguration` in `OnModelCreating`
- [X] T006 [P] Update `src/Autorisasjon.Infrastructure/ServiceBus/EventPublisher.cs` — add `public const string EntraBrukere = "entra.brukere";` inside the existing `Topics` static class; **also** create `src/Autorisasjon.Infrastructure/ServiceBus/IEventPublisher.cs` — interface with `Task PublishAsync<T>(string topic, T evt, string eventType, CancellationToken ct = default) where T : class`; make `EventPublisher` implement `IEventPublisher` — required so `ScimUserService` depends on the interface and `FakeEventPublisher` can substitute in test DI without subclassing the real `ServiceBusClient`-dependent class (H3 fix)
- [X] T007 Run EF Core migration from repo root: `dotnet ef migrations add AddKjentBruker --project src/Autorisasjon.Infrastructure --startup-project src/Autorisasjon.Api` — verify migration file created under `src/Autorisasjon.Infrastructure/Migrations/`

### ScimAdapter project skeleton

- [X] T008 [P] Create `src/Autorisasjon.ScimAdapter/Configuration/ScimOptions.cs` — `public class ScimOptions` with `string ProvisioningSecret { get; set; }` and `int PageSize { get; set; } = 20`; bound from `"Scim"` config section
- [X] T009 [P] Create `src/Autorisasjon.ScimAdapter/Services/ScimAdapterUserContext.cs` — implements `IUserContext`; returns `Guid.Parse("00000000-0000-0000-0000-000000000001")` for UserId and `null` for CorrelationId (see research.md IUserContext section)
- [X] T010 [P] Create SCIM request/response models in `src/Autorisasjon.ScimAdapter/Models/Scim/`: `ScimUser.cs` (record with `Id?`, `ExternalId?`, `UserName?`, `Active`, `[JsonPropertyName]` attributes for SCIM naming), `ScimListResponse.cs` (generic record per data-model.md), `ScimPatchRequest.cs` + `ScimPatchOperation.cs` (record with `Op`, `Path?`, `Value` as `JsonElement`), `ScimError.cs` (record with `Detail`, `Status`) — JSON shapes per contracts/scim-http-api.md
- [X] T011 [P] Create event models in `src/Autorisasjon.ScimAdapter/Models/Events/`: `BrukerAktivertEvent.cs` and `BrukerDeaktivertEvent.cs` — both are records with `string HendelsesId`, `string HendelsesType`, `string EntraObjectId`, `DateTimeOffset Tidsstempel`, `string KildeReferanse` (see data-model.md Event Contracts + plan.md Step 4)
- [X] T012 [P] Create `src/Autorisasjon.ScimAdapter/Authentication/ScimBearerAuthHandler.cs` — `AuthenticationHandler<AuthenticationSchemeOptions>`: reads `Authorization: Bearer <token>` header, compares with `IConfiguration["Scim:ProvisioningSecret"]` via `CryptographicOperations.FixedTimeEquals`, returns Success (role `ScimProvisioner`) / NoResult / Fail; secret MUST NOT be logged (FR-018) — see plan.md Step 3
- [X] T013 [P] Create `src/Autorisasjon.ScimAdapter/appsettings.json` with full config schema per data-model.md Configuration Schema section (ConnectionStrings, AzureServiceBus.HendelsesTopics.EntraBrukere, Scim.ProvisioningSecret empty, Scim.PageSize 20, KeyVault.Uri, Logging.LogLevel.Default Warning)
- [X] T014 Create `src/Autorisasjon.ScimAdapter/Program.cs` — DI registration per plan.md Step 7 **excluding observability** (deferred to Phase 6): Key Vault (non-dev), logging (SimpleConsole dev / JsonConsole prod), Authentication (`ScimBearer` scheme), Authorization policy `ScimProvisioner`, IUserContext scoped, EF Core with AuditInterceptor, `IEventPublisher` → `EventPublisher` scoped, Polly ResiliencePipeline `"scim-servicebus"` (3 retries, 500ms exponential), ScimUserService scoped, `AddHealthChecks().AddDbContextCheck<AutorisasjonsDbContext>("database")` (SQL only; `// TODO: T033a adds servicebus check`), `// TODO: T034 wires ScimMetrics + OpenTelemetry`; after `Build()`: fail-fast for `Scim:ProvisioningSecret` (LogCritical + return 1 — FR-022) (H1 fix: ScimMetrics/OTel/SB health deferred so Phase 2 compiles without Phase 6 files)

### Integration test infrastructure

- [X] T015 [P] Create `tests/Autorisasjon.ScimAdapter.IntegrationTests/Infrastructure/FakeEventPublisher.cs` — implements `IEventPublisher`; captures published events in a `ConcurrentBag<(string topic, string eventType, string json)>` for assertion; exposes `Clear()` and `CapturedEvents` for test assertions; **does not inherit `EventPublisher`** — uses interface from T006 (H3 fix)
- [X] T016 [P] Create `tests/Autorisasjon.ScimAdapter.IntegrationTests/Fixtures/DatabaseFixture.cs` — Testcontainers `MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")`, applies EF migrations on startup, implements `IAsyncLifetime`
- [X] T017 Create `tests/Autorisasjon.ScimAdapter.IntegrationTests/Infrastructure/ScimAdapterWebAppFactory.cs` — `WebApplicationFactory<Program>`: overrides DB connection string to Testcontainer, replaces `IEventPublisher` registration with `FakeEventPublisher` (`services.AddScoped<IEventPublisher>(_ => sharedFake)`), sets `Scim:ProvisioningSecret` to a test value, disables Azure Service Bus (`AzureServiceBus:Disabled=true`); exposes `FakeEventPublisher` property for test assertions (H3 fix)

**Checkpoint**: Foundation complete — solution builds, EF migration exists, ScimAdapter project skeleton in place.

---

## Phase 3: User Story 1 — User Activated (Priority: P1) 🎯 MVP

**Goal**: Entra's provisioning engine sends `POST /Users` or `PATCH /Users/{id}` with `active: true`; adapter creates/updates `KjentBruker` and publishes `BrukerAktivert` to `entra.brukere` before returning HTTP 2xx.

**Independent Test**: `POST /scim/v2/Users` with a new user ID → assert HTTP 201, `KjentBruker` row in DB with `IsActive=true`, one `BrukerAktivert` event captured by `FakeEventPublisher`.

### Tests for User Story 1 (write first — must fail before implementation)

- [X] T018 [P] [US1] Write unit test cases for activation state transitions in `tests/Autorisasjon.UnitTests/ScimAdapter/ScimUserServiceTests.cs`: "Not found + POST active=true → BrukerAktivert published", "Inactive + POST active=true → BrukerAktivert published", "Active + POST active=true → no-op no event", "Not found + PATCH active=true → BrukerAktivert published", "Inactive + PATCH active=true → BrukerAktivert published", "Active + PATCH active=true → no-op"; mock `AutorisasjonsDbContext` queryable and `EventPublisher` with NSubstitute
- [X] T019 [P] [US1] Write unit tests in `tests/Autorisasjon.UnitTests/ScimAdapter/ScimPatchRequestParserTests.cs`: parse `op=Replace path=active value=true` → `true`, `value=false` → `false`, unknown op → ignored, malformed JSON element → gracefully handled

### Implementation for User Story 1

- [X] T020 [US1] Implement `ScimUserService.cs` in `src/Autorisasjon.ScimAdapter/Services/ScimUserService.cs` — `CreateOrActivateAsync` method: load or create `KjentBruker`, apply idempotency check per state-transition table (data-model.md), publish `BrukerAktivert` with Polly pipeline if state changes, persist via DB transaction (publish-then-commit pattern from plan.md Step 5); inject `AutorisasjonsDbContext`, `IEventPublisher`, `ResiliencePipeline`, `ILogger<ScimUserService>` (H3 fix: depends on interface, not concrete class)
- [X] T021 [US1] Add `PatchAsync` method to `ScimUserService.cs` — parse `ScimPatchRequest.Operations` for `op=Replace` + `path=active` (case-insensitive), extract bool value from `JsonElement`, apply same idempotency + publish + commit pattern; all non-`active` operations logged and ignored (RFC 7644 §3.5.2)
- [X] T022 [US1] Create `src/Autorisasjon.ScimAdapter/Endpoints/UsersEndpoints.cs` — `MapGroup("/scim/v2").RequireAuthorization("ScimProvisioner")` with `POST /Users` (calls `CreateOrActivateAsync`, returns 201 on new / 200 on existing) and `PATCH /Users/{id}` (calls `PatchAsync`, returns 200 with current user state); SCIM response shapes per contracts/scim-http-api.md; wire into `Program.cs`
- [X] T023 [US1] Write integration tests in `tests/Autorisasjon.ScimAdapter.IntegrationTests/ScimUsersEndpointTests.cs`: POST new user active=true → 201 + BrukerAktivert captured; POST existing inactive user active=true → 200 + BrukerAktivert captured; POST same payload **5 times** → `FakeEventPublisher.CapturedEvents.Count == 1` (SC-003 — exactly one event regardless of retry count); request with wrong Bearer token → 401 + no event; PATCH active=true on inactive user → 200 + BrukerAktivert (M3 fix: SC-003 requires 5× verification)

**Checkpoint**: User Story 1 fully functional and tested independently — activation path end-to-end works.

---

## Phase 4: User Story 2 — User Deactivated (Priority: P1)

**Goal**: Entra sends `PATCH /Users/{id}` with `active: false` or `DELETE /Users/{id}`; adapter updates `KjentBruker` and publishes `BrukerDeaktivert`. Repeated delivery of same request is a no-op (idempotent).

**Independent Test**: `DELETE /scim/v2/Users/{id}` for an active user → assert HTTP 204, `KjentBruker.IsActive=false` in DB, one `BrukerDeaktivert` captured; repeat same DELETE → HTTP 204, no second event.

### Tests for User Story 2 (write first — must fail before implementation)

- [X] T024 [P] [US2] Add deactivation test cases to `tests/Autorisasjon.UnitTests/ScimAdapter/ScimUserServiceTests.cs`: "Active + PATCH active=false → BrukerDeaktivert", "Inactive + PATCH active=false → no-op", "Not found + PATCH active=false → create+BrukerDeaktivert", "Active + DELETE → BrukerDeaktivert", "Inactive + DELETE → no-op", "Not found + DELETE → create+BrukerDeaktivert"

### Implementation for User Story 2

- [X] T025 [US2] Add `DeactivateAsync` method to `src/Autorisasjon.ScimAdapter/Services/ScimUserService.cs` — handles `DELETE /Users/{id}`: load or create `KjentBruker`, suppress if already inactive, publish `BrukerDeaktivert` + commit if state changes; `KildeReferanse` format: `SCIM-DELETE /scim/v2/Users/{id}` (data-model.md Source Reference table)
- [X] T026 [US2] Add `DELETE /Users/{id}` endpoint to `src/Autorisasjon.ScimAdapter/Endpoints/UsersEndpoints.cs` — calls `DeactivateAsync`, returns 204 on success (including already-inactive idempotent case), 500 on SB/DB failure using `ScimError` shape
- [X] T027 [US2] Add deactivation integration tests to `tests/Autorisasjon.ScimAdapter.IntegrationTests/ScimUsersEndpointTests.cs`: PATCH active=false on active user → 200 + BrukerDeaktivert; DELETE active user → 204 + BrukerDeaktivert; DELETE already-inactive user → 204 + no event (idempotent); PATCH active=false twice → second is 200 + no duplicate event

**Checkpoint**: User Stories 1 and 2 both independently functional — activation and deactivation paths complete.

---

## Phase 5: User Story 3 — Full Synchronization (Priority: P2)

**Goal**: Entra's reconciliation cycle sends paginated `GET /Users` requests; adapter returns all known users (active + inactive) in SCIM list format with pagination and equality filter support.

**Independent Test**: Seed 25 `KjentBruker` rows (mix active/inactive); `GET /scim/v2/Users?startIndex=1&count=10` → assert 200, `totalResults=25`, `Resources.Count=10`, all rows included regardless of `IsActive`.

### Tests for User Story 3 (write first — must fail before implementation)

- [X] T028 [P] [US3] Add GET integration tests to `tests/Autorisasjon.ScimAdapter.IntegrationTests/ScimUsersEndpointTests.cs` (write first): GET /Users empty → 200 empty list; GET /Users with seeded data paginated → correct page + totalResults; GET /Users?filter=userName eq "..." → filtered result; GET /Users/{id} found → 200 user resource; GET /Users/{id} not found → 404 ScimError

### Implementation for User Story 3

- [X] T029 [US3] Add `ListAsync` method to `src/Autorisasjon.ScimAdapter/Services/ScimUserService.cs` — accepts `int startIndex`, `int count`, `string? filter`; implements simple filter parser (regex `^(\w+) eq "(.*)"$` per research.md matching `userName` or `externalId` on indexed columns); returns `ScimListResponse<ScimUser>` with `TotalResults` from count query and paginated `Resources`; max page size 200
- [X] T030 [US3] Add `GetByIdAsync` method to `src/Autorisasjon.ScimAdapter/Services/ScimUserService.cs` — looks up `KjentBrukere` by `EntraObjectId`; returns `ScimUser?` (null if not found)
- [X] T031 [US3] Add `GET /Users` and `GET /Users/{id}` endpoints to `src/Autorisasjon.ScimAdapter/Endpoints/UsersEndpoints.cs` — GET /Users: reads `startIndex`, `count`, `filter` query params, calls `ListAsync`, returns 200 with `ScimListResponse`; GET /Users/{id}: calls `GetByIdAsync`, returns 200 or 404 `ScimError`

**Checkpoint**: User Stories 1–3 all independently functional — full SCIM CRUD surface complete.

---

## Phase 6: User Story 4 — Operations Monitoring (Priority: P3)

**Goal**: Operations engineers can verify adapter health, observe structured logs per request, and see SCIM metrics. Health endpoint reports SQL Server + Service Bus + SCIM reachability.

**Independent Test**: `GET /health` → 200 with JSON body showing `database` and `servicebus` check statuses; verify structured log entry is produced for a POST /Users request.

### Tests for User Story 4

- [X] T032 [P] [US4] Add health + observability tests to `tests/Autorisasjon.ScimAdapter.IntegrationTests/ScimUsersEndpointTests.cs`: GET /health → 200 with `status: Healthy` and named checks for `database`; verify health endpoint is anonymous (no Bearer token required)

### Implementation for User Story 4

- [X] T033 [P] [US4] Create `src/Autorisasjon.ScimAdapter/Telemetry/ScimMetrics.cs` — `System.Diagnostics.Metrics` based: meter name `"Autorisasjon.ScimAdapter"`, counters for `scim.requests` (tagged by operation type), `scim.events.published` (tagged by event type), `scim.publish.failures`; registered as singleton in Program.cs T034
- [X] T033a [P] [US4] Create `src/Autorisasjon.ScimAdapter/Health/ServiceBusHealthCheck.cs` — implements `IHealthCheck`; reads `ConnectionStrings:ServiceBus` from `IConfiguration`; if `AzureServiceBus:Disabled == true` (dev mode) return `HealthCheckResult.Healthy("disabled")` immediately; otherwise instantiate `ServiceBusAdministrationClient` with `DefaultAzureCredential` and call `GetNamespacePropertiesAsync(cancellationToken)`; return `Healthy` on success, `Unhealthy(ex.Message)` on exception (H2 fix — fulfils FR-021 `servicebus` health check named in contracts/scim-http-api.md)
- [X] T034 [US4] Complete observability wiring in `src/Autorisasjon.ScimAdapter/Program.cs` (modifies file created in T014): register `services.AddSingleton<ScimMetrics>()`; wire `AddOpenTelemetry().WithMetrics(m => m.AddMeter("Autorisasjon.ScimAdapter"))`; register `AddCheck<ServiceBusHealthCheck>("servicebus")` alongside existing `AddDbContextCheck`; add per-request structured logging scope (CorrelationId middleware matching main Api pattern); replaces the `// TODO` placeholders left by T014 (H1 + H2 fix)
- [X] T035 [US4] Add `ScimMetrics` increments to `src/Autorisasjon.ScimAdapter/Services/ScimUserService.cs` — call `ScimMetrics` counters on each operation entry and each successful event publish; increment failure counter when Polly exhausts retries

**Checkpoint**: All 4 user stories complete. Adapter is fully observable.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T036 [P] Verify quickstart.md instructions work: `dotnet user-secrets set` commands execute without error, `dotnet run --project src/Autorisasjon.ScimAdapter` starts with a valid DB connection string, `/health` returns 200
- [X] T037 Run full test suite `dotnet test` and verify all tests pass: `Autorisasjon.UnitTests`, `Autorisasjon.IntegrationTests`, `Autorisasjon.ContractTests`, `Autorisasjon.ScimAdapter.IntegrationTests`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — start immediately
- **Phase 2 (Foundational)**: Depends on Phase 1 — **BLOCKS all user stories**
- **Phase 3 (US1)** and **Phase 4 (US2)**: Both P1 — depend on Phase 2 completion; can be worked sequentially (US1 first, then extend service for US2) or by separate developers
- **Phase 5 (US3)**: Depends on Phase 2; independent of US1/US2 implementation but logically after (service skeleton exists)
- **Phase 6 (US4)**: Depends on Phase 2 + ScimMetrics hook points in the service; add last
- **Phase 7 (Polish)**: All phases complete

### Within Phase 2 (Foundational)

```
T003 (KjentBruker entity)
  ├── T004 [P] (EF configuration) ─┐
  └── T005    (DbContext update)   ├── T007 (migration)
T006 [P] (EventPublisher const)    │
T008–T013 [P] (ScimAdapter files) ─┘ (parallel with above)
T014 (Program.cs) ← after T008, T009, T012
T015–T016 [P] (test doubles) ← parallel
T017 (WebAppFactory) ← after T014, T015, T016
```

### Within Each User Story

```
Tests (write first → must FAIL) → Service method(s) → Endpoints → Integration tests
```

### User Story Internal Dependencies

- **US1**: T018/T019 [P] → T020 → T021 → T022 → T023
- **US2**: T024 [P] → T025 → T026 → T027
- **US3**: T028 [P] → T029 → T030 → T031
- **US4**: T032/T033/T033a [P] → T034 → T035

---

## Parallel Execution Examples

### Phase 2 parallel batch (once T003 is done)

```
Agent A: T004 (KjentBrukerConfiguration) + T005 (DbContext) → T007 (migration)
Agent B: T008 (ScimOptions) + T009 (UserContext) + T010 (SCIM models) + T011 (Events) + T012 (AuthHandler) + T013 (appsettings)
Agent C: T015 (FakeEventPublisher) + T016 (DatabaseFixture)
```

### User Story 1 parallel batch

```
Agent A: T018 + T019 (unit tests, write failing first)
Agent B: T020 + T021 + T022 (service + endpoints)
→ merge → T023 (integration tests)
```

---

## Implementation Strategy

### MVP (User Stories 1 + 2 Only)

1. Phase 1: Setup (T001–T002)
2. Phase 2: Foundational (T003–T017) — **critical gate**
3. Phase 3: US1 Activation (T018–T023)
4. Phase 4: US2 Deactivation (T024–T027)
5. **STOP and VALIDATE**: Run `dotnet test tests/Autorisasjon.ScimAdapter.IntegrationTests`
6. Deploy MVP — Entra provisioning can activate and deactivate users

### Incremental Delivery

1. MVP above → P1 live
2. Phase 5 (US3 Full Sync) → Entra reconciliation works → reduced manual sync risk
3. Phase 6 (US4 Observability) → Ops dashboard complete
4. Phase 7 (Polish) → production-ready

---

## Summary

| Phase | Story | Priority | Tasks | Parallelizable |
|---|---|---|---|---|
| Phase 1: Setup | — | — | T001–T002 | — |
| Phase 2: Foundational | — | — | T003–T017 | T004,T006,T008–T013,T015–T016 |
| Phase 3: US1 Activation | US1 | P1 | T018–T023 | T018,T019 |
| Phase 4: US2 Deactivation | US2 | P1 | T024–T027 | T024 |
| Phase 5: US3 Full Sync | US3 | P2 | T028–T031 | T028 |
| Phase 6: US4 Observability | US4 | P3 | T032–T035 + T033a | T032,T033,T033a |
| Phase 7: Polish | — | — | T036–T037 | T036 |

**Total**: 39 tasks across 7 phases (37 original + T006 interface extraction + T033a health check)  
**MVP scope**: Phases 1–4 (27 tasks) — full activation + deactivation with idempotency  
**Suggested start**: T001 immediately; T003 next (unblocks all Phase 2 infrastructure work)
