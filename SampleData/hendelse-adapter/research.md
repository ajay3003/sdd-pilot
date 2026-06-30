# Research: BiRK Hendelsesadapter

All clarification questions were resolved during the `/speckit-clarify` phase. This document records the technology decisions and best-practices findings that inform the design.

---

## Event Hubs Consumer Pattern

**Decision**: `EventProcessorClient` with `BlobCheckpointStore`

**Rationale**: `EventProcessorClient` is the production-grade SDK class for event processing. It integrates natively with `BlobCheckpointStore` to persist partition checkpoints in Azure Blob Storage without custom logic (FR-007). Single-instance deployment (M01) means no competing-consumer coordination is needed. The SDK handles reconnection and resume-from-checkpoint automatically, satisfying SC-004.

**Alternatives considered**: `EventHubConsumerClient` (simpler but no built-in checkpointing), Kafka-compatible interface (unnecessary complexity). Both rejected.

---

## HTTP Resilience: Retry + Circuit Breaker

**Decision**: Polly v8 via `Microsoft.Extensions.Resilience` — `AddResilienceHandler` on named `HttpClient`

**Rationale**: GL-27 requires retry + exponential backoff + circuit breaker + explicit timeout on all outgoing HTTP calls. Polly v8's `ResiliencePipelineBuilder` composes `TimeoutResilience` → `RetryStrategy` → `CircuitBreakerStrategy` in the correct order. `AddResilienceHandler` integrates with `IHttpClientFactory`, avoiding repeated policy instantiation. Default retry parameters (10 retries, 5 s initial, 5 min max) are configurable via `IOptions` (FR-010).

**Alternatives considered**: Custom `DelegatingHandler` — too much boilerplate and harder to configure. `Refit` — adds a dependency for no gain over typed `HttpClient`.

---

## Azure SQL Access Pattern

**Decision**: EF Core (`Microsoft.EntityFrameworkCore.SqlServer`) with a minimal `DbContext` owning only `BirkHendelseRegistrering`

**Rationale**: EF Core is the standard data access pattern in M2LB services, ensuring consistency across the platform. The `BirkHendelseRegistrering` table is append-only with no complex queries; EF Core's overhead is negligible. Migrations are generated and applied at startup to ensure schema exists (no manual DDL scripts needed in CI).

**Alternatives considered**: Dapper — lighter but introduces a second ORM pattern diverging from M2LB conventions. ADO.NET — too much boilerplate for what is achievable with EF Core minimal API.

---

## Worker Service Host Configuration

**Decision**: `Microsoft.Extensions.Hosting.BackgroundService` + Kestrel health check endpoint

**Rationale**: `BackgroundService` is the idiomatic .NET host for long-running workers. Adding `app.MapHealthChecks("/health")` via `Microsoft.Extensions.Diagnostics.HealthChecks` provides the FR-018 health endpoint without a full ASP.NET API layer. The Worker project references `Microsoft.Extensions.Hosting.AspNetCore` to enable Kestrel only for the health endpoint.

**Alternatives considered**: Azure Functions (Isolated Worker) — added complexity for a simple continuous consumer; no meaningful advantage for low-volume single-instance workload. Pure `IHostedService` without health — rejected (FR-018 requires health endpoint).

---

## Correlation ID Strategy

**Decision**: Generate `Guid.NewGuid()` per CDC event as `CorrelationId`; propagate via `X-Correlation-Id` HTTP header on all outgoing calls; enrich Serilog log context.

**Rationale**: CDC events from Event Hubs do not carry M2LB correlation IDs. A new correlation ID must be generated at the point of event ingestion and threaded through all downstream calls (Tjeneste lookup, Hendelsestjenesten delivery) to satisfy GL-26. Serilog's `LogContext.PushProperty` provides scoped enrichment without manual passing.

**Alternatives considered**: Using the Event Hubs event's `SequenceNumber` or `Offset` as correlation key — insufficient uniqueness across partitions; not a UUID.

---

## Code Mapping Configuration

**Decision**: JSON file (`code-mappings.json`) loaded via `IConfiguration`; validated at startup using a fail-fast check; accessed via `IOptions<CodeMappingOptions>`.

**Rationale**: The clarification confirmed a static config file (JSON/YAML) loaded at startup with fail-fast validation (FR-006, spec assumption). `IConfiguration` with `IOptions<T>` is the idiomatic .NET pattern, supports file-based and Key Vault override, and integrates with the existing DI container.

**Alternatives considered**: Database table — unnecessary operational overhead for a static lookup updated only on M2LB code list changes. Embedded resource — harder to override per environment without redeployment.

---

## Azure SQL Authentication

**Decision**: `DefaultAzureCredential` via `Microsoft.Data.SqlClient` with `Authentication=Active Directory Default`

**Rationale**: Managed Identity authentication for Azure SQL is supported natively by `Microsoft.Data.SqlClient` using `DefaultAzureCredential` from `Azure.Identity`. No password in the connection string (FR-014, PS-02). Works transparently in local development (developer credential fallback) and on Azure Container App (system-assigned Managed Identity).

---

## Error Queue Pattern

**Decision**: Publish serialized event payload + metadata to Azure Service Bus queue using `ServiceBusClient` with `DefaultAzureCredential`.

**Rationale**: FR-011 requires messages exceeding max retries to be moved to an error queue. Azure Service Bus is the specified backend (spec clarification). The operations team re-queues by moving messages back to the main processing queue; the adapter processes re-queued messages via the same `EventProcessorClient` pipeline without code changes (spec).

---

## Startup Readiness Check

**Decision**: Custom `IHealthCheck` implementations for each dependency (Hendelsestjenesten, Tjeneste, Azure SQL); block `EventProcessorClient.StartProcessingAsync` until all checks pass.

**Rationale**: FR-009 requires the adapter to not begin stream processing until all dependencies are confirmed available. Health checks are also exposed via FR-018. Reusing `IHealthCheck` implementations for both startup gating and the health endpoint avoids duplicate connectivity logic.
