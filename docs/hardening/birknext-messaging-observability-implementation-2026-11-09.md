# BirkNext — Messaging observability — implementation (2026-11-09)

Branch `008-traceability-first` (local only). Audit: `docs/audits/birknext-messaging-topology-audit-2026-11-09.md`.

## What was added

Read-only **message observations**: files someone exported with their own read access, imported and redacted before anything is stored. BirkNext
never connects to a namespace or an envelope database for this, and there is no message operation anywhere (no receive, complete, abandon,
dead-letter, resubmit, replay or purge).

| Part | File |
|---|---|
| Contracts and pure rules (redaction, preview, correlation, processing state, timeline, envelope summary) | `shared/MessagingObservationContracts.cs` |
| Import, parsing, storage, view assembly, deletion | `backend/.../Services/Integrations/Messaging/MessageObservationService.cs` |
| API `api/message-observations` (import · list · get · delete) | `Controllers/MessageObservationsController.cs` |
| Table `message_observation_sets` (migration `AddMessageObservationSets`) | `Models/ExecutionEvidenceRecords.cs`, `Data/AppDbContext.cs` |
| UI: Integration Topology → **Messages** tab | `Components/MessageObservationsPanel.razor`, `Services/MessageObservationApiService.cs`, `Services/TopologyWorkflow.cs` |

### Accepted files

- **Service Bus peek export** (`"kind": "servicebus-peek"`): namespace, entity, subscription, sub-queue (Active / DeadLetter / Scheduled), observedAt,
  and messages with the Azure SDK received-message property names (`messageId`, `correlationId`, `subject`, `contentType`, `sequenceNumber`,
  `enqueuedTime`, `scheduledEnqueueTime`, `deliveryCount`, `sessionId`, `applicationProperties`, `deadLetterReason`,
  `deadLetterErrorDescription`, `body`). A script that peeks (non-settling) and serializes the SDK object produces it directly.
- **Wolverine envelope export** (`"kind": "wolverine-envelopes"`): rows of the incoming (inbox), outgoing (outbox) and dead-letter tables using
  Wolverine's column names (`id`, `status`, `message_type`, `attempts`, `execution_time`, `destination`, `exception_type`, `exception_message`, …).
  Envelope bodies are never imported.
- Limits: 5 MB per file, 500 messages, 2 000 envelope rows; unknown kinds and unreadable JSON are refused with a reason.

### Redaction (before storage and display)

Metadata only is the default: no body text, only size and SHA-256. Sanitized preview (opt-in per import): JSON values under sensitive keys
(password, token, authorization, connection string, SAS, fødselsnummer/fnr/personnummer/ssn, e-mail, phone, address, …) are masked whole; free
text has 11-digit identity-number-like values, e-mail addresses, bearer tokens / JWTs / SAS / Service Bus connection strings masked; XML elements
with sensitive names are masked; binary and unknown content types are never previewed; previews are bounded to 2 048 characters. Application
properties, correlation ids, subjects, dead-letter reasons/descriptions and envelope exception messages are redacted the same way. Each message
records how many values were masked.

### Assessment (rebuilt on every read, never stored)

- **Processing**: DeadLettered (in the DLQ) · Failed (Wolverine dead-letter row, with its exception) · Succeeded (inbox row `Handled`) · Pending
  (in Wolverine's inbox, or still in a queue — "the delivery count does not say why") · NotObserved. A message that is no longer in a queue is
  never a success.
- **Correlation**: Confirmed only when the message's correlation id or its `X-BirkNext-Execution-Id` application property equals exactly one id an
  Execution Evidence call sent in the same environment; Ambiguous when several; NotCorrelated otherwise. Time proximity is never used.
- **Timeline**: produced by test execution (when confirmed) → scheduled → enqueued → inbox / outbox / handled / dead-letter rows → redeliveries →
  dead-lettered, each with its source.
- **Envelope stores**: inbox pending / handled, outbox pending, dead letters, retried rows — counts of the exported rows at export time.

## Permissions and boundaries

Import and delete need the configuration-write permission; reads need none. A set is readable only in its own environment. Tests assert that the
controller exposes exactly import / list / get / delete and that the messaging code contains no Service Bus client, receiver, settlement call,
peek call, database connection or raw SQL.

## Tests

`MessageObservationTests` (10: metadata-only default, preview redaction and bound, binary and XML handling, correlation confirmed / ambiguous /
never by time, environment isolation, processing state and timeline, envelope summary without bodies, refusals, deletion, API surface);
`MessageObservationsPanelTests` (4); `IntegrationTopologyPageTests` (tab list updated).
