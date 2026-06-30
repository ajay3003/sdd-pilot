# SCIM HTTP API Contract

Base path: `/scim/v2`  
Authentication: `Authorization: Bearer <provisioning-secret>`  
All requests returning 401 on invalid/missing token.

---

## POST /scim/v2/Users

Creates a new user or re-activates an existing one.

**Request body** (Entra provisioning engine format):
```json
{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
  "id": "88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234",
  "externalId": "jsmith@contoso.com",
  "userName": "jsmith@contoso.com",
  "active": true
}
```

**Response 201 Created** (user created for first time):
```json
{
  "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
  "id": "88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234",
  "externalId": "jsmith@contoso.com",
  "userName": "jsmith@contoso.com",
  "active": true
}
```

**Response 200 OK** (idempotent — user already in requested state)

---

## GET /scim/v2/Users

Returns all known users (active and inactive) with pagination.

**Query parameters**:
- `startIndex` (int, default 1) — 1-based page start
- `count` (int, default configurable, max 200) — page size
- `filter` (string, optional) — equality filter: `externalId eq "..."` or `userName eq "..."`

**Response 200 OK**:
```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
  "totalResults": 42,
  "startIndex": 1,
  "itemsPerPage": 20,
  "Resources": [
    {
      "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
      "id": "88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234",
      "externalId": "jsmith@contoso.com",
      "userName": "jsmith@contoso.com",
      "active": true
    }
  ]
}
```

---

## GET /scim/v2/Users/{id}

Returns a single user by Entra Object ID.

**Response 200 OK** — same shape as individual Resource above.  
**Response 404 Not Found**:
```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
  "status": 404,
  "detail": "User 88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234 not found."
}
```

---

## PATCH /scim/v2/Users/{id}

Activates or deactivates an existing user.

**Request body** (SCIM PatchOp, RFC 7644 §3.5.2):
```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
  "Operations": [
    { "op": "Replace", "path": "active", "value": false }
  ]
}
```

Only `op=Replace` + `path=active` is acted upon; other operations are logged and ignored.

**Response 200 OK** — full user resource (current state after patch).  
**Response 404 Not Found** — if user not in KjentBrukere.

---

## DELETE /scim/v2/Users/{id}

Deactivates a user (no hard delete; sets IsActive=false).

**Response 204 No Content** — on success (including if already inactive — idempotent).  
**Response 404 Not Found** — if user not in KjentBrukere.

---

## Error Responses

All error responses use the SCIM error schema:

```json
{
  "schemas": ["urn:ietf:params:scim:api:messages:2.0:Error"],
  "status": <HTTP status code>,
  "detail": "<human-readable description>"
}
```

| HTTP Status | Condition |
|---|---|
| 401 | Missing or invalid Bearer token |
| 404 | User not found (GET/{id}, DELETE) |
| 500 | Service Bus or SQL Server error after Polly retries exhausted |

---

## Health Endpoint

`GET /health` — anonymous, returns 200 if all checks pass.

```json
{
  "status": "Healthy",
  "checks": [
    { "name": "database", "status": "Healthy" },
    { "name": "servicebus", "status": "Healthy" }
  ]
}
```

Checks:
- `database`: `AddDbContextCheck<AutorisasjonsDbContext>()` — verifies SQL Server connectivity
- `servicebus`: custom check — attempts `ServiceBusAdministrationClient.GetQueuePropertiesAsync` or connection test

---

## Service Bus Event Contract

**Topic**: `entra.brukere`  
**Message properties** (ApplicationProperties):

| Property | Value |
|---|---|
| `HendelsesId` | UUID v4 (matches `MessageId`) |
| `HendelsesType` | `BrukerAktivert` or `BrukerDeaktivert` |
| `Tidspunkt` | ISO-8601 UTC timestamp |

**Message body** (JSON, camelCase):
```json
{
  "hendelsesId": "550e8400-e29b-41d4-a716-446655440000",
  "hendelsesType": "BrukerAktivert",
  "entraObjectId": "88f1b0c9-3a72-4f1c-a4b1-fe2e45d61234",
  "tidsstempel": "2026-04-23T10:15:30.000Z",
  "kildeReferanse": "SCIM-POST /scim/v2/Users"
}
```

This contract is **frozen** — consumed by the Authorization module without changes.
