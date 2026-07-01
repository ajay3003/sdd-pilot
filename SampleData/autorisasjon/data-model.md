# Data Model: SCIM User Synchronization Adapter

## Entity: KjentBruker

**Location**: `Autorisasjon.Infrastructure` (not Domain — adapter-specific sync state)  
**Table**: `KjentBrukere`

### Fields

| Property | Column | Type | Constraints | Notes |
|---|---|---|---|---|
| `EntraObjectId` | `EntraObjectId` | `uniqueidentifier` | PK, NOT NULL | UUID v4 from Entra (PS-04) |
| `UserName` | `UserName` | `nvarchar(256)` | NOT NULL | Entra UPN; indexed for filter queries |
| `ExternalId` | `ExternalId` | `nvarchar(256)` | NULL | Entra externalId attribute; indexed for filter queries |
| `IsActive` | `IsActive` | `bit` | NOT NULL, DEFAULT 1 | Current sync state |
| `LastUpdated` | `LastUpdated` | `datetimeoffset` | NOT NULL | UTC timestamp of last state change |

### EF Configuration (`KjentBrukerConfiguration.cs`)

```csharp
builder.ToTable("KjentBrukere");
builder.HasKey(e => e.EntraObjectId);
builder.Property(e => e.EntraObjectId).ValueGeneratedNever();
builder.Property(e => e.UserName).HasMaxLength(256).IsRequired();
builder.Property(e => e.ExternalId).HasMaxLength(256).IsRequired(false);
builder.Property(e => e.IsActive).HasDefaultValue(true);
builder.HasIndex(e => e.UserName).HasDatabaseName("IX_KjentBrukere_UserName");
builder.HasIndex(e => e.ExternalId).HasDatabaseName("IX_KjentBrukere_ExternalId");
```

### Notes
- No soft-delete column (`ErAktiv`/`IsDeleted`): `IsActive` IS the sync state; a KjentBruker row is never physically deleted — DELETE SCIM requests set `IsActive = false`.
- Not in `AuditInterceptor.TrackedEntityTypes` — no audit rows. The Service Bus event stream is the authoritative change log.
- No `GyldigFra`/`GyldigTil`: justified deviation from GL-18. KjentBruker is an idempotency record, not a domain entity with meaningful temporal validity. The complete state history exists in the `entra.brukere` Service Bus topic.

---

## Event Contracts

### BrukerAktivertEvent

Published on topic `entra.brukere` when a user is created or activated.

```json
{
  "hendelsesId": "550e8400-e29b-41d4-a716-446655440000",
  "hendelsesType": "BrukerAktivert",
  "entraObjectId": "88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234",
  "tidsstempel": "2026-04-23T10:15:30.000Z",
  "kildeReferanse": "SCIM-POST /scim/v2/Users"
}
```

`MessageId` = `hendelsesId` (UUID v4, set by EventPublisher).  
`HendelsesId` and `HendelsesType` also appear in `ApplicationProperties`.

### BrukerDeaktivertEvent

Published on topic `entra.brukere` when a user is deactivated or deleted.

```json
{
  "hendelsesId": "660e8400-e29b-41d4-a716-446655440001",
  "hendelsesType": "BrukerDeaktivert",
  "entraObjectId": "88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234",
  "tidsstempel": "2026-04-23T10:16:00.000Z",
  "kildeReferanse": "SCIM-DELETE /scim/v2/Users/88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234"
}
```

### Source Reference (`KildeReferanse`) Format

| Operation | KildeReferanse |
|---|---|
| POST /Users (new) | `SCIM-POST /scim/v2/Users` |
| PATCH /Users/{id} (active) | `SCIM-PATCH /scim/v2/Users/{id}` |
| DELETE /Users/{id} | `SCIM-DELETE /scim/v2/Users/{id}` |

---

## SCIM Request/Response Models

### ScimUser (inbound + outbound)

```csharp
public record ScimUser(
    string? Id,           // Entra Object ID (GUID as string)
    string? ExternalId,   // Entra externalId
    string? UserName,     // Entra UPN
    bool Active           // Active state
);
```

SCIM schemas field (`"urn:ietf:params:scim:schemas:core:2.0:User"`) is always included in responses but not stored.

### ScimListResponse<T>

```csharp
public record ScimListResponse<T>(
    int TotalResults,
    int StartIndex,
    int ItemsPerPage,
    IReadOnlyList<T> Resources
);
```

JSON shape per RFC 7644 §3.4.2:
```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
  "totalResults": 42,
  "startIndex": 1,
  "itemsPerPage": 20,
  "Resources": [...]
}
```

### ScimPatchRequest

```csharp
public record ScimPatchRequest(
    IReadOnlyList<ScimPatchOperation> Operations
);

public record ScimPatchOperation(
    string Op,
    string? Path,
    JsonElement Value
);
```

The adapter only acts on `op == "Replace"` (case-insensitive) + `path == "active"` (case-insensitive). All other operations are logged and ignored (RFC 7644 §3.5.2 — partial attribute support is permitted).

### ScimError

```csharp
public record ScimError(string Detail, int Status);
```

JSON shape per RFC 7644 §3.12:
```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
  "status": 401,
  "detail": "Invalid or missing Bearer token."
}
```

---

## State Transition Table (KjentBruker × SCIM Operation)

| Current State | SCIM Operation | `active` value | Action | Event |
|---|---|---|---|---|
| Not found | POST /Users | true | Create (IsActive=true) + publish | BrukerAktivert |
| Not found | POST /Users | false | Create (IsActive=false) + publish | BrukerDeaktivert |
| Not found | PATCH /Users/{id} | true | Create (IsActive=true) + publish | BrukerAktivert |
| Not found | PATCH /Users/{id} | false | Create (IsActive=false) + publish | BrukerDeaktivert |
| Not found | DELETE /Users/{id} | — | Create (IsActive=false) + publish | BrukerDeaktivert |
| Active (IsActive=true) | POST /Users | true | No-op | — |
| Active (IsActive=true) | PATCH /Users/{id} | true | No-op | — |
| Active (IsActive=true) | PATCH /Users/{id} | false | Update + publish | BrukerDeaktivert |
| Active (IsActive=true) | DELETE /Users/{id} | — | Update + publish | BrukerDeaktivert |
| Inactive (IsActive=false) | POST /Users | true | Update + publish | BrukerAktivert |
| Inactive (IsActive=false) | PATCH /Users/{id} | true | Update + publish | BrukerAktivert |
| Inactive (IsActive=false) | PATCH /Users/{id} | false | No-op | — |
| Inactive (IsActive=false) | DELETE /Users/{id} | — | No-op | — |

---

## Configuration Schema

### `appsettings.json` for ScimAdapter

```json
{
  "ConnectionStrings": {
    "AutorisasjonDb": "",
    "ServiceBus": "",
    "ApplicationInsights": ""
  },
  "AzureServiceBus": {
    "Disabled": false,
    "HendelsesTopics": {
      "EntraBrukere": "entra.brukere"
    }
  },
  "Scim": {
    "ProvisioningSecret": "",
    "PageSize": 20
  },
  "KeyVault": {
    "Uri": "https://kv-m2lb.vault.azure.net/"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Key Vault prefix: `AutorisasjonScimAdapter--`  
→ `AutorisasjonScimAdapter--ConnectionStrings--AutorisasjonDb` → `ConnectionStrings:AutorisasjonDb`  
→ `AutorisasjonScimAdapter--Scim--ProvisioningSecret` → `Scim:ProvisioningSecret`
