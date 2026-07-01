# Implementation Plan: BiRK Hendelsesadapter

**Branch**: `001-birk-hendelse-adapter` | **Date**: 2026-05-06 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `/specs/001-birk-hendelse-adapter/spec.md`

## Summary

Build a .NET Worker Service that reads CDC change events for `TvangsProtokoll` and `Rømming` BiRK tables from Azure Event Hubs, translates them to Hendelsestjenesten ingestion payloads (resolving `BarnId` via synchronous Tjeneste lookup and code values via a startup-loaded mapping file), and delivers via `PUT /api/hendelser/v1/innmating/inngrep/{id}` and `PUT /api/hendelser/v1/innmating/romming/{id}`. Retry uses exponential backoff (10 retries, 5 s–5 min); undeliverable events go to an Azure Service Bus error queue. Progress is checkpointed via `BlobCheckpointStore`; delivered events are tracked in an adapter-owned Azure SQL table (`BirkHendelseRegistrering`). The service runs as a single-instance Azure Container App using Managed Identity for all external connections.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: `Azure.Messaging.EventHubs.Processor`, `Azure.Messaging.ServiceBus`, `Microsoft.EntityFrameworkCore` + `Microsoft.EntityFrameworkCore.SqlServer`, `Polly` v8 (`Microsoft.Extensions.Resilience`), `Serilog` + `Serilog.Sinks.Console` (JSON), `OpenTelemetry` + `Azure Monitor Exporter`, `Microsoft.Extensions.Diagnostics.HealthChecks`  
**Storage**: Azure Blob Storage (`BlobCheckpointStore` — progress marker), Azure SQL Server (Delivered Event Registry), Azure Service Bus queue (error queue)  
**Testing**: xUnit, NSubstitute, `Microsoft.AspNetCore.Mvc.Testing` (integration), `Azure.Messaging.EventHubs.Tests` (test utilities)  
**Target Platform**: Azure Container App (single instance, M01); private VNet endpoints  
**Project Type**: .NET Worker Service (background service) — no user-facing API, health check exposed via Kestrel  
**Performance Goals**: Events delivered within 60 s of appearing in Event Hubs stream (SC-001); low steady-state volume (hundreds/day)  
**Constraints**: No stored credentials — Managed Identity for all connections (FR-014, PS-02); startup blocks until Event Hubs, Hendelsestjenesten, Tjeneste, and Azure SQL are reachable (FR-009); single instance (no horizontal scaling M01); all config from Azure Key Vault via Managed Identity (GL-28)  
**Scale/Scope**: Single instance; stream replayed from earliest offset on first startup for historical load (FR-008)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Gate | Status | Notes |
|------|--------|-------|
| PP-01 — API-first communication | ✅ PASS | Adapter calls Hendelsestjenesten and Tjeneste via published REST contracts only |
| PP-02 — Centralized access decision | ✅ PASS (N/A) | Adapter P-07 (M2LB Plattformkonstitusjon v4.0, §Adapter-spesifikke unntak) explicitly exempts CDC adapter services with no user-facing operations from authorization registration, provided all connections use Managed Identity (PS-02) |
| PP-03 — Immutable audit trail | ✅ PASS (N/A) | Audit trail managed by Hendelsestjenesten; adapter has no audit responsibility |
| PP-04 — Security classification | ✅ PASS (N/A) | Adapter relays data; no classification logic |
| PP-05 — Data has legal history | ✅ PASS | `BirkHendelseRegistrering` is an operational tracking table (append-only); no domain entities requiring soft-delete |
| PP-06 — Service autonomy | ✅ PASS | Adapter owns its own Azure SQL schema; no cross-service DB access |
| PP-07 — Business logic in domain layer | ✅ PLANNED | Translation and code mapping logic in `M2LB.Hendelse.BiRK.Domain`; Worker layer orchestrates only |
| PP-08 — Domain language in contracts | ✅ PASS | `BirkHendelsesId` and BiRK numeric codes remain in adapter layer; Hendelsestjenesten contracts use M2LB UUIDs |
| PP-09 — Spec and test inseparable | ✅ PLANNED | All acceptance scenarios from spec become test cases |
| PS-02 — Managed Identity | ✅ PASS | FR-014 explicitly covers all connections; no credentials stored |
| PS-04 — UUID v4 primary ID | ✅ PASS | Platform `HendelsesId` is UUID; `BirkHendelsesId` is adapter-internal only |
| PS-05 — Event Hubs for CDC | ✅ PASS | Core architecture uses `EventProcessorClient` against Event Hubs |
| PS-06 — Operations registration | ✅ PASS (N/A) | Exempted by adapter P-07 (same basis as PP-02 above — §Adapter-spesifikke unntak) |
| PS-08 — Structured logging + correlation_id | ✅ PLANNED | Serilog JSON + correlation ID generated per CDC event, propagated to all downstream HTTP calls |
| GL-26 — correlation_id propagation | ✅ REQUIRED | Correlation ID set per CDC event; added as HTTP header on all calls to Hendelsestjenesten and Tjeneste |
| GL-27 — Retry + circuit breaker | ✅ REQUIRED | Polly `ResiliencePipeline` with timeout + retry + circuit breaker on all outgoing HTTP calls |
| GL-28 — Config from Key Vault | ✅ REQUIRED | Connection strings, service URLs, and all secrets loaded from Azure Key Vault at startup via Managed Identity |
| GL-20 — Domain event publication | ✅ PASS (N/A) | Adapter performs no M2LB domain data mutations of its own; `BirkHendelseRegistrering` is adapter-internal operational tracking (append-only). GL-20 does not apply — no domain state originates from this service. |

**Post-design re-check**: No violations introduced. `BirkHendelseRegistrering` table is adapter-owned and append-only; does not require temporal validity (operational tracking, not a domain entity).

## Project Structure

### Documentation (this feature)

```text
specs/001-birk-hendelse-adapter/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output
│   ├── hendelsestjenesten-innmating.md
│   ├── tjeneste-birkoppslag.md
│   └── health-check.md
└── tasks.md             # Phase 2 output (/speckit-tasks — not created here)
```

### Source Code (repository root)

```text
src/
├── M2LB.Hendelse.BiRK.Adapter/       ← Worker entry point: Program.cs, IHostedService, health checks, DI wiring
├── M2LB.Hendelse.BiRK.Domain/        ← Translation logic, code mapping, entity models
└── M2LB.Hendelse.BiRK.Infrastructure/ ← EventHubs consumer, ServiceBus error queue, SQL registry, HTTP clients

tests/
├── M2LB.Hendelse.BiRK.Unit/          ← Unit tests: translation, code mapping, retry logic
└── M2LB.Hendelse.BiRK.Integration/   ← Integration tests: full pipeline with real/emulated infrastructure
```

**Structure Decision**: Three-project source layout following M2LB convention. The entry-point project is named `Adapter` (not `Api`) because it is a worker service with no public HTTP API — the health check endpoint is an operational concern of the host, not a service API. Domain layer isolates all BiRK→M2LB translation logic for independent testability.

## Complexity Tracking

No constitution violations requiring justification.
