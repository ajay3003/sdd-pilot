# Feature Specification: MeldingsutvekslerReceiver — shared intake from Meldingsutveksleren

**Feature Branch**: `feature/skolenaervaer-spec`

**Created**: 2026-10-02

**Status**: Draft

**Input**: User description: "Meldingsutvekslermottaket constitution and spec. Source: docs/analysis/skolenaervaer-reference.md §1–§6, §7 (unit Meldingsutvekslermottaket), §9.6 (message chain), decisions DEC-01..DEC-44. Shared M2LB intake from Meldingsutveksleren; skolenærvær v2.0.0 is the first and only message type."

**Governing documents**: `MeldingsutvekslerReceiver/.specify/memory/constitution.md` (module,
v1.8.1) under the root platform constitution. Domain source and decision log:
`docs/analysis/skolenaervaer-reference.md`. Bracketed references such as `[MO-FR-006]` or
`[DEC-43]` point to that document. MU behaviour (client retries, dead-lettering, receipt
handling, receipt schema) was verified against the Meldingsutveksler repository at commit
`513c016` on 2026-10-07; the reference's §6 was updated to match ([A2]).

## Context

Meldingsutveksleren (MU) is Bufdir's message exchange between Altinn / Fiks I/O and source
systems, run by a separate team. For each inbound message it puts a reference on a queue; the
content sits encrypted in MU's storage, readable only with a key pair shared with the receiving
system. MU does not validate the payload of Altinn messages today; the MU team has committed to
XSD-validating them before go-live (Dependencies). It cannot hand out a message a second time. The receiver consumes its queue through MU's client library, which runs inside
the receiver and does the retrying and dead-lettering on the receiver's side. A receipt is an
ordinary MU message sent back the same way. When MU gets an application receipt it marks the
original message as fetched, which stops its one-day unacknowledged-message alert, and deletes
the content; for Altinn messages the deletion does not work today (known MU defect). [§6]

MeldingsutvekslerReceiver is M2LB's single entry point from MU (DEC-22). It turns each message
into a validated, mapped request to the bounded context that owns the message type, and
completes the message with MU only when that context has given a definite answer. It holds no
business data and decides no business outcome. The first and only message type is skolenærvær
v2.0.1 (replaced v2.0.0 on 2026-10-14, DEC-126), owned by SkoleService (`SkoleService/specs/001-skoleservice`).

Skolenærvær reports come from private institutions (the primary senders, usually as
organisations) and foster homes (as persons) through Altinn. This feature is built for the
Altinn channel, and Fiks I/O receipt routing is out of scope; a message that arrives through
Fiks I/O is still handled exactly like an Altinn one (FR-014a). [DEC-52, DEC-57]

The "users" of this feature are the platform and its operators: reporters never see this
component, and SkoleService is its only consumer today.

## Clarifications

### Session 2026-10-07

- Q: What should the intake do while Skoletjenesten is down: keep taking messages and let MU's redelivery limit run out, or stop taking messages until Skoletjenesten is back? → A: Pause consumption. After repeated temporary failures the intake stops pulling new messages, releases in-flight ones, probes the owning service with capped backoff, resumes on its own, and alerts after a configurable outage time (default 15 minutes). Same pattern as SkoleAdapter (DEC-88).
- Q: Where should the intake keep its rejection records (message id, type, version, reason code, time), given that it is meant to be stateless and hold no data? → A: As structured outcome events in the platform's telemetry/log store: allow-listed fields only, never sampled or filtered, searchable by message id, kept for the platform log retention period. The intake has no database.
- Q: Should operators be alerted when skolenærvær messages are rejected at the intake (schema violation or unsupported version), and if so, when? → A: Threshold alert: operators are alerted when intake rejections for a message type and reason exceed a configurable count within a configurable time window; individual rejections are visible on the dashboard and as outcome events.
- Q: What should happen when the intake picks up a queue message but its encrypted content is no longer in MU storage? → A: Fixable failure: not completed, left for dead-lettering with a "content missing" alert; operators check the message id against Skoletjenesten before discarding it.
- Q: MU's receipt format requires a `Mottaker` (recipient) field, so a foster home's receipt must contain the foster parent's fødselsnummer. Should the spec allow the reporter's identity in that one required field? → A: Yes, as a narrow exception: `Mottaker` carries the reporter type and identity copied from the report's `Avsender` (name only if the schema requires it); `Resultat` and `Informasjon` are omitted; receipt content is never logged. FR-022, FR-023 and MP-05 are amended accordingly, with Data Protection Officer approval.
- Q: When M2LB's own decryption key can't be used (Key Vault unreachable, or the wrong key after a rotation), should the intake pause consumption the way it does when Skoletjenesten is down, instead of letting each message use up its retries and be dead-lettered? → A: Yes. Repeated key or decryption failures pause the whole queue using the FR-016a pattern (threshold, probing, resume on its own, alert after 15 minutes). A single message whose MU content lacks its encryption data (`BirkKeyMissing`, `IvMissing`) is a fixable failure, dead-lettered with an alert; the intake never lets the MU client complete it silently. This corrects FR-017b, which wrongly listed a missing key as an infrastructure error held for retry (MU code, commit `513c016`).
- Q: When the intake rejects a message whose sender can't be read, how should it fill in the receipt's required recipient? → A: The MU team has committed to XSD-validating Altinn payloads before go-live (MU code at `513c016` validates only Fiks I/O payloads), so malformed XML and a missing or invalid sender no longer reach the intake. `Mottaker` is copied from `Avsender`; if the sender still can't be read as a complete person or organisation, no receipt is sent, the message is still completed, and the missing receipt is logged and alerted like a send failure. The intake keeps its own schema validation as the fail-closed gate and for non-finite numbers, which the XSD allows.
- Q: If a skolenærvær message reaches the intake's queue through Fiks I/O instead of Altinn, should the intake handle it the same way, or refuse it? → A: Handle it the same way. A Fiks I/O message gets the same validation, mapping, routing and receipt policy as an Altinn one; the channel is recorded in outcome events and dashboard counts. DEC-57 keeps Fiks I/O receipt routing out of scope; it does not refuse Fiks I/O reports.
- Q: Should the intake report itself "not ready" when an owning service such as Skoletjenesten can't be reached, or only when its own connection to MU fails? → A: Readiness covers only the intake's own MU connection (MU queue and storage reachable). Owning-service and key outages are handled by the pause (FR-016a, FR-016b) and shown on the dashboard as paused or running per message type, not as not-ready.
- Q: What load should SC-005 (95% of messages reach an outcome within 5 minutes) be tested against, and do messages that wait out a pause or sit in the go-live backlog count towards it? → A: Test at DEC-72's load (10,000 per period, peak 2,000 per hour). Time a message spends waiting during a pause doesn't count. The go-live backlog has its own target: it is processed at no less than the peak rate (2,000 per hour).
- Q: Should each registered message type have its own MU channel, so that a pause for one owning service never stops the other message types, or should all M2LB message types share one channel and queue? → A: One MU channel per message type (its own inbound queue and blob containers, routed on the type's namespace prefix), all using M2LB's one key pair; the intake runs one listener per channel. An owning-service pause stops only the listeners for that service's message types; a key failure pauses every channel. Dead-lettered messages share MU's single dead-letter container and are re-driven to the queue recorded in each record (`SourceQueue`). Verified in MU code at `513c016`: FagKlient opens one listener per configured channel, keys are looked up per channel by secret name, and dead-letter records carry their source queue.
- Q: While the intake is paused, how should it check whether the owning service (or its decryption key) works again? → A: Probe without messages. For an owning-service pause, the intake calls the owning service's readiness signal with its own identity; for a key pause, it reloads the private key from Key Vault and checks it against M2LB's public key with a test round trip. On success it resumes; if failures continue, the threshold pauses it again. No waiting message uses a retry attempt for a probe.
- Q: Should every dead-lettered message raise its own alert, or should dead-letter alerts be grouped by message type and failure reason? → A: Grouped. The first dead-letter for a message type and failure reason alerts right away; further ones within a configurable window are combined into one follow-up alert with a count. Every dead-lettered message still gets an outcome event, searchable by message id and shown on the dashboard.
- Q: When the intake runs as several instances, should each instance count failures and pause on its own, or should one pause apply to all instances at once? → A: Only one instance at a time listens on each channel; the others stand by and take over if it stops. A pause is therefore held by the one active listener, and each message type's throughput is what one instance can process.
- Q: What default values should the intake ship with for its pause threshold, probe backoff cap, rejection-alert threshold and dead-letter follow-up window? → A: Pause after 5 consecutive failures; probe backoff capped at 5 minutes; rejection alert at 10 per message type and reason within 60 minutes; dead-letter follow-up window of 60 minutes. All are defaults only and configurable without a code change.
- Q: Skoletjenesten's readiness signal is now specified (Skole FR-046). Does it give the intake what its pause probe needs? (from the Skoletjenesten clarification session) → A: Yes. It reports whether Skoletjenesten's ingestion path, database included, can accept requests, is callable with the intake's own identity, and stays ready while Person is down, because reports are then held, not refused. The intake resumes when it reports ready; the dependency is closed in the Skoletjenesten spec (FR-016a). (auto-accepted recommendation)
- Q: The send time (`Meldingshode/SendtTidspunkt`, an `xs:dateTime`) may arrive with or without a time-zone offset. How should the intake forward it? → A: Exactly as received, offset included or absent, never converted or filled in, so Skoletjenesten alone decides the submission date. Skoletjenesten reads a value without an offset as Europe/Oslo local time (FR-012). (auto-accepted recommendation; confirmed in review 2026-10-08)
- Q: Should absence codes be forwarded exactly as the text received, or may the intake read them as numbers? → A: Exactly as the text received, never parsed as a number or normalised, because Skoletjenesten flags a zero-padded code such as `01` as unknown and delivers it as received (Skole FR-015b). A number conversion would turn `01` into `1` and hide it (FR-012). (auto-accepted recommendation)

### Session 2026-10-09

- Q: The published skolenærvær v2.0.0 XSD restricts absence codes to the enumeration 1, 2, 3, 4, 5, 9, so `01` and every unknown code fail schema validation at the intake, against the answer above and US1 scenario 7 (found by `/speckit-analyze`). Should the intake accept that, or register a schema without the enumeration? → A: Register a relaxed copy of the v2.0.0 XSD in which `TypeFravaerType` is plain `xs:string` and nothing else changes, so absence codes reach Skoletjenesten as text and it flags unknown ones (Skole FR-015b). A test guards that the copy differs from the published XSD only there (FR-007, constitution MP-02 v1.7.0, DEC-123). (v2.0.1 since 2026-10-14, same relaxation, DEC-126.)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Deliver a valid skolenærvær message to SkoleService (Priority: P1)

A private institution's or foster home's Altinn report arrives at MU. The intake picks it up, fetches and decrypts the
content, checks it against the skolenærvær v2.0.1 schema, maps it to SkoleService's domain
form, hands it over, and completes the MU message once SkoleService has answered. [US-A1,
US-A3]

**Why this priority**: Nothing reaches M2LB without it. The first delivery to Bufdata on
11 November 2026 depends on reports flowing from October.

**Independent Test**: Put a valid, encrypted skolenærvær v2.0.1 message on the queue with
SkoleService stubbed, and verify the mapped request, the completion of the MU message, the
receipt, and that no identifier appears in any log, trace or record.

**Acceptance Scenarios**:

1. **Given** a valid skolenærvær v2.0.1 message, **When** the intake processes it, **Then**
   SkoleService receives one request containing the child's identifier and its type, the
   reporter's type and identity, period dates, planned and actual hours, absence lines, message
   id, send time and message-chain information.
2. **Given** SkoleService answers that the request was received, **When** the answer
   arrives, **Then** a positive application receipt is sent, as the skolenærvær receipt policy
   requires, and the MU message is completed.
3. **Given** a message with contact information, a reporter name or a pseudonym identifier,
   **When** it is mapped, **Then** none of these is forwarded.
4. **Given** a message without a child identifier or with any message-chain value, **When** it
   is mapped, **Then** it is forwarded unchanged for SkoleService to judge; the intake applies
   no domain rule.
5. **Given** any processed message, **Then** no fødselsnummer, DUF-nummer, reporter identity or
   contact information appears in logs, traces, metrics or records, and no raw message content
   is stored.
6. **Given** a valid skolenærvær v2.0.1 message that arrived through Fiks I/O, **When** the
   intake processes it, **Then** it is validated, mapped, forwarded and receipted exactly like
   an Altinn message, and its outcome event records the channel as Fiks I/O.
7. **Given** a message with absence code `01` and a send time without a time-zone offset,
   **When** it is mapped, **Then** SkoleService receives the code as `01` and the send time
   without an offset, both exactly as received.

---

### User Story 2 - Reject structurally invalid messages without losing track of them (Priority: P1)

A message that does not match its schema, or uses an unsupported version, is rejected at the
intake so SkoleService only receives well-formed data. Every rejection is recorded; none is
dropped silently. [US-A2]

**Why this priority**: MU does not validate Altinn payloads today, and even once it does, this
stays the fail-closed structural gate before the domain: it alone knows which versions are
registered, and it alone rejects non-finite numbers where a registered XSD allows them (the
skolenærvær v2.0.1 XSD no longer does, DEC-126). It must exist from the first message.

**Independent Test**: Send schema-invalid messages and messages in an older skolenærvær
version, and verify that none reaches SkoleService, each is completed with a recorded
rejection reason, and a positive application receipt but no negative receipt is sent for
skolenærvær.

**Acceptance Scenarios**:

1. **Given** a skolenærvær message that violates the v2.0.1 schema, **When** it is processed,
   **Then** it is not forwarded, the MU message is completed, and a rejection is recorded with
   message id, message type, version and reason, without any payload content.
2. **Given** a skolenærvær message in a version other than v2.0.1 (for example v2.0.0 or v0.8.2),
   **When** it is processed, **Then** it is rejected as "unsupported version" and never
   validated against another version.
3. **Given** a rejected skolenærvær message, **Then** a positive application receipt is sent,
   confirming receipt, and no negative receipt is sent, because the receipt policy for
   skolenærvær has negative receipts off.
4. **Given** SkoleService rejects a request with a reason code registered for skolenærvær,
   **When** the answer arrives, **Then** the MU message is completed and the rejection is
   recorded with that reason code.
5. **Given** a skolenærvær message with an hour value that is negative, fractional, `NaN`, `INF`
   or `-INF`, **When** it is processed, **Then** it is rejected as "schema violation" and not
   forwarded; a non-negative integer of any size is forwarded unchanged (DEC-126).
6. **Given** intake rejections for skolenærvær with the same reason exceed the configured count
   within the configured time window, **Then** operators are alerted with message type, reason
   and count; **Given** fewer rejections than the threshold, **Then** no alert is raised and
   the rejections are visible only on the dashboard and as outcome events.

---

### User Story 3 - Never lose a message when something fails (Priority: P1)

When SkoleService is unavailable or M2LB's decryption key cannot be used, the intake pauses
and waits instead of using up its retry attempts. When a single message cannot be decrypted, or
a message arrives that the intake does not know, the MU message stays recoverable: it is
retried, and if it still cannot be processed it is dead-lettered (or, for infrastructure
errors, held for retry) and operators are alerted.
[US-A3, MP-04]

**Why this priority**: MU cannot hand out a message twice. Completing a message too early
loses a child's report for good.

**Independent Test**: Make SkoleService time out, return server errors, and be unreachable;
supply a message encrypted with the wrong key, one whose MU content lacks its encryption data,
one whose content is missing from MU storage, and one with an unregistered namespace; make
SkoleService answer with an authorisation error, a routing error, a conflict and a rejection
whose reason code is not registered; verify in each case that the MU message is not completed,
that it is retried, and that it ends dead-lettered with an alert. Take SkoleService down for
longer than the alert threshold with messages on the queue, and verify that the intake pauses,
alerts, does not dead-letter the waiting messages, and processes them all once SkoleService
is back. Do the same with M2LB's decryption key unavailable, and then replaced by a key that
does not match.

**Acceptance Scenarios**:

1. **Given** SkoleService times out or reports a temporary failure, **When** the intake gets
   that answer, **Then** the MU message is handed back for retry, not completed.
2. **Given** a single message keeps failing temporarily while other messages succeed, **When**
   its retry attempts are used up, **Then** it is dead-lettered and operators are alerted.
3. **Given** a single message that cannot be decrypted, or whose MU content lacks its
   encryption data, **When** it is processed, **Then** it is not completed (the MU client is
   never allowed to complete it silently) and not recorded as a content rejection; it is
   dead-lettered with an alert, so it can be re-driven once the cause is fixed.
4. **Given** a message whose namespace is not registered at all, **When** it is processed,
   **Then** it is not forwarded and not completed; it is left for dead-lettering and an alert
   is raised.
5. **Given** processing takes longer than usual, **Then** the MU message stays locked to the
   intake until processing has finished, so it is not handed to a second worker mid-flight.
6. **Given** the same message is delivered twice, **When** both copies are processed, **Then**
   both are forwarded and SkoleService's idempotency on message id ensures a single effect.
7. **Given** SkoleService answers with an authentication, authorisation, routing or
   media-type error, or with a rejection whose reason code is not registered for the message
   type, **When** the answer arrives, **Then** the MU message is not completed; it is left for
   dead-lettering and an alert is raised.
8. **Given** SkoleService answers with a conflict or throttling response, **When** the answer
   arrives, **Then** the MU message is handed back for retry, not completed.
9. **Given** SkoleService is unreachable, times out, returns server errors or throttles for
   several consecutive messages, **When** the configured failure threshold is reached, **Then**
   the intake stops the skolenærvær listener, releases the messages in flight, and probes
   SkoleService's readiness signal with backoff up to a configured cap; no message is sent
   as a probe, and messages waiting on the queue use up no retry attempts.
10. **Given** the intake is paused, **When** the outage lasts longer than the configured alert
    time (default 15 minutes), **Then** operators are alerted; **When** SkoleService's
    readiness probe succeeds, **Then** the intake resumes on its own without operator action.
    Throughout the pause the intake reports ready, and the dashboard shows skolenærvær as
    paused.
11. **Given** a queue message whose content is no longer in MU storage, **When** it is
    processed, **Then** it is not completed, not forwarded and not recorded as a content
    rejection; it is left for dead-lettering and an alert with reason "content missing" is
    raised, carrying only the message id.
12. **Given** a dead-lettered message whose cause has been fixed, **When** an M2LB operator
    re-drives it, **Then** it is processed again from the queue like any other message,
    without help from the MU team.
13. **Given** M2LB's decryption key is unavailable or does not match, **When** decryption fails
    for the configured number of consecutive messages, **Then** the intake stops the listeners
    of all its channels, releases the messages in flight, alerts after the configured
    outage time, and resumes on its own once the key reloaded from Key Vault passes the test
    round trip; no message is decrypted as a probe, and messages waiting on the queue use up
    no retry attempts.
14. **Given** the intake has resumed after a successful probe, **When** requests or decryption
    still fail for the configured number of consecutive messages, **Then** it pauses again;
    each such cycle uses at most one retry attempt of those messages.
15. **Given** many skolenærvær messages are dead-lettered for the same reason within the
    configured window, **Then** operators get one alert for the first, with its message id,
    and one follow-up alert with the count; each message still has its own outcome event
    and appears in the dashboard's dead-letter count.

---

### User Story 4 - Receipts follow a per-message-type policy (Priority: P2)

Each message type decides whether positive and negative receipts are sent. For skolenærvær,
the positive application receipt is sent for every completed message, rejected ones included,
so MU can clean up and clear its alerts; it confirms receipt, not acceptance. Negative
receipts are not sent: foster parents have no return channel, and routing receipts to
organisations via Fiks I/O is out of scope for this Altinn-only feature. A failed receipt never
causes a message to be processed again. MU then tries to forward the receipt to the sender via
Fiks I/O; that fails for person senders and for organisations without a Fiks I/O address, but
MU has already marked the report as fetched by then. MU may answer the intake with a
confirmation or an error message about the receipt, which the intake records but never treats
as an unknown message type. [DEC-43, DEC-57, DEC-68, OQ-13]

**Why this priority**: Receipts matter for MU's housekeeping and alerting, but the report
reaches M2LB without them. The final behaviour depends on the MU team (OQ-13).

**Independent Test**: Process accepted and rejected skolenærvær messages with receipt sending
succeeding and failing (including MU storage and queue unavailable), and verify which receipts
are sent, what they contain, and that a receipt failure is alerted without reprocessing. Put
MU's forwarding confirmation and an MU error message about a receipt on the queue, and verify
both are completed, recorded and counted, with no alert and nothing forwarded.

**Acceptance Scenarios**:

1. **Given** a skolenærvær message SkoleService answers as received, **Then** one positive
   application receipt is sent.
2. **Given** a rejected skolenærvær message, **Then** one positive application receipt is sent
   and no negative receipt.
3. **Given** sending a receipt fails, for any reason including MU storage or queue being
   unavailable, **Then** the failure is logged and alerted, the MU message is still completed,
   and the message is not retried or forwarded again.
4. **Given** the receipt policy for a message type is changed in configuration, **Then** the
   new policy applies to messages processed afterwards without a code change.
5. **Given** a message left for dead-lettering, **Then** no receipt is sent, since the message
   is not completed.
6. **Given** MU sends its confirmation that a receipt was forwarded, or an error message
   (`Feilmelding`) about a receipt the intake sent, **When** it arrives on the intake's queue,
   **Then** it is completed and recorded as an outcome event with message id, referenced
   message id and, for an error, its error code; it is counted, raises no per-message alert,
   is not forwarded, and is not treated as an unregistered namespace.
7. **Given** a skolenærvær message from a foster home, **Then** its receipt's recipient carries
   the reporter type, fødselsnummer and name copied from the report's sender, and nothing else
   identifying; the receipt content appears in no log or trace.
8. **Given** a completed message whose sender cannot be read as a complete person or
   organisation, or has no name (optional in skolenærvær v2.0.1, required by MU's receipt
   schema, DEC-126), **Then** no receipt is sent, the message is still completed, and the missing
   receipt is logged and alerted like a send failure, carrying only the message id.

---

### User Story 5 - Add a message type without touching existing ones (Priority: P3)

A future message type (for example a new form) is added by registering its MU channel, schema,
mapping, owning service, receipt policy and permanent-rejection reason codes. Skolenærvær
handling is unaffected, also when the new type's owning service is down. [DEC-22, MP-06,
DEC-103]

**Why this priority**: Only skolenærvær exists today. The registry shape matters now so the
first type does not become a special case.

**Independent Test**: Register a test message type with its own MU channel, schema and target
alongside skolenærvær, and verify that each type's messages go only to their own target, that
taking the test type's target down pauses only the test type, and that the skolenærvær tests
are unchanged.

**Acceptance Scenarios**:

1. **Given** two registered message types, **When** messages of each arrive, **Then** each is
   validated against its own schema and routed only to its own owning service.
2. **Given** a new registration, **Then** no behaviour of existing message types changes.
3. **Given** two registered message types on their own MU channels, **When** one type's owning
   service is down long enough to pause it, **Then** only that type's listener stops; the other
   type's messages keep flowing and its dashboard status stays running.

---

### Edge Cases

- **Message for a period before October 2026** (left in the MU queue before go-live): it is
  forwarded like any other message; SkoleService rejects it as outside the validity window.
  The MU team is asked to remove, before go-live, only messages whose reporting period ends
  before 1 October 2026; messages for October 2026 or later stay in the queue. [DEC-42, DEC-66]
- **Intake not yet running when October reports arrive** (reports may be sent before the period
  ends): messages wait in the MU queue, unacknowledged, and MU raises its alert, which the MU
  team has been told to expect. None is lost, provided the queue's time-to-live is longer than
  the wait (OQ-08). Once the intake starts, it works through the backlog at no less than the
  peak rate (SC-005a). [DEC-65, DEC-66]
- **Schema-valid message without a child identifier**: forwarded; SkoleService rejects it.
  [DEC-44]
- **Message-chain value is a receipt or reply** (`ApplikasjonsKvittering`, `Svar`, …):
  forwarded; SkoleService rejects it as unexpected. [§9.6, DEC-44]
- **SkoleService unavailable**: the intake pauses and alerts after the configured outage time
  (FR-016a); messages wait in the MU queue without using retry attempts. If the outage
  lasts more than a day, MU also raises its unacknowledged-message alert, and none is lost
  provided the queue's time-to-live is longer than the outage (OQ-08). Accepted operational
  risk. An outage near the lock is handled by postponing the lock in SkoleService (DEC-69).
- **One message fails temporarily while others succeed** (for example a request that
  repeatedly triggers a server error): the intake does not pause for it; the message uses up
  its retry attempts (today about 16 minutes) and is dead-lettered with an alert.
- **MU storage or MU queue unavailable**: the MU client treats these as infrastructure errors
  and keeps retrying the message every few minutes, raising a critical alert each cycle, until
  the problem is fixed; such messages are never dead-lettered or completed (FR-017b).
- **M2LB's decryption key unavailable or wrong** (key store outage, a rotation that went
  wrong): every message fails to decrypt. The MU client has no retry policy for this (it falls
  to the client's default error handling), so without a pause every message would end up
  dead-lettered. The intake pauses all its channels after the configured number of
  consecutive failures, alerts, and resumes on its own (FR-016b).
- **Probe passes but processing still fails** (SkoleService reports ready while its ingestion
  fails, or MU encrypts with an outdated M2LB public key, which the key round trip cannot
  detect): the intake resumes, the threshold is reached again, and it pauses again. Each cycle
  costs the first messages one retry attempt each; the outage alert keeps running, and a
  message that reaches its attempt limit is dead-lettered and can be re-driven (FR-016a,
  FR-016b, FR-017a).
- **Encryption data missing from a message's MU content** (`BirkKeyMissing`, `IvMissing`): the
  MU client raises this as a sender error, which it completes silently. The intake catches it
  and leaves the message for dead-lettering with an alert (FR-017).
- **MU cannot forward a receipt** (person sender, or organisation without a Fiks I/O address):
  the receipt has already cleared MU's alert for the report; MU may send an error message back,
  which the intake records and counts (FR-021a). [OQ-13]
- **Reporter is an organisation rather than a person**: the orgnr is forwarded as the reporter
  identity with type "organisation".
- **Message arrives through Fiks I/O** (MU routes by namespace, not channel): handled exactly
  like an Altinn message (FR-014a). MU can forward the positive receipt to an organisation
  with a Fiks I/O address; negative receipts stay off.
- **Decrypted payload is not well-formed XML**: treated as a schema violation (content
  rejection), since decryption succeeded. Once MU validates Altinn payloads this should not
  happen; the intake still handles it fail-closed.
- **Sender cannot be read for the receipt** (only possible if MU's validation is bypassed or
  regresses): the message is completed with its outcome, no receipt is sent, and the missing
  receipt is alerted (FR-022); MU's one-day unacknowledged-message alert then fires for it.
- **Very large message or burst on the 10th** (up to 2,000 per hour, DEC-72): processing keeps
  each message locked for its full duration; with one active listener per channel (FR-001),
  throughput scales by processing more messages at once within that instance, never by
  completing early.
- **Active listener stops** (crash, deployment, node loss): a standby instance takes over the
  channel. Messages the stopped instance had locked return to the queue when their lock
  expires and are retried, each using one attempt. If a pause was in force, the new listener
  starts unpaused and pauses again at the threshold; its outage alert time counts from that
  new pause (FR-001a).
- **MU deletes nothing for Altinn messages** (known MU defect: deletion looks under the MU
  message id, but Altinn content is stored under Altinn's data id): not this module's concern;
  the module never deletes MU content itself. [§6]
- **Content no longer in MU storage** (for example a message redelivered after its receipt let
  MU delete the content, once the defect above is fixed, or content lost on MU's side): the
  intake cannot tell an already-delivered report from a lost one, so it never completes the
  message. It is left for dead-lettering with a "content missing" alert (FR-017); operators
  check the message id against SkoleService's idempotency record before discarding it.
- **Owning service starts using a new reason code before it is registered**: messages with that
  code are dead-lettered with an alert until the code is added to the registration; none is
  completed or lost. [DEC-64]

## Requirements *(mandatory)*

### Functional Requirements

**Consumption and decryption**

- **FR-001**: Each registered message type MUST have its own dedicated MU channel, routed on
  the type's namespace prefix, with its own inbound queue. Exactly one intake instance at a time
  MUST listen on each channel; other instances stand by and MUST take over when the active
  listener stops. The listener MUST consume in a mode where a message is locked during
  processing and removed only when explicitly completed; a mode that removes messages on
  receipt MUST NOT be used. [MO-FR-001, DEC-103, DEC-106]
- **FR-001a**: Failure counts and pause state (FR-016a, FR-016b) are held in memory by the
  active listener only and never shared between instances. How the active listener is chosen
  is decided in the plan. An instance that takes over starts
  unpaused; if the outage continues it pauses again at the threshold. [DEC-106]
- **FR-002**: The intake MUST fetch the encrypted content from MU storage and decrypt it using
  MU's client capability, through a boundary owned by the intake, with the MU client at a pinned
  version. [MO-FR-002, MP-08]
- **FR-003**: Decryption MUST use a key pair dedicated to M2LB, shared by all of M2LB's MU
  channels and by no other system. [MO-FR-003, MP-07, OQ-08, DEC-103]
- **FR-004**: A message MUST stay locked to the intake for the full duration of fetch,
  decryption, validation and the call to the owning service. [MO-FR-004]
- **FR-005**: The intake MUST NOT delete MU content. [MO-FR-015]

**Registry and validation**

- **FR-006**: The intake MUST accept only message types registered with: namespace and version,
  MU channel, schema, mapping, owning service, receipt policy and the owning service's
  permanent-rejection reason codes. The reason codes are configuration, taken from the owning service's published
  contract. Skolenærvær v2.0.1 is the only registration. [MP-06, DEC-08, DEC-64, DEC-126]
- **FR-007**: Each message MUST be validated only against the schema registered for its message
  type and version. The registered schema is the published XSD, or a documented M2LB copy of it
  that lifts only a code-list enumeration the owning service judges. For skolenærvær v2.0.1 the
  `TypeFravaer` enumeration is lifted, so any absence code passes as text (FR-012). [MO-FR-006,
  DEC-123, DEC-126]
- **FR-008**: A message of a registered type in an unsupported version MUST be rejected as
  "unsupported version" and never validated against another version. [MO-FR-006, DEC-08]
- **FR-009**: A message of a registered type that fails schema validation, or whose decrypted
  content is not well-formed, MUST be rejected as "schema violation". A schema-valid number that
  is not finite (`NaN`, `INF`, `-INF`, which `xs:double` allows) MUST also be rejected as
  "schema violation". Since skolenærvær v2.0.1 the hour elements are `xs:nonNegativeInteger`,
  so a negative, fractional or non-finite hour value already fails schema validation.
  [MO-FR-007, DEC-67, DEC-126]
- **FR-010**: Adding a registration MUST NOT change the handling of existing message types.
  [MO-FR-006a]
- **FR-011**: The intake MUST NOT apply domain rules or filter by period, child identifier,
  message-chain value or any other payload value. Rejecting a number that is not finite
  (FR-009) is a format check, not a domain rule. [MP-02, DEC-42, DEC-44, DEC-67]

**Mapping and routing**

- **FR-012**: A skolenærvær message MUST be mapped to SkoleService's domain form: child
  identifier and its type (fødselsnummer or DUF-nummer), reporter type and identity
  (fødselsnummer or orgnr), period start and end, planned hours, actual hours, absence lines
  (code, hours), message id, send time, and message-chain information (link type, start message
  id, referenced message id). The intake MUST NOT resolve identity. Hour values MUST be
  forwarded as received, as JSON numbers that can hold any value the schema allows (for v2.0.1
  any `xs:nonNegativeInteger`), so range checks happen in SkoleService; a schema-valid value
  MUST NOT cause a mapping fault. The period dates (`Periode/StartDato`, `SluttDato`, `xs:date`)
  MUST be forwarded as the text received, in `periode.startDato` and `periode.sluttDato`. The send time
  (`Meldingshode/SendtTidspunkt`) MUST be forwarded exactly as received, with its time-zone
  offset if it has one and without one if it has none; the intake MUST NOT convert it or add
  an offset, since the submission date is SkoleService's decision. Absence codes MUST be
  forwarded exactly as the text received, never parsed as a number or normalised, so a
  zero-padded code such as `01` reaches SkoleService unchanged (Skole FR-015b). [MO-FR-008,
  DEC-67, DEC-126]
- **FR-013**: Contact information, the reporter's name and any pseudonym identifier MUST be
  dropped and never forwarded to the owning service. The reporter's name is kept in memory only
  until the receipt is sent, because MU's receipt schema requires it (FR-022). [MO-FR-009,
  DEC-09]
- **FR-014**: Each message MUST be routed only to the owning service named in its
  registration. [MO-FR-006a]
- **FR-014a**: The intake MUST handle a message the same way whichever MU channel it arrived
  through (Altinn or Fiks I/O): same validation, mapping, routing and receipt policy. The
  channel, read from the queue message, MUST be recorded in the message's outcome event and in
  the counts operators see (FR-028). [DEC-57, DEC-100]

**Outcomes and acknowledgement**

- **FR-015**: When the owning service returns a definite outcome (received or rejected), the
  intake MUST complete the MU message. The intake MUST NOT expect, infer or record whether a
  received report was accepted or held, or anything about the child's classification.
  [MO-FR-010, MO-FR-012, DEC-60]
- **FR-015a**: Only a domain rejection counts as a permanent rejection: a response from the
  owning service that carries a reason code registered for the message type (FR-006). A
  rejection with an unregistered reason code, and authentication, authorisation, routing and
  media-type errors, MUST be treated as failures M2LB could fix (FR-017). Conflict, throttling
  and timeout responses MUST be treated as temporary failures (FR-016). The mapping from kind of
  response to outcome is fixed behaviour, not configuration; only the list of reason codes is
  configured. [MO-FR-012, DEC-64]
- **FR-016**: When the owning service times out, is unreachable, reports a temporary failure, a
  conflict or throttling, the intake MUST hand the message back to the MU client for retry and
  MUST NOT complete it. (The MU client may replace it with a copy sent before the original is
  completed; that keeps the message and is not a completion, MP-04.) The MU client retries after configured waits (today 1 s, 10 s, 1 min,
  5 min and 10 min: six attempts over about 16 minutes); when the attempts are used up, the
  message is dead-lettered (FR-017a) and MUST be alerted (FR-028b). The waits and attempt count
  are the intake's own configuration of the MU client. [MO-FR-011]
- **FR-016a**: When an owning service is unreachable, times out, returns server errors or
  throttles for a configurable number of consecutive messages (default 5), the intake MUST stop
  the listeners of that service's message types and release the messages in flight, so waiting
  messages do not use up their retry attempts. Listeners of other message types MUST keep
  running. While paused it MUST probe the owning service's readiness signal, with the intake's
  own identity and backoff capped at a configurable interval (default 5 minutes), never by
  sending a message; it MUST resume on its own when the probe succeeds, and MUST alert
  operators when the outage exceeds a configurable time (default 15 minutes). If failures
  continue after resuming, the threshold pauses it again. A conflict response affects only its
  own message and does not count towards the pause. For skolenærvær the probe calls
  SkoleService's readiness signal (Skole FR-046), which stays ready while Person is
  unavailable, so a Person outage never pauses the intake. [DEC-91, DEC-104, DEC-107; DEC-88
  pattern]
- **FR-016b**: When decryption fails for a configurable number of consecutive messages
  (default 5; M2LB's private key unavailable or not matching), the intake MUST stop the
  listeners of all its channels, since they share the key pair, and release the messages in
  flight, so waiting messages do not use up their retry attempts. While paused it MUST probe,
  with backoff capped at a configurable interval (default 5 minutes) and never by decrypting a
  message, by reloading the private key from Key Vault and checking it against M2LB's public
  key with a test round trip; it MUST resume on its own when the check succeeds, and MUST alert
  operators when the outage exceeds a configurable time (default 15 minutes). If decryption
  still fails after resuming (for example MU encrypting with an outdated public key, which the
  round trip cannot detect), the threshold pauses it again. A single message that fails to
  decrypt while others succeed does not count towards the pause; it is a fixable failure
  (FR-017). [DEC-98, DEC-104, DEC-107; DEC-91 pattern]
- **FR-017**: A failure M2LB could fix — failed decryption of a single message, encryption
  data (wrapped key or IV) missing from the message's MU content, content missing from MU
  storage, an unregistered namespace (other than MU's own messages, FR-021a), a mapping fault, or an
  owning-service response that is not a registered domain rejection (FR-015a) — MUST NOT
  complete the message; it MUST be dead-lettered once its retry attempts are used up (or held
  for retry, FR-017b) and MUST be alerted (FR-028b), so the message stays recoverable. [MP-04, MP-06,
  DEC-64, DEC-94, DEC-98]
- **FR-017a**: Dead-lettering is done by the MU client inside the intake: the queue message (a
  reference to MU storage, never the content) and failure metadata are written to a
  dead-letter container and removed from the queue, while the content stays in MU storage.
  Dead-letter metadata, exception messages included, MUST NOT contain identifiers or payload
  content. The dead-letter container is shared by all of the intake's channels; each record
  carries the queue it came from. M2LB operators MUST be able to re-drive a dead-lettered
  message to that queue without help from the MU team. [DEC-97, DEC-103]
- **FR-017b**: Failures the MU client classifies as infrastructure errors (MU storage or queue
  unavailable) are never dead-lettered: the client keeps the message and starts a new retry
  cycle after a fixed delay (today 5 minutes), raising a critical alert each time a cycle is
  used up. The intake MUST NOT turn such a failure into a completion or a content rejection; replacing the
  message with a copy sent first is not a completion (MP-04).
  The MU client lists missing encryption data (`BirkKeyMissing`, `IvMissing`) as an
  infrastructure error but raises it as a sender error, which it completes silently; the intake
  MUST catch it and handle it as a fixable failure (FR-017), never letting the MU client
  complete the message. [DEC-97, DEC-98]
- **FR-018**: Every content rejection (schema violation, unsupported version, domain rejection
  by the owning service with a registered reason code) MUST be completed and recorded with message id, message
  type, version, reason code and time — never payload content or identifiers. [MO-FR-012,
  MO-FR-013]
- **FR-019**: No message may end without a recorded outcome: completed with a received or
  rejected result, or dead-lettered with an alert. [MO-FR-013]
- **FR-019a**: Outcomes, rejection records included, MUST be recorded as structured outcome
  events in the platform's telemetry store, with allow-listed fields only. Outcome events MUST
  NOT be sampled or filtered, MUST be searchable by message id, and are kept for the platform's
  log retention period. The intake MUST NOT have a database of its own. [MP-03, PS-09, DEC-92]

**Receipts**

- **FR-020**: Receipts MUST follow the message type's receipt policy. For skolenærvær: a
  positive application receipt for every completed message, including content rejections,
  since the receipt confirms receipt, not acceptance; no negative receipts. A message that is
  not completed gets no receipt. [DEC-43, DEC-68, MO-FR-010]
- **FR-021**: The receipt is an ordinary MU message (an XML document in MU storage plus a queue
  reference), sent through the MU client while the intake is still processing the original
  message and routed with that message's namespace. A failure to send it, for any reason, MUST
  be caught by the intake, logged and alerted; the original message MUST still be completed and
  MUST NOT be retried, processed or forwarded again. A send failure MUST NOT reach the MU
  client's retry handling, which retries infrastructure errors indefinitely (FR-017b). [DEC-43, DEC-96]
- **FR-021a**: The intake MUST handle MU's own messages on its queues: MU's confirmation that a
  receipt was forwarded, and MU error messages (`Feilmelding`) about a receipt the intake sent.
  Each is completed and recorded as an outcome event with message id, referenced message id
  and, for an error, its error code; it is counted per message type and error code, raises no
  per-message alert, is never forwarded to an owning service, and its content is never logged.
  [OQ-13, DEC-97]
- **FR-022**: A positive application receipt MUST carry only the message id and the fields MU's
  receipt format requires; never payload content, the child's identifier or the outcome
  (`Resultat` and `Informasjon` are optional in the schema and are omitted). The required
  recipient (`Mottaker`) MUST carry the reporter type, identity and name copied from the
  report's sender (`Avsender`); MU's receipt schema requires the name for both persons and
  organisations. This is the only place a receipt may hold an identifier: it goes back only to
  MU, the system it came from, and MU routes on it. If the sender cannot be read as a complete
  person or organisation, or has no name (optional since skolenærvær v2.0.1, DEC-126), no
  receipt is sent until MU's receipt schema makes the name optional; the message is still completed and the missing
  receipt is logged and alerted like a send failure (FR-021). A negative
  receipt, where a receipt policy enables it, MUST carry only message id and reason code, plus
  the fields MU's receipt format requires under the same rule. [MP-05, DEC-68, DEC-95, DEC-99,
  DEC-126, OQ-13]
- **FR-022a**: Receipt content MUST NEVER be logged or traced; telemetry about a receipt carries
  only the message id and whether sending succeeded. [MP-05]

**Personal data and storage**

- **FR-023**: Fødselsnummer, DUF-nummer, reporter identity and contact information MUST NEVER
  appear in logs, traces, metrics, exception messages, rejection records, alerts or receipts,
  with one exception: the reporter identity and name in a receipt's required recipient field
  (FR-022). [MO-FR-014, MP-05]
- **FR-024**: The intake MUST NOT store raw message content anywhere; the MU message id is the
  only traceability key. [MO-FR-016, DEC-41]
- **FR-025**: The child's identifier and reporter identity MUST be passed to the owning service
  only over an encrypted connection, as part of the request. [MP-05]

**Operations**

- **FR-026**: The intake MUST carry the MU message id as correlation id through to the owning
  service and in every log entry for the message. [PS-08]
- **FR-027**: The intake MUST expose separate liveness and readiness health signals. Readiness
  reflects only whether it can reach the MU queue and MU storage. An unreachable owning service
  or an unusable decryption key MUST NOT make the intake not ready; those are handled by the
  pause (FR-016a, FR-016b). [GL-35, DEC-101]
- **FR-028**: Operators MUST be able to see, per message type and channel, counts of received,
  rejected (by reason), retried and dead-lettered messages, without any personal data, and,
  per message type, whether consumption is running or paused (FR-016a, FR-016b).
- **FR-028a**: The intake MUST alert operators when its own rejections (schema violation,
  unsupported version) for a message type and reason exceed a configurable count (default 10)
  within a configurable time window (default 60 minutes). A single rejection below the
  threshold raises no alert. The alert carries only message type, reason, count and window,
  never message ids or payload content. [DEC-93, DEC-107]
- **FR-028b**: Dead-letter alerts (FR-016, FR-017), "content missing" included, MUST be grouped
  per message type and failure reason. The first dead-letter for a type and reason MUST alert
  right away, with message type, reason and message id. Further dead-letters for the same type
  and reason within a configurable window (default 60 minutes) MUST be combined into one
  follow-up alert with message type, reason, count and window. No dead-letter may pass without
  an alert, and every dead-lettered message MUST still have its own outcome event (FR-019a).
  [DEC-105, DEC-107]
- **FR-028c**: Every threshold, interval and window in FR-016a, FR-016b, FR-028a and FR-028b,
  and the outage alert time, MUST be configuration that operators can change without a code
  change; the values given there are defaults only. [DEC-107]

### Key Entities

- **Meldingstyperegistrering**: Registration of one message type and version: namespace,
  version, MU channel, schema, mapping, owning service, receipt policy, permanent-rejection
  reason codes.
- **MU-kanal**: One MU channel per message type: the namespace prefix it routes on, its inbound
  queue and blob container, its outbound queue and container for receipts, and M2LB's key pair
  (shared by all of M2LB's channels). Provisioned by the MU team.
- **Mottatt melding** (transient): One MU message during processing: message id, message type,
  version, channel (Altinn or Fiks I/O), send time, decrypted content (in memory only).
- **Domeneforespørsel** (transient): The mapped request sent to the owning service.
- **Utfall**: The outcome of one message: received, rejected (with reason), handed back for
  retry, held for retry after an infrastructure error, or dead-lettered.
- **Avvisningspost**: Record of a content rejection: message id, message type, version, reason
  code, time. No payload, no identifiers. Kept as an unsampled outcome event in the platform's
  telemetry store, not in a database owned by the intake (FR-019a).
- **Kvitteringspolicy**: Per message type: positive receipts on/off, negative receipts on/off.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 100% of MU messages end in exactly one recorded outcome (received, rejected, or
  dead-lettered with alert); zero messages are completed before that
  outcome is known.
- **SC-002**: Zero fødselsnummer, DUF-nummer, reporter identity or contact information values
  exist in any log, trace, metric, record, alert or receipt produced by the intake, apart from
  the reporter identity and name in a receipt's required recipient field (FR-022).
- **SC-003**: Zero raw message content is stored by M2LB.
- **SC-004**: Zero schema-invalid or unsupported-version skolenærvær messages reach
  SkoleService.
- **SC-005**: Under a load test of 10,000 messages per period with a peak of 2,000 per hour
  (DEC-72, DEC-102), with one active listener per channel (FR-001), 95% of messages reach a
  definite outcome within 5 minutes of becoming available on the queue. Time a message spends waiting while the intake is paused (FR-016a, FR-016b) is
  not counted.
- **SC-005a**: Messages that queued up before the intake first runs (the go-live backlog) are
  processed at no less than 2,000 per hour once it starts, with none lost.
- **SC-006**: After SkoleService or the decryption key has been unavailable or broken and is
  restored, 100% of affected messages can be processed to a definite outcome without help from
  the MU team: messages that waited during a pause (FR-016a, FR-016b) or were held for retry
  resume on their own, and dead-lettered messages are re-driven by M2LB operators (FR-017a).
- **SC-007**: A second message type can be registered and processed with no change to the
  behaviour or tests of skolenærvær.

## Assumptions

- Volume is up to 10,000 skolenærvær messages per period, corrections included, concentrated in
  the first ten days of each month, with a peak of 2,000 per hour before the Altinn deadline
  around the 10th (DEC-72). The figure is an estimate and is adjusted if BiRK figures differ.
- The MU client behaves as found in the MU code (commit `513c016`): a failed message is retried
  after configurable waits (today six attempts over about 16 minutes) and then dead-lettered
  to a blob container, except infrastructure errors, which are retried every 5 minutes
  indefinitely, and sender errors, which it completes silently. A failure to load or use the
  receiver's private key is an ordinary error, not an infrastructure error. MU's unacknowledged-message alert fires after one day without a receipt. QA and
  production configuration is not in the repository and is confirmed with the MU team.
- SkoleService answers with distinguishable outcomes: received (accepted and held reports
  alike, deliberately not distinguished, DEC-60), permanent rejection with a reason code from
  its published contract, or temporary failure. SkoleService processes reports for Kode 6/7
  children like any other and enforces gradering on access (DEC-74); the intake handles every
  message the same way whatever the child's classification.
- From go-live, MU XSD-validates Altinn payloads before queueing them, so the intake receives
  only payloads that are valid against the XSD of their own namespace and version. The intake
  still validates against its registered schema (FR-007..FR-009). If MU validates against the
  published XSD, MU itself stops `01` and unknown absence codes before the intake, so US1
  scenario 7 then only arises if MU uses the relaxed schema too (DEC-123). The MU queue message carries
  only the storage path, message type namespace and channel; everything else, the sender
  included, is in the encrypted payload.
- The intake runs as its own deployable component. Each channel has one active listener at a
  time, with other instances on standby (FR-001); one instance can process DEC-72's peak
  (about 0.6 messages per second) by handling several messages at once.
- Alerts go to the M2LB operations channel defined by the platform's observability policy.

## Dependencies & Open Items

| ID | Dependency | Owner | Blocks |
|----|------------|-------|--------|
| OQ-14 / DEP-03 | A dedicated MU channel per M2LB message type, starting with skolenærvær on prefix `https://bufdir.no/barnevern.tiltak.skolenaervaer` (every version shares it): its own inbound and outbound queue and containers, M2LB's key pair (FR-001, FR-003). In MU's development configuration skolenærvær lands in "Tiltak Admin" (`https://bufdir.no/barnevern.tiltak`), together with every other `barnevern.tiltak` type; QA and production configuration is held in App Service settings, not in the MU repository, so which system reads "Tiltak Admin" there is unconfirmed. Verified at `513c016`: MU's resolver picks the most specific prefix, but its startup validation refuses prefixes that overlap across channels, so MU must relax that check or narrow "Tiltak Admin" to explicit types. MU routes each namespace to exactly one channel, so if BiRK still needs skolenærvær after go-live, MU cannot deliver it to both. | MU team | Go-live |
| OQ-08 / DEP-03 | Dedicated M2LB key pair, application-receipt registration, removal before go-live of only messages whose reporting period ends before 1 October 2026 (DEC-42), and a queue time-to-live long enough to hold messages until the intake runs. | MU team | Go-live |
| OQ-13 / DEP-03 | Receipts for Altinn messages. Verified in MU code: the receipt marks the report as fetched (clearing the unacknowledged-message alert) before MU tries to forward it via Fiks I/O; forwarding then fails for person senders (MU requires an organisation recipient) and for organisations without a Fiks I/O address. The receipt schema requires `Mottaker` with name (FR-022). The receipt policy (FR-020) is the interim behaviour; Fiks I/O routing to organisations stays out of scope (DEC-57). The MU team confirms what MU does after a failed forward (error message to the intake or MU's own dead-letter), and that the receipt schema in MU's test data is the one used in production. | MU team, architect | User Story 4 final behaviour |
| MU client support | Confirm or add in MU's client library: (a) the intake can pause each channel's listener on its own and set its own retry waits (FR-016a, FR-016b, FR-016); (b) M2LB operators can re-drive dead-lettered messages to the queue, with the access this needs (FR-017a); (c) MU routes its replies about receipts to the channel of the message type they concern (FR-021a). Verified at `513c016`: confirmations are routed by the original message's type, so they reach that type's channel; error messages (`Feilmelding`) are routed by their own namespace (`https://bufdir.no/feilmelding`), which no channel claims in MU's development configuration and at most one channel across all MU receivers could own, so today MU cannot deliver them to any receiver. MU needs to route them by the original type instead; until then the intake does not depend on receiving them (constitution MP-08). | MU team, M2LB | FR-016a, FR-016b, FR-017a, FR-021a |
| DEP-03 | MU XSD-validates Altinn payloads before queueing them, as it already does for Fiks I/O (code at `513c016` validates only Fiks I/O). Committed by the MU team 2026-10-07 (DEC-99); ticket not yet referenced. Until it ships, the intake's validation and the FR-022 fallback (no receipt, alert) cover malformed payloads. Open: whether MU validates `TypeFravaer` against the published enumeration or against the intake's relaxed schema (DEC-123); with the published one, `01` and unknown codes never reach SkoleService. | MU team, product owner | Go-live |
| OQ-15 / DEP-03 | Who answers MU's prefill requests for Altinn forms, and with what, now that pseudonym identifiers are not supported. Not part of this feature unless decided. | MU team, architect | Altinn form ordering |
| Skole EXC-03 | Approval for storing and delivering reporter identity; the intake forwards it. | Solution architect, data protection officer | FR-012 reporter mapping |
| Constitution amendment (MP-05) | MP-05 and Security & Compliance say receipts never carry identifiers and the reporter's name is dropped at the boundary; MU's receipt schema requires `Mottaker` with name, so the reporter identity and name go back to MU in that field (FR-013, FR-022). GL-23 and the Security & Compliance bullet on backoff also describe retries as MU redelivery, which FR-016, FR-017a and FR-017b correct. Amendment drafted as module constitution v1.4.0 (2026-10-07); v1.5.0 (same day) adds the key-failure pause, the receipt fallback, channel-independent handling and the readiness rule (DEC-98..DEC-101), and corrects v1.4.0's Fail-closed bullet, which listed a missing key as an infrastructure error held for retry. Both versions need approval by the solution architect, module owner and data protection officer. v1.6.0 (same day) covers the third clarification session (DEC-103..DEC-107): one MU channel per message type on M2LB's key pair, probing without messages, grouped dead-letter alerts, one active listener per channel, configurable defaults, and the MU routing findings (overlapping prefixes refused, `Feilmelding` unrouted); it corrects v1.5.0's "whole queue" for key failures. It does not touch personal data, so the solution architect and module owner approve it; all three versions can be approved together. v1.6.1 (2026-10-08, PATCH) records that send time and absence codes are forwarded as received (DEC-121) and that the SkoleService readiness signal is now specified (DEC-115); the solution architect and module owner approve it with the others. v1.7.0 (2026-10-09, MINOR) lets the registered XSD be an M2LB copy that lifts a code-list enumeration, used for `TypeFravaer` in skolenærvær v2.0.0 (DEC-123), and updates the inherited root version to v1.0.4; the solution architect and module owner approve it. | Solution architect, module owner, data protection officer | FR-001, FR-013, FR-022, FR-023 |
| Skole FR-045a | SkoleService publishes its rejection reason codes in the ingestion contract, so they can be registered for skolenærvær. | SkoleService | FR-006, FR-015a |
| SkoleService readiness | SkoleService exposes a readiness signal the intake can call with its own identity, reflecting whether its ingestion path (database included) can accept requests, and staying ready while Person is unavailable (DEC-104). Specified as Skole FR-046 (2026-10-07). | SkoleService | FR-016a |
| MU findings | Clear-text personnummer logging at form ordering, full XML logging in demo mode, clear-text prefill queues, blobs never deleted for Altinn (deletion looks under the wrong path), receipt forwarding crashes for person senders, missing encryption data (`BirkKeyMissing`, `IvMissing`) classed as infrastructure error but raised as a sender error, so FagKlient completes such messages silently. Reported to the MU team before production. | MU team | Production readiness (not this module) |

## Out of Scope

- Any domain rule for skolenærvær (identity, validity window, quality, corrections, lock,
  coverage, delivery) — SkoleService's feature.
- Archiving raw XML. [DEC-41]
- Answering MU prefill requests (OQ-15).
- Sending messages from M2LB to MU other than receipts.
- Fixing MU-side defects listed in §6 of the reference.
