# Data Model: BiRK Person-adapter

**Phase**: 1 — Design
**Feature**: BiRK Person-adapter
**Date**: 2026-04-20

---

## Overview

The adapter maintains exactly two categories of persistent state (constitution P-05):

1. **Stream Checkpoint** — Event Hubs offset per partition (Azure Blob Storage via `BlobCheckpointStore`, managed by `EventProcessorClient`)
2. **Fault Queue (`feilkoe`)** — failed delivery records awaiting re-processing (Azure SQL)

No person data, reference data, or business entities are stored beyond these two categories.
Any personal data that appears in the fault queue `payload` column is deleted on successful
re-delivery or after the configured maximum retention period (30-day default).

---

## Persistent Entities

### FaultQueueEntry — `feilkoe` table (Azure SQL)

A CDC record that could not be delivered to PersonModule after exhausting all retry attempts,
or that received an immediate 422 validation failure. Retained for background re-processing.

| Column | SQL type | Nullable | Description |
|--------|----------|----------|-------------|
| `id` | `uniqueidentifier` PK | No | Surrogate key; generated on insert |
| `birk_id` | `nvarchar(100)` | No | BiRK record identifier (PersonPK or BirkID); not PII by itself; used for correlation |
| `post_type` | `nvarchar(50)` | No | Record type: `PERSON`, `BARN`, `REFERANSEDATA` |
| `feiltype` | `nvarchar(50)` | No | Error category: `FORBIGAAENDE` (transient 5xx/timeout) or `VALIDERING` (422) |
| `feilmelding` | `nvarchar(500)` | No | Human-readable error message; MUST NOT contain personal data |
| `antall_forsok` | `int` | No | Number of delivery attempts (incremented on each retry) |
| `siste_forsok_tidspunkt` | `datetime2` | Yes | Timestamp of most recent delivery attempt; null if no retry yet |
| `opprettet_tidspunkt` | `datetime2` | No | Timestamp when the entry was first created |
| `utloper_tidspunkt` | `datetime2` | No | Auto-purge timestamp: `opprettet_tidspunkt` + `MaxRetentionPeriod` (default 30 days) |
| `payload` | `nvarchar(max)` | Yes | Transformed PersonModule-format payload (JSON); **contains personal data**; deleted on successful re-delivery or on `utloper_tidspunkt` |

**Indexes**:
- Primary key: `id`
- `(feiltype, post_type)` — filtered re-delivery queries
- `utloper_tidspunkt` — expiry purge batch job
- `siste_forsok_tidspunkt` — re-attempt scheduling

**Data classification**: The `payload` column contains personal data (names, national ID, DUF
number). Requirements:
- Azure SQL Transparent Data Encryption (TDE) enabled
- Access via Managed Identity only (FK-9.3)
- Private endpoint — no public access
- `payload` deleted immediately on successful re-delivery (FK-5.4)
- Entire row (including `payload`) auto-purged when `utloper_tidspunkt` is reached (FR-016)

---

### StreamCheckpoint — Azure Blob Storage

Managed entirely by `EventProcessorClient` / `BlobCheckpointStore`. Not a SQL table.
Represented as structured blobs in a dedicated container.

| Property | Description |
|----------|-------------|
| PartitionId | Event Hubs partition identifier |
| Offset | Byte offset in the Event Hub stream (string) |
| SequenceNumber | Monotonically increasing event sequence number |
| Blob path | `{container}/{event-hub-name}/{consumer-group}/{partition-id}` |

**Checkpoint advancement rule** (FR-008): `UpdateCheckpointAsync` is called once per batch,
AFTER PersonModule confirms delivery of the entire batch. Never called before confirmed delivery.
For Kode 6/7 rejections (FR-007) and silently discarded events (FR-002, FR-022), the checkpoint
still advances — stream processing must not block on rejected or filtered records.

**Expiry definition** (FR-011): The checkpoint is considered expired when the stored `Offset`
is no longer within the Event Hub retention window. `EventProcessorClient` surfaces this as a
partition-level error. The adapter detects this, logs the condition, triggers an operational
alert, and initiates a new full load.

---

## In-Transit Objects (not persisted by adapter)

These types flow through the processing pipeline but are never stored in the adapter's state.

### BiRK CDC Event — incoming from Event Hubs

Deserialized from `EventData.Body` (JSON produced by Debezium CDC pipeline).

| Field | Description |
|-------|-------------|
| `operasjon` | CDC operation: `c` (create), `u` (update), `d` (delete) |
| `tabellnavn` | Source BiRK table name — used for record-type routing |
| `sikkerhetsnivaa` | Security level integer (0–3); evaluated FIRST, before any other processing |
| `payload` | BiRK record fields — exact field names per `birk-person-feltmapping.md` (Å-01) |

**Routing decision tree**:
1. `sikkerhetsnivaa` == 2 or 3 → **reject** (FR-006); checkpoint advances (FR-007)
2. `operasjon` == `d` → **discard silently** (FR-022); checkpoint advances
3. `tabellnavn` matches organizational entity → **discard silently** (FR-002); checkpoint advances
4. Otherwise → transform and deliver

### PersonRecord — outgoing to `PUT /api/person/v1/innmating/personer`

| Field | Type | Notes |
|-------|------|-------|
| `PersonId` | Guid | Stable Guid derived deterministically from BiRK PersonPK — identical on every delivery (idempotency) |
| `EksternId` | string? | BiRK PersonPK — the only BiRK reference in PersonModule's API (P-01, ADR-008) |
| `Navn` | string | Full name — BiRK source field per Å-01 |
| `Foedselsnummer` | string? | National ID; null for unborn children / EMA |
| `UsikkerFoedselsnummer` | string? | Uncertain national ID |
| `DUFNummer` | string? | DUF number |
| `Foedselsdato` | DateOnly? | Date of birth |
| `UsikkerFoedselsdato` | DateOnly? | Uncertain date of birth |
| `KjoennTypeId` | Guid | PersonModule Guid for gender type — requires GUID resolution (Å-03) |
| `OpprettetAv` | Guid | System identity Guid for the adapter service account (from config) |
| `EndretAv` | Guid | System identity Guid for the adapter service account (from config) |
| `Kilde` | string | Fixed value `"BiRK"` for all adapter-sourced records |
| `KorrelasjonId` | Guid | Fresh `Guid.NewGuid()` per delivery call |
| `BirkEndringstidspunkt` | DateTimeOffset? | BiRK change timestamp from CDC event (Å-01 for exact source field) |

### ChildRegistrationRecord — outgoing to `PUT /api/person/v1/innmating/barn`

| Field | Type | Notes |
|-------|------|-------|
| `BarnRegistreringId` | Guid | Stable Guid derived deterministically from BiRK BirkId |
| `PersonId` | Guid | Stable Guid of the parent person — same derivation as `PersonRecord.PersonId` from the parent's BiRK PersonPK |
| `BirkId` | string | BiRK child registration identifier |
| `BarnTypeId` | Guid | PersonModule Guid for child type — requires GUID resolution (Å-03) |
| `BarnStatusTypeId` | Guid | PersonModule Guid for child status — requires GUID resolution (Å-03) |
| `SikkerhetsnivaaTypeId` | Guid | PersonModule Guid for security level — always 0 or 1 at this point (FR-006); requires GUID resolution (Å-03) |
| `KommuneNr` | string | Municipality code — passed through as string (no GUID lookup needed) |
| `OpprettetAv` | Guid | System identity Guid (from config) |
| `EndretAv` | Guid | System identity Guid (from config) |
| `Kilde` | string | Fixed value `"BiRK"` |
| `KorrelasjonId` | Guid | Fresh `Guid.NewGuid()` per delivery call |
| `BirkEndringstidspunkt` | DateTimeOffset? | BiRK change timestamp from CDC event (Å-01) |

### ~~ReferenceDataRecord~~ — not delivered to PersonModule

Reference data CDC events (gender types, child types, status types, security level types,
municipalities) are **silently discarded** at the routing step. PersonModule auto-creates
unknown reference data values when it first receives a person or child registration
containing an unrecognised type code — no dedicated delivery path is needed.

The type ID fields in `PersonRecord` and `ChildRegistrationRecord` (`KjoennTypeId`,
`BarnTypeId`, `BarnStatusTypeId`, `SikkerhetsnivaaTypeId`) require GUID resolution to
convert BiRK codes into PersonModule Guids — see open item **Å-03**.

---

## State Transitions

### Fault Queue Entry Lifecycle

```
[CDC delivery fails after max retries]  OR  [422 validation failure]
        |
        v
  CREATED — feilkoe row inserted
    antall_forsok = retry count at time of failure
    utloper_tidspunkt = opprettet_tidspunkt + MaxRetentionPeriod
        |
        v (BackgroundService polls at configured interval)
        |
        +-- Delivery succeeds ──────────────> DELETED
        |                                   (payload column cleared, row removed)
        |
        +-- Delivery fails ────────────────> UPDATED
        |                                   (antall_forsok++, siste_forsok_tidspunkt updated)
        |
        +-- utloper_tidspunkt reached ─────> AUTO-PURGED
                                            (row and payload deleted, purge logged as
                                            unresolved delivery failure per FR-016)
```

### CDC Event Processing Lifecycle

```
EventProcessorClient receives EventData batch
        |
        v
[1] Security level check (FR-006)
    level 2 or 3 ──> REJECTED: critical log + mandatory-acknowledgment alert + checkpoint advances
    level 0 or 1 ──> continue
        |
        v
[2] Operation type check (FR-022)
    delete ─────────> DISCARDED silently; checkpoint advances
    create / update ─> continue
        |
        v
[3] Record type routing (FR-002)
    organizational entity ─> DISCARDED silently; checkpoint advances
    reference data ──────────> DISCARDED silently; checkpoint advances
                               (PersonModule auto-creates on first receipt of person/child)
    person / child ──────────> continue
        |
        v
[4] Transform: BiRK fields → PersonModule format  (IPersonMapper / IChildRegistrationMapper)
        |
        v
[5] Deliver to PersonModule API
    204 ──────────────────────> SUCCESS (any outcome); checkpoint advances
    200 (batch only) ─────────> CHECK feil list; per-failure → feilkoe; checkpoint advances
    429 ──────────────────────> RATE-LIMITED: cool-down pause (retry count unchanged); resume
    5xx / timeout ────────────> RETRY: exponential backoff; on exhaustion → feilkoe created
    422 (single-record only) ─> VALIDATION FAILURE: feilkoe created immediately; checkpoint advances
```
