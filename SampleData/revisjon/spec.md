# Feature Specification: M2LB.Revisjon M01 — Receiving and storing leselogg events

**Feature Branch**: `001-M2LB.Revisjon-m01`
**Created**: 2026-04-27
**Status**: Draft

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Source service delivers a leselogg event (Priority: P1)

A source service (e.g. Henvisningstjenesten) performs a child-specific lookup where `barnId`
is an input parameter. After access is confirmed and data is retrieved, the service publishes
a `LeseloggHendelse` to the Service Bus queue `leselogg`. M2LB.Revisjon consumes the
event and writes it unchanged to Azure Immutable Blob Storage under the correct file path.

**Why this priority**: This is the core purpose of M2LB.Revisjon. Without this flow,
PP-03 and PP-10 are incompletely implemented and the platform is not compliant with GDPR and
Barnevernloven. All other user stories depend on this baseline flow working correctly.

**Independent Test**: Can be fully tested by publishing a valid `LeseloggHendelse` to the
Service Bus queue and verifying that the correct JSON file appears in Blob Storage under the
expected file path. Delivers standalone value as the technical foundation for statutory
auditability.

**Acceptance Scenarios**:

1. **Given** a valid `LeseloggHendelse` with a unique `hendelsesId` is published to the
   `leselogg` queue,
   **When** M2LB.Revisjon processes the event,
   **Then** the file `{year}/{month}/{day}/{hendelsesId}.json` exists in Blob Storage with
   content identical to the received event (no transformation).

2. **Given** a valid event has been processed,
   **When** the event is acknowledged to Service Bus,
   **Then** the event is removed from the queue.

3. **Given** an event with `HendelsesTidspunkt = 2026-03-12T10:23:45.123+01:00` and
   `HendelsesId = 550e8400-e29b-41d4-a716-446655440000`,
   **When** the event is written to Blob Storage,
   **Then** the file path is `2026/03/12/550e8400-e29b-41d4-a716-446655440000.json`.

---

### User Story 2 — Idempotent handling of a duplicate event (Priority: P1)

Azure Service Bus delivers events at least once. Under network failures, timeouts, or
restarts, the same `LeseloggHendelse` may be delivered twice. M2LB.Revisjon must handle
this such that duplicate delivery does not create extra files, return errors, or block
processing of subsequent events.

**Why this priority**: Idempotency is a non-negotiable constitutional obligation (Principle
III). Without correct idempotency implementation the service violates PP-03 and risks
double-storage or spurious error logging. Rated P1 alongside US1 because they belong to the
same implementation unit.

**Independent Test**: Can be tested by sending the same event twice to the queue and
verifying that exactly one file exists in Blob Storage and both deliveries are acknowledged
without error.

**Acceptance Scenarios**:

1. **Given** a `LeseloggHendelse` with an already stored `hendelsesId` is received again,
   **When** M2LB.Revisjon attempts to write with `IfNoneMatch: *`,
   **Then** Blob Storage returns HTTP 412, the event is acknowledged to Service Bus without
   error, and no new file is created.

2. **Given** two instances process the same event simultaneously,
   **When** both attempt to write with `IfNoneMatch: *`,
   **Then** one succeeds (HTTP 201) and the other receives HTTP 412 — both acknowledge to
   Service Bus, and exactly one file exists in Blob Storage.

---

### User Story 3 — Failures and unavailable infrastructure are handled without event loss (Priority: P1)

During transient unavailability (Blob Storage failure, network interruption) or invalid
message format, M2LB.Revisjon must never silently discard events. Valid events are
retried until infrastructure is available again. Invalid events are routed to the dead letter
queue with an error description, and the operations team is alerted.

**Why this priority**: Loss of audit events is not acceptable (constitution section 5).
Legal and regulatory requirements demand that every event either succeeds or is explicitly
escalated to manual handling.

**Independent Test**: Can be tested by sending an invalid JSON message to the queue and
verifying that it lands in the dead letter queue, and by simulating a Blob Storage failure
and verifying that the event is retried and eventually written correctly.

**Acceptance Scenarios**:

1. **Given** a message that cannot be deserialised to `LeseloggHendelse` is received,
   **When** M2LB.Revisjon processes it,
   **Then** the message is moved to the dead letter queue, the error reason is logged, and
   subsequent events are processed independently (not blocked).

2. **Given** Blob Storage is temporarily unavailable,
   **When** a valid event is received,
   **Then** the service retries in-process with exponential backoff; if all in-process retries
   are exhausted the message is abandoned to Service Bus for redelivery; the file is
   eventually written correctly once Blob Storage is available again.

3. **Given** an event has reached the maximum delivery count (10),
   **When** Service Bus moves the event to the dead letter queue,
   **Then** an operational alert is generated requiring manual follow-up.

---

### User Story 4 — Operations team monitors service availability (Priority: P2)

The operations team needs to know whether M2LB.Revisjon is running and whether its
dependencies (Service Bus and Blob Storage) are available, so that any issues can be detected
and escalated promptly.

**Why this priority**: Availability monitoring is a platform requirement (PS-08) and
necessary to meet the non-functional requirement that unavailability beyond short periods
constitutes an operational incident. Lower priority than the P1 stories because it does not
block core functionality.

**Independent Test**: Can be tested by calling `GET /health` and verifying that the response
reflects the actual availability of Service Bus and Blob Storage.

**Acceptance Scenarios**:

1. **Given** the service is running and both probe operations succeed,
   **When** `GET /health` is called,
   **Then** HTTP 200 is returned with
   `{ "status": "Healthy", "checks": { "serviceBus": "Healthy", "blobStorage": "Healthy" } }`.

2. **Given** the Blob Storage probe fails,
   **When** `GET /health` is called,
   **Then** HTTP 200 is returned with
   `{ "status": "Degraded", "checks": { "serviceBus": "Healthy", "blobStorage": "Unhealthy" } }`.

3. **Given** the service is unavailable,
   **When** `GET /health` is called,
   **Then** HTTP 503 is returned.

4. **Given** an external request reaches the reverse proxy targeting `/health`,
   **When** the request is routed,
   **Then** HTTP 404 is returned — the endpoint is not exposed externally.

---

### User Story 5 — Legal owner can document that the audit trail is legally valid (Priority: P2)

The legal owner and data protection officer at Bufdir need to be able to document that the
leselogg satisfies the requirements of GDPR and Barnevernloven — that it is immutable, that
it contains no personal data, and that its structure supports future access requests.

**Why this priority**: A regulatory prerequisite for platform operation, but verified
primarily through infrastructure configuration (WORM policy) and the technical architecture
— not through new functionality in M01 beyond what is already covered by US1–US4.

**Independent Test**: Can be verified by attempting to overwrite an existing blob (must fail
with HTTP 412) and by confirming that the event payload contains no personal data such as
names or national identity numbers.

**Acceptance Scenarios**:

1. **Given** a file has been written to Immutable Blob Storage,
   **When** an attempt is made to overwrite the file,
   **Then** the attempt fails with HTTP 412 (WORM policy is enforced).

2. **Given** a `LeseloggHendelse` is stored in Blob Storage,
   **When** the file content is inspected,
   **Then** it contains only UUID identifiers and metadata — never names, national identity
   numbers, addresses, or any other directly identifying personal information.

---

### Edge Cases

- What happens when `HendelsesTidspunkt` falls around midnight with a timezone offset?
  → The timestamp is normalised to UTC before the date is extracted.
  `2026-03-12T00:05:00.000+01:00` → UTC `2026-03-11T23:05:00Z` → stored under `2026/03/11/`.
- What happens during a race condition between two instances with the same `hendelsesId`?
  → `IfNoneMatch: *` ensures exactly one succeeds (HTTP 201); the other treats HTTP 412 as
  successful idempotency.
- What happens on a deserialisation error where only one required field is missing?
  → The event is routed to the dead letter queue — not retried, as this is a permanent error.
- What happens under very high volume (a burst of events)?
  → Service Bus's built-in buffering absorbs the burst; the service is stateless and can
  scale horizontally.
- What if Managed Identity loses permission to Blob Storage?
  → The health check reports `Degraded`; writes fail and events accumulate in the Service Bus
  queue.

---

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The service MUST continuously consume all messages from Azure Service Bus queue
  `leselogg` in accordance with at-least-once delivery semantics. When hosted on App Service,
  Always-On MUST be enabled to prevent the runtime from idling and halting consumption.

- **FR-002**: The service MUST deserialise incoming messages into the `LeseloggHendelse`
  structure defined in GL-32. Messages that cannot be deserialised MUST be routed to the dead
  letter queue with an error description without blocking subsequent messages.

- **FR-003**: The service MUST write valid `LeseloggHendelse` objects as one JSON file per
  event to Azure Immutable Blob Storage under the file path
  `{year}/{month}/{day}/{hendelsesId}.json`, where the date is derived from
  `HendelsesTidspunkt` normalised to UTC. Example: `2026-03-12T00:05:00.000+01:00`
  → UTC `2026-03-11T23:05:00Z` → path `2026/03/11/{hendelsesId}.json`.

- **FR-004**: The file written to Blob Storage MUST be identical to the event as received
  from Service Bus — no transformation or enrichment is performed.

- **FR-005**: The service MUST implement idempotent writes based on `hendelsesId` using
  `IfNoneMatch: *`. HTTP 412 from Blob Storage MUST always be treated as successful
  idempotency — the event is acknowledged to Service Bus without error.

- **FR-006**: The service MUST implement an in-process retry loop with exponential backoff
  for transient Blob Storage failures before abandoning a message. After exhausting in-process
  retries, the message is abandoned to Service Bus, which redelivers it up to the configured
  `MaxDeliveryCount` (10) before routing to the dead letter queue. The in-process retry count
  and backoff intervals are implementation details determined during planning.

- **FR-007**: The service MUST log a structured error entry whenever a message is moved to
  the dead letter queue. An Azure Monitor alert rule (configured in infrastructure) fires
  when the DLQ message count exceeds zero and notifies the operations team. The service has
  no direct responsibility for the notification channel beyond the structured log entry.

- **FR-008**: The service MUST expose an HTTP health-check endpoint (`GET /health`) that
  performs probe operations against Service Bus (`GetQueueRuntimePropertiesAsync`) and Blob
  Storage (`GetPropertiesAsync`). The endpoint MUST NOT be exposed via the YARP reverse proxy.

- **FR-009**: The service MUST log the number of events processed, duplicates ignored, errors,
  and dead letter events to Azure Monitor via structured logging. Each log entry produced
  during the processing of a `LeseloggHendelse` MUST carry the `korrelasjonId` from that
  event as the structured logging correlation key, enabling end-to-end trace correlation with
  the source service that published the event.

- **FR-010**: The service MUST authenticate against Service Bus and Blob Storage exclusively
  via Azure Managed Identity — no connection strings or static keys in configuration.

- **FR-011**: The service MUST NOT have an Azure SQL database or any other relational
  database. Azure Immutable Blob Storage is the sole persistence mechanism.

- **FR-012**: The service MUST run in the Norway East region.

- **FR-013**: The WORM retention period is configured in infrastructure — not in service code.
  Placeholder: 10 years. To be confirmed with the legal owner (Å-01).

- **FR-014**: The service MUST NOT publish an operation registration message to
  Autorisasjonsmodulen at startup in M01. Registration is deferred to a future milestone
  when user-facing operations are defined.

### Key Entities

- **LeseloggHendelse**: The only data structure the service interacts with. Owned by the
  platform (defined in GL-32). Fields: `hendelsesId` (UUID v4), `hendelsesTidspunkt`
  (ISO 8601 with timezone), `brukerId` (UUID v4), `barnId` (UUID v4), `operasjonNavn`
  (String), `tjenestenavn` (String), `korrelasjonId` (UUID v4). Never contains personal data
  such as names, national identity numbers, or addresses.

- **Outbox row**: Does NOT exist in M2LB.Revisjon. The outbox pattern (GL-33) applies
  to source services — not to M2LB.Revisjon, which has no SQL database.

- **Dead letter queue**: Service Bus's built-in mechanism for events that cannot be processed
  after exhausted in-process retry and Service Bus redelivery. Actively monitored via an
  Azure Monitor alert rule on DLQ message count — any event here requires manual handling.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All valid `LeseloggHendelse` messages from the `leselogg` queue are written to
  Blob Storage — zero event loss under normal operation.

- **SC-002**: 100 events published to the queue result in exactly 100 files in Blob Storage
  with correct file paths and unchanged content — verified by TEST-E-02.

- **SC-003**: An event delivered twice (duplicate) results in exactly one file in Blob Storage
  and no error status — idempotency is maintained for all delivery scenarios including race
  conditions between concurrent instances.

- **SC-004**: Messages that cannot be deserialised do not halt processing of subsequent
  messages — the service continues without interruption.

- **SC-005**: The health-check endpoint reflects the actual availability of both dependencies
  and detects Managed Identity permission misconfigurations (TEST-E-05).

- **SC-006**: The audit trail is legally valid — WORM policy prevents any overwrite of stored
  files (TEST-I-02).

- **SC-007**: The service scales horizontally without coordination between instances, owing to
  stateless design and correct idempotency implementation.

- **SC-008**: The service handles the expected load of hundreds of events per day and bursts
  up to a few thousand without message loss, increased DLQ rate, or degraded health-check
  status. A single instance is sufficient under normal load; horizontal scaling is available
  without code changes.

## Assumptions

- The service is write-only in M01. Search, filtering, and exposure of the audit trail are
  future scope and are not part of this specification.
- Autorisasjonsmodulen does not publish to the `leselogg` queue — it has its own internal
  audit trail for access decisions and administrator actions (ADR-003).
- All source services comply with GL-32 and publish events without personal data.
  M2LB.Revisjon performs no content validation and stores only what it receives.
- Startup registration with Autorisasjonsmodulen (GL-09) is **not** implemented in M01.
  The service has no user-facing operations to register, so publishing an empty list adds a
  boot-time dependency with no value. Registration is deferred to a future milestone when
  user-facing operations exist.
- `MaxDeliveryCount` (10 attempts) is configured on the Service Bus queue in infrastructure,
  not in service code.
- **Hosting (Å-03 resolved):** The service is deployed as a .zip file to an Azure Web App
  (App Service) as an interim solution. Migration to Azure Container App is planned once the
  current platform blocker is resolved. The `/health` endpoint is consumed by App Service's
  built-in health-check probe; it remains inaccessible via the YARP reverse proxy (FR-008).
- **App Service Always-On:** The App Service plan MUST have Always-On enabled. Without it,
  the runtime idles after ~20 minutes of no HTTP traffic and stops the Worker Service,
  violating FR-001. This is an infrastructure configuration requirement, not a service code
  concern. When the service is migrated to Azure Container App, this constraint no longer
  applies.
- WORM retention period to be confirmed with the legal owner (Å-01, placeholder: 10 years).
- **DLQ alerting (Å-02 resolved):** An Azure Monitor alert rule on the DLQ message count
  handles operational notification. The service emits a structured log entry on DLQ routing;
  the alert rule and recipient configuration are owned by the operations team in infrastructure.
- The date component of the filename pattern (`{year}/{month}/{day}/`) is derived from
  `HendelsesTidspunkt` normalised to UTC.
- Expected event volume is low: hundreds of events per day with occasional bursts up to a
  few thousand. A single App Service instance is sufficient for M01; the stateless design
  allows scale-out without code changes if volume grows.

## Clarifications

### Session 2026-04-27

- Q: Hosting strategy (Å-03) — Azure Container App vs. Azure Function? → A: Azure Web App
  (App Service) via .zip deploy as interim; planned migration to Azure Container App once
  the current platform blocker is resolved.
- Q: Expected event volume? → A: Low — hundreds of events per day, occasional bursts up to
  a few thousand.
- Q: Dead letter queue alerting mechanism (Å-02)? → A: Azure Monitor alert rule on DLQ
  message count > 0; service responsibility ends at routing to DLQ and logging the event.
- Q: Startup registration with Autorisasjonsmodulen (GL-09) — required in M01? → A: Skip;
  deferred until the service has actual user-facing operations to register.
- Q: Date component of file path — UTC or local date from `HendelsesTidspunkt` offset? →
  A: UTC — normalise `HendelsesTidspunkt` to UTC before extracting the date component.
  `2026-03-12T00:05:00.000+01:00` → UTC `2026-03-11T23:05:00Z` → path date `2026/03/11`.
- Q: Retry strategy for transient Blob Storage failures — in-process or Service Bus
  redelivery only? → A: In-process retry first (exponential backoff), then abandon to
  Service Bus; `MaxDeliveryCount` acts as the outer safety net.
- Q: App Service Always-On — required for continuous Service Bus consumption (FR-001)? →
  A: Yes — Always-On MUST be enabled; without it the Worker Service idles and stops
  consuming from the queue, violating FR-001.
- Q: `KorrelasjonId` source for structured logging (FR-009) — event field or new ID per
  delivery? → A: Use the `korrelasjonId` from the `LeseloggHendelse` being processed, so
  log entries can be correlated end-to-end with the source service.
