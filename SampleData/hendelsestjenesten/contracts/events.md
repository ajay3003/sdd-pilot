# Contract — Event Contracts

**Full specification**: `docs/Hendelsestjenesten-—-Hendelseskontrakter.md`

---

## Published Events

### Topic: `hendelser.barn`

#### HendelsesRegistrert
Trigger: Successful intake with BarnId set, OR async linking completed.
Never published with BarnId = null.

```json
{
  "HendelsesId": "<UUID>",
  "HendelsesType": "HendelsesRegistrert",
  "HendelsesTidspunkt": "<UTC datetime>",
  "UtførtAv": "<UUID>",
  "KorrelasjonsId": "<UUID>",
  "Kilde": "BiRK",
  "Data": {
    "BarnHendelseId": "<UUID>",
    "BarnId": "<UUID>",
    "TjenesteId": "<UUID | null>",
    "HendelsesTypeId": "<UUID>",
    "HendelsesTypeKode": "Inngrep",
    "HendelsesTypeNavn": "Inngrep",
    "FraDato": "2026-03-24",
    "FraTidspunkt": "08:30:00"
  }
}
```

No personal data in payload — UUIDs and metadata only (GL-21).

---

## Published to Queue: `revisjon.leselogg`

#### Leselogg-hendelse
Published after every `hentHendelserForBarn` and `hentHendelse` call (GL-32, ADR-023).

```json
{
  "HendelsesId": "<UUID>",
  "BrukerId": "<UUID>",
  "Operasjon": "Hendelse:HentHendelse",
  "RessursId": "<UUID>",
  "BarnId": "<UUID>",
  "Tidspunkt": "<UTC datetime>"
}
```

Fields per constitution Leselogg-hendelse schema: `HendelsesId` (unique log entry ID), `BrukerId` (caller's UUID), `Operasjon` (Tjeneste:Operasjon format), `RessursId` (ID of the read resource — HendelsesId for `hentHendelse`, BarnId for `hentHendelserForBarn`), `BarnId`, `Tidspunkt`.

Queue (not topic) — single consumer: Revisjonstjenesten.

---

## Consumed Events

### Topic: `tjeneste.tjenester` — TjenesteOpprettet

Used to resolve BarnId for events stored with BarnId = null.

| Field | Use |
|-------|-----|
| BirkTiltakPK | Match against Hendelse rows with BirkTiltakPK + BarnId = null |
| BarnId | Set on all matching Hendelse rows |
| TjenesteId | Set on all matching Hendelse rows |

After linking: `HendelsesRegistrert` published for each newly linked Hendelse.
Consumer is idempotent: duplicate TjenesteOpprettet messages produce no side effects (GL-22).

---

## Published to Queue: `operatorkontroll.varsler`

#### UkobletHendelseAlert
Trigger: `UkobletHendelseAlertScheduler` detects a `Hendelse` with `BarnId = null` older than 30 days (FR-03).
Published via Wolverine outbox — one message per unlinked Hendelse.

```json
{
  "HendelsesId": "<UUID>",
  "BirkTiltakPK": "<int>",
  "OpprettetTidspunkt": "<UTC datetime>",
  "DagerUkoblet": "<int>"
}
```

No personal data in payload (GL-21). Consumer: operations monitoring / on-call system.

---

## Infrastructure Summary

| Name | Type | Direction |
|------|------|-----------|
| `hendelser.barn` | Topic | Publishes |
| `revisjon.leselogg` | Queue | Publishes |
| `operatorkontroll.varsler` | Queue | Publishes |
| `tjeneste.tjenester` | Topic | Consumes |
