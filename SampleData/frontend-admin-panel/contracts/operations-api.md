# Contract: Operations API

**Service class**: `IOperationService` / `OperationService`  
**Base URL**: `{apiBaseUrl}autorisasjonstjeneste/v1/`  
**Auth handler**: `AutorisasjonMessageHandler` (bearer token via named `"AuthApi"` client, same as existing)  
**Named HTTP client**: `"AdminApi"` (to be registered in `Program.cs` with `AutorisasjonMessageHandler`)

---

## GET /operations

Returns all registered platform operations. Loaded once per page visit; client-side filtering thereafter (FR-006).

**Response `200 OK`**:
```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "serviceName": "PersonTjeneste",
    "name": "LesPersonopplysninger",
    "displayName": "Les personopplysninger",
    "classification": "General",
    "isActive": true,
    "isVerified": false,
    "assignedRoleCount": 3
  }
]
```

**Error responses**: `403 Forbidden` (missing `Autorisasjonstjeneste:LesOperasjonskatalog`), `5xx` → `Result.Failure`.

---

## GET /operations/{id}/history

Returns chronological classification history for one operation (FR-010).

**Response `200 OK`**:
```json
[
  {
    "id": "uuid",
    "operationId": "uuid",
    "changedAt": "2026-01-15T14:30:00Z",
    "changedByUserId": "uuid",
    "changedByDisplayName": "Ola Nordmann",
    "previousClassification": "General",
    "newClassification": "ChildSpecific",
    "justification": "Begrenset til barnefaglig kontekst"
  }
]
```

---

## POST /operations/{id}/classify

Reclassifies an operation (FR-007). Confirming the same classification as current is blocked client-side (FR-009) so this endpoint always receives a changed value.

**Request body**:
```json
{
  "newClassification": "ChildSpecific",
  "justification": "string | null"
}
```

**Responses**:
| Status | Meaning | Body |
|--------|---------|------|
| `200 OK` | Classification updated | Updated `Operation` object |
| `400 Bad Request` | Same classification (should be caught client-side) | `{ "code": "SAME_CLASSIFICATION" }` |
| `403 Forbidden` | Missing `KlassifiserOperasjon` right | — |
| `409 Conflict` | Active roles affected — admin must confirm separately | `{ "code": "AFFECTED_ROLES", "affectedRoleNames": ["string"] }` |

When `409` is returned, the UI shows the warning dialog with `affectedRoleNames`. If the administrator confirms, the request is re-sent with `force: true` in the body.

---

## POST /operations/{id}/deactivate

Deactivates an operation (FR-008).

**Request body**: `{ "force": false }`  
Set `force: true` after the administrator has confirmed the affected-roles warning.

**Responses**:
| Status | Meaning | Body |
|--------|---------|------|
| `200 OK` | Deactivated | Updated `Operation` |
| `403 Forbidden` | Missing `DeaktiverOperasjon` right | — |
| `409 Conflict` | Operation in active use | `{ "code": "AFFECTED_ROLES", "affectedRoleNames": ["string"] }` |

---

## GET /badge-counts

Returns current badge counter values. Called by `IAdminBadgeService` (see research.md Decision 1).

**Response `200 OK`**:
```json
{
  "unverifiedOperationCount": 5,
  "unreviewedActiveEmergencyEventCount": 2
}
```

Called at nav load and invalidated by any mutation that changes the relevant count. Accessible to any user holding at least one `Autorisasjonstjeneste:` operation.
