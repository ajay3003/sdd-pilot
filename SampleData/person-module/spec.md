# Feature Specification: Person Module Core

**Feature Branch**: `001-person-module`
**Created**: 2026-03-06
**Status**: Draft
**Source**: `docs/person-func-requirements-no.md`

## Clarifications

### Session 2026-03-06

- Q: What defines the valid BarnStatusType state values and valid transitions — should they be defined in this spec, or does BiRK govern them? → A: BiRK is authoritative; the Person module accepts any BarnStatusType value delivered by BiRK verbatim, without locally enforcing or validating transition order.
- Q: Which user stories are served via GraphQL vs REST? → A: GraphQL covers all presentation-layer queries (US1 search, US2 profile, US3 access management display, US4 reference data); REST is used exclusively for ingestion (US5) and operation registration at startup (FR-029).
- Q: Who stores the immutable audit trail (Revisjonshendelse) and how does atomicity work? → A: The Person module publishes audit events to a dedicated Service Bus topic via the outbox pattern; a separate platform-level Audit service persists them. The Person module's own database does not store audit records.
- Q: What is the target search response time SLA? → A: p95 under 2 seconds.
- Q: What does "fail-closed" mean for the Authorisation service from the user's perspective? → A: Only the individual request that cannot be authorised is rejected (HTTP 503); the rest of the service remains available and processes other requests normally.
- Q: How should the ingestion API handle the DUF → fødselsnummer identity upgrade? → A: Update the existing Person record in-place (UUID unchanged); retain the DUF number as a historical secondary identifier; publish `PersonOppdatert`.
- Q: What is the cardinality between Person and BarnIAndrelinjeBarnevernet? → A: 1:1 — a Person has at most one barn registration; re-registration updates the existing record via soft lifecycle (no new record created).
- Q: Is the set of operations registered at startup (FR-029) exhaustive for Phase 1? → A: Yes — exactly these 7 operations (corrected from 6 after reviewing `docs/person-module-operations.md`): `Person:SøkBarn`, `Person:SeBarnGrunnprofil`, `Person:SeBarnProfil`, `Person:SeFullIdentitet`, `Person:SeGradertBarn`, `Person:AdministerGradertBarntilgang`, `Person:SeRevisjonslogg`.
- Q: Does `docs/person-module-operations.md` (authoritative catalog, 7 ops) override the earlier "6 ops" answer? → A: Yes — `Person:SeRevisjonslogg` (general) was missing. FR-029 updated to 7 operations.
- Q: Should `BarnKommuneEndret` and `BarnTypeEndret` (from `docs/person-event-contracts-no.md`) be added to FR-025? → A: Yes — both added. All 7 events in the authoritative contracts doc are now in scope.
- Q: Should `EksternId` (from `docs/person-domain-model-no.md`) be added to the Person entity? → A: Yes — optional field for legacy/external system identifiers (e.g. BiRK Party-ID) for migration traceability.
- Q: Should `ErForventetOvergang` be a required field in `BarnStatusEndret` per the event contracts doc? → A: Yes — FR-021 updated to require this flag; makes anomaly detection contract-testable.
- Q: Should FR-027 require Service Bus session-based ordering (sessionId = entity UUID) for per-entity event ordering guarantees? → A: Yes — FR-027 updated to require session-based ordering to prevent race conditions on rapid state changes.
- Q: How is BarnStatusType transition history persisted for FR-012? → A: Dedicated append-only `BarnStatusHistorikk` table — one row per transition, never deleted.
- Q: Who orchestrates the access grant creation in the Authorisation module for US3? → A: The Person module — it validates pre-conditions (child exists, is classified, self-assignment check) and calls the Authorisation module to create the grant. The presentation layer talks to the Person module only.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Search for Children (Priority: P1)

A caseworker needs to quickly locate a child they are working with by searching on name,
national ID (fødselsnummer), DUF number, or BirkID. The system returns only children the
caseworker is authorised to see. Kode 6/7 children without explicit access are completely
invisible — no count, no metadata, no indication they exist.

**Why this priority**: Search is the primary entry point for all casework. Without it no
other feature is accessible. It also enforces the most critical safety requirement:
security classification must be absolute.

**Independent Test**: A caseworker can search by name and receive a paginated list of
results containing only authorised children. A second search for a child with Kode 7
classification — performed by a user without that child's `Person:SeGradertBarn`
operation — returns zero results with no indication the child exists.

**Acceptance Scenarios**:

1. **Given** a caseworker with `Person:SøkBarn` for an org unit, **When** they search by
   partial name, **Then** they receive a paginated summary list containing only children
   in that org unit at security level 0 or 1, within acceptable response time.
2. **Given** a caseworker with `Person:SeGradertBarn` for a specific Kode 6 child,
   **When** they search by that child's name, **Then** the child appears in results with
   a clear address-protection flag indicating the address must not be disclosed.
3. **Given** a caseworker without `Person:SeGradertBarn` for a Kode 7 child,
   **When** they search by name, national ID, or BirkID that would match that child,
   **Then** the response contains zero matches and no field or count that reveals the
   child's existence.
4. **Given** a caseworker applies filters for BarnStatusType, BarnType, or Municipality,
   **When** the search executes, **Then** only results matching all applied filters are
   returned.
5. **Given** a search returning more than one page of results, **When** the caseworker
   requests page 2, **Then** subsequent pages are returned correctly.
6. **Given** any search request, **When** it is processed, **Then** the system logs
   user identity, search criteria, and timestamp; for results including Kode 6/7 children
   the log records which classified children were included.

---

### User Story 2 — View Child Profile (Priority: P2)

A caseworker needs to view a child's full profile: identity, status, type, municipality,
address-protection flags, and status history. Access to individual fields (e.g. national
ID) depends on which operations the caseworker holds. Uncertain identity values are
visually marked as provisional.

**Why this priority**: Profile viewing is the core read operation after search. It exposes
the most sensitive data and therefore must enforce layered access control.

**Independent Test**: A caseworker with `Person:SeBarnGrunnprofil` can open a child's
profile and see all baseline fields. National ID is masked unless they also hold
`Person:SeFullIdentitet`. A Kode 6/7 child profile additionally requires
`Person:SeGradertBarn`.

**Acceptance Scenarios**:

1. **Given** a caseworker with `Person:SeBarnGrunnprofil`, **When** they open a child
   profile, **Then** they see: Name, BirkId, BarnType, BarnStatusType, Municipality,
   security level, and data source. National ID is masked.
2. **Given** a caseworker with `Person:SeFullIdentitet` for a child, **When** they view
   the profile, **Then** the full national ID (fødselsnummer/DUF) is shown unmasked.
3. **Given** a child with security level 1, Kode 6, or Kode 7, **When** the profile is
   viewed, **Then** a prominent visual flag states the address is protected and must not
   be disclosed externally, showing the security level/code.
4. **Given** a child with `UsikkerFødselsdato` or `UsikkerFødselsnummer`,
   **When** the profile is displayed, **Then** those values are shown with a clear
   "provisional/uncertain" marking.
5. **Given** a caseworker viewing a profile, **When** they access status history,
   **Then** all past BarnStatusType transitions are shown with timestamp, changed-by,
   and source (BiRK or manual).
6. **Given** a caseworker without `Person:SeGradertBarn` for a Kode 6/7 child,
   **When** they attempt to open that child's profile, **Then** access is denied with no
   indication the child exists.

---

### User Story 3 — Manage Access to Kode 6/7 Children (Priority: P3)

An authorised user who already has access to a Kode 6/7 child needs to grant named
colleagues access to that child. All grants are time-bounded, logged with full identity,
and self-assignment is forbidden. Administrators can audit the full access history.

**Why this priority**: Safety-critical administration but depends on search and profile
(P1/P2) being functional first and is used by a smaller set of users.

**Independent Test**: A user holding `Person:AdministerGradertBarntilgang` for a Kode 6
child can grant a colleague access with an expiry date. The grant appears in the access
list and is recorded in the immutable audit trail. The colleague cannot grant access
to themselves.

**Acceptance Scenarios**:

1. **Given** a user with `Person:AdministerGradertBarntilgang` for a Kode 6/7 child,
   **When** they grant another named user access via the Person module, **Then** the
   Person module validates pre-conditions (child exists, is classified, no
   self-assignment) and calls the Authorisation module to create the grant; the new
   access appears in the access list.
2. **Given** an access grant being created with an expiry date, **When** that date
   arrives, **Then** the access automatically expires without manual intervention.
   Expiry enforcement is the responsibility of the Authorisation module: it rejects
   access evaluations for grants where `GyldigTil` has passed. The Person module
   writes `GyldigTil` to the grant at creation time and takes no further action.
3. **Given** any access grant or revocation, **Then** the audit trail records:
   granting user, recipient user, target child, timestamp, and any stated reason.
4. **Given** a user attempting to grant themselves access to a Kode 6/7 child,
   **When** they submit the request, **Then** the system rejects it with an appropriate
   message (no self-assignment).
5. **Given** a leader/administrator viewing access for a Kode 6/7 child,
   **When** they open the access list, **Then** they see all current and historical
   grants: granted-by, granted-to, role, valid-from/to, and timestamp.

---

### User Story 4 — Reference Data (Priority: P4)

Caseworkers and consuming services need to read all active reference data values (gender
types, child types, status types, security levels, municipalities). In phase 1 (BiRK as
data owner), reference data is read-only and populated via the CDC pipeline. In phase 2,
administrators can add, update, and deactivate values without a new deployment.

**Why this priority**: Reference data underpins all other features but is a prerequisite
infrastructure concern, not a user-facing goal.

**Independent Test**: An API call to the reference data endpoint returns all active
KjønnType, BarnType, BarnStatusType, SikkerhetsnivåType, and Kommune values. A deactivated
value does not appear in new-registration options but does appear on historical records
that referenced it.

**Acceptance Scenarios**:

1. **Given** a consumer requests reference data for any type table,
   **When** the request is processed, **Then** all currently active values are returned.
2. **Given** a historical record referencing a now-deactivated value,
   **When** that record is viewed, **Then** the deactivated value is still shown correctly.
3. **Given** phase 1 (BiRK as data owner) and the CDC pipeline delivers a value not yet
   known to the Person module, **When** it is processed, **Then** it is added automatically.
4. **Given** phase 2 (Person module as data owner) and an authorised administrator
   deactivates a reference value, **When** they do so, **Then** it no longer appears in
   new registration options but existing data is unaffected.
5. **Given** any reference data change (any source), **Then** it is recorded in the
   immutable audit trail.

---

### User Story 5 — Data Ingestion from BiRK (CDC Pipeline) (Priority: P5)

The Person module continuously receives person and child data from BiRK via a CDC pipeline
adapter. The adapter translates BiRK's data model into the Person module's domain format.
Ingestion is idempotent and publishes domain events. Operations staff can monitor
ingestion health.

**Why this priority**: Foundational data supply; without it, no other story has data.
However, it is a system-to-system story with no direct user UI.

**Independent Test**: Sending a BiRK-format person record (via adapter) to the ingestion
endpoint results in: the record persisted in the Person module's domain model, source
marked as "BiRK" in the audit trail, and a `PersonOpprettet` event published to Service
Bus. Sending the same record twice produces no duplicates.

**Acceptance Scenarios**:

1. **Given** the BiRK adapter sends a new person record, **When** the ingestion API
   processes it, **Then** the person is created with source "BiRK" and a
   `PersonOpprettet` event is published.
2. **Given** a child registration event from BiRK, **When** ingested, **Then**
   `BarnRegistrert`, `BarnStatusEndret`, `SikkerhetsnivåEndret`, `BarnKommuneEndret`,
   or `BarnTypeEndret` events are published as appropriate.
3. **Given** the same record is submitted twice, **When** the second submission is
   processed, **Then** no duplicate is created and no duplicate event is published.
4. **Given** a record with invalid data (domain invariant violation), **When** submitted,
   **Then** the error is logged and reported; other records continue processing.
5. **Given** a status transition that is unexpected from a business context perspective
   (BiRK is authoritative for valid values; no local transition order is enforced),
   **When** received, **Then** the transition is accepted and stored verbatim, the
   `BarnStatusEndret` event is published with `ErForventetOvergang = false`, and
   the anomaly is logged for follow-up.
6. **Given** an operations user, **When** they view ingestion metrics, **Then** they see:
   records processed per period, error count, and average latency from BiRK change to
   availability.

---

### User Story 6 — Domain Events (Priority: P5)

Consuming services subscribe to Person module domain events on Service Bus. Events contain
only UUIDs and metadata — never personal data. Consumers fetch details via authorised API
calls.

**Why this priority**: Same infrastructure tier as ingestion; parallel to US5.

**Independent Test**: After a person is created, a `PersonOpprettet` event appears on
the Service Bus topic containing PersonId and timestamp but no name, national ID, or
personal data. After a security level change, a `SikkerhetsnivåEndret` event is published
with the new level.

**Acceptance Scenarios**:

1. **Given** a person is created or updated, **Then** the corresponding event
   (`PersonOpprettet` / `PersonOppdatert`) is published to its Service Bus topic.
2. **Given** a child is registered or changes status, security level, municipality,
   or type, **Then** the corresponding event (`BarnRegistrert`, `BarnStatusEndret`,
   `SikkerhetsnivåEndret`, `BarnKommuneEndret`, `BarnTypeEndret`) is published.
3. **Given** any event payload, **When** inspected, **Then** it contains only UUID
   identifiers and metadata — no names, national IDs, addresses, or family data.
4. **Given** a data mutation is persisted, **When** the event is published, **Then**
   both operations are atomic (no mutation without event, no event without mutation).
5. **Given** a consuming service processes the same event twice, **When** handling the
   duplicate, **Then** the outcome is identical to processing it once (idempotent).

---

### Edge Cases

- An unborn child has no birth date and no national ID — this is a valid, complete
  identity state, not a data quality problem.
- An EMA child may initially have only a name — no national ID, no DUF number.
- A person's national ID may become available later (DUF → fødselsnummer upgrade).
- A caseworker searching for a Kode 7 child they have access to receives results;
  a caseworker without access receives nothing — the same search term produces
  different results for different users.
- Municipality mergers: historical municipality codes must remain valid on historical
  records even after the municipality is no longer active.
- Concurrent ingestion of the same BiRK record must not create duplicates.

## Requirements *(mandatory)*

### Functional Requirements

**FR-001**: The system MUST support child search on: name (free-text, partial match),
national ID (exact and partial), DUF number (exact), and BirkID (exact) — individually
and in combination.

**FR-002**: All search results MUST be filtered through authorisation control before
returning to the user, according to the security level model:
- Levels 0 and 1: requires general `Person:SøkBarn` for the child's org unit
- Kode 6 / Kode 7: requires child-specific `Person:SeGradertBarn` for that child

**FR-003**: Kode 6/7 children for which the requesting user lacks `Person:SeGradertBarn`
MUST be completely invisible — no count, no metadata, no error that reveals their existence.

**FR-004**: Search results for children with security level 1, Kode 6, or Kode 7 MUST
include an address-protection flag showing the security level/code and that the address
must not be disclosed.

**FR-005**: Search results MUST support filtering on BarnStatusType, BarnType, and
Municipality, and MUST support pagination.

**FR-006**: Each search result entry MUST include at minimum: PersonId, name, birth date
(if available), BirkId, BarnStatusType, BarnType, Municipality, and address-protection
flag (if applicable).

**FR-007**: All search requests MUST be logged with user identity, search criteria, and
timestamp. Searches returning Kode 6/7 children MUST additionally log which classified
children were included.

**FR-008**: Child profile access MUST require authorisation:
- Levels 0–1: `Person:SeBarnGrunnprofil` (general) or `Person:SeBarnProfil` (child-specific)
- Kode 6/7: additionally requires `Person:SeGradertBarn` for that child

**FR-009**: The profile MUST display: name, birth date (or UsikkerFødselsdato with
marking), gender, BirkId, BarnType, BarnStatusType, Municipality, security level, and
source information.

**FR-010**: National ID MUST be masked (e.g. `***********`) unless the user holds
`Person:SeFullIdentitet` for the child.

**FR-011**: Uncertain identity fields (`UsikkerFødselsnummer`, `UsikkerFødselsdato`)
MUST be displayed with a clear "provisional/uncertain" marker.

**FR-012**: Status history MUST show all BarnStatusType transitions with timestamp,
actor, and source. Transitions are persisted in a dedicated append-only
`BarnStatusHistorikk` table (one row per transition, never deleted).

**FR-013**: Granting access to a Kode 6/7 child MUST require
`Person:AdministerGradertBarntilgang` for that child.

**FR-014**: Access grants MUST support an optional expiry date for time-limited access.

**FR-015**: Self-assignment of classified child access MUST be rejected.

**FR-016**: All access grant and revocation actions MUST be recorded in the immutable
audit trail with: granting user, recipient, child, timestamp, and optional reason.

**FR-017**: Reference data (KjønnType, BarnType, BarnStatusType, SikkerhetsnivåType,
Kommune) MUST be exposed as readable API endpoints. Reference data contains no personal
data and MUST be accessible without per-request Authorisation module evaluation — any
authenticated caller may read reference data. No reference data operation is registered
with the Authorisation module (FR-029).

**FR-018**: In phase 1, reference data MUST be updated only via the CDC pipeline. New
values delivered by BiRK MUST be added automatically.

**FR-019**: Deactivation of a reference value MUST NOT delete it or break historical
records referencing it.

**FR-020**: The ingestion API MUST be BiRK-agnostic — it accepts data in the Person
module's domain format.

**FR-021**: Ingestion MUST handle: Person (create/update) and BarnIAndrelinjeBarnevern
(create, update status, security level, municipality, type). When processing a
BarnStatusType change, the `BarnStatusEndret` event MUST include `ErForventetOvergang`
set to `true` if the transition matches a known expected path (per the BiRK state
machine in Key Entities), or `false` for anomalous/unexpected transitions.

**FR-022**: Ingestion MUST be idempotent — repeated delivery of the same change MUST
produce no duplicates or errors.

**FR-023**: Ingestion validation failures for individual records MUST be logged and
reported without stopping processing of other records.

**FR-024**: Ingestion MUST expose metrics: records processed per period, error count,
average latency from BiRK change to availability. The BiRK origination timestamp is
supplied by the adapter as an optional `BirkEndringstidspunkt: DateTimeOffset?` field
on the ingestion request. When present, latency is calculated as
`AvailabilityTimestamp − BirkEndringstidspunkt`. When absent, the metric is omitted
for that record (not defaulted to ingestion receipt time).

**FR-025**: Domain events MUST be published for all data mutations. Events are
published to two Service Bus topics:
- **`person.person`**: `PersonOpprettet`, `PersonOppdatert`
- **`person.barn`**: `BarnRegistrert`, `BarnStatusEndret`, `SikkerhetsnivåEndret`,
  `BarnKommuneEndret`, `BarnTypeEndret`

`SikkerhetsnivåEndret` is security-critical and MUST be marked high-priority on the
Service Bus message to minimise the window where a child's protection is not reflected.

**FR-026**: Event payloads MUST contain only UUID identifiers and metadata — no names,
national IDs, addresses, or family information.

**FR-027**: Event publication and data mutation MUST be atomic (outbox pattern or
equivalent). Events for the same entity MUST be published with Service Bus SessionId
set to the entity's UUID to guarantee per-entity ordering. Consumers MUST use
session-aware receivers and deduplicate by `HendelsesId` for idempotency.

**FR-028**: All data mutations MUST produce an audit event published to a dedicated
Service Bus topic via the outbox pattern. The audit event MUST contain: user identity
(or system process), action, entity, before/after state, timestamp, and source. A
separate platform-level Audit service is responsible for persisting these events
immutably. The Person module's own database MUST NOT store audit records (no local
table with DELETE rights).

**FR-029**: The Person module MUST register exactly the following operations with the
Authorisation module at startup (via Service Bus queue `operasjonsregistrering`,
using `IHostedService` per PS-06). These are the complete set for Phase 1:
- `Person:SøkBarn` — general search (org-unit scope)
- `Person:SeBarnGrunnprofil` — view child baseline profile (general)
- `Person:SeBarnProfil` — view child profile (child-specific)
- `Person:SeFullIdentitet` — view unmasked national ID (child-specific)
- `Person:SeGradertBarn` — view/search Kode 6/7 classified children (child-specific)
- `Person:AdministerGradertBarntilgang` — manage access grants for classified children (child-specific)
- `Person:SeRevisjonslogg` — search and view audit log entries for all Person module entities (general); for Kode 6/7 children additionally requires `Person:SeGradertBarn` for the specific child

**FR-030**: All data MUST be stored in the Norway East Azure region.

**FR-031**: If the Authorisation service is unreachable or returns an error, the
individual request MUST be rejected with HTTP 503 and a non-revealing error message.
No access decision MUST be assumed or cached from a failed call. Other concurrent
requests are unaffected. The service MUST remain operational for requests that can be
fully processed.

**FR-033**: For access grant creation (US3), the Person module MUST orchestrate the
full flow: validate that the granting user holds `Person:AdministerGradertBarntilgang`
for the target child, enforce self-assignment rejection (FR-015), confirm the child
exists and is classified, and then call the Authorisation module to create the grant.
The presentation layer MUST call only the Person module for this operation (PP-01).

**FR-032**: When a Person's national ID is upgraded from DUF number to fødselsnummer,
the existing Person record MUST be updated in-place (UUID unchanged). The DUF number
MUST be retained as a historical secondary identifier on the record. A `PersonOppdatert`
event MUST be published. No new Person record is created.

### API Surface

The Person module exposes a dual API surface per the module constitution:

- **GraphQL** — consumed by the presentation layer for all read operations: child search
  (US1), child profile display (US2), access management display (US3), and reference data
  (US4). Provides field-level flexibility and data minimisation per GDPR Article 5.
- **REST** — consumed by the BiRK adapter for data ingestion (US5) and by the service
  itself for operation registration at startup (FR-029). Provides predictable, idempotent
  ingestion with explicit HTTP status codes.

No end-user write operations are exposed in Phase 1. Both surfaces are headless and
contract-driven; no business logic resides in the API layer.

### Key Entities

- **Person**: Any individual relevant to child welfare work. Identified by UUID v4.
  Optionally carries national ID (fødselsnummer) and/or DUF number; both may coexist
  as the DUF number is retained as a historical secondary identifier after a
  fødselsnummer is assigned (FR-032). Optionally carries `EksternId` — an opaque
  string for external/legacy system identifiers (e.g. BiRK Party-ID) for migration
  traceability; never used as a primary identifier. Supports uncertain identity fields
  as a first-class structural state.
- **BarnIAndrelinjeBarnevernet**: A Person formally registered as a 2nd-line child welfare
  recipient. Each Person has at most one barn registration (1:1). Re-registration updates
  the existing record (soft lifecycle); no new record is created. Carries BirkId,
  BarnType, BarnStatusType, security classification level, and Municipality.
  BarnStatusType values are BiRK-authoritative (see BarnStatusType below).
- **BarnStatusType**: Values tracking a child's journey through service provision
  (domain-local reference data). Valid values are authoritative in BiRK; the Person
  module accepts and stores any value delivered by BiRK verbatim without enforcing
  local transition order. Known values from BiRK:
  `Bestilling/Under Behandling` → `ReservertTiltak` → `ITiltak` → `Avsluttet`;
  also `UavklartTiltak` → `ITiltak` or `Avsluttet`; and `Ukjent`.
  Unexpected transitions are accepted but flagged `ErForventetOvergang = false`
  in the `BarnStatusEndret` event.
- **SikkerhetsnivåType**: Classification level with numeric ordering. Governs
  visibility rules for all data operations:
  - Nivå 0 — no restriction (standard for most children)
  - Nivå 1 — hidden address (`SkjultAdresse`): visible via general access; address
    flagged as protected in API response
  - Nivå 2 — `Kode7` (fortrolig / BiRK: Kode 7): completely hidden without
    child-specific `Person:SeGradertBarn`; address flagged as protected
  - Nivå 3 — `Kode6` (strengt fortrolig / BiRK: Kode 6): completely hidden without
    child-specific `Person:SeGradertBarn`; address flagged as protected
  Kode 6 (Nivå 3) is more restrictive than Kode 7 (Nivå 2). Both are handled
  identically by access control — `Person:SeGradertBarn` covers both.
- **KjønnType / BarnType**: Domain-local reference data stored as database table rows.
- **Kommune**: Municipality reference, supporting historical codes for merger scenarios.
- **BarnStatusHistorikk**: Append-only table recording every BarnStatusType transition
  for a barn registration. One row per transition; rows are never deleted (PP-05).
  Each row contains: BarnId, previous status, new status, timestamp, actor (user or
  system), and source (BiRK or manual). Provides the data backing for FR-012.
- **Revisjonshendelse**: Audit event published by the Person module to a dedicated
  Service Bus topic for every mutation — who, what, when, source, before/after state.
  Persisted immutably by a separate platform-level Audit service. Contains only UUIDs
  and metadata; no sensitive personal data.

## Success Criteria *(mandatory)*

### Measurable Outcomes

**SC-001**: A caseworker can locate a known child by name or ID within 3 user interactions
(search → results → confirm) without assistance.

**SC-002**: Search results are returned within **p95 < 2 seconds** for large datasets
(thousands of children per org unit). This SLA applies to authorised queries through
the GraphQL search endpoint under expected load conditions.

**SC-003**: Kode 6/7 classification enforcement is verified by automated tests: zero
false positives (child visible to unauthorised user) and zero false negatives (child
invisible to authorised user) across all search and profile endpoints.

**SC-004**: All data mutations produce a corresponding immutable audit record — verifiable
by cross-checking mutation count against audit trail count in integration tests.

**SC-005**: Ingestion is idempotent — submitting the same BiRK record N times produces
exactly 1 stored record and 1 domain event, verified by automated tests.

**SC-006**: Domain events contain no personal data — verified by automated payload
inspection tests that fail if any non-UUID string field bearing personal data is present.

**SC-007**: Operation registration completes successfully at every service startup —
verified by a health-check that confirms registration before the service accepts requests.

**SC-008**: The Person module is the single source of truth — no other service in the
integration test environment stores person details beyond UUID references.

## Assumptions

- **Phase 1 scope**: BiRK is data owner. No end-user write operations (create/edit
  person or child) are in scope for this phase. The ingestion API is service-to-service.
- **Authorisation module availability**: The Authorisation module's evaluation API is
  available and callable under normal conditions. Its contract is defined by the
  Authorisation module spec. When unreachable, the Person module fails closed per
  FR-031: the affected request is rejected with 503; the service as a whole remains up.
- **Performance SLA**: Search response time target is p95 < 2 seconds (SC-002).
  Infrastructure sizing during planning must validate this target.
- **Security classification codes**: Kode 6, Kode 7, level 0, level 1 follow Norwegian
  child welfare legislation definitions and are not redefined in this spec.
- **Adapter separation**: The BiRK adapter is a separate deployable component. Its
  internal translation logic is out of scope; only the ingestion API contract is in scope.
