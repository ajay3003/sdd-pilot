<!--
SYNC IMPACT REPORT
==================
Version change: 1.1.0 → 1.1.1  [PATCH — clarification to Source Code Language standard]

Modified principles: None
Added sections: Development Standards > Source Code Language
Removed sections: None

Templates reviewed:
  ✅ .specify/templates/plan-template.md  — No changes needed
  ✅ .specify/templates/spec-template.md  — No changes needed
  ✅ .specify/templates/tasks-template.md — No changes needed
  ⚠  .specify/templates/commands/         — No command files found; no action needed

Deferred items: None
-->

# Autorisasjon — Spec Constitution

## Core Principles

### I. Contract-Driven Communication (PP-01)

All communication between layers and services MUST go through published API contracts.
Backend has no knowledge of the presentation layer. No service accesses another service's
data layer directly — regardless of technology. This is non-negotiable.

- GL-01: All frontend-to-backend communication routes through the reverse proxy (YARP/APIM).
  Direct backend URLs are forbidden in frontend code.
- GL-02: API contract design precedes implementation. The contract is the source of truth,
  not the database schema.
- GL-03: Blazor components fetch data exclusively via `HttpClient` against published contracts.
- GL-16: Contracts use domain language (not table names, legacy IDs, or internal codes).

### II. Zero-Trust Security (PP-02, PP-04)

No user, service, or network component is implicitly trusted. Access is verified explicitly
at every boundary, always. Security classification is absolute and evaluated as part of every
data operation — never as a post-filter.

- **All access decisions** are made by calling the Authorization evaluation API
  (`POST /api/autorisasjon/v1/evaluer`). No service implements its own access rules.
- **Fail-closed** (GL-25): Security-critical operations MUST return HTTP 503 on auth failure.
  Fail-open is forbidden.
- **EntraID** (PS-01): All user authentication is handled by Azure EntraID.
  Custom auth mechanisms are prohibited.
- **Managed Identities** (PS-02): Service-to-service auth uses Azure Managed Identities.
  Stored credentials (passwords, static keys) are forbidden.
- **Classified entities** (PP-04): Kode 6/7 individuals are invisible at all levels — including
  count operations — to any caller without explicit authorization. Classification is
  evaluated by the domain, not filtered afterwards.
- **Data minimization** (GL-14): APIs MUST filter to field/section level based on the
  caller's operation grants. Presentation layer does not decide what to hide.
- **No PII in events** (GL-21): Service Bus payloads contain only UUIDs and non-sensitive
  metadata. Names, SSNs, addresses are forbidden in event payloads.
- GL-11: Backend services deploy inside M2LB VNet with no public IP addresses.
  The only internet-facing entry point is the API gateway (YARP phase 1, APIM phase 2).
- GL-12: TLS for all inter-service communication; encryption-at-rest on all databases and storage.

### III. Domain-Driven Service Design (PP-03, PP-06, PP-07)

Services are autonomous and own their own data. Service boundaries are enforced at the
persistence level. Business logic belongs in the domain layer.

- **No cross-service database access** (GL-15): No JOINs across service databases.
  No shared schemas. Cross-service data access goes through published API contracts only.
- **Layered responsibility** (PP-07, GL-17): Domain layer owns business logic. API layer
  orchestrates and translates. Presentation layer presents. None may take another's responsibility.
- **Temporal validity** (PP-05, GL-18): All entities with state transitions MUST implement
  `GyldigFra`/`GyldigTil` and immutable change history. Hard DELETE is forbidden; use soft-delete.
- **Adapter pattern** (GL-19): All mapping from legacy/BiRK to M2LB domain model is isolated
  in the adapter layer. Service code and contracts are agnostic of the source system.
- **UUID v4** (PS-04): All entities use UUID v4 as primary identifier. BiRK IDs are stored as
  secondary references and are only exposed in the adapter layer.

### IV. Event-Driven Integration (PP-04-derived, PS-05)

Data mutations propagate asynchronously via Azure Service Bus. Synchronous dependencies
between services are minimized. Publishers have no knowledge of subscribers.

- **Publish on every mutation** (GL-20): Publish to Service Bus Topics after each successful
  persist. Use transactional outbox to guarantee delivery. Include `KorrelasjonsId` and
  `UtførtAv` (user UUID) in all events.
- **Idempotent consumers** (GL-22): Service Bus guarantees at-least-once delivery.
  Every consumer MUST be idempotent (deduplicate on MessageId/CorrelationId).
- **Dead letter handling** (GL-23): Failed messages MUST route to dead letter queue after
  retries with exponential backoff. Silent discard is forbidden.
- **Operation registration** (PS-06, GL-09): Services register their operations via
  Service Bus queue at startup. Format: `[ServiceName:OperationName]`.
  Services MUST NOT start without successful registration.

### V. Specification and Tests Are Inseparable (PP-09)

No functionality is implemented without a complete specification. No specification is complete
without corresponding automated test cases. A specification change without a test change is
incomplete.

- Full test suite MUST pass before merging (GL-24).
- Authorization scenario tests are required for every operation with access requirements.
- Contract tests validate the GraphQL SDL snapshot against the live schema.
- Integration tests use Testcontainers (SQL Server 2022 + Redis 7) — no mocks for the database.

## Authorization Module Constraints

These constraints are specific to the Autorisasjon service and are binding for all work in
this module.

### Two-Domain Access Model

All access control is divided into two strictly separated domains:

- **General access**: Governs operations not tied to a specific child. Determined by the
  combination of user identity, organizational unit, and general role(s) from the EntraID token.
- **Child-specific access**: Governs operations related to a specific child. Requires an
  explicit, managed relation between the user and the child. The relation's character is
  defined by the assigned child-specific role.

These domains are complementary and additive. Neither can substitute for the other.

### Strict Role–Operation Separation

- General roles MUST only contain general operations.
- Child-specific roles MUST only contain child-specific operations.
- This separation is enforced at the data model level and MUST be validated on every
  role–operation assignment.

### Additive Access Model (No Deny Rules)

A user's effective permissions at any point are the **sum** of all active role assignments.
There are no denial rules. If a user holds multiple roles, their effective permissions are
the union of all granted operations.

### Explicit Child Relations

Access to any child-specific operation requires an explicit, managed relation between the
user and that specific child. Organizational proximity (e.g., belonging to the same unit)
does NOT grant child-specific access.

### Operation Classification as Governance Mechanism

Every operation exposed by any platform service MUST be registered in the Authorization
service and classified as either **general** or **child-specific**. Reclassification changes
the access path without requiring code changes in the consuming service — it is an
administrative action.

### Separation of Administrative Rights

No user may grant permissions, roles, or child relations to themselves. All grants MUST
be performed by another authorized user. Emergency access (nødtilgang) is the only
exception and MUST be subject to mandatory logging, justification, and subsequent review.

### Audit Trail Requirements

Every mutation in the Authorization service MUST be recorded with:
- **Who** performed the action (user identity)
- **What** was changed (entity, before/after state)
- **When** the change occurred (timestamp)
- **Why** when a justification field is available

Child-specific relations (user↔child) are NEVER physically deleted. When a relation is
revoked, it is marked inactive with a timestamp and the identity of the revoking user.
The complete relation history for any child MUST be searchable.

### Performance as First-Class Concern

Authorization evaluation is on the critical path of every platform request.
Design choices around data access, caching, and evaluation logic MUST account for this
at every change. Unacceptable latency introduced by authorization is an architecture defect.

## Development Standards

### Observability (PS-08, GL-28)

All services MUST implement structured logging (JSON), propagate `KorrelasjonsId` through
all outbound calls and events, and publish health and custom metrics to Azure Monitor /
Application Insights per IT department policy. `Console.WriteLine` is forbidden in
production code. PII (names, SSNs, addresses) MUST NOT appear in logs — IDs only.

### Stateless Services (PS-09, GL-27)

Backend services MUST NOT store user- or request-specific state in process memory between
calls. Session state goes in Azure Cache for Redis or the database. `IMemoryCache` is
forbidden for data shared across instances.

### Resilience (GL-29)

All synchronous HTTP calls between services MUST implement retry with exponential backoff
plus jitter, explicit timeouts, and circuit breaker (Polly). An open circuit is an
operational signal requiring monitoring in Azure Monitor.

### Configuration and Secrets (PS-02, GL-26)

All secrets and environment-specific configuration MUST be fetched from Azure Key Vault
at runtime via Managed Identity. No connection strings with passwords, no static API keys
in source control or `appsettings.json` in production environments.

### API Versioning (PS-07, GL-13)

All API endpoints MUST be versioned via URL path (`/v1/`, `/v2/`). Breaking changes MUST
be introduced as a new parallel version. Existing versions are kept operational for a
minimum of 12 months after a new version is published.

### Source Code Language

All source code MUST be written in English. This applies to class names, method names,
variable names, property names, comments, and commit messages.

**Exception — domain terms**: Norwegian domain-specific vocabulary is preserved as-is and
MUST NOT be translated. Translating domain terms breaks the ubiquitous language shared with
the business and the platform constitution. Examples of retained Norwegian domain terms:

- Entity/concept names: `Barn`, `BarnRelasjon`, `OrgEnhet`, `RolleTildeling`
- Field names on domain entities: `GyldigFra`, `GyldigTil`, `OpprettetAv`, `UtførtAv`
- Event/message payload fields that mirror domain names: `KorrelasjonsId`
- Domain exception codes: `SELVTILDELING_FORBUDT`
- Access-model concepts: `nødtilgang`, `barnespesifikk`

**Character substitution**: When a retained Norwegian domain term contains the characters
`æ`, `ø`, or `å`, they MUST be replaced as follows in source code identifiers:

| Character | Replacement |
|-----------|-------------|
| `æ`       | `ae`        |
| `ø`       | `oe`        |
| `å`       | `aa`        |

Example: a domain concept spelled `nødtilgang` becomes `noedtilgang` in code;
`tiltålt` becomes `tiltaalt`. The business-facing spelling is unchanged in documentation
and contracts — only the code identifier is transliterated.

**Rule of thumb**: If the name appears in the platform constitution, module constitution, or
is used by the business to describe a concept — keep the Norwegian term (transliterated).
All other identifiers (infrastructure, patterns, utilities) MUST be English.

### AI-Assisted Development

The platform supports and encourages AI tools in development. The developer who delivers
code is responsible for it — regardless of which tools were used. AI-generated code MUST
be understood, evaluated, and tested on equal footing with handwritten code before it
is considered delivered.

## Governance

This constitution is subordinate to the M2LB Platform Constitution
(`docs/m2lb-platform-constitution-no.md`). Where this constitution is silent, the platform
constitution governs. Where it specifies further, it is always within the platform
constitution's framework. Platform principles PP-01 through PP-09 and platform standards
PS-01 through PS-09 apply in full.

**Amendment procedure:**
- Principle changes require written proposal, architecture review, and approval from the
  solution architect. All affected specs, plans, and tasks must be updated before the change
  takes effect.
- Standard/guideline changes require written technical justification and architecture review.
- Every approved change is recorded in the changelog below and referenced in the relevant ADR.

**Versioning policy:**
- MAJOR: Backward-incompatible governance or principle removal/redefinition.
- MINOR: New principle or section added, or material expansion of existing guidance.
- PATCH: Clarifications, wording, non-semantic refinements.

**Compliance:**
- All PRs and reviews MUST verify compliance with this constitution.
- A request to deviate from the constitution is a signal to start the amendment process —
  not a signal that the constitution can be ignored.
- Use `docs/m2lb-utviklingsretningslinjer.md` for detailed runtime development guidance (GL-01–GL-29).

**Version**: 1.1.1 | **Ratified**: 2026-02-01 | **Last Amended**: 2026-03-24

## Changelog

| Version | Date       | Change                              | Approver          |
|---------|------------|-------------------------------------|-------------------|
| 1.0.0   | 2026-03-24 | Initial spec constitution created from platform constitution v4.0, auth module constitution v2.0, and development guidelines v1.0 | Solution Architect |
| 1.1.0   | 2026-03-24 | Added Development Standard: source code in English, Norwegian domain terms preserved | Solution Architect |
| 1.1.1   | 2026-03-24 | Clarified: æ→ae, ø→oe, å→aa substitution required in domain term identifiers | Solution Architect |
