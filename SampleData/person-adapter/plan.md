# Implementation Plan: BiRK Person-adapter

**Branch**: `001-birk-person-adapter` | **Date**: 2026-04-20 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/001-birk-person-adapter/spec.md`

## Summary

The BiRK Person-adapter is an Azure-hosted .NET 10 Worker Service that continuously consumes
person and child registration CDC change events from Azure Event Hubs (via Debezium CDC
pipeline), transforms them to PersonModule's domain format using a dedicated field-mapping
layer, and delivers them to PersonModule's REST ingestion API. It manages initial full load
(persons before child registrations), per-batch stream-position checkpointing to Azure Blob
Storage, Kode 6/7 security rejection with mandatory-acknowledgment alerting, a fault queue
(`feilkoe`) with 30-day auto-purge, and structured observability via OpenTelemetry +
Application Insights — all using Azure Managed Identity with zero stored credentials.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**:
- `Azure.Messaging.EventHubs.Processor` — `EventProcessorClient` for Event Hubs consumption + checkpoint
- `Azure.Identity` — `DefaultAzureCredential` for Managed Identity across all connections
- `Azure.Storage.Blobs` — `BlobCheckpointStore` for Event Hubs offset persistence
- `Microsoft.EntityFrameworkCore` + `Microsoft.EntityFrameworkCore.SqlServer` — `feilkoe` persistence
- `Microsoft.Extensions.Http.Resilience` — HTTP retry + circuit breaker (replaces deprecated Polly extensions)
- `Microsoft.Extensions.Diagnostics.HealthChecks` — `/helse/live` and `/helse/ready` endpoints
- OpenTelemetry + `Azure.Monitor.OpenTelemetry.Exporter` — metrics + distributed tracing to Application Insights
- `System.Diagnostics.Metrics` (`Meter`, `Counter<long>`, `Gauge<long>`) — custom operational counters

**Storage**:
- Azure Blob Storage — `EventProcessorClient` checkpoint container (offset per partition)
- Azure SQL — `feilkoe` (fault queue) table; Managed Identity auth via Access Token on `SqlConnection`

**Testing**: xUnit, `Testcontainers.MsSql` (integration), NSubstitute (unit mocking)
**Target Platform**: Azure (Container Apps or App Service, private VNet, no public endpoints)
**Project Type**: .NET Worker Service — background service; exposes `/helse/live`, `/helse/ready`, and internal admin endpoint only
**Performance Goals**: Steady-state CDC event delivered to PersonModule within 15 minutes; batch ingestion maximizes throughput for initial load (no SLA on initial load duration)
**Constraints**: Zero stored credentials (Managed Identity only); private network only; per-batch checkpoint; state limited to checkpoint (Blob Storage) + fault table (Azure SQL)
**Scale/Scope**: Single consumer group on Event Hubs; full load volume is all BiRK person records (hundreds of thousands to low millions); single adapter instance

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- [x] **P-01 / GL-16**: Field mapping confined to Domain layer; no BiRK field names in PersonModule API contracts; `EksternId` is the sole BiRK reference by deliberate design (migration handle per ADR-008)
- [x] **P-02**: No other service has runtime dependency on this adapter; decommission at Phase 2 requires only adapter removal — PersonModule API, domain model, and event contracts are unaffected
- [x] **P-03 / PP-04**: FR-006/FR-007: security level 2–3 records rejected before any processing, critical log entry written, mandatory-acknowledgment alert raised, stream advances past rejected record; never forwarded to PersonModule
- [x] **P-04 / GL-22**: PersonModule PUT is idempotent keyed on `eksternId`; 204 response on duplicate delivery (no change); same CDC event delivered twice yields identical outcome (SC-004)
- [x] **P-05 / PS-09**: Checkpoint in Azure Blob Storage; `feilkoe` table in Azure SQL; personal data (`payload` column) deleted on successful re-delivery and auto-purged after 30-day expiry (FR-016)
- [x] **P-07 / GL-23**: `Microsoft.Extensions.Http.Resilience` exponential backoff retry for transient failures; `feilkoe` (dead-letter) for exhausted retries; 429 handled with separate cool-down (no retry count consumed); no silent drops (SC-003)
- [x] **PS-02**: `DefaultAzureCredential` for Event Hubs, PersonModule HTTP client, Azure SQL (Access Token on `SqlConnection`), Blob Storage; Key Vault for Application Insights connection string
- [x] **GL-24 / PP-09**: Transformation logic, Kode 6/7 rejection, idempotency, fault queue logic in `M2LB.PersonBiRKAdapter.Unit`; end-to-end processing in `M2LB.PersonBiRKAdapter.Integration`

**All gates pass. No violations.**

## Open Items

| ID | Item | Blocking implementation? | Resolution path |
|----|------|--------------------------|-----------------|
| Å-01 | `birk-person-feltmapping.md` — exact BiRK-to-PersonModule field names for Person, Barn, and reference data | No — transformation interface designed; mapper classes filled in when document arrives | Deferred; may arrive as a follow-up feature |
| ~~Å-02~~ | ~~**Authentication method conflict**~~ — **Resolved**: PersonModule will update innmating endpoints to use Managed Identity. Constitution PS-02 compliance maintained. T013 implements `DefaultAzureCredential` bearer token handler as originally designed. | Closed |
| Å-03 | **Reference data GUID resolution** — `KjoennTypeId`, `BarnTypeId`, `BarnStatusTypeId`, `SikkerhetsnivaaTypeId` in PersonModule's API are Guids, not string codes. Adapter must resolve BiRK integer codes to PersonModule Guids. Resolution strategy (startup pre-load, runtime lookup, or fixed mapping) and any required PersonModule lookup API to be confirmed. | Yes — T018, T026 mapper implementations cannot be completed until resolved |

## Project Structure

### Documentation (this feature)

```text
specs/001-birk-person-adapter/
├── plan.md                          # This file
├── research.md                      # Phase 0 output
├── data-model.md                    # Phase 1 output
├── quickstart.md                    # Phase 1 output
├── contracts/
│   ├── health-api.md                # /helse/live + /helse/ready endpoints
│   ├── admin-api.md                 # Internal fault queue retry endpoint
│   └── personmodule-outbound.md     # PersonModule REST API the adapter calls
└── tasks.md                         # Phase 2 output (not created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
  M2LB.PersonBiRKAdapter.Worker/          ← Worker Service host, health + admin endpoints, DI wiring
  M2LB.PersonBiRKAdapter.Domain/          ← Transformation logic, Kode 6/7 rejection, idempotency interfaces
  M2LB.PersonBiRKAdapter.Infrastructure/  ← EventProcessorClient, PersonModule HTTP client, EF Core feilkoe

tests/
  M2LB.PersonBiRKAdapter.Unit/            ← Transformation, Kode 6/7, idempotency, fault queue logic
  M2LB.PersonBiRKAdapter.Integration/     ← End-to-end processing: Event Hubs → transform → delivery

.pipeline/                                ← Azure DevOps pipeline YAML
specs/                                    ← Spec-kit documents
```

**Structure Decision**: .NET multi-project solution per constitution §Repository & Code Structure and
GL-30. Worker hosts the process entry point; Domain contains all business logic testable without
Azure dependencies; Infrastructure wires Azure SDK clients. This separation ensures Unit tests run
without any Azure or network dependencies.

## Complexity Tracking

> All Constitution Check gates pass — no violations to justify.
