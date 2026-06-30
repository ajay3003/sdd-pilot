# Tasks: BiRK Hendelsesadapter

**Input**: Design documents from `/specs/001-birk-hendelse-adapter/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Tests**: Test tasks are included for all user story acceptance scenarios (PP-09/GL-24 compliance).

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story. Within each phase, test skeletons come before implementation so tests fail first (PP-09).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US5)
- Exact file paths are included in all descriptions

> **Note**: T021b is the only non-sequential task ID in this file — it was added during spec refinement as a supplemental sub-task of Phase 2 after initial task numbering was complete. All other tasks use plain numeric IDs.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution scaffolding, project files, local development infrastructure

- [X] T001 Create solution file `M2LB.Hendelse.BiRK.sln` in repo root referencing all four source and test projects
- [X] T002 Create `src/M2LB.Hendelse.BiRK.Adapter/M2LB.Hendelse.BiRK.Adapter.csproj` (.NET 10 Worker Service; refs: Azure.Messaging.EventHubs.Processor, FrameworkReference:Microsoft.AspNetCore.App, Azure.Monitor.OpenTelemetry.Exporter, Azure.Identity, Azure.Messaging.ServiceBus, Serilog.Sinks.Console, OpenTelemetry.Extensions.Hosting)
- [X] T003 [P] Create `src/M2LB.Hendelse.BiRK.Domain/M2LB.Hendelse.BiRK.Domain.csproj` (classlib, .NET 10, no external dependencies beyond framework)
- [X] T004 [P] Create `src/M2LB.Hendelse.BiRK.Infrastructure/M2LB.Hendelse.BiRK.Infrastructure.csproj` (classlib, .NET 10; refs: Microsoft.EntityFrameworkCore.SqlServer, Azure.Messaging.EventHubs.Processor, Azure.Messaging.ServiceBus, Azure.Identity, Microsoft.Extensions.Http.Resilience, Microsoft.Data.SqlClient)
- [X] T005 [P] Create `tests/M2LB.Hendelse.BiRK.Unit/M2LB.Hendelse.BiRK.Unit.csproj` (xUnit, .NET 10; refs: xunit, NSubstitute, FluentAssertions, coverlet.collector)
- [X] T006 [P] Create `tests/M2LB.Hendelse.BiRK.Integration/M2LB.Hendelse.BiRK.Integration.csproj` (xUnit, .NET 10; refs: xunit, Microsoft.AspNetCore.Mvc.Testing, Azure.Messaging.EventHubs, FluentAssertions)
- [X] T007 Create `docker-compose.yml` in repo root: Azurite on ports 10000–10002, Azure Service Bus emulator on port 5672, SQL Server 2022 Express on port 1433 (per quickstart.md)
- [X] T008 [P] Create `src/M2LB.Hendelse.BiRK.Adapter/appsettings.json` and `src/M2LB.Hendelse.BiRK.Adapter/appsettings.Development.json.template` with full configuration schema: `EventHubs` section (keys: `FullyQualifiedNamespace`, `EventHubName`, `ConsumerGroup`, `BlobContainerName`); `Hendelsestjenesten` section (key: `BaseUrl`); `Tjeneste` section (key: `BaseUrl`); `ConnectionStrings` section (key: `BirkAdapterDb`); `ServiceBus` section (keys: `Namespace`, `ErrorQueueName`); `CodeMappings` section (sub-keys: `HjemmelType`, `TvangsProtokollStatusType`, `RommingKategoriType` — each a numeric-string-to-UUID-string object (e.g. `{ "1": "<uuid>" }`) — keys are BiRK integer code values serialized as strings, bound to `Dictionary<int, Guid>` in T010); `Resilience` section (keys: `MaxRetries`, `InitialDelay`, `MaxDelay`)
- [X] T009 [P] Create `src/M2LB.Hendelse.BiRK.Adapter/code-mappings.json` with placeholder UUID values for all required BiRK code types: HjemmelType, TvangsProtokollStatusType, RommingKategoriType (per data-model.md example structure)

**Checkpoint**: All projects compile, `docker compose up` starts infrastructure

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure required by ALL user stories — configuration, persistence, resilience, health checks, startup gating

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T010 Create `src/M2LB.Hendelse.BiRK.Domain/CodeMappings/CodeMappingOptions.cs` with three `Dictionary<int, Guid>` properties: `HjemmelType`, `TvangsProtokollStatusType`, `RommingKategoriType`
- [X] T011 Create `src/M2LB.Hendelse.BiRK.Infrastructure/Persistence/BirkHendelseRegistrering.cs` EF Core entity: `BirkHendelsesId` (string PK), `HendelsesId` (Guid), `HendelsesType` (string), `RegistrertTidspunkt` (DateTime, UTC)
- [X] T012 [P] Create `src/M2LB.Hendelse.BiRK.Infrastructure/Persistence/BirkAdapterDbContext.cs` with `DbSet<BirkHendelseRegistrering>` and `BiRKAdapter` schema configuration via `modelBuilder.HasDefaultSchema`
- [X] T013 Generate EF Core migration `Initial` in `src/M2LB.Hendelse.BiRK.Infrastructure/Migrations/` creating `BiRKAdapter.BirkHendelseRegistrering` table per data-model.md schema (PK on BirkHendelsesId, NVARCHAR(200))
- [X] T014 Create `src/M2LB.Hendelse.BiRK.Infrastructure/Http/ResilienceOptions.cs` with `MaxRetries` (default 10), `InitialDelay` (default 5s), `MaxDelay` (default 5min); register as `IOptions<ResilienceOptions>` bound to `"Resilience"` config section
- [X] T015 [P] Create `src/M2LB.Hendelse.BiRK.Infrastructure/Http/PollyResiliencePipelineExtensions.cs` adding `AddResilienceHandler` pipeline (TimeoutResilience 30s → RetryStrategy exponential → CircuitBreaker 5 failures/30s window/1min open) reading delays from `IOptions<ResilienceOptions>`
- [X] T016 Create `src/M2LB.Hendelse.BiRK.Adapter/HealthChecks/SqlHealthCheck.cs` implementing `IHealthCheck`: executes `SELECT 1` against `BirkAdapterDbContext`
- [X] T017 [P] Create `src/M2LB.Hendelse.BiRK.Adapter/HealthChecks/EventHubsHealthCheck.cs` implementing `IHealthCheck`: calls `EventHubConsumerClient.GetEventHubPropertiesAsync` to verify Event Hub connectivity — use a dedicated singleton `EventHubConsumerClient` registered in `Program.cs` (see T021b); do NOT use `EventHubProducerClient` (producer role is not provisioned for this adapter); also exposes `LastSuccessfulReadAt` (DateTimeOffset?) property updated by `BirkEventProcessorWorker` after each processed event, included in health check `Description`
- [X] T018 [P] Create `src/M2LB.Hendelse.BiRK.Adapter/HealthChecks/HendelsestjenesteHealthCheck.cs` implementing `IHealthCheck`: HTTP GET probe to Hendelsestjenesten health endpoint
- [X] T019 [P] Create `src/M2LB.Hendelse.BiRK.Adapter/HealthChecks/TjenesteHealthCheck.cs` implementing `IHealthCheck`: HTTP GET probe to Tjeneste health endpoint
- [X] T020 Create `src/M2LB.Hendelse.BiRK.Adapter/Workers/StartupReadinessWorker.cs` implementing `IHostedService`: polls all four health checks on startup; blocks `BirkEventProcessorWorker` from starting via a shared `TaskCompletionSource<bool>` gate (`IReadinessGate`) until all checks report Healthy; `TjenesteHealthCheck` MUST be explicitly included in the blocking condition (FR-009) — the gate must NOT signal ready unless Tjeneste is reachable; this is verified by the integration test in T060 (historical load only starts after Tjeneste passes)
- [X] T021 [P] Create `src/M2LB.Hendelse.BiRK.Infrastructure/Http/Correlation/ICorrelationIdAccessor.cs` (interface: `string? Get()`, `void Set(string id)`) and `AsyncLocalCorrelationIdAccessor.cs` (AsyncLocal-backed singleton implementation); this is the correlation ID carrier used by `BirkEventProcessorWorker` (T038) and `CorrelationIdDelegatingHandler` (T064) — must exist before US1 phase begins
- [X] T021b Configure `src/M2LB.Hendelse.BiRK.Adapter/Program.cs`: Azure Key Vault config provider (DefaultAzureCredential); Serilog JSON console sink; OpenTelemetry with Azure Monitor exporter; `IOptions<CodeMappingOptions>` bound to `"CodeMappings"` with fail-fast validation — each of the three dictionaries must contain all expected code keys from `code-mappings.json` (startup fails if any individual mapping is absent, not only if the dictionary is empty); EF Core SQL Server with `Authentication=Active Directory Default`; register all four health checks; expose `GET /health` via `MapHealthChecks`; register `StartupReadinessWorker`; register `ICorrelationIdAccessor` as singleton (from T021); register singleton `EventHubConsumerClient` (consumer group from `"EventHubs:ConsumerGroup"` config key, `DefaultAzureCredential`) for use by `EventHubsHealthCheck` (T017) — this is a separate client from `EventProcessorClient` (T037) and is used only for health-check probing

**Checkpoint**: `dotnet run` starts, health checks accessible at `/health`, EF migrations run, code mappings validated at startup

---

## Phase 3: User Story 1 — Continuous Event Stream Processing (Priority: P1) 🎯 MVP

**Goal**: Core event pipeline — read CDC events from Event Hubs, translate, deliver to Hendelsestjenesten, checkpoint progress

**Independent Test**: Publish a test CDC message to Event Hub; verify corresponding event appears in Hendelsestjenesten stub with correct field values and checkpoint is updated in Blob Storage

### Tests for User Story 1

> **Write these test skeletons first — they MUST fail until the corresponding implementation tasks complete.**

- [X] T022 [P] [US1] Create `tests/M2LB.Hendelse.BiRK.Unit/Translators/TvangsProtokollTranslatorTests.cs` with xUnit test methods for all AC1 fields: given a TvangsProtokoll `JsonElement` payload, assert `InngrepsInnmatingRequest.Kilde = "BiRK"`, `KildeId = BirkHendelsesId`, `KallerIdentitet = Guid.Empty`, `BirkTiltakPK` set, `FraDato` mapped, `InngrepDetalj.HjemmelTypeId` returns mapped UUID, `InngrepDetalj.TvangsProtokollStatusTypeId` returns mapped UUID, `Involverte[0].EksternBeskrivelse` = RegAv value and `InternBrukerId` is null; use NSubstitute for `CodeMapper`
- [X] T023 [P] [US1] Create `tests/M2LB.Hendelse.BiRK.Unit/Translators/RommingTranslatorTests.cs`: AC2 — `RommingKategoriType=1` produces correct `HendelsesTypeId` (Uteblivelse) and `RommingsDetalj`; AC3 — `RommingKategoriType=2` produces Rømming `HendelsesTypeId`; AC4 — `RommingKategoriType=3` produces Bortføring `HendelsesTypeId`; test `OriginalHendelsesId` resolved from registry when `OriginalRomningFk` found; test `OriginalHendelsesId = null` with Information log when not found; use NSubstitute for `IBirkHendelseRegistreringRepository` and `CodeMapper`
- [X] T024 [P] [US1] Create `tests/M2LB.Hendelse.BiRK.Unit/Workers/BirkEventProcessorWorkerTests.cs`: DELETE operation — verify no translator call, no HTTP call, checkpoint committed; INSERT/UPDATE on `TvangsProtokoll` — verify `TvangsProtokollTranslator` called, `HendelsestjenesteHttpClient.PutInngrepsInnmatingAsync` called with correct `BirkHendelsesId`, registry upserted, checkpoint committed; all dependencies NSubstitute stubs
- [X] T025 [US1] Create `tests/M2LB.Hendelse.BiRK.Integration/EventStream/EventStreamProcessingTests.cs`: AC1 full pipeline — publish a serialized TvangsProtokoll CDC INSERT message to Azurite EventHub; start adapter against test infrastructure; assert `HendelsestjenesteHttpClient` stub receives `PUT /api/hendelser/v1/innmating/inngrep/{birkHendelsesId}`; assert `BirkHendelseRegistrering` row exists in SQL with correct `BirkHendelsesId` and `HendelsesId`; assert checkpoint blob updated in Azurite
- [X] T026 [US1] Create `tests/M2LB.Hendelse.BiRK.Integration/EventStream/RestartResumeTests.cs`: AC5 — commit a checkpoint for partition at offset N; start a fresh adapter instance; verify events at offsets ≤ N are NOT re-delivered; verify events at offset N+1 ARE processed; assert no duplicate `BirkHendelseRegistrering` rows

### Implementation for User Story 1

- [X] T027 [P] [US1] Create `src/M2LB.Hendelse.BiRK.Domain/Events/BirkCdcEvent.cs` record: `BirkHendelsesId` (string), `Tabell` (string), `Operasjon` (string), `Payload` (JsonElement), `CorrelationId` (string), `EnqueuedTime` (DateTimeOffset)
- [X] T028 [P] [US1] Create `src/M2LB.Hendelse.BiRK.Domain/Innmating/InngrepsInnmatingRequest.cs` with all fields per `contracts/hendelsestjenesten-innmating.md`: `KildeId`, `HendelsesTypeId`, `BarnId` (nullable), `BirkTiltakPK` (nullable), `FraDato`, optional date/time/string fields, `InngrepDetalj` (nested object with `HjemmelTypeId`, `TvangsProtokollStatusTypeId`, protocol numbers, follow-up dates), `Involverte` list
- [X] T029 [P] [US1] Create `src/M2LB.Hendelse.BiRK.Domain/Innmating/RommingsInnmatingRequest.cs` with all fields per `contracts/hendelsestjenesten-innmating.md`: `KildeId`, `HendelsesTypeId`, `BarnId` (nullable), `BirkTiltakPK` (nullable), `FraDato`, optional fields, `RommingsDetalj` (nested with `RommingKategoriTypeId`, police dates, `OriginalHendelsesId` nullable), `Involverte` list
- [X] T030 [P] [US1] Create `src/M2LB.Hendelse.BiRK.Domain/Innmating/InnmatingResultat.cs` enum: `Opprettet`, `Oppdatert`, `Uendret`
- [X] T031 [US1] Create `src/M2LB.Hendelse.BiRK.Domain/Persistence/IBirkHendelseRegistreringRepository.cs` interface in Domain layer: `UpsertAsync(string birkHendelsesId, Guid hendelsesId, string hendelsesType, CancellationToken ct)` and `FindByBirkHendelsesIdAsync(string birkHendelsesId, CancellationToken ct) → Guid?`
- [X] T032 [US1] Implement `src/M2LB.Hendelse.BiRK.Domain/Translators/TvangsProtokollTranslator.cs`: map `JsonElement` BiRK payload fields to `InngrepsInnmatingRequest`; set `Kilde = "BiRK"`, `KallerIdentitet = Guid.Empty`, `KildeId = BirkHendelsesId`; map `RegAv` free-text to `Involverte[0].EksternBeskrivelse` (no `InternBrukerId`); delegate code mapping to `CodeMapper`
- [X] T033 [US1] Implement `src/M2LB.Hendelse.BiRK.Domain/Translators/RommingTranslator.cs`: map `JsonElement` BiRK payload to `RommingsInnmatingRequest`; delegate code mapping to `CodeMapper`; resolve `OriginalHendelsesId` by looking up `OriginalRomningFk` in `IBirkHendelseRegistreringRepository`; leave null and log Information if not found; map `RegAv` to `EksternBeskrivelse`
- [X] T034 [US1] Create `src/M2LB.Hendelse.BiRK.Infrastructure/Persistence/BirkHendelseRegistreringRepository.cs` implementing `IBirkHendelseRegistreringRepository`: `UpsertAsync` and `FindByBirkHendelsesIdAsync` using `BirkAdapterDbContext`
- [X] T035 [US1] Create `src/M2LB.Hendelse.BiRK.Infrastructure/Http/HendelsestjenesteHttpClient.cs`: typed HTTP client; `PutInngrepsInnmatingAsync(birkHendelsesId, request, correlationId)` calls `PUT /api/hendelser/v1/innmating/inngrep/{birkHendelsesId}` with `X-Correlation-Id` header; `PutRommingsInnmatingAsync` calls `PUT /api/hendelser/v1/innmating/romming/{birkHendelsesId}`; parse `HendelsesId` from 201/200 response body; return `(InnmatingResultat, Guid? hendelsesId)`
- [X] T036 [US1] Register `HendelsestjenesteHttpClient` in `Program.cs` via `AddHttpClient<HendelsestjenesteHttpClient>` with `DefaultAzureCredential` bearer token handler and Polly resilience pipeline (from T015)
- [X] T037 [US1] Create `src/M2LB.Hendelse.BiRK.Infrastructure/EventHubs/BirkEventProcessorSetup.cs`: factory creating `EventProcessorClient` with `BlobCheckpointStore` (DefaultAzureCredential for both Event Hubs and Blob Storage); configure `EventProcessorOptions.DefaultStartingPosition = EventPosition.Earliest` so that a first-run adapter (no checkpoint blob) replays from the beginning of the stream per FR-008; add an inline comment on this configuration line explaining it is the historical-load mechanism — do not remove or change this setting without re-reading FR-008
- [X] T038 [US1] Implement `src/M2LB.Hendelse.BiRK.Adapter/Workers/BirkEventProcessorWorker.cs` as `BackgroundService`: wait for `IReadinessGate`; start `EventProcessorClient`; on each event — deserialize to `BirkCdcEvent`, generate `CorrelationId = Guid.NewGuid().ToString()`, write to `ICorrelationIdAccessor` singleton (defined in T021, foundational phase); route `DELETE` to log-and-discard (log Tabell, BirkHendelsesId, timestamp); route `INSERT`/`UPDATE` through appropriate translator; call `HendelsestjenesteHttpClient`; on 201/200 upsert `BirkHendelseRegistrering`; commit Event Hubs checkpoint after successful processing; update `EventHubsHealthCheck.LastSuccessfulReadAt`
- [X] T039 [US1] Register `EventProcessorClient`, `BlobCheckpointStore`, `BirkHendelseRegistreringRepository`, `TvangsProtokollTranslator`, `RommingTranslator`, `BirkEventProcessorWorker` in `Program.cs`

**Checkpoint**: With a seeded Event Hub, the adapter reads, translates, and delivers a TvangsProtokoll or Rømming event; registry entry visible in SQL; checkpoint updated in Blob Storage; unit tests T022–T024 pass; integration tests T025–T026 pass

---

## Phase 4: User Story 2 — Child Resolution via Tjeneste Lookup (Priority: P1)

**Goal**: Resolve `BirkTiltakPK` to `BarnId` + `TjenesteId` before each event delivery

**Independent Test**: Call adapter lookup logic with a known `BirkTiltakPK` against a stubbed Tjeneste; verify `BarnId` and `TjenesteId` populated on ingestion request; verify null-BarnId path on 404

### Tests for User Story 2

> **Write these test skeletons first — they MUST fail until the corresponding implementation tasks complete.**

- [X] T040 [P] [US2] Create `tests/M2LB.Hendelse.BiRK.Unit/Http/TjenesteHttpClientTests.cs`: AC1 — mock HTTP 200 response with `barnId`/`tjenesteId` JSON; assert `TjenesteoppslagResultat.BarnId` and `TjenesteId` populated; AC2 — mock HTTP 404; assert `TjenesteoppslagResultat(null, null)` returned; verify `X-Correlation-Id` header present on outgoing request in both cases
- [X] T041 [P] [US2] Extend `tests/M2LB.Hendelse.BiRK.Unit/Workers/BirkEventProcessorWorkerTests.cs` with child-resolution cases: AC1 — Tjeneste returns match, assert `BarnId` and `TjenesteId` passed into translator; AC2 — Tjeneste returns no match, assert event delivered with `BarnId = null`, assert Information log with `BirkTiltakPK` and `BirkHendelsesId`; use NSubstitute for `ITjenesteClient`

> **Note**: US2 AC3 (Tjeneste unavailable → error queue) requires Phase 6 error queue infrastructure. Its integration test is **T042**, physically located in Phase 6. Implement T040–T041 here; T042 is completed when Phase 6 is reached.

### Implementation for User Story 2

- [X] T043 [P] [US2] Create `src/M2LB.Hendelse.BiRK.Domain/Clients/TjenesteoppslagResultat.cs` record: `BarnId` (Guid?), `TjenesteId` (Guid?)
- [X] T044 [P] [US2] Create `src/M2LB.Hendelse.BiRK.Domain/Clients/ITjenesteClient.cs` interface: `LookupByBirkTiltakPkAsync(int birkTiltakPK, string correlationId, CancellationToken ct)` returning `TjenesteoppslagResultat`
- [X] T045 [US2] Implement `src/M2LB.Hendelse.BiRK.Infrastructure/Http/TjenesteHttpClient.cs` implementing `ITjenesteClient`: call `GET /api/tjeneste/v1/birk/{birkTiltakPK}` with `X-Correlation-Id` header; on 200 deserialize `barnId`/`tjenesteId`; on 404 return `TjenesteoppslagResultat(null, null)`; 5xx propagates to Polly retry pipeline
- [X] T046 [US2] Register `TjenesteHttpClient` in `Program.cs` via `AddHttpClient<TjenesteHttpClient>` with `DefaultAzureCredential` bearer token handler and Polly resilience pipeline
- [X] T047 [US2] Integrate `ITjenesteClient` into `BirkEventProcessorWorker`: call `LookupByBirkTiltakPkAsync` after event deserialization and before translator call; pass resulting `BarnId`/`TjenesteId` into translator (or leave null); log no-match at Information level with `BirkTiltakPK` and `BirkHendelsesId`

**Checkpoint**: Processing a CDC event results in populated `BarnId` in delivered payload when Tjeneste returns a match; null `BarnId` when Tjeneste returns 404; unit tests T040–T041 pass. (The Tjeneste-unavailable integration test — T042 — requires Phase 6 error queue infrastructure and is placed there.)

---

## Phase 5: User Story 5 — Code Value Translation (Priority: P2)

**Goal**: Map BiRK numeric codes to M2LB UUID identifiers; route unmapped codes to error queue

**Independent Test**: Pass a `TvangsProtokoll` payload with known `HjemmelTypeFK = 5` through `CodeMapper`; verify returned UUID matches `code-mappings.json` entry; pass unknown code value and verify `CodeMappingNotFoundException` thrown

### Tests for User Story 5

> **Write this test skeleton first — it MUST fail until CodeMapper is implemented.**

- [X] T048 [US5] Create `tests/M2LB.Hendelse.BiRK.Unit/CodeMappings/CodeMapperTests.cs`: AC1 — `MapHjemmelType(5)` returns the UUID configured in test `CodeMappingOptions`; AC2 — `MapRommingKategoriType(2)` returns expected UUID; verify `MapTvangsProtokollStatusType` works for a known value; AC3 — `MapHjemmelType(999)` throws `CodeMappingNotFoundException` with `CodeType = "HjemmelType"` and `CodeValue = 999`; verify all three `Map*` methods throw for unmapped values

### Implementation for User Story 5

- [X] T049 [P] [US5] Create `src/M2LB.Hendelse.BiRK.Domain/CodeMappings/CodeMappingNotFoundException.cs` custom exception with `CodeType` (string) and `CodeValue` (int) properties
- [X] T050 [US5] Implement `src/M2LB.Hendelse.BiRK.Domain/CodeMappings/CodeMapper.cs`: inject `IOptions<CodeMappingOptions>`; provide `MapHjemmelType(int)`, `MapTvangsProtokollStatusType(int)`, `MapRommingKategoriType(int)` methods that return the mapped `Guid` or throw `CodeMappingNotFoundException` if not found
- [X] T051 [US5] Update `TvangsProtokollTranslator` and `RommingTranslator` to inject and use `CodeMapper` (replacing any direct `IOptions<CodeMappingOptions>` access); let `CodeMappingNotFoundException` propagate to caller for error queue routing

**Checkpoint**: Unit test T048 passes; all three mapping methods return correct UUIDs; `CodeMappingNotFoundException` thrown for unknown codes with correct properties

---

## Phase 6: User Story 4 — Error Handling and Retry (Priority: P2)

**Goal**: Never silently discard messages; retry transiently failing deliveries; move exhausted messages to Service Bus error queue with operational alert

**Independent Test**: Configure `HendelsestjenesteHttpClient` stub to return 503 repeatedly; verify adapter retries up to configured max; verify message published to Service Bus error queue; verify 422 does not block subsequent messages

### Tests for User Story 4

> **Write these test skeletons first — they MUST fail until the error handling is implemented.**

- [X] T052 [P] [US4] Create `tests/M2LB.Hendelse.BiRK.Unit/Workers/ErrorHandlingTests.cs`: AC1 — stub Polly pipeline to throw then succeed; assert retry called; assert delivery succeeds on recovery; AC2 — stub `HendelsestjenesteHttpClient` to return 422 (`HendelsestjenesteValidationException`); assert Warning log emitted with `BirkHendelsesId`; assert `IErrorQueuePublisher.PublishAsync` NOT called; assert next event processed normally; AC3 — stub to exhaust all retries; assert `IErrorQueuePublisher.PublishAsync` called once with `BirkHendelsesId` and `CorrelationId`; use NSubstitute for all dependencies
- [X] T053 [P] [US4] Create `tests/M2LB.Hendelse.BiRK.Unit/ServiceBus/ServiceBusErrorQueuePublisherTests.cs`: assert published `ServiceBusMessage` body contains `BirkHendelsesId`, `CorrelationId`, exception type, and table name; assert body does NOT contain `Payload`, `EksternBeskrivelse`, or any free-text name field (GL-21 / D2 PII guard); use NSubstitute for `ServiceBusClient`
- [X] T054 [US4] Create `tests/M2LB.Hendelse.BiRK.Integration/ErrorHandling/ErrorQueueIntegrationTests.cs`: AC3 — configure Hendelsestjenesten stub to always return 503; run adapter; assert Service Bus queue contains exactly one message after max retries; assert message JSON has `birkHendelsesId` field; AC2 — configure stub to return 422; publish two events; assert second event is processed (processing not halted by first 422)
- [X] T042 [US2] Create `tests/M2LB.Hendelse.BiRK.Integration/ChildResolution/TjenesteUnavailableTests.cs`: US2 AC3 (requires Phase 6 error queue — placed here) — configure Tjeneste stub to always return 503; assert adapter retries with backoff (verify ≥2 retry attempts); assert message published to Service Bus error queue after max retries; assert `BirkHendelsesId` present in queued message, raw payload absent

### Implementation for User Story 4

- [X] T055 [P] [US4] Create `src/M2LB.Hendelse.BiRK.Domain/ErrorHandling/IErrorQueuePublisher.cs` interface: `PublishAsync(string birkHendelsesId, string tabell, string correlationId, string exceptionSummary, CancellationToken ct)`
- [X] T056 [US4] Implement `src/M2LB.Hendelse.BiRK.Infrastructure/ServiceBus/ServiceBusErrorQueuePublisher.cs` implementing `IErrorQueuePublisher`: serialize only `birkHendelsesId`, `tabell`, `correlationId`, `exceptionSummary`, and UTC timestamp to JSON; publish to configured Service Bus queue via `ServiceBusClient` with `DefaultAzureCredential`; **MUST NOT** serialize `BirkCdcEvent.Payload` or any Involverte/EksternBeskrivelse content (GL-21 / GL-29)
- [X] T057 [US4] Register `ServiceBusClient` (DefaultAzureCredential, namespace from config) and `ServiceBusErrorQueuePublisher` in `Program.cs`
- [X] T058 [US4] Create `src/M2LB.Hendelse.BiRK.Domain/ErrorHandling/HendelsestjenesteValidationException.cs` non-retriable exception: `BirkHendelsesId` (string), `StatusCode` (int), `ResponseBody` (string) properties; NOT wired to Polly; placed in Domain layer consistent with `CodeMappingNotFoundException` (PP-07)
- [X] T059 [US4] Integrate `IErrorQueuePublisher` into `BirkEventProcessorWorker` and `HendelsestjenesteHttpClient`: on Polly max retries / `BrokenCircuitException` → call `IErrorQueuePublisher.PublishAsync` + log Error with `BirkHendelsesId` and `CorrelationId` (no payload/name fields); on `CodeMappingNotFoundException` → call `PublishAsync` + log Error; on `HendelsestjenesteValidationException` (422) → log Warning with `BirkHendelsesId`, `BirkTiltakPK`, HTTP status, and response body only — exclude `Involverte` (GL-29); continue to next event

**Checkpoint**: Service Bus receives a message when delivery exhausts all retries; 422 is logged and processing continues without interruption; unit tests T052–T053 pass; integration tests T054 and T042 pass

---

## Phase 7: User Story 3 — Full Historical Load on First Startup (Priority: P2)

**Goal**: First-run adapter replays all BiRK history from Event Hubs earliest offset before processing new events

**Independent Test**: Start adapter against a test Event Hub seeded with 10 historical CDC events and no existing checkpoint; verify all 10 events appear in Hendelsestjenesten stub calls after startup; verify checkpoint is written after completion

### Tests for User Story 3

> **Write this test skeleton first — it MUST fail until the historical load verification is in place.**

- [X] T060 [US3] Create `tests/M2LB.Hendelse.BiRK.Integration/HistoricalLoad/HistoricalLoadTests.cs`: AC1 — start adapter with no checkpoint blob against Azurite EventHub seeded with 5 events; assert all 5 events delivered and 5 `BirkHendelseRegistrering` rows exist in SQL; AC2 — seed 3 events with known `BirkTiltakPK` values; configure Tjeneste stub to return a `barnId` for those values; assert all 3 delivered ingestion requests have `BarnId` populated (not null); AC3 — re-run adapter against same 5 events; assert Hendelsestjenesten stub called again but stub returns 200 (idempotent); assert still 5 rows in registry (no duplicates); AC4 — simulate partial load (stub fails on event 3), restart adapter; assert adapter re-processes from event 1; assert events 1–5 eventually all delivered

### Implementation for User Story 3

- [X] T061 [P] [US3] Update `src/M2LB.Hendelse.BiRK.Adapter/code-mappings.json`: replace the Phase 1 placeholder UUID values (T009) with deterministic, hardcoded test UUIDs (use fixed `Guid` constants, e.g. `"11111111-0000-0000-0000-000000000001"` conventions) for every code value that appears in T060's seed events (`HjemmelType`, `TvangsProtokollStatusType`, `RommingKategoriType`); define these UUIDs as public constants in a shared `TestCodeMappings` class in `tests/M2LB.Hendelse.BiRK.Integration/` so T060 and T062 assertions can reference them by name rather than by magic string
- [X] T062 [P] [US3] Update `tests/M2LB.Hendelse.BiRK.Integration/EventStream/EventStreamProcessingTests.cs` (T025) and `tests/M2LB.Hendelse.BiRK.Integration/HistoricalLoad/HistoricalLoadTests.cs` (T060): replace any `!= null` or `!= Guid.Empty` UUID assertions for `HjemmelTypeId`, `TvangsProtokollStatusTypeId`, and `RommingKategoriTypeId` with equality assertions against the specific `TestCodeMappings` constants defined in T061; this ensures integration tests verify correct mapping, not just non-null presence
- [X] T063 [US3] Wire `BirkAdapterMetrics` (T065) into `BirkEventProcessorWorker` (T038): call `RecordEventProcessed(tabell, operasjon)` per event, `RecordDelivery(result, barnIdResolved)` on successful delivery, `RecordDeliveryError()` on Polly exhaustion / broken circuit, `RecordValidationDiscard()` on 422, `RecordErrorQueuePublish()` when `IErrorQueuePublisher.PublishAsync` is called, and call `UpdateStreamLag(seconds)` each event cycle. ⚠️ **Implement after T065** — `BirkAdapterMetrics` does not exist until Phase 8. Mark in-progress during Phase 7 planning; complete after T065 ships.

**Checkpoint**: Adapter replays from earliest offset on first startup with no checkpoint; historical events delivered; new events processed continuously thereafter; integration test T060 passes

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Correlation ID propagation, full metrics suite, Dockerfile, configuration hardening, quickstart validation

- [X] T064 [P] Create `src/M2LB.Hendelse.BiRK.Infrastructure/Http/CorrelationIdDelegatingHandler.cs`: `DelegatingHandler` that reads `CorrelationId` from the `ICorrelationIdAccessor` singleton defined in T021 (AsyncLocal-backed; **NOT** `IHttpContextAccessor`, which is unavailable in Worker Services); adds `X-Correlation-Id` header on all outgoing requests; `BirkEventProcessorWorker` sets the value via `ICorrelationIdAccessor.Set()` at the start of each event cycle (T038); register handler on both `HendelsestjenesteHttpClient` and `TjenesteHttpClient` in `Program.cs`
- [X] T065 [P] Implement `src/M2LB.Hendelse.BiRK.Adapter/Metrics/BirkAdapterMetrics.cs`: define the following using `System.Diagnostics.Metrics` — counter `events_processed` (tags: `tabell`, `operasjon`); counter `deliveries` (tags: `result`: Opprettet/Oppdatert/Uendret, `barnId_resolved`: true/false — required for SC-005 measurement); counter `delivery_errors`; counter `error_queue_publishes`; counter `validation_discards`; `ObservableGauge<double>` `stream_lag_seconds` (wall-clock UTC minus `EventData.EnqueuedTime` of last processed event). Expose public helper methods wrapping the counters: `RecordEventProcessed(string tabell, string operasjon)`, `RecordDelivery(string result, bool barnIdResolved)`, `RecordDeliveryError()`, `RecordValidationDiscard()`, `RecordErrorQueuePublish()`, and `UpdateStreamLag(double seconds)` — T063 depends on these exact names. Worker wiring is handled by T063 (run T063 after this task completes).
- [X] T066 [P] Configure health check OpenTelemetry export in `Program.cs`: register Azure Monitor health publisher so health status and last-read timestamp are exported to Azure Monitor (FR-018)
- [X] T067 [P] Create `Dockerfile` in repo root: multi-stage — `mcr.microsoft.com/dotnet/sdk:9.0` for build/publish; `mcr.microsoft.com/dotnet/aspnet:9.0` for runtime; copy published output; set `ENTRYPOINT ["dotnet", "M2LB.Hendelse.BiRK.Adapter.dll"]`
- [X] T068 Validate full quickstart.md flow: `dotnet restore` → `docker compose up -d` → `dotnet ef database update` → `dotnet run` → `curl /health` returns Healthy; confirm in CI pipeline script or `scripts/validate-quickstart.sh`; run full unit test suite (`dotnet test tests/M2LB.Hendelse.BiRK.Unit`) and confirm all pass
- [X] T069 Implement `error_queue_depth` observable gauge in `BirkAdapterMetrics.cs` (T065): register `Meter.CreateObservableGauge<long>("error_queue_depth")` that calls `ServiceBusAdministrationClient.GetQueueRuntimePropertiesAsync(queueName)` and returns `ActiveMessageCount`; register singleton `ServiceBusAdministrationClient` in `Program.cs` (namespace from `"ServiceBus:Namespace"` config key, `DefaultAzureCredential`); fulfils FR-017's "error queue depth" requirement — the `error_queue_publishes` counter alone does not expose current queue depth

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Requires Phase 1 completion — BLOCKS all user stories
- **US1 (Phase 3)**: Requires Phase 2 — 🎯 MVP increment
- **US2 (Phase 4)**: Requires Phase 2; integrates into Phase 3 pipeline — also P1, deliver with US1
- **US5 (Phase 5)**: Requires Phase 2; updates Phase 3 translators — deliver before US1 is considered complete
- **US4 (Phase 6)**: Requires Phase 3 pipeline to exist — adds resilience + error queue layer
- **US3 (Phase 7)**: Requires Phase 3 + Phase 4 (startup gate includes Tjeneste)
- **Polish (Phase 8)**: After all user story phases

### User Story Dependencies

- **US1 (P1)**: Start after Phase 2 — no story dependencies
- **US2 (P1)**: Start after Phase 2 — integrates into US1 worker; run in parallel with US1 implementation; note: T042 (US2 AC3 integration test — Tjeneste unavailable → error queue) depends on Phase 6 error queue infrastructure and is placed in Phase 6
- **US5 (P2)**: Depends on US1 domain types (T027–T030); updates translators T032–T033
- **US4 (P2)**: Depends on US1 delivery pipeline (T038) — adds error queue path to existing flow
- **US3 (P2)**: Depends on US1 (T037–T038) and US2 (startup readiness gate requires Tjeneste — enforced in T020)

### Within Each User Story

- Test skeletons first (write failing tests before implementation begins)
- Domain types (records, interfaces) before infrastructure implementations
- Infrastructure (HTTP clients, repositories) before Worker integration
- Worker integration last (assembles all pieces)

### Parallel Opportunities

- T003, T004, T005, T006: Project files within Phase 1
- T008, T009: Config files in Phase 1
- T012, T015, T016, T017, T018, T019: Foundational files in Phase 2
- T022, T023, T024: US1 test skeletons (different files)
- T027, T028, T029, T030: Domain types in Phase 3 (different files)
- T040, T041: US2 test skeletons (different files)
- T043, T044: Domain types in Phase 4
- T052, T053: US4 test skeletons (different files)
- T055, T058: Interface + exception in Phase 6
- T061, T062: Phase 7 code-mappings and test-assertion updates (different files)
- T064, T065, T069: Metrics and correlation handler (different files, different concerns)
- T066, T067: Health check OTel export and Dockerfile (independent)

---

## Parallel Example: US1

```
# Launch all US1 test skeletons together (write failing tests first):
T022: TvangsProtokollTranslatorTests.cs
T023: RommingTranslatorTests.cs
T024: BirkEventProcessorWorkerTests.cs

# Then launch all US1 domain types together (no dependencies between them):
T027: BirkCdcEvent record
T028: InngrepsInnmatingRequest DTO
T029: RommingsInnmatingRequest DTO
T030: InnmatingResultat enum

# Then proceed sequentially:
T031 → T032 → T033 (interface before implementation)
T034 (repository implementation)
T035 → T036 → T037 (HTTP client chain)
T038 (worker, assembles all above)
T025 → T026 (complete integration tests last)
```

---

## Implementation Strategy

### MVP First (US1 + US2 — both P1)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (blocks everything)
3. Write test skeletons for Phase 3 (T022–T026) — confirm they fail
4. Complete Phase 3: US1 pipeline (event read → translate → deliver → checkpoint)
5. Write test skeletons for Phase 4 (T040–T041) — confirm they fail (T042 is in Phase 6)
6. Complete Phase 4: US2 child resolution (integrate Tjeneste lookup into pipeline)
7. **STOP and VALIDATE**: All Phase 3–4 tests pass; publish test event; verify delivery with BarnId populated; check registry
8. US1 + US2 = functional adapter MVP

### Incremental Delivery

1. Phase 1 + Phase 2 → infrastructure ready
2. Phase 3 + Phase 4 → working adapter (MVP!) — all associated tests pass
3. Phase 5 (US5) → code mapping hardened — T048 passes
4. Phase 6 (US4) → error handling complete — T052–T054 pass
5. Phase 7 (US3) → historical load validated — T060 passes
6. Phase 8 → production-ready (observability, Docker, CI)

### Single Developer Strategy

Follow phase order. Write test skeletons at the start of each phase (mark as in-progress when writing, mark complete when tests pass after implementation). After T039 (end of Phase 3) the adapter is testable end-to-end. After T047 (end of Phase 4) BarnId resolution works. Remaining phases add resilience, correctness, and observability.

---

## Notes

- Tests MUST be written (skeleton) BEFORE implementation within each phase. Tests should fail on first run and pass after implementation — this is PP-09 compliance.
- [P] tasks operate on different files with no shared dependencies — safe to parallelize
- [Story] labels map directly to spec.md user story numbers (US1–US5)
- `ICorrelationIdAccessor` (T021, foundational phase) MUST use `AsyncLocal<string>` backing — `IHttpContextAccessor` does not work in Worker Services; T064 only wires the DelegatingHandler
- `ServiceBusErrorQueuePublisher` (T056) MUST NOT serialize `BirkCdcEvent.Payload` — store only IDs and exception summary (GL-21)
- 422 log entries (T059) MUST exclude `Involverte.EksternBeskrivelse` (contains RegAv free-text name) — log UUIDs only (GL-29)
- FR-011 "operational alert" is fulfilled by an Azure Monitor alert rule watching `error_queue_publishes > 0` — provision this rule in the operations team's infrastructure scripts; the adapter code only needs to emit the metric (T065)
- Commit after each phase checkpoint to maintain clean history
- EF migrations (T013) must be regenerated after any entity change
- `appsettings.Development.json` is gitignored; only the `.template` is committed
- The historical load (US3) requires Event Hub retention to cover full BiRK history — infrastructure configuration concern outside the adapter codebase
- `stream_lag_seconds` (T065) is defined as: wall-clock UTC minus `EventData.EnqueuedTime` of the last processed event, exported as a gauge
- T063 (metrics wiring) MUST be completed after T065 (BirkAdapterMetrics) — it cross-phases from Phase 7 into Phase 8 execution; the task is listed in Phase 7 for planning context but cannot be marked complete until Phase 8 T065 ships
- `error_queue_depth` gauge (T069) requires `ServiceBusAdministrationClient` which has a separate IAM role (`Azure Service Bus Data Owner` or equivalent management role) from the data-plane `ServiceBusClient` (T057) — verify the Container App Managed Identity has both roles assigned
- T061 test UUIDs MUST be shared constants (not magic strings repeated in each test file) — define them once in `TestCodeMappings.cs` and reference from T025, T026, T060, T062
