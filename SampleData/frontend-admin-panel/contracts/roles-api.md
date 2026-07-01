# Contract: Roles API

**Service classes**: `IGeneralRoleService` / `GeneralRoleService`, `IChildSpecificRoleService` / `ChildSpecificRoleService`  
**Base URL**: `{apiBaseUrl}autorisasjonstjeneste/v1/`  
**Auth handler**: `AutorisasjonMessageHandler`

The General Roles and Child-Specific Roles APIs mirror each other with the addition of `gisVedNødtilgang` on child-specific roles. Endpoints are listed once for the general pattern; child-specific differences are noted where they apply.

---

## General Roles

### GET /general-roles

Returns all general roles with their assigned operations and active assignment count.

**Response `200 OK`**:
```json
[
  {
    "id": "uuid",
    "name": "Saksbehandler",
    "description": "Grunnleggende saksbehandlerrettigheter",
    "isActive": true,
    "operations": [
      { "id": "uuid", "name": "LesPersonopplysninger", "serviceName": "PersonTjeneste", "classification": "General" }
    ],
    "activeAssignmentCount": 12
  }
]
```

---

### POST /general-roles

Create a new general role (FR-011).

**Request body**: `{ "name": "string", "description": "string | null" }`

**Responses**:
| Status | Body |
|--------|------|
| `201 Created` | Created `GeneralRole` object |
| `400 Bad Request` | `{ "code": "EMPTY_NAME" }` |
| `409 Conflict` | `{ "code": "DUPLICATE_NAME" }` → UI shows "Rollenavn er allerede i bruk" |
| `403 Forbidden` | Missing `OpprettGenerellRolle` |

---

### PUT /general-roles/{id}

Update name and/or description (FR-011).

**Request body**: `{ "name": "string", "description": "string | null" }`

**Responses**: same as POST except `200 OK` on success.

---

### POST /general-roles/{id}/operations

Add an operation to the role (FR-013 — only general operations allowed).

**Request body**: `{ "operationId": "uuid" }`

**Responses**:
| Status | Body |
|--------|------|
| `200 OK` | Updated `GeneralRole` |
| `400 Bad Request` | `{ "code": "WRONG_CLASSIFICATION" }` → UI shows "Kun generelle operasjoner kan legges til en generell rolle" |
| `403 Forbidden` | Missing `EndreGenerellRolle` |

---

### DELETE /general-roles/{id}/operations/{operationId}

Remove an operation from the role.

**Responses**: `200 OK` (updated role) | `403 Forbidden`

---

### POST /general-roles/{id}/assign

Assign the role to a user with optional org unit scope and expiry (FR-014).

**Request body**:
```json
{
  "userId": "uuid",
  "orgUnitId": "uuid | null",
  "validFrom": "2026-05-08T00:00:00Z",
  "validTo": "2027-05-08T00:00:00Z | null"
}
```

**Responses**:
| Status | Body |
|--------|------|
| `201 Created` | Created `GeneralRoleAssignment` |
| `400 Bad Request` | `{ "code": "PAST_EXPIRY" }` → client should prevent this (FR-026), but handle defensively |
| `403 Forbidden` | Missing `TildelBrukertilgang` |

---

### DELETE /general-roles/{id}/assignments/{assignmentId}

Revoke an assignment (FR-015 — requires confirmation dialog before calling).

**Responses**: `200 OK` | `403 Forbidden`

---

### POST /general-roles/{id}/deactivate

Deactivate a general role (FR-016).

**Request body**: `{ "force": false }`  
Set `force: true` after the administrator confirms the affected-users warning.

**Responses**:
| Status | Body |
|--------|------|
| `200 OK` | Updated `GeneralRole` |
| `409 Conflict` | `{ "code": "ACTIVE_ASSIGNMENTS", "affectedUserCount": 7, "affectedUserNames": ["string"] }` |
| `403 Forbidden` | — |

---

## Child-Specific Roles

All endpoints mirror General Roles above, replacing `/general-roles` with `/child-specific-roles`. Additional fields and differences:

### GET /child-specific-roles

Response items include `gisVedNødtilgang: bool` and `activeRelationCount: int` instead of `activeAssignmentCount`.

### POST /child-specific-roles/{id}/operations

Only accepts child-specific operations. Returns `{ "code": "WRONG_CLASSIFICATION" }` if a general operation is submitted.

### POST /child-specific-roles/{id}/emergency-flag

Toggle the `GisVedNødtilgang` flag (FR-017–FR-021).

**Request body**: `{ "enable": true | false }`

**Responses**:
| Status | Body |
|--------|------|
| `200 OK` | Updated `ChildSpecificRole` with new `gisVedNødtilgang` value |
| `403 Forbidden` | Missing `Autorisasjonstjeneste:EndreBarnespesifikkRolle` |

**Client behaviour**: No optimistic update — `GisVedNødtilgang` displayed state changes only after `200 OK` (FR-021). Activation requires confirmation dialog before this endpoint is called (FR-018). Deactivation calls the endpoint directly (FR-019).

### POST /child-specific-roles/{id}/deactivate

Response on `409 Conflict`: `{ "code": "ACTIVE_RELATIONS", "affectedRelationCount": 4, "affectedRelationNames": ["string"] }` (FR-022).
