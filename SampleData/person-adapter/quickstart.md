# Quickstart: BiRK Person-adapter

**Feature**: BiRK Person-adapter
**Date**: 2026-04-20

---

## Prerequisites

- .NET 10 SDK
- Docker Desktop (for `Testcontainers.MsSql` in integration tests)
- Azure CLI or Visual Studio (for local `DefaultAzureCredential` resolution)
- Read access to:
  - Azure Event Hubs namespace (BiRK CDC stream)
  - Azure Blob Storage container (Event Hubs checkpoint)
  - Azure SQL database (or SQL Server in Docker locally)
  - Azure Key Vault (Application Insights connection string)
  - PersonModule REST API (or local mock)

---

## Running Locally

### 1. Authenticate with Azure

```bash
az login
```

`DefaultAzureCredential` resolves credentials from the Azure CLI automatically in development.
No credentials in config files.

### 2. Configure `appsettings.Local.json`

Create `src/M2LB.PersonBiRKAdapter.Worker/appsettings.Local.json` (gitignored):

```json
{
  "EventHubs": {
    "FullyQualifiedNamespace": "<your-namespace>.servicebus.windows.net",
    "EventHubName": "<birk-cdc-event-hub>",
    "ConsumerGroup": "$Default",
    "CheckpointContainerUrl": "https://<storage-account>.blob.core.windows.net/<checkpoint-container>"
  },
  "PersonModule": {
    "BaseUrl": "https://<personmodul-base-url>"
  },
  "Database": {
    "ConnectionString": "Server=<server>;Database=<db>;Authentication=Active Directory Default;Encrypt=True"
  },
  "KeyVault": {
    "Uri": "https://<keyvault-name>.vault.azure.net/"
  }
}
```

### 3. Apply database migrations

```bash
dotnet ef database update --project src/M2LB.PersonBiRKAdapter.Infrastructure \
  --startup-project src/M2LB.PersonBiRKAdapter.Worker
```

### 4. Start the adapter

```bash
cd src/M2LB.PersonBiRKAdapter.Worker
dotnet run
```

Health endpoints:
- `http://localhost:5000/helse/live` → `{"status":"Frisk"}`
- `http://localhost:5000/helse/ready` → dependency statuses

---

## Running Tests

### Unit tests (no external dependencies)

```bash
dotnet test tests/M2LB.PersonBiRKAdapter.Unit/
```

No Azure credentials required. All Azure SDK dependencies are mocked via NSubstitute.

### Integration tests (requires Docker)

```bash
dotnet test tests/M2LB.PersonBiRKAdapter.Integration/
```

`Testcontainers.MsSql` spins up SQL Server in Docker automatically. Event Hubs and PersonModule
HTTP are mocked via test doubles. No Azure credentials required.

---

## Verifying a Healthy Setup

After starting the adapter locally or in a test environment, verify these scenarios:

| Scenario | How to verify |
|----------|---------------|
| Liveness | `GET /helse/live` returns `{"status":"Frisk"}` |
| Readiness | `GET /helse/ready` shows all three dependencies as `Frisk` |
| Kode 6/7 rejection | Send a test event with `sikkerhetsnivaa: 2`; verify no PersonModule call is made, a critical log entry is written, and the event is acknowledged in Event Hubs |
| Idempotency | Deliver the same CDC event twice; verify PersonModule call count is 2 (both succeed) and PersonModule contains one record (second call returns 204) |
| Fault queue | Simulate PersonModule returning 5xx; verify a `feilkoe` row is created with correct fields after max retries |
| Re-delivery | With a `feilkoe` entry present, restore PersonModule; verify the background processor re-delivers, the row is removed, and the operational alert resolves |
| Checkpoint | Verify `UpdateCheckpointAsync` is called only after PersonModule confirms delivery, not before |

---

## Open Items Before Implementation Starts

| Item | Action required |
|------|----------------|
| Å-01: `birk-person-feltmapping.md` | Obtain document; implement `IPersonMapper` and `IChildRegistrationMapper` concrete classes with actual BiRK-to-PersonModule field mapping |
| PersonModule API URL | Confirm base URL and authentication scope for the target environment |
| Event Hubs namespace and consumer group | Confirm with infrastructure / operations team |
| Checkpoint container name | Confirm with infrastructure team |
| Azure SQL connection string | Confirm server, database name, and Managed Identity role assignment |
