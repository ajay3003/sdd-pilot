<!-- Sync Impact Report
Version change: 1.0.0 → 1.0.1
Modified principles: none (names translated to English, content unchanged)
Added sections: none
Removed sections: none
Templates reviewed:
  - .specify/templates/plan-template.md ✅ No changes needed
  - .specify/templates/spec-template.md ✅ No changes needed
  - .specify/templates/tasks-template.md ✅ No changes needed
  - .specify/templates/constitution-template.md ✅ Source template (read-only, not modified)
Deferred items (intentional placeholders):
  - TODO(WORM_RETENTION_PERIOD): Å-01 — retention period for leselogg events not yet
    confirmed. Placeholder: 10 years. To be agreed with legal owner for M2LB.
  - ~~TODO(HOSTING_STRATEGY)~~: **Resolved 2026-04-27** — Azure App Service (.zip deploy) as
    interim; planned migration to Azure Container App once current platform blocker resolves.
    See spec.md Clarifications (Session 2026-04-27, Å-03).
-->

# Revisjonstjenesten — Constitution

## Core Principles

### I. Immutable Audit Trail

The audit trail of Revisjonstjenesten is absolutely immutable. Events written to Azure
Immutable Blob Storage (WORM) cannot be modified, deleted, or overwritten by any actor —
including system administrators and the solution architect. The service has no delete
operations, no update operations, and no mechanism for correcting written events.

Incorrectly published events from source services are the responsibility of that source
service — Revisjonstjenesten stores exactly what it receives, unchanged. This principle
cannot be waived without invalidating the service's legal foundation. (→ PP-03, ADR-003)

### II. Single, Bounded Responsibility

Revisjonstjenesten has one single technical responsibility: consume `LeseloggHendelse` events
from Azure Service Bus and write them unchanged to Azure Immutable Blob Storage. The service
MUST NOT implement search, filtering, alerting, semantic validation of event content, or
exposure of data to end users.

Expanding the scope of responsibility requires a new version of this constitution and approval
from the solution architect. (→ ADR-003, ADR-023)

### III. Idempotent Event Consumption

Azure Service Bus guarantees at-least-once delivery. Revisjonstjenesten MUST implement
idempotent writes based on `hendelsesId`: an event with an already stored `hendelsesId` MUST
be silently ignored without error and without writing again.

Retry with exponential backoff and dead letter queue handling is REQUIRED. No event is
silently discarded on failure — processing errors MUST be logged and routed to the dead letter
queue for manual follow-up and operational alerting. (→ GL-22, GL-23, PP-03)

### IV. Zero Custom Persistence Layer

Revisjonstjenesten has NO Azure SQL database. Azure Immutable Blob Storage is the only
storage mechanism. The storage format is one JSON file per event with the filename pattern:

```
{year}/{month}/{day}/{hendelsesId}.json
```

No alternative persistence, intermediate relational storage, or local state storage may be
introduced without a formal architecture review. (→ ADR-003, PP-06)

### V. Specification and Tests Are Inseparable

No functionality is implemented without a complete specification. No specification is complete
without accompanying automated test cases. The following scenarios MUST be covered by
integration tests before functionality is considered delivered:

- Successful receipt and write of a `LeseloggHendelse`
- Idempotency handling on duplicate delivery (same `hendelsesId`)
- Retry and dead letter queue routing on transient Service Bus failure
- Correct filename pattern in Immutable Blob Storage

A specification change without a corresponding test change is incomplete. (→ PP-09, GL-24)

## Regulatory and Technology Constraints

The audit trail of Revisjonstjenesten is a legal instrument, not merely a technical one.
The following regulatory requirements depend directly on the service functioning correctly:

| Requirement | Legal basis | Consequence of non-compliance |
|-------------|-------------|-------------------------------|
| Processing security | GDPR art. 5(1)(f), art. 32 | Inability to detect unauthorised access to child data |
| Record of processing activities | GDPR art. 30 | Missing documentation of who processes personal data |
| Right of access for data subjects | GDPR art. 15 | Inability to respond to access requests from children/guardians |
| Statutory auditability | Barnevernloven, arkivloven | No legally valid trail of access to sensitive child welfare data |

**Technology framework:**

- Implementation: .NET Worker Service
- Hosting: Azure App Service (Linux, .zip deploy) — interim solution; planned migration to
  Azure Container App once current platform blocker is resolved (Å-03, resolved 2026-04-27)
- Persistence: Azure Immutable Blob Storage with WORM policy (Write Once, Read Many)
- Message queue: Azure Service Bus — queue `leselogg` for `LeseloggHendelse`
- Retention period: TODO(WORM_RETENTION_PERIOD) — Å-01: to be confirmed with legal owner.
  Placeholder: 10 years. Configured in infrastructure, not in service code.
- Event model: fields defined in GL-32 in the development guidelines

**Data minimisation (GDPR art. 5(1)(c)):** The service stores only what it receives.
It is the responsibility of the source service (GL-32) to ensure that the event payload
contains only UUID identifiers and metadata — never sensitive personal data such as names,
national identity numbers, or addresses. Revisjonstjenesten performs no content validation.

## Platform Constitution Inheritance

Revisjonstjenesten fully inherits from the M2LB Platform Constitution (version 4.0,
February 2026). Where this constitution is silent, the platform constitution applies.
Platform principles PP-01 through PP-09 and platform standards PS-01 through PS-09 apply
in full without being restated here.

The following platform rules are particularly relevant to Revisjonstjenesten:

| Rule | Name | Relevance to Revisjonstjenesten |
|------|------|---------------------------------|
| PP-02 | Centralised access decision | Revisjonstjenesten implements no access rules of its own |
| PP-03 | Immutable audit obligation | Absolute foundation — see Principle I |
| PP-06 | Service autonomy | No direct database access to other services |
| PP-09 | Specification and tests are inseparable | Restated as Principle V |
| PS-02 | Managed Identities | Authentication against Service Bus and Blob Storage |
| PS-05 | Event infrastructure | Service Bus as the sole inbound channel |
| PS-08 | Observability | Structured logging with `correlation_id`, health-check endpoint |
| PS-09 | Services are stateless | Prerequisite for horizontal scaling with correct idempotency |
| GL-22 | Idempotent consumers | See Principle III |
| GL-23 | Error handling with backoff and DLQ | See Principle III |
| GL-25 | Fail-closed | Processing errors: retry and DLQ — never silent discard |
| GL-28 | Observability | Structured logging, `KorrelasjonId`, metrics to Azure Monitor |
| GL-32 | Read log via Service Bus | Definition of the `LeseloggHendelse` contract consumed |

## Governance

The constitution of Revisjonstjenesten is subordinate to the M2LB Platform Constitution
(document hierarchy level 2). Amendment process:

- **Principle changes (Core Principles I–V):** Requires a written proposal, architecture
  review, and approval by the solution architect. All affected spec-kit artefacts are updated
  before the change takes effect. `CONSTITUTION_VERSION` is bumped MAJOR.
- **Technology or regulatory updates (sections 2–3):** May be revised at a module review
  within the bounds of the platform constitution. `CONSTITUTION_VERSION` is bumped MINOR.
- **Clarifications and refinements:** `CONSTITUTION_VERSION` is bumped PATCH.

All spec-kit artefacts (spec.md, plan.md, tasks.md) for Revisjonstjenesten MUST verify
compliance with Principles I–V (Constitution Check) during planning and review. Functionality
that apparently conflicts with Principle II (single responsibility) MUST be explicitly
justified and approved by the solution architect.

Refer to `docs/Revisjonstjenesten-—-Konstitusjon.md` and `docs/M2LB-Plattformkonstitusjon.md`
for the rationale behind architectural decisions. ADR-003 and ADR-023 are the primary
references.

**Changelog:**

| Version | Date | Change | Approver |
|---------|------|--------|----------|
| 1.0.0 | 2026-03-01 | Initial Spec Kit constitution from Revisjonstjenesten-—-Konstitusjon.md v0.1 | Solution Architect |
| 1.0.1 | 2026-04-27 | Translated to English; domain-specific names unchanged | Solution Architect |

**Version**: 1.0.1 | **Ratified**: 2026-03-01 | **Last Amended**: 2026-04-27
