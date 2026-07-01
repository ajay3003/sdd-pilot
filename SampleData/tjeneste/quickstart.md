# Quickstart: Tjenestemodul M01

**Branch**: `001-tjenestemodul-m01`
**Stack**: .NET 10 · Hot Chocolate 15 · EF Core 10 · Azure SQL · Azure Service Bus · Azure Event Hubs · Wolverine

---

## Prerequisites

- .NET 10 SDK (`dotnet --version` should show `10.x`)
- Docker Desktop (required for integration tests via Testcontainers)
- Azure CLI (`az login` for local managed identity emulation)
- Access to an Azure subscription with:
  - Azure SQL server
  - Azure Service Bus namespace (Standard tier or above for topics)
  - Azure Event Hubs namespace
  - Azure Blob Storage account (for Event Hubs checkpoints)

---

## Solution Setup

```bash
# Create solution
dotnet new sln -n M2LB.Tjeneste

# Create projects
dotnet new webapi   -n M2LB.Tjeneste.Api            -o src/M2LB.Tjeneste.Api
dotnet new classlib -n M2LB.Tjeneste.Domain          -o src/M2LB.Tjeneste.Domain
dotnet new classlib -n M2LB.Tjeneste.Infrastructure  -o src/M2LB.Tjeneste.Infrastructure
dotnet new xunit    -n M2LB.Tjeneste.Unit            -o tests/M2LB.Tjeneste.Unit
dotnet new xunit    -n M2LB.Tjeneste.Integration     -o tests/M2LB.Tjeneste.Integration

# Add to solution
dotnet sln add src/**/*.csproj tests/**/*.csproj

# Project references
dotnet add src/M2LB.Tjeneste.Api            reference src/M2LB.Tjeneste.Domain src/M2LB.Tjeneste.Infrastructure
dotnet add src/M2LB.Tjeneste.Infrastructure  reference src/M2LB.Tjeneste.Domain
dotnet add tests/M2LB.Tjeneste.Unit         reference src/M2LB.Tjeneste.Domain src/M2LB.Tjeneste.Infrastructure
dotnet add tests/M2LB.Tjeneste.Integration  reference src/M2LB.Tjeneste.Api src/M2LB.Tjeneste.Domain src/M2LB.Tjeneste.Infrastructure
```

---

## Key Package References

### M2LB.Tjeneste.Api

```xml
<PackageReference Include="HotChocolate.AspNetCore"           Version="15.*" />
<PackageReference Include="HotChocolate.Authorization"        Version="15.*" />
<PackageReference Include="Microsoft.Identity.Web"            Version="3.*" />
<PackageReference Include="Serilog.AspNetCore"                Version="9.*" />
<PackageReference Include="Serilog.Sinks.ApplicationInsights" Version="4.*" />
<PackageReference Include="WolverineHttp"                     Version="3.*" />
```

### M2LB.Tjeneste.Infrastructure

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer"     Version="10.*" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design"        Version="10.*" />
<PackageReference Include="Azure.Messaging.ServiceBus"                  Version="7.*" />
<PackageReference Include="Azure.Messaging.EventHubs.Processor"         Version="5.*" />
<PackageReference Include="Azure.Storage.Blobs"                         Version="12.*" />
<PackageReference Include="Azure.Identity"                              Version="1.*" />
<PackageReference Include="WolverineEntityFrameworkCore"                Version="3.*" />
<PackageReference Include="WolverineAzureServiceBus"                    Version="3.*" />
<PackageReference Include="Microsoft.Extensions.Http.Resilience"        Version="9.*" />
```

### M2LB.Tjeneste.Integration (tests)

```xml
<PackageReference Include="Testcontainers.MsSql"                       Version="4.*" />
<PackageReference Include="Testcontainers.ServiceBus"                  Version="4.*" />
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing"           Version="10.*" />
<PackageReference Include="FluentAssertions"                           Version="7.*" />
<PackageReference Include="xunit.runner.visualstudio"                  Version="3.*" />
```

---

## Program.cs Skeleton

```csharp
var builder = WebApplication.CreateBuilder(args);

// --- Authentication (PS-01, PS-02) ---
builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration);
builder.Services.AddAuthorization();

// --- GraphQL (Hot Chocolate v15) ---
builder.Services
    .AddGraphQLServer()
    .AddTypes()                   // assembly scan — discovers all ObjectType<T> and QueryType classes
    .AddAuthorization()
    .AddProjections();

// --- EF Core (two DbContexts, one per schema) ---
var connStr = builder.Configuration.GetConnectionString("TjenesteDb");
builder.Services.AddDbContext<TjenesteDbContext>(opts => opts.UseSqlServer(connStr));
builder.Services.AddDbContext<BirkStagingDbContext>(opts => opts.UseSqlServer(connStr));

// --- Wolverine (transactional outbox + Service Bus, GL-33) ---
builder.Host.UseWolverine(opts =>
{
    opts.UseAzureServiceBus(builder.Configuration["ServiceBus:Namespace"])
        .UseTopicAndSubscriptionRouting();
    opts.PersistMessagesWithSqlServer(connStr, schemaName: "wolverine");
});

// --- HTTP client with resilience for Personmodulen (FR-014) ---
builder.Services
    .AddHttpClient<IPersonmodulClient, PersonmodulClient>(client =>
        client.BaseAddress = new Uri(builder.Configuration["Personmodulen:BaseUrl"]!))
    .AddStandardResilienceHandler();

// --- Health checks (FR-024, FR-025, PS-08) ---
builder.Services.AddHealthChecks()
    .AddDbContextCheck<TjenesteDbContext>()
    .AddCheck<BirkSyncHealthCheck>("birk-sync");

// --- Background services ---
builder.Services.AddHostedService<OperationsRegistrationService>(); // FR-027
builder.Services.AddHostedService<BirkImportService>();             // FR-010, FR-011
builder.Services.AddHostedService<BirkCdcProcessorService>();       // FR-007
builder.Services.AddHostedService<BarnLinkageDeadlineService>();    // FR-019a

// --- Serilog (PS-08) ---
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.WithCorrelationId());

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CorrelationIdMiddleware>();

app.MapGraphQL("/graphql");

// Internal lookup — system identity only (FR-020, FR-021)
app.MapGet("/v1/internal/tiltak/{birkTiltakKey}", TiltakLookupHandler.Handle)
   .RequireAuthorization("SystemIdentity");

app.MapHealthChecks("/health");

app.Run();
```

---

## EF Core Migrations

```bash
# Add initial migration
dotnet ef migrations add InitialCreate \
  -p src/M2LB.Tjeneste.Infrastructure \
  -s src/M2LB.Tjeneste.Api

# Apply to database
dotnet ef database update \
  -p src/M2LB.Tjeneste.Infrastructure \
  -s src/M2LB.Tjeneste.Api
```

---

## Running Tests

Ensure Docker Desktop is running. Testcontainers will start SQL Server and Service Bus emulator containers automatically.

```bash
# Unit tests (no Docker required)
dotnet test tests/M2LB.Tjeneste.Unit

# Integration tests (requires Docker)
dotnet test tests/M2LB.Tjeneste.Integration
```

---

## Azure Resource Dependencies

| Resource | Purpose | Required for |
|----------|---------|-------------|
| Azure SQL | Domain schema `tjeneste` + staging schema `birk_staging` + Wolverine outbox schema | All |
| Azure Service Bus | Domain events (`tjenester`, `leselogg` topics), operations registration queue, `BarnRegistrert` subscription | Event publishing, audit, startup |
| Azure Event Hubs | BiRK CDC stream — one Event Hub per BiRK table | Synchronization |
| Azure Blob Storage | `EventProcessorClient` checkpoint storage — one container per Event Hub + consumer group | Synchronization resume (FR-011) |
| Azure EntraID | JWT bearer auth for case workers; Managed Identity for system-to-system | All |

**Network requirement** (PS-03): All resources must be within the M2LB VNet with Private Endpoints. No public IPs on the service or its dependencies.

---

## Field Mapping Configuration

BiRK field whitelisting and name translation are defined in `BirkFieldMappings.json` (FR-008, FR-009). Add new BiRK fields here — no code changes required. Each table entry lists:

```json
{
  "birk_tiltak": {
    "whitelist": ["birkkey", "..."],
    "mapping": {
      "birkFieldName": "m2lbFieldName"
    }
  }
}
```

Changes to this file require a new BiRK export to backfill any newly whitelisted fields (MP-02).
