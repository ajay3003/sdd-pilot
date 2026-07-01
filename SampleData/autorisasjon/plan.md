# Implementation Plan: SCIM User Synchronization Adapter

**Branch**: `004-scim-user-sync` | **Date**: 2026-04-23 | **Spec**: [spec.md](spec.md)  
**Input**: Feature specification from `/specs/004-scim-user-sync/spec.md`

## Summary

Replace the Graph Change Notifications adapter with a SCIM 2.0 endpoint that Entra's built-in
provisioning engine calls directly. The adapter is a new ASP.NET Core Minimal API project
(`Autorisasjon.ScimAdapter`) that exposes `/scim/v2/Users`, persists sync state in a new
`KjentBrukere` table (shared `AutorisasjonsDbContext`), and publishes `BrukerAktivert` /
`BrukerDeaktivert` events to the `entra.brukere` Service Bus topic via the existing
`EventPublisher`. All research items are resolved; see `research.md` for decisions and
rationale.

## Technical Context

**Language/Version**: C# 13 / .NET 10  
**Primary Dependencies**: ASP.NET Core Minimal API, EF Core 10, Azure.Messaging.ServiceBus 7.x, Polly 8.x (new), Azure.Identity 1.x  
**Storage**: SQL Server 2022 — shared `AutorisasjonsDbContext`; new `KjentBrukere` table  
**Testing**: xUnit 2.9.3, Shouldly 4.3.0, NSubstitute 5.x, Testcontainers 4.x  
**Target Platform**: Container on Azure (same cluster as `Autorisasjon.Api`)  
**Project Type**: Web service (ASP.NET Core Minimal API, separate deployable)  
**Performance Goals**: No specific SLA; Entra's provisioning engine has a 30-second timeout per request  
**Constraints**: Service Bus publish must complete synchronously before HTTP 2xx (FR-008); Key Vault unreachable at startup → fail fast (FR-022)  
**Scale/Scope**: Single replica; up to 500 users (SC-004); no optimistic concurrency needed

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Gate | Status | Note |
|---|---|---|
| PP-01 Contract-Driven | ✅ PASS | SCIM endpoint is the inbound contract; `entra.brukere` topic is the frozen outbound contract |
| PP-02 Zero-Trust | ✅ PASS | Bearer token on every request (FR-003); secret from Key Vault (FR-016); fail fast if secret missing (FR-022) |
| GL-11 No public IP | ✅ PASS | Exposed via platform gateway, same as original adapter (FR-004) |
| GL-15 No cross-service DB | ✅ PASS | `KjentBrukere` is in the same Authorization module database; no foreign service DB access |
| GL-18 Temporal validity | ⚠️ JUSTIFIED DEVIATION | `KjentBruker` has state transitions but no `GyldigFra`/`GyldigTil`. Justified: it is an idempotency/sync-state record, not a domain entity. The Service Bus event stream is the audit trail. |
| GL-20 Transactional outbox | ⚠️ JUSTIFIED DEVIATION | Polly retry + 5xx used instead of outbox. Spec clarification explicitly overrides GL-20 for this adapter. See research.md for full rationale. |
| GL-21 No PII in events | ✅ PASS | Events contain only Entra Object ID (UUID) — no names, emails, or PII |
| GL-22 Idempotent consumers | ✅ PASS | KjentBruker state comparison suppresses duplicate events |
| GL-26 Secrets from Key Vault | ✅ PASS | Provisioning secret from Key Vault via Managed Identity (FR-016) |
| PS-01 EntraID auth | ⚠️ JUSTIFIED DEVIATION | SCIM endpoint uses provisioning Bearer token (machine-to-machine; Entra's provisioning engine, not a user). Justified: no human user exists in this flow; provisioning credential is machine identity stored in Key Vault. |
| PS-04 UUID v4 PKs | ✅ PASS | `KjentBruker.EntraObjectId` is UUID v4 (assigned by Entra) |
| PS-08 Observability | ✅ PASS | FR-019/FR-020/FR-021 structured logging, metrics, health endpoint |
| PP-09 Spec + Tests | ✅ PASS | Integration and unit tests required per constitution; see test plan below |

*Post-design re-check: All deviations are justified and documented. No new violations introduced by Phase 1 design.*

## Project Structure

### Documentation (this feature)

```text
specs/004-scim-user-sync/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   └── scim-http-api.md # Phase 1 output
└── tasks.md             # Phase 2 output (/speckit.tasks — not created by /speckit.plan)
```

### Source Code Changes

```text
src/
├── Autorisasjon.Infrastructure/           # Modified
│   ├── Persistence/
│   │   ├── AutorisasjonsDbContext.cs      # + DbSet<KjentBruker> KjentBrukere
│   │   ├── Entities/
│   │   │   └── KjentBruker.cs            # NEW — sync-state entity
│   │   └── Configurations/
│   │       └── KjentBrukerConfiguration.cs # NEW — EF table mapping
│   ├── Migrations/
│   │   └── XXXX_AddKjentBruker.cs        # NEW — EF migration
│   └── ServiceBus/
│       └── EventPublisher.cs             # + Topics.EntraBrukere constant
│
└── Autorisasjon.ScimAdapter/             # NEW project
    ├── Autorisasjon.ScimAdapter.csproj
    ├── Program.cs
    ├── appsettings.json
    ├── Authentication/
    │   └── ScimBearerAuthHandler.cs      # Custom Bearer token validator
    ├── Endpoints/
    │   └── UsersEndpoints.cs             # MapGroup("/scim/v2") — all 5 endpoints
    ├── Models/
    │   ├── Scim/
    │   │   ├── ScimUser.cs
    │   │   ├── ScimListResponse.cs
    │   │   ├── ScimPatchRequest.cs
    │   │   └── ScimError.cs
    │   └── Events/
    │       ├── BrukerAktivertEvent.cs
    │       └── BrukerDeaktivertEvent.cs
    ├── Services/
    │   ├── ScimUserService.cs            # Core sync logic (idempotency, publish, save)
    │   └── ScimAdapterUserContext.cs     # IUserContext impl for AuditInterceptor compat
    ├── Telemetry/
    │   └── ScimMetrics.cs               # Custom meters (request count, event count)
    └── Configuration/
        └── ScimOptions.cs               # Bound from "Scim" section

tests/
├── Autorisasjon.UnitTests/              # Modified — add ScimUserService unit tests
│   └── ScimAdapter/
│       ├── ScimUserServiceTests.cs
│       └── ScimPatchRequestParserTests.cs
│
└── Autorisasjon.ScimAdapter.IntegrationTests/  # NEW project
    ├── Autorisasjon.ScimAdapter.IntegrationTests.csproj
    ├── ScimUsersEndpointTests.cs         # Full HTTP → DB → SB verification
    ├── Infrastructure/
    │   ├── ScimAdapterWebAppFactory.cs   # WebApplicationFactory<Program>
    │   └── FakeEventPublisher.cs         # Capture published events for assertion
    └── Fixtures/
        └── DatabaseFixture.cs            # Testcontainers SQL Server
```

**Structure Decision**: Two-project solution extension — one new deliverable project (`Autorisasjon.ScimAdapter`) + one new test project (`Autorisasjon.ScimAdapter.IntegrationTests`). Infrastructure changes are additive only. No changes to `Autorisasjon.Api`, `Autorisasjon.Application`, or `Autorisasjon.Domain`.

## Implementation Steps

### Step 1 — Infrastructure: KjentBruker Entity + Migration

**Files**:
- `src/Autorisasjon.Infrastructure/Persistence/Entities/KjentBruker.cs`
- `src/Autorisasjon.Infrastructure/Persistence/Configurations/KjentBrukerConfiguration.cs`
- Update `AutorisasjonsDbContext.cs`: add `DbSet<KjentBruker> KjentBrukere`
- Update `EventPublisher.cs`: add `public const string EntraBrukere = "entra.brukere";` to `Topics`
- Run EF migration: `dotnet ef migrations add AddKjentBruker --project src/Autorisasjon.Infrastructure --startup-project src/Autorisasjon.Api`

**KjentBruker entity** (see data-model.md):

```csharp
namespace Autorisasjon.Infrastructure.Persistence.Entities;

public class KjentBruker
{
    public Guid EntraObjectId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```

No factory method (not a domain entity with invariants). No private setters (no invariant protection needed — service layer enforces correctness).

---

### Step 2 — New Project: Autorisasjon.ScimAdapter

**`Autorisasjon.ScimAdapter.csproj`**:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Autorisasjon.Infrastructure\Autorisasjon.Infrastructure.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Azure.Extensions.AspNetCore.Configuration.Secrets" Version="1.*" />
    <PackageReference Include="Azure.Identity" Version="1.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.*">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore" Version="10.*" />
    <PackageReference Include="Polly" Version="8.*" />
  </ItemGroup>
</Project>
```

**Key dependencies**: `Autorisasjon.Infrastructure` (for `AutorisasjonsDbContext`, `EventPublisher`, `AuditInterceptor`). `Polly` 8.x for retry pipeline. `Azure.Extensions.AspNetCore.Configuration.Secrets` for Key Vault.

---

### Step 3 — Authentication Handler

**`Authentication/ScimBearerAuthHandler.cs`**:

Custom `AuthenticationHandler<AuthenticationSchemeOptions>` that:
1. Reads `Authorization: Bearer <token>` header
2. Compares against `IConfiguration["Scim:ProvisioningSecret"]` (constant-time comparison using `CryptographicOperations.FixedTimeEquals`)
3. On match: returns `AuthenticateResult.Success` with a minimal claims identity (role: `ScimProvisioner`)
4. On no header: returns `AuthenticateResult.NoResult()`
5. On mismatch: returns `AuthenticateResult.Fail("Invalid provisioning secret.")`

The secret must NOT be logged (FR-018). Use `IConfiguration` directly (resolved at startup from Key Vault).

---

### Step 4 — SCIM Models

**`Models/Scim/`** — plain C# records:

- `ScimUser`: `Id?`, `ExternalId?`, `UserName?`, `Active` — JSON serialized with `JsonPropertyName` attributes for SCIM naming (e.g., `externalId`)
- `ScimListResponse<T>`: `TotalResults`, `StartIndex`, `ItemsPerPage`, `Resources`
- `ScimPatchRequest`: `Operations` array; `ScimPatchOperation` has `Op`, `Path?`, `Value` (JsonElement)
- `ScimError`: `Detail`, `Status`

JSON naming: use `[JsonPropertyName("...")]` attributes since the property names diverge from C# naming conventions in SCIM (e.g., `"Resources"` is PascalCase in SCIM spec despite camelCase convention).

**`Models/Events/`**:

```csharp
public record BrukerAktivertEvent(
    string HendelsesId,
    string HendelsesType,
    string EntraObjectId,
    DateTimeOffset Tidsstempel,
    string KildeReferanse);

public record BrukerDeaktivertEvent(
    string HendelsesId,
    string HendelsesType,
    string EntraObjectId,
    DateTimeOffset Tidsstempel,
    string KildeReferanse);
```

`HendelsesId` is set by `EventPublisher.PublishAsync` (it generates UUID v4 and sets `MessageId`). The event body should also carry it for consumers that deserialize the body. The service sets `HendelsesId = Guid.NewGuid().ToString()` before calling `PublishAsync`.

---

### Step 5 — ScimUserService (Core Logic)

**`Services/ScimUserService.cs`**:

Handles all five SCIM operations. Injected: `AutorisasjonsDbContext`, `EventPublisher`, `ResiliencePipeline` (Polly), `ILogger<ScimUserService>`.

**Publish helper** (using Polly):

```csharp
await _resiliencePipeline.ExecuteAsync(
    async ct => await _publisher.PublishAsync(
        EventPublisher.Topics.EntraBrukere, evt, eventType, ct),
    cancellationToken);
```

**Idempotency** (see state transition table in data-model.md): compare `requestedActive` vs `kjentBruker?.IsActive`. No-op path returns early without touching DB or SB.

**Transaction pattern** (publish-first, then commit):
1. Begin explicit transaction
2. Load or create `KjentBruker` (no `SaveChanges` yet)
3. If state change needed: publish to SB with Polly
4. On SB success: `SaveChanges` + commit
5. On SB failure: rollback transaction + rethrow (caller returns 5xx)

This ensures no partial state: either both SQL and SB succeed, or neither do.

---

### Step 6 — Endpoints

**`Endpoints/UsersEndpoints.cs`**:

```csharp
app.MapGroup("/scim/v2")
   .RequireAuthorization("ScimProvisioner")
   .MapScimUsers();
```

Five endpoints matching FR-002:
1. `POST /Users` → `ScimUserService.CreateOrActivateAsync`
2. `GET /Users` → `ScimUserService.ListAsync` (pagination + filter)
3. `GET /Users/{id}` → `ScimUserService.GetByIdAsync`
4. `PATCH /Users/{id}` → `ScimUserService.PatchAsync`
5. `DELETE /Users/{id}` → `ScimUserService.DeactivateAsync`

All return appropriate SCIM response shapes (see contracts/scim-http-api.md). 5xx responses from SB/SQL failures use `ScimError` format.

---

### Step 7 — Program.cs

Setup sequence (mirrors Autorisasjon.Api patterns where applicable):

1. **Key Vault** (non-dev only): `AddAzureKeyVault` with `ScimAdapterPrefixKeyVaultSecretManager` (`AutorisasjonScimAdapter--`)
2. **Structured logging**: same pattern as Api (SimpleConsole dev, JsonConsole prod)
3. **Application Insights** (non-dev)
4. **Authentication**: `AddAuthentication().AddScheme<AuthenticationSchemeOptions, ScimBearerAuthHandler>("ScimBearer", null)`
5. **Authorization**: `AddAuthorization(o => o.AddPolicy("ScimProvisioner", p => p.RequireAuthenticatedUser()))`
6. **IUserContext**: `services.AddScoped<IUserContext, ScimAdapterUserContext>()` (fixed UUID)
7. **EF Core**: `services.AddDbContext<AutorisasjonsDbContext>(...)` with `AuditInterceptor`
8. **Service Bus**: same pattern as `InfrastructureServices` (connection string vs Managed Identity based on env)
9. **EventPublisher**: `services.AddScoped<EventPublisher>()`
10. **Polly pipeline**: `services.AddResiliencePipeline("scim-servicebus", builder => builder.AddRetry(...))`
11. **ScimUserService**: `services.AddScoped<ScimUserService>()`
12. **Health checks**: `AddHealthChecks().AddDbContextCheck<AutorisasjonsDbContext>("database").AddServiceBusCheck(...)`
13. **Metrics**: `services.AddSingleton<ScimMetrics>()`; `AddOpenTelemetry()`
14. **Fail-fast validation**: after `builder.Build()`, validate `Scim:ProvisioningSecret` is non-empty; throw if missing

**Fail-fast** (FR-022): before `app.Run()`, verify the secret loaded:
```csharp
var secret = app.Configuration["Scim:ProvisioningSecret"];
if (string.IsNullOrEmpty(secret))
{
    app.Logger.LogCritical("Scim:ProvisioningSecret is not configured. Refusing to start.");
    return 1;
}
```

---

### Step 8 — Unit Tests

Add to `Autorisasjon.UnitTests/ScimAdapter/`:

- `ScimUserServiceTests.cs`: test all state transition rows from data-model.md. Use NSubstitute to mock `AutorisasjonsDbContext` (via `IQueryable<KjentBruker>` mocks) and `EventPublisher`. Verify: correct event published, correct KjentBruker state, idempotent paths publish nothing.
- `ScimPatchRequestParserTests.cs`: test PatchOp parsing for `active=true`, `active=false`, unknown operations, malformed JSON.

---

### Step 9 — Integration Tests (New Project)

**`Autorisasjon.ScimAdapter.IntegrationTests`**:

Uses `WebApplicationFactory<Program>` from `Autorisasjon.ScimAdapter`. Testcontainers SQL Server 2022. `FakeEventPublisher` captures published events for assertion (registers over `EventPublisher` in test DI).

**Test scenarios** (map to acceptance scenarios in spec):
- POST /Users (new user, active=true) → 201 + `BrukerAktivert` published
- POST /Users (existing inactive user, active=true) → 200 + `BrukerAktivert` published
- POST /Users (same request twice) → second returns 200 + no duplicate event
- PATCH /Users/{id} (active=false) → 200 + `BrukerDeaktivert` published
- DELETE /Users/{id} → 204 + `BrukerDeaktivert` published
- DELETE /Users/{id} (already inactive, repeat) → 204 + no duplicate event
- GET /Users (empty) → 200 with empty list
- GET /Users (paginated) → correct page
- GET /Users?filter=userName eq "..." → filtered result
- GET /Users/{id} (not found) → 404
- POST /Users with invalid token → 401
- Health endpoint → 200

---

## Complexity Tracking

Violations requiring justification:

| Violation | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| GL-18: KjentBruker without GyldigFra/GyldigTil | KjentBruker is a sync-state idempotency record, not a domain entity. Adding temporal fields would require tracking "who changed it" (IUserContext), which the adapter has no meaningful value for. | Adding audit fields (GyldigFra/GyldigTil) to a machine-driven sync record would add complexity without operational value; the SB event stream IS the audit trail |
| GL-20: No transactional outbox | Spec clarification explicitly ruled out an outbox table. Polly + Entra retry + full-sync reconciliation provide sufficient reliability for the operational SLA. | Outbox table would require polling/delivery infrastructure; spec explicitly rejected this. |
| PS-01: Non-EntraID auth on SCIM endpoint | Entra's provisioning engine authenticates with a provisioning credential, not as an EntraID-authenticated user. PS-01 is designed for human user flows. | Cannot use EntraID JWT — Entra's provisioning client sends a static Bearer token defined in the enterprise app configuration, not a JWT. |
