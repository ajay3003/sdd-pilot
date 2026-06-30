# Quickstart: Running SCIM Adapter Locally

## Prerequisites

- .NET 10 SDK
- Docker Desktop (for Testcontainers in integration tests)
- SQL Server accessible (or use Testcontainers for tests)

## Local Development Setup

1. **Set user secrets** for the ScimAdapter project:

```bash
cd src/Autorisasjon.ScimAdapter
dotnet user-secrets set "ConnectionStrings:AutorisasjonDb" "Server=localhost;Database=AutorisasjonDb;Trusted_Connection=True;"
dotnet user-secrets set "ConnectionStrings:ServiceBus" ""  # leave empty to use disabled mode
dotnet user-secrets set "Scim:ProvisioningSecret" "dev-test-secret-replace-in-prod"
```

2. **Run the adapter** (Service Bus disabled in dev if connection string is empty):

```bash
dotnet run --project src/Autorisasjon.ScimAdapter
```

3. **Test the SCIM endpoint** with `curl`:

```bash
# POST a new user
curl -X POST http://localhost:5100/scim/v2/Users \
  -H "Authorization: Bearer dev-test-secret-replace-in-prod" \
  -H "Content-Type: application/json" \
  -d '{
    "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
    "id": "11111111-1111-1111-1111-111111111111",
    "externalId": "test@contoso.com",
    "userName": "test@contoso.com",
    "active": true
  }'

# List all users
curl http://localhost:5100/scim/v2/Users \
  -H "Authorization: Bearer dev-test-secret-replace-in-prod"

# Deactivate user
curl -X PATCH http://localhost:5100/scim/v2/Users/11111111-1111-1111-1111-111111111111 \
  -H "Authorization: Bearer dev-test-secret-replace-in-prod" \
  -H "Content-Type: application/json" \
  -d '{
    "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
    "Operations": [{"op": "Replace", "path": "active", "value": false}]
  }'
```

4. **Health check**:

```bash
curl http://localhost:5100/health
```

## Running Tests

```bash
# Unit tests (mocked dependencies)
dotnet test tests/Autorisasjon.UnitTests

# Integration tests (Testcontainers — SQL Server + Redis)
dotnet test tests/Autorisasjon.IntegrationTests

# SCIM adapter integration tests (Testcontainers — SQL Server)
dotnet test tests/Autorisasjon.ScimAdapter.IntegrationTests
```

## EF Core Migration

After adding `KjentBruker` entity, run from repo root:

```bash
dotnet ef migrations add AddKjentBruker \
  --project src/Autorisasjon.Infrastructure \
  --startup-project src/Autorisasjon.Api
dotnet ef database update \
  --project src/Autorisasjon.Infrastructure \
  --startup-project src/Autorisasjon.Api
```

## Key Configuration for Production

| Config Key | Source | Description |
|---|---|---|
| `ConnectionStrings:AutorisasjonDb` | Key Vault | SQL Server connection string |
| `ConnectionStrings:ServiceBus` | Key Vault | Service Bus namespace (Managed Identity auth) |
| `Scim:ProvisioningSecret` | Key Vault | Bearer token expected from Entra |
| `KeyVault:Uri` | appsettings.json | Key Vault URI |
| `AzureServiceBus:HendelsesTopics:EntraBrukere` | appsettings.json | Topic name (default: `entra.brukere`) |
| `Scim:PageSize` | appsettings.json | Default page size for GET /Users (default: 20) |

Key Vault secret naming: `AutorisasjonScimAdapter--<section>--<key>`  
Example: `AutorisasjonScimAdapter--Scim--ProvisioningSecret`
