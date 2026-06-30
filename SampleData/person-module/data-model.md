# Data Model: Person Module Core

**Branch**: `001-person-module` | **Date**: 2026-03-06
**Source**: `docs/person-domain-model-no.md` (authoritative), enriched with spec clarifications

---

## 1. Entity Overview

```
Person ──(1:1)──> BarnIAndrelinjeBarnevern ──(1:N)──> BarnStatusHistorikk
   │                    │
   │                    ├──> BarnType (ref)
   │                    ├──> BarnStatusType (ref)
   │                    ├──> SikkerhetsnivaaType (ref)
   │                    └──> Kommune (ref)
   │
   └──> KjønnType (ref)

OutboxMessage (infrastructure — not a domain entity)
```

---

## 2. Core Entities

### 2.1 Person

**Table**: `Person`

| Column | Type | Nullable | Constraints | Notes |
|--------|------|----------|-------------|-------|
| PersonId | UNIQUEIDENTIFIER | No | PK, Default: NEWID() | Generated client-side (UUID v4). PS-04. |
| EksternId | NVARCHAR(255) | Yes | UNIQUE (filtered: NOT NULL) | BiRK Party-ID for migration traceability. Never used as primary identifier. |
| Navn | NVARCHAR(500) | No | NOT NULL | Full name, single field matching BiRK. |
| Foedselsnummer | NVARCHAR(11) | Yes | UNIQUE (filtered: NOT NULL), CHECK (LEN=11) | Norwegian national ID. Unique across active persons. |
| UsikkerFoedselsnummer | NVARCHAR(11) | Yes | | Provisional/uncertain national ID. Structural state, not a quality problem. |
| DUFNummer | NVARCHAR(50) | Yes | UNIQUE (filtered: NOT NULL) | Retained as historical secondary ID after fødselsnummer upgrade (FR-032). |
| Foedselsdato | DATE | Yes | | Exact birth date. Null for unborn children. |
| UsikkerFoedselsdato | DATE | Yes | | Provisional/estimated birth date for unborn or unknown. |
| KjønnTypeId | UNIQUEIDENTIFIER | No | FK → KjønnType | |
| ErAktiv | BIT | No | Default: 1 | Soft deactivation only. Never hard-deleted (PP-05). |
| OpprettetTidspunkt | DATETIME2 | No | Default: GETUTCDATE() | |
| OpprettetAv | UNIQUEIDENTIFIER | No | | EntraID user/process UUID. |
| EndretTidspunkt | DATETIME2 | No | Default: GETUTCDATE() | Updated on every mutation. |
| EndretAv | UNIQUEIDENTIFIER | No | | |
| Kilde | NVARCHAR(50) | No | | e.g. "BiRK-adapter", "Manuell", "System". |

**Indexes**:
- `IX_Person_EksternId` (non-clustered, filtered: EksternId IS NOT NULL)
- `IX_Person_Foedselsnummer` (non-clustered, filtered: Foedselsnummer IS NOT NULL)
- `IX_Person_DUFNummer` (non-clustered, filtered: DUFNummer IS NOT NULL)
- Full-text index on `Navn` for partial-name search (FR-001)

**Invariants** (enforced in domain layer):
- PersonId is the sole primary identifier — never a national ID
- Fødselsnummer, if set, is unique among all active persons
- DUFNummer, if set, is unique among all active persons
- Person is never physically deleted

---

### 2.2 BarnIAndrelinjeBarnevern

**Table**: `BarnIAndrelinjeBarnevern`

| Column | Type | Nullable | Constraints | Notes |
|--------|------|----------|-------------|-------|
| BarnRegistreringId | UNIQUEIDENTIFIER | No | PK | UUID v4, generated client-side. |
| PersonId | UNIQUEIDENTIFIER | No | FK → Person, UNIQUE | 1:1 with Person. One active registration per Person. |
| BirkId | NVARCHAR(100) | No | UNIQUE | BiRK system child identifier. |
| BarnTypeId | UNIQUEIDENTIFIER | No | FK → BarnType | |
| BarnStatusTypeId | UNIQUEIDENTIFIER | No | FK → BarnStatusType | Current status. History in BarnStatusHistorikk. |
| SikkerhetsnivaaTypeId | UNIQUEIDENTIFIER | No | FK → SikkerhetsnivaaType | Governs visibility. Mandatory (default: Nivå 0). |
| KommuneNr | NVARCHAR(4) | No | FK → Kommune, CHECK (LEN=4) | |
| OpprettetTidspunkt | DATETIME2 | No | Default: GETUTCDATE() | |
| OpprettetAv | UNIQUEIDENTIFIER | No | | |
| EndretTidspunkt | DATETIME2 | No | Default: GETUTCDATE() | |
| EndretAv | UNIQUEIDENTIFIER | No | | |
| Kilde | NVARCHAR(50) | No | | |

**Indexes**:
- `IX_BarnIAndrelinjeBarnevern_PersonId` (UNIQUE — enforces 1:1)
- `IX_BarnIAndrelinjeBarnevern_BirkId` (UNIQUE)
- `IX_BarnIAndrelinjeBarnevern_BarnStatusTypeId` (for filter performance)
- `IX_BarnIAndrelinjeBarnevern_SikkerhetsnivaaTypeId` (critical for security filter)
- `IX_BarnIAndrelinjeBarnevern_KommuneNr` (for municipality filter)
- Composite: `IX_Barn_Search` on (SikkerhetsnivaaTypeId, BarnStatusTypeId, KommuneNr) for filtered search

**Invariants** (enforced in domain layer):
- PersonId must reference an existing Person
- A Person can have at most one BarnIAndrelinjeBarnevern (UNIQUE constraint on PersonId)
- BirkId must be unique across all registrations
- SikkerhetsnivaaTypeId is mandatory
- Security level changes trigger SikkerhetsnivåEndret event (security-critical)
- Never physically deleted

---

### 2.3 BarnStatusHistorikk

**Table**: `BarnStatusHistorikk`

Append-only. Records every BarnStatusType transition. Backing store for FR-012.

| Column | Type | Nullable | Constraints | Notes |
|--------|------|----------|-------------|-------|
| HistorikkId | UNIQUEIDENTIFIER | No | PK | UUID v4. |
| BarnRegistreringId | UNIQUEIDENTIFIER | No | FK → BarnIAndrelinjeBarnevern | |
| ForrigeBarnStatusTypeId | UNIQUEIDENTIFIER | Yes | FK → BarnStatusType | Null for initial registration. |
| NyBarnStatusTypeId | UNIQUEIDENTIFIER | No | FK → BarnStatusType | |
| ErForventetOvergang | BIT | No | | Per known BiRK state machine. |
| Tidsstempel | DATETIME2 | No | Default: GETUTCDATE() | |
| UtfoertAv | UNIQUEIDENTIFIER | No | | |
| Kilde | NVARCHAR(50) | No | | |

**Indexes**:
- `IX_BarnStatusHistorikk_BarnRegistreringId` (for profile history query — FR-012)

**Invariants**:
- Rows are never deleted (PP-05)
- Written in the same DB transaction as the BarnIAndrelinjeBarnevern status update

---

## 3. Reference Data

All reference data is domain-local configuration stored as DB rows. Auto-created from BiRK values in Phase 1 (FR-018).

### 3.1 KjønnType

**Table**: `KjønnType`

| Column | Type | Nullable | Constraints |
|--------|------|----------|-------------|
| KjønnTypeId | UNIQUEIDENTIFIER | No | PK |
| Verdi | NVARCHAR(100) | No | UNIQUE |
| Beskrivelse | NVARCHAR(500) | No | |
| ErAktiv | BIT | No | Default: 1 |
| SorteringsRekkefoelge | INT | No | |

**Seed data**: Gutt, Jente, Ukjent (from BiRK)

---

### 3.2 BarnType

**Table**: `BarnType`

| Column | Type | Nullable | Constraints |
|--------|------|----------|-------------|
| BarnTypeId | UNIQUEIDENTIFIER | No | PK |
| Verdi | NVARCHAR(100) | No | UNIQUE |
| Beskrivelse | NVARCHAR(500) | No | |
| ErAktiv | BIT | No | Default: 1 |
| SorteringsRekkefoelge | INT | No | |

**Seed data**: Ordinær, EMA, Ufødt

---

### 3.3 BarnStatusType

**Table**: `BarnStatusType`

| Column | Type | Nullable | Constraints |
|--------|------|----------|-------------|
| BarnStatusTypeId | UNIQUEIDENTIFIER | No | PK |
| Verdi | NVARCHAR(200) | No | UNIQUE |
| Beskrivelse | NVARCHAR(500) | No | |
| ErAktiv | BIT | No | Default: 1 |
| SorteringsRekkefoelge | INT | No | |

**Seed data** (from BiRK, BiRK-authoritative in Phase 1):

| Verdi | Sort |
|-------|------|
| Bestilling/Under Behandling | 1 |
| ReservertTiltak | 2 |
| UavklartTiltak | 3 |
| ITiltak | 4 |
| Avsluttet | 5 |
| Ukjent | 99 |

**State machine — known expected transitions** (from domain model):
```
Bestilling/Under Behandling → ReservertTiltak
Bestilling/Under Behandling → UavklartTiltak
ReservertTiltak             → ITiltak
UavklartTiltak              → ITiltak
UavklartTiltak              → Avsluttet
ITiltak                     → Avsluttet
```
Any other transition: `ErForventetOvergang = false` in `BarnStatusEndret` event.
Note: BiRK is authoritative for *values*; transition order is not locally enforced in Phase 1.

---

### 3.4 SikkerhetsnivaaType

**Table**: `SikkerhetsnivaaType`

| Column | Type | Nullable | Constraints |
|--------|------|----------|-------------|
| SikkerhetsnivaaTypeId | UNIQUEIDENTIFIER | No | PK |
| Nivaa | INT | No | UNIQUE — critical for security comparisons |
| Verdi | NVARCHAR(100) | No | UNIQUE |
| BiRKKode | NVARCHAR(50) | Yes | |
| ElementsKode | NVARCHAR(50) | Yes | |
| Beskrivelse | NVARCHAR(500) | No | |
| KreverGradertTilgang | BIT | No | |
| ErAktiv | BIT | No | Default: 1 |
| SorteringsRekkefoelge | INT | No | |

**Seed data** (fixed — per Norwegian child welfare legislation):

| Nivaa | Verdi | BiRKKode | ElementsKode | KreverGradertTilgang |
|-------|-------|----------|--------------|----------------------|
| 0 | Ingen | NULL | NULL | 0 |
| 1 | SkjultAdresse | NULL | NULL | 0 |
| 2 | Kode7 | Kode 7 | K1 | 1 |
| 3 | Kode6 | Kode 6 | K2 | 1 |

**Critical invariant**: `Nivaa` is used for numeric security comparisons in all queries. Never modify the Nivaa values for seeded rows.

---

### 3.5 Kommune

**Table**: `Kommune`

| Column | Type | Nullable | Constraints |
|--------|------|----------|-------------|
| KommuneNr | NVARCHAR(4) | No | PK, CHECK (LEN=4) |
| Navn | NVARCHAR(200) | No | |
| ErAktiv | BIT | No | Default: 1 |

**Invariant**: Merged/dissolved municipalities are deactivated, not deleted (historical records preserved — PP-05).

---

## 4. Infrastructure Entity

### 4.1 OutboxMessage

**Table**: `OutboxMessage` — NOT a domain entity; infrastructure concern.

| Column | Type | Nullable | Constraints | Notes |
|--------|------|----------|-------------|-------|
| MessageId | UNIQUEIDENTIFIER | No | PK | UUID v4. Also used as Service Bus MessageId for deduplication. |
| TopicName | NVARCHAR(200) | No | | e.g. "person.person", "person.barn", "person.audit" |
| SessionId | NVARCHAR(200) | No | | Entity UUID (PersonId or BarnRegistreringId). Ensures ordered delivery per entity. |
| Subject | NVARCHAR(200) | No | | Event type name, e.g. "PersonOpprettet". "CRITICAL" suffix for SikkerhetsnivåEndret. |
| Payload | NVARCHAR(MAX) | No | | JSON-serialized event envelope. Never contains personal data (FR-026). |
| Priority | NVARCHAR(10) | No | Default: 'Normal' | 'High' for SikkerhetsnivåEndret (FR-025). |
| CreatedAt | DATETIME2 | No | Default: GETUTCDATE() | |
| PublishedAt | DATETIME2 | Yes | | Set by outbox poller on successful publish. |
| Attempts | INT | No | Default: 0 | Retry counter. |
| Status | NVARCHAR(20) | No | Default: 'Pending' | Pending / Published / Failed |

**Indexes**:
- `IX_OutboxMessage_Status_CreatedAt` on (Status, CreatedAt) — for poller query efficiency
- Rows are never deleted (could be archived after 30 days)

**Outbox poller behavior**:
1. Every 1–2 seconds: `SELECT TOP 50 WHERE Status = 'Pending' ORDER BY CreatedAt ASC`
2. Publish to Service Bus with SessionId and Priority set
3. On success: UPDATE Status = 'Published', PublishedAt = NOW()
4. On failure: UPDATE Attempts++; if Attempts > 5, Status = 'Failed' and alert

---

## 5. Entity Relationship Diagram (textual)

```
Person (PersonId PK)
  │ 1
  │ 1 (UNIQUE FK)
  ▼
BarnIAndrelinjeBarnevern (BarnRegistreringId PK)
  │ 1
  │ N
  ▼
BarnStatusHistorikk (HistorikkId PK)

BarnIAndrelinjeBarnevern.BarnTypeId         ──> BarnType.BarnTypeId
BarnIAndrelinjeBarnevern.BarnStatusTypeId   ──> BarnStatusType.BarnStatusTypeId
BarnIAndrelinjeBarnevern.SikkerhetsnivaaTypeId ──> SikkerhetsnivaaType.SikkerhetsnivaaTypeId
BarnIAndrelinjeBarnevern.KommuneNr          ──> Kommune.KommuneNr
Person.KjønnTypeId                          ──> KjønnType.KjønnTypeId
BarnStatusHistorikk.ForrigeBarnStatusTypeId ──> BarnStatusType.BarnStatusTypeId (nullable)
BarnStatusHistorikk.NyBarnStatusTypeId      ──> BarnStatusType.BarnStatusTypeId

OutboxMessage (standalone — infrastructure)
```

---

## 6. EF Core Migration Notes

- Initial migration seeds all reference data (KjønnType, BarnType, BarnStatusType, SikkerhetsnivaaType, Kommune seed data from Statistics Norway)
- `HasData()` seed for SikkerhetsnivaaType rows 0–3 with fixed UUIDs (referenced in domain logic)
- All UUID PKs use `ValueGeneratedNever()` — generated client-side
- `BarnStatusHistorikk` mapped with `HasNoKey()` override removed — has PK, but no update operations
- Full-text index on `Person.Navn` added via `migrationBuilder.Sql("CREATE FULLTEXT INDEX ...")`
