# Person Module — Event Contracts (Implementation Reference)

**Authoritative source**: `docs/person-event-contracts-no.md`
**Branch**: `001-person-module` | **Date**: 2026-03-06

---

## Infrastructure

- **Transport**: Azure Service Bus Topics
- **Session ordering**: `SessionId = entity UUID` (PersonId for person events, BarnRegistreringId for child events) — required per FR-027
- **At-least-once delivery**: Consumers MUST be idempotent, deduplicated by `HendelsesId`
- **Outbox pattern**: All events written to `OutboxMessage` table in the same DB transaction as the mutation, then published by `OutboxPublisherHostedService`

---

## Common Event Envelope

All events share this wrapper (serialized as JSON in `OutboxMessage.Payload`):

```json
{
  "HendelsesId": "<UUID v4>",
  "HendelsesType": "PersonOpprettet",
  "HendelsesTidspunkt": "2026-03-06T12:00:00Z",
  "UtfoertAv": "<UUID — EntraID user or process identity>",
  "KorrelasjonId": "<UUID — propagated from request>",
  "Kilde": "BiRK-adapter | Manuell | System",
  "Data": { /* event-specific fields below */ }
}
```

**Privacy invariant (FR-026, PP-03)**: `Data` MUST contain only UUIDs and metadata. Never names, national IDs, addresses, or family information.

---

## Topic: `person.person`

### PersonOpprettet

Published when a new Person is created.

```json
{
  "Data": {
    "PersonId": "<UUID>",
    "HarFoedselsnummer": false,
    "HarDUFNummer": false,
    "KjønnTypeId": "<UUID>"
  }
}
```

**Service Bus**: `Subject = "PersonOpprettet"`, `SessionId = PersonId`

---

### PersonOppdatert

Published when a Person's data changes (name, birth date, identity numbers, gender).

```json
{
  "Data": {
    "PersonId": "<UUID>",
    "EndredeFelter": ["Navn", "Foedselsdato"],
    "FoedselsnummerEndret": false,
    "DUFNummerEndret": true
  }
}
```

**Service Bus**: `Subject = "PersonOppdatert"`, `SessionId = PersonId`

---

## Topic: `person.barn`

### BarnRegistrert

Published when a Person is registered as a child in 2nd-line child welfare.

```json
{
  "Data": {
    "PersonId": "<UUID>",
    "BarnRegistreringId": "<UUID>",
    "BirkId": "BIRK-B-12345",
    "BarnTypeId": "<UUID>",
    "BarnStatusTypeId": "<UUID>",
    "SikkerhetsnivaaTypeId": "<UUID>",
    "KommuneNr": "0301"
  }
}
```

**Service Bus**: `Subject = "BarnRegistrert"`, `SessionId = BarnRegistreringId`

---

### BarnStatusEndret

Published when a child's BarnStatusType changes.

```json
{
  "Data": {
    "PersonId": "<UUID>",
    "BarnRegistreringId": "<UUID>",
    "BirkId": "BIRK-B-12345",
    "ForrigeBarnStatusTypeId": "<UUID>",
    "NyBarnStatusTypeId": "<UUID>",
    "ErForventetOvergang": true
  }
}
```

**`ErForventetOvergang`**: `true` if the transition is in the known expected set (per BarnStatusType state machine in data-model.md); `false` for anomalous transitions (FR-021).

**Service Bus**: `Subject = "BarnStatusEndret"`, `SessionId = BarnRegistreringId`

---

### SikkerhetsnivåEndret ⚠️ SECURITY-CRITICAL

Published when a child's security classification changes. **Must be processed with priority.**

```json
{
  "Data": {
    "PersonId": "<UUID>",
    "BarnRegistreringId": "<UUID>",
    "BirkId": "BIRK-B-12345",
    "ForrigeSikkerhetsnivaaTypeId": "<UUID>",
    "NySikkerhetsnivaaTypeId": "<UUID>",
    "ForrigeNivaa": 0,
    "NyttNivaa": 3
  }
}
```

**`ForrigeNivaa` / `NyttNivaa`**: Integer 0–3. Included for fast evaluation by subscribers without a reference data lookup.

**Service Bus**:
- `Subject = "SikkerhetsnivaaEndret_CRITICAL"`
- `SessionId = BarnRegistreringId`
- Custom property: `Priority = "High"` (FR-025 — minimise the window where protection is not reflected)

---

### BarnKommuneEndret

Published when a child's municipality changes.

```json
{
  "Data": {
    "PersonId": "<UUID>",
    "BarnRegistreringId": "<UUID>",
    "BirkId": "BIRK-B-12345",
    "ForrigeKommuneNr": "0301",
    "NyKommuneNr": "1103"
  }
}
```

**Service Bus**: `Subject = "BarnKommuneEndret"`, `SessionId = BarnRegistreringId`

---

### BarnTypeEndret

Published when a child's BarnType changes (e.g., from `Ufødt` to `Ordinær`).

```json
{
  "Data": {
    "PersonId": "<UUID>",
    "BarnRegistreringId": "<UUID>",
    "BirkId": "BIRK-B-12345",
    "ForrigeBarnTypeId": "<UUID>",
    "NyBarnTypeId": "<UUID>"
  }
}
```

**Service Bus**: `Subject = "BarnTypeEndret"`, `SessionId = BarnRegistreringId`

---

## Topic: `person.audit` (Internal)

Published for every data mutation per FR-028. Consumed by the platform-level Audit service.

```json
{
  "Data": {
    "UtfoertAv": "<UUID>",
    "Handling": "Opprettet | Endret | Deaktivert | StatusEndret | SikkerhetsnivaaEndret",
    "EntitetType": "Person | BarnIAndrelinjeBarnevern | BarnStatusHistorikk",
    "EntitetId": "<UUID>",
    "FoerTilstand": { /* JSON snapshot, null on creation */ },
    "EtterTilstand": { /* JSON snapshot */ },
    "Kilde": "BiRK-adapter | Manuell | System",
    "Tidsstempel": "2026-03-06T12:00:00Z"
  }
}
```

**Privacy**: `FoerTilstand` and `EtterTilstand` contain field names and UUID values only — never raw personal data values in the audit event. The separate Audit service is responsible for persisting these immutably.

**Service Bus**: `Subject = "RevisjonsHendelse"`, `SessionId = EntitetId`

---

## Operation Registration Topic: `operasjonsregistrering`

Published at service startup via `IHostedService` (PS-06, FR-029).

```json
[
  { "OperasjonId": "Person:SoekBarn", "KlassifiseringForslag": "Generell" },
  { "OperasjonId": "Person:SeBarnGrunnprofil", "KlassifiseringForslag": "Generell" },
  { "OperasjonId": "Person:SeBarnProfil", "KlassifiseringForslag": "Barnespesifikk" },
  { "OperasjonId": "Person:SeFullIdentitet", "KlassifiseringForslag": "Barnespesifikk" },
  { "OperasjonId": "Person:SeGradertBarn", "KlassifiseringForslag": "Barnespesifikk" },
  { "OperasjonId": "Person:AdministerGradertBarntilgang", "KlassifiseringForslag": "Barnespesifikk" },
  { "OperasjonId": "Person:SeRevisjonslogg", "KlassifiseringForslag": "Generell" }
]
```

Note: Norwegian ø/æ/å characters should be replaced with oe/ae/aa
