# Quickstart — Hendelsestjenesten

**Repository**: `m2lb-hendelser`
**Platform**: .NET 10, Azure SQL, Azure Service Bus (via Wolverine)

---

## Prerequisites

- .NET 10 SDK
- Docker Desktop (for Testcontainers SQL Server in integration tests)
- Azure CLI (`az login`) or a local Azure Service Bus emulator for full local run
- Access to M2LB Azure dev subscription (for Managed Identity services)

---

## Project Structure

```
src/
  M2LB.Hendelse.Api/            ← ASP.NET Core 10 (REST controllers + Hot Chocolate GraphQL)
  M2LB.Hendelse.Domain/         ← Entities, domain services, invariants
  M2LB.Hendelse.Infrastructure/ ← EF Core DbContext, Wolverine setup, message handlers
tests/
  M2LB.Hendelse.Unit/           ← Domain logic (xUnit, no I/O)
  M2LB.Hendelse.Integration/    ← End-to-end via Testcontainers (SQL Server)
```

---

## Run Locally

```bash
# Restore
dotnet restore

# Build
dotnet build

# Run (requires Azure SQL + Service Bus connection strings in user secrets or appsettings.Development.json)
dotnet run --project src/M2LB.Hendelse.Api
```

GraphQL playground: `https://localhost:5001/graphql`
Health live: `https://localhost:5001/api/hendelser/v1/helse/live`
Health ready: `https://localhost:5001/api/hendelser/v1/helse/ready`

---

## Run Tests

```bash
# Unit tests only (no Docker required)
dotnet test tests/M2LB.Hendelse.Unit

# All tests including integration (requires Docker for Testcontainers)
dotnet test
```

---

## Run Database Migrations

```bash
dotnet ef database update --project src/M2LB.Hendelse.Infrastructure \
    --startup-project src/M2LB.Hendelse.Api
```

---

## Key Configuration (appsettings.json)

```json
{
  "ConnectionStrings": {
    "HendelseDb": "<Azure SQL connection string — Key Vault in production>"
  },
  "Wolverine": {
    "ServiceBusNamespace": "<service-bus-namespace>.servicebus.windows.net"
  },
  "Autorisasjon": {
    "BaseUrl": "<autorisasjon-api-url>"
  }
}
```

Wolverine is configured in `Program.cs` via `builder.Host.UseWolverine(opts => { ... })`.
Topic/queue names, subscriptions, and Managed Identity (`DefaultAzureCredential`) are wired
there — not in appsettings. All secrets are from Azure Key Vault at runtime (GL-26).

All secrets are retrieved from Azure Key Vault at runtime via Managed Identity (GL-26).
No secrets in source control or `appsettings.json`.

---

## Operations Registration

On startup the service publishes the following operations to Service Bus (GL-09):
```
Hendelse:HentHendelserForBarn
Hendelse:HentHendelse
Hendelse:SeInvolverte
Hendelse:SeInngrepDetalj
Hendelse:SeRommingsDetalj
```

The service refuses to start if registration fails.

---

## Data Flow Summary

```
BiRK → CDC → Event Hub → Hendelsesadapteren
                              ↓
                   PUT /api/hendelser/v1/innmating/{type}/{id}
                              ↓
                   HendelsesInnmatingTjeneste (domain)
                              ↓
                   Azure SQL (HendelsesVersjon + Wolverine outbox envelope)
                              ↓ (Wolverine sender daemon)
                   Service Bus → hendelser.barn topic
                              ↓
                   Downstream subscribers (WorkflowTjenesten etc.)

Service Bus → tjeneste.tjenester → TjenesteOpprettetHandler (Wolverine)
                              ↓
                   BarnId linked on matching Hendelse rows
                              ↓ (Wolverine outbox)
                   Service Bus → hendelser.barn (HendelsesRegistrert)

Saksbehandler → Blazor WASM → YARP → GraphQL /graphql
                                          ↓
                              Autorisasjon API (evaluer)
                                          ↓
                              Azure SQL read + Wolverine outbox (leselogg)
```
