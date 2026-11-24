# Feature Specification: SkoleAdapter — education master data from BiRK

> **Superseded in part 2026-11-09 (cross-service contract translation):** the names that services exchange are English since `specs/003-cross-service-contract-translation`: the GL-20 envelope (`eventId`, `eventType`, `occurredAt`, `performedBy`, `correlationId`, `source`), the GL-32 read log, the operation registration command (`RegisterOperationsCommand`, `operationName`, `serviceName`), Person's event types (`BarnRegistered` and the rest), and the routes, fields and event types of every service. Where the files of this folder quote one of those names in its Norwegian spelling, they describe the state before that feature and are kept as history. The current names are in the name map (`specs/003-cross-service-contract-translation/contracts/name-map/`) and in `docs/glossary.md` §10. Files here that quote old names: `quickstart.md`, `spec.md`, `tasks.md`.

**Feature Branch**: `feature/skolenaervaer-spec`

**Created**: 2026-10-02

**Status**: Draft

**Input**: User description: "SkoleAdapter constitution and spec. Source: docs/analysis/skolenaervaer-reference.md §1–§4, §8 (unit SkoleAdapter), §9.2 (Utdanning context), decisions DEC-17, DEC-21, DEC-46..DEC-49."

**Governing documents**: `SkoleAdapter/.specify/memory/constitution.md` (module, v4.1.3) under
the root platform constitution. Domain source and decision log:
`docs/analysis/skolenaervaer-reference.md`. Bracketed references such as `[CA-FR-005]` or
`[DEC-48]` point to that document. The receiving side is specified in
`SkoleService/specs/001-skoleservice` (FR-038..FR-040e) under the Skole constitution (v2.4.0).

## Context

To know which children should have reported school attendance, SkoleService needs each
child's education relations (utdanningsforhold) and any registered missing school offer
(manglende skoletilbud). While BiRK owns this data, SkoleAdapter imports it from BiRK's change
stream into SkoleService's Utdanning context. Only the fields the expected population needs
are imported. Attendance rows from BiRK are not imported: institution attendance stays in BiRK
and reaches Bufdata directly. [§2.1, DEC-17]

SkoleAdapter is disposable: when BiRK no longer owns education data, it is removed without any
change to SkoleService's behaviour. All BiRK knowledge — tables, keys, code values — stays
inside it, with one exception: the child reference, which is Person's external id for the
child (`EksternId`, BiRK `BarnPK`), is forwarded so SkoleService can resolve the child
through Person (EXC-05).

SkoleAdapter only translates and forwards. It does not call Person, does not resolve children
and holds no unresolved rows; SkoleService does, as Hendelsestjenesten and Tjenestemodulen do
for their BiRK data.

The "users" of this feature are SkoleService (as consumer of the data) and the platform's
operators.

## Clarifications

### Session 2026-10-06

- Q: When a BiRK row is deleted, should SkoleAdapter always send a delete to Skoletjenesten, even if a retry or parked record exists for the row? → A: Yes. The adapter always sends a delete for the record identity and also removes any retry or parked record; Skoletjenesten treats a delete for an unknown identity as a no-op (FR-013). *Amended by the next answer: the adapter no longer has retry or parked records; every delete is sent.*
- Q: Should Skoletjenesten, not SkoleAdapter, resolve the child's M2LB identity and hold records whose child can't be resolved yet? → A: Yes, following the Hendelse/Tjeneste pattern. The adapter forwards Person's external child reference; Skoletjenesten looks the child up through Person on receipt, holds unresolved records encrypted and hidden, links them on Person's `BarnRegistrert`/`BarnEksternIdEndret` events and never unlinks a linked record. The adapter stores only its stream position. Requires a PP-08 exception (EXC-05) and amendments to both constitutions (FR-009, FR-010).
- Q: When Skoletjenesten stays unavailable past the retry limit, should the adapter pause the stream instead of dead-lettering the change? → A: Yes. The adapter stops advancing its stream position, keeps retrying with capped backoff, alerts after a configurable outage time (default 15 minutes) and resumes on its own. There is no dead-letter (FR-017).
- Q: How should the adapter guarantee that an older change never overwrites a newer one for the same row? → A: By stream order. All changes for a row are on the same partition, keyed on the BiRK primary key; the adapter processes each partition strictly in order, and a pause holds the whole partition. Keyed partitioning is a requirement on the platform team; no version field is added to Skoletjenesten's contract (FR-004).
- Q: Where does the manual reconciliation get the list of current BiRK rows to compare against Skoletjenesten? → A: From the full reload. The adapter collects the record identities in memory during the reload; when the snapshot is complete it fetches Skoletjenesten's identity list, reports how many records would be deleted and sends the deletes only after an operator confirms. If the snapshot did not complete in one run, the reconciliation is refused and operators are alerted (FR-015b).

### Session 2026-10-07

- Q: Which kinds of failure response from Skoletjenesten should the adapter treat as a permanent rejection that is logged and skipped, rather than a temporary failure that pauses the stream? → A: Only a rejection whose reason code Skoletjenesten's ingestion contract publishes as permanent for the record's content. Every other failure — timeout, server error, throttling, authentication or authorisation failure, unknown endpoint or version, unrecognised reason code — is temporary and pauses the partition (FR-017, FR-018). (auto-accepted recommendation; confirmed in review 2026-10-08)
- Q: When the reconciliation compares Skoletjenesten's records with the rows collected during a full reload, should rows created or changed in BiRK while the reload was running, or before the operator confirmed, also count as current? → A: Yes. From the start of the reload until the deletes are sent, every identity the adapter forwards as a create or update is added to the collected set, and any such identity is dropped from the reported deletes before they are sent, so a row that exists in BiRK is never deleted (FR-015b). (auto-accepted recommendation)
- Q: How should an operator confirm the reconciliation deletes, given that the adapter has no user-facing operations? → A: Through an audited configuration override that names the reconciliation run and its reported number of deletes, the same pattern as Skoletjenesten's lock postponement (Skole FR-023a). The adapter acts only when both match the run it holds in memory; anything else is ignored and logged (FR-015b). (auto-accepted recommendation)
- Q: When the adapter's stream position has expired, who starts the full reload of BiRK's current rows? → A: The platform team, on request. The adapter raises the incident, continues from the oldest change still in the stream, and consumes the new snapshot of the education tables like any other change; it has no access to BiRK's database or Debezium's signalling and never starts a snapshot itself (FR-015). (auto-accepted recommendation)
- Q: Should operators be alerted when Skoletjenesten permanently rejects an education row, and how does the row get back? → A: Yes. Every permanent rejection alerts operators with table, record identity and reason only, since a missing row makes a child wrongly expected or not. The row reaches Skoletjenesten again when it next changes in BiRK after the data is fixed, or in a full reload (FR-018). (auto-accepted recommendation)
- Q: May more than one instance of the adapter process the change stream at the same time? → A: No. Exactly one active instance processes every partition, so the in-memory reconciliation set (FR-015b) sees every row; a replacement instance takes over from the stored stream position. At a few hundred changes per day no horizontal scaling is needed, as for HendelseAdapter (FR-024). (auto-accepted recommendation)
- Q: How does the adapter tell that a full reload's snapshot completed in one run? → A: By the snapshot markers the change capture puts on the snapshot records: the run is complete when, without a restart in between, the adapter has seen the first and the last snapshot record of every configured table and has processed every partition past its own snapshot records (to a later change or to the end of the partition). Anything else counts as not completed (FR-015b). (auto-accepted recommendation)
- Q: Should the adapter report itself unhealthy while it is paused because Skoletjenesten is unavailable? → A: Liveness never fails because of a pause; it reports only that the process runs, so the platform does not restart a paused adapter in a loop. Readiness reports the status of the change stream, the position store and Skoletjenesten, as PersonAdapter does; the pause itself is visible through the pause metric and the outage alert (FR-017, FR-022, FR-023). (auto-accepted recommendation)
- Q: What is the shortest change-stream retention the adapter can rely on? → A: 7 days, the same as PersonAdapter assumes. The 24-hour default lag alert then leaves about six days to act before the position expires; a configured lag threshold at or above the retention is a configuration error (FR-015a). (auto-accepted recommendation)
- Q: Where is a change the adapter itself rejects (no child reference, unreadable change) recorded, given that only the stream position is persisted? → A: In a structured log entry with change id, table and record identity only, and in the per-table rejected count; nothing is persisted. Operators are alerted as for a permanent rejection by Skoletjenesten, since the row is then missing there too (FR-010, FR-018, FR-019, FR-022). (auto-accepted recommendation) *Amended later this session: an unreadable change pauses the stream instead of being rejected.*
- Q: How does the adapter get the existing BiRK rows on its first start, when the change capture's own initial snapshot may already have left the 7-day stream? → A: The same way as after an expired position: it starts from the oldest change still in the stream, and the platform team starts a snapshot of the education tables for go-live, early enough for the October 2026 population to lock on 11 November 2026 (FR-014). (auto-accepted recommendation)
- Q: Should every update the adapter sends carry the row's full allow-listed content, so Skoletjenesten can create a record it does not hold yet (for example after an earlier rejection)? → A: Yes. Creates and updates both carry the full allow-listed content of the row after the change, and Skoletjenesten applies an update for a record identity it does not hold as a create. That is what lets a rejected row come back on its next change (FR-012, FR-018). (auto-accepted recommendation; confirmed in review 2026-10-08)
- Q: How are BiRK's date values turned into the from and to dates sent to Skoletjenesten? → A: As the calendar date recorded in BiRK, with no time-zone conversion and any time of day dropped, as HendelseAdapter does; both dates are passed on as inclusive, as BiRK records them, and an empty to date means open-ended (FR-005a). (auto-accepted recommendation; confirmed in review 2026-10-08)
- Q: Should a change the adapter cannot read for a configured table (for example after a BiRK schema change) be rejected and skipped, or pause the stream? → A: Pause. It is a contract mismatch to fix, not a problem with one row, so it is handled like a temporary failure (FR-017) and alerts operators; processing resumes once the adapter can read it. Only row-level data problems, such as a missing child reference, are rejected (FR-019). (auto-accepted recommendation)
- Q: What should happen when a period is about to lock while the adapter is paused or behind, or a reconciliation is still unconfirmed? → A: The lock never waits on its own. The adapter's outage, lag and reconciliation alerts are the signals, and operations postpone that period's lock through Skoletjenesten's audited override (Skole FR-023a) until the education data is current, as they do for an intake backlog (FR-015b, FR-017). (auto-accepted recommendation)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Keep education relations and missing school offers current (Priority: P1)

When a caseworker registers or changes a child's education relation or missing school offer in
BiRK, the change reaches SkoleService, translated into SkoleService's language and carrying
the child's external reference, with only the fields the population calculation needs. [US-C1]

**Why this priority**: Without education data, SkoleService cannot decide whether a child of
compulsory school age has an utdanningsforhold and so is expected, cannot apply missing school
offers, and cannot produce the clean-up list. [DEC-84]

**Independent Test**: Create and update utdanningsforhold and manglende skoletilbud rows in a
BiRK test source and verify what SkoleService receives: record identity, child reference,
dates, translated codes, and nothing else.

**Acceptance Scenarios**:

1. **Given** a new utdanningsforhold in BiRK, **When** the change arrives, **Then**
   SkoleService receives a create with a stable record identity, the child reference, from
   date, to date (or open-ended), education type and school type in SkoleService's code
   lists.
2. **Given** a new manglende skoletilbud, **When** the change arrives, **Then** SkoleService
   receives a create with record identity, child reference, from date, to date and reason in
   SkoleService's code list.
3. **Given** a change to an existing row (for example a new end date), **When** it arrives,
   **Then** SkoleService receives an update for the same record identity.
4. **Given** a BiRK row with name, contact person, contact information or remarks, **When** it
   is translated, **Then** none of those fields is sent.
5. **Given** any change, **Then** no BiRK table key, table name, column name or BiRK code value
   reaches SkoleService; the child reference is the only BiRK-side identifier sent (EXC-05).
6. **Given** a BiRK attendance (`Skolenaervaer`) row, **Then** it is ignored and never sent.

---

### User Story 2 - Forward every row with the child reference; never resolve or guess identity (Priority: P1)

The BiRK education tables carry no security classification, and the adapter needs none.
SkoleAdapter forwards every row with the child reference as it is in BiRK, whether or not
Person knows the child and whatever the child's classification. SkoleService resolves the
child through Person, holds rows it cannot resolve yet, and enforces gradering on access. The
adapter never sends an M2LB child identity of its own making. [CA-P-03, DEC-74, DEC-48]

**Why this priority**: Without the education data of Kode 6/7 children and of children Person
does not know yet, SkoleService cannot place them correctly in the expected population or
the follow-up lists. Resolving in one place, the service that owns the data, keeps the adapter
disposable and keeps unresolved personal data out of it.

**Independent Test**: Send rows for a child known to Person, a child unknown to Person, a
Kode 6 child and a Kode 7 child, and verify that all four are forwarded identically with their
child reference, that the adapter makes no call to Person, and that nothing about
classification or resolution appears in the adapter's state, logs or metrics.

**Acceptance Scenarios**:

1. **Given** a row whose child is unknown to Person, **When** it arrives, **Then** it is
   forwarded with its child reference like any other row.
2. **Given** a row whose child is Kode 6 or Kode 7, **When** it arrives, **Then** it is
   forwarded like any other row, and no classification is stored, logged or counted by the
   adapter.
3. **Given** any row, **Then** the adapter makes no call to Person and stores no child
   reference or education field.
4. **Given** a row whose child reference is missing in BiRK, **Then** it is not forwarded, is
   logged and counted as rejected with change id, table and record identity only, and
   operators are alerted.

---

### User Story 3 - Deletes and ends reach SkoleService (Priority: P1)

When a row is deleted in BiRK, SkoleService receives a delete for that record. When an end
date is set, it receives an update. Upsert-only handling is not allowed. [CA-P-04]

**Why this priority**: A lingering relation makes a child wrongly expected, or wrongly not
expected, in every later period.

**Independent Test**: Delete rows and set end dates in the BiRK test source and verify the
delete and update operations in SkoleService for the same record identities.

**Acceptance Scenarios**:

1. **Given** a forwarded row, **When** it is deleted in BiRK, **Then** SkoleService receives
   a delete for the same record identity.
2. **Given** a forwarded row, **When** its end date is set or changed, **Then** SkoleService
   receives an update with the new end date.
3. **Given** a delete for any row, **Then** SkoleService receives a delete for the record
   identity. A delete for an identity SkoleService does not hold has no effect there.

---

### User Story 4 - Initial load, restart and reload (Priority: P2)

On first start, SkoleAdapter imports all existing education rows. After downtime it resumes
from where it stopped. It alerts operators long before its position can expire. If the position
expires anyway, or BiRK's change capture is re-snapshotted, it raises an incident and performs
a full reload from a snapshot of the current rows, which the platform team starts. At the end
of the reload it reports how many records in SkoleService no longer have a BiRK row, and
deletes them when an operator confirms.
[MP-06, MP-07, DEC-63]

**Why this priority**: The first expected population (October 2026) needs the existing BiRK
history, not just changes made after go-live.

**Independent Test**: Start against a BiRK test source with existing rows and verify all are
imported; stop, make changes, restart and verify resumption; let the lag pass the alert
threshold and verify the alert; expire the position, delete a row in BiRK during the gap, and
verify a full reload with no duplicates, an incident alert, a reported delete count of one,
and that the row is deleted only after operator confirmation; interrupt a reload and verify the
reconciliation is refused.

**Acceptance Scenarios**:

1. **Given** existing education rows in BiRK and no previous position, **When** the adapter
   starts, **Then** every row is forwarded once, whether or not Person knows its child yet.
2. **Given** the adapter was stopped and changes were made, **When** it restarts within the
   stream's retention, **Then** it resumes from its last position without missing or
   duplicating effects.
3. **Given** the stream position has expired, **When** the adapter restarts, **Then** it
   raises an incident alert, continues from the oldest change still in the stream, and
   forwards the current rows from the snapshot the platform team starts on request. Rows
   deleted in BiRK during the gap are not removed by the reload.
4. **Given** the adapter's lag exceeds the configured threshold, **Then** operators are alerted
   while the stream position is still valid.
5. **Given** a full reload after an incident has completed in one run, **Then** operators see
   how many records in SkoleService have no BiRK row, with a run id, and **When** an operator
   confirms through a configuration override naming that run id and number, **Then** each of
   those records receives a delete, and nothing else changes. A confirmation naming another run
   or another number is ignored and logged.
6. **Given** a full reload that was interrupted (for example by a restart), or whose collected
   identities were lost before confirmation, **Then** the reconciliation is refused, nothing is
   deleted, and operators are alerted that a new full reload is needed.
7. **Given** a row created in BiRK after the snapshot read its table but before the deletes are
   sent, **When** an operator confirms the reconciliation, **Then** the row is not deleted in
   SkoleService.

---

### User Story 5 - Translate BiRK codes (Priority: P2)

BiRK education types, school types and missing-offer reasons are translated to SkoleService's
code lists. A BiRK value without a mapping is forwarded as `unknown`, and a warning makes the gap
visible so the mapping can be added. [CA-FR-004, DEC-49]

**Why this priority**: The population calculation counts an utdanningsforhold of any type,
`unknown` included, so an unmapped code never removes a child from it (DEC-84). Unknown codes
must not block data, but must be visible.

**Independent Test**: Send rows with every mapped code and with an unmapped code, and verify
the translated values, the `unknown` value and the warning.

**Acceptance Scenarios**:

1. **Given** a row with a mapped code, **Then** SkoleService receives its own code for it.
2. **Given** a row with an unmapped code, **Then** SkoleService receives `unknown` and a
   warning is logged with the table, the code list and the BiRK code value only.
3. **Given** a mapping is added in configuration, **Then** later changes use it without a code
   change.

---

### Edge Cases

- **Same change delivered twice, or replayed in a reload**: one effect in SkoleService.
- **Several changes for the same row in quick succession**: they are on the same partition and
  delivered in stream order, so the newest change is applied last; an older change never
  overwrites a newer one.
- **Row with no end date**: forwarded as open-ended.
- **Row with end date before start date**: forwarded unchanged; SkoleService decides validity.
- **SkoleService rejects a row permanently** (a reason code its ingestion contract publishes
  as permanent): logged and alerted with diagnostic metadata and no personal data; processing
  continues with the next change. The row is sent again when it next changes in BiRK (for
  example after the data is corrected) or in a full reload.
- **SkoleService refuses the adapter's calls** (for example an expired role assignment, or an
  unknown API version): temporary, not a rejection; the adapter pauses (FR-017) and no change
  is skipped.
- **SkoleService unavailable**: the adapter pauses at the failing change, retries with capped
  backoff, alerts after the configured outage time and resumes on its own. Nothing is skipped or
  dropped, and later changes never overtake the failing one.
- **Adapter paused or behind, or a reconciliation unconfirmed, as a period approaches its
  lock**: the lock does not wait on its own. The outage, lag and reconciliation alerts are the
  signals; operations postpone that period's lock (Skole FR-023a) until the education data is
  current.
- **One change keeps failing temporarily while SkoleService otherwise works**: the adapter
  stays paused on it and the outage alert fires; operators resolve the cause. The lag alert
  (FR-015a) fires before the position can expire.
- **Child's BiRK reference changes on an existing row**: forwarded as an update with the new
  child reference; SkoleService re-resolves it.
- **Child unknown to Person, or Kode 6/7 child not yet in Person (DEP-09)**: forwarded like any
  row; SkoleService holds it until the child is registered in Person and links it then. No
  reload is needed.
- **Child reclassified to or from Kode 6/7**: nothing changes in the adapter; rows keep being
  forwarded. SkoleService applies the new classification from Person (DEP-09).
- **Unknown table on the stream**: ignored and counted; never forwarded.
- **Change for a configured table that cannot be read** (for example after a BiRK schema
  change): the adapter pauses and alerts (FR-017, FR-019); nothing is skipped, and processing
  resumes once the adapter can read it.
- **Row deleted in BiRK while the stream position was expired**: not removed by the full
  reload, because a snapshot carries only current rows; removed by the manual reconciliation
  (FR-015b).
- **Row created or changed in BiRK while a full reload runs, or before the operator confirms
  the reconciliation**: forwarded like any change and counted as current, so the
  reconciliation never deletes it, even if the snapshot did not contain it (FR-015b).

## Requirements *(mandatory)*

### Functional Requirements

**Consumption**

- **FR-001**: The adapter MUST consume changes to the BiRK utdanningsforhold and manglende
  skoletilbud tables from BiRK's change stream and deliver them to SkoleService's Utdanning
  ingestion interface. [CA-FR-001]
- **FR-002**: The adapter MUST ignore BiRK attendance rows and any table not explicitly
  configured. [CA-FR-008, DEC-17]
- **FR-003**: The adapter MUST use the change's id as idempotency key, so a change processed
  more than once has one effect. Snapshot records from a full reload are new changes with new
  ids; a reload is idempotent because SkoleService applies a create for a known record
  identity as an update (Skole FR-040b). [CA-FR-009, DEC-63]
- **FR-004**: For a given row, an older change MUST NOT overwrite the effect of a newer one. The
  adapter MUST achieve this by stream order: it MUST process the changes of each partition
  strictly in order, one at a time, and MUST NOT let a later change on a partition overtake an
  earlier one that is still being retried (FR-017). This relies on all changes for a row being
  on the same partition (Dependencies: CDC capture). [DEC-89]

**Field minimisation and translation**

- **FR-005**: The adapter MUST import only: for utdanningsforhold — child reference, from date,
  to date, education type, school type; for manglende skoletilbud — child reference, from date,
  to date, reason type. [CA-FR-002]
- **FR-005a**: From and to dates MUST be forwarded as the calendar date recorded in BiRK, with
  no time-zone conversion and any time of day dropped. Both dates are inclusive, as in BiRK; an
  empty to date MUST be forwarded as open-ended.
- **FR-006**: Names, contact person names, contact information and remarks MUST NOT be imported.
  [CA-FR-003]
- **FR-007**: BiRK code values MUST be translated to SkoleService's code lists from
  configuration. An unmapped value MUST be forwarded as `unknown` and logged as a warning with
  the table, the code list and the BiRK code value only. [CA-FR-004, DEC-49]
- **FR-008**: No BiRK table key, table name, column name or code value may reach
  SkoleService. The only exception is the child reference, Person's external id for the
  child, which is forwarded for resolution (EXC-05). [MP-02, PP-08]

**Identity and classification**

- **FR-009**: The adapter MUST forward every row with its child reference, whether or not
  Person knows the child and whatever the child's classification. It MUST NOT call Person,
  resolve the child or send an M2LB child identity, and MUST NOT filter on, store, log or
  count classification. SkoleService resolves the child and enforces classification (Skole
  FR-008, FR-038). [CA-FR-005, CA-FR-007, DEC-74, DEC-87]
- **FR-010**: SkoleService, not the adapter, MUST hold rows whose child cannot be resolved
  yet: encrypted, invisible to users, looked up on receipt, linked on Person's
  `BarnRegistrert` and `BarnEksternIdEndret` events, and never unlinked once linked (Skole
  FR-040c, FR-040d). The adapter MUST keep no retry or parked records. A row with no child
  reference MUST NOT be forwarded and MUST be recorded as rejected. [MP-04, MP-07, DEC-87;
  replaces DEC-48 and DEC-62]
- **FR-011**: Each row MUST be identified towards SkoleService by a stable identity derived
  deterministically from the BiRK table and primary key, so the same row always gets the same
  identity without any stored mapping. [CA-FR-010, DEC-47, EXC-04]

**Operations on records**

- **FR-012**: New rows MUST be forwarded as creates, changed rows as updates, and deleted rows
  (including tombstones) as deletes, all for the same record identity. Upsert-only handling is
  forbidden. Creates and updates MUST both carry the full allow-listed content of the row after
  the change (FR-005), so SkoleService can apply an update for a record identity it does not
  hold as a create (Skole FR-040b). [CA-FR-006]
- **FR-013**: Every delete MUST be sent to SkoleService for the record identity.
  SkoleService MUST treat a delete for an identity it does not hold as a no-op. [DEC-86]

**Load and recovery**

- **FR-014**: On first start the adapter MUST import all existing rows. With no stored
  position it MUST start from the oldest change still in the stream and import the existing
  rows from a snapshot of the education tables that the platform team starts for go-live, in
  time for the October 2026 period's lock. It need not wait for Person's initial load, because
  SkoleService links rows when their children appear in Person.
- **FR-015**: After downtime the adapter MUST resume from its last stream position. If that
  position has expired, the adapter MUST raise an incident alert, continue from the oldest
  change still in the stream, and perform a full reload by consuming the new snapshot of the
  education tables that the platform team starts on request. If BiRK's change capture has been
  re-snapshotted on its own, the adapter MUST raise an incident alert and treat the snapshot as
  a full reload. The adapter MUST NOT start a snapshot itself and has no access to BiRK's
  database or Debezium's signalling. A full reload does not remove records whose rows were
  deleted in BiRK during the gap; those are handled by the manual reconciliation (FR-015b).
  [DEC-63]
- **FR-015a**: The adapter MUST alert operators when its lag (the age of the oldest unprocessed
  change) exceeds a configurable threshold well below the stream's retention (default: 24
  hours, against a retention of at least 7 days), so the position never expires unnoticed. A
  threshold at or above the configured retention MUST be treated as a configuration error at
  startup. [DEC-63]
- **FR-015b**: During a full reload the adapter MUST collect, in memory only, the record
  identities of every current row of the configured tables. From the start of the reload until
  the deletes are sent, it MUST also add the identity of every create or update it forwards
  from the live stream, and MUST drop any such identity from the deletes before sending them,
  so a row that exists in BiRK is never deleted. When the snapshot has completed in one run, it
  MUST fetch the record identities SkoleService holds from SkoleAdapter (Skole FR-040b),
  report to operators how many of them have no current BiRK row under an id for the
  reconciliation run, and send a delete for each of those only after an operator confirms. The
  confirmation MUST be given through an audited configuration override (who, when, run id,
  number of deletes) naming that run id and the reported number; the adapter MUST act only
  when both match the run it holds, and MUST ignore and log any other confirmation. The adapter
  exposes no user-facing operation for this. A snapshot has completed in one run when, without
  a restart in between, the adapter has seen the first and the last snapshot record of every
  configured table, as marked by the change capture, and has processed every partition past
  its own snapshot records (to a later change or to the end of the partition). If the snapshot
  did not complete in one run, or the collected identities were lost before confirmation, the
  reconciliation MUST be refused, nothing deleted, and operators alerted. A completed run that
  finds no records to delete ends at once and reports zero, without waiting for a
  confirmation; this is how the go-live snapshot (FR-014) ends. After an incident under FR-015 the confirmation MUST happen before the next period lock; if it cannot,
  operations postpone that period's lock through SkoleService's override (Skole FR-023a)
  until it has happened. The procedure MUST be described in the operations runbook. [DEC-63,
  DEC-90]
- **FR-016**: *Withdrawn 2026-10-06: the adapter has no parked rows to retry (FR-010).*

**Failure handling**

- **FR-017**: On a temporary failure towards SkoleService the adapter MUST pause: it MUST NOT
  advance its stream position past the failing change, MUST keep retrying with exponential
  backoff capped at a configurable interval, MUST alert operators when the outage exceeds a
  configurable time (default 15 minutes), and MUST resume on its own when SkoleService
  accepts the change. There is no dead-letter. [GL-23, DEC-88]
- **FR-018**: A permanent rejection from SkoleService MUST be logged with change id, table,
  record identity and reason, operators MUST be alerted with the same diagnostic metadata, and
  processing MUST continue with the next change. A rejected row reaches SkoleService again
  when it next changes in BiRK, or in a full reload. Only a rejection whose reason code
  SkoleService's ingestion contract publishes as permanent for the record's content counts as
  permanent. Every other failure — timeout, server error, throttling, an authentication or
  authorisation failure, an unknown endpoint or version, or an unrecognised reason code — MUST
  be treated as temporary under FR-017.
- **FR-019**: No change may be dropped without a recorded outcome: forwarded, ignored by
  configuration or rejected. A change that cannot be delivered yet holds the stream (FR-017)
  and is never skipped. An outcome is recorded as a structured log entry (change id, table,
  record identity, outcome) and in the counts of FR-022; it is not persisted (FR-020). A change
  the adapter rejects itself — a row without a child reference — MUST alert operators as under
  FR-018. A change for a configured table that the adapter cannot read (for example after a
  BiRK schema change) MUST NOT be rejected: it MUST be handled as a temporary failure under
  FR-017, alerting operators, until the adapter can read it.

**State, privacy and observability**

- **FR-020**: The adapter MUST persist only its stream position. It MUST NOT store child
  references or education field values. The record identities collected for reconciliation
  (FR-015b) are held in memory only and discarded after the reconciliation.
- **FR-021**: Logs, traces and metrics MUST NOT contain child references, education field
  values, names or contact data; they may contain change id, table, record identity, outcome
  and reason, stream position, reconciliation run id, counts and durations, and (for unmapped
  codes) the code list and the BiRK code value.
- **FR-022**: Operators MUST be able to see counts of forwarded, ignored and rejected changes,
  and of `unknown` translations, per table and day, and whether the adapter is paused (FR-017)
  and for how long.
- **FR-023**: The adapter MUST expose separate liveness and readiness health signals. Liveness
  MUST report only that the process runs and MUST NOT fail because the adapter is paused
  (FR-017). Readiness MUST report the status of the change stream, the stream-position store
  and SkoleService. [GL-35]
- **FR-024**: Exactly one instance of the adapter MUST process the change stream at a time,
  covering every partition, so the reconciliation (FR-015b) sees every row. A replacement
  instance MUST take over from the stored stream position. Losing the active instance during a
  full reload means the snapshot did not complete in one run (FR-015b).

### Key Entities

- **Change** of `dbo.Utdanning` (transient): One change to a BiRK utdanningsforhold row: operation, BiRK
  key, child reference, from–to, education type, school type.
- **Change** of `dbo.ManglendeSkoletilbud` (transient): One change to a BiRK manglende skoletilbud row:
  operation, BiRK key, child reference, from–to, reason type.
- **BarnReference**: The child reference forwarded to SkoleService: Person's external id for
  the child (`eksternId`), which SkoleService resolves to the M2LB identity through Person
  (EXC-05).
- **RecordIdentity** (`recordId`): The stable identity of an education row towards SkoleService, derived
  from table and BiRK key.
- **CodeTranslation**: Configured mapping from BiRK code values to SkoleService codes, per
  code list, with `unknown` as fallback.
- **Stream position**: The adapter's position in the change stream, and its only persisted state.
- **ReconciliationRun** (transient, in memory only): One reconciliation after a full reload:
  run id, the record identities collected from the snapshot and the live stream, and the
  identities reported for deletion awaiting an operator's confirmation (FR-015b).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 99% of changes are reflected in SkoleService within 15 minutes of the change in
  BiRK during normal operation.
- **SC-002**: After the initial load, the set of utdanningsforhold and manglende skoletilbud in
  SkoleService, linked or held, equals the BiRK rows with a child reference, with zero
  differences in a reconciliation run.
- **SC-003**: 100% of rows with a child reference are forwarded, whether or not Person knows
  the child, Kode 6/7 included; the adapter makes zero calls to Person and sends zero M2LB
  child identities; zero classification values appear in the adapter's state, logs, traces or
  metrics.
- **SC-004**: Zero names, contact data, remarks, BiRK table keys or BiRK code values reach
  SkoleService (the child reference excepted, EXC-05), and zero child references or education
  field values appear in logs, traces or metrics (apart from unmapped code values in warnings).
- **SC-005**: The adapter's persisted state contains zero child references and zero education
  field values.
- **SC-006**: Deleting the adapter after BiRK hands over education data requires zero changes to
  SkoleService's behaviour; its path for resolving child references simply falls out of use.
- **SC-007**: Replaying any part of the change stream, or a full reload, changes nothing in
  SkoleService that uninterrupted processing would not have changed.
- **SC-008**: Operators are alerted before the stream position expires in 100% of cases where
  the adapter falls behind. After a position expiry or a BiRK re-snapshot, the manual
  reconciliation leaves zero records in SkoleService whose BiRK rows no longer exist, before
  the next period lock.
- **SC-009**: When SkoleService is unavailable, operators are alerted within the configured
  outage time (default 15 minutes) in 100% of cases, zero changes are skipped, and processing
  resumes without operator action once SkoleService is available again.

## Assumptions

- BiRK's change stream carries create, update and delete (tombstone) events for the education
  tables, with a change id and the full row after the change.
- BiRK's from and to dates on education rows are inclusive calendar dates (confirmed
  2026-10-08).
- The change stream's retention is at least 7 days, as PersonAdapter also assumes.
- The change stream is partitioned by the BiRK primary key, so every change for a row,
  snapshot records included, is on the same partition and in commit order.
- A full reload is a Debezium snapshot. It carries the current rows only, never deletes for rows
  removed during a gap, and its records mark the first and last record of each table's
  snapshot. BiRK re-snapshots (for example after a Debezium offset reset) can also
  happen outside SkoleAdapter's control.
- The BiRK child reference on an education row is the same value Person holds as the child's
  external id (`eksternId`), so SkoleService can resolve it through Person (DEP-08).
- Person contains Kode 6/7 children once PersonAdapter imports them (DEP-09). Until then
  SkoleService holds their rows unlinked.
- Education volume is small (thousands of rows in total, a few hundred changes per day), so
  one active instance is enough (FR-024).
- Retention of rows SkoleService holds unlinked follows the retention decided for the
  Utdanning context (OQ-25).

## Dependencies & Open Items

| ID | Dependency | Owner | Blocks |
|----|------------|-------|--------|
| CDC capture | The BiRK utdanningsforhold and manglende skoletilbud tables must be captured in the change stream, partitioned by the row's primary key so all changes for a row are on one partition in commit order, with snapshot records marking the first and last record of each table's snapshot, and a snapshot of both tables started for go-live after the adapter is running. | Platform team | Go-live, FR-004, FR-014, FR-015b |
| Stream retention | Retention of the BiRK CDC Event Hub of at least 7 days, notice of BiRK re-snapshots, and a snapshot of the education tables started on request after a position expiry, so the lag alert (FR-015a) can be set well below retention and an incident starts the reload and the reconciliation. | Platform team | FR-015, FR-015a, FR-015b |
| ~~Source filter~~ | ~~A source-level filter that drops education rows for classified children.~~ Withdrawn 2026-10-06: rows for Kode 6/7 children are forwarded (DEC-74). | — | — |
| DEP-08 | Lookup by child external id returning M2LB identity for every child, Kode 6/7 included, with access control for machine callers. Needed by SkoleService, not by the adapter (since 2026-10-06). Today's `hentBarnRegistreringIdFraEksternId` has no access check. | Development team (Person) | Skole FR-038; go-live |
| SkoleService | Utdanning ingestion interface with create, update and delete keyed on record identity and accepting the child reference (Skole FR-038..FR-040b), with the reason codes for permanent rejections published in the contract (FR-018); an update for an unknown identity applied as a create (FR-012); a delete for an unknown identity is a no-op (Skole FR-040b); resolution of the child through Person, encrypted holding of unlinked rows, linking on Person's `BarnRegistrert`/`BarnEksternIdEndret` events and at lock, never unlinking a linked row (Skole FR-040c, FR-040d). | SkoleService | FR-001, FR-009, FR-010, FR-012, FR-013 |
| ~~EXC-05~~ | Exception to PP-08 (SkoleAdapter MP-02; Skole MP-02): Person's external child id reaches SkoleService's ingestion contract, used only for resolution through Person, held encrypted only while a record is unlinked, never exposed in its read APIs, events or delivery. Recorded in both constitutions (SkoleAdapter v4.0.0, Skole v2.1.0). Approved 2026-10-08 (SkoleAdapter v4.1.1, Skole v2.1.2). | Solution architect, Data Protection Officer | — |
| Constitution approval | SkoleAdapter constitution v4.0.0 (MAJOR: MP-04 and MP-07 redefined) and Skole constitution v2.1.0 (MINOR: EXC-05, Utdanning resolves and holds) must be approved; both touch stored personal data, so the Data Protection Officer approves too. SkoleAdapter v4.1.0 (MINOR) and Skole v2.1.1 (PATCH), from the 2026-10-07 clarifications ([U12]), touch no personal-data principle and are approved with them by the Solution Architect and module owners. | Solution architect, module owners, Data Protection Officer | `/speckit.plan` |
| ~~EXC-04~~ | ~~Approval of name-based record identities against PS-04.~~ Approved 2026-10-08 (SkoleAdapter v4.1.1, Skole v2.1.2). | Solution architect | — |
| DEP-09 | PersonAdapter imports Kode 6/7 children and forwards classification changes for children already in Person. Today PersonAdapter's classification guard drops them, so SkoleService cannot link their rows. Platform-level, affects every module using Person. | Development team (Person), architect | Edge cases: child not in Person, reclassification |
| Code mappings | BiRK code values for education type, school type and missing-offer reason, mapped to SkoleService's code lists. Which types mean upper secondary is no longer needed: upper secondary is never expected (DEC-84). | Product owner | FR-007 |

## Out of Scope

- Persisting, versioning or using education data — SkoleService's feature.
- Resolving the child's M2LB identity and holding rows whose child is not yet known —
  SkoleService's feature.
- Importing BiRK attendance (`Skolenaervaer`) rows. [DEC-17]
- Classification of children (PersonAdapter / Person) and enforcing it on access
  (SkoleService).
- Education data created in M2LB after BiRK hands over.
