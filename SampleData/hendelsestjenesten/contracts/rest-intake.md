# Contract — REST Intake API

**Consumer**: Hendelsesadapteren (machine-to-machine, Managed Identity)
**Base path**: `/api/hendelser/v1`
**Auth**: Azure Managed Identity Bearer token (EntraID)
**Full OpenAPI spec**: `docs/Hendelsestjenesten-—-REST-API.md`

---

## Endpoints

### Intake

| Method | Path | Idempotency Key | Description |
|--------|------|-----------------|-------------|
| PUT | `/innmating/inngrep/{birkHendelsesId}` | `birkHendelsesId` (BiRK TvangsProtokollPK) | Receive or update Inngrep event |
| PUT | `/innmating/romming/{birkHendelsesId}` | `birkHendelsesId` (BiRK RommingPK) | Receive or update Rømming/Uteblivelse/Bortføring event |

**Response codes**:
- `201 Created` — new event stored
- `200 OK` — updated event (new version created)
- `204 No Content` — no change (identical data)
- `422 Unprocessable Entity` — validation error with `feilkode` + `detaljer`
- `401 Unauthorized` — invalid/missing Managed Identity token

**Null BarnId**: Both endpoints accept `barnId: null` + `birkTiltakPK` set. Async linking via BackgroundService.

### Health

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/helse/live` | None | Liveness — is process alive? |
| GET | `/helse/ready` | None | Readiness — all dependencies up? |

### Reference Data (for adapter startup)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/referansedata/hjemmeltyper` | All HjemmelType with BirkVerdi mapping |
| GET | `/referansedata/rommingkategorier` | All RommingKategoriType with BirkVerdi mapping |
| GET | `/referansedata/tvangsprotokollstatuser` | All TvangsProtokollStatusType with BirkVerdi mapping |

---

## Adapter Startup Sequence

```
1. GET /referansedata/hjemmeltyper          → cache BirkVerdi → HjemmelTypeId
2. GET /referansedata/rommingkategorier     → cache BirkVerdi → RommingKategoriTypeId
3. GET /referansedata/tvangsprotokollstatuser → cache BirkVerdi → TvangsProtokollStatusTypeId
4. Begin processing events from Event Hub
```

---

## Idempotency Guarantee

Same `birkHendelsesId` sent twice with identical data → `204`. Same ID with changed data → `200`
(new version). The adapter can safely replay without risk of duplicates.
