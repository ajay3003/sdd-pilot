# Contract: Administration Endpoint

**Adapter**: BiRK Person-adapter
**Reference**: FR-017, FK-6.4
**Authentication**: Azure Managed Identity (service-to-service; Bearer token required)
**Gateway**: MUST NOT be reachable via public API gateway; internal VNet access only

---

## POST /admin/feilkoe/reprosesser — Trigger Fault Queue Re-processing

Triggers immediate re-processing of all entries in the `feilkoe` table, bypassing the
configured polling interval. Used by operations staff to force re-delivery without waiting for
the next scheduled background run.

### Request

No request body. Authentication via Managed Identity Bearer token in `Authorization` header.

```http
POST /admin/feilkoe/reprosesser
Authorization: Bearer <managed-identity-token>
```

### Response

| HTTP status | Body | Meaning |
|-------------|------|---------|
| `202 Accepted` | `{"antallPoster": <int>}` | Re-processing triggered; body contains count of `feilkoe` entries queued |
| `401 Unauthorized` | — | Request did not present a valid Managed Identity token |
| `409 Conflict` | `{"melding": "Allerede under prosessering"}` | A re-processing run is already in progress |

### Example — successful trigger

```http
POST /admin/feilkoe/reprosesser
Authorization: Bearer eyJ...

HTTP/1.1 202 Accepted
Content-Type: application/json

{"antallPoster": 3}
```

### Example — conflict

```http
POST /admin/feilkoe/reprosesser
Authorization: Bearer eyJ...

HTTP/1.1 409 Conflict
Content-Type: application/json

{"melding": "Allerede under prosessering"}
```

### Behavior Notes

- The endpoint returns `202` immediately; actual re-processing runs asynchronously.
- A `202` with `antallPoster: 0` means the `feilkoe` table was empty at trigger time.
- Re-delivery follows the same retry logic as the scheduled background processor (FK-6.2).
- The operational alert for `feilkoe` entries auto-resolves once the table is empty (FK-8.2).
