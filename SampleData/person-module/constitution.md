<!--
SYNC IMPACT REPORT
==================
Version change: (new) → 1.0.0

Created from three source documents:
  - docs/m2lb-platform-constitution-no.md       (Platform Constitution v4.0)
  - docs/person-module-constitution-no.md        (Person Module Constitution v1.0)
  - docs/utviklingsretningslinjer.md             (Development Guidelines v1.0)

Added sections:
  - Platform Principles (PP-01 through PP-09) — derived from platform constitution
  - Platform Standards (PS-01 through PS-09)  — derived from platform constitution
  - Person Module Constraints                  — derived from module constitution
  - Development Guidelines (GL-01 through GL-29) — derived from dev guidelines
  - Security & Compliance                      — cross-cutting from all three docs
  - Governance

Templates reviewed:
  ✅ .specify/templates/plan-template.md — Constitution Check section present; gates will reflect new principles
  ✅ .specify/templates/spec-template.md — No constitution-specific sections; alignment maintained
  ✅ .specify/templates/tasks-template.md — Task categories align with principle-driven requirements

Deferred TODOs:
  - None. All ratification dates taken from source documents (February 2026).
-->

# M2LB Personservice Constitution

## Core Principles

### I. Contract-Driven Communication (PP-01)

All communication between layers and services MUST occur exclusively via published API
contracts. The backend has no knowledge of the presentation layer. No service accesses
another service's data layer directly.

**Rationale**: Loose coupling enables independent development, testing, and deployment
of each layer. It protects the system from cascading breakage when any single layer
is replaced or upgraded.

### II. Centralised Access Decision (PP-02)

No service makes its own access decisions. All access decisions MUST be evaluated by
the dedicated Authorisation service (`POST /api/autorisasjon/v1/evaluer`). Access that
cannot be confirmed MUST be denied (fail-closed). No role-checks (`IsInRole(...)`) in
service code.

**Rationale**: Distributed access rules are a security and audit liability. A single
authority allows access policies to be changed in one place with immediate platform-wide
effect.

### III. Immutable Audit Obligation (PP-03)

All access-related events MUST be written to an immutable audit trail via Azure Service
Bus — never directly to a database table with DELETE rights. The audit trail MUST NOT
be modifiable, deletable, or retroactively adjusted — not even by administrators.
Audit entries MUST contain only UUIDs and metadata; never sensitive personal data
(names, national IDs, addresses).

**Rationale**: The audit trail is the platform's legal documentation and the basis for
internal audits and supervisory handling. An alterable audit trail is not an audit trail.

### IV. Security Classification Is Absolute (PP-04)

At the individual level, security-classified entities (Kode 6/7) are invisible to all
users without explicit access — with no exceptions, no degraded modes, and no appearance
in count operations in user context. Classification MUST be evaluated as part of every
data operation, not as a post-filter. Aggregate statistics about classified entities are
permitted only for authorised administrative roles via dedicated statistical operations
with their own access control.

**Rationale**: A module that exposes data about a Kode 6 child without respecting
classification constitutes a security breach regardless of which module it is in.

### V. Data Has Legal History (PP-05)

No production data is permanently deleted. All entities with business significance MUST
retain history through temporal validity (`GyldigFra`/`GyldigTil`) and soft deactivation
(`IsAktiv = false`). Hard DELETE operations on entities with business value are forbidden.

**Rationale**: In the child welfare system, history is not nice-to-have — it is legally
required. Soft-delete and temporal modelling are the minimum standard.

### VI. Service Autonomy (PP-06)

Each service owns its persisted data. No service has direct access to another service's
data storage, regardless of technology. Domain boundaries are enforced at the persistence
level. No shared DbContext, no cross-service JOINs, no shared connection strings.

**Rationale**: Shared database is the most common source of tight coupling that makes
systems hard to change, scale, and test.

### VII. Business Logic Belongs to the Domain (PP-07)

Business logic MUST be implemented in the domain layer. The API layer orchestrates and
translates. The presentation layer presents. State machines and validation rules belong
in domain service classes, tested via unit tests. Business logic in API controllers,
SQL stored procedures, or Blazor components is a constitution violation.

**Rationale**: Logic hidden in controllers or SQL procedures is invisible and untestable.
Domain-layer logic is the only kind that can be independently tested.

### VIII. Domain Language in Contracts (PP-08)

API contracts and domain events MUST use the platform's domain language. Concepts, ID
formats, and data models from source systems (BiRK) MUST NOT leak into contracts.
Legacy field names (PARTY_ID, CaseStatusCode) are forbidden in M2LB contracts.
All translation happens in the adapter layer.

**Rationale**: Contracts represent M2LB's long-term promise to consumers. The contract
must reflect the domain model, not the table structure of a system being phased out.

### IX. Specification and Test Are Inseparable (PP-09)

No functionality is implemented without a complete specification. No specification is
complete without corresponding automated test cases. A specification change without a
test change is incomplete. Full test suite MUST pass before merging.

**Rationale**: In a system where access control is a legal and ethical obligation, tests
are not optional — they are proof that the code does what it claims. An authorisation
scenario test for a Kode 7 child is a runnable security specification.

## Platform Standards

### PS-01 — Identity Service: Azure EntraID

All authentication MUST be handled by Azure EntraID via MSAL on the client side.
No service implements its own authentication mechanism. Authorisation context propagates
from gateway to all downstream services. No stored credentials (passwords, static tokens)
in code or configuration.

*Revision*: May be replaced on platform migration, provided the replacement satisfies PP-02.

### PS-02 — Service-to-Service Authentication: Managed Identities

All service-to-service and service-to-Azure-resource authentication MUST use Azure Managed
Identities. Secrets, passwords, and manually managed keys are forbidden. Configuration
values are stored in Azure Key Vault and retrieved at runtime via Managed Identity.

*Revision*: May be revised on platform migration, provided the replacement eliminates
manual secret management.

### PS-03 — Network Topology: Segmented VNet Architecture

Backend services MUST deploy within M2LB's VNet without public IP addresses. The only
permitted internet entry point is the API Gateway component. Phase 1: YARP in M2LB's own
VNet. Phase 2: IT department's central APIM takes over the gateway role; M2LB backend
exposed via private endpoints. NSGs MUST restrict traffic to only necessary ports and
protocols.

*Revision*: Segmentation model and gateway technology determined by operations and IT
security. No-public-IP and private-endpoint principles are implementations of PP-02.

### PS-04 — Primary Identifier: UUID v4

All entities MUST be identified with UUID v4. BiRK IDs are stored as secondary references
and exposed only in the adapter layer. No legacy codes as primary identifiers in M2LB.

*Revision*: Format may be revised. The requirement for a platform-native identity
independent of BiRK is an implementation of PP-08.

### PS-05 — Event Infrastructure: Service Bus and Event Hubs

Internal asynchronous domain event communication MUST use Azure Service Bus with topics
and subscriptions. Import of data from source systems occurs via Azure Event Hubs with
Debezium CDC — the adapter layer translates and publishes normalised events on Service Bus.
Event consumers MUST implement idempotent processing (deduplicate by MessageId/CorrelationId).
Retry with exponential backoff and dead-letter-queue routing for persistent failures are
mandatory. Events silently discarded on failure are forbidden.

*Revision*: May be replaced on platform migration. The distinction between internal domain
communication and source system import is not revisable.

### PS-06 — Operation Registration

All services MUST publish their operations to the Authorisation module via Service Bus
(queue: operasjonsregistrering) at startup using `IHostedService`. Operation identifiers
follow the format `[ServiceName:OperationName]`. Proposed classification (general /
child-specific) is provided at registration. Services MUST NOT start without successful
registration. Operations not registered cannot be used in access evaluations.

*Revision*: Mechanism and format may be revised. The requirement for a centralised
registry is an implementation of PP-02.

### PS-07 — API Versioning: 12-Month Deprecation Notice

Breaking changes MUST be introduced as a new API version via the URL path
(e.g., `/api/person/v1/...`). Existing versions MUST remain operative for a minimum of
12 months after publishing a new version. Non-breaking additive changes (new optional
fields, new endpoints) may be added within an existing version.

*Revision*: Minimum period may be adjusted. The requirement for explicit versioning on
breaking changes is not revisable.

### PS-08 — Observability Follows IT Policy

All services MUST expose a health check endpoint and publish structured logging (e.g.,
Serilog with JSON output) with `correlation_id` (KorrelasjonsId) per request, propagated
to all outgoing calls and events. Observability platform, workspace configuration, and
security monitoring follow IT department's current policy. Console.WriteLine in production
code is forbidden. Logging of sensitive personal information (names, national IDs) is
forbidden — only UUIDs.

*Revision*: Not applicable — this is IT department's area of responsibility.

### PS-09 — Services Are Stateless

Backend services MUST store no state between requests in process memory. Distributed
state storage (Azure Cache for Redis, database) MUST be used where state is necessary.
IMemoryCache for cross-instance-shared data is forbidden. Sticky sessions are forbidden.

*Revision*: May be clarified for specific service types. The requirement for horizontal
scalability without instance coupling is not revisable.

## Person Module Constraints

These constraints apply specifically to the Personservice and are derived from the
Person Module Constitution. They narrow and specialise the platform principles above.

### Single Source of Truth for Person Identity

The Person module is the **only authority** for person identity and child registration
status across the platform. No other service stores, duplicates, or maintains person
data beyond opaque UUID references. All services MUST consume the Person module's API
to retrieve person details.

### Internal Identity Model — UUID-First

The platform uses self-generated UUIDs as primary identifiers for all persons. National
identifiers (fødselsnummer, DUF number) are optional attributes. Uncertain identity
(`UsikkerFødselsnummer`, `UsikkerFødselsdato`) is a first-class structural state — not
a data quality problem. The identity model MUST support:

- Unborn children (no birth number, no birth date)
- EMA children (enslige mindreårige asylsøkere) who may lack documentation
- Persons registered with only a name

### Child Registration Concept

"Barn" in M2LB context means a person registered as a recipient of 2nd-line child welfare
services, identified by a BirkID. Registration triggers: BirkID assignment, BarnType
classification (Ordinær/EMA/Ufødt), placement in `BarnStatusType` state machine, and
enables Authorisation module to create child-specific access relations. Not every person
under 18 is a "barn" in this sense.

### Reference Data as Domain Configuration

All type tables (KjønnType, BarnType, BarnStatusType, SikkerhetsnivåType) are domain-local
configuration owned and managed by the Person module. They are stored as data in the
Person module's database — not as hardcoded enums in application code or shared platform
reference data.

### Dual API Surface

The Person module MUST expose:
- **GraphQL** — consumed by the presentation layer for search, profile display, and
  reference data. Provides flexibility to fetch exactly needed information in one request.
- **REST** — consumed by the BiRK adapter for data ingestion (service-to-service).
  Provides predictable, idempotent ingestion with clear HTTP status codes.

Both surfaces are headless and contract-driven per PP-01.

### Event Privacy Principle

Domain events published to Service Bus MUST contain only UUIDs and metadata — never
sensitive personal data (name, national ID, family information). This is a stricter
interpretation of PP-03 and PP-04 driven by privacy considerations for child data.
Subscribers requiring display information MUST fetch it from the Person module's API,
and only if authorised.

### Transition Architecture Awareness

Phase 1 (current): BiRK is data owner. The Person module ingests via CDC pipeline,
stores in its own domain model, exposes via API, publishes domain events. No write
operations from end-users.

Phase 2 (future): Person module takes over as data owner. BiRK synchronises from
Person module or is decommissioned. Write operations are introduced.

All design decisions MUST account for both phases. The adapter is disposable by design.

### Authorisation Integration for All Data Access

All access to data — whether via search, lookup, or event subscription — MUST be
authorised. The Person module cooperates with the Authorisation module:
- Users see only person data they are authorised to see
- Child-specific data requires an active child relation in the Authorisation module
- Data minimisation applies — users see only information necessary for their role
- Kode 6/7 children are completely invisible to unauthorised users — no metadata,
  no search hits, no indication that the child exists

## Security & Compliance

### GDPR and Norwegian Law

- Data minimisation (Article 5) MUST be applied: API contracts and the presentation
  layer MUST support filtering at field- and section-level for different roles.
- Right of access (Article 15) and record-keeping requirements (Article 30) MUST be
  supported.
- All data MUST be stored in the Norway East Azure region.
- The audit trail MUST satisfy Norwegian requirements for public sector archiving.

### Encryption

All communication between services MUST be encrypted in transit with TLS (minimum TLS 1.2).
All databases and storage MUST be encrypted at rest (Azure SQL TDE, Azure Storage Service
Encryption). Unencrypted HTTP for internal service-to-service communication is forbidden.

### Resilience

All HTTP calls from one service to another MUST implement retry with exponential backoff
(with jitter), explicit timeouts, and circuit breaker pattern (Polly or equivalent).
Security-critical calls (to Authorisation service) MUST be fail-closed. Non-critical
calls may degrade gracefully. Unlimited wait time on HttpClient is forbidden.

## Governance

This constitution is the supreme governing document for the Personservice on the M2LB
platform. It inherits from the M2LB Platform Constitution. Where this constitution is
silent, the platform constitution applies.

### Amendment Procedure

**Platform Principles (Section: Core Principles)**: May only be changed upon fundamental
change in what M2LB is as a platform. Requires written proposal, architecture review,
approval by Solution Architect and Project Manager, and updating all affected module
constitutions and ADRs.

**Platform Standards (Section: Platform Standards)**: May be revised when a new technology
satisfies the principled requirement equally well or better. Requires written proposal
with technical justification, architecture review, approval from Solution Architect, and
a new ADR replacing the current one.

**Module-level rules (Section: Person Module Constraints)**: May be revised via explicit
module architecture review with stakeholder approval. Changes must be documented with
rationale and date.

### Versioning Policy

- **MAJOR**: Backward-incompatible governance/principle removals or redefinitions.
- **MINOR**: New principle or section added or materially expanded.
- **PATCH**: Clarifications, wording, typo fixes, non-semantic refinements.

### Compliance Review

All code reviews and AI-assisted implementation sessions MUST verify compliance with this
constitution. A request to deviate from the constitution is a signal for an amendment
process — not a signal that the constitution can be ignored. AI agents and developers
MUST treat this document as inviolable during implementation unless a formal amendment
has been issued.

### Source Documents

This constitution is derived from and supersedes (for Personservice implementation
purposes) the following source documents:

- `docs/m2lb-platform-constitution-no.md` — M2LB Platform Constitution v4.0
- `docs/person-module-constitution-no.md` — Person Module Constitution v1.0
- `docs/utviklingsretningslinjer.md` — M2LB Development Guidelines v1.0

**Version**: 1.0.0 | **Ratified**: 2026-02-01 | **Last Amended**: 2026-03-06
