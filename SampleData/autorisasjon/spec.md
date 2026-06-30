# Feature Specification: SCIM User Synchronization Adapter

**Feature Branch**: `004-scim-user-sync`  
**Created**: 2026-04-23  
**Status**: Draft  
**Replaces**: Entra Synkroniseringsadapter (Graph Change Notifications approach)

---

## Background & Problem Statement

The M2LB platform uses Azure Entra ID as the authoritative identity system. The Authorization
module must stay current on which users are active (permitted to use M2LB) and which are
inactive (disabled, deleted, or removed from scope). Without this synchronization, a user
deactivated in Entra may retain access in the Authorization module — an active security risk.

The original design bridged this gap by subscribing to Microsoft Graph Change Notifications
(webhooks) and forwarding events to the Authorization module via Azure Service Bus. That design
required managing subscriptions with a 3-day maximum lifetime, renewal logic, webhook endpoint
security (dual-layer validation: access token + clientState), and recovery from subscription
gaps after downtime.

This feature replaces that approach. Instead of the adapter polling or subscribing to Graph,
Azure Entra ID's built-in enterprise application provisioning engine pushes changes using the
**SCIM 2.0 standard** (RFC 7644). The adapter exposes a SCIM-compliant endpoint; Entra drives
all provisioning events to it. The downstream contract — BrukerAktivert / BrukerDeaktivert
events on the `entra.brukere` Service Bus topic — remains identical. Only the inbound
integration mechanism changes.

---

## Clarifications

### Session 2026-04-23

- Q: What persistence medium stores KjentBruker user state? → A: SQL Server via EF Core (new table in existing Authorization module database; requires an EF Core migration on deployment)
- Q: Is the adapter a new project within this solution or a separate repository? → A: New project within this solution (e.g., `Autorisasjon.ScimAdapter` .csproj), sharing EF Core DbContext and Service Bus infrastructure, deployed as a separate container
- Q: What happens if Key Vault is unreachable at adapter startup? → A: Fail fast — adapter refuses to start; container orchestrator handles restart
- Q: Does KjentBruker use the existing AutorisasjonsDbContext or a dedicated DbContext? → A: Shared `AutorisasjonsDbContext` — KjentBruker entity and migration are added to `Autorisasjon.Infrastructure`; `Autorisasjon.ScimAdapter` references `Autorisasjon.Infrastructure`
- Q: Does the adapter call the Authorization module API (FR-017), or is Service Bus its only outbound integration? → A: Service Bus only — FR-017 removed; adapter publishes to Service Bus and reads its own `KjentBruker` table; no Authorization module API calls
- Q: Single instance or multiple replicas? → A: Single instance — no optimistic concurrency required on `KjentBruker`; Entra's retry logic on 5xx provides resilience
- Q: What is the "dead-letter store" in FR-014, and how do FR-013 and FR-014 interact? → A: Service Bus DLQ only — adapter applies a short Polly retry policy on each publish call; exhausted → 5xx to Entra; Azure Monitor alerts on Service Bus DLQ depth; no separate outbox table
- Q: Should the health endpoint include SQL Server connectivity (given new KjentBruker SQL dependency)? → A: Yes — health endpoint reports SCIM reachability, Service Bus connectivity, and SQL Server connectivity
- Q: What should the adapter return if SQL Server is unreachable during a KjentBruker read/write? → A: Return 5xx — symmetric with Service Bus failure; idempotency requires a successful KjentBruker write before acknowledging the request
- Q: What base URL path should the SCIM endpoint expose? → A: `/scim/v2` — SCIM standard convention; operations at `/scim/v2/Users` and `/scim/v2/Users/{id}`
- Q: What PATCH body format does the adapter parse? → A: SCIM PatchOp (RFC 7644 §3.5.2) — `Operations` array with `op`, `path`, `value`; not JSON Merge Patch
- Q: Does GET /Users include inactive (deactivated) KjentBruker records? → A: Yes — all known users, both active and inactive, are returned with their current `active` state; SCIM RFC 7644 §3.4.2 standard behaviour
- Q: Does the synchronous-publish requirement (FR-008) apply to activation events (BrukerAktivert) as well as deactivation? → A: Yes — all Service Bus publishes must complete before HTTP 2xx is returned; async background publish is not permitted for any event type

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 — User Activated When Assigned to M2LB in Entra (Priority: P1)

An IT administrator assigns a user to the M2LB enterprise application in Entra. The
provisioning engine automatically pushes a SCIM Create or Update request to the adapter, which
records the user as active in the Authorization module. The administrator does not need to
perform any additional action in M2LB; the user appears as active within seconds.

**Why this priority**: This is the primary onboarding path. Without it, new employees cannot
be granted roles in M2LB.

**Independent Test**: Assign a user in Entra's enterprise app provisioning scope and verify a
BrukerAktivert event arrives on the Service Bus topic and the user becomes active in the
Authorization module.

**Acceptance Scenarios**:

1. **Given** a user is not yet known to the Authorization module, **When** Entra's provisioning
   engine sends a SCIM `POST /Users` for that user with `active: true`, **Then** the adapter
   publishes a `BrukerAktivert` event on `entra.brukere` containing the user's Entra Object ID.

2. **Given** a user exists in the Authorization module as inactive, **When** Entra's provisioning
   engine sends a SCIM `PATCH /Users/{id}` with `active: true` (e.g., user re-assigned to scope),
   **Then** the adapter publishes a `BrukerAktivert` event.

3. **Given** a SCIM request arrives with a Bearer token that does not match the configured
   provisioning secret, **When** the adapter evaluates the request, **Then** it responds HTTP 401
   and publishes no event.

---

### User Story 2 — User Deactivated When Removed from Entra Scope or Disabled (Priority: P1)

An IT administrator removes a user from the M2LB provisioning scope in Entra, or disables the
user account in Entra. The provisioning engine pushes a SCIM Deactivate or Delete request. The
adapter immediately records the user as inactive in the Authorization module. The deactivation
propagates within Entra's next provisioning cycle.

**Why this priority**: Deactivation is time-critical. A user who is dismissed or has their
account disabled must lose access promptly — a delayed deactivation is a security risk.

**Independent Test**: Disable a user or remove them from provisioning scope in Entra; verify a
BrukerDeaktivert event arrives on the Service Bus topic.

**Acceptance Scenarios**:

1. **Given** a user is active in the Authorization module, **When** Entra sends
   `PATCH /Users/{id}` with `active: false` (account disabled or removed from scope),
   **Then** the adapter publishes a `BrukerDeaktivert` event without unnecessary delay.

2. **Given** a user is active in the Authorization module, **When** Entra sends
   `DELETE /Users/{id}` (user deleted from directory), **Then** the adapter publishes a
   `BrukerDeaktivert` event.

3. **Given** the same deactivation SCIM request is delivered twice (Entra retries), **When** the
   adapter processes the second delivery, **Then** no duplicate event is published and no error
   is returned — idempotent processing.

---

### User Story 3 — Initial Full Synchronization on Adapter Startup (Priority: P2)

When the adapter starts for the first time, or after extended downtime, Entra's provisioning
engine performs a full reconciliation by sending paginated SCIM `GET /Users` requests. The
adapter responds with its current known users. Entra then reconciles its provisioning scope
against the adapter's response and pushes any missing create/update/delete events. This ensures
no users are stale or missing after a downtime gap.

**Why this priority**: Full sync is the safety net against missed events during downtime. It is
less time-critical than live event processing but essential for operational correctness.

**Independent Test**: Clear the adapter's known users, restart it, and verify Entra's next
provisioning cycle re-provisions all in-scope users into the Authorization module.

**Acceptance Scenarios**:

1. **Given** the adapter responds to `GET /Users` with an empty list, **When** Entra's
   provisioning engine compares this against its in-scope user set, **Then** Entra issues
   `POST /Users` for each in-scope user, and the adapter publishes corresponding
   `BrukerAktivert` events.

2. **Given** the adapter responds to `GET /Users` with a list that includes users no longer
   in Entra's scope, **When** Entra reconciles, **Then** Entra issues `DELETE /Users/{id}`
   or a deactivating `PATCH` for those users, and the adapter publishes `BrukerDeaktivert`
   events for each.

3. **Given** a full-sync cycle is in progress with a large user set, **When** the same user
   appears across paginated responses, **Then** the adapter handles duplicates idempotently
   without publishing duplicate events.

---

### User Story 4 — Operations Team Monitors Provisioning Health (Priority: P3)

An operations engineer can confirm that the SCIM endpoint is reachable, that the Entra
provisioning cycle is completing successfully, and that dead-lettered events are visible and
alerting. They do not need to dig through application logs to determine whether synchronization
is working.

**Why this priority**: Observability is operationally necessary but does not block the core
synchronization path.

**Independent Test**: Trigger a SCIM request and verify structured log entries and metrics are
produced; check that a health endpoint returns a meaningful status.

**Acceptance Scenarios**:

1. **Given** the adapter is running, **When** an operations engineer calls the health endpoint,
   **Then** the response indicates SCIM endpoint availability, Service Bus connectivity, and
   SQL Server connectivity.

2. **Given** a SCIM request results in a failed Service Bus publish after all retries, **When**
   the event is dead-lettered, **Then** an operational alert is raised and the event is
   traceable in the dead-letter queue.

3. **Given** normal operation, **When** SCIM requests arrive, **Then** structured log entries
   record event type, Entra Object ID (masked or partial), outcome, and duration for each
   request.

---

### Edge Cases

- What happens when Entra sends `POST /Users` for a user already known as active? (No duplicate
  event published; HTTP 200/201 returned — idempotent.)
- What happens when the Service Bus is temporarily unavailable? (Adapter returns HTTP 5xx to
  Entra; Entra's provisioning engine retries on its own schedule — no event is silently dropped.)
- What happens when Entra sends `PATCH` with `active: false` for a user not yet known to the
  adapter? (Publish `BrukerDeaktivert` anyway — deactivation is always safe to apply and
  the Authorization module handles it idempotently.)
- What happens when the SCIM payload contains unknown attributes? (Unknown attributes are
  ignored; only `id`, `externalId`, and `active` are meaningful to the adapter.)
- What happens when Entra sends a `GET /Users` with filter expressions during reconciliation?
  (The adapter supports at least `filter=externalId eq "..."` and `filter=userName eq "..."`
  as required by Entra's provisioning protocol.)
- What happens when the provisioning secret is rotated? (A restart is required to pick up the
  new secret; this is acceptable and must be documented in the runbook.)
- What happens if Key Vault is unreachable when the adapter starts? (The adapter fails fast —
  it refuses to start if the provisioning secret cannot be retrieved. The container orchestrator
  handles the restart. No SCIM endpoint is exposed without a valid secret loaded.)
- What happens if SQL Server is unreachable during a SCIM request? (The adapter returns HTTP
  5xx to Entra. A request is never acknowledged with HTTP 2xx unless the `KjentBruker` write
  succeeds — partial processing would silently break idempotency.)

---

## Requirements *(mandatory)*

### Functional Requirements

**SCIM Endpoint**

- **FR-001**: The adapter MUST expose a SCIM 2.0–compliant HTTP endpoint (RFC 7644) at base
  path `/scim/v2`. The IT administrator configures this base URL in Entra's enterprise
  application provisioning settings.
- **FR-002**: The endpoint MUST support `POST /scim/v2/Users`, `GET /scim/v2/Users`,
  `GET /scim/v2/Users/{id}`, `PATCH /scim/v2/Users/{id}`, and `DELETE /scim/v2/Users/{id}`
  operations as required by Entra's SCIM provisioning client. `PATCH` requests use SCIM PatchOp
  format (RFC 7644 §3.5.2): `{"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
  "Operations":[{"op":"Replace","path":"active","value":false}]}`. JSON Merge Patch is not used.
- **FR-003**: The endpoint MUST authenticate all incoming requests using a Bearer token
  (provisioning secret). Requests with a missing or incorrect Bearer token MUST be rejected
  with HTTP 401 without processing.
- **FR-004**: The SCIM endpoint MUST be publicly reachable via the platform gateway layer,
  in the same manner as the original webhook endpoint was exposed.
- **FR-005**: The endpoint MUST support paginated responses for `GET /Users` using the SCIM
  `startIndex` and `count` parameters, and the `filter` query parameter for at least
  `externalId` and `userName` equality filters.

**Event Translation**

- **FR-006**: The adapter MUST translate an inbound SCIM user-create or user-activate event
  (`active: true`) into a `BrukerAktivert` event published on the `entra.brukere` Service Bus
  topic, preserving the existing event contract.
- **FR-007**: The adapter MUST translate an inbound SCIM user-deactivate event (`active: false`)
  or user-delete event into a `BrukerDeaktivert` event published on the `entra.brukere` Service
  Bus topic, preserving the existing event contract.
- **FR-008**: All Service Bus events — both `BrukerAktivert` and `BrukerDeaktivert` — MUST be
  published before the HTTP 200/204 response is returned to Entra. Asynchronous background
  publishing is not permitted. This ensures Entra's provisioning engine only considers a
  provisioning action confirmed when the downstream event is durably queued, and prevents
  silent publish failures from leaving `KjentBruker` state inconsistent with the event stream.
- **FR-009**: Each published event MUST include: a unique `HendelsesId` (UUID v4), the user's
  Entra Object ID (`EntraObjectId`), a UTC timestamp, and a source reference tying the event
  to the originating SCIM request.

**Idempotency**

- **FR-010**: All SCIM operations MUST be processed idempotently. Repeated delivery of an
  identical request (same user, same active state) MUST NOT result in duplicate events or
  errors.
- **FR-011**: The adapter MUST track which Entra Object IDs are known and their last-seen
  active/inactive state, to detect and suppress no-op state transitions.

**Full Synchronization Support**

- **FR-012**: The adapter MUST respond correctly to Entra's `GET /Users` reconciliation
  queries, returning all currently known `KjentBruker` records — both active and inactive — in
  SCIM list response format with pagination. Each record is returned with its current `active`
  state, allowing Entra to reconcile its provisioning scope against the adapter's full known-user
  set (RFC 7644 §3.4.2).

**Reliability & Error Handling**

- **FR-013**: If the Service Bus is temporarily unavailable when processing an inbound SCIM
  request, the adapter MUST apply a short Polly retry policy on the publish call (e.g., 3
  attempts with exponential backoff). If all retries are exhausted within the request, the
  adapter MUST return a retryable HTTP error (5xx) to Entra rather than acknowledging with
  HTTP 2xx. This delegates retry scheduling to Entra's provisioning engine.
- **FR-014**: The Service Bus dead-letter sub-queue is the durable store for events that cannot
  be delivered to consumers. An Azure Monitor alert MUST be configured on the dead-letter queue
  depth. No separate outbox or `FailedEvent` database table is required.
- **FR-015**: A processing failure for one SCIM request MUST NOT prevent the adapter from
  processing subsequent requests.

**Security**

- **FR-016**: The provisioning secret (Bearer token) MUST be stored in Azure Key Vault and
  retrieved via Managed Identity — never in configuration files or source code.
- **FR-018**: The provisioning secret MUST NOT appear in logs, metrics, or health endpoint
  responses.
- **FR-022**: If the provisioning secret cannot be retrieved from Key Vault at startup, the
  adapter MUST fail fast (abort startup with a fatal log entry) rather than start in a degraded
  state. No SCIM endpoint is exposed until a valid secret is loaded.
- **FR-023**: If SQL Server is unreachable when processing an inbound SCIM request (e.g., during
  a `KjentBruker` read or write), the adapter MUST return a retryable HTTP error (5xx) to Entra.
  A SCIM request MUST NOT be acknowledged as successful unless the `KjentBruker` state transition
  is durably written — partial processing would break idempotency guarantees (FR-010).

**Observability**

- **FR-019**: The adapter MUST produce structured log entries for every SCIM request received,
  every Service Bus event published, and every error encountered.
- **FR-020**: The adapter MUST expose operational metrics: count of SCIM requests per operation
  type, count of events published per event type, count of dead-letter events, and Service Bus
  connectivity status.
- **FR-021**: The adapter MUST expose a health endpoint indicating SCIM endpoint reachability,
  Service Bus connectivity, and SQL Server connectivity (via `AddDbContextCheck<AutorisasjonsDbContext>()`).

### Key Entities

- **ScimUser**: A user record as seen by the SCIM endpoint. Key attributes: `id` (Entra Object
  ID), `externalId`, `userName`, `active`. Only `id` and `active` are forwarded downstream;
  no display information (name, email) is retained.
- **BrukerAktivert / BrukerDeaktivert event**: The downstream event contract consumed by the
  Authorization module — unchanged from the original adapter. Fields: `HendelsesId`,
  `HendelsesType`, `EntraObjectId`, `Tidsstempel`, `KildeReferanse`.
- **KjentBruker (Known User)**: The adapter's minimal persistent record of users it has
  synchronized, used to detect no-op transitions and to answer Entra's `GET /Users`
  reconciliation queries. Fields: Entra Object ID, last-seen active state, last-updated
  timestamp. Persisted via the shared `AutorisasjonsDbContext` in `Autorisasjon.Infrastructure`
  (new `KjentBrukere` table, same SQL Server database); an EF Core migration is required on
  initial deployment. `Autorisasjon.ScimAdapter` references `Autorisasjon.Infrastructure` to
  access the DbContext.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A user assigned to the M2LB provisioning scope in Entra becomes active in the
  Authorization module within Entra's provisioning cycle time (typically under 40 seconds for
  incremental sync).
- **SC-002**: A user disabled or removed from scope in Entra is deactivated in the
  Authorization module within Entra's next provisioning cycle, with no manual intervention.
- **SC-003**: All SCIM operations are processed idempotently — submitting the same SCIM
  payload 5 times results in exactly 1 downstream event published, verified in integration
  testing.
- **SC-004**: Full reconciliation by Entra (`GET /Users` cycle) completes without timeout or
  error for a directory of up to 500 users.
- **SC-005**: No provisioning event is silently discarded — every failed Service Bus publish
  results in a dead-lettered entry and an observable operational alert.
- **SC-006**: The new adapter has zero subscription management concerns (no renewal jobs,
  no subscription TTL tracking, no clientState secrets) compared to the original design,
  reducing ongoing operational overhead.

---

## Assumptions

- Azure Entra ID's built-in enterprise application provisioning engine is used to configure
  the SCIM endpoint URL and provisioning secret. IT administration performs this configuration
  in the Azure portal — the adapter does not self-register with Entra.
- The provisioning scope in Entra (which users are synchronized) replaces the M2LB Security
  Group membership model from the original design. Scope may be defined via Entra groups or
  attribute-based filters configured in the enterprise app — this is an IT administration
  concern, not an adapter concern.
- The downstream Service Bus event contract (`BrukerAktivert` / `BrukerDeaktivert` on
  `entra.brukere`) is unchanged. The Authorization module requires no changes.
- Entra's provisioning engine handles its own retry schedule for failed SCIM requests (it
  retries on 5xx responses). The adapter does not implement a retry loop for inbound SCIM
  requests — only for outbound Service Bus publishes.
- Organizational unit (OrgEnhet) synchronization remains out of scope for this adapter, as
  in the original design.
- The adapter is a separate deployable service from the Authorization module, implemented as
  a new project (`Autorisasjon.ScimAdapter`) within this solution. It shares the existing
  EF Core `AutorisasjonsDbContext` and Service Bus infrastructure, and is deployed as a
  separate container alongside the Authorization module. The adapter runs as a single replica;
  no optimistic concurrency is required on `KjentBruker`. Entra's 5xx retry behaviour provides
  resilience in lieu of horizontal redundancy.
- Entra provisioning cycles for incremental changes typically complete within 20–40 seconds.
  Full sync cycles run on a configurable Entra-side interval (default: 40 minutes).
- Provisioning secret rotation is an infrequent operational event; a restart to pick up a
  rotated secret is acceptable if documented in the runbook.
