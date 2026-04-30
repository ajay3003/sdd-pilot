# Research: Scenario Management

**Phase**: 0 | **Branch**: `001-create-scenario` | **Date**: 2026-04-30

---

## 1. GraphQL Server — HotChocolate 14

**Decision**: HotChocolate 14 (code-first schema)  
**Rationale**: First-class ASP.NET Core integration via `AddGraphQLServer()`. Code-first approach keeps schema in sync with C# domain models without a separate SDL file. Native EF Core DataLoader support. Built-in Banana Cake Pop IDE at `/graphql` for development. Schema snapshot testing supported via `HotChocolate.Testing`.  
**Alternatives considered**:
- *graphql-dotnet*: Older API, less ergonomic EF Core integration.
- *SDL-first (schema-first)*: Adds a synchronisation burden between the SDL file and C# resolvers; code-first is strictly less work for a small team.

---

## 2. GraphQL Client — Strawberry Shake 14

**Decision**: Strawberry Shake 14 (HotChocolate's typed client generator for .NET)  
**Rationale**: Generates strongly typed C# classes from `.graphql` operation documents and the server schema at build time. Integrates with Blazor WASM's DI container via `AddScenariosClient()`. Eliminates manual JSON serialisation and keeps client operations in sync with the schema automatically. Same HotChocolate ecosystem as the server — single version to manage.  
**Alternatives considered**:
- *Raw `HttpClient` + `System.Text.Json`*: No type safety, verbose, error-prone schema drift.
- *ZeroQL*: Lighter, but smaller community and fewer Blazor examples.

---

## 3. Database — PostgreSQL 16 with EF Core 8

**Decision**: PostgreSQL 16 via `Npgsql.EntityFrameworkCore.PostgreSQL` (single provider, both dev and production)  
**Rationale**: PostgreSQL is the constitution-selected production target. Using a single provider eliminates the SQLite-to-Postgres divergence risk (e.g. case-sensitivity, enum handling). EF Core migrations are run via `dotnet ef migrations`. Local development uses a Docker Compose Postgres instance.  
**Alternatives considered**:
- *SQLite (dev) + Postgres (prod)*: Reduces local setup but introduces provider differences that can mask bugs; rejected on Test-First grounds.
- *SQL Server*: Heavier licensing and resource footprint; not warranted.

---

## 4. Structured Logging — Serilog

**Decision**: Serilog with `WriteTo.Console(new CompactJsonFormatter())`  
**Rationale**: Produces structured JSON meeting the constitution requirement (level, timestamp, trace-id, correlation-id). Console sink is container-friendly; downstream aggregators (Seq, ELK, Azure Monitor) ingest from stdout with no code change.  
**Key events** (from spec §Observability):

| Event | Fields |
|-------|--------|
| `ScenarioCreated` | scenarioId, projectId, type, durationMs |
| `ScenarioValidationFailed` | fields[], projectId, correlationId |
| `ScenarioCreationFailed` | exception, projectId, correlationId |

---

## 5. Correlation / Trace IDs

**Decision**: Custom `CorrelationIdMiddleware` that reads `X-Correlation-Id` from the request header (or generates a new GUID) and pushes it into Serilog's `LogContext` for every request.  
**Rationale**: HotChocolate receives all operations at `POST /graphql`, so per-operation tracing must be carried by a correlation ID on every HTTP request. OpenTelemetry full instrumentation can be layered on in a future observability story without altering this middleware.  
**Alternatives considered**: OpenTelemetry full setup — deferred; middleware is sufficient for v1.

---

## 6. Project Scoping (FR-010)

**Decision**: `projectId` is a required argument on both the `scenarios` query and the `createScenario` mutation.  
**Rationale**: Makes the scope explicit in the schema, avoids hidden JWT-claim magic, and keeps resolvers independently testable without a token present.  
**Alternatives considered**: Derive `projectId` from the JWT claim server-side — cleaner UX but harder to test in isolation and introduces hidden coupling between auth middleware and resolvers.

---

## 7. Double-Submit Prevention

**Decision**: Disable the submit button in Blazor component state while the `createScenario` mutation is in flight (bind to a `_isSubmitting` bool field).  
**Rationale**: Zero infrastructure cost; covers the common user case (slow connection, accidental double-click). The server-side domain model does not enforce deduplication in v1.  
**Alternatives considered**: Idempotency key header on the mutation — adds backend complexity not warranted at v1 scale.

---

## 8. Input Validation

**Decision**: Validate in two layers — Blazor component (DataAnnotations on the form model) and HotChocolate input type (custom `InputValidator` / `IInputFormatter` on the server).  
**Rationale**: Client-side validation provides immediate feedback (SC-003, US3); server-side validation is the authoritative gate (security principle — never trust client input). Both layers use the same rules: title required, max 500 chars; type must be a valid enum value.  
**Alternatives considered**: Server-only validation — compliant but poorer UX (round-trip per error).

---

## 9. Testing Strategy

### Backend

| Layer | Tool | Scope |
|-------|------|-------|
| Unit | xUnit + Moq + FluentAssertions | `ScenarioService` business logic, validation rules |
| Integration | xUnit + `WebApplicationFactory` + Testcontainers (PostgreSQL) | Full GraphQL request → real DB round trip |
| Contract | HotChocolate schema snapshot tests | Schema shape does not regress between commits |

### Frontend (Blazor)

| Layer | Tool | Scope |
|-------|------|-------|
| Component (unit) | bUnit + Moq | `ScenarioForm` renders, validation messages, disabled state; `ScenarioList` empty state and data rows |
| Page (integration) | bUnit + mocked Strawberry Shake client | `Scenarios.razor` full interaction — form submit triggers mutation, list refreshes |

**TDD order** (mandated by constitution):
1. Write failing test
2. Run → confirm red
3. Implement minimum code to pass
4. Refactor

---

## 10. CORS

**Decision**: Allow the Blazor WASM origin (configurable via `FRONTEND_ORIGIN` env var, default `http://localhost:5173`) for `POST /graphql`. All other origins blocked.  
**Rationale**: Single GraphQL endpoint; fine-grained per-operation CORS is unnecessary. Environment variable keeps the origin out of source control.

---

## 11. Local Development Setup

**Decision**: Docker Compose file at the repo root providing a single `postgres` service. Backend connects via `ConnectionStrings__Default` environment variable (override in `appsettings.Development.json` pointing to `localhost:5432`).  
**Rationale**: Zero-install PostgreSQL for contributors; reproducible across machines. `dotnet ef database update` runs migrations on first launch.
