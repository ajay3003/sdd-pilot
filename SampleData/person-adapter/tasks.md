# Tasks: BiRK Person-adapter

**Input**: Design documents from `specs/001-birk-person-adapter/`
**Prerequisites**: plan.md ✓, spec.md ✓, research.md ✓, data-model.md ✓, contracts/ ✓, quickstart.md ✓

**Tests**: Included — xUnit + Testcontainers.MsSql + NSubstitute defined as primary dependencies in plan.md; testing strategy in research.md §9.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story?] Description with file path`

- **[P]**: Can run in parallel (different files, no incomplete-task dependencies)
- **[Story]**: User story this task belongs to ([US1]–[US5])
- Exact file paths included in all descriptions

## Path Conventions

- `src/M2LB.PersonBiRKAdapter.Worker/` — hosted service, health + admin endpoints, DI wiring
- `src/M2LB.PersonBiRKAdapter.Domain/` — transformation logic, security guard, routing, full load
- `src/M2LB.PersonBiRKAdapter.Infrastructure/` — EventProcessorClient, HTTP client, EF Core, metrics
- `tests/M2LB.PersonBiRKAdapter.Unit/` — transformation, Kode 6/7, idempotency, fault queue logic
- `tests/M2LB.PersonBiRKAdapter.Integration/` — end-to-end processing tests (Testcontainers SQL)

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: .NET 10 solution scaffold with all projects, packages, and base configuration.

- [X] T001 Create `PersonBiRKAdapter.sln` and all five csproj stubs: `src/M2LB.PersonBiRKAdapter.Worker`, `src/M2LB.PersonBiRKAdapter.Domain`, `src/M2LB.PersonBiRKAdapter.Infrastructure`, `tests/M2LB.PersonBiRKAdapter.Unit`, `tests/M2LB.PersonBiRKAdapter.Integration` — Worker is `Microsoft.NET.Sdk.Worker`, Domain and Infrastructure are `Microsoft.NET.Sdk`, test projects are `Microsoft.NET.Sdk` with `IsPackable=false`
- [X] T002 Add NuGet package references per plan.md: `Azure.Messaging.EventHubs.Processor`, `Azure.Identity`, `Azure.Storage.Blobs` to Infrastructure; `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.Extensions.Http.Resilience`, `Microsoft.Extensions.Diagnostics.HealthChecks` to Infrastructure; OpenTelemetry + `Azure.Monitor.OpenTelemetry.Exporter` to Infrastructure; `System.Diagnostics.DiagnosticSource` to Infrastructure; xUnit + `Testcontainers.MsSql` + NSubstitute to test projects; Infrastructure + Domain referenced by Worker and test projects
- [X] T003 [P] Create `src/M2LB.PersonBiRKAdapter.Worker/appsettings.json` with placeholder config sections: `EventHubs` (FullyQualifiedNamespace, EventHubName, ConsumerGroup, CheckpointContainerUrl), `PersonModule` (BaseUrl, SystemBrukerId — SystemBrukerId is the system-identity Guid used in OpprettetAv/EndretAv fields; no ApiKey needed — Managed Identity handles auth), `Database` (ConnectionString), `KeyVault` (Uri), `Resilience` (MaxRetryAttempts, BaseDelaySeconds, RateLimitCoolDownSeconds), `FaultQueue` (PollIntervalMinutes, MaxRetentionDays), `FullLoad` (ProgressLogIntervalRecords, BatchSize)
- [X] T004 [P] Add `.gitignore` entries for `appsettings.Development.json`, `appsettings.*.json` (except `.Development.json` template), `bin/`, `obj/`, `*.user`, `.pipeline/secrets/`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain interfaces, entity model, EF Core schema, and Azure SDK DI registrations that ALL user stories depend on.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [X] T005 [P] Define configuration options classes with `[Required]` validation: `EventHubsOptions`, `PersonModuleOptions` (BaseUrl, SystemBrukerId — SystemBrukerId is the system-identity Guid populated into OpprettetAv/EndretAv on every outbound record; no ApiKey field — Managed Identity handles auth per PS-02), `DatabaseOptions`, `ResilienceOptions`, `FaultQueueOptions`, `FullLoadOptions` in `src/M2LB.PersonBiRKAdapter.Worker/Configuration/`
- [X] T006 [P] Define domain mapping interfaces: `IPersonMapper` (maps `CdcEvent` → `PersonRecord`) and `IChildRegistrationMapper` (maps `CdcEvent` → `ChildRegistrationRecord`) in `src/M2LB.PersonBiRKAdapter.Domain/Mapping/`
- [X] T007 [P] Define domain event types: `CdcEvent` record (operasjon, tabellnavn, sikkerhetsnivaa, payload as `JsonElement`); `OperationType` enum (Create, Update, Delete); `PostType` enum (Person, Barn, OrganisationEntity) — `ReferensData` removed; reference data CDC events are discarded at the routing step (C1 resolution: PersonModule auto-creates reference data); `RoutingOutcome` enum (Delivered, Discarded, Rejected, FaultQueued) in `src/M2LB.PersonBiRKAdapter.Domain/Events/`
- [X] T008 [P] Define outbound DTOs matching PersonModule's real API in `src/M2LB.PersonBiRKAdapter.Domain/Models/`: **`PersonRecord`** (PersonId Guid, EksternId string?, Navn string, Foedselsnummer string?, UsikkerFoedselsnummer string?, DUFNummer string?, Foedselsdato DateOnly?, UsikkerFoedselsdato DateOnly?, KjoennTypeId Guid, OpprettetAv Guid, EndretAv Guid, Kilde string, KorrelasjonId Guid, BirkEndringstidspunkt DateTimeOffset?); **`ChildRegistrationRecord`** (BarnRegistreringId Guid, PersonId Guid, BirkId string, BarnTypeId Guid, BarnStatusTypeId Guid, SikkerhetsnivaaTypeId Guid, KommuneNr string, OpprettetAv Guid, EndretAv Guid, Kilde string, KorrelasjonId Guid, BirkEndringstidspunkt DateTimeOffset?); **`BatchIngestRequest`** (`{ "Personer": List<PersonRecord>, "Barn": List<ChildRegistrationRecord> }`); **`BatchResultat`** (`{ "behandlet": int, "feil": List<BatchFeilOppfoering> }`); note: `ReferenceDataRecord` removed — reference data CDC events are discarded; GUID type ID fields (KjoennTypeId etc.) pending Å-03
- [X] T009 Define `FaultQueueEntry` EF Core entity with all columns from data-model.md: `Id` (Guid PK), `BirkId` (nvarchar 100), `PostType` (nvarchar 50), `Feiltype` (nvarchar 50 — `FORBIGAAENDE` or `VALIDERING`), `Feilmelding` (nvarchar 500), `AntallForsok` (int), `SisteForsokTidspunkt` (datetime2 nullable), `OpprettetTidspunkt` (datetime2), `UtlopertTidspunkt` (datetime2 — set on insert to `OpprettetTidspunkt + FaultQueueOptions.MaxRetentionDays`; default 30 days; NEVER hardcoded), `Payload` (nvarchar max, nullable) in `src/M2LB.PersonBiRKAdapter.Infrastructure/Persistence/FaultQueueEntry.cs`
- [X] T010 Create `AdapterDbContext` with `DbSet<FaultQueueEntry> Feilkoe`; override `OnConfiguring` to set `SqlConnection.AccessToken` from `DefaultAzureCredential` before each connection open (Managed Identity SQL auth — no password in connection string); configure `feilkoe` table name, indexes `(feiltype, post_type)`, `utloper_tidspunkt`, `siste_forsok_tidspunkt` per data-model.md in `src/M2LB.PersonBiRKAdapter.Infrastructure/Persistence/AdapterDbContext.cs`
- [X] T011 Add EF Core migration `InitialSchema` creating `feilkoe` table with all columns and indexes; verify migration SQL matches data-model.md exactly in `src/M2LB.PersonBiRKAdapter.Infrastructure/Persistence/Migrations/`
- [X] T012 [P] Register `EventProcessorClient` + `BlobCheckpointStore` using `DefaultAzureCredential` and `EventHubsOptions`; bind options from configuration in `src/M2LB.PersonBiRKAdapter.Infrastructure/Extensions/EventHubsServiceExtensions.cs`
- [X] T013 [P] Register `PersonModuleClient` as typed `HttpClient` with `PersonModuleOptions.BaseUrl` and `DefaultAzureCredential` bearer token handler (`// DefaultAzureCredential`); bind options from configuration in `src/M2LB.PersonBiRKAdapter.Infrastructure/Extensions/HttpClientServiceExtensions.cs`
- [X] T014 Wire all Infrastructure service extensions and options into Worker host builder; apply EF Core migrations on startup; add hosted service skeleton in `src/M2LB.PersonBiRKAdapter.Worker/Program.cs`

**Checkpoint**: Foundation complete — all five user stories can now be implemented independently.

---

## Phase 3: User Story 1 — Continuous Person Data Synchronization (Priority: P1) 🎯 MVP

**Goal**: CDC create/update events for persons flow from Event Hubs → CdcRouter → PersonMapper → PersonModuleClient → checkpoint advanced.

**Independent Test**: Inject a person create CdcEvent, verify `PUT /innmating/personer/{eksternId}` is called with `eksternId` = PersonPK, verify `UpdateCheckpointAsync` is called after delivery. Inject a delete event, verify no HTTP call and no error.

- [X] T015 [P] [US1] Unit tests: `CdcRouter` discards delete operations (`OperationType.Delete`) silently — no mapper call, no HTTP call; discards organizational entity events (owner, unit, institution, employee, contact person) silently; discards reference data table events silently (same path as org entities — PersonModule auto-creates reference data); passes person create/update to person path in `tests/M2LB.PersonBiRKAdapter.Unit/Routing/CdcRouterTests.cs`
- [X] T016 [P] [US1] Unit tests: `PersonMapper.Map()` sets `eksternId` from `CdcEvent.payload.PersonPK`; `personId` is a stable non-empty Guid (same PersonPK input always produces the same Guid); `kilde` = `"BiRK"` always; `korrelasjonId` is a non-empty Guid (fresh each call); `opprettetAv`/`endretAv` equal `PersonModuleOptions.SystemBrukerId`; returns null values for absent nullable identity fields (name, NIN, DOB, DUF) without throwing in `tests/M2LB.PersonBiRKAdapter.Unit/Mapping/PersonMapperTests.cs`
- [X] T017 [US1] Implement `CdcRouter.Route()`: step 1 — check security level (stub, passes all levels ≤ 1 for now); step 2 — discard if `OperationType.Delete` (FR-022), return `RoutingOutcome.Discarded`; step 3 — check `tabellnavn` against known organizational entity table names, discard (FR-002); also discard known reference data table names silently (PersonModule auto-creates reference data — no explicit delivery needed); step 4 — route to `PostType.Person` for person table names in `src/M2LB.PersonBiRKAdapter.Domain/Routing/CdcRouter.cs`
- [X] T018 [P] [US1] Implement `PersonMapper.Map()` stub: `eksternId` from BiRK PersonPK field; `personId` = deterministic Guid from BiRK PersonPK (stable across calls — same input always produces same Guid); `navn`, `foedselsnummer`, `usikkerFoedselsnummer`, `dufNummer`, `foedselsdato`, `usikkerFoedselsdato`, `birkEndringstidspunkt` per Å-01 source field names; `kjoennTypeId` = GUID lookup stub returning `Guid.Empty` until Å-03 is resolved; `opprettetAv`/`endretAv` from `PersonModuleOptions.SystemBrukerId` config Guid; `kilde` = `"BiRK"` constant; `korrelasjonId` = `Guid.NewGuid()` per call; null-safe for all nullable fields; comment `// TODO Å-01: source field names; TODO Å-03: KjoennTypeId Guid resolution` in `src/M2LB.PersonBiRKAdapter.Domain/Mapping/PersonMapper.cs`
- [X] T019 [US1] Implement `PersonModuleClient.UpsertPersonAsync()`: `PUT /api/person/v1/innmating/personer`, JSON body from `PersonRecord`; `DefaultAzureCredential` bearer token auth; returns `DeliveryResult` (Success 204, ValidationFailure 422, RateLimited 429, TransientFailure 5xx) — PersonModule returns 204 for all successful outcomes (create/update/no-change are not distinguished by status code) in `src/M2LB.PersonBiRKAdapter.Infrastructure/Http/PersonModuleClient.cs`
- [X] T020 [US1] Add `AddResilienceHandler` on PersonModule HttpClient: exponential backoff retry for 5xx/timeout — attempt count from `ResilienceOptions.MaxRetryAttempts`, base delay from `ResilienceOptions.BaseDelaySeconds`, with jitter; separate 429 handler that pauses delivery for `ResilienceOptions.RateLimitCoolDownSeconds` without consuming retry count; 422 bypasses retry entirely in `src/M2LB.PersonBiRKAdapter.Infrastructure/Extensions/HttpClientServiceExtensions.cs`
- [X] T021 [US1] Implement `CheckpointService.AdvanceAsync()`: call `UpdateCheckpointAsync` on `ProcessEventArgs` once after PersonModule confirms delivery of the entire batch — NEVER before confirmed delivery (FR-008); include Kode 6/7 rejected and silently discarded events in the advancing batch (FR-007) in `src/M2LB.PersonBiRKAdapter.Infrastructure/EventHubs/CheckpointService.cs`
- [X] T022 [US1] Integration test: inject person CDC create event → `CdcRouter` → `PersonMapper` → `PersonModuleClient` (mocked) returns 204 → `CheckpointService.AdvanceAsync()` called once; inject delete event → no HTTP call → checkpoint still advances in `tests/M2LB.PersonBiRKAdapter.Integration/PersonProcessingTests.cs`
- [X] T023 [US1] Create `CdcProcessorWorker` as `BackgroundService`: receive `EventData` batches from `EventProcessorClient`, deserialize to `CdcEvent`, call `CdcRouter.Route()` per event, deliver result via `PersonModuleClient`, call `CheckpointService.AdvanceAsync()` after batch; register as hosted service in `src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs`

**Checkpoint**: User Story 1 is fully functional. Person CDC events are consumed, routed, delivered to PersonModule, and checkpointed. Delete and org events are silently discarded.

---

## Phase 4: User Story 2 — Child Registration Synchronization (Priority: P1)

**Goal**: CDC create/update events for `Barn` and `Barn_n_Hjemmstedskommune` tables flow through child registration path to `PUT /innmating/barn/{birkId}`. Composite status values pass through unchanged.

**Independent Test**: Inject a child registration create CdcEvent with composite status "Bestilling/Under Behandling". Verify `PUT /innmating/barn/{birkId}` receives the status unchanged. Verify a municipality-change event updates the child registration.

- [X] T024 [P] [US2] Unit tests: `ChildRegistrationMapper.Map()` sets `birkId` from BirkID field; `barnRegistreringId` is a stable Guid derived deterministically from BirkID; `personId` is derived from the parent's BiRK PersonPK (same derivation as PersonMapper); passes composite status value "Bestilling/Under Behandling" through unchanged (no splitting); `barnTypeId`, `barnStatusTypeId`, `sikkerhetsnivaaTypeId` are Guids (not integers or strings); accepts null fields in `tests/M2LB.PersonBiRKAdapter.Unit/Mapping/ChildRegistrationMapperTests.cs`
- [X] T025 [P] [US2] Unit tests: `CdcRouter` routes `Barn` tabellnavn to `PostType.Barn`; routes `Barn_n_Hjemmstedskommune` to `PostType.Barn`; does not route unknown table names to child path in `tests/M2LB.PersonBiRKAdapter.Unit/Routing/CdcRouterTests.cs`
- [X] T026 [P] [US2] Implement `ChildRegistrationMapper.Map()` stub: `birkId` from BirkID; `barnRegistreringId` = deterministic Guid from BirkID (same strategy as PersonMapper `personId`); `personId` = deterministic Guid from parent BiRK PersonPK; composite `status` passed through as-is (FK-2.6); `barnTypeId`, `barnStatusTypeId`, `sikkerhetsnivaaTypeId` = GUID lookup stubs returning `Guid.Empty` until Å-03 resolved; `kommuneNr` = string code (no Guid lookup); `opprettetAv`/`endretAv` from `PersonModuleOptions.SystemBrukerId`; `kilde` = `"BiRK"`; `korrelasjonId` = `Guid.NewGuid()`; null-safe for all nullable fields; comment `// TODO Å-01: source field names; TODO Å-03: type Guid resolution` in `src/M2LB.PersonBiRKAdapter.Domain/Mapping/ChildRegistrationMapper.cs`
- [X] T027 [US2] Extend `CdcRouter.Route()` step 4 with child registration routing: `Barn` and `Barn_n_Hjemmstedskommune` tabellnavn → `PostType.Barn`; reference data table names → `RoutingOutcome.Discarded` (same path as org entities — PersonModule auto-creates reference data; no forwarding needed) in `src/M2LB.PersonBiRKAdapter.Domain/Routing/CdcRouter.cs`
- [X] T028 [US2] Add `PersonModuleClient.UpsertChildRegistrationAsync()`: `PUT /api/person/v1/innmating/barn`, JSON body from `ChildRegistrationRecord`; same resilience pipeline and `DeliveryResult` mapping as `UpsertPersonAsync` (204 = Success, 422 = ValidationFailure, 429 = RateLimited, 5xx = TransientFailure) in `src/M2LB.PersonBiRKAdapter.Infrastructure/Http/PersonModuleClient.cs`
- [X] T029 [US2] Integration test: child registration CDC event → `CdcRouter` → `ChildRegistrationMapper` → `PersonModuleClient` (mocked) `PUT /api/person/v1/innmating/barn` called with composite status unchanged → checkpoint advances in `tests/M2LB.PersonBiRKAdapter.Integration/ChildRegistrationProcessingTests.cs`

**Checkpoint**: User Stories 1 and 2 are both functional. Person and child CDC events are independently processed and delivered.

---

## Phase 5: User Story 3 — Security Classification Enforcement (Priority: P1)

**Goal**: CDC records with `sikkerhetsnivaa` 2 (Kode 6) or 3 (Kode 7) are rejected before any processing. No data reaches PersonModule. A critical log entry is written. A mandatory-acknowledgment operational alert is raised. Stream advances past the record.

**Independent Test**: Inject CDC event with `sikkerhetsnivaa: 2`. Verify zero HTTP calls to PersonModule, `ILogger.LogCritical` called with BiRK ID and timestamp, alert method invoked, `UpdateCheckpointAsync` still called. Verify Kode 6/7 counter = 1. Under normal operation, verify counter remains 0.

- [X] T030 [P] [US3] Unit tests: `SecurityClassificationGuard.Evaluate()` with level 2 → returns `GuardResult.Rejected`, no mapper call; level 3 → same outcome; level 0 → `GuardResult.Allowed`; level 1 → `GuardResult.Allowed`; reject path does NOT include personal data in log message in `tests/M2LB.PersonBiRKAdapter.Unit/Security/SecurityClassificationGuardTests.cs`
- [X] T031 [P] [US3] Implement Kode 6/7 rejection `Counter<long>` metric (`birk.kode67.rejections`) in `AdapterMetrics` using `System.Diagnostics.Metrics.Meter`; expose method `RecordKode67Rejection()` in `src/M2LB.PersonBiRKAdapter.Infrastructure/Observability/AdapterMetrics.cs`
- [X] T032 [US3] Implement `SecurityClassificationGuard.Evaluate()`: if `CdcEvent.sikkerhetsnivaa` is 2 or 3 → call `ILogger.LogCritical` with BiRK record identifier (PersonPK for person records, BirkID for child records — extracted from `CdcEvent.payload` per Å-01 field names) and UTC timestamp, MUST NOT log any personal data fields; invoke `IAlertService.RaiseKode67Alert()` (mandatory-acknowledgment, does not auto-resolve); increment Kode 6/7 counter via `AdapterMetrics`; return `GuardResult.Rejected` in `src/M2LB.PersonBiRKAdapter.Domain/Security/SecurityClassificationGuard.cs`
- [X] T033 [US3] Replace the step 1 stub in `CdcRouter.Route()` with a real call to `SecurityClassificationGuard.Evaluate()`; if `Rejected` return `RoutingOutcome.Rejected` immediately before any other check (security check is the unconditional first step per FR-006) in `src/M2LB.PersonBiRKAdapter.Domain/Routing/CdcRouter.cs`
- [X] T034 [US3] Update `CdcProcessorWorker` to call `CheckpointService.AdvanceAsync()` for `RoutingOutcome.Rejected` events — checkpoint MUST advance past Kode 6/7 rejections so subsequent records are not blocked (FR-007) in `src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs`
- [X] T035 [US3] Integration test: inject CDC event with `sikkerhetsnivaa: 2` → verify `PersonModuleClient` not called, `ILogger.LogCritical` captured, alert service invoked, `UpdateCheckpointAsync` called once → Kode 6/7 counter = 1; inject follow-up normal event → processes successfully in `tests/M2LB.PersonBiRKAdapter.Integration/SecurityClassificationTests.cs`

**Checkpoint**: Security classification enforcement is active. No Kode 6/7 record can reach PersonModule.

---

## Phase 6: User Story 4 — Initial Full Load (Priority: P1)

**Goal**: On first startup (no saved checkpoint), all BiRK persons and child registrations are delivered to PersonModule via batch ingestion. Persons are always delivered before child registrations. Operation is idempotent (second run → no duplicates).

**Independent Test**: Start adapter with empty checkpoint against mock PersonModule. Verify `POST /innmating/batch` called with all persons before any child registrations. Run full load again — PersonModule returns 204 for all records. Verify no errors and no duplicate records.

- [X] T036 [P] [US4] Unit tests: `FullLoadService` calls `BatchIngestAsync` with person batch before any child batch; progress log emitted at configured interval (e.g. every 1000 records); second run with all 204 responses completes without error in `tests/M2LB.PersonBiRKAdapter.Unit/FullLoad/FullLoadServiceTests.cs`
- [X] T036b [P] [US4] Define `IBirkPersonSource` with method `IAsyncEnumerable<CdcEvent> GetAllPersonsAsync(CancellationToken ct)` and `IBirkChildRegistrationSource` with method `IAsyncEnumerable<CdcEvent> GetAllChildRegistrationsAsync(CancellationToken ct)` in `src/M2LB.PersonBiRKAdapter.Domain/FullLoad/IBirkPersonSource.cs` and `src/M2LB.PersonBiRKAdapter.Domain/FullLoad/IBirkChildRegistrationSource.cs`; concrete Infrastructure implementations depend on BiRK data access approach (TBD per Å-01)
- [X] T037 [US4] Add `PersonModuleClient.BatchIngestAsync()`: `POST /api/person/v1/innmating/batch`, body is `BatchIngestRequest` (`{ "Personer": [...], "Barn": [...] }`); response is always `200 OK` with `BatchResultat` body (`{ behandlet, feil }`); iterate `feil` list and write each entry to fault queue (there is no `422` at batch level — per-record failures are in the body); checkpoint advances after full response processed (FR-008) in `src/M2LB.PersonBiRKAdapter.Infrastructure/Http/PersonModuleClient.cs`
- [X] T038 [US4] Implement `FullLoadService`: enumerate all BiRK persons (injected via `IBirkPersonSource`) → call `PersonMapper` → `BatchIngestAsync` in configured batch sizes → enumerate all BiRK child registrations (injected via `IBirkChildRegistrationSource`) → call `ChildRegistrationMapper` → `BatchIngestAsync`; persons MUST be fully ingested before first child record is submitted (FR-009, SC-006) in `src/M2LB.PersonBiRKAdapter.Domain/FullLoad/FullLoadService.cs`
- [X] T039 [US4] Add configurable progress logging to `FullLoadService`: log `ILogger.LogInformation` every `FullLoadOptions.ProgressLogIntervalRecords` records with count processed and estimated total in `src/M2LB.PersonBiRKAdapter.Domain/FullLoad/FullLoadService.cs`
- [X] T040 [US4] In `CdcProcessorWorker.StartAsync()`: check whether a checkpoint exists via `EventProcessorClient` metadata; if no checkpoint found → call `FullLoadService.ExecuteAsync()` before starting CDC stream processing in `src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs`
- [X] T041 [US4] Integration test (Testcontainers SQL): run `FullLoadService` with 50 mock persons + 20 mock children → verify HTTP call order (all persons first), verify all records in PersonModule mock; run again → all calls return 204 → no errors, no duplicate fault queue entries in `tests/M2LB.PersonBiRKAdapter.Integration/FullLoadTests.cs`

**Checkpoint**: Initial full load delivers all persons then all children on first startup. Running twice is safe.

---

## Phase 7: User Story 5 — Fault Tolerance and Operational Recovery (Priority: P2)

**Goal**: Transient delivery failures are retried. Exhausted retries and validation failures persist to `feilkoe`. Background re-processor re-delivers automatically. Expired checkpoint triggers new full load. Personal data is deleted from `feilkoe` on successful re-delivery or after 30-day expiry.

**Independent Test**: Simulate PersonModule returning 5xx. Verify `feilkoe` row created with `feiltype=FORBIGAAENDE`, correct expiry, stream advances. Restore PersonModule. Verify `FaultQueueProcessor` re-delivers, row deleted (payload cleared), alert resolves.

- [X] T042 [P] [US5] Unit tests: `FaultQueueEntry` created on delivery exhaustion has `feiltype=FORBIGAAENDE`, `AntallForsok` = max retry count, `UtlopertTidspunkt = OpprettetTidspunkt + FaultQueueOptions.MaxRetentionDays` (verify the test reads from config, not a hardcoded constant), `Payload` contains PersonModule-format JSON, `Feilmelding` MUST NOT contain personal data; 422 path creates entry with `feiltype=VALIDERING` in `tests/M2LB.PersonBiRKAdapter.Unit/FaultQueue/FaultQueueEntryTests.cs`
- [X] T043 [P] [US5] Unit tests: `EventProcessorClient` resumes from saved checkpoint on restart — `CdcProcessorWorker` does NOT trigger full load when checkpoint exists and is current in `tests/M2LB.PersonBiRKAdapter.Unit/FaultQueue/CheckpointResumptionTests.cs`
- [X] T044 [US5] Define `IFaultQueueRepository` with methods: `AddAsync(FaultQueueEntry)`, `QueryForRedeliveryAsync()`, `IncrementRetryAsync(Guid)`, `ClearPayloadAndDeleteAsync(Guid)`, `PurgeExpiredAsync()` in `src/M2LB.PersonBiRKAdapter.Infrastructure/Persistence/IFaultQueueRepository.cs`
- [X] T045 [US5] Implement `FaultQueueRepository` (EF Core): `AddAsync` inserts with `OpprettetTidspunkt=UtcNow`, `UtlopertTidspunkt=UtcNow+MaxRetentionDays`; `QueryForRedeliveryAsync` filters by `siste_forsok_tidspunkt`; `ClearPayloadAndDeleteAsync` deletes the row (payload gone); `PurgeExpiredAsync` deletes all rows where `utloper_tidspunkt <= UtcNow`, logs each purge as unresolved delivery failure (FR-016) in `src/M2LB.PersonBiRKAdapter.Infrastructure/Persistence/FaultQueueRepository.cs`
- [X] T046 [US5] Wire delivery exhaustion path: on `DeliveryResult.TransientFailure` after max retries → call `IFaultQueueRepository.AddAsync()` with serialized payload and `feiltype=FORBIGAAENDE`; raise operational alert via `IAlertService.RaiseDeliveryFailureAlert()`; checkpoint MUST still advance so stream is not blocked in `src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs`
- [X] T047 [US5] Wire 422 validation failure path: on `DeliveryResult.ValidationFailure` → call `IFaultQueueRepository.AddAsync()` with `feiltype=VALIDERING` immediately, no retry (FK-5.3); checkpoint advances in `src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs`
- [X] T048 [US5] Implement `FaultQueueProcessor` as `BackgroundService` with `PeriodicTimer` (interval from `FaultQueueOptions.PollIntervalMinutes`): query `feilkoe`, attempt re-delivery using same resilience pipeline, call `ClearPayloadAndDeleteAsync` on success, call `PurgeExpiredAsync` each cycle, resolve delivery-failure alert when `feilkoe` is empty (FK-8.2) in `src/M2LB.PersonBiRKAdapter.Worker/Workers/FaultQueueProcessor.cs`
- [X] T049 [US5] Implement expired checkpoint detection in `CdcProcessorWorker.OnProcessErrorAsync`: catch partition-level `EventHubsException` with `ServiceCommunicationProblem`; log `ILogger.LogCritical`; invoke `IAlertService.RaiseExpiredCheckpointAlert()` (FR-011) in `src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs`
- [X] T050 [US5] Register `FaultQueueProcessor` as hosted service; register `IFaultQueueRepository` → `FaultQueueRepository` (singleton via `IDbContextFactory`); register `IPersonDeliveryClient` → `PersonModuleClient`; register `IFullLoadService` → `FullLoadService`; switch to `AddDbContextFactory` (singleton) for background-service compatibility in `src/M2LB.PersonBiRKAdapter.Worker/Program.cs`
- [X] T051 [US5] Integration test (Testcontainers SQL): verify `AddAsync` persists entry with correct fields; `QueryForRedeliveryAsync` excludes expired rows; `ClearPayloadAndDeleteAsync` removes row; `PurgeExpiredAsync` removes only expired rows; `IncrementRetryAsync` increments count and sets timestamp in `tests/M2LB.PersonBiRKAdapter.Integration/FaultQueueTests.cs` (requires Docker)

**Checkpoint**: All five user stories are independently functional. No record can be silently lost.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Health endpoints, admin endpoint, full observability stack, Managed Identity audit, and quickstart validation.

- [X] T052 [P] Implement three health check classes per health-api.md contract: `EventHubsHealthCheck` (Unhealthy if connection lost), `PersonModuleHealthCheck` (Degraded if 5xx/timeout), `FeilkoeHealthCheck` (Unhealthy if SQL unreachable); cache readiness results 15 seconds to avoid synchronous network calls per request in `src/M2LB.PersonBiRKAdapter.Infrastructure/HealthChecks/`
- [X] T053 [P] Implement `AdminController` with `POST /admin/feilkoe/reprosesser`: validate Bearer token (Managed Identity service-to-service); return 202 + `{"antallPoster": <count>}` triggering `FaultQueueProcessor` immediately; return 409 + `{"melding": "Allerede under prosessering"}` if run already in progress; return 401 if no valid token; mark endpoint internal (MUST NOT be routed via YARP gateway) in `src/M2LB.PersonBiRKAdapter.Worker/Controllers/AdminController.cs`
- [X] T054 [P] Implement all FR-018 metrics in `AdapterMetrics` via `Meter`: `Counter<long>` for events processed per record type (Person/Barn — reference data events are discarded, not counted as deliveries); `Counter<long>` for delivery outcomes per type (success/validationFailure/faultQueued); `ObservableGauge<long>` for fault queue depth (`feilkoe` row count); `ObservableGauge<long>` for stream lag (Event Hubs latest offset minus checkpoint offset); `ObservableGauge<long>` for initial load progress (records processed); Kode 6/7 counter already added in T031 in `src/M2LB.PersonBiRKAdapter.Infrastructure/Observability/AdapterMetrics.cs`
- [X] T055 Complete `src/M2LB.PersonBiRKAdapter.Worker/Program.cs`: register T052 health checks with `AddHealthChecks()`; map `GET /helse/live` → liveness (always `{"status":"Frisk"}`); map `GET /helse/ready` → readiness with aggregate worst-status rule and Norwegian mapping (`Healthy`→`Frisk`, `Degraded`→`Degradert`, `Unhealthy`→`Utilgjengelig`) per health-api.md; configure OpenTelemetry pipeline with `Azure.Monitor.OpenTelemetry.Exporter`; retrieve Application Insights connection string from Azure Key Vault via `SecretClient` + `DefaultAzureCredential` at startup (not from `appsettings.json`)
- [X] T056 [P] Audit all Infrastructure DI registrations for `DefaultAzureCredential` compliance: `EventProcessorClient` (Event Hubs), `BlobContainerClient` (checkpoint), `PersonModuleClient` (bearer token via `DefaultAzureCredential`), `AdapterDbContext` (`AccessToken` on `SqlConnection`), `SecretClient` (Key Vault) — verify zero stored credentials in any config file or source; add `// DefaultAzureCredential` comment to each registration in `src/M2LB.PersonBiRKAdapter.Infrastructure/Extensions/`
- [X] T057 Run quickstart.md verification scenarios: start adapter locally (`dotnet run`); verify `GET /helse/live` → `{"status":"Frisk"}`; verify `GET /helse/ready` shows all three dependencies; submit test event with `sikkerhetsnivaa: 2` → no HTTP call, critical log written; submit same CDC event twice → PersonModule receives two calls, one record in PersonModule; verify fault queue scenario; verify checkpoint resumes on restart — **Known gaps (pending live Azure env)**: (1) `/helse/ready` EventHubs check will show Utilgjengelig until EventProcessorClient starts (EventHubs not connected in local dev); (2) `FaultQueueProcessor.RedeliverAsync` is a stub pending Å-01 field name confirmation — re-delivery returns false until implemented; (3) `AzureAd` TenantId/Audience must be filled in appsettings.Development.json for admin endpoint auth; (4) Key Vault URI must be configured for OTel/AI connection string (degrades gracefully if missing)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Depends on Phase 1 — **BLOCKS all user stories**
- **User Stories (Phases 3–7)**: Depend on Phase 2 completion
  - US1 (Phase 3): Start after Foundational
  - US2 (Phase 4): Start after Foundational — **parallel with US1**
  - US3 (Phase 5): Start after Foundational — integrates into `CdcRouter` built in US1 (T033 extends T017)
  - US4 (Phase 6): Needs US1 mapper (T018) + US2 mapper (T026) + batch endpoint; start after US1+US2 complete
  - US5 (Phase 7): Needs delivery infrastructure from US1 (T019, T020, T021); start after US1 complete
- **Polish (Phase 8)**: Depends on all user stories complete

### User Story Dependencies

| Story | Can start after | Integration dependency |
|-------|----------------|------------------------|
| US1 (P1) | Phase 2 | None |
| US2 (P1) | Phase 2 | Parallel with US1 |
| US3 (P1) | Phase 2 | Extends `CdcRouter` from US1 (T017 must be complete before T033) |
| US4 (P1) | US1 + US2 | Reuses PersonMapper (T018) and ChildRegistrationMapper (T026) |
| US5 (P2) | US1 | Extends PersonModuleClient (T019) with fault queue wiring |

### Within Each Phase

- Tests → then implementation (tests must compile to check they fail before implementation)
- Models/interfaces before services
- Domain before Infrastructure before Worker wiring
- Core implementation before integration test

---

## Parallel Examples

### Phase 2 Parallel Burst
```
T005: Configuration options          (Worker/Configuration/)
T006: Domain interfaces              (Domain/Mapping/)
T007: Domain event types             (Domain/Events/)
T008: Outbound DTOs                  (Domain/Models/)
T012: EventProcessorClient DI        (Infrastructure/Extensions/)
T013: PersonModuleClient DI          (Infrastructure/Extensions/)
— all six run concurrently (different files)
```

### Phase 3 (US1) Parallel Burst
```
T015: Unit tests CdcRouter           (Unit/Routing/)
T016: Unit tests PersonMapper        (Unit/Mapping/)
T018: PersonMapper implementation    (Domain/Mapping/)
— three in parallel after T005–T008 are done
```

### Phase 4 (US2) Parallel Burst
```
T024: Unit tests ChildRegistrationMapper   (Unit/Mapping/)
T025: Unit tests CdcRouter child routing   (Unit/Routing/)
T026: ChildRegistrationMapper              (Domain/Mapping/)
— three in parallel
```

### Phase 8 Parallel Burst
```
T052: Three HealthCheck classes      (Infrastructure/HealthChecks/)
T053: AdminController                (Worker/Controllers/)
T054: AdapterMetrics (full FR-018)   (Infrastructure/Observability/)
T056: DefaultAzureCredential audit   (Infrastructure/Extensions/)
— four in parallel
```

---

## Implementation Strategy

### MVP First (User Stories 1–3 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (critical — blocks everything)
3. Complete Phase 3: US1 (person sync, checkpoint, resilience)
4. **STOP and VALIDATE**: person CDC events flowing end-to-end
5. Complete Phase 5: US3 (Kode 6/7 guard — safety requirement)
6. Complete Phase 4: US2 (child registration sync)
7. **STOP and VALIDATE**: all three P1 safety stories working
8. Deploy/demo

### Full Delivery

Continue with Phase 6 (US4 full load) → Phase 7 (US5 fault tolerance) → Phase 8 (polish).

### Note on Å-01

`IPersonMapper` and `IChildRegistrationMapper` are implemented as stubs with known mappings (`PersonPK→eksternId`, `BirkID→birkId`, composite status pass-through, null safety). When `birk-person-feltmapping.md` arrives, only the mapper concrete classes (T018, T026) are updated — no other tasks are affected.

---

## Notes

- `[P]` = different files, no dependencies on incomplete tasks in the same phase
- `[US#]` = maps task to specific user story for traceability
- Each story is independently completable and testable
- Tests must compile and fail before implementation begins in each story
- Commit after each phase checkpoint
- Kode 6/7 guard (US3) is a safety requirement — do not defer it past MVP
