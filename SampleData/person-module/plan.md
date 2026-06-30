# Implementation Plan: Person Module Core

**Branch**: `001-person-module` | **Date**: 2026-03-06 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `specs/001-person-module/spec.md`

---

## Summary

Build the Person Module — the M2LB platform's single source of truth for person identity
and child registration. The module exposes a dual API surface (GraphQL for the presentation
layer, REST for BiRK ingestion), publishes domain events via Azure Service Bus using the
outbox pattern, and enforces absolute security classification (Kode 6/7 invisibility) on
every data access. Phase 1: BiRK is data owner; all data flows in via CDC pipeline adapter.

---

## Technical Context

**Language/Version**: C# / .NET 10 (LTS, released Nov 2025)
**Primary Dependencies**:
- `HotChocolate.AspNetCore` 15.x — GraphQL server
- `Microsoft.EntityFrameworkCore.SqlServer` 10.x — ORM for Azure SQL
- `Azure.Messaging.ServiceBus` — Service Bus client (domain events, outbox, audit)
- `Microsoft.AspNetCore.OpenApi` — REST OpenAPI documentation
- `Polly` / `Microsoft.Extensions.Http.Resilience` — resilience for auth client
- `Serilog.AspNetCore` — structured JSON logging
- `xunit` + `Testcontainers.MsSql` + `Shouldly` + `NSubstitute` — testing

**Storage**: Azure SQL (EF Core 10, TDE enabled, Norway East region per FR-030)
**Testing**: xUnit 3 + TestContainers (SQL Server), `Microsoft.AspNetCore.Mvc.Testing` for API tests
**Target Platform**: Azure (containerized ASP.NET Core, VNet-isolated, no public IP per PS-03)
**Project Type**: Web service (ASP.NET Core)
**Performance Goals**: p95 < 2s for GraphQL search endpoint (SC-002)
**Constraints**:
- All data in Norway East Azure region (FR-030)
- Fail-closed auth: unreachable → HTTP 503, service stays up (FR-031)
- No hard deletes (PP-05)
- No personal data in logs or events (FR-026, PS-08)
- No local audit table (FR-028)
- Norwegian characters in all identifiers (class names, properties, method names, file names): `ø` → `oe`, `æ` → `ae`, `å` → `aa` (e.g., `KjønnType` → `KjoennType`, `Kjønn` → `Kjoenn`)

**Scale/Scope**: ~66,172 registered children; thousands per org unit; p95 < 2s search

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Requirement | Status |
|-----------|-------------|--------|
| PP-01 Contract-Driven | GraphQL + REST only; no direct DB access cross-service | ✅ PASS |
| PP-02 Centralised Access | All decisions via Auth module `POST /api/autorisasjon/v1/evaluer`; fail-closed (FR-031); no `IsInRole()` | ✅ PASS |
| PP-03 Immutable Audit | Audit events via Service Bus outbox → platform Audit service; no local audit table with DELETE rights (FR-028) | ✅ PASS |
| PP-04 Security Classification Absolute | Kode 6/7 filter in base SQL query (never post-filter); 404 not 403 for direct lookups; invisible in counts (FR-003) | ✅ PASS |
| PP-05 Data Has Legal History | No hard DELETEs; ErAktiv for deactivation; BarnStatusHistorikk append-only; municipalities deactivated not deleted | ✅ PASS |
| PP-06 Service Autonomy | Owns its own Azure SQL database; no shared DbContext; no cross-service JOINs | ✅ PASS |
| PP-07 Business Logic in Domain | Security logic, state machine, invariants in `PersonService.Domain`; API layer orchestrates only | ✅ PASS |
| PP-08 Domain Language | All contracts use Norwegian domain language; BirkId is secondary reference only; no legacy field names | ✅ PASS |
| PP-09 Spec + Test Inseparable | Every FR has acceptance scenarios; SC-003 security tests are runnable specifications | ✅ PASS |
| PS-01 EntraID | Auth validated by YARP reverse proxy; Person module reads validated claims | ✅ PASS |
| PS-02 Managed Identities | BiRK adapter → Managed Identity; auth client → Managed Identity; no stored credentials | ✅ PASS |
| PS-04 UUID v4 | PersonId, BarnRegistreringId are client-generated UUID v4; BirkId is secondary reference | ✅ PASS |
| PS-05 Service Bus + Event Hubs | Domain events → Service Bus Topics; session ordering (SessionId = entity UUID); idempotent consumers; dead-letter routing | ✅ PASS |
| PS-06 Operation Registration | 7 ops registered via IHostedService → queue `operasjonsregistrering` at startup (FR-029) | ✅ PASS |
| PS-07 API Versioning | REST at `/api/person/v1/...`; GraphQL schema evolution for non-breaking changes | ✅ PASS |
| PS-08 Observability | Serilog JSON; KorrelasjonsId per request; health endpoint; no personal data in logs | ✅ PASS |
| PS-09 Stateless | No in-memory state; DB + Service Bus for persistence | ✅ PASS |

**Result**: All gates PASS. No violations.

---

## Project Structure

### Documentation (this feature)

```text
specs/001-person-module/
├── plan.md              # This file
├── research.md          # Phase 0 — technology decisions
├── data-model.md        # Phase 1 — database schema and entity model
├── quickstart.md        # Phase 1 — developer setup guide
├── contracts/
│   ├── graphql-schema.graphql    # GraphQL SDL (implementation reference)
│   ├── events.md                 # Service Bus event contracts
│   └── auth-integration.md      # Authorisation module integration patterns
└── tasks.md             # Phase 2 output (/speckit.tasks command)
```

### Source Code (repository root)

```text
src/
├── PersonService.Api/
│   ├── GraphQL/
│   │   ├── Queries/               # Hot Chocolate query resolvers
│   │   ├── Mutations/             # Hot Chocolate mutation resolvers
│   │   ├── Types/                 # GraphQL type definitions
│   │   └── Authorization/         # GraphQL auth middleware
│   ├── Rest/
│   │   ├── Innmating/             # PUT /innmating/personer, PUT /innmating/barn, POST /innmating/batch
│   │   └── Drift/                 # GET /helse, GET /innmating/metrikker
│   ├── Middleware/
│   │   └── KorrelasjonsIdMiddleware.cs
│   ├── Program.cs
│   └── appsettings.json
│
├── PersonService.Domain/
│   ├── Entities/
│   │   ├── Person.cs
│   │   ├── BarnIAndrelinjeBarnevern.cs
│   │   └── BarnStatusHistorikk.cs
│   ├── ReferenceData/
│   │   ├── KjønnType.cs
│   │   ├── BarnType.cs
│   │   ├── BarnStatusType.cs
│   │   ├── SikkerhetsnivaaType.cs
│   │   └── Kommune.cs
│   ├── Services/
│   │   ├── BarnStatusTransitionService.cs   # ErForventetOvergang logic (FR-021)
│   │   └── SecurityClassificationService.cs # Kode 6/7 visibility rules (PP-04)
│   ├── Events/                              # Domain event payload records
│   └── Exceptions/
│       ├── PersonNotFoundException.cs
│       └── AuthorisasjonException.cs
│
├── PersonService.Application/
│   ├── BarnSearch/
│   │   └── BarnSearchService.cs            # US1: search with auth filter
│   ├── BarnProfile/
│   │   └── BarnProfileService.cs           # US2: profile with field-level auth
│   ├── GradertBarntilgang/
│   │   └── GradertBarntilgangService.cs    # US3: access management (FR-033)
│   ├── ReferenceData/
│   │   └── ReferenceDataService.cs         # US4: reference data queries
│   ├── Innmating/
│   │   └── InnmatingService.cs             # US5: ingestion (person + child)
│   └── Interfaces/
│       ├── IAutorisasjonClient.cs
│       ├── IMicrosoftGraphClient.cs
│       └── IPersonRepository.cs
│
└── PersonService.Infrastructure/
    ├── Persistence/
    │   ├── PersonDbContext.cs
    │   ├── Migrations/
    │   └── Repositories/
    │       ├── PersonRepository.cs
    │       └── BarnRepository.cs
    ├── Outbox/
    │   ├── OutboxMessage.cs
    │   └── OutboxPublisherHostedService.cs  # polls + publishes via Service Bus
    ├── ServiceBus/
    │   └── ServiceBusEventPublisher.cs      # writes to OutboxMessage table
    ├── Http/
    │   ├── AutorisasjonClient.cs            # typed HTTP client with Polly
    │   └── MicrosoftGraphClient.cs
    └── OperasjonsRegistrering/
        └── OperasjonsRegistreringHostedService.cs  # FR-029, PS-06

tests/
├── PersonService.Domain.Tests/
│   ├── BarnStatusTransitionServiceTests.cs  # ErForventetOvergang logic
│   └── SecurityClassificationServiceTests.cs
├── PersonService.Application.Tests/
│   ├── BarnSearchServiceTests.cs
│   └── InnmatingServiceTests.cs
├── PersonService.Integration.Tests/
│   ├── PersonIngestionTests.cs
│   ├── OutboxPatternTests.cs
│   └── SecurityFilterIntegrationTests.cs   # SC-003: zero false positives/negatives
└── PersonService.Contract.Tests/
    ├── GraphQLSchemaTests.cs
    └── EventPayloadTests.cs                 # SC-006: no personal data in events
```

**Structure Decision**: 4-project Clean Architecture (Domain → Application → Infrastructure → Api). The Domain project has zero external NuGet dependencies — pure C# records and interfaces. This enforces PP-07 at the compiler level: business logic cannot accidentally reference EF Core or Service Bus.

---

## Complexity Tracking

No constitution violations require justification.

---

## Implementation Phases

### Phase A — Foundation

1. Solution setup: 4 src projects + 4 test projects; NuGet references; Serilog + KorrelasjonsId middleware
2. Domain entities: Person, BarnIAndrelinjeBarnevern, BarnStatusHistorikk, all reference data entities with invariants
3. EF Core: PersonDbContext, entity type configurations, initial migration with full seed data
4. Outbox infrastructure: OutboxMessage entity; OutboxPublisherHostedService (poll → Service Bus publish)
5. Operation registration: OperasjonsRegistreringHostedService at startup (FR-029, PS-06)
6. Health endpoint: GET /api/person/v1/helse with DB + Service Bus status

### Phase B — Data Ingestion (US5)

1. InnmatingService: Person upsert (idempotent, FR-022); BarnIAndrelinjeBarnevern upsert; anomaly detection (FR-021); auto-create reference data (FR-018)
2. REST endpoints: PUT /innmating/personer, PUT /innmating/barn, POST /innmating/batch
3. Event publication via outbox: PersonOpprettet, PersonOppdatert, BarnRegistrert, BarnStatusEndret (with ErForventetOvergang), BarnKommuneEndret, BarnTypeEndret, SikkerhetsnivaaEndret (high-priority)
4. BarnStatusHistorikk: write history row in same transaction as status update (FR-012 backing)
5. Ingestion metrics: GET /api/person/v1/innmating/metrikker

### Phase C — Search & Profile (US1, US2)

1. AutorisasjonClient: typed HTTP client with Polly; batch SeGradertBarn check; fail-closed (FR-031)
2. BarnSearchService: SQL filter `WHERE (Nivaa < 2) OR (BarnRegistreringId IN @grants)` (PP-04, FR-003); pagination; address protection flag
3. BarnProfileService: full profile query; field-level national ID masking (FR-010); status history from BarnStatusHistorikk
4. GraphQL resolvers: soekBarn, hentBarn (with statusHistorikk), reference data queries
5. GraphQL authorization middleware: operation-based checks per FR-002, FR-008

### Phase D — Access Management (US3)

1. GradertBarntilgangService: pre-condition validation; self-assignment check (FR-015); Auth module grant call (FR-033)
2. GraphQL: tildelGradertBarntilgang mutation; hentGradertBarntilgang query
3. MicrosoftGraphClient: resolve display names for access list

### Phase E — Audit Events

1. Audit event publication: Revisjonshendelse via outbox to person.audit topic (FR-028)
2. Before/after state snapshots: serialized as JSON with UUIDs only (FR-026)

### Phase F — Test Coverage (PP-09)

1. SC-003: Kode 6/7 invisibility tests — zero false positives and false negatives
2. SC-005: Idempotency tests — double-ingestion = 1 record + 1 event
3. SC-004: Audit completeness — mutation count = audit event count in integration tests
4. SC-006: Event payload tests — no personal data field contains non-UUID strings
5. SC-007: Operation registration health check
