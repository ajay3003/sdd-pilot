# Research: BiRK Person-adapter

**Phase**: 0 — Outline & Research
**Feature**: BiRK Person-adapter
**Date**: 2026-04-20

---

## 1. Event Hubs Processing Strategy

**Decision**: `EventProcessorClient` from `Azure.Messaging.EventHubs.Processor`

**Rationale**: Azure's recommended production-grade Event Hubs client. Handles partition
ownership, lease management, load balancing across instances, and checkpoint storage via a
pluggable `BlobCheckpointStore`. The `ProcessEventAsync` handler receives `EventData` per call;
per-batch checkpointing is achieved by calling `UpdateCheckpointAsync` once after processing
each batch — after PersonModule confirms delivery.

**Alternatives considered**: `EventHubConsumerClient` (raw polling) — rejected because it
requires manual partition management and does not support distributed checkpointing.

**Source**: FK-1.2 implementation note explicitly recommends `EventProcessorClient`.

---

## 2. Checkpoint Persistence

**Decision**: Azure Blob Storage via `BlobCheckpointStore`

**Rationale**: Built into the `Azure.Messaging.EventHubs.Processor` SDK. Accessed via Managed
Identity (`BlobContainerClient` + `DefaultAzureCredential`). Lease and checkpoint files are
small blobs written per partition; automatic handling of partition rebalancing.

**Per-batch semantics**: `UpdateCheckpointAsync` called once per processed batch, after
PersonModule confirms delivery — never before confirmed delivery (FR-008, FK-1.2).

**Expiry detection**: When the saved offset is no longer within the Event Hub retention window,
`EventProcessorClient` raises a partition error. The adapter catches this, logs the condition,
raises an operational alert, and initiates full load (FR-011).

---

## 3. Fault Queue Storage

**Decision**: Azure SQL with EF Core (`Microsoft.EntityFrameworkCore.SqlServer`)

**Rationale**: Azure SQL is the established M2LB platform database (per Utviklingsretningslinjer
and BiRK constitution). Managed Identity authentication via Access Token on `SqlConnection` —
no password in connection string. EF Core migrations manage schema. SQL query capability
supports expiry purge (`utloper_tidspunkt`) and retry scheduling.

**Table name**: `feilkoe` (per FK-5.2 field definition)

**Personal data handling**: `payload` column deleted on successful re-delivery (FK-5.4) and
auto-purged after `utloper_tidspunkt` (30-day default, configurable per FR-016). Table is
covered by Azure SQL TDE and private endpoint (FK-5.4, FK-9.3).

---

## 4. HTTP Resilience

**Decision**: `Microsoft.Extensions.Http.Resilience` — `AddResilienceHandler` on `HttpClient`

**Rationale**: Recommended approach in current .NET; `Polly.Extensions.Http` is deprecated.
Provides retry (exponential backoff + jitter), circuit breaker, and timeout as a composable
pipeline registered in DI. Three distinct error paths are wired separately:

| Error type | Handling |
|------------|----------|
| 5xx / timeout | Retry with exponential backoff (max 3 attempts, 5s base, jitter enabled); on exhaustion → `feilkoe` |
| 429 (rate limit) | Separate cool-down pause; retry count NOT consumed; processing resumes after cool-down (FR-013) |
| 422 (validation) | Bypass retry entirely; write to `feilkoe` immediately (FK-5.3) |

**Configuration**: All retry parameters (max attempts, backoff intervals, cool-down period) are
operational configuration — not hardcoded (per Assumptions section in spec.md).

**Source**: FK-5.1 implementation note confirms `Microsoft.Extensions.Http.Resilience`.

---

## 5. Metrics and Observability

**Decision**: OpenTelemetry + `Azure.Monitor.OpenTelemetry.Exporter`; custom metrics via
`System.Diagnostics.Metrics.Meter`

**Rationale**: `Microsoft.ApplicationInsights.WorkerService` SDK 2.x is deprecated. OpenTelemetry
is the platform-standard observability approach. `Meter` + `Counter<long>` / `Gauge<long>` from
.NET's built-in metrics API are captured automatically by OpenTelemetry and exported to
Application Insights. `ILogger` structured logging and HTTP calls to PersonModule are
auto-instrumented.

**Application Insights connection string**: Retrieved from Azure Key Vault at startup via
`DefaultAzureCredential` (not stored in `appsettings.json`).

**Alert rules**: Configured by operations team via Bicep — not in application code (FK-7.2).

**Operational metrics exposed** (FR-018):
- Events processed per record type (Person, Barn, reference data)
- Delivery outcomes per type (created, updated, unchanged)
- Fault queue depth (`feilkoe` row count)
- Kode 6/7 rejection count (critical — must remain zero in normal operation)
- Stream lag (estimated offset delta from Event Hubs latest)
- Initial load progress (records processed / total estimated)

**Source**: FK-7.2 implementation note confirms OpenTelemetry + `Azure.Monitor.OpenTelemetry.Exporter`.

---

## 6. Health Check Endpoints

**Decision**: `Microsoft.Extensions.Diagnostics.HealthChecks` with cached readiness checks

**Rationale**: Platform standard for .NET health checks (FK-7.3, ADR-026). Separate
`IHealthCheck` implementations for Event Hubs, PersonModule API, and `feilkoe` table.
Readiness check results cached for 15 seconds — avoids synchronous network calls per request.

**Endpoint paths** (Norwegian, per FK-7.3 and ADR-026):
- `GET /helse/live` — liveness; always `Frisk` if process is running; no auth required
- `GET /helse/ready` — readiness; reports dependency status; no auth required

**Status mapping**: `Healthy` → `Frisk`, `Degraded` → `Degradert`, `Unhealthy` → `Utilgjengelig`

**Routing**: MUST NOT be routed via YARP gateway.

**Source**: FK-7.3 implementation note confirms `Microsoft.Extensions.Diagnostics.HealthChecks`.

---

## 7. Background Fault Queue Processor

**Decision**: `Microsoft.Extensions.Hosting.BackgroundService` + `PeriodicTimer`

**Rationale**: Integrates cleanly with .NET Worker Service host model. `PeriodicTimer` avoids
timer drift and is lifecycle-managed. Polling interval from configuration (default 5 minutes).
Same resilience pipeline as FK-5.1 reused for re-delivery attempts. Alert auto-resolves when
`feilkoe` is empty (FK-8.2, US5 scenario 4).

**Source**: FK-6.1 implementation note confirms `BackgroundService` + `PeriodicTimer`.

---

## 8. Admin Endpoint

**Decision**: Internal `POST /admin/feilkoe/reprosesser` — not exposed via public gateway

**Rationale**: FR-017 requires authenticated, non-gateway-accessible endpoint for triggering
immediate fault queue re-processing. Managed Identity authentication (service-to-service only).

---

## 9. Testing Strategy

**Decision**: xUnit + `Testcontainers.MsSql` (integration) + NSubstitute (unit)

**Unit tests** (`M2LB.PersonBiRKAdapter.Unit`) — no external dependencies:
- Transformation logic per `IPersonMapper` / `IChildRegistrationMapper` interface
- Kode 6/7 rejection: level 2 or 3 input → no PersonModule call, critical log, alert
- Idempotency: duplicate CDC event → same outcome as first delivery
- Fault queue entry creation: fields populated correctly, expiry calculated

**Integration tests** (`M2LB.PersonBiRKAdapter.Integration`) — real SQL via Testcontainers,
mocked Event Hubs and PersonModule HTTP:
- End-to-end event processing pipeline
- Initial full load ordering: persons before child registrations
- Fault queue: delivery failure → `feilkoe` created → re-delivery succeeds → `feilkoe` cleared
- Checkpoint: `UpdateCheckpointAsync` called after delivery, not before

---

## 10. Field Mapping (Deferred — Å-01)

**Status**: Deferred. `birk-person-feltmapping.md` not yet available.

**Design approach**: `IPersonMapper` and `IChildRegistrationMapper` interfaces in Domain layer;
concrete implementations inject the field mapping. When the document arrives, only the concrete
mapper classes are modified — no infrastructure or contract changes required. The processing
pipeline, fault queue logic, health checks, and all other components are unaffected.

**Known mapping rules** (from spec and constitution, independent of Å-01):
- BiRK PersonPK → `eksternId` (person identity key in PersonModule)
- BiRK BirkID → `birkId` (child registration key)
- Composite status values (e.g. "Bestilling/Under Behandling") → passed through unchanged
- Null fields (unborn children, EMA) → null values in delivery request (accepted by PersonModule)
- Security level: adapter only maps levels 0 and 1; levels 2–3 rejected before mapping
