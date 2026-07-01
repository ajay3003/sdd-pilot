<!--
Sync Impact Report
==================
Version Change: [unversioned template] → 1.0.0
Bump Rationale: MAJOR — initial substantive fill replacing all template placeholders with
  binding governance content derived from M2LB Plattformkonstitusjon v4.0 and
  M2LB Utviklingsretningslinjer v2.0.

Modified Principles: N/A (initial creation)
Added Sections:
  - Core Principles (P1–P4 governing principles with key GL rules)
  - Platform Principles (PP-01 through PP-09)
  - Platform Standards (PS-01 through PS-09)
  - Repository and Code Structure
  - Testing and Quality Standards
  - Operational Standards
  - Governance

Removed Sections: N/A (template placeholders replaced)

Templates Requiring Updates:
  - .specify/templates/plan-template.md ✅ (Constitution Check section is dynamic — no changes needed)
  - .specify/templates/spec-template.md ✅ (no structural changes needed)
  - .specify/templates/tasks-template.md ✅ (no structural changes needed)

Deferred TODOs:
  - RATIFICATION_DATE set to 2026-02-01 (approximate — derived from source docs dated February 2026)
-->

# M2LB Hendelse BiRK Adapter — Constitution

**Source Documents**:
M2LB Plattformkonstitusjon v4.0 (Februar 2026) |
M2LB Utviklingsretningslinjer v2.0 (Mars 2026)

This constitution governs all development in this repository. It is derived from the M2LB
platform-level governing documents and is binding on all implementations, AI agents, and
contributors working in this codebase.

> ⚠️ This constitution is non-negotiable. Deviations require formal architecture review
> and written approval from the M2LB solution architect. Violations in code review block merge.

## Core Principles

The four governing principles are the philosophical foundation. All other rules are read
through this lens.

### P1 — API-First and Headless Architecture

Backend services have no knowledge of the presentation layer. All communication between
layers and services occurs exclusively via published API contracts through the reverse
proxy. No component may bypass contracts.

**Non-negotiable rules (GL-01, GL-02, GL-03):**

- All frontend-to-backend requests MUST route through the reverse proxy (YARP/APIM).
  Hardcoded backend URLs in frontend code are forbidden.
- API contracts (GraphQL SDL or OpenAPI) MUST be designed and approved before
  implementation begins. The contract is the source of truth — not the implementation.
- Blazor components MUST use HttpClient against published contracts. Injection of
  DbContext, IMemoryCache, or any server-side service is an architecture violation.

### P2 — Zero-Trust Security

No user, service, or network component is implicitly trusted, regardless of network
position. Trust is established explicitly at every call boundary through cryptographically
verified identity.

**Non-negotiable rules (GL-06, GL-07, GL-08, GL-09, GL-10, GL-25, GL-32):**

- All authentication MUST be handled by Azure EntraID. Custom authentication mechanisms
  are forbidden.
- Services MUST use Azure Managed Identities for service-to-service authentication.
  Stored credentials (passwords, static keys, connection strings) are forbidden.
- All access decisions MUST be made by calling the Authorization module's evaluation API
  (`POST /api/autorisasjon/v1/evaluer`). No service implements its own access rules.
- All services MUST register their operations via Service Bus at startup.
- On authorization lookup failure for security-critical operations, the system MUST deny
  access (fail-closed). Fail-open is forbidden.
- All services returning information about one identified child (where `barnId` is an input
  parameter) MUST publish a read-log event to Azure Service Bus. Read-log events MUST NOT
  contain personal data — only UUIDs and metadata.
- Security classification MUST be evaluated as part of every data operation, not as a
  post-filter.

### P3 — Domain-Driven, Service-Oriented Design

Services are autonomous and own their own data. Service boundaries are enforced at the
persistence level. Service boundaries are crossed exclusively via published API contracts.

**Non-negotiable rules (GL-15, GL-16, GL-17, GL-18, GL-19, GL-33, GL-34):**

- Each service MUST have its own isolated data layer. Cross-service database queries and
  shared schemas are forbidden.
- API contracts MUST use domain model terminology. Database column names, legacy system
  IDs, and source system codes MUST NOT appear in contracts.
- Business logic MUST be implemented in the domain layer. The API layer orchestrates and
  translates. The presentation layer presents. These responsibilities cannot be swapped.
- All entities with state transitions MUST implement temporal validity (GyldigFra/GyldigTil)
  and immutable change history. Hard deletion of data is forbidden — use soft-delete
  (IsAktiv = false) only.
- All translation from legacy data models to M2LB's domain model MUST occur in the adapter
  layer. The service's internal code and contracts are agnostic to the source system.
- When publishing events to Service Bus as part of a logical operation alongside SQL writes,
  the outbox pattern MUST be used to guarantee event publication.
- Incoming XML from source systems (e.g., BiRK Henvisning-XML) MUST NOT be archived in
  M2LB. Store only the `MeldingsId` reference to Elements. Retrieve XML from Elements when
  needed.

### P4 — Event-Driven Integration

Data mutations are propagated asynchronously via Azure Service Bus. Services publish domain
events as facts, with no knowledge of subscribers. Publishers never assume they are the
only listener.

**Non-negotiable rules (GL-20, GL-21, GL-22, GL-23):**

- All services MUST publish domain events to Azure Service Bus Topics at all data
  mutations. Events are part of the service's published contract.
- Events MUST contain only UUID identifiers and non-sensitive metadata. Personal data
  (names, national ID numbers, addresses, family relations) is forbidden in event payloads.
- All Service Bus consumers MUST be implemented idempotently. A duplicate-delivered event
  MUST produce the same result as a single delivery.
- Event consumers MUST implement retry with exponential backoff. Events that consistently
  fail MUST be routed to the dead letter queue — never silently discarded.

## Platform Principles (Non-Negotiable)

Timeless and technology-agnostic. Cannot be deviated from without a fundamental change in
what M2LB is as a platform. See M2LB ADR register for full rationale.

| ID | Principle | Non-Negotiable Rule |
|----|-----------|---------------------|
| PP-01 | Contractual Communication | All inter-layer/service communication via published API contracts. No direct data layer access across services. |
| PP-02 | Centralized Access Decision | No service makes its own access decisions. All decisions evaluated by the authorization service. Unconfirmed access is denied. |
| PP-03 | Immutable Audit Obligation | All access-related events written to an immutable audit trail. Cannot be modified or deleted — not even by administrators. |
| PP-04 | Security Classification is Absolute | Classified entities are invisible to all without explicit access — no exceptions, no degraded mode, not counted in user-facing operations. |
| PP-05 | Data Has Legal History | No production data is permanently deleted. All entities retain history through temporal validity and soft deactivation. |
| PP-06 | Service Autonomy | Each service owns its persisted data. No service has direct access to another service's storage layer, regardless of technology. |
| PP-07 | Business Logic Belongs to the Domain | Business logic in domain layer. API layer orchestrates. Presentation layer presents. Roles cannot be swapped. |
| PP-08 | Domain Language in Contracts | Contracts and domain events use M2LB's domain language. Source system concepts and IDs do not leak into contracts. |
| PP-09 | Specification and Test are Inseparable | No functionality implemented without a complete specification. No specification complete without automated test cases. |

## Platform Standards (Binding, Technology-Bound)

Binding but revisable when a new technology meets the principled requirement equally well
or better, subject to formal architecture review and a new ADR.

| ID | Standard | Current Binding Technology / Rule |
|----|----------|-----------------------------------|
| PS-01 | Identity Service | Azure EntraID — no alternative authentication mechanisms |
| PS-02 | Service-to-Service Auth | Azure Managed Identities — stored credentials forbidden |
| PS-03 | Network Topology | Segmented VNet; no public IPs on M2LB services; private endpoints only |
| PS-04 | Primary Identifier | UUID v4 — BiRK IDs as secondary references in adapter layer only |
| PS-05 | Event Infrastructure | Service Bus (topics/subscriptions) for internal domain events; Event Hubs for CDC streams |
| PS-06 | Operations Registration | Services register operations at startup; format: `ServiceName:OperationName` |
| PS-07 | API Versioning | Breaking changes as new version; minimum 12-month deprecation notice |
| PS-08 | Observability | Structured logging + `correlation_id` per request; follows IT-department policy |
| PS-09 | Stateless Services | No per-request state stored in process memory between calls |

## Repository and Code Structure

**Binding rules (GL-30, Utviklingsretningslinjer section 2.2):**

This repository follows the `m2lb-[domain]` naming convention. The internal folder
structure MUST conform to:

```
src/
  M2LB.[Module].Api/            ← API layer (controllers, DTOs, middleware)
  M2LB.[Module].Domain/         ← Domain layer (entities, services, rules)
  M2LB.[Module].Infrastructure/ ← Infrastructure layer (EF Core, Service Bus, adapters)
tests/
  M2LB.[Module].Unit/           ← Unit tests
  M2LB.[Module].Integration/    ← Integration tests
specs/                          ← Spec Kit feature documents
.pipeline/                      ← Azure DevOps pipeline YAML
```

One repo per independently deployable unit. Two services in one repo because they
"belong together" is not a valid justification (GL-30).

## Testing and Quality Standards

**Binding rules (PP-09, GL-24):**

- Every new feature MUST have automated tests written alongside the implementation.
  A specification change without a test change is incomplete delivery.
- Authorization scenarios MUST be tested for all operations with access requirements.
  An authorization test is a security specification — not optional.
- Full test suite MUST pass before merging. Test coverage MUST NOT decrease sprint to
  sprint.
- Tests are executable specifications — not afterthoughts to be "added later."

## Operational Standards

**Binding rules (GL-26, GL-27, GL-28, GL-29):**

- All services MUST implement structured logging (e.g., Serilog with JSON output) with
  `correlation_id` propagated from incoming request to all outgoing calls and events.
  `Console.WriteLine` in production code is forbidden.
- All backend services MUST be stateless. No user-specific or request-specific state in
  process memory between calls.
- All outgoing HTTP calls to other services MUST implement retry with exponential backoff,
  explicit timeouts, and the circuit breaker pattern. Calls without timeout configuration
  are forbidden.
- All configuration and secrets MUST be retrieved from Azure Key Vault at runtime via
  Managed Identity. Hardcoded values, connection strings with passwords in config files,
  and credentials in source control are forbidden.
- Sensitive personal data (names, national ID numbers, addresses) MUST NOT appear in logs.
  Log UUIDs only.

## Governance

This constitution is the highest governing document for this repository. It derives from
and is fully subject to the M2LB Platform Constitution v4.0 and M2LB
Utviklingsretningslinjer v2.0. Where the platform documents are more restrictive, the
platform documents govern.

**Amendment procedure:**

- **Platform Principles (PP-*)**: Requires fundamental change in the M2LB platform itself.
  Written proposal → architecture review → approval from solution architect and project
  manager → updates to all affected module constitutions and ADRs.
- **Platform Standards (PS-*)**: Revisable when a new technology meets the principled
  requirement. Written technical justification → architecture review → solution architect
  approval → new ADR replacing the current one.
- **This constitution**: Same process as Platform Standards. All changes documented with
  rationale and date in the changelog below.

**Compliance expectations:**
- All PRs and code reviews MUST verify compliance with PP-01 through PP-09 and the four
  Core Principles. Violations block merge.
- A request to deviate from this constitution is a signal for an amendment process — not
  a signal that the constitution can be ignored.

**Versioning policy:**

- MAJOR: Removal or redefinition of a governing principle or non-negotiable rule.
- MINOR: New principle, section, or materially expanded guidance added.
- PATCH: Clarifications, wording improvements, typo fixes.

**Version**: 1.0.0 | **Ratified**: 2026-02-01 | **Last Amended**: 2026-05-04

---

*Changelog*

| Version | Date | Change | Approved By |
|---------|------|--------|-------------|
| 1.0.0 | 2026-05-04 | Initial constitution created from M2LB Plattformkonstitusjon v4.0 and Utviklingsretningslinjer v2.0 | Løsningsarkitekt (via platform docs) |
