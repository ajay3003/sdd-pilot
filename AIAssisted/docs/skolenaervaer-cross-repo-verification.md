# Skolenærvær cross-repository verification

Review date: 2026-10-14. BirkNext branch `008-traceability-first`, HEAD `3a3aacf4`.

This document records what was verified offline from the source of the target systems, and what BirkNext can and cannot execute or prove
for each Skolenærvær scenario. It is a source review. No Skole or MM test was executed, and no external system was contacted.

**Sources reviewed**

| Source | Archive | Notes |
|---|---|---|
| Skoletjenesten (`M2LB.Skole.*`: Api, Domain, Infrastructure, Unit, Integration) | `Skole (2).zip`, 2026-10-14 | 132 source files, 48 test files |
| Meldingsutvekslermottak (MM), including `tools/MuSimulator` | `Meldingsutvekslermottak (3).zip`, 2026-10-14 | 173 files |
| SkoleAdapter | `SkoleAdapter (1).zip`, 2026-10-08 | **Specification only, no source.** Its CDC behaviour is *Not verified* |
| BirkNext / AIAssisted | working tree at `3a3aacf4` | |

**Evidence labels**

- **V**: verified from source.
- **T**: verified by an automated test that was actually executed.
- **I**: inferred.
- **NV**: not verified.

Paths prefixed `[S]` are in the Skole repository, `[MM]` in Meldingsutvekslermottak. A Skole or MM test that exists in source is
*Discovered (not executed)* until its execution evidence exists. A test that passes in an isolated fixture is not proof that deployed QA works.

## 1. Architecture as verified from source

```
Altinn TT02 ─► MU ─(Service Bus queue <kanal> + Blob <kanal>, AES-256-CBC, key RSA-OAEP-wrapped)─► MM
MM ─(XSD v2.0.0 flat, XML→JSON, Entra app token)─► POST /api/skole/v1/skolenaervaer/rapporter (Skoletjenesten)
MM ─(Applikasjonskvittering v2, encrypted, queue <kanal>_mu + Blob <kanal>-mu)─► MU
SkoleAdapter ─(CDC, NV)─► /api/skole/v1/utdanning/* (Skoletjenesten)
Skoletjenesten ─(Wolverine durable outbox, SQL Server)─► Service Bus topics skole.perioder, skole.rapporter, skole.utdanning
Person ─► topics person.barn, person.person (subscription skoletjenesten) ─► Skoletjenesten
Skoletjenesten period lock ─► delivery rows (Klargjort) ─► [no file writer, no publisher yet] ─► Bufdata (NV)
```

- MM's input is a **Service Bus queue plus Blob Storage**, not CDC or Event Hub (V; no Event Hub reference in MM).
- Every MM and Skole project, and MuSimulator, targets **net10.0** (V). BirkNext targets net8, so it cannot reference their assemblies.

## 2. MM → Skoletjenesten report contract

Each aspect was compared field by field (MM `SkolenaervaerMapper`, `SkoletjenestenKlient`, `Svarklassifisering`; Skole
`RapportinntakEndepunkt`, `RapportForespoersel`, `Rapportinntaksbehandler`, `Problemdetaljer`, `openapi.yaml`).

| Aspect | Verdict |
|---|---|
| Path, method, JSON content type | Match (V) |
| Correlation: `X-Correlation-Id` = `Meldingshode/Id`, required UUID in Skole | Match (V) |
| Idempotency: `meldingId` is Skole's key; a repeat returns the stored outcome; MM's one HTTP retry relies on it | Match (V) |
| All 10 JSON fields: names, camelCase, nullability, enum spellings (`Foedselsnummer`/`DufNummer`, `Person`/`Organisasjon`, 9 follow-up types), `skjemaversjon` "2.0.0", length limits | Match (V) |
| The 6 permanent 422 reason codes (MM configuration ↔ Skole OpenAPI and code) | Match (V) |
| Response handling: 200 `utfall=Mottatt` completes; configured 422 is permanent; 409 is temporary and does not count towards pause; 408/429/5xx are temporary; other 4xx are fixable, then dead-lettered | Match (V) |
| Skole can return 500 (`[S] Problemdetaljer.cs:86`), which its OpenAPI does not list; MM treats it as temporary | Compatible; documentation gap in Skole (V) |
| Authentication: Entra app token for scope `…/m2lb-skole-api-<env>/.default` ↔ app role `Skole.Rapportinntak` | Match in design (V). The audience and the role assignment to MM's identity are NV (infrastructure) |
| MM readiness probe `/health/ready` | NV (mapped by external M2LB.Common) |
| Privacy: names, contact info and `PseudonymId` are never mapped; DTO `ToString` is masked | Match (V) |

**Verified drift**

- **D1 – timestamp text (code paths V, runtime outcome I).** MM forwards `SendtTidspunkt`, `StartDato` and `SluttDato` exactly as written
  (`[MM] SkolenaervaerMapper.cs:24,28`, `.Value`). Skole parses them with `TryParseExact` and fixed formats
  `yyyy-MM-dd'T'HH:mm:ss.FFFFFFF[zzz|'Z']`: no surrounding whitespace, at most 7 fractional-second digits (`[S] Rapporteringskalender.cs:11-14,39-60`).
  XSD `xs:dateTime` collapses whitespace and allows any number of fractional digits. A schema-valid message with padded text or more than 7
  fractional digits therefore passes MM and gets 400 from Skole. MM classifies that as fixable, then retries and dead-letters it instead of
  receipting it. *Owner: MM (trim or normalise before mapping) or Skole (accept the XSD lexical space). Needs a regression test in MM's
  `SkoletjenestenKontraktTests`.*
- **D2 – absence-code length (I).** The XSD `maxLength 16` counts characters; Skole's `KreverMaksLengde` counts UTF-16 units. A code with
  surrogate-pair characters could pass MM and get 400 from Skole.

**Contract semantics a tester must know (V)**

- The 200 answer hides the outcome. Accepted, held, closed-chain, `Tillegg` and replay all answer `{utfall:"Mottatt"}`.
- MM never learns a later outcome. A held report rejected by the sweep, or as `UKJENT_BARN` at lock, is not reported back.
- An `Oppdatering` that changes the dates creates a second report (`[S] Rapportlagring.cs:30-35`).
- `Trukket` and `Slettet` go through the full validity rules. After lock they get 422 `UTENFOR_GYLDIGHETSVINDU`.

## 3. Skoletjenesten behaviour relevant to testing

**Report intake.** `POST /api/skole/v1/skolenaervaer/rapporter`:

- Auth is the app role `Skole.Rapportinntak`.
- The request is strict JSON: unknown, duplicate or missing fields give 400.
- The Person lookup runs before the transaction (3 s budget).
- The period row is locked, and the 7 validity rules (`[S] Gyldighetskontroll.cs`) are applied.
- Report identity is child + period + reporter + dates. The newest send time wins, and older versions are kept.
- Kode 6/7 children are stored and withheld at lock.

**Held reports.**

- **When a report is held:** child not found, Person unavailable, no classification, or no BirkId.
- **Storage:** the report is encrypted with AES-GCM, using a Key Vault-wrapped key. If Key Vault is down, intake answers 503 and writes nothing.
- **Sweep:** after a Person event or hourly, single replica, open periods only.
- **At lock:** a final lookup is made. Unresolved reports are rejected `UKJENT_BARN` and deleted.
- **Observable from outside:** only the `SkolenaervaersrapportEndret` event, held-report metrics and GraphQL. Whether a report is held is visible in the database only.

**Education API.** Eight routes under `/api/skole/v1/utdanning`, app role `Skole.Utdanningsinnmating`.

- **Idempotency:** the `Idempotency-Key` header is the key. No request hash is kept, so reusing a key with a different body returns the old outcome (V).
- **DELETE is logical for a linked record:** `ErAktiv=false`, a `Slettet` version, and `UtdanningspostEndret` is published.
- **Unknown child:** the change is held encrypted and linked later by Person events, an hourly retry, or the final lookup at lock.

**Period lock.**

- **Schedule:** by default 06:00 Europe/Oslo on the 11th of the following month, with no July period. This is configurable in `Skolenaervaer:Kalender` (`[S] Innstillinger.cs`).
- **Start:** `LaasplanleggerJobb` claims the period and enqueues `KjoerPeriodelaas` in one transaction.
- **Run:** a gather phase, then a single-transaction commit.
- **Repeats:** a repeated run does nothing.
- **Person unavailable:** nothing is committed, and the lock is retried indefinitely. An alert fires after 1 h.
- **Postponement:** `PUT /api/skole/v1/drift/perioder/{periode}/laastidspunkt` **only postpones** the lock. It needs `Skole:UtsetteLaas`, returns 409 `LAAS_HAR_KJOERT` once the lock has run, and 422 if the new time is not later.
- **Immediate lock:** the only immediate trigger is `POST /api/skole/v1/lokalt/perioder/{periode}/laas-naa`. It is mapped only when `IsLocal()`, has no authorization, and must never be used against QA. **A QA lock is operator-only.**

**Coverage GraphQL** (`[S] tests/M2LB.Skole.Integration/GraphQL/schema.snapshot.graphql`).

- **`dekningsstatistikk(periode)`:** locked periods only, `Skole:SeDekningsstatistikk`, aggregates only.
- **Follow-up queries:** `oppfolgingManglendeRapporter` and `oppfolgingManglendeUtdanningsforhold` work on open periods only. Access is checked per child, including graded access for Kode 6/7, and each read is logged.
- **Invariant:** received + rejectedOnly + missing = expected holds by construction (`[S] Dekningsberegner.cs`).
- **What the delivered (LEVERT) variant excludes:** graded and unknown children.
- **Not the same population:** open-period follow-up data and frozen lock coverage are different populations. Neither is the Bufdata delivery population.

**Messaging.**

- **Infrastructure:** Wolverine with a SQL Server durable inbox and outbox. Deduplication on `hendelsesId` happens inside the handler transaction.
- **Envelope:** `{hendelsesId, hendelsesType, hendelsesTidspunkt, utfoertAv, korrelasjonId, kilde, data}`.
- **Event content:** events carry internal child UUIDs only; never a national id, BirkId or `barnEksternId` (V).
- **Outbox ≠ delivery:** an outbox row does not mean external delivery, and Service Bus acceptance does not mean downstream processing.

**Bufdata delivery (largest gap).** At lock Skoletjenesten writes delivery rows (BirkId and reporter identity), frozen coverage and a delivery
with status `Klargjort`. The rest does not exist yet (V; ADR-028 pending; the related tests are skipped):

- No Parquet library is referenced, and nothing processes the file outbox.
- `MarkerLevert` has no caller.
- `SkolenaervaersperiodeLukket` (period, delivered row count, coverage) is routed to `skole.perioder` but never published.

| Delivery stage | Can be evidenced today |
|---|---|
| Delivery row prepared | In Skole's database only |
| Dataset artifact generated | No: not implemented |
| Period-closed message published | No: not implemented |
| Bufdata receipt | No: no acknowledgement mechanism exists |
| Content validated (no fødselsnummer/DUF) | No |

## 4. Existing automated tests (Discovered, not executed)

| Suite | Classes | Test methods | Infrastructure |
|---|---|---|---|
| Skole Unit | 18 | 184 | none |
| Skole Integration | 30 | 158 (7 skipped: "Venter på ADR-028") | Testcontainers SQL Server 2022 + Azurite; WireMock for Person, Tjeneste and Autorisasjon; fake auth; no Service Bus (outbox table read directly) |
| MM Unit | 25 | 152 | none |
| MM Integration | 12 | 28 | Service Bus emulator + Azurite; WireMock as Skoletjenesten; MuSimulator in process |

All 26 Skole classes named in the test strategy exist. `LaasutsettelseTests` is under `Integration/Drift/`.

**What is needed to run them.** Running them is the recommended way to get this evidence into BirkNext: run each suite in an isolated net10
container with emulator-only networking, keep only the TRX summary, and import it through Test Coverage's TRX import.

- **SDK:** the .NET 10 SDK, which this workstation does not have; only 9.0 SDKs are installed.
- **Feeds:** the private "Smidig" feed for `M2LB.Common` 2.3.0.
- **MM only:**
  - locally packed, patched `Buf.Meldingsutveksler.*` 2.0.0 packages (`MU_LOKALE_PAKKER`);
  - Skole's `rapportinntak.openapi.yaml` at the monorepo-relative path that MM's unit project links to.
- **Integration suites:** Docker or Podman.
- Use `dotnet test --project <proj> --report-trx`. Never set `UPDATE_GRAPHQL_SNAPSHOT`, because it rewrites the source.

`tests/load/rapportinntak.js` (k6) sends 10,000 reports. Pointed at QA, it writes real or held reports there, so it needs explicit approval.

**What isolated tests do not cover:**

- real Entra tokens and role assignments;
- real Person and Person events on Service Bus;
- Skole on real Service Bus, and differences between the emulator and real Service Bus;
- real MU/Fiks IO ingress;
- a live MM → Skole call;
- scheduled locks across replicas;
- every Bufdata stage after "rows prepared".

## 5. MuSimulator

`tools/MuSimulator` has four verbs:

- `noekler` writes an RSA-4096 PEM pair.
- `send --mal <template>` builds, encrypts and uploads the blob, then sends the envelope. Its 13 templates each trigger one failure path.
- `kvitteringer` lists and **decrypts** receipts from the outbound container.
- `last` runs a load test.

It takes plain connection strings and **has no guard limiting it to emulators**, and `--barn` accepts any child identifier. BirkNext must
therefore not invoke it directly against a target. Recommended reuse, smallest first:

1. Run MM's own suites, which already drive MuSimulator against emulators, in an isolated worker and import the TRX.
2. Only if live evidence from a trusted deployed environment is needed: a thin net8 adapter inside BirkNext that implements the wire protocol
   and enforces BirkNext's trusted-environment and destination allowlist. Before writing it, verify the envelope JSON against the published
   FagKlient package. MuSimulator must not become a second generator inside BirkNext.

## 6. What BirkNext can execute and verify per scenario

**Can run now?**

- **Yes:** possible with the current configuration.
- **Conditional:** possible once the stated prerequisites exist.
- **No:** no safe mechanism exists.

**Can verify?** says what BirkNext itself can prove about the result.

| Scenario | Existing source test | BirkNext capability | Can run now? | Can verify? | Gap / action |
|---|---|---|---|---|---|
| Normal report (E2E-01) | `RapportinntakKontraktTests`, MM `US1/LevereGyldigMeldingTests` | Altinn journey `valid-report` | Conditional: ID-porten configuration incomplete | Partial: Altinn steps only | MU/MM/report observers; report read contract (none exists) |
| Unknown child / created later / unknown at lock | `HoldtRapportTests`, `HoldVedLaasTests` | `person-not-found` | Conditional | No: the 200 hides the hold | Import Skole TRX; GraphQL follow-up read (authorized) |
| Kode 6/7 | `GradertBarnInntakTests`, `GraderingVedLaasTests` | — | No (needs graded synthetic data) | No | Rely on Skole tests; never use real graded persons |
| Duplicate message | `RapportinntakIdempotensTests`, MM US tests | `duplicate-report` (NotAssessed) | No | No | Exact repeat is invisible by design |
| Correction / overlap / multiple reporters | `KorreksjonOgKjedeTests`, `SamtidighetTests` | `correction`, `overlapping-report`, `multiple-reporters` | Overlap: Conditional; others: No | No | Report read contract; second approved reporter |
| Unknown absence code | `Unit/Skolenaervaer/KvalitetskontrollTests` | — | No | No | Rely on Skole tests |
| Invalid XSD / MM temporary failure / retry / pause / dead-letter / re-drive | MM `US2/AvviseUgyldigMeldingTests`, US3–US5, `Drift` | `invalid-xsd`, `retrieval-decrypt-failure` (NotAssessed) | No | No | Import MM TRX; re-drive is an operator action (`redrive send --bekreft`) |
| Education create/update/delete, missing school offer | `UtdanningInnmatingKontraktTests`, `UtdanningsgrunnlagTests`, `PostidentiteterTests` | Active Event provider `skolenaervaer.cdc` (create, replay, invalid → control) | Conditional | Transport only | SkoleAdapter source needed; Skole `GET /postidentiteter` as read boundary (authorized) |
| Period lock / recovery / repeat | `PeriodelaasEndeTilEndeTests`, `LaasGjentakelseTests`, `LaasGjenopprettingTests` | `close-valid-period` (operator required) | No | No | Operator-run lock in QA; never a BirkNext trigger |
| Lock postponement | `LaasutsettelseTests` | — | No (state-changing, `Skole:UtsetteLaas`) | No | Operator action; read `GET /drift/perioder` |
| Coverage calculation | `DekningsstatistikkTests` | `coverage-metadata` (NotAssessed) | No | No | Authorized read-only GraphQL reader (proposed) |
| Bufdata delivery / PII absent / period-closed event | skipped (ADR-028) | Periodeavslutning (NotAssessed) | No | No | Not implemented in Skole yet |
| No personal data in events | `PersonvernTests` | — | No | No | Rely on Skole tests; event observer (proposed) |

## 7. Changes made in BirkNext for this review

- **Periodeavslutning no longer claims to be runnable.** `close-valid-period` is *Not assessed — operator required*, and the
  `domain-trigger` prerequisite states the verified lock lifecycle. The event, Bufdata and PII scenarios state that Skoletjenesten does not
  write files or publish the event yet.
- **Rapportmottak reasons now state the semantics Skoletjenesten implements,** labelled as source review, not runtime verified. This covers
  the correction, duplicate and period-locked scenarios.
- **Source evidence counts every snapshot.** MU, MM and Skoletjenesten uploaded as separate archives add up, and MM's project name
  (`Meldingsutvekslermottak`) is recognised.
- Tests: `SkoleNaervaerJourneyPackTests.SourceEvidence_CountsEverySnapshot_SoSeparatelyUploadedRepositoriesAddUp` and
  `PeriodClosure_IsOperatorOnly_NoScenarioClaimsToRun_AndTheReasonStatesTheVerifiedLockLifecycle`.
- **Scenario capability view** (journey panel → *Scenario capability*). Every catalog scenario has a computed status: Runnable, Conditionally
  runnable, Operator required, Not assessed or Blocked. Execution and result verification are shown separately, together with the declared
  requirements, missing evidence, responsible team and next action. The seven evidence limitations of this review are listed with it. The CDC
  rows are the `skolenaervaer.cdc` provider catalog: create, exact replay and invalid → control are supported; update, delete, tombstone,
  snapshot read, natural-key duplicate and late linkage stay Not assessed. Tests: `ScenarioCapabilityTests` (backend) and
  `IntegrationJourneyPanelTests.ScenarioCapability_*` (frontend). See [integration-journeys.md](integration-journeys.md#scenario-capability).
- **Message Verification** (Skolenærvær Testing → *Message Verification*; the read-only Rapportmottak scenario `message-verification`).
  - **Evidence read:** BirkNext's run history; MM's documented `MeldingUtfall` outcome events; Skoletjenesten's intake log line. The two
    telemetry sources are read only through an enrolled IQR integration's telemetry scope and the existing backend credential.
  - **What is recorded:** correlations with their certainty, and assertions against the expected outcome, under the verdict rules above.
  - **Where it is stored:** on the existing run history.
  - **Not verified against a real environment:** only fixtures and local tests were used; no QA telemetry was read. Tests:
    `MessageVerificationTests` (backend) and `MessageVerificationPanelTests` (frontend).

## 8. Open dependencies

| Dependency | Owner |
|---|---|
| ID-porten TEST client id, scopes, redirect URI, secret; reporter-a TT02 profile; approved synthetic child; trusted target and report-intake enrollment | BirkNext operator / ID-porten client owner |
| .NET 10 SDK, private feeds, `MU_LOKALE_PAKKER` packages for running Skole/MM suites | Platform / MM team |
| D1 timestamp drift fix and regression test | MM or Skole team |
| SkoleAdapter source and its CDC contract | SkoleAdapter team |
| A read contract for report state (or an approved read-only GraphQL client and role) | Skole team |
| Parquet export, file outbox, `Levert`, `SkolenaervaersperiodeLukket` publisher (ADR-028); a Bufdata receipt surface | Skole team / Bufdata |
| A QA lock procedure (operator) | Skole operations |
| Skoletjenesten's component name in BirkNext Source Analysis (upload the Skole archive and confirm how it is named before adding a matching token) | BirkNext |
