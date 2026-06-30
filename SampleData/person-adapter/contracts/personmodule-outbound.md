# Contract: PersonModule Outbound Calls

**Adapter**: BiRK Person-adapter
**Direction**: Outbound — adapter calls PersonModule REST ingestion API
**Base URL**: `{PersonModuleOptions.BaseUrl}/api/person/v1`
**Reference**: FK-2.2, FK-2.3, FK-4.3, FK-5.1–FK-5.3, BiRK constitution §3.1
**Authentication**: Azure Managed Identity (`DefaultAzureCredential`) — bearer token in `Authorization: Bearer {token}` header (constitution PS-02 compliant; PersonModule innmating endpoints will use Managed Identity)
**Transport**: HTTPS over private VNet endpoint (no public internet, per FK-9.4 / PS-03)

---

## PUT /api/person/v1/innmating/personer — Upsert Person

Delivers a single person record to PersonModule. Used for individual CDC events during
steady-state stream processing.

**Idempotency key**: `EksternId` — BiRK PersonPK. PersonModule upserts on this field.
`PersonId` must be a stable Guid derived deterministically from BiRK PersonPK so that
repeated delivery produces the same PUT, not a collision.

### Request Body

```json
{
  "PersonId": "<stable-guid-derived-from-birk-personpk>",
  "Navn": "<string>",
  "Foedselsnummer": "<string|null>",
  "UsikkerFoedselsnummer": "<string|null>",
  "DUFNummer": "<string|null>",
  "Foedselsdato": "<DateOnly|null>",
  "UsikkerFoedselsdato": "<DateOnly|null>",
  "KjoennTypeId": "<guid>",
  "EksternId": "<BiRK PersonPK|null>",
  "OpprettetAv": "<system-user-guid-from-config>",
  "EndretAv": "<system-user-guid-from-config>",
  "Kilde": "BiRK",
  "KorrelasjonId": "<new-guid-per-request>",
  "BirkEndringstidspunkt": "<DateTimeOffset|null>"
}
```

**Field notes**:
- `PersonId`: Stable Guid derived deterministically from BiRK PersonPK (e.g. name-based UUID v5
  keyed on PersonPK). Must be identical on every delivery of the same BiRK record.
- `KjoennTypeId`: PersonModule Guid for the gender reference data type. Requires GUID resolution —
  see open item **Å-03**. The adapter cannot derive this from a BiRK integer code directly.
- `OpprettetAv` / `EndretAv`: Fixed system-identity Guid for the adapter service account,
  from `PersonModuleOptions.SystemBrukerId` (or equivalent config field).
- `Kilde`: Fixed value `"BiRK"` for all adapter-sourced records.
- `KorrelasjonId`: Fresh `Guid.NewGuid()` per delivery call (used for distributed tracing).
- `BirkEndringstidspunkt`: Change timestamp from the BiRK CDC event — exact source field per Å-01.
- All other fields per `birk-person-feltmapping.md` (open item Å-01). Null values accepted
  for unborn children and EMA records (FK-2.5).

### Response

| Status | Meaning | Adapter action |
|--------|---------|----------------|
| `204 No Content` | Record created, updated, or duplicate (no-change) | Checkpoint advances |
| `422 Unprocessable Entity` | Validation failure | Written to `feilkoe` immediately; no retry; checkpoint advances |
| `429 Too Many Requests` | Rate limit exceeded | Delivery paused for cool-down period; retry count NOT consumed; resume after cool-down |
| `5xx` / timeout | Transient error | Retry with exponential backoff; on exhaustion → `feilkoe` |

> **Note**: PersonModule returns `204` for all successful outcomes — the adapter cannot
> distinguish created, updated, or no-change from the HTTP status code alone. `DeliveryResult`
> uses a single `Success` value for `204`.

---

## PUT /api/person/v1/innmating/barn — Upsert Child Registration

Delivers a single child registration to PersonModule. Used for individual CDC events during
steady-state stream processing.

**Idempotency key**: `BirkId` — BiRK child registration identifier.
`BarnRegistreringId` must be a stable Guid derived deterministically from `BirkId`.

### Request Body

```json
{
  "BarnRegistreringId": "<stable-guid-derived-from-birkid>",
  "PersonId": "<stable-guid-of-parent-person>",
  "BirkId": "<string>",
  "BarnTypeId": "<guid>",
  "BarnStatusTypeId": "<guid>",
  "SikkerhetsnivaaTypeId": "<guid>",
  "KommuneNr": "<string>",
  "OpprettetAv": "<system-user-guid-from-config>",
  "EndretAv": "<system-user-guid-from-config>",
  "Kilde": "BiRK",
  "KorrelasjonId": "<new-guid-per-request>",
  "BirkEndringstidspunkt": "<DateTimeOffset|null>"
}
```

**Field notes**:
- `BarnRegistreringId`: Stable Guid derived deterministically from BiRK `BirkId` (same
  derivation strategy as `PersonId` above).
- `PersonId`: Stable Guid of the parent person — same deterministic derivation from the
  parent's BiRK PersonPK. The CDC event for a child registration must carry the parent
  PersonPK; exact source field name per Å-01.
- `BarnTypeId`, `BarnStatusTypeId`, `SikkerhetsnivaaTypeId`: PersonModule Guids for
  reference data types. Require GUID resolution — see open item **Å-03**.
- `KommuneNr`: Municipality number passed through as a string code (no GUID lookup needed).
- Security level is always 0 or 1 at this point — records with level 2 or 3 are rejected
  before mapping (FR-006).
- Composite `status` values (e.g. "Bestilling/Under Behandling") are passed through
  unchanged (FK-2.6).
- `OpprettetAv`, `EndretAv`, `Kilde`, `KorrelasjonId`: Same rules as person endpoint above.

### Response

Same status codes and adapter actions as `PUT /api/person/v1/innmating/personer` above.

---

## POST /api/person/v1/innmating/batch — Batch Ingestion

Used for initial full load and high-volume change sets (P-08, FK-4.3). Reduces API call
count by grouping multiple records into a single request.

### Request Body

```json
{
  "Personer": [
    { /* InnmatingPersonRequest — same field shape as PUT /innmating/personer body */ }
  ],
  "Barn": [
    { /* InnmatingBarnRequest — same field shape as PUT /innmating/barn body */ }
  ]
}
```

**Ordering constraint** (FR-009): During initial full load, all `Personer` records MUST be
submitted before any `Barn` records — either in separate batch calls (persons batch first,
then children batch) or in a single batch where `Barn` is empty until all persons are sent.
PersonModule processes `Personer` before `Barn` within each batch request.

### Response

| Status | Meaning | Adapter action |
|--------|---------|----------------|
| `200 OK` | Batch processed; partial failures possible | Iterate `feil` list; each failing entry written to `feilkoe`; checkpoint advances |

**Response body**:
```json
{
  "behandlet": 42,
  "feil": [
    { "entitetType": "Person", "entitetId": "<guid>", "melding": "<string|null>" },
    { "entitetType": "Barn",   "entitetId": "<guid>", "melding": "<string|null>" }
  ]
}
```

> **Note**: The batch endpoint always returns `200 OK`. Per-record validation failures
> appear in the `feil` list — there is no `422` at the batch level. The adapter must
> iterate `feil` after every batch call and write each entry to `feilkoe`.

**Checkpoint rule** (FR-008): `UpdateCheckpointAsync` is called once after the entire
batch response is received and all `feil` entries are persisted — not per individual record.

---

## Error Handling Summary

| Response | Classification | Adapter handling |
|----------|---------------|-----------------|
| `204` (single-record) | Success (create / update / no-change) | Checkpoint advances |
| `200` (batch) | Batch success | Iterate `feil`; per-failure → `feilkoe`; checkpoint advances |
| `422` (single-record only) | Permanent / validation | Record to `feilkoe` immediately; no retry; checkpoint advances |
| `429` | Rate limit | Pause delivery for cool-down period; retry count NOT consumed; resume |
| `5xx` / timeout | Transient | Exponential backoff (configurable attempts + base delay from `ResilienceOptions`); on exhaustion → `feilkoe`; checkpoint advances |

**No record is ever silently dropped** (SC-003, P-07): every failed delivery ends up in
`feilkoe` or triggers a critical security log (Kode 6/7 path).

---

## Open Items

| ID | Item | Blocks |
|----|------|--------|
| **Å-03** | **Reference data GUID resolution**: `KjoennTypeId`, `BarnTypeId`, `BarnStatusTypeId`, `SikkerhetsnivaaTypeId` are PersonModule Guids. Adapter must resolve BiRK integer/string codes to PersonModule Guids. Resolution strategy (startup pre-load vs. runtime lookup vs. fixed mapping) and any PersonModule lookup API are not yet documented. | T018, T026 mapper full implementation |
