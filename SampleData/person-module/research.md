# Research: Person Module Core

**Branch**: `001-person-module` | **Date**: 2026-03-06
**Status**: Complete — all NEEDS CLARIFICATION items resolved

---

## 1. Technology Stack

### Decision: Runtime & Language
**Decision**: C# / .NET 10 on ASP.NET Core
**Rationale**: The dev guidelines explicitly name the M2LB platform as built on Azure Cloud with .NET. .NET 10 is the current LTS release (released Nov 2025) — the right choice for a new platform that will run in production for several years. LTS guarantees 3 years of support, aligning with the M2LB roadmap.
**Alternatives considered**: .NET 9 (STS) — shorter support window (18 months); not appropriate for a production child welfare platform.

---

### Decision: GraphQL Server
**Decision**: **Hot Chocolate 14** (ChilliCream / `HotChocolate.*` NuGet packages)
**Rationale**:
- De facto standard for enterprise .NET GraphQL servers
- Full SDL (schema-definition-language) support — the authoritative schema in `docs/person-graphql-sdl.graphql` can be used directly
- Built-in field-level authorization middleware (critical for FR-010 national ID masking and security level filtering)
- First-class pagination support (`UseOffsetPaging`, `UsePaging`)
- Custom scalar support for `DateTime`, `Date`, `JSON`
- Active development, excellent .NET 9 support

**Alternatives considered**:
- Strawberry Shake — client-side focus, not a server library
- graphql-dotnet — older, less actively maintained, fewer enterprise features

---

### Decision: REST API Framework
**Decision**: **ASP.NET Core Minimal API** with `Microsoft.AspNetCore.OpenApi`
**Rationale**: The REST surface is limited to 5 endpoints (3 ingestion + health + metrics). Minimal API reduces boilerplate. The authoritative OpenAPI spec is in `docs/person-rest-openapi.txt` — endpoints are implemented directly against it.
**Alternatives considered**: Controller-based API — adds unnecessary ceremony for 5 endpoints.

---

### Decision: Database ORM
**Decision**: **Entity Framework Core 10** with Azure SQL (via `Microsoft.EntityFrameworkCore.SqlServer`)
**Rationale**: Standard M2LB platform choice (dev guidelines reference Entity Framework). EF Core 9 provides strong support for UUID PKs, JSON columns, and the append-only BarnStatusHistorikk table.
**Key configuration**:
- UUID v4 generated client-side (not database auto-increment) per PS-04
- TDE enabled on Azure SQL per security requirements
- `EnableSensitiveDataLogging = false` in production (constitution forbids logging personal data)

---

### Decision: Outbox Pattern Implementation
**Decision**: **Custom EF Core outbox table + `IHostedService` poller**
**Rationale**:
- MassTransit adds significant abstraction overhead; the constitution prefers minimum necessary complexity
- Direct control over Azure Service Bus session IDs (required per FR-027) and message priority (required for `SikkerhetsnivåEndret`)
- The outbox table is written within the same EF Core `SaveChangesAsync()` transaction as the domain mutation — guaranteed atomicity
- A hosted service polls every 1–2 seconds, publishes pending messages, marks them as delivered

**Table**: `OutboxMessage` (see data-model.md for schema)
**Alternatives considered**:
- MassTransit Transactional Outbox — mature but heavy; session/priority configuration is more complex via abstraction layer
- CAP (DotNetCore.CAP) — popular but adds another top-level dependency; overkill for this service's event volume

---

### Decision: Service Bus Client
**Decision**: **Azure.Messaging.ServiceBus** NuGet package
**Rationale**: Official Azure SDK, first-class session sender support, high-priority message labelling via `ServiceBusMessage.Subject` and custom properties.
**Session configuration**: `SessionId = entity UUID` (PersonId for person events, BarnRegistreringId for child events)
**Priority**: `SikkerhetsnivåEndret` messages set `Subject = "CRITICAL"` and custom property `Priority = "High"` — subscribers can filter or prioritise accordingly.

---

### Decision: Resilience
**Decision**: **Polly 8** (via `Microsoft.Extensions.Http.Resilience`)
**Rationale**: Standard .NET resilience library. Required by constitution for all outgoing HTTP calls.
**Configuration for Authorisation module client**:
- Retry: 2 retries with exponential backoff (50ms, 100ms) — fast fail to meet SLA
- Timeout: 500ms per attempt (3 × 500ms max = 1.5s, leaves headroom for 2s p95 SLA)
- Circuit breaker: opens after 5 consecutive failures in 30s window
- Fail-closed: if circuit is open or retries exhausted → `AuthorisationException` → HTTP 503 to caller

---

### Decision: Structured Logging
**Decision**: **Serilog** with JSON output to stdout + Azure Monitor sink
**Rationale**: Explicitly mentioned in dev guidelines. JSON structured logging enables correlation-ID-based tracing across services (PS-08).
**Sensitive data**: `Destructure.ByIgnoring` configured to never log Fødselsnummer, Navn, DUF-nummer fields.

---

### Decision: Testing Stack
**Decision**: **xUnit 3 + TestContainers**
**Packages**:
- `Testcontainers.MsSql` — spins up real SQL Server for integration tests
- `Azure.Messaging.ServiceBus` test harness / fake for unit tests
- `Microsoft.AspNetCore.Mvc.Testing` for API-level integration tests
- `Shouldly` for readable assertions
- `NSubstitute` for mocking (lighter than Moq)

**Test categories**:
1. **Domain unit tests** — pure business logic, no I/O
2. **Application unit tests** — use cases with mocked infra (auth client, Service Bus)
3. **Integration tests** — EF Core + TestContainers SQL Server; outbox → Service Bus flow
4. **GraphQL contract tests** — validate schema matches SDL, field-level auth behaves correctly

---

### Decision: Authorization Integration Pattern
**Decision**: Typed `IAutorisasjonClient` HTTP client calling `POST /api/autorisasjon/v1/evaluer`

**Search authorization strategy** (O(1) Auth calls per search, not O(n)):
1. Fetch user's effective `Person:SeGradertBarn` grants from Auth module (returns list of child UUIDs)
2. Apply SQL query: `WHERE (SikkerhetsnivåNivå < 2) OR (BarnRegistreringId IN @grantedChildIds)`
3. This avoids N+1 Auth calls for paginated search results

**Profile/mutation authorization**: One call per request to confirm operation for specific child.

**Fail-closed behavior** (FR-031):
- If Auth module unreachable: throw `AuthorisasjonException` → HTTP 503
- No caching of auth decisions — each request calls the Auth module fresh
- Circuit breaker prevents cascading failures during Auth module outage

---

### Decision: Application Layer Pattern
**Decision**: **Direct application service classes** — no MediatR
**Rationale**: MediatR adds an indirection layer (request/handler/pipeline) that is valuable for cross-cutting concerns in large applications. For this service, the domain logic is well-encapsulated in domain services; the application layer is thin orchestration. Direct injection is simpler, fully traceable, and avoids premature abstraction.
**Structure**: One application service class per user story area (e.g., `BarnSearchService`, `BarnProfileService`, `GradertBarntilgangService`, `InnmatingService`).

---

## 2. Resolved Architecture Decisions

| # | Decision | Resolution |
|---|----------|------------|
| R-01 | Who stores audit records? | Platform-level Audit service (separate); Person module publishes to dedicated Service Bus topic via outbox. No local audit table. |
| R-02 | BarnStatusHistorikk persistence? | Dedicated append-only DB table. One row per transition. Written in same transaction as status update. |
| R-03 | Auth call for search | Batch: fetch all SeGradertBarn grants for user → apply in SQL WHERE clause |
| R-04 | Person:BarnIAndrelinjeBarnevern cardinality | 1:1. PersonId has UNIQUE constraint on BarnIAndrelinjeBarnevern table. Re-registration → UPDATE, not INSERT. |
| R-05 | DUF → fødselsnummer upgrade | UPDATE Person in-place; DUF retained; PersonOppdatert published via outbox |
| R-06 | US3 access grant orchestration | Person module calls Auth module; presentation layer calls Person module only (PP-01) |
| R-07 | ErForventetOvergang logic | Known expected transitions seeded from BarnStatusType domain service; any other transition = false |
| R-08 | Service Bus session ordering | SessionId = entity UUID (PersonId for person events, BarnRegistreringId for child events) |
| R-09 | Operation registration | IHostedService at startup → Service Bus queue `operasjonsregistrering` → 7 operations |
| R-10 | Search p95 SLA | Index on `BarnIAndrelinjeBarnevern.PersonId`, `BirkId`, `BarnStatusTypeId`, `SikkerhetsnivåTypeId`. Full-text index on `Person.Navn` for partial-name search. |

---

## 3. Security Architecture

### Kode 6/7 Invisibility Implementation

The absolute invisibility requirement (PP-04, FR-003) is implemented at the **query level**, not as a post-filter:

```sql
-- Search query includes security filter in the base query
SELECT b.* FROM BarnIAndrelinjeBarnevern b
  JOIN Person p ON b.PersonId = p.PersonId
  JOIN SikkerhetsnivaaType s ON b.SikkerhetsnivaaTypeId = s.SikkerhetsnivaaTypeId
WHERE
  -- Text/ID search conditions ...
  AND (
    s.Nivaa < 2                                          -- Nivå 0 or 1: general access
    OR b.BarnRegistreringId IN @userGradertChildIds      -- Kode 6/7: only explicit grants
  )
```

This means Kode 6/7 children **never appear in result sets or count queries** for unauthorised users. The 404-not-403 pattern (section 5.2 of API contracts) for direct lookups is enforced in the application service layer.

### National ID Masking
Implemented as a **Hot Chocolate field-level resolver**:
- `foedselsnummer` resolver checks if requesting user holds `Person:SeFullIdentitet` for this child
- Returns `null` or `***` if not authorised — no separate query needed; the authorization check is part of the resolver

---

## 4. Data Volume & Performance Estimates

From `docs/sikkerhetsgraderte-barn-forklaring.md`:
- ~65,300 children at Nivå 0 (no restriction)
- ~720 children at Nivå 1 (hidden address)
- ~22 children at Kode 7
- ~130 children at Kode 6

**Total registered children**: ~66,172

For search p95 < 2s at this scale:
- Azure SQL is well-suited for 66K rows with proper indexing
- The auth module call (500ms timeout) is the dominant latency factor
- Connection pooling + EF Core compiled queries keep DB time < 200ms
- Total budget: 500ms auth + 200ms DB + overhead ≤ 2000ms p95

---

## 5. Phase 2 Considerations (Design-time awareness)

Per the constitution's "Transition Architecture Awareness":
- All domain entities are designed to support write operations (fields, invariants, domain methods) — not just reads
- The ingestion REST API follows the same domain format that end-user write operations will eventually use
- The adapter (`Kilde = "BiRK-adapter"`) is isolated; removing it in Phase 2 requires no domain model changes
- Reference data auto-creation (FK-6.2) is a Phase 1 ingestion concern; in Phase 2, reference data is managed through explicit admin operations
