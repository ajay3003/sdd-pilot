# Quickstart: Person Module Development

**Branch**: `001-person-module` | **Date**: 2026-03-06

---

## Prerequisites

- .NET 10 SDK
- Docker Desktop (for TestContainers / local SQL Server)
- Azure CLI (for Key Vault access in non-local environments)
- Access to Azure Service Bus namespace (dev environment) or local emulator

---

## Project Structure

```
personservice/
├── src/
│   ├── PersonService.Api/               # ASP.NET Core host: GraphQL + REST endpoints
│   ├── PersonService.Domain/            # Entities, domain services, domain events
│   ├── PersonService.Application/       # Use cases (BarnSearchService, InnmatingService, …)
│   └── PersonService.Infrastructure/   # EF Core, outbox publisher, auth client, Graph client
├── tests/
│   ├── PersonService.Domain.Tests/      # Pure domain unit tests
│   ├── PersonService.Application.Tests/ # Application layer unit tests (mocked infra)
│   ├── PersonService.Integration.Tests/ # EF Core + TestContainers: real DB
│   └── PersonService.Contract.Tests/   # GraphQL schema contract tests
└── specs/
    └── 001-person-module/               # This spec, plan, and design artifacts
```

---

## Local Development Setup

### 1. Clone and restore

```bash
git clone <repo> && cd personservice
dotnet restore
```

### 2. Local configuration

Copy and configure local secrets (Key Vault values mocked locally):

```bash
cd src/PersonService.Api
dotnet user-secrets set "ConnectionStrings:PersonDb" "Server=localhost,1433;Database=PersonService;User=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=true"
dotnet user-secrets set "ServiceBus:ConnectionString" "<dev-servicebus-conn-string>"
dotnet user-secrets set "AutorisasjonModule:BaseUrl" "https://auth-module.dev.m2lb.no"
```

### 3. Start local SQL Server via Docker

```bash
docker run -e 'ACCEPT_EULA=Y' -e 'SA_PASSWORD=YourStrong!Passw0rd' \
  -p 1433:1433 --name personservice-sql -d mcr.microsoft.com/mssql/server:2022-latest
```

### 4. Run EF Core migrations

```bash
cd src/PersonService.Infrastructure
dotnet ef database update --startup-project ../PersonService.Api
```

### 5. Run the service

```bash
cd src/PersonService.Api
dotnet run
```

GraphQL playground: `https://localhost:5001/graphql`
REST health check: `GET https://localhost:5001/api/person/v1/helse`

---

## Running Tests

```bash
# Unit tests only (no Docker required)
dotnet test tests/PersonService.Domain.Tests
dotnet test tests/PersonService.Application.Tests

# Integration tests (requires Docker — TestContainers spins up SQL Server automatically)
dotnet test tests/PersonService.Integration.Tests

# All tests
dotnet test
```

### Test categories

Use `[Trait("Category", "...")]` to filter:
- `Unit` — pure logic, no I/O
- `Integration` — real DB via TestContainers
- `Contract` — GraphQL schema validation
- `Security` — Kode 6/7 visibility enforcement (SC-003)

```bash
dotnet test --filter "Category=Security"
```

---

## Key Development Patterns

### Adding a new domain event

1. Add event payload record in `PersonService.Domain/Events/`
2. In the domain service, create `OutboxMessage` row in same `SaveChangesAsync()` transaction
3. `OutboxPublisherHostedService` picks it up and publishes to Service Bus
4. Add integration test verifying atomicity (mutation + event)

### Adding a new GraphQL query

1. Add resolver class in `PersonService.Api/GraphQL/Queries/`
2. Register with Hot Chocolate in `Program.cs`
3. Add `[Authorize(Policy = "...")]` attribute mapped to Person module operation
4. Add acceptance scenario test in `PersonService.Contract.Tests`

### Security classification rule

Every query that returns child data MUST apply the security filter:
```csharp
// In application service — NEVER skip this filter
var grantedChildIds = await _autorisasjonClient
    .HentGradertBarntilganger(brukerId);
query = query.Where(b =>
    b.SikkerhetsnivaaType.Nivaa < 2 ||
    grantedChildIds.Contains(b.BarnRegistreringId));
```

This filter MUST be in the base query, not applied post-result.

---

## EF Core Migration Commands

```bash
# Add new migration
dotnet ef migrations add <MigrationName> \
  --project src/PersonService.Infrastructure \
  --startup-project src/PersonService.Api

# Apply migrations
dotnet ef database update \
  --project src/PersonService.Infrastructure \
  --startup-project src/PersonService.Api

# Generate SQL script for production deployment
dotnet ef migrations script \
  --project src/PersonService.Infrastructure \
  --startup-project src/PersonService.Api \
  --output migrations.sql
```

---

## Useful References

- GraphQL schema: `specs/001-person-module/contracts/graphql-schema.graphql`
- REST OpenAPI: `docs/person-rest-openapi.txt`
- Event contracts: `specs/001-person-module/contracts/events.md`
- Auth integration: `specs/001-person-module/contracts/auth-integration.md`
- Domain model: `specs/001-person-module/data-model.md`
- Full spec: `specs/001-person-module/spec.md`
- Constitution: `.specify/memory/constitution.md`
