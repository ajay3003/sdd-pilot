# Research — Hendelsestjenesten

**Phase**: 0 | **Date**: 2026-04-27 | **Status**: Complete

All NEEDS CLARIFICATION items were resolved in the spec's clarification sessions (2026-04-24,
2026-04-27). This document records the technology decisions made for implementation.

---

## 1. C# / .NET 10 + ASP.NET Core 10

**Decision**: .NET 10 LTS, C# 14, ASP.NET Core 10 Minimal APIs or Controllers.
**Rationale**: .NET 10 is the current LTS (released November 2025, supported until November 2028);
Managed Identity and Azure SDK integration is first-class; Wolverine 3.x targets .NET 8+ and
is fully compatible with .NET 10.
**Alternatives considered**: .NET 8 LTS — still supported but superseded by .NET 10 LTS.

---

## 2. Immutable Versioned History — EF Core Approach

**Decision**: Table-per-hierarchy via separate `HendelsesVersjon` table with a FK to `Hendelse`.
EF Core 8 with `HasNoKey()` never applied to versjon table — every insert is append-only.
No `DbSet<T>.Remove()` calls permitted in the repository.
**Rationale**: Simplest EF Core pattern that enforces immutability at the ORM layer.
Domain invariant "no delete" enforced in repository — not in DB schema (avoids complex triggers).
**Alternatives considered**:
- Temporal tables (Azure SQL): Requires system-versioning on every table; read path becomes
  complex; rejected because immutability is a domain rule, not a DBA concern.
- Event sourcing: Overkill for a service with bounded write volume; rejected per constitution
  principle "no complexity beyond what the task requires."

---

## 3. Outbox Pattern — Wolverine Built-in

**Decision**: Wolverine's built-in transactional outbox via `Wolverine.Persistence.EntityFrameworkCore`.
Wolverine manages its own envelope tables (`wolverine_outgoing_envelopes`,
`wolverine_incoming_envelopes`) in the same Azure SQL database, co-located with domain tables.
Publishing a message inside a Wolverine message handler (or via `IMessageContext.PublishAsync`)
is automatically enrolled in the ambient EF Core transaction — no manual outbox table required.
The Wolverine sender daemon (internal background thread) delivers envelopes to Service Bus.
**Rationale**: Eliminates custom outbox infrastructure code; transactional guarantee (GL-33)
is enforced by the framework; retry + dead-letter handling (GL-23) is built-in.
**Alternatives considered**:
- Custom outbox table: Works but requires maintaining polling loop, retry logic, and
  dead-letter routing manually — replaced by Wolverine.
- MassTransit Outbox: Similar capability but larger dependency surface area.
- NServiceBus: Commercial licensing; rejected.

---

## 4. GraphQL — Hot Chocolate 14

**Decision**: Hot Chocolate 14 (ChilliCream) for the GraphQL read API.
**Rationale**: Best .NET GraphQL server; supports field-level authorization via `[Authorize]`
attributes and the `IAuthorizationHandler` interface; integrates cleanly with ASP.NET Core DI.
**Alternatives considered**:
- graphql-dotnet: Less actively maintained; missing field-level auth middleware.
- Strawberry Shake: Client library, not server; wrong layer.

**Key patterns**:
- Field-level auth: resolver returns `null` when caller lacks required operation permission
  (spec FR-05 and FR-06) — not an error, just an absent field.
- Leselogg published from resolver after successful data fetch (GL-32).

---

## 5. Authorization Integration

**Decision**: All 5 operations call `POST /api/autorisasjon/v1/evaluer` via typed HttpClient
registered with `AddHttpClient`. Fail-closed: HTTP 503 returned to caller when auth API is
unreachable (GL-25).
**Rationale**: GL-08 forbids local role checks. No cached auth decisions for security-critical paths.
**Alternatives considered**: Local policy cache — rejected; fail-closed requirement means stale
cache would block all access during auth outage, which is the correct behavior anyway.

---

## 6. Messaging — Wolverine + Azure Service Bus

**Decision**: Wolverine 3.x (`Wolverine` + `Wolverine.AzureServiceBus`) for all Service Bus
integration.
**Publisher**: `IMessageContext.PublishAsync<T>()` inside domain services or handlers; message
is written transactionally to Wolverine's outbox and delivered asynchronously by the sender daemon.
**Consumer**: Plain C# handler class with a `Handle(TjenesteOpprettet msg)` method — Wolverine
discovers and wires it automatically via convention. Idempotent processing is guaranteed by
Wolverine's inbox (incoming envelope deduplication via `wolverine_incoming_envelopes`).
Dead-letter queue routing for persistent failures is configured in `WolverineOptions` (GL-23).
**Managed Identity**: Wolverine.AzureServiceBus accepts `TokenCredential` — pass
`new DefaultAzureCredential()` (no connection string required in production).
**Rationale**: Wolverine replaces both the Azure.Messaging.ServiceBus manual subscriber loop
and the custom outbox publisher IHostedService — reduces boilerplate, centralises retry/DLQ
policy, and maintains all constitutional guarantees.
**Alternatives considered**: Direct `Azure.Messaging.ServiceBus` SDK — requires custom
IHostedService consumers and custom outbox; replaced by Wolverine for the reasons above.

---

## 7. OpenTelemetry

**Decision**: `OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Instrumentation.AspNetCore`
+ `OpenTelemetry.Instrumentation.Http` + `OpenTelemetry.Instrumentation.SqlClient`.
Export to Azure Monitor via `Azure.Monitor.OpenTelemetry.AspNetCore`.
**Rationale**: Platform requires OTel; Azure Monitor is the M2LB observability backend.
KorrelasjonsId propagated via W3C TraceContext headers and Activity.Current.

---

## 8. Structured Logging

**Decision**: Serilog with `Serilog.AspNetCore`, JSON output sink, enriched with
`CorrelationId`, `MachineName`, `Environment`.
No sensitive personal data in logs — UUIDs only (PS-08).

---

## 9. Integration Testing

**Decision**: Testcontainers for .NET + `testcontainers-dotnet/mssql` image.
Each test class spins up an isolated SQL Server container, runs EF Core migrations, then tears down.
**Rationale**: Avoids shared database state between test runs; tests real SQL behavior.
**Alternatives considered**: SQLite in-memory — rejected; does not support all Azure SQL
features (e.g., sequence types, certain index constraints).

---

## 10. Async BarnId Linking — Wolverine Message Handler

**Decision**: Wolverine message handler `TjenesteOpprettetHandler` on topic
`tjeneste.tjenester` subscription `hendelsestjenesten`.
On receiving `TjenesteOpprettet`: finds all `Hendelse` rows with matching `BirkTiltakPK`
and `BarnId = null`, sets `BarnId` and `TjenesteId`, saves, then publishes `HendelsesRegistrert`
for each via Wolverine's outbox — all in a single Wolverine-managed transaction.
Idempotency: Wolverine's inbox deduplication prevents double-processing of the same message.
**Rationale**: Replaces custom `IHostedService` consumer; Wolverine handles subscription
management, retry, and dead-letter routing without boilerplate.
**30-day alert**: A `ScheduledMessage` or a nightly cron-style Wolverine scheduled message
checks for `Hendelse` rows with `BarnId = null` older than 30 days and publishes an
operator alert via the outbox.
