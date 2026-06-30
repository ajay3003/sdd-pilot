# Data Model: Tjenestemodul M01

**Phase**: 1 — Design & Contracts
**Branch**: `001-tjenestemodul-m01`
**Date**: 2026-04-13

---

## Domain Entities — Schema: `tjeneste`

### Tjeneste

The core domain entity. Represents one child's placement or service engagement from BiRK. BiRK-agnostic — no BiRK field names appear in the domain schema, except `BirkTiltakKey` which is stored as a secondary reference for Infrastructure use only and never exposed in the API.

| Field | Type | Nullable | Notes |
|-------|------|----------|-------|
| `Id` | `Guid` | No | UUID v4, domain PK, generated client-side |
| `BirkTiltakKey` | `string` | No | BiRK Tiltak primary key — Infrastructure layer only, never exposed via GraphQL (MP-03) |
| `BarnId` | `Guid` | Yes | M2LB child identifier; null until resolved via `BarnRegistrert` (FR-014) |
| `TjenesteTypeId` | `Guid` | No | FK → `TjenesteType.Id` |
| `StatusId` | `Guid` | No | FK → `TjenesteStatus.Id` |
| `AvslutningsarsakId` | `Guid` | Yes | FK → `Avslutningsarsak.Id`; null when not terminated |
| `PlanlagtInnflyttingsdato` | `DateOnly` | Yes | Planned move-in date |
| `AktuelInnflyttingsdato` | `DateOnly` | Yes | Actual move-in date |
| `PlanlagtUtflyttingsdato` | `DateOnly` | Yes | Planned move-out date |
| `AktuelUtflyttingsdato` | `DateOnly` | Yes | Actual move-out date |
| `BarnLinkageStatus` | `BarnLinkageStatus` | No | `Pending` / `Linked` / `PermanentlyUnresolved` |
| `OpprettetTidspunkt` | `DateTimeOffset` | No | Set on first insert; never updated |
| `OppdatertTidspunkt` | `DateTimeOffset` | No | Updated on every upsert from CDC |

**Unique constraint**: `BirkTiltakKey` (natural key from BiRK — basis for idempotent upserts, FR-012).

**Invariants**:
- `BarnId` transitions at most once: `null` → `{uuid}`. Re-linking or unlinking is invalid (FR-018). Domain service throws if attempting to overwrite a resolved `BarnId`.
- `BarnLinkageStatus` is a one-way state machine (see Enums section).
- Records are never hard-deleted (PP-05, assumption in spec).
- `TjenesteOpprettet` is never published until `BarnId` is non-null (FR-017). Domain service validates this before event creation.

**Sort rule** (FR-001): `AktuelInnflyttingsdato ?? PlanlagtInnflyttingsdato` descending; placements where both are null appear last.

**API visibility** (FR-003, FR-004): EF Core named query filter on `TjenesteDbContext` excludes any `Tjeneste` where `BarnLinkageStatus != Linked`. Applied globally — all case worker queries automatically exclude pending/permanently-unresolved placements.

---

### TjenesteType

Lookup table for hierarchical service types. Populated from BiRK via CDC; translated to M2LB naming.

| Field | Type | Nullable | Notes |
|-------|------|----------|-------|
| `Id` | `Guid` | No | UUID v4, domain PK |
| `BirkTypeKey` | `string` | No | BiRK primary key — Infrastructure layer only (MP-03) |
| `Navn` | `string` | No | M2LB display name |
| `NivaaPath` | `string` | No | Hierarchical path (e.g. `Plassering/Fosterhjem/Slektsfosterhjem`) |
| `OpprettetTidspunkt` | `DateTimeOffset` | No | |
| `OppdatertTidspunkt` | `DateTimeOffset` | No | |

---

### TjenesteStatus

Lookup table for placement status types.

| Field | Type | Nullable | Notes |
|-------|------|----------|-------|
| `Id` | `Guid` | No | UUID v4, domain PK |
| `BirkStatusKey` | `string` | No | BiRK primary key — Infrastructure layer only (MP-03) |
| `Kode` | `string` | No | M2LB status code |
| `Navn` | `string` | No | M2LB display name |
| `OpprettetTidspunkt` | `DateTimeOffset` | No | |

---

### Avslutningsarsak

Lookup table for termination reason types.

| Field | Type | Nullable | Notes |
|-------|------|----------|-------|
| `Id` | `Guid` | No | UUID v4, domain PK |
| `BirkArsakKey` | `string` | No | BiRK primary key — Infrastructure layer only (MP-03) |
| `Kode` | `string` | No | M2LB reason code |
| `Navn` | `string` | No | M2LB display name |
| `OpprettetTidspunkt` | `DateTimeOffset` | No | |

---

## Staging Entities — Schema: `birk_staging`

Raw BiRK data. Field names are preserved exactly as received from BiRK. These tables are internal to `M2LB.Tjeneste.Infrastructure` and are never accessed from `Domain` or `Api` projects (MP-03, MP-05). The field whitelist and all name mappings are defined in `BirkFieldMappings.json` (FR-008, FR-009).

### `birk_tiltak`

| Field | Type | Notes |
|-------|------|-------|
| `birkkey` | `string` | BiRK PK; basis for upsert |
| `[whitelisted fields]` | varies | Defined in `BirkFieldMappings.json`; all others silently dropped (FR-008) |
| `_ingestert_tidspunkt` | `DateTimeOffset` | Set on first insert |
| `_oppdatert_tidspunkt` | `DateTimeOffset` | Updated on every upsert |

### `birk_tiltakstype`, `birk_statustype`, `birk_avslutningsarsaktype`, `birk_oppdrag`

Same pattern — raw BiRK field names, whitelisted columns only, ingest timestamps.

---

## Enums

### BarnLinkageStatus

```csharp
public enum BarnLinkageStatus
{
    Pending = 0,             // BarnId not yet resolved
    Linked = 1,              // BarnId confirmed; placement visible in API
    PermanentlyUnresolved = 2 // Deadline expired; operator review required (FR-019a)
}
```

**State transitions**:

```
Pending ──BarnRegistrert──► Linked
Pending ──deadline──────────► PermanentlyUnresolved
Linked                       (terminal — no further transitions)
PermanentlyUnresolved        (terminal — no further transitions)
```

- `Pending` → `Linked`: triggered by `BarnRegistrert` consumer when a matching `BirkBarnKey` is found (FR-015)
- `Pending` → `PermanentlyUnresolved`: triggered by `BarnLinkageDeadlineService` when a configurable time threshold is exceeded (FR-019a)
- Both terminal states are irreversible (FR-018)

---

## Events

### TjenesteOpprettet (outgoing — published via Wolverine outbox)

Published to Service Bus topic `tjenester` when `BarnLinkageStatus` transitions to `Linked` (FR-016, FR-017). The outbox guarantees at-least-once delivery; consumers must be idempotent on `HendelsesId`.

| Field | Type | Notes |
|-------|------|-------|
| `HendelsesId` | `Guid` | UUID v4; Service Bus `MessageId` for duplicate detection |
| `HendelsesTidspunkt` | `DateTimeOffset` | ISO 8601 with timezone |
| `TjenesteId` | `Guid` | Internal placement identifier |
| `BirkTiltakKey` | `string` | BiRK correlator for Hendelsestjenesten (justified exception to PP-08 — required by spec) |
| `BarnId` | `Guid` | Resolved child identifier — NEVER null (FR-017) |
| `TjenesteNavn` | `string` | Hierarchical service name from `TjenesteType.NivaaPath` |
| `OpprettetTidspunkt` | `DateTimeOffset` | Domain entity creation timestamp |

### LeseloggHendelse (outgoing — published via Wolverine outbox)

Published after every case worker read operation (FR-006a, GL-32). Platform-standard schema. No PII — metadata and UUIDs only.

| Field | Type | Notes |
|-------|------|-------|
| `HendelsesId` | `Guid` | UUID v4 |
| `HendelsesTidspunkt` | `DateTimeOffset` | ISO 8601 with timezone |
| `BrukerId` | `Guid` | Actor identifier extracted from JWT claim |
| `BarnId` | `Guid` | Child identifier from query parameter |
| `OperasjonNavn` | `string` | e.g. `Tjeneste:HentTjenesterForBarn` or `Tjeneste:HentTjeneste` |
| `Tjenestenavn` | `string` | This module's registered service name |
| `KorrelasjonId` | `Guid` | Request correlation identifier from `CorrelationIdMiddleware` |

Published to Service Bus topic `leselogg` as event type `LeseloggHendelse`.

### BarnRegistrert (incoming — consumed via Wolverine handler)

Consumed from the Service Bus subscription on the Personmodulen topic (FR-015).

| Field | Type | Notes |
|-------|------|-------|
| `BirkBarnKey` | `string` | BiRK child identifier — used to match `Tjeneste` records with pending linkage |
| `BarnId` | `Guid` | M2LB child identifier to assign |

Processing is idempotent: if all placements matching `BirkBarnKey` are already `Linked`, this is a silent no-op (FR-015, GL-22).

---

## Validation Rules

| Rule | Where Enforced |
|------|---------------|
| `BarnId` is write-once (null → uuid, no overwrites) | `BarnLinkageService` in Domain |
| `TjenesteOpprettet` never published with null `BarnId` | `BarnLinkageService` validates before event creation (FR-017) |
| CDC writes are idempotent by `BirkTiltakKey` | `TjenesteRepository` uses `ExecuteUpdate` upsert (FR-012) |
| Lookup records must exist before referencing placement records | `BirkTiltakAdapter` checks staging; defers message if missing (FR-012a) |
| Only `Linked` placements returned in API queries | EF Core named query filter on `TjenesteDbContext` (FR-003) |
| Access requires explicit permission claim | Hot Chocolate `[Authorize]` + Autorisasjonsmodulen eval call (FR-005, GL-08) |
| BiRK CDC delete op (`"d"`) maps to a status transition, not a row deletion | `BirkTiltakAdapter` updates `Tjeneste.StatusId` to the relevant termination `TjenesteStatus`; the row is never deleted (PP-05). If no termination status can be resolved, the adapter logs a warning and leaves the current status unchanged. |
