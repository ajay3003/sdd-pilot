# Contract: User Access API

**Service classes**: `IUserAccessService` / `UserAccessService`, `IOrgUnitService` / `OrgUnitService`  
**Base URL**: `{apiBaseUrl}autorisasjonstjeneste/v1/`  
**Auth handler**: `AutorisasjonMessageHandler`

---

## GET /users/search?query={q}

Searches the identity directory (Microsoft Entra) by name or email. Called by `IUserAccessService` with debounce (FR-023, see research.md Decision 5).

**Response `200 OK`**:
```json
[
  {
    "userId": "uuid",
    "displayName": "Kari Nordmann",
    "email": "kari.nordmann@nav.no"
  }
]
```

**Responses**:
| Status | Meaning | UI Action |
|--------|---------|-----------|
| `200 OK` | Results (may be empty array) | Show results or "Ingen brukere funnet" |
| `4xx / 5xx` | Directory search failed | Inline error below search field; screen remains usable (FR-023 clarification) |

---

## GET /users/{userId}/access

Returns the full access picture for a selected user: general role assignments, child relations, and effective access summary (FR-024).

**Response `200 OK`**:
```json
{
  "userId": "uuid",
  "displayName": "Kari Nordmann",
  "generalRoleAssignments": [
    {
      "id": "uuid",
      "roleId": "uuid",
      "roleName": "Saksbehandler",
      "orgUnitId": "uuid | null",
      "orgUnitName": "NAV Oslo | null",
      "validFrom": "2026-01-01T00:00:00Z",
      "validTo": "2027-01-01T00:00:00Z | null",
      "isActive": true
    }
  ],
  "childRelations": [
    {
      "id": "uuid",
      "childId": "uuid",
      "roleId": "uuid",
      "roleName": "Fosterhjemsansvarlig",
      "validFrom": "2026-01-01T00:00:00Z",
      "validTo": null,
      "isActive": true
    }
  ],
  "effectiveAccess": {
    "operationNames": ["LesPersonopplysninger", "EndrePersonopplysninger"],
    "computedAt": "2026-05-08T10:00:00Z"
  }
}
```

**Note**: `childId` is a M2LB UUID, never displayed in URLs (Constitution VI). The child is identified in UI only by role name + relation ID.

---

## POST /users/{userId}/general-role-assignments

Assign a general role to a user (FR-014). Blocked client-side when `userId` equals logged-in administrator OID (FR-025).

**Request body**:
```json
{
  "roleId": "uuid",
  "orgUnitId": "uuid | null",
  "validFrom": "2026-05-08T00:00:00Z",
  "validTo": "2027-05-08T00:00:00Z | null"
}
```

**Responses**:
| Status | Body |
|--------|------|
| `201 Created` | Updated user access object (full) |
| `400 Bad Request` | `{ "code": "PAST_EXPIRY" }` |
| `403 Forbidden` | Missing `TildelBrukertilgang` |

---

## DELETE /users/{userId}/general-role-assignments/{assignmentId}

Revoke a general role assignment. Requires confirmation dialog before calling (FR-015).

**Responses**: `200 OK` (updated user access) | `403 Forbidden`

---

## POST /users/{userId}/child-relations

Create a child relation with a child-specific role and optional expiry.

**Request body**:
```json
{
  "childId": "uuid",
  "roleId": "uuid",
  "validFrom": "2026-05-08T00:00:00Z",
  "validTo": null
}
```

**Responses**:
| Status | Body |
|--------|------|
| `201 Created` | Updated user access object |
| `400 Bad Request` | `{ "code": "CHILD_NOT_FOUND" }` → UI shows "Barnet ble ikke funnet" |
| `400 Bad Request` | `{ "code": "PAST_EXPIRY" }` |
| `403 Forbidden` | — |

---

## DELETE /users/{userId}/child-relations/{relationId}

Revoke a child relation. Requires confirmation dialog before calling.

**Responses**: `200 OK` (updated user access) | `403 Forbidden`

---

## GET /org-units

Returns the full organisation unit list for use in assignment forms. Read-only (spec §Assumptions).

**Response `200 OK`**:
```json
[
  { "id": "uuid", "name": "NAV Oslo", "parentId": "uuid | null" }
]
```

Fetched once per admin session and used to populate org unit select lists in assignment dialogs.
