# Data Model: BiRK Hendelsesadapter

The adapter owns exactly one persistent data store: the **Delivered Event Registry** (Azure SQL). All other data is transient (in-flight event payloads) or owned by external services (Event Hubs checkpoints in Blob Storage, Service Bus error queue).

---

## Delivered Event Registry — `BirkHendelseRegistrering`

**Purpose**: Maps each delivered `BirkHendelsesId` to the platform-assigned `HendelsesId` UUID returned by the Hendelsestjenesten ingestion API. Required to resolve `OriginalHendelsesId` for `Rømming` cross-references (FR-016).

**Schema** (adapter-owned Azure SQL table):

```sql
CREATE TABLE BiRKAdapter.BirkHendelseRegistrering (
    BirkHendelsesId     NVARCHAR(200)       NOT NULL,
    HendelsesId         UNIQUEIDENTIFIER    NOT NULL,
    HendelsesType       NVARCHAR(50)        NOT NULL,  -- 'Inngrep' | 'Uteblivelse' | 'Romming' | 'Bortforing'
    RegistrertTidspunkt DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_BirkHendelseRegistrering PRIMARY KEY (BirkHendelsesId)
);
```

**Invariants**:
- `BirkHendelsesId` is the unique natural key matching the path parameter in the ingestion API calls.
- `HendelsesId` is the UUID returned in the ingestion API response body.
- Records are append-only; no updates or deletes.
- `RegistrertTidspunkt` uses UTC.

**EF Core entity**:

```csharp
public class BirkHendelseRegistrering
{
    public string BirkHendelsesId { get; set; } = string.Empty;
    public Guid HendelsesId { get; set; }
    public string HendelsesType { get; set; } = string.Empty;
    public DateTime RegistrertTidspunkt { get; set; }
}
```

---

## Code Mapping Configuration

**Purpose**: Maps BiRK numeric code values to M2LB UUID identifiers at startup (FR-006). Not a database table — a static configuration structure loaded from `code-mappings.json`.

```csharp
public class CodeMappingOptions
{
    public Dictionary<int, Guid> HjemmelType { get; set; } = [];
    public Dictionary<int, Guid> TvangsProtokollStatusType { get; set; } = [];
    public Dictionary<int, Guid> RommingKategoriType { get; set; } = [];
}
```

**Validation at startup**: All three dictionaries must be non-empty. Any event containing an unmapped code value results in the event being moved to the error queue (FR-006 acceptance scenario 3).

**Example `code-mappings.json`**:

```json
{
  "CodeMappings": {
    "HjemmelType": {
      "1": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
      "5": "yyyyyyyy-yyyy-yyyy-yyyy-yyyyyyyyyyyy"
    },
    "TvangsProtokollStatusType": {
      "1": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
    },
    "RommingKategoriType": {
      "1": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
      "2": "cccccccc-cccc-cccc-cccc-cccccccccccc",
      "3": "dddddddd-dddd-dddd-dddd-dddddddddddd"
    }
  }
}
```

---

## In-Flight Domain Types (no persistence)

These types exist only within the processing pipeline and are not persisted.

### `BirkCdcEvent`

Represents a single CDC record read from Event Hubs before translation.

```csharp
public record BirkCdcEvent(
    string BirkHendelsesId,
    string Tabell,           // "TvangsProtokoll" | "Romming"
    string Operasjon,        // "INSERT" | "UPDATE" | "DELETE"
    JsonElement Payload,
    string CorrelationId,
    DateTimeOffset EnqueuedTime
);
```

### `TjenesteoppslagResultat`

Result of the synchronous `BirkTiltakPK` lookup against Tjeneste.

```csharp
public record TjenesteoppslagResultat(
    Guid? BarnId,
    Guid? TjenesteId
);
```

### `InnmatingResultat` (mirroring Hendelsestjenesten's result)

```csharp
public enum InnmatingResultat
{
    Opprettet,
    Oppdatert,
    Uendret
}
```

---

## State Transitions

```
CDC Event received
    │
    ▼
[DELETE?] ──yes──► Log and discard (no delivery)
    │
    no
    ▼
Code mapping lookup
    │
    ├─[code not found]──► Move to error queue + alert
    │
    ▼
Tjeneste lookup (BirkTiltakPK → BarnId + TjenesteId)
    │
    ├─[no match]──► Continue with BarnId=null (FR-005)
    ├─[unavailable]──► Retry → max retries exceeded → error queue
    │
    ▼
Build InnmatingRequest
    │
    ▼
PUT to Hendelsestjenesten
    │
    ├─[422 validation error]──► Log, discard, continue (FR-012)
    ├─[5xx transient]──────────► Retry (exponential backoff) → error queue on max retries
    │
    ▼
Parse HendelsesId from response
    │
    ▼
Upsert BirkHendelseRegistrering
    │
    ▼
Checkpoint Event Hubs offset
```
