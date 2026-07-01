# Internal Lookup Endpoint Contract

**Endpoint**: `GET /v1/internal/tiltak/{birkTiltakKey}`
**Access**: System-to-system only — Azure Managed Identity required (FR-021)
**Consumer**: Hendelsestjenesten

---

## Authentication

The caller must present a valid Azure Managed Identity JWT bearer token with the audience configured for this service. End users must not have access (FR-021). Requests without a valid system identity token are rejected with `401 Unauthorized`.

```
Authorization: Bearer {managed-identity-token}
```

---

## Request

| Parameter | In | Type | Required | Description |
|-----------|-----|------|----------|-------------|
| `birkTiltakKey` | path | string | Yes | BiRK Tiltak primary key |

---

## Responses

### 200 OK — Placement found and child linkage confirmed

```json
{
  "barnId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "tjenesteId": "7c9e6679-7425-40de-944b-e07fc1f90ae7"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `barnId` | UUID | Resolved M2LB child identifier |
| `tjenesteId` | UUID | Internal placement identifier |

### 404 Not Found — BiRK Tiltak key not found (FR-022)

```json
{
  "kode": "TILTAK_IKKE_FUNNET"
}
```

The BiRK key is not present in the system. The caller should not retry unless the placement is expected to arrive via synchronization.

### 409 Conflict — Placement exists, child linkage pending (FR-023)

```json
{
  "kode": "BARN_ID_IKKE_KOBLET"
}
```

The placement exists but `BarnId` has not yet been resolved. The caller **should** retry later — linkage is expected to complete automatically (via `BarnRegistrert`).

### 410 Gone — Placement permanently unresolved (FR-023a)

```json
{
  "kode": "TILTAK_PERMANENT_UKOBLET"
}
```

The placement exists but has been flagged as permanently unresolved after the configurable deadline elapsed. The caller **must not** retry — linkage will not be resolved automatically. The placement requires operator review.

### 401 Unauthorized — Missing or invalid managed identity token (FR-021)

No body. The request did not carry a valid system identity token.

---

## Error Code Summary

| HTTP Status | Code | Retry? |
|-------------|------|--------|
| 404 | `TILTAK_IKKE_FUNNET` | No (unless placement not yet synced) |
| 409 | `BARN_ID_IKKE_KOBLET` | Yes — retry later |
| 410 | `TILTAK_PERMANENT_UKOBLET` | No — permanent |
| 401 | *(no code)* | No — check credentials |
