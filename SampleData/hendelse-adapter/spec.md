# Feature Specification: BiRK Hendelsesadapter

**Feature Branch**: `001-birk-hendelse-adapter`
**Created**: 2026-05-05
**Status**: Draft
**Input**: Build an adapter that integrates with Hendelsestjenesten by reading event data from BiRK (via Azure Event Hubs CDC stream), translating it to Hendelsestjenesten's format, and delivering it via the ingestion API.

---

## Clarifications

### Session 2026-05-05

- Q: How is the code mapping table (BiRK numeric codes → M2LB UUIDs) sourced? → A: Static config file (JSON/YAML) loaded at startup; startup fails fast if any mapping is missing.
- Q: Where is the Event Hubs progress marker (checkpoint) stored? → A: Azure Blob Storage via the SDK's built-in `BlobCheckpointStore` (Azure.Messaging.EventHubs.Processor).
- Q: What is the error queue backend for messages exceeding max retries? → A: Azure Service Bus queue (dedicated error queue); the operations team reprocesses by re-queuing messages.
- Q: If the historical load fails partway through, does it checkpoint progress or restart from the beginning? → A: Restart from the beginning; Hendelsestjenesten's idempotency guarantees safe re-delivery of already-processed records.
- Q: What is the expected event volume? → A: Low volume — hundreds of events per day.
- Q: How does the adapter read historical BiRK records for the initial full load? → A: Replay from the beginning of the Event Hubs stream on first startup (full stream retention required).
- Q: Where is the delivered-event tracking store (FR-016, `BirkHendelsesId` → `HendelsesId` map) persisted? → A: Azure SQL / SQL Server table in an adapter-owned schema, persisted across restarts.
- Q: How should CDC DELETE operations on BiRK records be handled? → A: Log and discard — only INSERT and UPDATE events are translated and delivered; DELETEs are not forwarded.
- Q: Which observability backend receives the FR-017 operational metrics? → A: OpenTelemetry instrumentation with export to Azure Monitor (Application Insights).
- Q: What are the retry policy defaults (max retries, initial/max delay)? → A: 10 retries, initial delay 5 s, max delay 5 min (exponential backoff; covers ~30 min outage per SC-006).
### Session 2026-05-06

- Q: What is stored in the Delivered Event Registry — does the ingestion API return a platform HendelsesId? → A: Yes; the API response contains a platform-assigned `HendelsesId` UUID which the adapter stores alongside `BirkHendelsesId`.
- Q: FR-013 prohibits storing event data beyond progress marker and error queue — does the Delivered Event Registry contradict this? → A: Amend FR-013 to explicitly permit all three stores: progress marker, error queue, and Delivered Event Registry.
- Q: Should FR-014 explicitly cover Azure SQL and Service Bus authentication? → A: The adapter MUST NOT store credentials for any connection — principle applies to all services without enumeration.
- Q: Where does the adapter process run? → A: Azure Container App — managed container runtime with native Managed Identity and built-in ingress for the health check endpoint.
- Q: Should Azure SQL (Delivered Event Registry) be part of the startup readiness check? → A: Yes — block stream processing until Azure SQL is reachable, consistent with FR-009.

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Continuous Event Stream Processing (Priority: P1)

The adapter runs continuously, reading new changes from the BiRK change-data-capture stream in Azure Event Hubs. For each change to the `TvangsProtokoll` or `Rømming` tables, the adapter translates the record to Hendelsestjenesten's format and delivers it via the ingestion API. The adapter resolves the child (`BarnId`) by looking up `BirkTiltakPK` against Tjeneste. After delivery, the adapter marks its position in the stream so it can resume correctly after a restart.

**Why this priority**: This is the adapter's core operational mode. Without it, no BiRK events reach the platform in real time.

**Independent Test**: Can be fully tested by publishing a test CDC message to an Event Hub and verifying that a corresponding event appears in Hendelsestjenesten with the correct field values.

**Acceptance Scenarios**:

1. **Given** a new `TvangsProtokoll` record appears in the Event Hubs stream, **When** the adapter processes it, **Then** a `Inngrep` event is created in Hendelsestjenesten with all mandatory fields correctly populated.
2. **Given** a new `Rømming` record with `RommingKategoriType = 1` appears, **When** the adapter processes it, **Then** a `Uteblivelse` event is created with the correct `HendelsesTypeId` and `RommingsDetalj`.
3. **Given** a new `Rømming` record with `RommingKategoriType = 2` appears, **When** the adapter processes it, **Then** a `Rømming` event is created.
4. **Given** a new `Rømming` record with `RommingKategoriType = 3` appears, **When** the adapter processes it, **Then** a `Bortføring` event is created.
5. **Given** the adapter is restarted, **When** it starts up again, **Then** it resumes from where it left off without re-processing already-delivered events or skipping new ones.

---

### User Story 2 — Child Resolution via Tjeneste Lookup (Priority: P1)

When the adapter processes a BiRK event, it must identify which child the event concerns. BiRK stores only a `BirkTiltakPK` (numeric key) on the event. The adapter performs a synchronous lookup against Tjeneste to resolve this to a `BarnId` (UUID) and a `TjenesteId` (UUID) for the ingestion request.

**Why this priority**: Without child resolution, events cannot be linked to the correct child record in the platform. This is a core requirement for data integrity.

**Independent Test**: Can be tested independently by calling the adapter's lookup logic with a known `BirkTiltakPK` and verifying it returns the expected `BarnId` and `TjenesteId`. A stub for Tjeneste can be used.

**Acceptance Scenarios**:

1. **Given** a BiRK event with `BirkTiltakPK = 12345`, **When** Tjeneste returns a matching `BarnId` and `TjenesteId`, **Then** the event is delivered to Hendelsestjenesten with both fields populated.
2. **Given** a BiRK event with `BirkTiltakPK = 99999`, **When** Tjeneste returns no match, **Then** the event is delivered to Hendelsestjenesten with `BirkTiltakPK` set and `BarnId = null`. The adapter does not wait or retry for the missing child link.
3. **Given** Tjeneste is temporarily unavailable, **When** the adapter attempts a lookup, **Then** delivery is retried with increasing delays until the configured maximum, after which the message is moved to the error queue and an operational alert is triggered.

---

### User Story 3 — Full Historical Load on First Startup (Priority: P2)

On first startup (or after a full reset), the adapter reads all existing `TvangsProtokoll` and `Rømming` records from BiRK and delivers them to Hendelsestjenesten. This ensures that historical events are available on the platform before real-time processing begins.

**Why this priority**: Required for the platform to have a complete view of historical events when going live. Without it, the platform would only have events from the point the adapter started.

**Independent Test**: Can be tested by running the adapter against a test Event Hub seeded with known historical CDC events from the earliest offset, verifying that all records appear in Hendelsestjenesten after the initial replay completes.

**Acceptance Scenarios**:

1. **Given** the adapter has no progress marker (first run), **When** it starts, **Then** it reads all existing BiRK records before processing new stream events.
2. **Given** Tjeneste has already loaded service-module data, **When** the historical load runs, **Then** the majority of events are delivered with `BarnId` populated.
3. **Given** an event was already delivered in a previous run, **When** the same event is encountered again during a historical load, **Then** Hendelsestjenesten's idempotency guarantee ensures no duplicate is created.
4. **Given** the historical load fails partway through, **When** the adapter restarts, **Then** it restarts the load from the beginning of the BiRK dataset; previously delivered events are re-sent but produce no duplicates due to idempotency. No mid-load checkpoint is maintained.

---

### User Story 4 — Error Handling and Retry (Priority: P2)

The adapter must never silently discard messages. When delivery to Hendelsestjenesten fails due to a transient error (network issue, service temporarily unavailable), the adapter retries with exponential backoff. Messages that cannot be delivered after the maximum number of retries are placed in an error queue, and the operations team is alerted.

**Why this priority**: Data loss is unacceptable. The adapter must guarantee that every BiRK event either reaches Hendelsestjenesten or lands in the error queue for manual review.

**Independent Test**: Can be tested by configuring Hendelsestjenesten to return 503 errors and verifying that the adapter retries and eventually moves the message to the error queue, emitting an alert.

**Acceptance Scenarios**:

1. **Given** Hendelsestjenesten returns a transient error (5xx), **When** the adapter retries, **Then** it uses increasing delays between attempts and succeeds once the service recovers.
2. **Given** Hendelsestjenesten returns a validation error (422), **When** the adapter receives it, **Then** the message is logged with structured context per FR-012 (`BirkHendelsesId`, `BirkTiltakPK`, HTTP status code, and response body — excluding personal data fields such as `RegAv` / `EksternBeskrivelse` per GL-29) but does not block processing of subsequent messages.
3. **Given** a message has failed the maximum number of retries, **When** it is placed in the error queue, **Then** an operational alert is sent to the operations team.

---

### User Story 5 — Code Value Translation (Priority: P2)

BiRK stores reference data as numeric codes (e.g., `HjemmelTypeFK`, `TvangsProtokollStatusTypeFK`, `RommingKategoriType`). The adapter translates these numeric codes to the corresponding M2LB identifier UUIDs before sending to Hendelsestjenesten.

**Why this priority**: Hendelsestjenesten requires M2LB identifiers (UUIDs), not BiRK numeric codes. Without translation, all events would fail validation.

**Independent Test**: Can be tested by supplying a BiRK event with known numeric codes and verifying that the resulting ingestion request contains the correct M2LB UUID identifiers.

**Acceptance Scenarios**:

1. **Given** a `TvangsProtokoll` record with `HjemmelTypeFK = 5`, **When** translated, **Then** the `InngrepsDetaljRequest.HjemmelTypeId` contains the corresponding M2LB UUID.
2. **Given** a `Rømming` record with `RommingKategoriType = 2`, **When** translated, **Then** `RommingsDetaljRequest.RommingKategoriTypeId` contains the UUID for category 2.
3. **Given** a BiRK code value that has no mapping, **When** the adapter encounters it, **Then** the event is moved to the error queue and an alert is triggered.

---

### Edge Cases

- What happens when the Event Hubs connection is lost mid-stream? The adapter must reconnect and resume from the last committed progress marker without re-delivering already-processed events.
- How does the system handle a BiRK record where `RegAv` (registered by) is a free-text name rather than a system user ID? The value is stored as an unstructured `EksternBeskrivelse` in `Involverte` and no `InternBrukerId` is set.
- What happens if `OriginalRomningFk` on a `Rømming` record references a rømming that has not yet been processed by the adapter? The `OriginalHendelsesId` field should be resolved from the adapter's own tracking of previously delivered events. If not found, the field is left null and the discrepancy is logged.
- What happens when the same CDC message is delivered twice by Event Hubs (at-least-once delivery)? Hendelsestjenesten's ingestion API is idempotent; repeated delivery of identical data must not create duplicates.
- What happens when Tjeneste and Hendelsestjenesten are both unavailable at startup? The adapter must wait (with retries) for both services to become available before beginning to read from the stream.
- What happens when the CDC stream contains a DELETE operation for a `TvangsProtokoll` or `Rømming` record? The adapter logs the DELETE with full context (table, `BirkHendelsesId`, timestamp) and discards it without delivery. No cancellation or retraction event is sent to Hendelsestjenesten.
- What happens to events in the error queue when the cause of failure is resolved? The operations team can trigger a reprocessing of error queue messages; the adapter must support this without code changes.

---

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The adapter MUST read CDC change events for the `TvangsProtokoll` and `Rømming` BiRK tables from Azure Event Hubs. Only INSERT and UPDATE operations are translated and delivered; DELETE operations MUST be logged and discarded without delivery to Hendelsestjenesten.
- **FR-002**: The adapter MUST translate `TvangsProtokoll` records to `Inngrep` events and send them to the Hendelsestjenesten ingestion API endpoint `PUT /api/hendelser/v1/innmating/inngrep/{birkHendelsesId}`.
- **FR-003**: The adapter MUST translate `Rømming` records to `Uteblivelse` (kategori 1), `Rømming` (kategori 2), or `Bortføring` (kategori 3) events and send them to `PUT /api/hendelser/v1/innmating/romming/{birkHendelsesId}`.
- **FR-004**: The adapter MUST perform a synchronous lookup against Tjeneste using `BirkTiltakPK` to resolve `BarnId` (UUID) and `TjenesteId` (UUID).
- **FR-005**: When a Tjeneste lookup returns no match, the adapter MUST deliver the event with `BirkTiltakPK` set and `BarnId = null`. The adapter MUST NOT retain a pending state for unresolved child links.
- **FR-006**: The adapter MUST translate BiRK numeric code values (`HjemmelTypeFK`, `TvangsProtokollStatusTypeFK`, `RommingKategoriType`) to M2LB UUID identifiers before delivery.
- **FR-007**: The adapter MUST persist a progress marker indicating how far it has read from the Event Hubs stream, enabling resumption after restart without re-processing or skipping events.
- **FR-008**: The adapter MUST perform a full historical load of all existing `TvangsProtokoll` and `Rømming` records from BiRK on first startup (no progress marker present). The historical load is achieved by replaying the Event Hubs stream from the earliest available offset. Full stream retention must be configured on the Event Hub to ensure all historical events are available.
- **FR-009**: The adapter MUST NOT proceed to stream processing until Event Hubs, Hendelsestjenesten, Tjeneste, and Azure SQL (Delivered Event Registry) are all confirmed available.
- **FR-010**: The adapter MUST retry delivery on transient errors using exponential backoff. Default configuration: 10 retries, initial delay 5 s, maximum delay 5 min. All retry parameters MUST be configurable without code changes.
- **FR-011**: Messages that exceed the maximum retry count MUST be moved to an error queue and trigger an operational alert. The alert is delivered via an Azure Monitor alert rule (provisioned as infrastructure configuration by the operations team) that fires when the `error_queue_publishes` metric (FR-017) exceeds zero within a configured polling window. No in-process notification dispatch is required from the adapter code itself.
- **FR-012**: Validation errors (422 from Hendelsestjenesten) MUST be logged with structured context — specifically: `BirkHendelsesId`, `BirkTiltakPK`, HTTP status code, and response body. Personal data fields (including `RegAv` / `EksternBeskrivelse`) MUST be excluded per GL-29. Processing of subsequent messages MUST NOT be halted.
- **FR-013**: The adapter MUST NOT store event data beyond the three permitted stores: the progress marker (Azure Blob Storage), the error queue (Azure Service Bus), and the Delivered Event Registry (Azure SQL). No other persistent event or payload storage is permitted.
- **FR-014**: The adapter MUST NOT store credentials of any kind for any connection. All service authentication MUST use Azure Managed Identity or equivalent credential-free mechanisms. This applies to all external connections including but not limited to Event Hubs, Hendelsestjenesten, Tjeneste, Azure SQL, Azure Service Bus, and Azure Blob Storage.
- **FR-015**: The adapter MUST map BiRK `RegAv` (free-text name) to an unstructured `EksternBeskrivelse` field in `Involverte`, without setting `InternBrukerId`.
- **FR-016**: The adapter MUST track delivered events by `BirkHendelsesId` (the BiRK event key) to support `OriginalHendelsesId` resolution for `Rømming` references. After each successful delivery, the adapter MUST parse the `HendelsesId` UUID from the ingestion API response body and store the `BirkHendelsesId → HendelsesId` mapping in the Delivered Event Registry. This mapping is persisted in an adapter-owned Azure SQL / SQL Server table and survives adapter restarts.
- **FR-017**: The adapter MUST report operational metrics via OpenTelemetry, exported to Azure Monitor (Application Insights): messages processed per table, successful/updated/unchanged deliveries, error counts, error queue message depth (current `ActiveMessageCount` in the Service Bus queue, sampled via `ServiceBusAdministrationClient.GetQueueRuntimePropertiesAsync` and exported as an observable gauge), and stream lag.
- **FR-018**: The adapter MUST expose a health check (via .NET health check middleware) reporting connectivity to Event Hubs, Hendelsestjenesten, Tjeneste, and Azure SQL, plus the timestamp of the last successful read. Health status is also exported via OpenTelemetry to Azure Monitor.

### Key Entities

- **BiRK CDC Event**: A change record from the Event Hubs stream representing an insert or update to a BiRK table. Contains the raw BiRK field values and a unique `BirkHendelsesId`.
- **InngrepsInnmatingRequest**: The Hendelsestjenesten ingestion payload for a `TvangsProtokoll`-derived event. Key fields: `KildeId`, `HendelsesTypeId`, `BarnId` (nullable), `BirkTiltakPK`, `FraDato`, and nested `InngrepDetalj` (with `HjemmelTypeId`, `TvangsProtokollStatusTypeId`, protocol numbers, follow-up dates).
- **RommingsInnmatingRequest**: The Hendelsestjenesten ingestion payload for a `Rømming`-derived event. Key fields: `KildeId`, `HendelsesTypeId`, `BarnId` (nullable), `BirkTiltakPK`, `FraDato`, and nested `RommingsDetalj` (with `RommingKategoriTypeId`, police registration dates, duration, `OriginalHendelsesId`).
- **Progress Marker**: A persisted cursor recording the last successfully processed position in the Event Hubs stream, per partition. Stored in Azure Blob Storage using the `BlobCheckpointStore` from `Azure.Messaging.EventHubs.Processor`; no custom checkpoint logic is required.
- **Error Queue**: An Azure Service Bus queue for messages that could not be delivered after maximum retries. The operations team triggers reprocessing by re-queuing messages from this queue; the adapter processes re-queued messages using the same delivery pipeline without code changes.
- **Code Mapping Table**: A lookup table mapping BiRK numeric codes to M2LB UUID identifiers for each code type (`HjemmelTypeFK`, `TvangsProtokollStatusTypeFK`, `RommingKategoriType`). Stored as a static configuration file loaded at startup.
- **Delivered Event Registry** (Azure SQL table `BiRKAdapter.BirkHendelseRegistrering`): An adapter-owned Azure SQL / SQL Server table mapping each delivered `BirkHendelsesId` (BiRK key, string) to the platform-assigned `HendelsesId` UUID returned in the Hendelsestjenesten ingestion API response. Used to resolve `OriginalHendelsesId` for `Rømming` cross-references. Persisted across restarts.

---

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All BiRK events produced after the adapter starts are delivered to Hendelsestjenesten within 60 seconds of appearing in the Event Hubs stream under normal operating conditions. Expected steady-state volume is low (hundreds of events per day); throughput is not a limiting constraint.
- **SC-002**: The historical load completes without data loss — every `TvangsProtokoll` and `Rømming` record in BiRK is accounted for in Hendelsestjenesten (as a new event, an updated event, or an idempotent no-op).
- **SC-003**: No event is permanently lost — every message either reaches Hendelsestjenesten or lands in the error queue, with zero silent discards.
- **SC-004**: After a restart, the adapter resumes correctly in under 2 minutes, without re-creating duplicate events or skipping new ones.
- **SC-005**: The adapter resolves `BarnId` for at least 95% of events when Tjeneste has loaded the corresponding service-module data. Measured operationally via the `deliveries` counter (FR-017) labelled with `barnId_resolved = true/false` emitted by `BirkAdapterMetrics` (T065).
- **SC-006**: The adapter remains operational while Tjeneste or Hendelsestjenesten experiences outages of up to 30 minutes, recovering automatically once services are restored. The default retry policy (10 retries, 5 s initial delay, 5 min max delay) is sufficient to cover this window without manual intervention.
- **SC-007**: Operational metrics and health status are available within 5 minutes of adapter startup.

---

## Assumptions

- BiRK CDC events are delivered via Azure Event Hubs with at-least-once delivery semantics; the adapter must handle duplicate messages.
- Hendelsestjenesten's ingestion API (`PUT /api/hendelser/v1/innmating/inngrep/{birkHendelsesId}` and `PUT /api/hendelser/v1/innmating/romming/{birkHendelsesId}`) is idempotent — sending the same `birkHendelsesId` multiple times does not create duplicates.
- Tjeneste exposes a lookup endpoint that accepts `BirkTiltakPK` and returns `BarnId` + `TjenesteId`, or a 404/empty response when no match is found.
- The Event Hub is configured with sufficient retention to hold the full history of BiRK CDC events. On first startup, the adapter replays from the earliest offset to perform the historical load; thereafter it continues from the committed checkpoint. No direct database access to BiRK is required.
- The code mapping table (BiRK numeric codes → M2LB UUIDs) is maintained as a static configuration file (JSON or YAML) embedded with the adapter and loaded at startup. A startup validation check must fail fast if any expected code value is missing from the mapping. The adapter does not query Hendelsestjenesten's reference data API for code mappings.
- `BirkHendelsesId` is the unique key used to identify a specific BiRK event record across restarts and re-deliveries. It is passed as the URL path parameter to the ingestion API.
- The adapter runs as a single instance (no horizontal scaling) in M01; concurrency and partitioned processing are out of scope for this milestone. Expected steady-state event volume is low (hundreds of events per day), so throughput is not a design constraint. The adapter is hosted as an Azure Container App, which provides native Managed Identity, scales to zero, and exposes the health check endpoint (FR-018) via built-in ingress.
- Mobile/browser UI is out of scope — the adapter is a background system-to-system integration component with no user-facing interface.
- Tjeneste data must be loaded before the historical event load begins to maximize the number of events that can be delivered with a resolved `BarnId`.
- The `KildeId` field in the ingestion request is set to the BiRK event's unique identifier string (`BirkHendelsesId`).
- `Kilde` is hardcoded to `"BiRK"` for all events submitted by this adapter.
- `KallerIdentitet` is set to `Guid.Empty` as the adapter has no M2LB user identity; authorization is via Managed Identity at the transport level.
- Follow-up fields on `TvangsProtokoll` (`UnderretningTilBarnetDato`, `EvalueringMedBarnetDato`, `EvalueringMedLederDato`) are read and passed through in M01 even though they are not actively used until M02+.
