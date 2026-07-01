# Contract: Audit Log API

**Service class**: `IAuditLogService` / `AuditLogService`  
**Base URL**: `{apiBaseUrl}autorisasjonstjeneste/v1/`  
**Auth handler**: `AutorisasjonMessageHandler`

---

## GET /audit-log

Returns a paginated, filtered list of audit log entries (FR-032, FR-033). Supports deep-linking via URL query parameters (FR-035, research.md Decision 8).

**Query parameters**:
| Parameter | Type | Notes |
|-----------|------|-------|
| `actorId` | `Guid?` | Filter by actor who performed the action |
| `entityType` | `string?` | e.g. `"Operation"`, `"GeneralRole"`, `"EmergencyAccessEvent"` |
| `entityId` | `Guid?` | Filter by specific entity ID |
| `from` | `string?` | ISO 8601 date (`2026-01-01`) — inclusive |
| `to` | `string?` | ISO 8601 date (`2026-12-31`) — inclusive |
| `pageIndex` | `int` | 0-based, default `0` |
| `pageSize` | `int` | Default `25`, max `100` |

**Response `200 OK`**:
```json
{
  "items": [
    {
      "id": "uuid",
      "timestamp": "2026-05-08T09:30:00Z",
      "actorId": "uuid",
      "actorDisplayName": "Ola Nordmann",
      "actionType": "KlassifiserOperasjon",
      "entityType": "Operation",
      "entityId": "uuid",
      "entityDisplayName": "LesPersonopplysninger (PersonTjeneste)",
      "beforeState": { "classification": "General", "isVerified": false },
      "afterState": { "classification": "ChildSpecific", "isVerified": true }
    }
  ],
  "totalCount": 142,
  "pageIndex": 0,
  "pageSize": 25
}
```

**Notes**:
- `beforeState` and `afterState` are structured JSON objects — the frontend renders them as formatted key-value lists, not raw JSON text (FR-034).
- `entityDisplayName` is a human-readable label so the administrator can identify the entity without needing to look it up.
- If no filter is applied, the endpoint returns all entries sorted by `timestamp` descending.

**Error responses**: `403 Forbidden` (missing `LesRevisjonslogg`) → `Result.Failure`, page shows error state (FR-037).

---

## Supported Entity Types (for filter dropdown)

The frontend populates the `entityType` filter from this fixed list:

| Value | Norwegian label |
|-------|----------------|
| `Operation` | Operasjon |
| `GeneralRole` | Generell rolle |
| `ChildSpecificRole` | Barnespesifikk rolle |
| `GeneralRoleAssignment` | Rolletildeling |
| `ChildRelation` | Barnerelasjonstildeling |
| `EmergencyAccessEvent` | Nødtilgang |

---

## Deep-Link URL Format

The "Show history" button in `OperationCataloguePage` navigates to:

```
/admin/audit-log?entityType=Operation&entityId=3fa85f64-5717-4562-b3fc-2c963f66afa6
```

On load, `AuditLogPage` reads `[SupplyParameterFromQuery]` parameters and pre-populates the filter before the initial fetch (research.md Decision 8, FR-035, SC-008).
