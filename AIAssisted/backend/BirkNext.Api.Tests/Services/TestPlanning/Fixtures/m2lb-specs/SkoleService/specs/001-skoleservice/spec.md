# Feature Specification: SkoleService — school attendance reporting from private institutions and foster homes

> **Superseded in part 2026-11-09 (cross-service contract translation):** the names that services exchange are English since `specs/003-cross-service-contract-translation`: the GL-20 envelope (`eventId`, `eventType`, `occurredAt`, `performedBy`, `correlationId`, `source`), the GL-32 read log, the operation registration command (`RegisterOperationsCommand`, `operationName`, `serviceName`), Person's event types (`BarnRegistered` and the rest), and the routes, fields and event types of every service. Where the files of this folder quote one of those names in its Norwegian spelling, they describe the state before that feature and are kept as history. The current names are in the name map (`specs/003-cross-service-contract-translation/contracts/name-map/`) and in `docs/glossary.md` §10. Files here that quote old names: `checklists/quickstart-validering.md`, `contracts/avhengigheter/tjeneste-plasseringer.md`, `contracts/hendelser.md`, `contracts/utdanning-innmating.md`, `data-model.md`, `plan.md`, `quickstart.md`, `research.md`, `spec.md`, `tasks.md`.

**Feature Branch**: `feature/skolenaervaer-spec`

**Created**: 2026-10-02

**Status**: Draft

**Names**: The module was called Skoletjenesten until 2026-10-20, when it became SkoleService and its code,
contracts and data model moved to English names (`SkoleService/specs/002-translate-source-english/`). The user
description below is quoted as given.

**Input**: User description: "Feature for the Skoletjenesten module in SkoleService/ (spec folder SkoleService/specs/001-skoleservice, governed by SkoleService/.specify/memory/constitution.md). Skoletjenesten receives monthly school attendance reports (skolenærvær) for children in private and state foster homes via Altinn → Meldingsutveksleren → Meldingsutvekslermottaket, resolves the child's identity via Person, validates, holds unresolved reports until lock, locks each period, calculates coverage against the expected population, and delivers one final dataset per period to Bufdata; it also owns education master data (Utdanning context) imported via SkoleAdapter. Source: docs/analysis/skolenaervaer-reference.md — shared context §1–§6, unit section §9, dependencies §10, success criteria §11, open questions §14. Decisions DEC-01..DEC-38 are binding. Keep design notes (§9.8) out of the spec. Carry remaining open questions (OQ-01, OQ-03, OQ-09, OQ-12, OQ-21, OQ-25) as NEEDS CLARIFICATION / dependencies."

**Governing documents**: `SkoleService/.specify/memory/constitution.md` (module, v2.4.1) under the
root platform constitution. Domain source and decision log:
`docs/analysis/skolenaervaer-reference.md`. Bracketed references such as `[SR-FR-013]` or
`[DEC-25]` point to that document.

## Context

SkoleService receives monthly school attendance reports (skolenærvær) for children in
private institutions and foster homes via Altinn. Private institutions are the primary
senders and usually report as organisations; foster parents report as persons. Meldingsutveksleren
forwards the encrypted message, and MeldingsutvekslerReceiver validates its structure and hands
SkoleService one period summary per child and reporter. [DEC-52, DEC-54]

**Bufdata owns the report. M2LB owns the boundary where the child's fødselsnummer stops, and
knows who should have reported.** SkoleService translates the child's identity to BirkID,
validates the report, works out which children were expected to be reported, and delivers one
final dataset per period. **State** institution attendance stays in BiRK and goes to Bufdata
unchanged; Bufdata combines both channels on BirkID. [§2.1, §2.2, DEC-01]

### Reporting cycle

| Step | When | What happens |
|------|------|--------------|
| Period | Configurable length; calendar month today | Institutions and foster homes record attendance through the month. |
| Validity window | From the period's first day until its lock | Reports and corrections are accepted, also before the period has ended (DEC-65). The Altinn form closes on the 10th of the following month. |
| Lock | The 11th of the following month, Europe/Oslo, after a settling delay; operations can postpone a single period's lock before it runs (FR-023a, DEC-69) | The period locks, held reports are resolved or rejected, the expected population is frozen, coverage is calculated and the dataset is produced. |
| Non-reporting periods | Configurable list; default July | No reporting, no lock, no coverage, no delivery. |

The first reporting period is October 2026. The first delivery is 11 November 2026. Reports
for periods before October 2026 are not processed. [DEC-07]

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Accept a valid report without keeping the child's national ID (Priority: P1)

A private institution or a foster home submits a month's attendance for a child. SkoleService looks the child up in
Person, discards the child's fødselsnummer or DUF-nummer, checks the report, and stores it
against the child's M2LB identity together with the reporter. A meaningless report is rejected
with a machine-readable reason; a plausible but inconsistent report is accepted with a quality
flag. [US-B1, US-B2, US-B3]

**Why this priority**: Without accepted reports there is nothing to deliver. This is also where
the most sensitive rule lives: the child's national identifier must stop here.

**Independent Test**: Submit valid, invalid and inconsistent reports for children known to
Person and verify the stored outcome, the rejection reasons, the flags, and that no child
national identifier exists anywhere afterwards.

**Acceptance Scenarios**:

1. **Given** a report for a child Person knows, with valid hours, dates within the open period
   and valid absence codes, **When** it arrives, **Then** it is accepted, stored against the
   child's M2LB identity with BirkID and reporter, and the child's fødselsnummer/DUF-nummer is
   not stored, published or logged.
2. **Given** a report where `AntallTimerFaktiskNaervaer` is greater than
   `AntallTimerEtterPlanen`, **When** it arrives, **Then** it is accepted and flagged
   "attendance above plan", and the absence-sum check is not applied to it.
3. **Given** a report with an absence code outside the accepted codes configured for its
   schema version (default for v2.0.1: `1`, `2`, `3`, `4`, `5`, `9`), or a zero-padded code such
   as `01`, **When** it arrives, **Then** it is accepted, the code is delivered as received, and
   the report is flagged "unknown absence code".
4. **Given** a report whose absence hours sum differs from planned minus actual hours by more
   than 0.5 hours, **When** it arrives, **Then** it is accepted and flagged "inconsistent hour
   totals".
5. **Given** a report with no absence breakdown where planned hours exceed actual hours,
   **When** it arrives, **Then** it is accepted and flagged "missing absence breakdown"; where
   planned equals actual, it is accepted with no flag.
6. **Given** a report listing the same absence code twice, **When** it arrives, **Then** the
   hours are summed into one line and the report is processed normally with no flag.
7. **Given** a report without a child identifier, **When** it arrives, **Then** it is rejected
   with reason "missing identifier".
8. **Given** a report whose dates span two periods, start after the submission date, or fall in
   a locked period or a period that has not started, **When** it arrives, **Then** it is
   rejected with reason "outside validity window".
9. **Given** the same message delivered twice, **When** the second copy arrives, **Then**
   nothing changes and the same outcome is returned.
10. **Given** a report for the current period sent before the period ends, with dates up to the
    period's last day, **When** it arrives, **Then** it is accepted and flagged "submitted
    before period end"; a correction sent after the period has ended replaces it without the
    flag.
11. **Given** a report with an hour value greater than 24 × the number of days in its date
    range, **When** it arrives, **Then** it is rejected with a machine-readable reason.

---

### User Story 2 - Process reports for security-classified children and enforce gradering on access (Priority: P1)

Reports for children with Kode 6 or Kode 7 classification are processed like any other report.
Gradering is enforced where the data is read, under the authorisation service's rules: a Kode
6/7 child is visible only to a user who holds `Person:SeGradertBarn` for that child, and to
anyone else the child does not exist. Rows for Kode 6/7 children are withheld from Bufdata until
delivery is decided, and whether they count in the expected population and coverage is a
configuration option, off by default. [US-B4, DEC-74, DEC-75, DEC-76, DEC-122]

**Why this priority**: Kode 6/7 children are among the children who most need follow-up, so
their data must be as complete as anyone else's. At the same time, showing or counting a
classified child to a user without explicit access is a breach (PP-04, MP-05). Both must hold
from the first accepted report.

**Independent Test**: Submit reports for a Kode 6 child, a Kode 7 child and an unclassified
child. Verify that all three are stored. Read every list and lookup as a user with the relation
and `Person:SeGradertBarn` for the classified children, and as a user with the relation only,
and verify what each sees and counts. Lock the period with the counting option off and on, and
verify the dataset and the coverage figures.

**Acceptance Scenarios**:

1. **Given** a report for a child Person returns as Kode 6 or Kode 7, **When** it arrives,
   **Then** it is validated, stored and versioned like any other report, the child's gradering
   is not stored, and it is answered as received.
2. **Given** stored data for a Kode 6/7 child, **When** a user without `Person:SeGradertBarn`
   for that child reads any list or lookup, **Then** the child does not appear and is not
   counted, and a direct lookup answers as if the child did not exist.
3. **Given** a user with the relation and `Person:SeGradertBarn` for a Kode 6/7 child, **When**
   they read a list or lookup, **Then** the child appears and the read is read-logged.
4. **Given** a locked period with accepted reports for children who are Kode 6/7 at lock,
   **Then** their rows are not in the Bufdata dataset, and the reports stay stored.
5. **Given** the counting option is off (default), **When** the period locks, **Then** Kode
   6/7 children are in neither the expected population nor any coverage figure. **Given** it
   is on, **Then** they are counted like any other child in a full coverage breakdown available
   only through the dedicated statistical operation, the breakdown delivered to Bufdata still
   leaves them out, and the period-closed notification carries no coverage counts.
6. **Given** a held report whose child resolves as Kode 6 or Kode 7, **When** it resolves,
   **Then** it is processed as in scenario 1 and the held identifier is deleted.
7. **Given** a child whose gradering changes in Person after data about them was stored,
   **When** the child is next read, **Then** access follows the new gradering, nothing is
   deleted, and the gradering read at lock decides delivery and counting for the period.
8. **Given** Person answers a lookup without a gradering, **When** the report arrives, **Then**
   the lookup is treated as failed: the report is held and retried (User Story 4), never
   processed as unclassified.

---

### User Story 3 - Lock the period and deliver one final dataset (Priority: P1)

On the 11th of the month after each reporting period, the period locks and Bufdata receives
exactly one final dataset for it, with one row per report (child, period, reporter and date
range), keyed on BirkID. Subscribers are notified when the period is ready. [US-D2, US-E1, US-E3]

**Why this priority**: Bufdata needs the October 2026 period on 11 November 2026. Delivery is
the purpose of the service.

**Independent Test**: Accept reports for a period, run the lock, and verify that the dataset
contains exactly the accepted reports, that a second lock or export run produces an identical
result, that later submissions for the period are rejected, and that one "period closed"
notification was published.

**Acceptance Scenarios**:

1. **Given** accepted reports in an open period, **When** the lock time passes, **Then** the
   period locks and one dataset is produced containing one row per accepted report with BirkID,
   period, report dates, reporter type and identity, planned and actual hours, absence lines,
   source `ALTINN` and quality flags, except reports for children who are Kode 6/7 at lock
   (User Story 2).
2. **Given** a locked period, **When** the lock or export is run again, **Then** the result is
   identical and nothing changes.
3. **Given** a locked period, **When** a new report or correction for it arrives, **Then** it
   is rejected with reason "outside validity window".
4. **Given** a period has locked, **Then** exactly one `SkolenaervaerPeriodClosed`
   notification is published, carrying only the period, the number of delivered rows, the
   coverage counts (unless the counting option in FR-009 is on) and a correlation id, and no
   separate count of Kode 6/7 or unknown children.
5. **Given** July is configured as a non-reporting period, **When** August 11th passes,
   **Then** no lock, coverage or delivery happens for July.
6. **Given** a locked period's dataset, **Then** it never contains a child's fødselsnummer or
   DUF-nummer.

---

### User Story 4 - Hold reports for children not yet known (Priority: P2)

A child's registration in Person sometimes lags behind the reporter's report. Instead of
rejecting such a report, SkoleService holds it with the identifier and content encrypted,
treating it as possibly concerning a classified child, retries the lookup, and processes it as
soon as the child becomes known. Anything still unresolved at lock
is rejected and its identifier deleted. Reporters get no feedback on rejection in this feature,
which covers only the Altinn channel and sends no negative receipts. [US-B6, DEC-13, DEC-57]

**Why this priority**: Without holding, reports are lost silently whenever Person lags, and
coverage looks worse than reality. The first delivery can still go out without it, so it
follows the P1 stories.

**Independent Test**: Submit a report for an identifier Person does not know, make the child
known before lock and verify normal processing; repeat without making the child known and
verify rejection at lock and deletion of the identifier.

**Acceptance Scenarios**:

1. **Given** a report whose identifier Person cannot resolve, **When** it arrives, **Then** it
   is answered as received and held, the identifier and the report content are stored
   encrypted together with their own access control, and the lookup is retried when Person
   reports a new or changed person and at a fixed interval.
2. **Given** a held report, **When** the child becomes resolvable before lock, **Then** the
   report is processed as in User Stories 1 and 2 and the held identifier is deleted.
3. **Given** a held report still unresolved after a final lookup at lock, **Then** it is
   rejected with reason "unknown child", counted as rejected, and the identifier and content
   are deleted.
4. **Given** Person is unavailable, **When** a report arrives, **Then** it is held and retried,
   never lost.
5. **Given** a report that fails a validity rule that does not depend on the child's identity,
   **When** it arrives, **Then** it is rejected immediately and the identifier is never held.

---

### User Story 5 - Corrections, withdrawals and more than one reporter (Priority: P2)

Within the validity window a reporter can correct, withdraw or delete its report. When a child
moves between placements during a month (for example from a private institution to a foster
home), both reporters' reports stand and are delivered as separate rows. [US-B5, US-B7, §9.6]

**Why this priority**: Corrections make the delivered figures right, and a mid-month move is
common enough that losing one reporter's report would distort the statistics.

**Independent Test**: Submit, correct, withdraw and delete reports for one child from one and
from two reporters within the window, and verify the versions, history and the rows in the
resulting dataset.

**Acceptance Scenarios**:

1. **Given** an accepted report, **When** the same reporter sends a correction
   (`Oppdatering`) within the window, **Then** a new version replaces the previous one, and the
   previous version is kept as history.
2. **Given** reports from two different reporters for the same child and period, **When** the
   period locks, **Then** both are delivered as separate rows, each with its reporter and
   flagged "multiple reporters".
3. **Given** a correction that arrives before its original, **When** both have arrived,
   **Then** the version with the latest send time is current, regardless of arrival order.
4. **Given** an accepted report, **When** a withdrawal (`Trukket`) or deletion (`Slettet`)
   arrives within the window, **Then** the report is not delivered and counts as no report in
   coverage.
5. **Given** a correction whose child, period, reporter or date range differs from the report
   it points to, **When** it arrives, **Then** it is processed as a new report under its own key
   and the original is unchanged.
6. **Given** a message marked `Duplikat` with an id already seen, **Then** nothing changes;
   with a new id, it is treated as a correction.
7. **Given** a message marked `Tillegg`, **Then** it is logged and acknowledged with no effect
   on the report. **Given** a receipt or reply value (`ApplikasjonsKvittering`,
   `SaksbehandlerKvittering`, `Svar`, `Feil`), **Then** it is rejected as unexpected.
8. **Given** an accepted report from a reporter for 1–10 October, **When** the same reporter
   sends a report for the same child for 20–31 October, **Then** both stand and are delivered as
   separate rows, with no overlap flag.
9. **Given** an accepted report from a reporter for 1–15 October, **When** the same reporter
   sends a report for the same child for 1–31 October, **Then** both stand, and at lock both rows
   are flagged "overlapping reports from same reporter".

---

### User Story 6 - Expected population and coverage breakdown (Priority: P2)

At lock, SkoleService works out which children should have been reported and delivers a
coverage breakdown with the dataset, so Bufdata can judge whether the figures are
representative. [US-D1, US-D3]

**Why this priority**: Coverage is required with the delivery, but it depends on placements
from Tjeneste and education data from the Utdanning context, so it builds on the earlier
stories.

**Independent Test**: Set up placements, birth dates, utdanningsforhold and manglende
skoletilbud for a set of children, submit a mix of accepted, rejected and missing reports, lock
the period, and verify every child lands in the right bucket and the counts add up.

**Acceptance Scenarios**:

1. **Given** a child placed in a foster home (any type) or a private institution for at least
   one day of the period, of compulsory school age (grades 1–10), and with an utdanningsforhold
   overlapping the period, **When** the period locks, **Then** the child is expected.
2. **Given** a child placed only in a state institution during the period, **When** the period
   locks, **Then** the child is not expected (the state channel reports through BiRK).
3. **Given** a child past compulsory school age, **When** the period locks, **Then** the child
   is not expected, even with an upper secondary utdanningsforhold; an accepted report for the
   child is delivered and counted as *received, not expected*.
4. **Given** a child whose manglende skoletilbud covers the whole period, **Then** the child is
   not expected; if it covers only part of the period, the child is still expected.
5. **Given** the frozen population, **Then** each expected child is in exactly one of
   *received* (at least one accepted report), *rejected only* or *missing*, and received +
   rejected only + missing = expected.
6. **Given** the coverage breakdown, **Then** *received*, *rejected only* and *missing* are
   also given per reporter category (private institution, foster home), and the categories
   add up to the totals; *received, not expected* and rejections not attributed to an expected
   child are given as totals only.
7. **Given** an accepted report for a child outside the expected population, **Then** it is
   delivered and counted as *received, not expected*.
8. **Given** a delivered period, **When** anyone asks why a child is in a given bucket,
   **Then** the answer can be reproduced from the frozen basis.
9. **Given** an expected child whose only report was rejected for a validity failure, **When**
   the period locks, **Then** the child is in *rejected only*, not *missing*.
10. **Given** a placed child of compulsory school age with no utdanningsforhold overlapping the
    period, **When** the period locks, **Then** the child is not expected; the child appears on
    the clean-up list (FR-041 (b)), and an accepted report for the child is counted as *received,
    not expected*.

---

### User Story 7 - Maintain education master data (Priority: P2)

The Utdanning context keeps each child's utdanningsforhold and any registered manglende
skoletilbud, imported from BiRK through SkoleAdapter, with full temporal validity so the state
for any past period can be read. SkoleAdapter sends each record with the child reference
(Person's external id for the child); Utdanning resolves it through Person, and holds records
whose child Person does not know yet until it does. [US-C1, UT-FR-001..005, EXC-05]

**Why this priority**: The expected population cannot be calculated without it.

**Independent Test**: Send creates, updates and ends/deletes for utdanningsforhold and
manglende skoletilbud, and verify the stored state and the state as of past periods. Send a
record for a child unknown to Person, register the child in Person, and verify the record is
linked, was invisible and unused until then, and keeps no child reference afterwards.

**Acceptance Scenarios**:

1. **Given** a new utdanningsforhold whose child reference Person resolves, **When** it is
   ingested, **Then** it is stored linked to the child's M2LB identity with from–to,
   education type and school type, and the child reference is not kept.
2. **Given** an existing record, **When** an update or an end/delete is ingested, **Then** the
   record changes or ends, and the previous state remains readable for past periods.
3. **Given** the same change delivered twice, **Then** the second has no effect.
4. **Given** an ingestion that carries a field the ingestion contract does not define, **When**
   it arrives, **Then** nothing is stored and it is answered as a contract mismatch that is not a
   permanent rejection, as for an unknown code value (scenario 12). The contract defines only the
   fields the expected population needs (FR-039).
5. **Given** a create for a record identity that already exists (for example from a full
   reload in SkoleAdapter), **Then** it is applied as an update, and has no effect if the
   content is unchanged. **Given** an update for a record identity the system does not hold,
   **Then** it is applied as a create.
6. **Given** SkoleAdapter's machine identity, **When** it asks for the record identities it
   has supplied, **Then** it gets the identities of current records only, unlinked records
   included and records it has deleted left out, with no education fields or child
   references.
7. **Given** a record whose child reference Person does not resolve, or a record that arrives
   while Person is unavailable, **When** it is ingested, **Then** it is held unlinked: child
   reference and fields encrypted, invisible to every read, and not used by the expected
   population or the clean-up list.
8. **Given** an unlinked record, **When** Person publishes `BarnRegistrert` or
   `BarnEksternIdEndret` for a child with that external id, or a later lookup succeeds,
   **Then** the record is linked to the child, its child reference is deleted, and it counts
   from then on.
9. **Given** a linked record, **When** an update arrives whose child reference cannot be
   resolved, **Then** the record stays linked to its current child with its current fields,
   and the update is held until it resolves; the record is never unlinked.
10. **Given** a delete for a record identity the system does not hold, **Then** it has no
    effect. **Given** a delete for an unlinked record, **Then** the record, its child reference
    and fields are deleted.
11. **Given** unlinked records when a period locks, **Then** a final lookup is made for them
    before the expected population is frozen, and records that link count in that period.
12. **Given** a create that is linked on receipt and a create that is held unlinked, **Then**
    SkoleAdapter gets the same answer, *received*, for both. **Given** a create without a from
    date, **Then** it is rejected with a reason code published in the ingestion contract.
    **Given** a create with a code value outside Utdanning's code lists, **Then** it is not
    stored and is answered as a failure that is not a permanent rejection. **Given** a record
    whose to date is before its from date, **Then** it is stored and counts for no period.

---

### User Story 8 - Follow-up lists for caseworkers (Priority: P3)

During the validity window, caseworkers can see which expected children for whom they have a
relation still lack an accepted report, so they can follow up with the institution or foster
home. A separate clean-up list shows children of compulsory school age in foster homes or
private institutions without a registered utdanningsforhold. [US-D4]

**Why this priority**: It raises coverage but is not needed for the first delivery. The
caseworker–child relation comes from the authorisation service (DEC-56).

**Independent Test**: With a known caseworker–child relation, query the lists during the window
and verify the content, that children outside the relation are not shown, that a Kode 6/7 child
is shown only to a caseworker who also holds `Person:SeGradertBarn` for the child, and that each
lookup is read-logged.

**Acceptance Scenarios**:

1. **Given** an open period, **When** a caseworker opens the follow-up list, **Then** it shows
   only expected children they have a relation to who have no accepted report yet.
2. **Given** any lookup in the lists, **Then** a read-log entry is written after access is
   confirmed.
3. **Given** a Kode 6/7 child, **Then** the child appears in either list only for a user who
   has both the relation and `Person:SeGradertBarn` for the child, whatever the counting option
   in FR-009; for anyone else it is neither shown nor counted.
4. **Given** two open periods (between the 1st and the previous period's lock), **When** a
   caseworker opens a list without choosing a period, **Then** it shows the period that locks
   first; **When** they choose the other open period, **Then** it shows that one.

---

### Edge Cases

- **Report arrives during the settling delay before lock**: it is processed against the
  previous period if its send time falls within the window; once the lock has run, it is
  rejected.
- **Intake outage around the deadline**: reports sent on time but delayed (for example
  dead-lettered and re-driven after the 11th) would be rejected once the lock has run.
  Operations postpone that period's lock (FR-023a) until the backlog has been processed; the
  lock does not detect the backlog itself. The inactivity alert (FR-026a) and the intake's own
  alerts are the signals.
- **Mid-month move with only one reporter reporting**: the child is *received*; the missing
  report from the other reporter is not counted separately, and the row is not flagged
  "multiple reporters".
- **More than one reporter for the same child and period** (a mid-month move, both foster
  parents in one home reporting as persons, or a report from a former placement): every report
  stands, date ranges are not compared, and at lock each delivered row for the child is flagged
  "multiple reporters" so Bufdata can spot possible double counting (FR-019). The child is
  counted once in coverage.
- **Same reporter, two stays in one period** (e.g. 1–10 and 20–31 with the child away in
  between): both reports stand as separate rows. If their dates overlap, both stand and are
  flagged "overlapping reports from same reporter" at lock. Two reports from one reporter never
  set the "multiple reporters" flag.
- **Reporter wants to change a report's dates**: a correction with other dates is a new report,
  and the original stays (FR-021). The reporter withdraws (`Trukket`) or deletes (`Slettet`) the
  original; otherwise both are delivered, flagged if they overlap.
- **Mid-month move between a state and a private institution**: the child is expected in M2LB
  (private placement overlaps the period); the state institution's attendance reaches Bufdata
  through BiRK with source `BIRK`.
- **Child becomes Kode 6/7 after a report was accepted**: from the next read after Person
  records the change, the child is visible only to users with `Person:SeGradertBarn` for the child. The
  stored data is kept (PP-05) and never deleted on reclassification. At lock, the gradering read
  then decides that the child's rows are withheld from Bufdata and whether the child is counted
  (FR-009). This depends on Person receiving the reclassification (DEP-09); today PersonAdapter
  drops it.
- **Child stops being Kode 6/7**: access follows the new gradering from the next read after
  Person records the change. A period that has already locked is not changed (FR-025).
- **Kode 6/7 child not yet in Person**: until PersonAdapter imports Kode 6/7 children (DEP-09),
  their reports cannot be resolved; they are held and, if still unresolved at lock, rejected as
  "unknown child" (User Story 4). Their education records are held unlinked and linked once
  the child appears in Person (FR-040c).
- **Education record arrives before its child is in Person**: held unlinked; linked by
  Person's `BarnRegistrert` or `BarnEksternIdEndret` event, or by the final lookup at lock.
  Until then the child has no utdanningsforhold here, so is not expected and does not appear
  on the clean-up list.
- **Child registered in Person before its education record arrives**: the lookup on receipt
  links the record; no event is needed.
- **Child reference on an existing education record changes**: the update is resolved like a
  new record; if it resolves, the record moves to the new child; if not, the record stays with
  its current child until the update resolves (FR-040d).
- **Held report receives a correction before resolution**: the correction is held too, and
  both are applied in send-time order on resolution.
- **Withdrawal or deletion arrives before its report** (the report is not held): recorded
  against the message chain; the report, sent earlier, has no effect when it arrives, and it
  counts as no report (FR-020).
- **Held report is withdrawn or deleted before resolution**: the hold ends at once, the held
  identifier and content are deleted, and the report counts as no report (FR-006).
- **Report for a period in the non-reporting list**: rejected with reason "non-reporting
  period" and recorded like any other rejection.
- **Partial-period report** (e.g. 15th–31st after a move): valid; dates need not cover the
  whole period.
- **Report sent before the period ends** (e.g. 1st–31st sent on the 28th): accepted and flagged
  "submitted before period end". The reporter can correct it before lock; a correction sent
  after the period has ended carries no such flag.
- **Person or Tjeneste unavailable at lock time**: the lock waits and retries; it never
  produces a partial delivery. Operations is alerted if the lock cannot complete.
- **Person unavailable while reports arrive**: the service stays ready (FR-046); reports are
  answered as received and held, so MeldingsutvekslerReceiver does not pause.
- **Person answers at lock but has no classification for a child** (for example after a
  merged or removed record): the child is frozen as Kode 6/7 for the period, the lock
  completes, and operations is alerted with a count only (FR-008a).
- **Placed child with only a provisional birth date, or none, in Person**: school age is
  decided from the provisional date; a child with no birth date at all is not expected, and
  operations is alerted at lock with the count only (FR-028).
- **Report from a reporter the child is not placed with**: accepted, delivered with the
  reporter's identity and counted; no reporter–placement check is made (FR-019a).
- **Report for a child outside the expected population** (e.g. placement not yet registered
  in Tjeneste, a pupil in upper secondary, or a compulsory-age child without a registered
  utdanningsforhold): accepted and delivered as *received, not expected*.
- **Reporter identity appears in an error path**: rejection reasons, logs and notifications
  never carry the reporter's or the child's identifier.
- **Submission for a period before October 2026**: rejected as outside the validity window.

### Clarifications

#### Session 2026-10-02

- Q: A report's dates fall in a non-reporting period (default July). What happens? → A: Rejected with reason "non-reporting period" and recorded like any other rejection (FR-012).
- Q: A child becomes Kode 6/7 after their report was accepted. What happens to the stored data? → A: Kept under PP-05, excluded from every list, count and delivery, reachable only through classification-aware access until retention expires (FR-009a). *Superseded 2026-10-06: kept, with access following the current gradering (DEC-77).*

#### Session 2026-10-05

- Q: Who reports via Altinn? → A: Private institutions (primary senders) and all foster homes. State institutions stay in BiRK (DEC-52, DEC-54).
- Q: Attendance above plan? → A: Accepted and flagged "attendance above plan" for any excess; the absence-sum check is skipped for that report (FR-015a, DEC-50).
- Q: Absence codes outside the accepted set? → A: Accepted codes are configurable per schema version; an unknown code is accepted, delivered as received and flagged (FR-015b, DEC-51).
- Q: How do private-institution children enter the expected population? → A: They are expected; telling private from state institutions needs provider ownership from Tjeneste (DEP-07), which blocks go-live (DEC-53).
- Q: Coverage per reporter category? → A: Yes, every bucket of the expected population is split by private institution and foster home (DEC-55).
- Q: Caseworker–child relation? → A: Provided by the authorisation service; DEP-05 withdrawn (DEC-56).
- Q: Can Person contain Kode 6/7 children? → A: It may. No component may assume either way; a child is processed only when Person returns it with a known classification that is not Kode 6/7 (FR-008, DEC-58). *Superseded 2026-10-06: Kode 6/7 children are processed (DEC-74).*
- Q: How is a held report protected when its child may be classified? → A: Identifier and content are stored encrypted together; a report resolving as Kode 6/7 is deleted; the "unknown child" count follows the excluded-count option (FR-006, FR-009, DEC-59). *Amended 2026-10-06: a report resolving as Kode 6/7 is processed; the "unknown child" count follows the counting option (DEC-59, DEC-76).*
- Q: May the intake learn that a report was excluded or held? → A: No. Accepted, held and excluded reports get the same answer, received (FR-045, DEC-60). *Amended 2026-10-06: no report is excluded any more; accepted and held reports get the same answer (DEC-74).*
- Q: How is a rejection attributed to an expected child? → A: Identity and classification are looked up before validity rules; the rejection record carries the child's M2LB identity and the reporter when the child resolved as not classified, otherwise neither (FR-007, FR-013, DEC-61). *Amended 2026-10-06: whenever the child resolved, whatever its gradering (DEC-61).*
- Q: May a report be submitted before the period ends? → A: Yes. Within the current period the end date may lie after the submission date, but the start date may not; the report is flagged "submitted before period end" (FR-011, FR-015c, DEC-65).
- Q: How are reports sent on time protected when an outage delays their arrival past the lock? → A: Operations can postpone a single period's lock before it runs, through an audited configuration override. The lock never waits automatically for the intake's backlog (FR-023a, DEC-69).
- Q: How are coverage buckets without a frozen placement split by reporter category? → A: Only *received*, *rejected only* and *missing* are split by category. *Received, not expected* and rejections that cannot be linked to an expected child are given as totals only (FR-029, DEC-70).
- Q: Which reporter category does a child get when a foster home and a private institution placement cover equally many days? → A: The placement that ends last in the period; if both end on the same day, foster home (FR-027a, DEC-71).
- Q: What volume, peak and ingestion response time is the service sized and load-tested for? → A: Up to 10,000 reports per period, a peak of 2,000 per hour, and 95% of reports answered to the intake within 5 seconds (SC-010, DEC-72).
- Q: Which problems besides a failed lock must alert operations before a period locks? → A: Reports still held 24 hours before the scheduled lock; no report received for a configured time between the 1st and the 10th; and the hourly share of rejected reports above a configured threshold (FR-026a, DEC-73).

#### Session 2026-10-06

- Q: May SkoleService process reports for Kode 6/7 children? → A: Yes. They are processed like any other report, and gradering is enforced on every read under the authorisation service's rules (FR-008, DEC-74).
- Q: Which grant lets a user see a Kode 6/7 child? → A: Person's child-specific `Person:SeGradertBarn` for that child, in addition to the read's own operation. One grant covers the whole platform; SkoleService registers no operation of its own (FR-008, FR-043, DEC-74).
- Q: Are rows for Kode 6/7 children delivered to Bufdata? → A: Not for now. They are stored but withheld from the dataset; delivery is decided later (FR-008b, DEC-75, OQ-26).
- Q: Do Kode 6/7 children count in the expected population and coverage? → A: Configurable, off by default. Turning it on requires data protection approval and moves the coverage breakdown behind a dedicated statistical operation (FR-009, DEC-76). *Amended 2026-10-06: only the full breakdown moves; the breakdown delivered to Bufdata always leaves Kode 6/7 children out (FR-035).*
- Q: What if Person cannot say a child's gradering? → A: BiRK has no unknown gradering, so Person always returns one; an answer without gradering is a failed lookup and the report is held (FR-002, FR-006).
- Q: Is a held report that resolves as Kode 6/7 still deleted? → A: No. It is processed like any other report; encryption and own access control while it is held are unchanged (FR-006, DEC-59).
- Q: How does the service know a child's current gradering? → A: It keeps a copy per child from the Person lookup, updated from Person's `SikkerhetsnivåEndret` event and read again at lock (FR-008a, DEC-77). *Superseded 2026-10-09: no copy is kept; gradering is looked up in Person every time it is needed (DEC-122).*
- Q: When the option to count Kode 6/7 children is on, what coverage breakdown goes in the delivery to Bufdata? → A: The delivered breakdown always leaves out Kode 6/7 children, as with the option off; the full breakdown exists only behind the statistical operation. Bufdata has confirmed it reads only the delivered Parquet period files and calls no operation (FR-009, FR-035, FR-036, DEC-78; OQ-09 closed).
- Q: At lock, Person is reachable but returns no gradering for a particular child. What does the lock do with that child? → A: Treats the child as Kode 6/7 at lock (rows withheld, counted only as FR-009 allows), completes the lock and alerts operations with the count only (FR-008a, DEC-79).
- Q: Does the service check that the reporter has the child placed with them? → A: No. Any authenticated reporter's valid report is accepted, delivered with reporter identity and counted; the risk is accepted because placement data cannot identify the provider today (FR-019a, DEC-80).
- Q: Is the dedicated statistical operation built in this feature or only when the counting option is turned on? → A: Built and registered now. With the option off it returns the same breakdown as the delivery; with it on, the full breakdown (FR-009, FR-043, DEC-81).
- Q: A withdrawal or deletion arrives for a report still held because the child is unknown. What happens? → A: The hold ends at once: the held identifier and content for that message chain are deleted, no child identity is recorded, and the report counts as no report (FR-006, DEC-82).
- Q: When more than one reporter submits a report for the same child and period, should the delivery mark this so Bufdata can spot possible double counting? → A: Yes. All reports stand; at lock every delivered row for a child with accepted reports from more than one reporter in the period is flagged "multiple reporters". Date ranges are not compared (FR-017, FR-019, DEC-83).
- Q: Should a young person past compulsory school age stop being expected after a certain age? → A: Only children of compulsory school age (grades 1–10, typically 5–16 years, by birth-year cohort) with an utdanningsforhold overlapping the period, and no manglende skoletilbud covering the whole period, are expected. Upper secondary is no longer expected. Reports for anyone else are accepted and counted as *received, not expected* (FR-027, FR-040a, FR-041, DEC-84; replaces DEC-30).
- Q: If the same reporter sends two reports for the same child and period with different dates, should the second replace the first? → A: No. The date range is part of the report key, so reports with other dates all stand as separate rows; only an exact key match makes a new version. A correction with other dates is a new report and leaves the original. Reports from the same reporter whose dates overlap both stand and are flagged "overlapping reports from same reporter" at lock (FR-017, FR-019, FR-021, FR-033, DEC-85; refines DEC-23).
- Q: Who resolves the child for education records from SkoleAdapter, and who holds records whose child is not yet known? (from the SkoleAdapter clarification session) → A: Utdanning does. SkoleAdapter sends the child reference (Person's external id); Utdanning looks it up through Person on receipt, holds unresolved records encrypted, invisible and unused, links them on Person's `BarnRegistrert`/`BarnEksternIdEndret` events and with a final lookup at lock, deletes the child reference once linked, and never unlinks a linked record (FR-038, FR-040c, FR-040d, DEC-87, EXC-05).
- Q: What does Utdanning do with a delete for a record identity it does not hold? (from the SkoleAdapter clarification session) → A: Nothing; it is a no-op. SkoleAdapter sends every delete because it does not know which records were forwarded (FR-040b, DEC-86).

#### Session 2026-10-07

- Q: How should Utdanning answer SkoleAdapter, and which education records may it reject permanently? (from the SkoleAdapter clarification session) → A: It answers *received* for every create, update or delete it applies or holds, linked or unlinked alike, so the adapter learns nothing about resolution. It rejects permanently, with a reason code published in the ingestion contract, only a record that lacks its record identity, its child reference (create or update) or its from date, or has a date that cannot be read. A code value outside its code lists is a contract mismatch, answered as a failure that is not a permanent rejection, so SkoleAdapter pauses until its mapping is fixed. A record whose to date is before its from date is stored as received and covers no day (FR-040e). (auto-accepted recommendation)
- Q: What should Utdanning do with an update for a record identity it does not hold? (from the SkoleAdapter clarification session) → A: Apply it as a create. SkoleAdapter sends the full content on every update, so a record that was rejected earlier, or missed, comes back with the row's next change in BiRK (FR-040b). (auto-accepted recommendation)
- Q: Are the from and to dates of an utdanningsforhold or manglende skoletilbud inclusive, and what does a missing to date mean? (from the SkoleAdapter clarification session) → A: Both dates are inclusive calendar dates, as SkoleAdapter forwards them from BiRK; a missing to date means open-ended, covering every day from the from date on. A record overlaps a period when it covers at least one of the period's days (FR-039). (auto-accepted recommendation)
- Q: What should SkoleService's readiness signal report, given that MeldingsutvekslerReceiver probes it to decide when to resume after a pause? (from the MeldingsutvekslerReceiver spec, DEC-104) → A: Whether its ingestion paths, database included, can accept requests. Person, Tjeneste and Autorisasjon being unavailable MUST NOT make it not ready, since reports are then held and education records held unlinked, not refused. The signal can be called by the intake's and SkoleAdapter's machine identities and reveals nothing else (FR-046). (auto-accepted recommendation)
- Q: Should the list of record identities given to SkoleAdapter for reconciliation include records SkoleAdapter has already deleted, which Utdanning keeps as history? → A: No. It lists only current records, linked and unlinked; records ended by a delete are left out, so the adapter's reported delete count covers only records that would actually change (FR-040b). (auto-accepted recommendation)
- Q: What default values should the calendar and alert settings ship with where the spec names none? → A: Settling delay 6 hours (lock at 06:00 Europe/Oslo on the 11th); lookup retry for held reports and unlinked education records every hour; inactivity alert after 24 hours with no report within the watch window; rejection-share alert when more than 20% of at least 20 reports in an hour are rejected; lock alert when a lock has not completed 1 hour after its scheduled time. All are defaults only and configurable without a code change (FR-023, FR-026, FR-026a, FR-047). (auto-accepted recommendation)
- Q: How should the service answer the intake or SkoleAdapter when it cannot reach a definite outcome itself, for example when its database is unavailable or two changes to the same report collide? → A: As a temporary failure (a conflict for a collision), never as *rejected*. Only the published reason codes mean a permanent rejection; the caller retries anything else, so a report or record is never lost to the service's own trouble (FR-045b). (auto-accepted recommendation)
- Q: When two periods are open at once (from the 1st until the previous period locks on the 11th), which period do the follow-up lists show? → A: One open period at a time, chosen by the caller, defaulting to the open period that locks first. Each list is calculated for that period from the current data with the rules of FR-027, not frozen, and is marked as provisional until the period locks (FR-041). (auto-accepted recommendation)
- Q: A withdrawal or deletion arrives before the report it refers to (for example after a retry reordered them), and the report is not held. What happens? → A: The same as for a held report: the withdrawal is answered as received and recorded against the message chain, and any message of that chain sent before it has no effect when it arrives later. A later message in the chain, by send time, applies as usual (FR-020). (auto-accepted recommendation)
- Q: How does the service get BirkID, birth date and classification for placed children who sent no report, which the lock and the follow-up lists need? → A: Through a machine-identity lookup in Person by M2LB identity, for many children at once, returning BirkID, birth date and classification for every child, Kode 6/7 included. Person's user-facing `hentBarn` cannot be used, since it applies the caller's own access. The lookup is added to DEP-01 and blocks go-live like the lookup by national identifier (FR-024a). (auto-accepted recommendation)
- Q: Person holds only a provisional birth date for some children, and none for a few. How is compulsory school age decided for them? → A: From the exact birth date when Person has one, otherwise from the provisional one. A placed child with neither is not expected; at lock operations are alerted with the number of such children only, and an accepted report for such a child counts as *received, not expected* (FR-028). (auto-accepted recommendation)

#### Session 2026-10-08

- Q: How is a send time that arrives without a time-zone offset read? (from the MeldingsutvekslerReceiver clarification session) → A: As Europe/Oslo local time. MeldingsutvekslerReceiver forwards `SendtTidspunkt` exactly as received and never converts it, so the submission date is decided here (Assumptions; MeldingsutvekslerReceiver FR-012).

#### Session 2026-10-09

- Q: Does SkoleService need to keep its own record of each child's gradering? → A: No. It looks gradering up in Person every time it is needed: on every read in user context and at lock, where the value read is frozen with the period's basis for delivery and counting. `SikkerhetsnivåEndret` is no longer consumed, and a read fails closed when Person cannot be reached (FR-008a, DEC-122, replaces DEC-77).

## Requirements *(mandatory)*

### Functional Requirements

**Intake and identity**

- **FR-001**: The system MUST accept from MeldingsutvekslerReceiver one period summary per
  child and reporter, containing the child's identifier and its type (fødselsnummer or
  DUF-nummer), reporter type and identity (fødselsnummer or orgnr), period dates, planned and
  actual hours, absence lines, message id, send time and message-chain information.
  [SR-FR-001]
- **FR-002**: The system MUST resolve the child's identifier through Person to the child's
  M2LB identity, BirkID, birth date and classification, for every child, Kode 6/7 included.
  Only verified identifiers match. An answer without a classification MUST be treated as a
  failed lookup (FR-006), never as an unclassified child. [SR-FR-002, DEP-01, DEC-74]
- **FR-003**: A report without a child identifier MUST be rejected. A pseudonym identifier, if
  present, MUST be ignored and not stored. [SR-FR-003, DEC-09]
- **FR-004**: Once resolved, the child's fødselsnummer/DUF-nummer MUST be discarded and MUST
  NEVER be persisted (except under FR-006), published, delivered or written to logs, traces or
  telemetry. [SR-FR-004, MP-01]
- **FR-005**: The reporter's type and identity MUST be stored with the report and delivered on
  each row. It MUST NOT appear in notifications, logs, traces or telemetry. The reporter's name
  and contact information are never received or stored. [SR-FR-004a, DEC-24, EXC-03]
- **FR-006**: A report whose child identifier cannot be resolved, or cannot be looked up
  because Person is unavailable, MUST be answered as received and held as possibly concerning a
  classified child: the identifier and the report content are stored encrypted together under
  their own access control, and the lookup is retried when Person reports new or changed persons
  and at a fixed interval. On resolution the identifier MUST be deleted and the report
  processed, whatever the child's classification. At lock, a final lookup MUST be made; reports
  still unresolved MUST be rejected with reason "unknown child", counted as rejected (subject to
  FR-009), and their identifiers and content deleted. A withdrawal (`Trukket`) or deletion
  (`Slettet`) for a held report MUST end the hold at once: the held identifiers and content of
  that message chain are deleted, no child identity is recorded, and the report counts as no
  report. The withdrawal is recorded against the message chain only, so a message in that
  chain sent before it has no effect when it arrives later; a new submission outside the chain
  is handled as a fresh report. [SR-FR-005, SR-FR-006, DEC-13, DEC-59, DEC-74, DEC-82, EXC-02]
- **FR-007**: The child's identity and classification MUST be looked up before validity rules
  are applied whenever Person answers. Reports for Kode 6/7 children are validated and
  rejected under the same rules as any other. Validity rules that do not depend on the child's
  identity MUST be applied before a report is held, so an invalid report never causes an
  identifier to be held. [DEC-61, DEC-74]

**Classification**

- **FR-008**: Reports for Kode 6/7 children MUST be processed like any other report.
  Classification MUST be evaluated as part of every read in user context, never as a filter
  applied afterwards: a Kode 6/7 child MUST be visible only to a user the authorisation service
  grants `Person:SeGradertBarn` for that child, in addition to the operation the read itself
  requires. To anyone else the child MUST NOT appear in any list, count, lookup or error message,
  and a direct lookup MUST answer as if the child did not exist. A child whose classification
  is not available when data is read MUST be treated as Kode 6/7. Classification MUST NEVER be
  inferred from whether a child exists in Person. [SR-FR-007, SR-FR-008, MP-05, DEC-74]
- **FR-008a**: The system MUST NOT keep its own record of a child's classification. It MUST
  look classification up in Person (DEP-01) every time it is needed: on every read in user
  context, so a change in Person takes effect from the next read, and at lock. When Person
  cannot be reached, a read that needs classification MUST fail closed with no data. At lock,
  classification MUST be read from Person for every child in the period and frozen with the
  period's basis, where it decides delivery and counting only, never access. When Person answers but returns no classification for a
  child, that child MUST be frozen as Kode 6/7 for the period (rows withheld under FR-008b,
  counted only as FR-009 allows), the lock MUST complete, and operations MUST be alerted with
  the number of such children only. Person being unavailable is handled under FR-026.
  [SR-FR-007a, DEC-79, DEC-122]
- **FR-008b**: Rows for children who are Kode 6/7 at lock MUST NOT be part of the Bufdata
  dataset; their reports MUST stay stored. Whether and how they are delivered later is open
  (OQ-26); a later decision applies to periods that lock after it, and datasets already
  delivered are not changed (FR-025). [SR-FR-008b, DEC-75]
- **FR-009**: Whether Kode 6/7 children (by classification at lock) are part of the expected
  population and the coverage figures MUST be a configuration option, off by default. When off,
  they MUST be left out of the population and every coverage figure, rejection counts and the
  "unknown child" count (which may include Kode 6/7 children Person does not contain) included.
  When on, they are counted like any other child in a full coverage breakdown; because their
  number can then be derived from it, the full breakdown MUST be exposed only through a
  dedicated statistical operation with its own access control, and the period-closed
  notification MUST NOT carry coverage counts. The statistical operation MUST exist whatever
  the option's setting: with the option off it returns the same breakdown as the delivery,
  so turning the option on needs only configuration and approval. The breakdown delivered to
  Bufdata is not affected by the option (FR-035). Turning the option on requires approval by the data protection officer. A
  change of the option applies to periods that lock after it. [SR-FR-009, DEC-76, DEC-81, OQ-03]
- **FR-009a**: A change of a child's classification MUST NOT delete any data. Access follows
  the current classification (FR-008a); the classification frozen at lock decides delivery
  (FR-008b) and counting (FR-009) for that period. Correct classification for reclassified
  children depends on Person receiving reclassifications (DEP-09). [PP-04, PP-05, MP-05,
  DEC-122]

**Validity control (reject with machine-readable reason)**

- **FR-010**: The system MUST reject a report when: any hour value is negative or greater than
  the configured maximum for the report's date range (default: 24 × the number of days from
  start date to end date, inclusive); the start date is after the end date; or the report is a
  receipt/reply message type. [SR-FR-010, SR-FR-012, §9.6, DEC-67]
- **FR-011**: The system MUST reject a report whose dates (as written, `xs:date`; any time zone is ignored, DEC-126) are not
  within a single open period of the validity window on the submission date: they span two
  periods, lie in a period that has not started, lie in a locked period, or lie before the first
  reporting period (October 2026). In the current period the end date MAY lie after the
  submission date, but the start date MUST NOT. Partial periods are valid. The window MUST be
  computed from configuration. Example: on 5 November, dates in 1–31 October, or in November
  starting no later than 5 November (for example 1–30 November), are valid; after the October
  lock on 11 November, only November is open. [SR-FR-013, SR-FR-016, DEC-25, DEC-65]
- **FR-012**: The system MUST reject a report whose dates fall in a configured non-reporting
  period (default July) with reason "non-reporting period". [SR-FR-016a, DEC-33]
- **FR-013**: Every rejection MUST be recorded with message id, reason code and period. When
  the child was resolved, whatever its classification, the record MUST also hold the child's
  M2LB identity and the reporter, so the rejection can be attributed in coverage (FR-029); a
  rejection record for a Kode 6/7 child is read under FR-008 like the child's other data. When
  the child was not resolved, the record MUST hold neither. A rejection record MUST NEVER
  contain a national identifier. [§9.7 Avvisning, DEC-26, DEC-61]

**Quality control (accept and flag)**

- **FR-014**: Duplicate absence codes in one report MUST be merged by summing their hours,
  without rejection or flag. [DEC-27]
- **FR-015**: When an absence breakdown is present and its sum differs from planned minus
  actual hours by more than 0.5 hours, the report MUST be accepted and flagged "inconsistent
  hour totals". This check MUST NOT be applied when FR-015a applies. [SR-FR-017]
- **FR-015a**: When actual hours exceed planned hours by any amount, the report MUST be
  accepted and flagged "attendance above plan". [SR-FR-017a, DEC-50]
- **FR-015b**: Accepted absence codes MUST be configurable per schema version (default for
  v2.0.1: `1`, `2`, `3`, `4`, `5`, `9`, compared as strings without zero-padding). A code outside
  the configured list MUST be accepted, delivered as received, and the report flagged "unknown
  absence code". [SR-FR-017b, DEC-51, MP-08]
- **FR-015c**: When a report's end date is after its submission date, the report MUST be
  accepted and flagged "submitted before period end". The flag belongs to the version, so a
  correction sent after the period has ended replaces it without the flag. [SR-FR-017c, DEC-65]
- **FR-016**: A report without an absence breakdown MUST be accepted; it MUST be flagged
  "missing absence breakdown" when planned hours exceed actual hours, and not flagged when they
  are equal. [SR-FR-019, DEC-28]
- **FR-017**: Quality flags MUST be stored with the report and delivered on the same row. The
  "multiple reporters" and "overlapping reports from same reporter" flags (FR-019) are set at
  lock and stored with the locked period. [SR-FR-018, DEC-83, DEC-85]

**Corrections, chains and idempotency**

- **FR-018**: Re-delivery of a message with an id already processed MUST have no effect and
  return the same outcome. [SR-FR-020]
- **FR-019**: A report is identified by child + period + reporter + date range (start and end
  date). A new valid submission for the same key within the validity window MUST become a new
  version that replaces the previous one; previous versions MUST be kept as history. Reports
  with different keys MUST all stand, whether they come from different reporters or from the
  same reporter with other dates, and whether or not their date ranges overlap. At lock, among
  accepted reports (not withdrawn or deleted) for a child and period: every delivered row MUST
  be flagged "multiple reporters" when they come from more than one reporter; and every
  delivered row whose dates overlap another report from the same reporter MUST be flagged
  "overlapping reports from same reporter". [SR-FR-021, DEC-23, DEC-83, DEC-85]
- **FR-019a**: The system MUST NOT check whether the reporter has the child placed with them.
  A valid report from any reporter authenticated by Altinn MUST be accepted, delivered with
  the reporter's identity (FR-005) and counted in coverage, even when the child is placed
  with someone else. This is an accepted risk: placement data cannot identify the provider
  today (DEP-07, DEC-70), and the reporter identity on each row lets Bufdata judge the report.
  [SR-FR-021a, DEC-80]
- **FR-020**: Message-chain values MUST be applied as follows: no chain → create (or new
  version); `Oppdatering` → correct; `Duplikat` → no effect for a known id, otherwise treat as
  correction; `Trukket` → withdraw; `Slettet` → delete; `Tillegg` → log and acknowledge; receipt
  and reply values → reject. Withdrawn and deleted reports are not delivered. A withdrawal or
  deletion that arrives before the report it refers to MUST be answered as received and
  recorded against the message chain; a message of that chain sent before it MUST have no
  effect when it arrives later, while a message sent after it applies as usual (FR-022), as
  for held reports (FR-006). [SR-FR-022, §9.6]
- **FR-021**: A correction whose child, period, reporter or date range differs from the report
  it points to MUST be processed as a new report under its own key, leaving the original
  unchanged. [DEC-34, DEC-85]
- **FR-022**: The current version MUST be decided by send time, not arrival order, and
  concurrent changes to the same report MUST NOT overwrite each other. [SR-FR-023]

**Period lock**

- **FR-023**: Each reporting period MUST lock at the configured lock time (default: the 11th
  of the following month, after a settling delay, Europe/Oslo). Non-reporting periods MUST
  NOT lock. [SR-FR-024, MP-09]
- **FR-023a**: Operations MUST be able to postpone the lock of a single period to a later
  time through a configuration override, as long as that lock has not run. Every override
  MUST be audited (who, when, old and new lock time). Until the postponed lock runs, the period
  stays open and reports for it are processed under the normal validity window. A lock that
  has run MUST NOT be postponed or undone. The lock MUST NOT wait automatically for a backlog
  in the intake. [SR-FR-024a, DEC-69]
- **FR-024**: The lock MUST, as one all-or-nothing step: close the period, make a final lookup
  for held reports and reject those still unresolved, make a final lookup for unlinked
  education records (FR-040c), freeze the expected population (with BirkID and placement period per child),
  calculate coverage and prepare the dataset. [SR-FR-025]
- **FR-024a**: For every child with a placement in the period, whether or not a report was
  received, the lock MUST get BirkID, birth date and classification from Person through a
  machine-identity lookup by M2LB identity covering many children at once, Kode 6/7 included
  (DEP-01). The follow-up lists (FR-041) use the same lookup for the children they evaluate.
  Person's user-facing child lookup MUST NOT be used for this, since it applies the caller's
  own access. [DEP-01, DEC-122]
- **FR-025**: Running the lock again for a locked period MUST change nothing. After lock,
  nothing belonging to the period may change. [SR-FR-026, SR-FR-027, MP-03]
- **FR-026**: If a dependency needed for the lock is unavailable, the lock MUST retry and MUST
  NOT produce a partial result; operations MUST be alerted when the lock cannot complete.
- **FR-026a**: Before a period locks, operations MUST also be alerted when: (a) reports for the
  period are still held 24 hours before its scheduled lock; (b) no report has been received
  for a configured length of time within a configured watch window (default: the 1st to the
  10th of the month after the period; a sign of an intake outage); (c) the share of rejected reports in an hour exceeds a
  configured threshold (a sign of a misconfiguration or contract mismatch). Alerts carry only
  the period, counts and reason codes, never a child's or reporter's identifier.
  [SR-FR-027a, DEC-73]

**Expected population and coverage**

- **FR-027**: A child MUST be expected in a period when all apply: (a) the child is in a
  foster home placement of any type or a private institution placement overlapping at least one
  day of the period, as historically recorded for that period, or a placement whose category
  Tjeneste does not know (`Ukjent`, which is every placement until BiRK supplies the kind);
  placements in state institutions do not count; (b) the child is of compulsory school age (grades 1–10) in the period;
  children past compulsory school age are never expected, upper secondary included; (c) the
  child has an utdanningsforhold of any type, `unknown` included, overlapping at least one day of
  the period; (d) the child is not covered by a manglende skoletilbud for the whole period.
  A child who fails (c) is not expected and is found through the clean-up list (FR-041 (b)).
  [SR-FR-028..030, DEC-31, DEC-53, DEC-54, DEC-84, DEP-02, DEP-07]
- **FR-027a**: Each child in the frozen population MUST carry its reporter category (private
  institution or foster home), derived from the placement type; a child with both kinds of
  placement in the period belongs to the category of the placement covering most days. On a
  tie, the placement that ends last in the period decides; if both end on the same day, the
  child belongs to foster home. A child whose only placements overlapping the period are
  `Ukjent` (or `Ukjent` next to a state institution or `Annen`) carries the reporter category
  `Unknown`: a known category always wins over `Ukjent`, whatever the days. The child is
  expected and counted in the totals, but in neither the private institution nor the foster home
  split, so the two splits can add up to less than the total. [SR-FR-031, DEC-55, DEC-71, DEC-66]
- **FR-028**: Compulsory school age MUST be decided by birth year (school-year cohort), not
  exact birthday. The birth year MUST be taken from the exact birth date in Person, or, when
  Person has none, from its provisional birth date. A placed child with neither MUST NOT be
  expected; at lock, operations MUST be alerted with the number of such children only, and an
  accepted report for such a child counts as *received, not expected*. [SR-FR-028]
- **FR-029**: Coverage MUST be counted per expected child: each expected child is in exactly
  one of *received* (at least one accepted report), *rejected only* (only rejected reports,
  attributed through the child identity on the rejection record, FR-013) or *missing* (no
  report), and received + rejected only + missing = expected. Accepted reports
  for children outside the population MUST be counted as *received, not expected*. Rejections
  that cannot be attributed to an expected child MUST be counted by reason. Kode 6/7 children,
  and the "unknown child" count, are counted only as FR-009 allows. Withdrawn and deleted
  reports count as no report. *Received*, *rejected only* and *missing* MUST also be given per
  reporter category (FR-027a). *Received, not expected* and rejections that cannot be
  attributed to an expected child have no frozen placement and MUST be given as totals only;
  their category MUST NOT be derived from the reporter type. [SR-FR-031, DEC-26, DEC-55,
  DEC-70, DEC-76]
- **FR-030**: Coverage MUST NEVER be delivered only as a single percentage, and every figure
  MUST be reproducible from the frozen basis. [SR-FR-031, SR-FR-032, MP-06]
- **FR-031**: There is no "not applicable" code: a child in hospital or otherwise absent is
  reported normally with planned hours = actual hours + absence hours. [DEC-32]

**Delivery to Bufdata**

- **FR-032**: The system MUST produce exactly one dataset per locked period. Producing it again
  MUST give an identical result. Bufdata loads it by replacing the period, not by merging rows.
  [SR-FR-037, SR-FR-041]
- **FR-033**: Each row MUST represent one report (FR-019): one child who is not Kode 6/7 at
  lock (FR-008b), one period, one reporter and one date range, and contain: BirkID,
  period, report dates, reporter type and identity, planned hours, actual hours, absence lines
  (code, hours), source and quality flags. Source is one of `ALTINN`, `BIRK`, `M2LB`; this
  service delivers `ALTINN`. [SR-FR-038, DEC-35, EXC-01, EXC-03]
- **FR-034**: The child's fødselsnummer/DUF-nummer MUST NEVER be part of the dataset.
  [SR-FR-039]
- **FR-035**: A coverage breakdown MUST be delivered with the dataset. It MUST always be
  calculated as with the option in FR-009 off, whatever the option's setting: it leaves out
  Kode 6/7 children and the "unknown child" count, so it matches the delivered rows and no
  Kode 6/7 count can be derived from the delivery. [SR-FR-040, DEC-76, DEC-78]
- **FR-036**: Bufdata reads only the delivered period files (dataset and coverage breakdown)
  and calls no operation of the service. The files MUST be readable by Bufdata's machine
  identity under a general grant, without child-specific access. These machine-to-machine
  reads are not read-logged (DEC-125). A general grant suffices because the files hold no
  Kode 6/7 rows (FR-008b) and the breakdown leaves Kode 6/7 children out (FR-035). [SR-FR-042,
  SR-FR-045, DEC-37, DEC-75, DEC-78, DEC-125]
- **FR-037**: A `SkolenaervaerPeriodClosed` notification MUST be published exactly once per
  locked period, carrying only the period, the number of delivered rows, the coverage counts
  (not when the option in FR-009 is on) and a correlation id, never a separate count of Kode 6/7
  or unknown children, and only once the dataset is available. [SR-FR-043, DEC-60, DEC-76]

**Education master data (Utdanning)**

- **FR-038**: The system MUST accept creates, updates and ends/deletes of utdanningsforhold
  (child reference, from–to, education type, school type) and manglende skoletilbud (child
  reference, from–to, reason) from SkoleAdapter, keyed on record identity. The child reference
  is Person's external id for the child; the system MUST resolve it to the child's M2LB
  identity through Person (DEP-08) when a create or update is received, for every child,
  Kode 6/7 included. Deletes need no lookup. [UT-FR-001, UT-FR-002, UT-FR-004, UT-FR-006,
  DEC-87, EXC-05]
- **FR-039**: The system MUST store only the fields the expected population needs and keep
  temporal validity, so the state as of any past period can be read. The from and to dates of
  an education record are inclusive calendar dates; a missing to date means open-ended. A
  record overlaps a period when it covers at least one of the period's days (Europe/Oslo).
  [UT-FR-003, UT-FR-005]
- **FR-040**: Ingestion MUST be idempotent per source change. Each record is identified by a
  stable record identity supplied by SkoleAdapter; no BiRK table key is received. The child
  reference is the only BiRK-side value received (EXC-05). [CA-FR-009, DEC-47]
- **FR-040a**: Education type, school type and missing-offer reason code lists MUST include
  `unknown`. An utdanningsforhold of unknown type MUST count as an utdanningsforhold both for the
  expected population (FR-027 (c)) and for the clean-up list. [DEC-49, DEC-84]
- **FR-040b**: A create for an already known record identity MUST be applied as an update, and
  an update identical to the stored state MUST have no effect, so a full reload from
  SkoleAdapter is idempotent. An update for a record identity the system does not hold MUST be
  applied as a create, since SkoleAdapter sends the full content on every update; a record
  rejected or missed earlier then comes back with the row's next change. A delete for a record
  identity the system does not hold MUST have no effect. The system MUST provide, to SkoleAdapter's machine identity only, the list
  of record identities it currently holds from SkoleAdapter, unlinked records included and
  records ended by a delete left out (identities only, no fields or child references), for
  SkoleAdapter's reconciliation. [DEC-63, DEC-86, DEC-90]
- **FR-040c**: A create or update whose child reference Person does not resolve, or that
  arrives while Person is unavailable, MUST be held unlinked: the child reference and fields
  encrypted with a Key Vault key under their own access control, invisible to every read, and
  not used by the expected population (FR-027) or the clean-up list (FR-041). The system MUST
  try to link unlinked records when Person publishes `BarnRegistrert` or `BarnEksternIdEndret`
  for a matching external id, at a fixed interval, and in a final lookup before the expected
  population is frozen at lock (FR-024). On linking, the child reference MUST be deleted. A
  delete from SkoleAdapter for an unlinked record MUST delete the record with its child
  reference and fields. Operations MUST see the number of unlinked records, and be alerted
  when any record has been unlinked longer than a configurable time (default 30 days), with
  counts only. [UT-FR-006, DEC-87, EXC-05]
- **FR-040d**: A linked record MUST NEVER be unlinked by a failed or missing lookup. An update
  for a linked record whose child reference does not resolve MUST leave the record as it is
  and be held, under FR-040c, until it resolves; an update that resolves to another child moves
  the record to that child. Held updates for the same record MUST be applied in the order
  received, a newer one replacing an older one still held. The child reference MUST NOT appear
  in any read API, domain event, log, trace, metric or delivery. [UT-FR-007, DEC-87, EXC-05]
- **FR-040e**: Utdanning MUST answer SkoleAdapter *received* for every create, update or
  delete it applies or holds, whether the record is linked or unlinked, so the answer reveals
  nothing about the child's resolution or classification. It MUST reject permanently, with a
  reason code published in the ingestion contract, only a create or update that lacks its
  record identity, its child reference or its from date, or carries a date that cannot be read
  (and a delete that lacks its record identity). A code value outside its code lists is a
  contract mismatch: it MUST be answered as a failure that is not a published permanent
  rejection, so SkoleAdapter pauses until its mapping is fixed (SkoleAdapter FR-018). A record
  whose to date is before its from date MUST be stored as received; it covers no day, so it
  counts for no period. [DEC-64 pattern]

**Follow-up lists**

- **FR-041**: During the validity window the system MUST provide (a) expected children without
  an accepted report and (b) children of compulsory school age in foster homes or private
  institutions without a registered utdanningsforhold. Since such children are not expected
  (FR-027 (c)), list (b) is the only place they are surfaced. Both lists cover one open period
  at a time, chosen by the caller and defaulting to the open period that locks first; they are
  calculated from the current data with the rules of FR-027 and are provisional until that
  period locks. [SR-FR-034, SR-FR-035]
- **FR-042**: Access to these lists MUST be child-specific: the authorisation service decides
  access per child and provides the children a caseworker has an active relation to. A Kode 6/7
  child MUST appear, and be counted, only for a caseworker who also holds
  `Person:SeGradertBarn` for the child (FR-008), whatever the option in FR-009. Every lookup
  MUST be read-logged. [SR-FR-036, DEC-56, DEC-74]

**Access, audit and retention**

- **FR-043**: The system MUST register its operations at startup: a general operation for
  Bufdata delivery access, a child-specific operation for the follow-up lists, a general
  operation for SkoleAdapter's record-identity list (FR-040b), and a general operation with
  its own access control for the statistical coverage breakdown (FR-009, DEC-81). Visibility of Kode 6/7 children
  uses Person's `Person:SeGradertBarn`; the system MUST NOT register an operation of its own for
  it. [SR-FR-044, DEC-74]
- **FR-044**: Retention MUST be configurable per data category (reports and versions, reporter
  identity, frozen populations, delivery datasets, rejection records, unlinked education
  records), and the values MUST be
  set and approved before go-live. Held identifiers are deleted at the latest at lock.
  [SR-FR-046, DEC-38, OQ-25]
- **FR-045**: Every report MUST be answered to the intake with a definite result before the
  intake acknowledges it: *received* for accepted and held reports alike, so the intake cannot
  tell them apart or learn anything about the child's classification, or *rejected* with a
  reason code. [SC-007, DEC-60]
- **FR-045a**: The reason codes returned to the intake MUST be published in the ingestion
  contract and cover every rejection made at intake (FR-003, FR-010, FR-011, FR-012). A new
  code MUST be published before it is used, so the intake can register it; until then the
  intake treats it as a failure to fix, never as a permanent rejection. The "unknown child"
  rejection is made at lock and is never returned to the intake. [DEC-64]
- **FR-045b**: When the service cannot reach a definite outcome for a report or an education
  change — for example its database is unavailable, or a concurrent change to the same report
  or record collides (FR-022) — it MUST answer the caller with a temporary failure (a conflict
  for a collision), never with *rejected* or a published reason code. Only the published
  reason codes mean a permanent rejection, so MeldingsutvekslerReceiver and SkoleAdapter retry
  every other answer and no report or record is lost to the service's own failure.
- **FR-046**: The system MUST expose separate liveness and readiness health signals. Readiness
  MUST report whether the ingestion paths (report intake and Utdanning), database included,
  can accept requests, and MUST be callable by MeldingsutvekslerReceiver's and SkoleAdapter's
  machine identities, which probe it while paused. Person, Tjeneste or Autorisasjon being
  unavailable MUST NOT make the service not ready, because reports are then held (FR-006) and
  education records held unlinked (FR-040c), not refused. The signal MUST reveal nothing
  beyond ready or not ready. [GL-35, PS-08, DEC-104]
- **FR-047**: Where this specification names a configurable value without a default, the
  defaults MUST be: settling delay before lock 6 hours (the lock runs at 06:00 Europe/Oslo on
  the 11th); lookup retry interval for held reports (FR-006) and unlinked education records
  (FR-040c) 1 hour; inactivity alert (FR-026a (b)) after 24 hours with no report received
  within the watch window; rejection-share alert (FR-026a (c)) when more than 20% of the
  reports answered in an hour are rejected and at least 20 reports were answered; lock alert
  (FR-026) when a lock has not completed 1 hour after its scheduled time. Every such value,
  and every value given elsewhere as a default, MUST be configuration that operations can
  change without a code change.

### Key Entities

Business names, with the English code name in brackets (data-model.md has the tables).

- **Skolenærværsrapport** (`Report`): One child's attendance report for one period from one reporter for
  one date range. Identified by child (M2LB identity) + period + reporter + date range. Holds status (held, accepted,
  replaced, withdrawn, deleted, rejected, delivered, withheld — locked but not delivered
  because the child is Kode 6/7 at lock), current version and source.
- **Rapportversjon** (`ReportVersion`): One version of a report: report dates, planned and actual hours, quality
  flags, message id, chain reference, send time, registered time. Corrections add versions;
  replaced versions are kept as history.
- **Fraværslinje** (`FravaerLine`): One absence line of a version: absence code and hours.
- **Rapportør** (`Reporter`): The reporter of a report: type (person or organisation) and identity
  (fødselsnummer or orgnr).
- **Holdt identifikator** (`HeldReport`): The child identifier of a held report, stored encrypted together
  with the report content, with identifier type and retry count. The identifier is deleted on
  resolution, on withdrawal or deletion of the report, or at lock; the content is deleted on
  withdrawal or deletion, or at lock if unresolved.
- **Rapporteringsperiode** (`ReportingPeriod`, `PeriodLock`): A period (month) with its lock time and status (open, locked,
  non-reporting), derived from configuration.
- **Dekningsgrunnlag** (`dekning_basis`): The population frozen at lock: per child, BirkID, placement period,
  reporter category (private institution or foster home), classification at lock and coverage
  bucket. Basis for every coverage figure; Kode 6/7 children are in it only under FR-009.
- **Avvisning** (`Rejection`): A rejection record: message id, reason code, period and, when the child was
  resolved, the child's M2LB identity and the reporter. No national identifier.
- **Utdanningsforhold**: A child's education relation: record identity, child (M2LB identity)
  once linked, from–to, education type, school type.
- **ManglendeSkoletilbud**: A registration that a child lacks a school offer: record identity,
  child (M2LB identity) once linked, from–to, reason.
- **Ukoblet utdanningspost** (`unlinked_utdanning_record`): An education record or update whose child is not resolved yet:
  record identity, received time and, encrypted, the child reference and fields. Invisible and
  unused; deleted when linked (reference) or when SkoleAdapter deletes the record (FR-040c).
- **Periodeleveranse** (`delivery`): The final dataset for one locked period, with its coverage breakdown
  (always without Kode 6/7 children, FR-035).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The October 2026 period is delivered to Bufdata on 11 November 2026, and every
  later reporting period is delivered on the 11th of the following month, or when its lock
  runs if operations postponed it under FR-023a. (At risk: depends on
  DEP-01, DEP-02 and DEP-07. If DEP-07 is not ready by 1 November 2026, the go/no-go decision
  in DEC-66 applies.)
- **SC-002**: Zero child fødselsnummer or DUF-nummer values exist in any stored data,
  notification, log or delivery, except encrypted held identifiers before their period locks.
  Reporter identity appears only in stored reports and the delivery.
- **SC-003**: Zero held identifiers remain after a period's lock.
- **SC-004**: In authorisation scenario tests covering every list and lookup, zero reads show,
  count or reveal a Kode 6 or Kode 7 child to a user without `Person:SeGradertBarn` for that
  child, and 100% of such reads show the child to a user with the relation and the grant. Zero
  rows for children who are Kode 6/7 at lock appear in any Bufdata dataset. For children
  reclassified after registration this depends on DEP-09.
- **SC-005**: Re-running the lock or the export for any locked period gives a byte-for-byte
  identical result in 100% of runs.
- **SC-006**: 100% of coverage figures for a delivered period can be traced to individual
  entries in the frozen basis, and received + rejected only + missing equals expected.
- **SC-007**: 100% of messages handed to the service end in exactly one of accepted, held or
  rejected, and none is acknowledged before that outcome is recorded.
- **SC-008**: A report for a child whose Person registration appears before lock is delivered
  in that period in 100% of cases.
- **SC-009**: Bufdata can load each period without manual correction, and the "period closed"
  notification is never published before the dataset is available.
- **SC-010**: In a load test before go-live with 10,000 reports for one period, arriving at a
  peak rate of 2,000 per hour, 95% of reports are answered to the intake (received or
  rejected) within 5 seconds, and none is lost. (DEC-72)
- **SC-011**: An education record whose child is in Person before a period locks counts in that
  period's expected population in 100% of cases; zero linked education records are ever
  unlinked; and zero child references remain stored for linked records or appear in any read
  API, event, log or delivery.

## Assumptions

- The submission date used for the validity window is the date of the message's send time in
  Europe/Oslo. A send time without a time-zone offset, which MeldingsutvekslerReceiver forwards
  unchanged, is read as Europe/Oslo local time. A report that arrives after its period has
  locked is rejected regardless of send time; protection against a delayed arrival is a
  postponed lock (FR-023a).
- The settling delay before lock is configurable and long enough for messages sent on the 10th
  to arrive; the default of 6 hours (FR-047) covers the intake's retry cycle of about 16
  minutes many times over.
- Compulsory school age follows the Norwegian school-year cohort: a child starts in August of
  the calendar year they turn 6 and finishes in June of the calendar year they turn 16.
- Reports are always for one child and one period (XSD v2.0.1); only XSD v2.0.1 reaches the
  service (it replaced v2.0.0 on 2026-10-14, DEC-126), and structural validation has already
  happened in MeldingsutvekslerReceiver.
- Hours are whole numbers in XSD v2.0.1, but the contract accepts decimals; the 0.5-hour
  tolerance applies to the absolute difference.
- Volume is up to 10,000 reports per period, corrections included, with a peak of 2,000 per
  hour before the Altinn deadline (about 7,000 school-age children are in foster homes and
  private institutions). The figure is an estimate and is adjusted if BiRK numbers show
  otherwise (DEC-72).
- The caseworker-facing presentation of the follow-up lists is out of scope; this feature
  provides the data and access rules.
- BirkID is available from Person for every child in the expected population while BiRK is in
  use.
- BiRK has no child with unknown classification (checked in production, 2026-10-06), so Person
  always returns a known classification.
- Person contains Kode 6/7 children with their classification once PersonAdapter imports them
  (DEP-09). Until then, reports for Kode 6/7 children cannot be resolved, and their education
  records stay unlinked.
- The child reference on BiRK education rows is the value Person holds as the child's external
  id (`EksternId`, BiRK `BarnPK`), and Person's `BarnRegistrert` and `BarnEksternIdEndret`
  events carry it.

## Dependencies & Open Items

| ID | Dependency | Owner | Blocks |
|----|------------|-------|--------|
| DEP-01 / OQ-21 | Machine identity lookup in Person by fødselsnummer or DUF-nummer, returning M2LB identity, BirkID, birth date and classification for every child, Kode 6/7 included (DEC-74); and a machine identity lookup by M2LB identity for many children at once, returning BirkID, birth date (exact or provisional) and classification, used at lock and by the follow-up lists (FR-024a). Neither exists today; Person's `hentBarn` applies the user's own access and cannot be used. | Development team (Person) | User Stories 1, 2, 4, 6, 8; go-live |
| DEP-02 / OQ-01 | Historically correct foster home and private institution placements for a period. Placement dates exist in Tjeneste but no query answers it, and corrected dates overwrite history. | Development team (Tjeneste) | User Story 6; go-live |
| DEP-04 / OQ-09 | **Closed 2026-10-06.** Bufdata confirmed it reads only the delivered Parquet period files and calls no operation (FR-036, DEC-37, DEC-78). | Bufdata | — |
| DEP-04 / OQ-12 | Sample message and field mapping against Bufdata's existing extract. | Product owner, Bufdata | FR-033 |
| DEP-07 | Provider ownership (private vs state) for institution placements per period. Tjeneste has the service type but no provider or ownership field today. **Blocks go-live and SC-001.** No fallback is chosen; a go/no-go point on 1 November 2026 decides what happens if it is late (DEC-66). | Development team (Tjeneste), BiRK | User Story 6, FR-027; go-live |
| Autorisasjon | Per-child access decision, the set of children a caseworker has an active relation to (`BarneRelasjon`), and the per-child decision and grant list for `Person:SeGradertBarn` (as used by Person's child search). Exists today. | Development team (Autorisasjon) | User Stories 2, 8 |
| DEP-06 | Person events for new and changed persons, used to retry held reports and to link unlinked education records (`BarnRegistrert`, `BarnEksternIdEndret`, carrying the external id). Classification is looked up, never taken from events (FR-008a). | Development team (Person) | User Stories 4, 7; FR-040c |
| DEP-08 | Machine identity lookup in Person by the child's external id, returning M2LB identity for every child, Kode 6/7 included, or not found, with access control for machine callers. Today's `hentBarnRegistreringIdFraEksternId` has no access check. | Development team (Person) | User Story 7, FR-038; go-live |
| DEP-09 | PersonAdapter imports Kode 6/7 children with their classification and forwards classification changes for children already in Person. Today the classification guard drops every Kode 6/7 `Barn` event, new children and updates alike, so Kode 6/7 children never reach Person and reclassified children keep the old classification. Until then their reports are rejected as "unknown child" at lock. Requires revising PersonAdapter's principle III. Platform-wide (PP-04). | Development team (Person), architect | User Story 2, FR-009a, SC-004 |
| OQ-03 | Whether Kode 6/7 children are counted in population and coverage. Configurable; default off; on only with data protection approval. | Product owner, data protection | FR-009 (default in place) |
| OQ-26 | Whether, how and under what access rows for Kode 6/7 children are delivered to Bufdata. Withheld until decided. | Product owner, data protection, Bufdata | FR-008b (default in place) |
| OQ-25 | Retention per data category and the platform rule for GDPR deletion. | Data protection | Go-live (FR-044) |
| ~~EXC-01..05~~ | ~~Constitution exceptions (BirkID in delivery, held identifier, reporter identity, education record identities, child reference for education records) must be approved.~~ Approved 2026-10-08 (Skole constitution v2.1.2). | Solution architect, data protection officer | — |

## Out of Scope

- State institution attendance (stays in BiRK, delivered directly to Bufdata). [DEC-01]
- Message transport, decryption, XSD validation and receipts to Meldingsutveksleren
  (MeldingsutvekslerReceiver's feature).
- BiRK change capture and code translation (SkoleAdapter's feature).
- A caseworker view of a child's attendance history.
- Registering attendance in M2LB after migration from BiRK.
- Reporting missing school offers through a new form.
- Pseudonymisation; linking attendance to tiltak in Tjeneste.
