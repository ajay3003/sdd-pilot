# Data Model — Hendelsestjenesten

**Phase**: 1 | **Date**: 2026-04-27 | **Source**: `docs/Hendelsestjenesten-—-Domenemodell.md` (v0.3)

Full entity descriptions, field-level detail, and design rationale are in the domain model doc.
This file captures the persistence schema and key implementation constraints for the plan.

---

## Entity Map

```
Hendelse ──────────────────────── HendelsesVersjon (1..*)
   │                                      │
   │ [BarnId: UUID?]                      ├── Involvert (0..*)
   │ [TjenesteId: UUID?]                  ├── InngrepDetalj? (1:0..1)
   │ [BirkTiltakPK: int?]                 └── RommingsDetalj? (1:0..1)
   │ [AktivVersjonId → HendelsesVersjon]
   │
   └── HendelsesType (ref)                HjemmelType (ref)
                                          RommingKategoriType (ref)
                                          TvangsProtokollStatusType (ref)
```

---

## Core Tables

### Hendelse
| Column | Type | Notes |
|--------|------|-------|
| HendelsesId | UUID PK | Platform-generated |
| BarnId | UUID? | Null when received from BiRK without linked child |
| TjenesteId | UUID? | Optional contextual reference |
| BirkTiltakPK | int? | BiRK foreign key — used for async linking via TjenesteOpprettet |
| BirkHendelsesId | nvarchar(255)? | Idempotency key for adapter intake |
| HendelsesTypeId | UUID FK → HendelsesType | Immutable after creation |
| AktivVersjonId | UUID FK → HendelsesVersjon | Updated on each new version |
| OpprettetTidspunkt | datetime2 | UTC |
| OpprettetAv | UUID | Adapter process identity |
| Kilde | nvarchar(50) | e.g. 'BiRK' |
| IsAktiv | bit | Default 1; soft-delete per GL-18 (never hard-delete) |

**Indexes**: `BirkHendelsesId` (unique, for idempotency); `BarnId` (for timeline queries);
`BirkTiltakPK` + `BarnId IS NULL` (partial, for async linking lookup).

**Invariants**:
- `HendelsesTypeId` never changes after insert.
- `BarnId` transitions exactly once: NULL → UUID. Once set, locked.
- `BirkTiltakPK` and `BirkHendelsesId` never appear in API contracts.

### HendelsesVersjon
| Column | Type | Notes |
|--------|------|-------|
| HendelsesVersjonId | UUID PK | |
| HendelsesId | UUID FK → Hendelse | |
| VersjonNummer | int | Sequential, unique per HendelsesId |
| FraDato | date | Required |
| FraTidspunkt | time? | |
| TilDato | date? | |
| TilTidspunkt | time? | |
| Sted | nvarchar(500)? | |
| Beskrivelse | nvarchar(max)? | |
| ElementsReferanse | nvarchar(500)? | |
| OpprettetTidspunkt | datetime2 | UTC |
| OpprettetAv | UUID | |

**Invariants**: Append-only. No UPDATE or DELETE permitted by repository layer.

### Involvert
| Column | Type | Notes |
|--------|------|-------|
| InvolvertId | UUID PK | |
| HendelsesVersjonId | UUID FK → HendelsesVersjon | |
| InternBrukerId | UUID? | M02+: structured (M2LB user UUID) |
| EksternBeskrivelse | nvarchar(1000)? | M01: free-text from BiRK |
| Rolle | nvarchar(100)? | |

**Invariant**: Exactly one of `InternBrukerId` or `EksternBeskrivelse` is non-null.

### InngrepDetalj
| Column | Type | Notes |
|--------|------|-------|
| InngrepDetaljId | UUID PK | |
| HendelsesVersjonId | UUID FK → HendelsesVersjon | Unique |
| HjemmelTypeId | UUID FK → HjemmelType | Required |
| Politiinvolvering | bit? | |
| ProtokollNummer | int? | |
| ProtokollAar | int? | |
| TvangsProtokollStatusTypeId | UUID? FK → TvangsProtokollStatusType | |
| EnhetId | UUID? | |
| UnderretningTilBarnetDato | date? | M02+ |
| EvalueringMedBarnetDato | date? | M02+ |
| EvalueringMedLederDato | date? | M02+ |

### RommingsDetalj
| Column | Type | Notes |
|--------|------|-------|
| RommingsDetaljId | UUID PK | |
| HendelsesVersjonId | UUID FK → HendelsesVersjon | Unique |
| RommingKategoriTypeId | UUID FK → RommingKategoriType | Required |
| FoerstegangsregPolitietDato | date? | |
| FoerstegangsregPolitietTidspunkt | time? | |
| FormeltEtterlystPolitietDato | date? | |
| FormeltEtterlystPolitietTidspunkt | time? | |
| DokumentertDato | date? | |
| DokumentertTidspunkt | time? | |
| Varighet | nvarchar(200)? | |
| OriginalHendelsesId | UUID? FK → Hendelse | |

---

## Reference Tables

### HendelsesType
Kode values: `Inngrep`, `Romming`, `Uteblivelse`, `Bortforing`
Seeded at migration time; not hardcoded in application code (H-02).

### HjemmelType
Columns: `HjemmelTypeId`, `Kode`, `Beskrivelse`, `GjelderFra`, `GjelderTil?`, `BirkVerdi?`
`GjelderTil = null` means currently valid. Historical hjemler retained for backward compatibility (H-07).

### RommingKategoriType
Seeded with BiRK mapping values. `BirkVerdi` column for adapter lookup table at startup.

### TvangsProtokollStatusType
Seeded after faglig clarification (open item Å-TBD). `BirkVerdi` is integer in BiRK.

---

## Outbox Tables — Wolverine Managed

Wolverine's transactional outbox creates and manages its own tables in the same Azure SQL
database. These tables are **not** hand-crafted by the team — Wolverine migrates them automatically.

| Table | Purpose |
|-------|---------|
| `wolverine_outgoing_envelopes` | Pending outbound messages (HendelsesRegistrert, leselogg) |
| `wolverine_incoming_envelopes` | Inbox deduplication for consumed messages (TjenesteOpprettet) |
| `wolverine_dead_letters` | Dead-lettered messages for operator inspection |

No custom `HendelsesPublisering` table is needed. Both `HendelsesRegistrert` (topic
`hendelser.barn`) and leselogg events (queue `revisjon.leselogg`) are published through
Wolverine's outbox, satisfying GL-33 without any manual plumbing.

---

## Key Design Decisions

1. **No soft-delete on HendelsesVersjon** — physical append-only is the immutability guarantee.
   `Hendelse` itself uses `IsAktiv` if soft-deactivation is ever needed (GL-18 compliance).
2. **Concurrency conflict (FR-01)**: `BirkHendelsesId` unique index prevents duplicate rows.
   Last-write-wins based on source timestamp: incoming version compared against
   `HendelsesVersjon.FraDato/FraTidspunkt`; older timestamp → 204 No Content, no insert.
3. **BarnId linking lock**: After `BarnId` is set, a DB CHECK constraint or application-layer
   guard prevents re-setting. Implemented in domain service (not EF Core fluent API).
