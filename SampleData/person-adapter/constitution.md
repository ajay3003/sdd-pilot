<!--
Sync Impact Report
==================
Version change: N/A (initial creation) → 1.0.0
Source documents:
  - docs/BiRK-Person-adapter-—-Konstitusjon.md (v0.1, March 2026)
  - docs/M2LB-Plattformkonstitusjon.md (v4.0, February 2026)
  - docs/M2LB-Utviklingsretningslinjer.md (v2.0, March 2026)

Added sections:
  - Core Principles (I–VII, derived from adapter P-01–P-10)
  - Security & Authentication
  - Domain Boundary table
  - Repository & Code Structure
  - Governance

Templates updated:
  - .specify/templates/plan-template.md ✅ Constitution Check gates filled in
  - .specify/templates/tasks-template.md ✅ M2LB path conventions added
  - .specify/templates/spec-template.md ✅ Security classification note added

Deferred items: None — all placeholders resolved.
-->

# BiRK Person-adapter Constitution

## Core Principles

### I. Single Responsibility — Pure Integration (P-01)

The adapter has ONE responsibility: translate BiRK person data into PersonModule's
domain format and deliver it via PersonModule's REST ingestion API. It contains no
business logic beyond transformation.

- No BiRK-specific terms, field names, or identifiers MUST leak into PersonModule
  contracts. `EksternId` is the only field that carries a BiRK reference — by
  deliberate design, as a migration handle.
- If BiRK's integration changes, PersonModule's API, domain model, and event
  contracts MUST remain unaffected.
- The adapter models no business domain and owns no domain state. (GL-19)

### II. Disposable by Design (P-02)

The adapter exists ONLY while BiRK owns person data (Phase 1).

- When PersonModule takes over in Phase 2, this adapter MUST be decommissionable
  without changes to PersonModule's API, domain model, or event contracts.
- No other service MUST have a runtime dependency on this adapter.
- Decommission readiness MUST be preserved through every change made to the adapter.

### III. Security Classification is Absolute (P-03 / PP-04)

Children with Kode 6 or Kode 7 (security level 2–3) MUST NEVER reach this adapter.
Primary filtering is at BiRK's source database, before the CDC stream.

- If a record with security level 2 or 3 reaches the adapter despite source-level
  filtering, the adapter MUST:
  1. Reject the record immediately — never forward to PersonModule.
  2. Log the event as a **critical security incident** with full metadata (no PII).
  3. Trigger an immediate operational alert.
- This rule has NO exceptions and NO graceful degradation mode.
- Future expansion to Kode 6/7 requires formal revision of this principle and
  source-level filter removal. P-03 governs until explicitly superseded.

### IV. Idempotency is Mandatory (P-04 / GL-22)

PersonModule's ingestion API is idempotent (PUT keyed on `eksternId`). The adapter
MUST be designed under the assumption that CDC events are delivered more than once.

- Processing the same event twice MUST yield the same outcome as processing it once.
- The adapter MUST never assume a CDC event represents a new record.
- Idempotency MUST be verified with automated tests before any implementation is
  considered complete.

### V. Near-Stateless Operation (P-05 / PS-09)

The adapter MUST persist only two categories of state:

| Allowed state | Purpose |
|---|---|
| Offset / checkpoint | Current position in the Event Hubs stream |
| Fault records | Records that have failed ingestion and are awaiting retry |

No person data, reference data, or business entities MUST be stored in the adapter
beyond in-transit handling. Fault records containing personal data MUST be stored
in a dedicated fault table only while retry is active; they MUST NOT persist
indefinitely.

### VI. Resilient Event Processing (P-06 / P-07 / GL-23)

**Initial full load (P-06)**
- MUST be performed at first startup.
- Order is critical: persons FIRST, then child registrations (child registrations
  reference persons via `eksternId`).
- After extended downtime, the adapter MUST resume from checkpoint OR perform full
  reload if the checkpoint has expired.

**Error handling (P-07)**
- Transient API failures (5xx, timeout): exponential backoff retry.
- Records that cannot be delivered after the maximum retry count MUST be routed to
  dead-letter and trigger an operational alert. Records MUST NEVER be silently
  dropped.
- Validation errors (422) from PersonModule are non-transient: log immediately with
  full diagnostic context, then continue processing subsequent records.

### VII. Platform-Delegated Concerns (P-08 / P-09 / P-10)

The following responsibilities belong to OTHER components and MUST NOT be
re-implemented in the adapter:

| Concern | Delegated to | Reference |
|---|---|---|
| Outbox pattern / atomic transaction | PersonModule | GL-33, P-10 |
| Domain event publishing | PersonModule | GL-20, P-10 |
| Operation registration | Not applicable (no user-facing ops) | GL-09 does not apply — P-09 |
| Authorization logic | PersonModule | GL-08 |
| Reference data lifecycle | PersonModule (auto-creates unknown values) | Adapter maps; never pre-registers |

Batch ingestion (`POST /innmating/batch`) SHOULD be used for initial full load and
high-volume change sets. Single PUT endpoints are used for routine low-volume CDC
streaming. Both paths produce identical outcomes.

## Security & Authentication

### Managed Identity (PS-02)

The adapter MUST authenticate to Azure Event Hubs and PersonModule's REST API via
**Azure Managed Identity**. No credentials MUST appear in:
- `appsettings.json` or any configuration file
- Azure Key Vault (no secret is needed — identity is proven cryptographically)
- Source control

### Network Isolation (PS-03 / GL-11 / GL-12)

- All communication MUST occur over private endpoints within the VNet.
- No public IP address or public DNS entry for this adapter.
- All traffic MUST be encrypted with TLS 1.2 or higher.
- Event Hubs access MUST be scoped to the minimum partitions containing
  person-related CDC data (least-privilege).

### Domain Boundary

The following ownership boundary is absolute. The adapter translates — it never
owns.

| Data domain | Owner (Phase 1) |
|---|---|
| Person identity (name, SSN, DUF, birth date) | BiRK |
| Child registration status (BirkID, type, status, municipality) | BiRK |
| Security level (classification) — levels 0 and 1 only | BiRK |
| Domain model and API contract | PersonModule |
| Reference data (KjønnType, BarnType, BarnStatusType, etc.) | PersonModule |
| Domain event publishing | PersonModule |
| Audit trail | PersonModule |

## Repository & Code Structure

### Repository Convention (GL-30)

Repo name: `m2lb-person-birk-adapter`. Deployed independently from PersonModule.
Changes to adapter transformation logic MUST NOT require a new PersonModule version,
and vice versa.

### Internal Structure (Utviklingsretningslinjer §2.2)

```
src/
  M2LB.PersonBiRKAdapter.Worker/          ← Hosted service, Event Hubs consumption
  M2LB.PersonBiRKAdapter.Domain/          ← Transformation logic (BiRK → PersonModule format)
  M2LB.PersonBiRKAdapter.Infrastructure/  ← Event Hubs client, HTTP client for PersonModule API
tests/
  M2LB.PersonBiRKAdapter.Unit/            ← Transformation logic, Kode 6/7 rejection, idempotency
  M2LB.PersonBiRKAdapter.Integration/     ← End-to-end adapter processing tests
specs/                                     ← Spec-kit documents for features
.pipeline/                                 ← Azure DevOps pipeline YAML
```

### Inherited Platform Rules

This constitution is subordinate to M2LB Plattformkonstitusjon v4.0. Platform
principles PP-01–PP-09 and standards PS-01–PS-09 apply without exception and are
not repeated here.

Key development guidelines that apply directly to this service:

| Guideline | Rule |
|---|---|
| GL-16 | Domain language in contracts — no BiRK field names in PersonModule API |
| GL-19 | Adapter absorbs all legacy complexity — PersonModule stays BiRK-agnostic |
| GL-22 | All event consumers MUST be idempotent |
| GL-23 | Retry with exponential backoff + dead-letter queue for all consumer failures |
| GL-24 | Tests are executable specifications — mandatory before implementation |
| GL-33 | Outbox pattern belongs to PersonModule, not this adapter |

## Governance

- **Hierarchy**: This constitution is subordinate to M2LB Plattformkonstitusjon.
  Where this document is silent, the platform constitution governs. Where it
  elaborates, it is always within the platform constitution's bounds.
- **Amendments**: Require written proposal, architectural review, and written
  approval from the solution architect. All dependent spec-kits MUST be updated
  before the amendment takes effect.
- **Compliance review**: All PRs MUST verify compliance with Principles I–VI and
  the security classification requirement in Principle III. Violations require
  architectural review before merge.
- **Test mandate (PP-09 / GL-24)**: No feature MUST be delivered without complete
  specification and corresponding automated tests. A specification change without a
  test change is incomplete.
- **Decommission**: This adapter and its constitution become obsolete when
  PersonModule takes over as data owner (Phase 2 transition). The constitution MUST
  be formally closed and archived at that point.
- **Version policy**: MAJOR for principle removal or redefinition; MINOR for new
  principles or material guidance additions; PATCH for clarifications.

**Version**: 1.0.0 | **Ratified**: 2026-04-20 | **Last Amended**: 2026-04-20
