# Quickstart: BiRK Hendelsesadapter (Local Development)

## Prerequisites

- .NET 9 SDK
- Docker Desktop (for local Azure emulators)
- Azure CLI (`az login` for DefaultAzureCredential fallback)
- Access to the M2LB development Azure subscription (for real Event Hubs / SQL if not emulating)

## 1. Clone and restore

```bash
git clone <repo-url>
cd m2lb-hendelse-birk-adapter
dotnet restore
```

## 2. Start local infrastructure

Use Docker Compose to run Azurite (Blob Storage emulator), Azure Service Bus emulator, and SQL Server locally:

```bash
docker compose up -d
```

The `docker-compose.yml` in the repo root starts:
- **Azurite** on port 10000–10002 (Blob, Queue, Table) — used for `BlobCheckpointStore`
- **Azure Service Bus emulator** on port 5672 (AMQP) — used for error queue
- **SQL Server 2022 Express** on port 1433 — used for `BirkHendelseRegistrering`

> For Event Hubs, connect to the shared development Event Hub namespace in the Azure dev subscription (emulator does not support all SDK features).

## 3. Configure local settings

Copy the template and fill in values:

```bash
cp src/M2LB.Hendelse.BiRK.Adapter/appsettings.Development.json.template \
   src/M2LB.Hendelse.BiRK.Adapter/appsettings.Development.json
```

Required values in `appsettings.Development.json`:

```json
{
  "EventHubs": {
    "FullyQualifiedNamespace": "<dev-namespace>.servicebus.windows.net",
    "EventHubName": "birk-cdc",
    "ConsumerGroup": "$Default",
    "CheckpointBlobContainerUrl": "http://127.0.0.1:10000/devstoreaccount1/birk-checkpoints"
  },
  "Hendelsestjenesten": {
    "BaseUrl": "https://hendelsestjenesten.dev.m2lb.no"
  },
  "Tjeneste": {
    "BaseUrl": "https://tjeneste.dev.m2lb.no"
  },
  "ConnectionStrings": {
    "AdapterDb": "Server=localhost,1433;Database=M2LBBiRKAdapter;User Id=sa;Password=YourPassword123!;TrustServerCertificate=true"
  },
  "ServiceBus": {
    "FullyQualifiedNamespace": "Endpoint=sb://localhost:5672;...",
    "ErrorQueueName": "birk-adapter-errors"
  }
}
```

## 4. Apply database migrations

```bash
dotnet ef database update --project src/M2LB.Hendelse.BiRK.Infrastructure \
                           --startup-project src/M2LB.Hendelse.BiRK.Adapter
```

## 5. Run the adapter

```bash
dotnet run --project src/M2LB.Hendelse.BiRK.Adapter
```

The adapter will:
1. Validate all code mappings from `code-mappings.json`
2. Run startup readiness checks (Hendelsestjenesten, Tjeneste, Azure SQL)
3. Start the `EventProcessorClient` (replays from earliest offset if no checkpoint exists)
4. Expose the health check at `http://localhost:8080/health`

## 6. Run tests

```bash
# Unit tests (no infrastructure needed)
dotnet test tests/M2LB.Hendelse.BiRK.Unit

# Integration tests (requires Docker infrastructure from step 2)
dotnet test tests/M2LB.Hendelse.BiRK.Integration
```

## 7. Health check

```bash
curl http://localhost:8080/health
```

## Common issues

| Issue | Fix |
|-------|-----|
| `Azure.RequestFailedException: No such host` for Event Hubs | Run `az login` so `DefaultAzureCredential` can use your developer identity |
| SQL migration fails | Ensure Docker SQL Server is running: `docker compose ps` |
| `CodeMappingValidationException` at startup | Check `code-mappings.json` — all expected BiRK code values must have UUID entries |
| Health check reports `hendelsestjenesten: Unhealthy` | Verify VPN/tunnel to dev environment is active |
