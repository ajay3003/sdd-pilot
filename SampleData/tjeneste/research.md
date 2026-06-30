# Research: Tjenestemodul M01

**Phase**: 0 — Outline & Research
**Branch**: `001-tjenestemodul-m01`
**Date**: 2026-04-13

All NEEDS CLARIFICATION items from Technical Context resolved.

---

## 1. Hot Chocolate v15 / .NET 10 GraphQL Patterns

**Decision**: Code-first with `AddTypes()` assembly scanning; `[Authorize]` attribute-based authorization; structured error union types for domain error codes.

**Rationale**: `AddTypes()` eliminates manual type registration and is the v15 recommended pattern — Hot Chocolate auto-discovers all `ObjectType<T>` and `QueryType` classes in the assembly. Attribute-based `[Authorize(policy: "...")]` integrates directly with Azure EntraID JWT bearer via `Microsoft.Identity.Web`. Error unions (e.g., a `TjenesteResult` type resolving to `Tjeneste | TjenesteError`) model domain errors cleanly in the schema without abusing GraphQL extensions.

**Breaking changes from v14 to v15**:
- .NET 8.0+ required (no issue — targeting .NET 10)
- `LocalDateType` now uses `DateOnly`; `LocalTimeType` uses `TimeOnly`
- `DataLoader` must use GreenDonut extension methods; manual DI registration no longer allowed
- Type interceptor `OnAfterCompleteTypes` hook behavior changed

**Alternatives considered**: Schema-first SDL approach — rejected; code-first provides compile-time safety and better DI integration with the rest of the .NET stack.

---

## 2. Transactional Outbox Library

**Decision**: Wolverine with EF Core + Azure Service Bus transport.

**Rationale**: Wolverine is the recommended greenfield choice for .NET 10 + Azure SQL + Azure Service Bus. It provides a native transactional outbox (events persisted to SQL in the same `DbTransaction` as domain writes, then relayed to Service Bus asynchronously by a background process), built-in Azure Service Bus transport, and a unified message handler model that reduces boilerplate. The alternative MassTransit also supports outbox via `MassTransit.EntityFrameworkCore` but has higher configuration ceremony. Custom implementation rejected — the relay process, dead-letter handling, and redelivery after restart are non-trivial to build correctly.

**Key configuration**: Enable Azure Service Bus duplicate detection via `MessageId` to handle idempotent republishing after relay failures.

**Alternatives considered**: MassTransit (more ceremony, similar capability), NServiceBus (commercial licensing), `SqlTransactionalOutbox` GitHub library (no built-in relay process, more maintenance).

---

## 3. EF Core 10 + Azure SQL

**Decision**: EF Core 10 with `SaveChangesInterceptor` for soft-delete enforcement; UUID v4 `Guid` PKs; two `DbContext` subclasses — one per schema.

**Rationale**: EF Core 10's `SaveChangesInterceptor` supports clean soft-delete enforcement without polluting entity classes. Separate `DbContext` per schema (`TjenesteDbContext` for the `tjeneste` schema, `BirkStagingDbContext` for `birk_staging`) enforces the schema isolation required by MP-03 and MP-05 at the code level, not just convention.

**Key patterns**:
- Named query filter on `TjenesteDbContext` excludes `BarnId = null` and `BarnLinkageStatus != Linked` placements from all queries (FR-003)
- `ExecuteUpdate` / bulk upsert by `BirkTiltakKey` for idempotent CDC writes (FR-012)
- `Guid` PKs generated client-side via `Guid.NewGuid()` — no database-generated GUIDs

---

## 4. Azure Event Hubs / Debezium CDC Consumer

**Decision**: `EventProcessorClient` in an ASP.NET `IHostedService`; Azure Blob Storage checkpoints; one Event Hub per BiRK table; batch checkpoint every 50 events.

**Rationale**: `EventProcessorClient` is the Azure SDK standard for long-running consumers with durable checkpointing. Separate Event Hubs per BiRK table provides operational isolation and independent consumer group management. Batching checkpoints every 50 events balances replay risk against throughput and satisfies FR-011 (resume from last checkpoint after downtime). One Blob Storage container provisioned per Event Hub + consumer group pair (must be pre-provisioned; `EventProcessorClient` does not auto-create containers).

**Debezium envelope deserialization**: Extract `payload.before`, `payload.after`, and `payload.op` fields. Route by `op`: `"c"` → insert, `"u"` → upsert, `"d"` → soft-delete.

**Full import on startup** (FR-010): A separate `BirkImportService` hosted service runs before the CDC processor starts. It queries BiRK snapshot data directly via a configurable endpoint, loads lookup tables first (orders → service types → status types → termination reasons), then placements. Completion sets a `birk_import_complete` flag in SQL. Subsequent startups check this flag — if set and checkpoint valid, skip to incremental mode (FR-011); if checkpoint expired, re-run full import.

**Alternatives considered**: Single Event Hub with header-based routing — rejected for operational complexity and risk of one slow consumer group blocking others.

---

## 5. BiRK Lookup Deferral (FR-012a)

**Decision**: Azure Service Bus message deferral + scheduled retry message.

**Rationale**: When a CDC placement message arrives before its referenced lookup table entry exists in `birk_staging`, defer the Service Bus message by sequence number (it becomes invisible to normal receive but remains in the queue). Publish a self-directed scheduled message with `ScheduledEnqueueTimeUtc` set to a configurable retry interval (default: 30 s). When the scheduled message fires, retrieve and process the deferred message. This approach is durable across restarts and leverages Service Bus's native `MaxDeliveryCount` dead-lettering for exhausted retries (FR-013).

**Alternatives considered**: In-memory retry queue — rejected; not durable across restarts and violates PS-09 (stateless). Polly retry on the consumer thread — rejected; blocks the consumer partition and does not survive service restarts.

---

## 6. Personmodulen HTTP Retry (FR-014)

**Decision**: Polly v8 `ResiliencePipeline` via `Microsoft.Extensions.Http.Resilience` (`AddStandardResilienceHandler()`).

**Rationale**: `AddStandardResilienceHandler()` (from `Microsoft.Extensions.Http.Resilience`) wraps `IHttpClientFactory` with a Polly v8 pipeline that includes retry with exponential backoff + jitter, circuit breaker, and timeout — configured in one call. After retries are exhausted (child not found or Personmodulen unreachable), the placement is stored with `BarnId = null` and `BarnLinkageStatus = Pending` without failing ingestion (FR-014).

---

## 7. Integration Testing

**Decision**: xUnit + Testcontainers.MsSql + Testcontainers.ServiceBus + `WebApplicationFactory<Program>`.

**Rationale**: Testcontainers spins up real SQL Server and Azure Service Bus emulator containers, eliminating mock/real divergence risk (especially for the transactional outbox and Service Bus consumer tests). `WebApplicationFactory` with `ConfigureTestServices` overrides environment-specific registrations (e.g., replaces `AutorisasjonsmodulClient` with a test double). Container startup (15–30 s for SQL Server) is amortized across test classes using `IAssemblyFixture`. Hot Chocolate v15 GraphQL queries are executed in tests via `HttpClient.PostAsJsonAsync` with a GraphQL JSON body — no dedicated test client needed.

**Alternatives considered**: Azurite — Azure Blob Storage emulator only, not Service Bus. In-memory fakes for Service Bus — risk of divergence from real broker behaviour for outbox relay tests.

---

## 8. .NET 10 Minimal API vs Controllers

**Decision**: Minimal API for all endpoints (health checks, internal lookup); Hot Chocolate `MapGraphQL()` for the GraphQL endpoint.

**Rationale**: .NET 10 minimal API is the recommended pattern for new services. The internal lookup endpoint (`GET /v1/internal/tiltak/{key}`) maps cleanly to a typed route handler with full DI support. `MapHealthChecks` covers the health endpoint. No MVC controller base classes are needed, reducing the dependency surface.
