# Tasks: M2LB.Revisjon M01 — Receiving and storing leselogg events

**Input**: Design documents from `specs/001-M2LB.Revisjon-m01/`
**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅, quickstart.md ✅

**Test approach**: Tests are required by constitution Principle V and the test specification.
All acceptance scenarios have corresponding test IDs (TEST-U-*, TEST-I-*, TEST-E-*) and must be
covered before functionality is considered delivered.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to
- Exact file paths are included in each description

---

## Phase 1: Setup

**Purpose**: Create the .NET 10 solution, projects, and shared configuration

- [x] T001 Create .NET 10 solution `M2LB.Revisjon.sln` with five projects: `src/M2LB.Revisjon`, `src/M2LB.Revisjon.Domain`, `src/M2LB.Revisjon.Infrastructure`, `tests/M2LB.Revisjon.Unit`, `tests/M2LB.Revisjon.Integration`
- [x] T002 Add NuGet packages: `WolverineFx` 5.33.0 + `WolverineFx.AzureServiceBus` 5.33.0 to `src/M2LB.Revisjon`; `Azure.Storage.Blobs` 12.27.0 + `Azure.Messaging.ServiceBus` 7.20.1 + `Azure.Identity` 1.21.0 to `src/M2LB.Revisjon.Infrastructure`; `xunit.v3` 3.2.2 + `xunit.runner.visualstudio` 3.1.5 + `Shouldly` 4.3.0 + `Microsoft.AspNetCore.Mvc.Testing` 10.0.7 + `Testcontainers` 4.11.0 + `Testcontainers.Azurite` 4.11.0 to `tests/M2LB.Revisjon.Integration`; `xunit.v3` 3.2.2 + `xunit.runner.visualstudio` 3.1.5 + `Shouldly` 4.3.0 + `Microsoft.AspNetCore.Mvc.Testing` 10.0.7 to `tests/M2LB.Revisjon.Unit`
- [x] T003 [P] Add `.gitignore` entries for `appsettings.Development.json`, `publish/`, `deploy.zip`, and standard .NET build artefacts
- [x] T004 [P] Add `leselogg` queue to felles-compose — **completed manually**: removed existing `leselogg` topic entry from `servicebus-config.json` in the [felles-compose](https://dev.azure.com/Smidig/M2LB%20-%20Modulert%202.linje%20barnevern/_git/felles-compose) repo and added `leselogg` queue under `Queues` array; topic/queue coexistence conflict resolved

**Checkpoint**: Solution builds; `docker compose up -d` in felles-compose starts all services including the `leselogg` queue (local dev only — integration tests use Testcontainers)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Domain types, application skeleton, and DI wiring that all user stories depend on

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [x] T005 Create `LeseloggHendelse` record in `src/M2LB.Revisjon.Domain/LeseloggHendelse.cs` — seven fields matching GL-32: `HendelsesId` (Guid), `HendelsesTidspunkt` (DateTimeOffset), `BrukerId` (Guid), `BarnId` (Guid), `OperasjonNavn` (string), `Tjenestenavn` (string), `KorrelasjonId` (Guid)
- [x] T006 [P] Create `BlobPath` static helper in `src/M2LB.Revisjon.Domain/BlobPath.cs` — `Compute(LeseloggHendelse)` calls `HendelsesTidspunkt.ToUniversalTime()` and returns `$"{utc.Year}/{utc.Month:D2}/{utc.Day:D2}/{hendelse.HendelsesId}.json"`
- [x] T007 [P] Create `IBlobLeseLoggCreator` interface in `src/M2LB.Revisjon.Domain/IBlobLeseLoggCreator.cs` — single method `Task<bool> WriteAsync(string blobPath, byte[] rawJson, CancellationToken ct = default)`; returns `true` on write (HTTP 201), `false` on duplicate (HTTP 412); throws on transient failure
- [x] T008 Create `Program.cs` skeleton in `src/M2LB.Revisjon/Program.cs` using `WebApplication.CreateBuilder`; register `IBlobLeseLoggCreator` → `AzureBlobLeseLoggCreator` (scoped); add placeholder for Wolverine registration; call `app.Run()`
- [x] T009 [P] Create `appsettings.json` in `src/M2LB.Revisjon/` with `ServiceBus:ConnectionString`, `ServiceBus:QueueName` (`leselogg`), `BlobStorage:ConnectionString`, `BlobStorage:ContainerName` (`leselogg`) keys; create `appsettings.Development.json` with local emulator connection strings from quickstart.md (this file must be gitignored — verify T003 covers it)

**Checkpoint**: Solution compiles; DI wiring resolves at startup

---

## Phase 3: User Story 1 — Source service delivers a leselogg event (P1) 🎯 MVP

**Goal**: A valid `LeseloggHendelse` published to queue `leselogg` → file at UTC-derived path in Blob Storage → event acknowledged

**Independent Test**: Publish one valid `LeseloggHendelse` to the Service Bus emulator queue; verify a JSON file appears in Azurite at `{year}/{month}/{day}/{hendelsesId}.json` with content identical to the published message

### Tests for User Story 1

- [x] T010 [P] [US1] Create `BlobPathTests.cs` in `tests/M2LB.Revisjon.Unit/BlobPathTests.cs` with three Shouldly tests: TEST-U-01 (standard timestamp → `2026/03/12/...`), TEST-U-02 (midnight CET → UTC gives `2026/03/11/...` — note: test must expect `2026/03/11`, NOT `2026/03/12`; see research.md section 6), TEST-U-03 (zero-padded month/day → `2026/01/05/...`)
- [x] T011 [P] [US1] Create `LeseloggHendelseTests.cs` in `tests/M2LB.Revisjon.Unit/LeseloggHendelseTests.cs` — TEST-U-04: deserialise valid GL-32 JSON with all seven fields; assert all fields match expected values using Shouldly
- [x] T011b [P] [US1] Create `AzuriteContainerFixture` in `tests/M2LB.Revisjon.Integration/Fixtures/AzuriteContainerFixture.cs` using `Testcontainers.Azurite` — implements `IAsyncLifetime`; in `InitializeAsync`: construct `AzuriteContainer` via `new AzuriteBuilder().Build()`, call `await _container.StartAsync()`, retrieve connection string via `_container.GetConnectionString()`, construct `BlobContainerClient` for container `leselogg`, call `await containerClient.CreateIfNotExistsAsync()` (add code comment: "CreateIfNotExists is test-only — production container is pre-provisioned by infrastructure"); in `DisposeAsync`: call `await _container.StopAsync()` then `await _container.DisposeAsync()`; expose `ConnectionString` property returning `_container.GetConnectionString()` for use in blob-writing tests
- [x] T011c [P] [US1] Create `ServiceBusEmulatorFixture` in `tests/M2LB.Revisjon.Integration/Fixtures/ServiceBusEmulatorFixture.cs` using the generic `Testcontainers` `ContainerBuilder` API — no official `Testcontainers.AzureServiceBus` package exists; implements `IAsyncLifetime`; in `InitializeAsync`: (1) create and start a shared Docker network via `new NetworkBuilder().Build()`; (2) start SQL Server sidecar (`mcr.microsoft.com/mssql/server:2022-latest`, env `ACCEPT_EULA=Y` + `SA_PASSWORD=YourStrong!Passw0rd`, wait for port 1433, joined to the shared network); (3) start Service Bus emulator (`mcr.microsoft.com/azure-messaging/servicebus-emulator:latest`) on the same network, with `servicebus-config.json` copied into the container at `/ServiceBus_Emulator/ConfigFiles/Config.json` (embed file as `EmbeddedResource` in test project at `TestResources/servicebus-config.json`), wait for port 5672; in `DisposeAsync`: stop and dispose both containers, then dispose the network; expose `ConnectionString` property for use in Service Bus tests; the embedded `servicebus-config.json` must define two queues: `leselogg` (MaxDeliveryCount: 10, LockDuration: PT1M) and `leselogg-dlq-test` (MaxDeliveryCount: 2, LockDuration: PT5S)

### Implementation for User Story 1

- [x] T012 [US1] Implement `AzureBlobLeseLoggCreator` in `src/M2LB.Revisjon.Infrastructure/AzureBlobLeseLoggCreator.cs`: inject `BlobContainerClient`; call `blob.UploadAsync(BinaryData.FromBytes(rawJson), new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All }, HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" } }, ct)`; catch `RequestFailedException` where `ex.Status == 412` and return `false`; return `true` on success; let all other `RequestFailedException` propagate
- [x] T013 [US1] Implement `LeseloggHendelseHandler` in `src/M2LB.Revisjon/LeseloggHendelseHandler.cs`: **first**, write a spike test that publishes a known JSON payload to the SB emulator and logs both `envelope.Data` and the known bytes to confirm byte-level identity (see research.md section 3); if byte-identical, use `envelope.Data` directly; if not, fall back to `JsonSerializer.SerializeToUtf8Bytes(hendelse, new JsonSerializerOptions(JsonSerializerDefaults.Web))` and record the decision in research.md section 3; then implement the handler: `Handle(LeseloggHendelse hendelse, Envelope envelope, IBlobLeseLoggCreator lager, ILogger<LeseloggHendelseHandler> logger)`; open `logger.BeginScope` with `korrelasjonId = hendelse.KorrelasjonId`; call `BlobPath.Compute(hendelse)`; call `lager.WriteAsync(path, rawBytes)`; log processed or duplicate outcome
- [x] T014 [US1] Complete `Program.cs` Wolverine setup in `src/M2LB.Revisjon/Program.cs`: register `builder.Host.UseWolverine(opts => { opts.UseAzureServiceBus(...); opts.ListenToAzureServiceBusQueue("leselogg"); })`; inject `BlobContainerClient` into DI (constructed from config: connection string in dev, `DefaultAzureCredential` + URI in production)
- [x] T015 [US1] Create `BlobStorageTests.cs` in `tests/M2LB.Revisjon.Integration/BlobStorageTests.cs` — TEST-I-01: given unique event, when processed, then file exists at correct path in Azurite with byte-identical content; TEST-I-04: two events same date different IDs → two separate files, no collision
- [x] T016 [US1] Create `FullFlytTests.cs` in `tests/M2LB.Revisjon.Integration/FullFlytTests.cs` — TEST-E-01: publish valid `LeseloggHendelse` to Service Bus emulator queue; wait for processing; assert file in Azurite matches input exactly; TEST-E-02: publish 100 events; assert exactly 100 files in Azurite with correct paths

**Checkpoint**: Unit tests pass (no infrastructure). Integration tests start Testcontainers automatically and pass. A manually published message to the local dev emulator produces the correct blob.

---

## Phase 4: User Story 2 — Idempotent handling of a duplicate event (P1)

**Goal**: The same `LeseloggHendelse` delivered twice → exactly one file in Blob Storage → both deliveries acknowledged without error

**Independent Test**: Send the same event twice to the queue; verify exactly one file exists in Azurite and no errors are reported

### Tests for User Story 2

- [x] T017 [P] [US2] Add TEST-I-03 to `tests/M2LB.Revisjon.Integration/BlobStorageTests.cs`: given event already written, when received again, then `WriteAsync` returns `false` (no exception), no new file created, original file unchanged; use Shouldly assertions
- [x] T018 [P] [US2] Add TEST-I-03b to `tests/M2LB.Revisjon.Integration/BlobStorageTests.cs`: two concurrent `WriteAsync` calls with same `blobPath` (simulated via `Task.WhenAll`); assert exactly one file in Azurite and no exceptions; both calls complete without error — note: covers intra-process concurrency only; cross-instance idempotency is guaranteed by Blob Storage's atomic `IfNoneMatch` semantics at the storage layer and requires no additional application code

### Implementation for User Story 2

- [x] T019 [US2] Verify structured log entry in `LeseloggHendelseHandler` when `WriteAsync` returns `false` — log at `Information` level with message indicating idempotent duplicate and `hendelsesId` field; confirm `korrelasjonId` scope is present in the log entry

**Checkpoint**: Duplicate delivery produces no errors, no extra files. Verified by TEST-I-03 and TEST-I-03b.

---

## Phase 5: User Story 3 — Failures handled without event loss (P1)

**Goal**: Transient Blob Storage failures → in-process retry → eventual write; deserialization failures → DLQ with structured error log; DLQ routing triggers operational alert (via Azure Monitor — infrastructure config only)

**Independent Test**: Send an invalid JSON message to the queue; verify it lands in the DLQ within seconds. Send a valid event while simulating a Blob Storage failure; verify the event is eventually written after recovery.

### Tests for User Story 3

- [x] T020 [P] [US3] Add TEST-U-05 and TEST-U-06 to `tests/M2LB.Revisjon.Unit/LeseloggHendelseTests.cs`: TEST-U-05: `System.Text.Json.JsonSerializer.Deserialize<LeseloggHendelse>` on JSON missing `HendelsesId` throws `JsonException`; TEST-U-06: `Deserialize<LeseloggHendelse>` on invalid JSON throws `JsonException` — note: these tests verify the `LeseloggHendelse` schema contract only; Wolverine's own deserialization pipeline and SB DLQ routing are verified separately by T021 and T022 (integration tests through the actual Wolverine transport)
- [x] T021 [P] [US3] Create `ErrorHandlingTests.cs` in `tests/M2LB.Revisjon.Integration/ErrorHandlingTests.cs` — TEST-I-06: send non-JSON message to Service Bus emulator queue; wait; assert message appears in DLQ; assert subsequent valid event is processed without blocking
- [x] T022 [P] [US3] Add TEST-I-07 to `tests/M2LB.Revisjon.Integration/ErrorHandlingTests.cs`: send valid JSON missing required field to queue; assert message in DLQ; assert error log entry contains the error reason and is scoped with `korrelasjonId`
- [x] T023 [US3] Add TEST-I-05 to `tests/M2LB.Revisjon.Integration/ErrorHandlingTests.cs`: implement `FaultInjectingBlobLeseloggLager` in `tests/M2LB.Revisjon.Integration/Helpers/FaultInjectingBlobLeseloggLager.cs` — a decorator over `IBlobLeseLoggCreator` that throws `RequestFailedException` (status 503) on the first N calls then delegates to the real `AzureBlobLeseLoggCreator`; register this decorator in place of the real implementation for this test; publish a valid event; assert the handler retried (via log capture or invocation count); assert the blob was eventually written to Azurite after the fault threshold — avoids Docker lifecycle management and is CI-safe

### Implementation for User Story 3

- [x] T024 [US3] Configure Wolverine retry policy in `src/M2LB.Revisjon/Program.cs`: `opts.OnException<RequestFailedException>(ex => ex.Status >= 500 || ex.Status == 429).RetryWithCooldown(100.Milliseconds(), 500.Milliseconds(), 2.Seconds(), 10.Seconds())`; HTTP 412 must NOT be included (it is handled in `AzureBlobLeseLoggCreator` and never reaches this policy)
- [x] T025 [US3] Configure Wolverine dead letter handling in `src/M2LB.Revisjon/Program.cs`: with `SystemQueuesAreEnabled(false)`, Wolverine abandons SB messages after retry exhaustion, incrementing SB delivery count toward `MaxDeliveryCount`; after exhausting SB redeliveries, the SB service moves the message to `$DeadLetterQueue` — no application-layer DLQ call needed. MaxDeliveryCount: 10 confirmed in servicebus-config.json.
- [x] T025b [US3] Add integration test to `tests/M2LB.Revisjon.Integration/ErrorHandlingTests.cs`: use a dedicated short-lived test queue (`leselogg-dlq-test`) with `MaxDeliveryCount: 2` and `LockDuration: PT5S` defined in the embedded `servicebus-config.json` test resource (see T011c) — DO NOT reuse the `leselogg` queue for this test (keep test runtime under 30 seconds); publish a message that always throws in the handler; allow SB to exhaust 2 redeliveries; assert the message appears in the `$DeadLetterQueue` of the test queue; verify DLQ entry is accessible via `ServiceBusAdministrationClient` — confirms SB-native DLQ routing required for Azure Monitor alert (SC-004)
- [x] T026 [US3] Add structured error log entry in `src/M2LB.Revisjon/LeseloggHendelseHandler.cs`: try-catch in handler body logs at `Error` with `hendelsesId` and `errorReason` within the `korrelasjonId` scope before re-throwing (case 2: handler failure after successful deserialization). Case 1 (deserialization failure) is logged by Wolverine's built-in error handling; original SB `CorrelationId` is preserved in the DLQ entry and asserted in TEST-I-07.

**Checkpoint**: Invalid messages reach DLQ without halting the queue. Valid messages retry on transient failure. Log entries appear for DLQ events.

---

## Phase 6: User Story 4 — Operations team monitors service availability (P2)

**Goal**: `GET /health` returns the correct JSON format reflecting actual dependency availability; endpoint is internal only (not via YARP)

**Independent Test**: With Testcontainers providing Azurite and the Service Bus emulator, call `GET /health`; verify HTTP 200 with `{ "status": "Healthy", "checks": { "serviceBus": "Healthy", "blobStorage": "Healthy" } }`. Configure `BlobStorageHealthCheck` to point at a non-existent container; verify response shows `"blobStorage": "Unhealthy"`.

### Tests for User Story 4

- [x] T027 [P] [US4] Create `HelsesjekTests.cs` in `tests/M2LB.Revisjon.Integration/HelsesjekTests.cs` — TEST-E-03: given service running and both probes succeed, `GET /health` returns HTTP 200 with fully Healthy JSON (exact match using Shouldly)
- [x] T028 [P] [US4] Add TEST-E-04 to `tests/M2LB.Revisjon.Integration/HelsesjekTests.cs`: register a `BlobContainerClient` pointed at a non-existent Azurite container name in the test DI setup; `GetPropertiesAsync()` will throw `RequestFailedException` (404); assert `GET /health` returns HTTP 200 with `"status": "Degraded"` and `"blobStorage": "Unhealthy"`
- [x] T029 [P] [US4] Add TEST-E-05 to `tests/M2LB.Revisjon.Integration/HelsesjekTests.cs`: register a `BlobContainerClient` constructed with an invalid account key or a URI pointing to a non-existent Azurite account; `GetPropertiesAsync()` will throw a `RequestFailedException` (401 or connection error); assert `GET /health` returns HTTP 200 with `"blobStorage": "Unhealthy"` — note: this tests the health check's auth-failure path; Managed Identity itself is not testable in Azurite and is validated during App Service deployment
- [x] T029b [P] [US4] Add test to `tests/M2LB.Revisjon.Integration/HelsesjekTests.cs`: configure both `ServiceBusHealthCheck` and `BlobStorageHealthCheck` to throw in the test DI setup; assert `GET /health` returns HTTP 200 with `{ "status": "Degraded", "checks": { "serviceBus": "Unhealthy", "blobStorage": "Unhealthy" } }` — verifies top-level status aggregation when all checks fail simultaneously

### Implementation for User Story 4

- [x] T030 [US4] Implement `ServiceBusHealthCheck` in `src/M2LB.Revisjon.Infrastructure/ServiceBusHealthCheck.cs`: implements `IHealthCheck`; calls `ServiceBusAdministrationClient.GetQueueRuntimePropertiesAsync("leselogg")`; returns `Healthy` on success, `Unhealthy` on any exception
- [x] T031 [P] [US4] Implement `BlobStorageHealthCheck` in `src/M2LB.Revisjon.Infrastructure/BlobStorageHealthCheck.cs`: implements `IHealthCheck`; calls `BlobContainerClient.GetPropertiesAsync()`; returns `Healthy` on success, `Unhealthy` on any exception (including HTTP 403)
- [x] T032 [US4] Implement `HealthCheckResponseWriter` in `src/M2LB.Revisjon/HealthCheckResponseWriter.cs`: static `Task WriteResponse(HttpContext, HealthReport)` that serialises to `{ "status": "Healthy"|"Degraded", "checks": { "serviceBus": "Healthy"|"Unhealthy", "blobStorage": "Healthy"|"Unhealthy" } }` — `status` is `"Healthy"` only when all entries are `Healthy`, otherwise `"Degraded"`
- [x] T033 [US4] Register health checks and map `/health` in `src/M2LB.Revisjon/Program.cs`: `builder.Services.AddHealthChecks().AddCheck<ServiceBusHealthCheck>("serviceBus").AddCheck<BlobStorageHealthCheck>("blobStorage")`; `app.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = HealthCheckResponseWriter.WriteResponse })`

**Checkpoint**: `GET /health` returns correct JSON format. Probe failures surface as `"Unhealthy"` without throwing unhandled exceptions.

---

## Phase 7: User Story 5 — Legal owner documents audit trail validity (P2)

**Goal**: Verify WORM immutability enforcement and absence of personal data in stored events

**Independent Test**: Write a blob to Azurite with an immutability policy; attempt overwrite; confirm HTTP 412. Inspect `LeseloggHendelse` fields; confirm all are UUID or free-text metadata — no PII fields exist in the schema.

### Tests for User Story 5

- [x] T034 [P] [US5] Add TEST-I-02 to `tests/M2LB.Revisjon.Integration/BlobStorageTests.cs`: write blob to an Azurite container with a time-based immutability policy (test setup); attempt second `UploadAsync` to the same path WITHOUT `IfNoneMatch` (bypassing idempotency check); assert `RequestFailedException` with `Status == 412` — confirming WORM policy is enforced by storage layer independently of the service's `IfNoneMatch` logic
- [x] T035 [P] [US5] Add TEST-S-02 note to `tests/M2LB.Revisjon.Integration/SikkerhetsTests.cs`: document that TEST-S-02 (Managed Identity write-only access) requires production Managed Identity configuration — not testable in local emulator; mark as infrastructure validation task for deployment checklist

### Implementation for User Story 5

- [x] T036 [US5] Add an XML doc comment on `WriteAsync` in `src/M2LB.Revisjon.Infrastructure/AzureBlobLeseLoggCreator.cs` noting that WORM retention is enforced by infrastructure (not service code) per FR-013, and that the production container is pre-provisioned with no public access — this serves as a code-level reminder for future reviewers; verify the quickstart.md pre-deploy checklist includes confirming no public access on the container (infrastructure team responsibility)

**Checkpoint**: TEST-I-02 confirms WORM policy enforced independently of service `IfNoneMatch`. TEST-S-01 (YARP exclusion) documented as infrastructure config requirement.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Security test notes, deployment validation, final review

- [x] T037 [P] Create `SikkerhetsTests.cs` in `tests/M2LB.Revisjon.Integration/SikkerhetsTests.cs` with TEST-S-01 documented as an infrastructure test: `/health` not routed via YARP — document expected YARP configuration (no route matching `/health`) and how to verify in the deployment environment; mark as manual verification item for App Service deploy; also add a code-review checklist note for FR-014: grep confirms no service registry HTTP client is registered in DI at startup — this is a negative property verified by code inspection, not by runtime test
- [x] T038 [P] Validate quickstart.md end-to-end: `docker compose up -d` → `dotnet test tests/M2LB.Revisjon.Unit` → `dotnet test tests/M2LB.Revisjon.Integration`; fix any discrepancies found in quickstart.md
- [x] T039 Update `specs/001-M2LB.Revisjon-m01/checklists/requirements.md` to mark implementation complete; note Å-01 (WORM retention period) as the only remaining open item (infrastructure configuration, not blocking service delivery)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: No dependencies — start immediately
- **Phase 2 (Foundational)**: Depends on Phase 1 — **BLOCKS all user stories**
- **Phase 3 (US1)**: Depends on Phase 2 — MVP increment
- **Phase 4 (US2)**: Depends on Phase 3 complete (idempotency test requires the blob write path from US1)
- **Phase 5 (US3)**: Depends on Phase 3 complete (retry/DLQ wraps the same handler)
- **Phase 6 (US4)**: Depends on Phase 2 complete — can be worked in parallel with US1/US2/US3
- **Phase 7 (US5)**: Depends on Phase 3 complete (WORM test requires blob write infrastructure)
- **Phase 8 (Polish)**: Depends on all desired user stories complete

### User Story Dependencies

- **US1 (P1)**: Can start after Phase 2 — no dependencies on other stories — **deliver as MVP**
- **US2 (P1)**: Depends on US1 complete (adds tests on top of US1's blob path)
- **US3 (P1)**: Depends on US1 complete (configures Wolverine retry/DLQ over US1's handler)
- **US4 (P2)**: Independent — can proceed in parallel with US1/US2/US3 after Phase 2
- **US5 (P2)**: Depends on US1 complete (WORM test needs blob write working)

### Parallel Opportunities Within Phase 3 (US1)

```
After Phase 2:
  Launch in parallel:
    T010 — BlobPathTests.cs (no infrastructure needed)
    T011 — LeseloggHendelseTests.cs (no infrastructure needed)
    T011b — AzuriteContainerFixture.cs (Testcontainers.Azurite, starts Azurite container)
    T011c — ServiceBusEmulatorFixture.cs (generic ContainerBuilder, SQL Server + SB emulator)
    T012 — AzureBlobLeseLoggCreator.cs (infrastructure impl)

  Once T005–T007 + T012 are done:
    T013 — LeseloggHendelseHandler.cs

  Once T013 + T014 are done:
    T015 — BlobStorageTests.cs (integration, requires Azurite)
    T016 — FullFlytTests.cs (e2e, requires both emulators)
```

### Parallel Opportunities Within Phase 6 (US4)

```
After Phase 2:
  Launch in parallel:
    T030 — ServiceBusHealthCheck.cs
    T031 — BlobStorageHealthCheck.cs

  Once T030 + T031 done:
    T032 — HealthCheckResponseWriter.cs
    T033 — Program.cs health check registration

  Tests (T027, T028, T029, T029b) run after T033
```

---

## Implementation Strategy

### MVP: User Story 1 Only

1. Complete Phase 1 (Setup)
2. Complete Phase 2 (Foundational) — BLOCKS everything
3. Complete Phase 3 (US1) — core happy path
4. **STOP and VALIDATE**: `dotnet test` unit + integration; publish one event manually → verify blob
5. Deploy to App Service; verify Always-On is enabled; verify `/health` accessible internally

### Incremental Delivery After MVP

- Add US2 (idempotency verification tests) — zero new implementation; fast
- Add US3 (retry + DLQ) — Wolverine policy configuration + tests
- Add US4 (health check) — independent of US1–US3 beyond Phase 2
- Add US5 (WORM/compliance) — mostly tests and documentation

### Parallel Team Strategy

With two developers after Phase 2 completes:
- Developer A: US1 → US2 → US3 (event processing path)
- Developer B: US4 (health check) — fully independent

---

## Task Summary

| Phase | Tasks | Notes |
|---|---|---|
| Phase 1: Setup | T001–T004 | 4 tasks (T004 complete), T003+T004 parallelizable |
| Phase 2: Foundational | T005–T009 | 5 tasks, T006+T007+T009 parallelizable |
| Phase 3: US1 (MVP) | T010–T016 + T011b + T011c | 9 tasks, T010+T011+T011b+T011c+T012 parallelizable |
| Phase 4: US2 | T017–T019 | 3 tasks, T017+T018 parallelizable |
| Phase 5: US3 | T020–T026 + T025b | 8 tasks, T020+T021+T022 parallelizable |
| Phase 6: US4 | T027–T033 + T029b | 8 tasks, T027+T028+T029+T029b and T030+T031 parallelizable |
| Phase 7: US5 | T034–T036 | 3 tasks, T034+T035 parallelizable |
| Phase 8: Polish | T037–T039 | 3 tasks, T037+T038 parallelizable |
| **Total** | **43 tasks** (1 complete) | |

**Test IDs covered**: TEST-U-01 through TEST-U-06, TEST-I-01 through TEST-I-07, TEST-E-01 through TEST-E-05, TEST-S-01, TEST-S-02
