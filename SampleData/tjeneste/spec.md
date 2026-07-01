# Feature Specification: Tjenestemodul M01

**Feature Branch**: `001-tjenestemodul-m01`  
**Created**: 2026-04-13  
**Status**: Draft  
**Input**: Tjenestemodul M01 — Service module for tracking child placements in second-line child welfare, with BiRK synchronization, case worker API, and internal service lookup

---

## Clarifications

### Session 2026-04-13

- Q: Sort order when move-in dates are null → A: Use actual move-in date if set, otherwise planned move-in date; placements where both are null appear last.
- Q: Synchronization latency target → A: Near-real-time — a BiRK change must be visible in the case worker view within 1–2 minutes.
- Q: Audit logging for case worker reads → A: Every case worker read publishes an audit event to the platform audit service.
- Q: Permanently unresolvable placements → A: Placements unlinked beyond a configurable deadline are automatically flagged with a dedicated status for operator visibility.
- Q: Idempotency of BarnRegistrert child-linkage events → A: A duplicate link event for an already-linked placement is silently ignored — no error, no state change.
- Q: Async linkage completion target → A: Child linkage must complete within 1–2 minutes of the BarnRegistrert event being received.
- Q: TjenesteOpprettet publish failure handling → A: Linkage persists; the publish is retried with exponential backoff until successful or dead-lettered.
- Q: Missing lookup table at ingestion time → A: Defer the placement message until the referenced lookup record arrives; retry automatically.
- Q: Case worker query response time target → A: Placement history must be returned in under 500 milliseconds.
- Q: Personmodulen unavailability at ingestion → A: Retry the lookup with exponential backoff; only store with null child linkage after retries are exhausted.
- Q: Pagination of placement list → A: No pagination for M01 — always return the full list; the 500ms target applies to the complete response.
- Q: Error code for permanently flagged placements → A: Two distinct codes: `BARN_ID_IKKE_KOBLET` for pending linkage (retry later), `TILTAK_PERMANENT_UKOBLET` for permanently flagged (do not retry).
- Q: Audit event content scope → A: Metadata-only using platform-standard schema: hendelsesId, hendelsesTidspunkt, brukerId, barnId, operasjonNavn, tjenestenavn, korrelasjonId. Published to topic `leselogg` as event type `LeseloggHendelse`.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Saksbehandler ser tjenesteoversikt for et barn (Priority: P1)

A case worker opens a child's profile page and sees a complete, up-to-date list of all placements and services the child has received from second-line child welfare (2. linje barnevern). The list shows each placement with its type, current status, reason for termination (if applicable), and relevant dates. The list is sorted with the most recent first, allowing the case worker to quickly understand the child's current situation and history.

**Why this priority**: This is the primary user-facing value of the module. Case workers need accurate, consolidated placement history to make informed decisions. Without this, the module has no value to end users.

**Independent Test**: Can be fully tested by requesting the placement history for a known child identifier, verifying that all placements are returned in correct order with correct fields.

**Acceptance Scenarios**:

1. **Given** a child has two active placements registered in the system, **When** a case worker queries for that child's services, **Then** both placements are returned, sorted with the most recent first, each including type, status, and all relevant dates.

2. **Given** a child has no registered services, **When** a case worker queries for that child's services, **Then** an empty list is returned — not an error.

3. **Given** a child has one fully linked placement and one with pending child linkage (BarnId not yet resolved), **When** a case worker queries for that child's services, **Then** only the fully linked placement is returned.

4. **Given** a case worker has valid authentication but lacks the required access permission (`Tjeneste:HentTjenesterForBarn`), **When** they query for a child's services, **Then** access is denied.

5. **Given** a case worker has no valid authentication token, **When** they query for a child's services, **Then** access is denied.

---

### User Story 2 - Saksbehandler ser detaljer for én tjeneste (Priority: P2)

A case worker needs to look up the full details of a specific placement by its unique identifier — for example, when following a reference from another part of the system.

**Why this priority**: Supports detailed inspection of a single placement. Lower priority than the overview, but needed for a complete view.

**Independent Test**: Can be tested by requesting a single placement by its unique identifier and verifying all fields are returned correctly.

**Acceptance Scenarios**:

1. **Given** a placement exists with a known identifier and is fully linked to a child, **When** a case worker queries for that specific placement, **Then** all fields are returned with correct values, and optional fields not set are returned as empty rather than errors.

2. **Given** a placement identifier that does not exist, **When** a case worker queries for it, **Then** an empty result is returned — not an error.

3. **Given** a placement exists but its child linkage is still pending, **When** a case worker queries for it by identifier, **Then** an empty result is returned.

---

### User Story 3 - Hendelsestjenesten slår opp barn for et BiRK-tiltak (Priority: P1)

The Hendelsestjenesten integration layer receives events from BiRK that are tied to a Tiltak (not directly to a child). To process these events correctly, it needs to resolve which child the Tiltak belongs to. It calls the internal lookup endpoint with a BiRK Tiltak identifier and receives back the internal child identifier and placement identifier.

**Why this priority**: This is required for the broader M2LB event processing pipeline to function correctly. Equal in priority to the case worker view as it enables the platform's event handling.

**Independent Test**: Can be tested by calling the internal lookup with a known BiRK Tiltak key and verifying the correct child and placement identifiers are returned.

**Acceptance Scenarios**:

1. **Given** a placement exists with a known BiRK Tiltak key and is fully linked to a child, **When** the Hendelsestjenesten calls the lookup with that key, **Then** the internal child identifier and placement identifier are returned.

2. **Given** a BiRK Tiltak key that does not exist in the system, **When** the Hendelsestjenesten calls the lookup, **Then** error code `TILTAK_IKKE_FUNNET` is returned.

3. **Given** a placement exists for the BiRK Tiltak key, but child linkage is still pending, **When** the Hendelsestjenesten calls the lookup, **Then** error code `BARN_ID_IKKE_KOBLET` is returned — signalling the caller should retry later.

4. **Given** a call without a valid system identity token, **When** the lookup is called, **Then** access is denied (401).

---

### User Story 4 - Synkronisering fra BiRK holder tjenesteoversikten oppdatert (Priority: P1)

When placements change in BiRK (new placement, status update, termination), these changes are automatically reflected in the Tjenestemodul without manual intervention. Case workers always see the latest known state from BiRK.

**Why this priority**: Without continuous synchronization, the module quickly becomes stale and unreliable, undermining all use cases.

**Independent Test**: Can be tested by inserting or updating a placement in BiRK and verifying that the change is reflected in the Tjenestemodul API.

**Acceptance Scenarios**:

1. **Given** a new placement is created in BiRK, **When** the synchronization processes it, **Then** a corresponding entry is available in the system with correctly translated field values and the child correctly linked.

2. **Given** an update arrives for an already-synchronized placement, **When** processed, **Then** the existing entry is updated — no duplicate is created.

3. **Given** the same change message is delivered twice (duplicate), **When** both are processed, **Then** no duplicate entries are created and no errors occur.

4. **Given** a lookup table (service type, status type, termination reason) is missing when a placement arrives, **When** the placement is processed, **Then** the system handles this gracefully — lookup tables are always loaded before placement records.

5. **Given** a temporary system failure occurs during synchronization, **When** the system recovers, **Then** unprocessed messages are retried with increasing wait times; messages that exhaust all retries are moved to a dead-letter queue and operations staff are alerted.

---

### User Story 5 - Asynkron kobling av barn til tjeneste (Priority: P2)

When a new placement arrives from BiRK but the child is not yet registered in the Personmodulen, the placement is stored in a waiting state. When the child is later registered in Personmodulen, the placement is automatically linked and made available for case workers. The Hendelsestjenesten is notified so it can link any pending events for that child.

**Why this priority**: Handles an expected race condition between BiRK and Personmodulen data. Required for completeness but does not block the primary happy path.

**Independent Test**: Can be tested by synchronizing a placement for an unknown child, verifying it is not visible in the API, then triggering a child registration event and confirming the placement becomes visible and `TjenesteOpprettet` is published.

**Acceptance Scenarios**:

1. **Given** a placement arrives for a child not yet in Personmodulen, **When** processed, **Then** the placement is stored, child linkage is pending, the placement is not visible via the case worker API, and no `TjenesteOpprettet` event is published.

2. **Given** a placement is waiting for child linkage, and Personmodulen registers the child, **When** the `BarnRegistrert` event is received, **Then** all pending placements for that child are linked, each becomes visible in the API, and a `TjenesteOpprettet` event is published for each.

3. **Given** the number of unlinked placements exceeds a configured threshold over time, **When** that threshold is crossed, **Then** operations staff are alerted and the health endpoint reports the count.

---

### Edge Cases

- What happens when a BiRK lookup table update arrives after dependent placement records? → Placement message is deferred and retried automatically when the lookup record arrives (see FR-012a).
- How does the system handle placements that are permanently unresolvable (child never registered in Personmodulen)? → Flagged with a dedicated status after a configurable deadline; remain in the system for operator review (see FR-019a).
- What happens when a duplicate `BarnRegistrert` event triggers re-linking of an already-linked placement? → Silent no-op; already-linked placements are not affected (see FR-015).
- How does the system behave if the Personmodulen is temporarily unavailable during child lookup? → Lookup retried with exponential backoff; placement stored with null only after retries exhausted (see FR-014).
- What if a `TjenesteOpprettet` event fails to publish after a successful child linkage? → Linkage persists; publish retried with exponential backoff, dead-lettered on exhaustion (see FR-016).

---

## Requirements *(mandatory)*

### Functional Requirements

**Visning for saksbehandlere:**

- **FR-001**: The system MUST expose a query that returns all placements for a given child identifier as a single unpaginated list, sorted with the most recent first. Sort key: actual move-in date if set, otherwise planned move-in date; placements where both are null appear last. The full response MUST be returned in under 500 milliseconds.
- **FR-002**: Each placement MUST include: internal placement identifier, hierarchical service name, status, optional termination reason, and four date fields (planned and actual move-in, planned and actual move-out).
- **FR-003**: Placements without a resolved child linkage MUST NOT be returned in case worker queries.
- **FR-004**: The system MUST support single-placement lookup by internal placement identifier, returning empty when not found or when child linkage is pending.
- **FR-005**: All case worker queries MUST require a valid authenticated session and the caller MUST hold the corresponding access permission (`Tjeneste:HentTjenesterForBarn` or `Tjeneste:HentTjeneste`).
- **FR-006**: The case worker query interface MUST be read-only; no write operations are permitted via this interface.
- **FR-006a**: Every case worker read operation (placement history query and single placement lookup) MUST publish a `LeseloggHendelse` event to the `leselogg` topic on the platform messaging infrastructure. The event MUST use the platform-standard schema with the following fields: `hendelsesId` (unique event UUID), `hendelsesTidspunkt` (ISO 8601 timestamp with timezone), `brukerId` (actor identifier), `barnId` (child identifier), `operasjonNavn` (e.g. `Tjeneste:HentTjenesterForBarn`), `tjenestenavn` (this module's name), `korrelasjonId` (request correlation identifier). No result data is included in the audit event.

**Synkronisering fra BiRK:**

- **FR-007**: The system MUST consume a change stream from BiRK for placements, orders, service types, status types, and termination reason types.
- **FR-008**: Only fields explicitly listed in a whitelist configuration MUST be written to the staging layer; all other fields from BiRK MUST be silently ignored.
- **FR-009**: BiRK field names MUST be translated to M2LB terminology before storage in the domain layer; BiRK names MUST never be exposed outside the staging layer.
- **FR-010**: On first startup, the system MUST perform a full import of all relevant BiRK tables in this order: lookup tables (orders, service types, status types, termination reason types) before placement records.
- **FR-011**: After recovery from downtime, the system MUST support incremental synchronization from the last checkpoint, or full import if the checkpoint has expired.
- **FR-012**: Processing the same change message more than once MUST be idempotent — no duplicate entries, no errors.
- **FR-012a**: If a placement record arrives via the live change stream and references a lookup value (service type, status type, or termination reason) not yet present in the staging layer, the placement message MUST be deferred and automatically retried once the referenced lookup record arrives; it MUST NOT be dropped or stored with a placeholder value.
- **FR-013**: Temporary failures MUST be retried with exponential backoff; messages that exhaust all retries MUST be moved to a dead-letter queue and MUST trigger an operational alert; messages MUST never be silently dropped.

**Asynkron barnkobling:**

- **FR-014**: When a placement arrives, the system MUST attempt to resolve the child identifier from Personmodulen with exponential backoff retries. Only after retries are exhausted (whether child not found or Personmodulen unreachable) MUST the placement be stored with pending child linkage; ingestion MUST NOT fail.
- **FR-015**: The system MUST subscribe to `BarnRegistrert` events from Personmodulen and MUST attempt to link child identifiers for all pending placements matching the BiRK child identifier. Processing a `BarnRegistrert` event for a child whose placements are already fully linked MUST be a silent no-op.
- **FR-016**: When child linkage is resolved, the system MUST publish a `TjenesteOpprettet` event with the complete placement payload. If the publish fails, the child linkage MUST remain committed and the publish MUST be retried with exponential backoff; exhausted retries MUST be dead-lettered and trigger an operational alert.
- **FR-017**: `TjenesteOpprettet` MUST NOT be published until child linkage is confirmed; the event payload MUST always include a resolved child identifier.
- **FR-018**: Once a child identifier is linked to a placement, it MUST NOT be changed or unlinked.
- **FR-019**: When unlinked placements exceed a configured count threshold, the system MUST trigger an operational alert.
- **FR-019a**: Placements that remain unlinked beyond a configurable time deadline MUST be automatically flagged with a dedicated "permanently unresolved" status, making them identifiable for operator review without removing them from the system.

**Internt oppslag for Hendelsestjenesten:**

- **FR-020**: The system MUST expose an internal lookup endpoint that accepts a BiRK Tiltak primary key and returns the internal child identifier and placement identifier.
- **FR-021**: The internal lookup endpoint MUST only be accessible to authorized services via managed system identity; end users MUST NOT have access.
- **FR-022**: When the BiRK key is not found, the endpoint MUST return error code `TILTAK_IKKE_FUNNET`.
- **FR-023**: When the placement exists but child linkage is pending (not yet permanently flagged), the endpoint MUST return error code `BARN_ID_IKKE_KOBLET`, indicating the caller should retry later.
- **FR-023a**: When the placement exists but has been flagged "permanently unresolved" (FR-019a), the endpoint MUST return error code `TILTAK_PERMANENT_UKOBLET`, indicating the caller MUST NOT retry — the linkage will not be resolved automatically.

**Observabilitet og helse:**

- **FR-024**: The system MUST expose a health check endpoint that reports connectivity status for the database and the synchronization component.
- **FR-025**: The health check MUST report the count of placements with pending child linkage.
- **FR-026**: The synchronization layer MUST expose metrics including: messages processed per BiRK table, successful writes, errors and dead-letter events, count of pending child linkages, current checkpoint, and estimated lag.

**Operasjonsregistrering:**

- **FR-027**: On startup, the system MUST publish its access operations to the platform's shared operations registration queue so the authorization module can register them.

### Key Entities

- **Tjeneste (Placement/Service)**: The core domain entity representing one child's placement or service engagement. Key attributes: unique internal identifier, child identifier (nullable until linked), hierarchical service name, status, optional termination reason, four date fields, creation and last-updated timestamps.
- **Lookup tables (from BiRK)**: Service types (with hierarchical name paths), status types, and termination reason types — used during transformation from BiRK data to domain model.
- **TjenesteOpprettet event**: Published when a placement is fully stored and child linkage is confirmed. Contains placement identifier, BiRK Tiltak key, child identifier, service name, and creation timestamp.
- **BarnRegistrert event**: Consumed from Personmodulen. Contains the BiRK child identifier and the internal M2LB child identifier — used to resolve pending child linkages.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Case workers can view a complete, correctly ordered placement history for any child within the bounds of their access permissions in under 500 milliseconds, with no missing or duplicated entries.
- **SC-002**: All placement records in the system accurately reflect the latest known state from BiRK — a change in BiRK is visible in the case worker view within 1–2 minutes of the change occurring.
- **SC-003**: Duplicate change messages from BiRK never result in duplicate placement records or system errors.
- **SC-004**: Placements for children not yet registered in Personmodulen are automatically linked within 1–2 minutes of the child's registration event being received — no manual intervention required.
- **SC-005**: The Hendelsestjenesten can always resolve a BiRK Tiltak to the correct child identifier, or receive a clear signal to retry when linkage is still pending.
- **SC-006**: The health endpoint correctly reflects the system's operational status and the count of pending child linkages at all times.
- **SC-007**: Unauthorized callers (both unauthenticated users and authenticated users lacking the required permission) are consistently rejected from all case worker and internal endpoints.
- **SC-008**: Messages that cannot be processed after all retries are preserved in a dead-letter queue and trigger an operational alert — no data is silently lost.
- **SC-009**: Full synchronization startup completes without errors when lookup tables are loaded before placement records.
- **SC-010**: The `TjenesteOpprettet` event always carries a fully resolved child identifier — a null child identifier is never published.

---

## Assumptions

- The BiRK change stream (CDC) is already available and delivers structured change events for the required tables; the Tjenestemodul is a consumer, not a producer of that stream.
- Personmodulen is an existing service that publishes `BarnRegistrert` events when a new child is registered; the Tjenestemodul subscribes to those events.
- The authorization module is an existing service; the Tjenestemodul registers its operations at startup and delegates authorization decisions to it.
- The platform's managed messaging infrastructure is the delivery mechanism for both event publishing and consumption.
- The health endpoint is unauthenticated and intended for reverse proxy and monitoring systems; no sensitive data is exposed through it.
- A placement (Tjeneste) is never deleted — it can only be deactivated via a status change originating from BiRK.
- The child identifier field on a placement transitions at most once: from pending (null) to a resolved M2LB identifier. Re-linking or unlinking is not a valid operation.
- The BiRK field whitelist and field name mappings are maintained in a configuration file external to the core application logic; changes to mappings do not require code changes.
- Mobile and direct external access to this module are out of scope for M01 — all case worker access is via the presentation layer using GraphQL, all service access is via managed identity.
