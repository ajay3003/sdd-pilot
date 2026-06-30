# Contract: Emergency Access API

**Service class**: `IEmergencyAccessService` / `EmergencyAccessService`  
**Base URL**: `{apiBaseUrl}autorisasjonstjeneste/v1/`  
**Auth handler**: `AutorisasjonMessageHandler`

---

## GET /emergency-access/events

Returns emergency access events. The screen defaults to showing active and unreviewed events, but the administrator can toggle to see all events. The sorting contract (FR-028) is: unreviewed active events first, then others by activation time descending.

**Query parameters** (all optional):
- `status` — `Active | Expired | Revoked` (omit for all)
- `isReviewed` — `true | false` (omit for all)

**Response `200 OK`**:
```json
[
  {
    "id": "uuid",
    "userId": "uuid",
    "userDisplayName": "Per Hansen",
    "childId": "uuid",
    "justification": "Akutt bekymring for barnets sikkerhet",
    "activatedAt": "2026-05-08T09:00:00Z",
    "duration": "02:00:00",
    "expiresAt": "2026-05-08T11:00:00Z",
    "status": "Active",
    "isReviewed": false,
    "reviewedAt": null,
    "reviewedByUserId": null,
    "reviewNote": null,
    "revokedAt": null,
    "revocationReason": null
  }
]
```

**Note**: `childId` is returned for completeness but must not appear in URLs or page titles (Constitution VI). The child is not identified by name in any UI element within this module.

---

## POST /emergency-access/events/{id}/review

Submit a review for an event (FR-029). Available for any unreviewed event regardless of status (FR-028 clarification).

**Request body**:
```json
{ "reviewNote": "string" }
```

`reviewNote` must be non-empty — confirm button is disabled client-side until this is filled (FR-029).

**Responses**:
| Status | Body |
|--------|------|
| `200 OK` | Updated `EmergencyAccessEvent` |
| `400 Bad Request` | `{ "code": "EMPTY_REVIEW_NOTE" }` (defensive; client prevents this) |
| `400 Bad Request` | `{ "code": "ALREADY_REVIEWED" }` |
| `403 Forbidden` | Missing `GjennomgåNødtilgang` |

After `200 OK`: update the row inline, update badge counter (FR-031). If the reviewed event was the last unreviewed active event, badge drops to 0.

---

## POST /emergency-access/events/{id}/revoke

Revoke an active emergency access event (FR-030). Only available for `status == Active` events.

**Request body**:
```json
{ "reason": "string | null" }
```

Revocation reason is optional (FR-030). A confirmation dialog is required before this endpoint is called.

**Responses**:
| Status | Body |
|--------|------|
| `200 OK` | Updated `EmergencyAccessEvent` with `status: "Revoked"` |
| `400 Bad Request` | `{ "code": "NOT_ACTIVE" }` (event already expired or revoked) |
| `403 Forbidden` | Missing `TilbakekallNødtilgang` |

After `200 OK`: update the row inline, update badge counter if the event was also unreviewed (FR-031).
