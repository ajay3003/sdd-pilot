# Contract: Hendelsestjenesten Innmating API (consumed)

The adapter calls these endpoints to deliver translated BiRK events. Both endpoints are idempotent on `birkHendelsesId` — repeated delivery of the same payload produces no duplicates (FR-002, FR-003, spec assumption).

**Authentication**: Azure Managed Identity (bearer token via `DefaultAzureCredential`)  
**Base URL**: Configured via Azure Key Vault at startup  
**Resilience**: Polly pipeline — timeout (30 s) → retry (10x, 5 s–5 min exponential) → circuit breaker (5 failures / 30 s window, 1 min open)  
**Correlation**: `X-Correlation-Id` header set to per-event `CorrelationId` on all requests

---

## PUT /api/hendelser/v1/innmating/inngrep/{birkHendelsesId}

Delivers a `TvangsProtokoll` BiRK record as an `Inngrep` event.

**Path parameter**: `birkHendelsesId` — unique BiRK event key (string)

**Request body** (`InngrepsInnmatingRequest`):

```json
{
  "kildeId": "string (required) — set to BirkHendelsesId",
  "hendelsesTypeId": "uuid (required) — M2LB HendelsesType UUID",
  "barnId": "uuid | null",
  "birkTiltakPK": "int | null",
  "fraDato": "date (required, ISO 8601)",
  "fraTidspunkt": "time | null",
  "tilDato": "date | null",
  "tilTidspunkt": "time | null",
  "sted": "string | null",
  "beskrivelse": "string | null",
  "elementsReferanse": "string | null",
  "inngrepDetalj": {
    "hjemmelTypeId": "uuid (required)",
    "politiinvolvering": "bool | null",
    "protokollNummer": "int | null",
    "protokollAar": "int | null",
    "tvangsProtokollStatusTypeId": "uuid | null",
    "enhetId": "uuid | null",
    "underretningTilBarnetDato": "date | null",
    "evalueringMedBarnetDato": "date | null",
    "evalueringMedLederDato": "date | null"
  },
  "involverte": [
    {
      "internBrukerId": "uuid | null",
      "eksternBeskrivelse": "string | null — used for BiRK RegAv (free-text name)",
      "rolle": "string | null"
    }
  ]
}
```

**Responses**:

| HTTP | Meaning | Adapter action |
|------|---------|---------------|
| 201 Created | New event registered | Parse `HendelsesId` from response body; store in registry |
| 200 OK | Event updated | Parse `HendelsesId` from response body; upsert in registry |
| 204 No Content | No change (idempotent) | No registry update needed |
| 422 Unprocessable | Validation error | Log with full context; discard; continue (FR-012) |
| 5xx | Transient error | Retry with backoff; error queue after max retries (FR-010, FR-011) |

**Response body** (on 201/200): Contains `hendelsesId` UUID — the platform-assigned identifier stored in `BirkHendelseRegistrering`.

---

## PUT /api/hendelser/v1/innmating/romming/{birkHendelsesId}

Delivers a `Rømming` BiRK record as a `Uteblivelse` (kategori 1), `Rømming` (kategori 2), or `Bortføring` (kategori 3) event.

**Path parameter**: `birkHendelsesId` — unique BiRK event key (string)

**Request body** (`RommingsInnmatingRequest`):

```json
{
  "kildeId": "string (required) — set to BirkHendelsesId",
  "hendelsesTypeId": "uuid (required) — M2LB HendelsesType UUID (varies by kategori)",
  "barnId": "uuid | null",
  "birkTiltakPK": "int | null",
  "fraDato": "date (required, ISO 8601)",
  "fraTidspunkt": "time | null",
  "tilDato": "date | null",
  "tilTidspunkt": "time | null",
  "sted": "string | null",
  "beskrivelse": "string | null",
  "elementsReferanse": "string | null",
  "rommingsDetalj": {
    "rommingKategoriTypeId": "uuid (required)",
    "foerstegangsregPolitietDato": "date | null",
    "foerstegangsregPolitietTidspunkt": "time | null",
    "formeltEtterlystPolitietDato": "date | null",
    "formeltEtterlystPolitietTidspunkt": "time | null",
    "dokumentertDato": "date | null",
    "dokumentertTidspunkt": "time | null",
    "varighet": "string | null",
    "originalHendelsesId": "uuid | null — resolved from BirkHendelseRegistrering via OriginalRomningFk"
  },
  "involverte": [
    {
      "internBrukerId": "uuid | null",
      "eksternBeskrivelse": "string | null",
      "rolle": "string | null"
    }
  ]
}
```

**Responses**: Same as inngrep endpoint above.

---

## Fixed Field Values (adapter-set)

| Field | Value | Source |
|-------|-------|--------|
| `Kilde` | `"BiRK"` | Hardcoded (spec assumption) |
| `KallerIdentitet` | `Guid.Empty` | Adapter has no M2LB user identity |
| `KildeId` | `BirkHendelsesId` | Spec assumption |
