<!--
Sync Impact Report
==================
Version change:   [TEMPLATE] → 1.0.0 (initial population from template)
Bump type:        MINOR — new principles, sections, and full governance content added.

Principles added:
  - I.    API-First and Headless Architecture (from M2LB PP-01, P1)
  - II.   Zero-Trust Security (from M2LB PP-02–PP-04, P2)
  - III.  Domain-Driven Service Design (from M2LB PP-05–PP-08, P3)
  - IV.   Event-Driven Integration (from M2LB PP, P4)
  - H-01  Versioned History is Non-Negotiable (Hendelsestjenesten-specific)
  - H-02  HendelsesType is Structured Data
  - H-03  BarnId is Primary Identifier; TjenesteId is Contextual
  - H-04  Loosely Coupled to Faglig Oppfølging
  - H-05  Involverte Support Both Structured and Unstructured Forms
  - H-06  Varsling is Documented, Not Sent
  - H-07  Type-Specific Extensions on a Shared Core

Sections added:
  - Platform Standards and Development Guidelines (PS-01–PS-09, key GL-XX)
  - Service Constraints and Architecture (scope, repos, dependencies, event contracts)

Templates reviewed:
  - .specify/templates/plan-template.md    ✅ Constitution Check section aligns with PP/GL rules
  - .specify/templates/spec-template.md    ✅ Mandatory sections align with PP-09 (spec+test)
  - .specify/templates/tasks-template.md   ✅ Foundational phase covers security, outbox, ops reg
  - .specify/templates/constitution-template.md  ✅ (source, no changes needed)

Deferred TODOs:
  - None. All fields resolved from source documents.
-->

# Hendelsestjenesten — M2LB Constitution

## Core Platform Principles

These four principles are the philosophical foundation of the M2LB platform, inherited from the
M2LB Plattformkonstitusjon (v4.0). They are non-negotiable and apply to all development without
exception. Platform Principles PP-01 through PP-09 and Platform Standards PS-01 through PS-09
govern in full.

### I. API-First and Headless Architecture (P1)

All communication between layers and services MUST occur exclusively via published API contracts.
Backend services have no knowledge of the presentation layer. The contract is the only permitted
communication point between services — never direct data access.

- All requests from frontend to backend MUST be routed through the reverse proxy (YARP/APIM).
  Direct backend URLs are forbidden in frontend code.
- Blazor WebAssembly components MUST use HttpClient against published GraphQL or REST contracts.
  Injection of DbContext, IMemoryCache, or any server-side service is an architecture violation.
- Authentication MUST use MSAL client-side only. No server-side auth state, no InteractiveServer.
- API contracts (GraphQL SDL or OpenAPI) MUST be defined and approved before implementation starts.
- Breaking changes MUST be introduced as a new parallel API version (e.g., `/v2/`). Existing
  versions MUST remain operative for at least 12 months after the new version is published.

### II. Zero-Trust Security (P2)

No component, user, network position, or service is implicitly trusted.
Access is verified explicitly at every call boundary through cryptographically verified identity.
"Trust never the position — trust always the identity."

- All access decisions MUST be evaluated by calling the Autorisasjonsmodul's evaluation API
  (`POST /api/autorisasjon/v1/evaluer`). No service implements its own access rules.
- All services MUST register their operations in the Autorisasjonsmodul's registry via Service Bus
  at startup, in the format `[TjenesteNavn:OperasjonNavn]`. Unregistered operations cannot be
  access-controlled.
- Fail-closed is mandatory: access MUST be denied (HTTP 503) on authorization API failure for
  security-critical operations. Fail-open (granting access on error) is forbidden.
- Security classification (sikkerhetsklassifisering) MUST be evaluated as part of every data
  operation — not as subsequent filtering. Classified entities (e.g., Kode 6) are entirely
  invisible to unauthorized users, including in count operations in user context.
- All access-related events MUST be written to an immutable audit trail via Azure Service Bus.
  The trail cannot be modified or deleted — not even by system administrators.
- Read operations where `barnId` is an explicit input parameter MUST publish a leselogg-hendelse
  to Azure Service Bus after access is confirmed and data is fetched (GL-32, ADR-023).
- All service-to-service communication MUST use TLS (minimum TLS 1.2).
- No backend service is exposed directly to the internet. The API gateway is the sole ingress.
- EntraID is the sole identity provider. No custom password or API-key authentication.
- Azure Managed Identities for all service-to-service auth. No stored credentials anywhere.
  All secrets retrieved from Azure Key Vault at runtime via Managed Identity.

### III. Domain-Driven Service Design (P3)

Each service is an autonomous unit that owns its own data, its own API, and its own release cycle.
Service boundaries are enforced at the persistence level. "The module owns the data.
Everyone else borrows it via API."

- No service has direct database access to another service's database. No cross-service JOINs.
  No shared database schema or shared ConnectionString between services.
- All entities MUST use UUID v4 as primary identifier (PS-04). Legacy system IDs (e.g., BiRK
  TiltakPK) are stored as secondary references and exposed only in the adapter layer.
- API contracts and domain events MUST use M2LB domain language. Database column names, legacy
  codes, and kildesystem identifiers MUST NOT appear in contracts.
- All entities with state transitions MUST implement temporal validity (GyldigFra/GyldigTil)
  and immutable change history. Hard DELETE is forbidden; soft-delete (IsAktiv = false) is
  mandatory for all entities with business value.
- Business logic MUST reside in the domain layer. The API layer orchestrates and translates.
  The presentation layer presents. None may assume another layer's responsibility.
- All translation from legacy data models to M2LB domain models MUST occur in the adapter layer.
  The service's internal code and API contracts are agnostic of source system details.

### IV. Event-Driven Integration (P4)

Services communicate asynchronously via Azure Service Bus. Domain events are platform resources —
published independently of consumers. Publishers have no knowledge of their subscribers.
"Publish events as facts. Consume events as signals. Never assume you are the only listener."

- Domain events MUST be published to Azure Service Bus Topics after every successful data mutation.
  The transactional outbox pattern MUST be used to guarantee delivery even during transient faults.
- Event payloads MUST contain only UUID identifiers and non-sensitive metadata. Personal data
  (names, fødselsnummer, addresses, family information) is NEVER included in event payloads.
- All Service Bus consumers MUST be idempotent — delivering an event twice produces the same
  result as delivering it once. Use event MessageId/CorrelationId for deduplication.
- Failed event processing MUST use retry with exponential backoff and jitter. Events that
  consistently fail MUST be routed to the dead letter queue. Silent discard is forbidden.
- KorrelasjonsId MUST be propagated from incoming requests to all outgoing calls and events.

## Hendelsestjenesten — Service Principles

These principles are specific to the Hendelsestjenesten and extend the platform principles.
All development on the `m2lb-hendelser` repository MUST comply with both this section and the
Core Platform Principles above.

### H-01 — Versioned History is Non-Negotiable

A Hendelse (event) may be updated, but all previous versions MUST be preserved unchanged.
The most recent version is active. No version may be deleted. This is a legal and supervisory
requirement — tilsynsmyndigheter (oversight authorities) can demand what was registered, when,
and whether it was subsequently changed.

### H-02 — HendelsesType is Structured Data

HendelsesType is not merely a display label. External services (e.g., WorkflowTjenesten) may
build rules and logic based on the type. HendelsesType MUST always be set on a Hendelse.
Types MUST be stored as configurable reference data in database tables — never as hardcoded
enums in application code.

### H-03 — BarnId is the Primary Identifier; TjenesteId is Contextual

Every Hendelse belongs to exactly one child via `BarnId` — always an internal M2LB UUID from
Personmodulen. `TjenesteId` is an optional contextual reference indicating the opphold (placement)
during which the event occurred. No kildesystem key (e.g., BiRK TiltakFK) is stored as a primary
reference on a Hendelse.

### H-04 — Loosely Coupled to Faglig Oppfølging

Hendelsestjenesten publishes facts about what occurred. It has no knowledge of faglige vurderinger
(professional assessments), tiltak (measures), or oppfølgingsplaner (follow-up plans). The
connection between a Hendelse and any downstream consequence is owned exclusively by subscribing
services via Service Bus. Hendelsestjenesten has no knowledge of who subscribes or what they do.

### H-05 — Involverte Support Both Structured and Unstructured Forms

Hendelsestjenesten MUST NOT force all involved parties into structured form. Both are valid:
- **Structured**: M2LB-registered users (employees, professionals) stored as UUID reference
  to Autorisasjon.
- **Unstructured**: External persons not registered in M2LB (police officer, physician, classmate,
  guardian) stored as free-text description of name and role.

The child the event concerns is always implicit via `BarnId` and is NOT registered separately
as an involvert. Other children at the institution MUST be registered as unstructured only
(no UUID reference) until personal privacy and access questions are formally resolved.

### H-06 — Varsling is Documented, Not Sent

Hendelsestjenesten documents that a notification was given — it does not send notifications.
A varslingsregistrering contains: who was notified, the channel, and the timestamp.
Actual dispatch of notifications to external parties is the responsibility of WorkflowTjenesten
or other Service Bus subscribers — not of Hendelsestjenesten.

### H-07 — Type-Specific Extensions on a Shared Core

Different event types (Inngrep, Rømming, Uteblivelse, Bortføring, etc.) have distinct field
structures. The domain model MUST use a shared core for all events and type-specific extensions
for fields unique to a single type:
- Inngrep: hjemmel (HjemmelType with gyldighetsperiode), politiinvolvering.
- Rømming: politidatoer, kategori.
HjemmelType MUST be stored as configurable reference data with GjelderFra/GjelderTil because
it is governed by the legislature (BVL 2021, chapter 10) and can change with new legislation.
Historical events registered under an older law version MUST display correctly even if the
hjemmel is no longer valid for new registrations.

## Platform Standards and Development Guidelines

These standards are binding. Technology-specific standards can be revised when a superior
alternative satisfies the same principle equally well, via formal architecture review and new ADR.

| Standard | Requirement |
|----------|-------------|
| **PS-01 Identity** | Azure EntraID is the sole authentication provider. No custom auth mechanisms. |
| **PS-02 Service Auth** | Azure Managed Identities for all service-to-service auth. Stored credentials and static API keys are forbidden. Configuration values in Azure Key Vault only. |
| **PS-03 Network** | All backend services deploy in M2LB VNet without public IP addresses. The API gateway (YARP phase 1; IT-avdelingens APIM phase 2) is the sole internet-facing ingress. |
| **PS-04 Identifiers** | UUID v4 for all entities. BiRK-IDs are secondary references in the adapter layer only. |
| **PS-05 Events** | Internal async communication via Azure Service Bus topics/subscriptions. BiRK CDC data ingested via Azure Event Hubs through a dedicated Hendelsesadapter. The adapter always calls the target service's API — the service owns persistence and event publishing. |
| **PS-06 Ops Reg** | All operations MUST be registered in Autorisasjonsmodulen's registry at startup via Service Bus. Format: `[TjenesteNavn:OperasjonNavn]`. Unregistered operations cannot be managed. |
| **PS-07 Versioning** | Breaking API changes introduced as new parallel version. Existing versions held operative ≥ 12 months after new version publication. |
| **PS-08 Observability** | Health-check endpoint required. Structured logging (Serilog, JSON) with `correlation_id` per request. Follows IT-avdelingens gjeldende policy. No Console.WriteLine in production. No sensitive personal data in logs — UUIDs only. |
| **PS-09 Stateless** | Backend services store no per-user or per-request state between calls. Distributed state where persistence is required (Azure Cache for Redis or database). |

**Key development guidelines** (from M2LB-Utviklingsretningslinjer v2.0):

- **GL-08**: All access decisions via `POST /api/autorisasjon/v1/evaluer`. Hardcoded role checks (`if user.IsInRole(...)`) are forbidden.
- **GL-09**: Publish operation list to Service Bus at startup. Service MUST NOT start without successful registration.
- **GL-10**: Any service exposing data about classified persons MUST respect classification levels and filter by authorization evaluation. Write all access-related events to the immutable audit trail.
- **GL-18**: GyldigFra/GyldigTil on all entities with temporal validity. Soft-delete (IsAktiv) required. Log changes with who, what, and when.
- **GL-20**: Publish domain event immediately after successful persistence. Outbox pattern (GL-33) guarantees delivery.
- **GL-21**: Event payloads contain only UUIDs and metadata. Personal data in events is a security violation.
- **GL-22**: All consumers MUST be idempotent. Check MessageId/CorrelationId before processing.
- **GL-23**: Retry with exponential backoff. Dead letter queue for persistent failures. Silent discard is forbidden.
- **GL-24**: All new features MUST have automated tests. Full test suite MUST pass before merging. Specification change without test change is incomplete.
- **GL-25**: Fail-closed on security-critical authorization failures. HTTP 503 on auth API error — never grant access by default.
- **GL-26**: All configuration and secrets via Azure Key Vault at runtime. No hardcoded values in code or appsettings.json. No secrets in source control.
- **GL-32**: Read operations where `barnId` is an input parameter MUST publish a leselogg-hendelse after access is confirmed and data is fetched. List searches not targeting a single child are exempt.
- **GL-33**: Outbox pattern is mandatory when writing to Azure SQL and publishing to Service Bus in the same logical operation. No direct publish in the request pipeline without outbox.

## Service Constraints and Architecture

**Repositories** (per GL-30, ADR-021):
- `m2lb-hendelser` — Hendelsestjenesten source code, domain model, and API.
- `m2lb-hendelser-adapter` — BiRK CDC adapter, separately deployable.

**Standard repo structure**:

```
src/
  M2LB.Hendelse.Api/            ← API layer (controllers, DTOs, middleware)
  M2LB.Hendelse.Domain/         ← Domain layer (entities, services, rules)
  M2LB.Hendelse.Infrastructure/ ← Infrastructure layer (EF Core, Service Bus, adapters)
tests/
  M2LB.Hendelse.Unit/
  M2LB.Hendelse.Integration/
specs/
.pipeline/
README.md
```

**Scope — Trinn 1 / M01 (current)**:
- Read-only intake from BiRK via CDC stream (Event Hub) through Hendelsesadapteren.
- Expose event data for Barnets profilside as a chronological timeline via GraphQL.
- Publish `HendelsesRegistrert` events to Service Bus topic `hendelser.barn`.
- Manage HendelsesType and HjemmelType as configurable reference data.
- Subscribe to `TjenesteOpprettet` to link events stored with `BarnId = null`.

**Not in scope for Hendelsestjenesten** (regardless of delivery phase):
- Sending actual notifications to external parties.
- Faglige vurderinger, tiltak, or oppfølgingsplaner.
- Administration of who was present at an institution at a given time.
- Direct integration with Netpower (avvikssystem) — this is WorkflowTjenestens responsibility.
- Archiving of Henvisnings-XML — stored in Elements; referenced via MeldingsId only.

**Ingress channels**:

| Channel | Description |
|---------|-------------|
| Hendelsesadapteren | Delivers events from BiRK CDC stream. Primary data source in M01. |
| `TjenesteOpprettet` (Service Bus) | Subscribed to resolve `BarnId` for events stored with `BarnId = null`. |

**Published events** on topic `hendelser.barn`:

| Event | Payload |
|-------|---------|
| `HendelsesRegistrert` | `HendelsesId`, `BarnId`, `HendelsesTypeId`, `HendelsesTypeKode`, `tidspunkt` |

**Leselogg-hendelse** (required fields — GL-32, ADR-023):

| Field | Type | Description |
|-------|------|-------------|
| `hendelsesId` | UUID | Unique identifier for this log entry |
| `brukerId` | UUID | User's UUID from Autorisasjonsmodulen |
| `operasjon` | String | `Tjeneste:Operasjon` format, e.g. `Hendelse:Se` |
| `ressursId` | UUID | Identifier of the read object |
| `barnId` | UUID | Child's UUID — always required for leselogg events |
| `tidspunkt` | DateTime | Timestamp of the read operation (UTC) |

**Service dependencies**:

| Service | Relation |
|---------|----------|
| Personmodulen | `BarnId` always an internal M2LB UUID from Person. No direct API dependency. |
| Autorisasjon | All operations require valid token and rolecheck. Structured involverte validated against user UUID. |
| Tjeneste | Synchronous lookup of `BirkTiltakPK` to resolve `BarnId`. Async subscription to `TjenesteOpprettet`. |
| Hendelsesadapteren | Primary data source in M01 via BiRK CDC/Event Hub. |
| WorkflowTjenesten (future) | Subscribes to `hendelser.barn` for workflow orchestration and Netpower integration. |
| Fremtidige varslingstjenester | Future subscribers on `hendelser.barn`. |

## Governance

- This constitution supersedes all other practices, patterns, and instructions for the
  `m2lb-hendelser` repository. No implementation decision may contradict these rules.
- This constitution inherits from and is subordinate to the **M2LB Plattformkonstitusjon (v4.0,
  February 2026)**. Where this constitution is silent, the platform constitution governs.
  Where it elaborates, it is always within the platform constitution's bounds.
- **Amendments to Platform Principles (PP-01–PP-09)**: Require written proposal, architecture
  review, written approval from løsningsarkitekt and prosjektleder, and updates to all affected
  module constitutions and ADRs before the change takes effect.
- **Amendments to Platform Standards (PS-01–PS-09)**: Require written proposal with technical
  rationale, architecture review, approval from løsningsarkitekt, and a new ADR that supersedes
  the current one.
- **Amendments to Service Principles (H-01–H-07)**: Require module-level review and approval
  from løsningsarkitekt. The platform constitution MUST NOT be violated.
- **All changes**: Documented in the version line and endringslogg below, and in the ADR register.
  All affected specifications, plans, and task lists MUST be updated before the change takes effect.
- Any request to deviate from this constitution is a signal for an amendment process — not a
  signal that the constitution can be ignored.
- **All PRs and code reviews** MUST verify compliance with PP-01–PP-09, PS-01–PS-09,
  H-01–H-07, GL-08, GL-09, GL-10, GL-18, GL-20–GL-26, GL-32, GL-33.
- No feature is implemented without a complete specification and passing automated tests (PP-09).
- Complexity beyond what the task requires MUST be justified explicitly in the plan document.

### Endringslogg

| Version | Date | Change | Approver |
|---------|------|--------|----------|
| 1.0.0 | 2026-04-24 | Initial constitution populated from M2LB Plattformkonstitusjon v4.0, M2LB-Utviklingsretningslinjer v2.0, and Hendelsestjenesten Konstitusjon v1.0. | Løsningsarkitekt |

**Version**: 1.0.0 | **Ratified**: 2026-03-01 | **Last Amended**: 2026-04-24
