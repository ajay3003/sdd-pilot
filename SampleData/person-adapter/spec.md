# Feature Specification: BiRK Person-adapter

**Feature Branch**: `001-birk-person-adapter`
**Created**: 2026-04-20
**Status**: Draft
**Input**: Based on `docs/BiRK-Person-adapter-—-Konstitusjon.md`, `docs/person-birk-adapter-func-requirements-no.md`, `docs/person-birk-adapter-test-spec-no.md`

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Continuous Person Data Synchronization (Priority: P1)

Operations staff and PersonModule consumers rely on person identity data from BiRK
being continuously available in M2LB. New persons and changes in BiRK appear in
PersonModule without manual intervention.

**Why this priority**: Person identity is the foundational record. Child
registrations, case assignments, and all other M2LB data depend on the person
existing in PersonModule first.

**Independent Test**: Create a new person in BiRK, wait for CDC processing, verify
the person appears in PersonModule with all identity fields correctly mapped. Then
update a field in BiRK and verify the update is reflected.

**Acceptance Scenarios**:

1. **Given** a new person record is created in BiRK, **When** the CDC event is
   processed, **Then** the person exists in PersonModule with identity fields
   (name, national ID, date of birth, DUF number) correctly mapped from BiRK values.
2. **Given** an existing person's data is updated in BiRK, **When** the CDC event
   is processed, **Then** PersonModule reflects the updated data.
3. **Given** a CDC event for an organizational entity (owner, unit, institution,
   employee, or contact person), **When** the adapter processes the stream, **Then**
   the event is silently discarded — no delivery to PersonModule, no error logged,
   no alert raised.
4. **Given** a person with missing identity data (unborn child, unaccompanied minor),
   **When** the CDC event is processed, **Then** the record is accepted with null
   values for absent fields — no error occurs.

---

### User Story 2 — Child Registration Synchronization (Priority: P1)

Child registration data from BiRK is available in M2LB, enabling case workers and
the system to act on the correct child record, registration type, and status.

**Why this priority**: Child registrations are the primary domain object M2LB tracks.
Without current registration data, no case workflows can function correctly.

**Independent Test**: Create a child registration in BiRK, verify it appears in
PersonModule with correct BirkID, type, status, and municipality. Verify a composite
status value is preserved unchanged.

**Acceptance Scenarios**:

1. **Given** a child registration is created in BiRK, **When** the CDC event is
   processed, **Then** the registration exists in PersonModule with BirkID,
   registration type, status, and municipality correctly mapped.
2. **Given** a child registration has a composite status value (e.g.,
   "Bestilling/Under Behandling"), **When** processed, **Then** the value is
   preserved in PersonModule unchanged — no splitting or transformation.
3. **Given** a municipality assignment changes for a child, **When** the
   corresponding CDC event is processed, **Then** PersonModule reflects the updated
   municipality.

---

### User Story 3 — Security Classification Enforcement (Priority: P1)

Children with Kode 6 or Kode 7 security classification are never exposed through
the adapter. This protects the most vulnerable children from accidental data
exposure in M2LB.

**Why this priority**: This is an absolute safety requirement. A single Kode 6/7
record reaching PersonModule is a security incident with legal consequences.

**Independent Test**: Submit a CDC record with security level 2. Verify no API call
is made to PersonModule, a critical security log entry is written, an alert fires,
and stream processing continues for subsequent records. Verify the Kode 6/7 counter
increments. Under normal operation, verify the counter is zero.

**Acceptance Scenarios**:

1. **Given** a CDC record with security level 2 (Kode 6), **When** the adapter
   processes it, **Then**: (a) no data is forwarded to PersonModule, (b) the event
   is logged as a critical security incident with the BiRK ID and timestamp,
   (c) an immediate operational alert is triggered that requires manual
   acknowledgment and does not auto-resolve, (d) stream processing advances past
   the record so subsequent records are not blocked.
2. **Given** a CDC record with security level 3 (Kode 7), **When** the adapter
   processes it, **Then** the outcome is identical to scenario 1.
3. **Given** CDC records with security level 0 (no protection) or level 1 (hidden
   address), **When** processed, **Then** records are delivered to PersonModule
   normally — no alerts, no Kode 6/7 counter increment.
4. **Given** normal operation with no Kode 6/7 records, **When** the adapter runs,
   **Then** the Kode 6/7 rejection counter remains zero throughout.

---

### User Story 4 — Initial Full Load (Priority: P1)

When the adapter starts for the first time, all existing BiRK person and child data
is loaded into PersonModule so M2LB starts with a complete, current dataset.

**Why this priority**: Without initial full load, M2LB would only know about changes
that occur after the adapter starts, leaving pre-existing data invisible.

**Independent Test**: Start the adapter fresh against a PersonModule with no data.
Verify all BiRK persons and child registrations appear in PersonModule. Verify
persons were loaded before child registrations. Verify the operation is idempotent
— running it again produces no duplicates or errors.

**Acceptance Scenarios**:

1. **Given** the adapter starts for the first time, **When** full load completes,
   **Then** all BiRK persons and child registrations exist in PersonModule.
2. **Given** a full load is executing, **When** the load sequence runs, **Then** all
   person records are fully ingested before the first child registration is
   submitted — the ordering constraint is never violated.
3. **Given** a large dataset during full load, **When** processing runs, **Then**
   batch delivery is used for bulk records and progress is logged at a configurable
   interval so operations staff can monitor it.
4. **Given** the initial full load is run a second time, **When** it completes,
   **Then** PersonModule contains no duplicates and the outcome is identical to the
   first run.

---

### User Story 5 — Fault Tolerance and Operational Recovery (Priority: P2)

The adapter handles transient failures without losing data, and operations staff
can monitor, understand, and resolve persistent failures.

**Why this priority**: Data loss in a child welfare system has direct legal and
operational consequences. Every change event MUST be accountable.

**Independent Test**: Simulate PersonModule unavailability. Verify records are
queued, not dropped. Restore availability. Verify automatic re-delivery. Verify
fault queue empties and alert auto-resolves. Restart adapter after planned downtime
and verify it resumes from last known position without re-processing old records.

**Acceptance Scenarios**:

1. **Given** PersonModule returns transient errors (5xx, timeout), **When** the
   adapter encounters failures, **Then** it retries the delivery with increasing
   wait times between attempts.
2. **Given** a delivery fails after all retry attempts are exhausted, **When** the
   limit is reached, **Then**: (a) the record is saved to the fault queue with
   enough data for re-delivery, (b) stream processing advances so other records
   are not blocked, (c) an operational alert is raised.
3. **Given** a validation error (rejected data) from PersonModule, **When** the
   adapter receives it, **Then** the record goes directly to the fault queue without
   retrying — validation errors are never retried.
4. **Given** records in the fault queue and PersonModule becomes available again,
   **When** the background re-processor runs, **Then** records are automatically
   re-delivered, removed from the fault queue on success, and the alert
   auto-resolves when the queue is empty.
5. **Given** the adapter restarts after planned downtime (deploy, maintenance),
   **When** it starts, **Then** it resumes processing from its last saved stream
   position — no previously processed records are re-processed.
6. **Given** the adapter's saved position has expired (extended unplanned downtime),
   **When** it starts, **Then** it performs a new full load, logs the condition,
   and triggers an operational alert.

---

### Edge Cases

- What happens when the same CDC event is delivered twice? — The second delivery
  MUST produce the same outcome as the first (idempotent) with no duplicates in
  PersonModule.
- How does the system handle a reference data value unknown to PersonModule? —
  PersonModule auto-creates it; the adapter passes it through without error.
- What if a child record arrives before its parent person record during full load? —
  The ordering guarantee (persons first) prevents this. Full load MUST enforce
  person-before-child sequencing.
- What if the operations team needs to immediately retry fault queue entries before
  the next scheduled interval? — An administration endpoint allows triggering
  immediate re-processing. This endpoint requires authenticated access and is not
  reachable via the public gateway.
- What if a delete event arrives in the CDC stream for a person or child record? —
  Silently discarded (FR-022). BiRK records are legally retained and not physically
  deleted; delete events are treated as no-ops with no delivery to PersonModule.

---

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The adapter MUST consume create and update events for persons, child
  registrations, municipality assignments, and reference data from the BiRK CDC
  stream continuously.
- **FR-002**: The adapter MUST filter out organizational entity events (owner, unit,
  institution, employee, contact person) silently — no error, no alert, no delivery.
- **FR-022**: The adapter MUST silently discard CDC delete events for all record
  types — no delivery to PersonModule, no error, no alert. BiRK records are legally
  retained and not physically deleted; delete events are treated as no-ops.
- **FR-003**: The adapter MUST translate BiRK person identity fields to PersonModule's
  domain format per the canonical field mapping document (birk-person-feltmapping.md).
- **FR-004**: The adapter MUST translate BiRK child registration fields to
  PersonModule's domain format, including BirkID, type, status, security level,
  and municipality.
- **FR-005**: The adapter MUST translate BiRK reference data codes (gender, child
  type, child status, security level, municipality) to PersonModule's string-based
  reference data format.
- **FR-006**: The adapter MUST reject any record with security level 2 or 3, log the
  event as a critical security incident (BiRK ID + timestamp, no personal data),
  and trigger an immediate operational alert — the record MUST NEVER reach
  PersonModule. The operational alert MUST require manual acknowledgment and MUST
  NOT auto-resolve; each Kode 6/7 rejection is a security incident requiring
  explicit operator review and closure.
- **FR-007**: After rejecting a Kode 6/7 record, the adapter MUST advance the stream
  position so subsequent records continue processing without interruption.
- **FR-008**: The adapter MUST advance its stream position (checkpoint) only AFTER
  PersonModule has confirmed receipt of the delivered record. For batch delivery,
  the checkpoint advances once after the entire batch is confirmed — not per
  individual record within the batch.
- **FR-009**: The adapter MUST perform a complete initial load on first startup,
  delivering all persons before any child registrations.
- **FR-010**: The adapter MUST resume from its last saved stream position after a
  planned restart.
- **FR-011**: The adapter MUST detect an expired stream position — defined as a
  saved offset that is no longer within the Event Hub retention window — and
  initiate a new full load, with logging and operational alerting.
- **FR-012**: For high-volume operations (initial load, large change volumes), the
  adapter MUST use batch delivery to PersonModule's batch ingestion endpoint. During
  initial load, progress MUST be logged at a configurable interval (ops-defined)
  so operations staff can monitor it.
- **FR-013**: Transient delivery failures (server errors, timeouts) MUST be retried
  with increasing wait intervals. Rate-limit responses (HTTP 429) MUST be handled
  separately — the adapter MUST pause delivery for a configurable cool-down period
  without consuming retry attempts, then resume normal processing. Validation errors
  (rejected data) MUST NOT be retried.
- **FR-014**: Records that cannot be delivered after the maximum retry count MUST be
  persisted in a fault queue with full re-delivery data, and an operational alert
  MUST be raised. Records MUST NEVER be silently discarded.
- **FR-015**: A fault queue entry MUST contain the transformed payload in
  PersonModule's format plus error metadata. Metadata fields MUST NOT contain
  personal data.
- **FR-016**: The fault queue MUST be automatically reprocessed at a configurable
  interval. After successful re-delivery, the entry MUST be deleted and its
  personal data payload removed. Entries that have not been successfully
  re-delivered within a configurable maximum retention period (default: 30 days)
  MUST be automatically purged — the entry and its personal data payload are
  deleted, and the purge is logged as an unresolved delivery failure.
- **FR-017**: Operations staff MUST be able to trigger immediate fault queue
  re-processing via an administration endpoint. This endpoint requires
  authentication and MUST NOT be reachable via the public API gateway.
- **FR-018**: The adapter MUST expose the following operational metrics: events
  processed per record type, delivery outcomes (created/updated/unchanged), fault
  queue depth, Kode 6/7 rejection count, stream lag, and initial load progress.
- **FR-019**: The adapter MUST expose a liveness health endpoint (always returns
  healthy if the process is running) and a readiness health endpoint (reports
  dependency status including CDC stream, PersonModule API, and fault store).
- **FR-020**: The adapter MUST NOT store credentials for any connection — all
  connections MUST use platform-managed identity provided by the hosting environment.
- **FR-021**: All network communication MUST be contained within the private network —
  no traffic over public internet is permitted.

### Key Entities

- **Person**: An individual identity record from BiRK, identified by BiRK PersonPK
  (used as the external reference in PersonModule). Carries name, national ID,
  date of birth, and DUF number. Fields may be null for unborn children or
  unaccompanied minors.
- **Child Registration (BarnRegistrering)**: Links a child to M2LB tracking.
  Carries BirkID, registration type, status code, security level (0–1 in Phase 1),
  and municipality assignment.
- **Reference Data**: Lookup values for gender type, child type, child status type,
  security level type, and municipality. PersonModule is the authoritative owner;
  unknown values are auto-created on first use.
- **Fault Queue Entry**: A failed delivery record retained for re-processing. Contains
  the transformed payload (PersonModule's format), error type, error message, retry
  count, last attempt timestamp, creation timestamp, and expiry timestamp. Personal
  data in the payload is deleted after successful re-delivery or on expiry
  (whichever comes first).

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In steady-state CDC operation, all BiRK person and child changes are
  available in PersonModule within 15 minutes of the change being captured in the
  CDC stream. This criterion applies to continuous CDC processing only — initial
  full load duration is not bounded by this criterion.
- **SC-002**: The Kode 6/7 rejection counter is zero during all normal operating
  periods — any non-zero value is a critical incident requiring immediate
  investigation.
- **SC-003**: No CDC event is lost due to transient failures — every event is either
  successfully delivered or tracked in the fault queue with full re-delivery data.
- **SC-004**: Delivering the same CDC event twice does not create duplicate records in
  PersonModule — second delivery is a no-op.
- **SC-005**: After a planned restart, the adapter resumes without re-processing any
  previously delivered records.
- **SC-006**: Initial full load delivers all persons before any child registrations.
  Running the full load a second time produces no duplicates or errors.
- **SC-007**: Fault queue entries are automatically re-attempted within the configured
  interval (default: 5 minutes) once PersonModule becomes available.
- **SC-008**: Operations staff can determine adapter liveness, readiness, stream lag,
  and fault queue depth in real time without access to internal application logs.
- **SC-009**: All connections to external resources use platform-managed identity —
  a code audit finds zero stored credentials.

---

## Security Classification *(mandatory for features handling person or child data)*

- **Kode 6/7 impact**: The adapter handles child data including security level for
  every incoming child record. Security level is evaluated before any processing or
  forwarding. Records with level 2 (Kode 6) or level 3 (Kode 7) MUST be rejected
  immediately with no forwarding to PersonModule, a critical security log entry, and
  an immediate operational alert. This is a secondary defensive filter — the primary
  filter is in BiRK's source database.
- **Idempotency**: The adapter consumes a CDC stream that can re-deliver events and
  calls PersonModule's ingestion API. All processing MUST be idempotent — duplicate
  delivery produces the same outcome as single delivery.
- **Managed Identity**: All external connections (CDC stream, PersonModule API, fault
  store) use the hosting platform's managed identity. Zero credentials are stored
  in configuration, key stores, or source code.
- **State**: Persistent state is limited to stream position (checkpoint) and fault
  queue records. Fault queue records contain personal data in their payload; this
  data MUST be deleted immediately after successful re-delivery or auto-purged
  after the maximum retention period (default: 30 days), whichever comes first.

---

## Assumptions

- The BiRK CDC stream provides change events for the Person, Barn, and
  Barn_n_Hjemmstedskommune tables, plus reference data tables for gender, child
  type, child status, and security level. Exact table names for reference data
  tables are confirmed in birk-person-feltmapping.md (open item Å-01).
- BiRK's source database filters out Kode 6/7 records before they enter the CDC
  stream. The adapter's Kode 6/7 check is a secondary defensive layer and does not
  replace the source-level filter.
- PersonModule's ingestion API is idempotent — delivering the same record twice
  returns a "no change" response, not an error.
- PersonModule auto-creates unknown reference data values on first receipt — the
  adapter does not need to pre-register any reference data.
- Event Hub retention is at least 7 days, sufficient to recover from planned
  downtime. For extended outages or first-time full load where retention is
  insufficient, a dedicated full-load mechanism is available via the CDC pipeline
  in coordination with the operations team.
- Retry parameters (maximum attempts, backoff intervals), alerting thresholds (lag,
  unavailability duration), fault queue polling interval, fault queue maximum
  retention period, rate-limit cool-down period, initial load progress log
  interval, and batch sizes are operational configuration managed by the operations
  team — they are not functional requirements defined here.
- The adapter is a Phase 1 component and will be decommissioned when PersonModule
  takes over as the authoritative source for person data in Phase 2.

---

## Clarifications

### Session 2026-04-20

- Q: Does the 15-minute end-to-end SLA (SC-001) apply to initial full load? → A: No — SC-001 applies to steady-state CDC processing only; initial load duration is not bounded and varies with dataset size.
- Q: What makes a saved stream position "expired" (FR-011)? → A: The position is expired when the saved offset is no longer within the Event Hub retention window.
- Q: How should HTTP 429 responses from PersonModule be handled (FR-013)? → A: Separate from 5xx — pause delivery for a configurable cool-down period without consuming retry attempts, then resume.
- Q: What is the maximum retention period for unresolved fault queue entries (FR-016)? → A: Configurable maximum age, default 30 days; entry and personal data payload auto-deleted on expiry, purge logged as unresolved delivery failure.
- Q: What constitutes "regular intervals" for initial load progress logging (FR-012)? → A: Configurable ops-defined interval; not spec-mandated.

### Session 2026-04-20 (run 2)

- Q: How should the adapter handle CDC delete events for persons and child registrations? → A: Silently discard — no-op. BiRK records are legally retained and not physically deleted (FR-022).
- Q: At what granularity does the checkpoint advance during batch delivery (FR-008)? → A: Per batch — checkpoint advances once after the entire batch is confirmed by PersonModule.
- Q: Should Kode 6/7 operational alerts auto-resolve or require manual acknowledgment (FR-006)? → A: Manual acknowledgment required — alert stays open until an operator explicitly closes the incident.
