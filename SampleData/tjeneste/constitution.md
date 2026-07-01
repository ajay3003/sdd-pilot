<!--
SYNC IMPACT REPORT
==================
Version change: [template] → 1.0.0
Added sections:
  - Platform Principles (PP-01–PP-09)
  - Module Principles (MP-01–MP-05)
  - Platform Standards (PS-01–PS-09)
  - Development Guidelines Reference (GL-01–GL-34)
  - Security & Compliance Requirements
  - Governance
Removed sections: N/A (initial fill from template)
Templates reviewed:
  - .specify/templates/plan-template.md ✅ no changes needed
  - .specify/templates/spec-template.md ✅ no changes needed
  - .specify/templates/tasks-template.md ✅ no changes needed
Deferred TODOs:
  - RATIFICATION_DATE set to 2026-03-01 (inferred from module constitution v0.1, "Mars 2026")
-->

# Tjenestemodul Constitution

> **Arver fra:** M2LB Plattformkonstitusjon v4.0 (Februar 2026)
> **Gjelder for:** `m2lb-tjeneste` repo — domenelogikk, BiRK-integrasjonslag, API og tester
> **Domenekontekst:** Forvalter og eksponerer informasjon om barns aktive og historiske
> tjenester (BiRK: «Tiltak») i andrelinjebarnevernet. M01-scope: kun visning av BiRK-data.

---

## Platform Principles (PP) — Inherited, Non-Negotiable

These principles are inherited from the M2LB Platform Constitution.
They apply in full without exception. Derogations require formal architecture review.

### PP-01 — Contract-Driven Communication

All communication between layers and services MUST occur exclusively via published
API contracts. Backend has no knowledge of the presentation layer. No service accesses
another service's data layer directly.

### PP-02 — Centralised Access Decision

No service makes its own access decisions. All access decisions MUST be evaluated by
the dedicated Authorisation service. Access that cannot be confirmed MUST be denied.

### PP-03 — Immutable Audit Trail

All access-related events MUST be written to an immutable audit trail. The trail MUST
NOT be modified, deleted, or adjusted retroactively — including by system administrators.

### PP-04 — Security Classification is Absolute

Security-classified entities are invisible to all users without explicit access — without
exception, without degraded mode, and without appearing in count operations in user context.
Classification is evaluated as part of every data operation, not as post-hoc filtering.

### PP-05 — Data Has Legal History

No production data is permanently deleted. All entities with business significance MUST
retain history through temporal validity and soft deactivation.

### PP-06 — Service Autonomy

Each service owns its persisted data. No service has direct access to another service's
data storage, regardless of technology. Domain boundaries are enforced at the persistence level.

### PP-07 — Business Logic Belongs to the Domain

Business logic MUST be implemented in the domain layer. The API layer orchestrates and
translates. The presentation layer presents. None of these may take over each other's
responsibilities.

### PP-08 — Domain Language in Contracts

API contracts and domain events use the platform's domain language. Concepts, ID formats,
and data models from source systems (BiRK) MUST NOT leak into contracts.

### PP-09 — Specification and Tests are Inseparable

No functionality is implemented without a complete specification. No specification is
complete without corresponding automated test cases. A specification change without a
test change is incomplete.

---

## Module Principles (MP) — Tjenestemodul-Specific

### MP-01 — Read-Only in M01

Tjenestemodulen MUST NOT support creation, modification, or deletion of tjenester in M01.
All data is read-only for M2LB users. The scope is exclusively the display of existing BiRK data.

### MP-02 — Data Minimisation

Only fields necessary for the module's stated purpose MUST be stored. The integration layer
implements configurable whitelist filtering — only fields defined in configuration are written
to `birk_staging`. Extension with new fields requires an explicit, controlled operation including
a new BiRK export.

### MP-03 — BiRK Terminology Does Not Leak Out

The external API MUST expose M2LB terminology exclusively. BiRK field names are never visible
outside the `birk_staging` schema and the field-mapping configuration file. The domain model and
all external API contracts are BiRK-agnostic.

### MP-04 — Loose Coupling to Personmodulen

`BarnId` is a loose reference — Tjenestemodulen MUST NOT enforce referential integrity against
Personmodulen. Access control based on a child's security level is enforced by Autorisasjonsmodulen,
not by Tjenestemodulen. Tjenester with `BarnId = null` are not visible in the external API.

### MP-05 — Integration Layer and Domain Phase Out Together

The BiRK integration layer and the domain model share the same lifetime. When BiRK is decommissioned,
the module is rewritten from scratch. There is no expectation of preserving current code beyond the
BiRK period. No legacy logic from the integration layer may leak into domain classes.

---

## Platform Standards (PS) — Binding, Technology-Specific

Standards are binding but may be revised through formal architecture review when a replacement
satisfies the underlying principle equally well or better.

| ID | Standard | Technology |
|----|----------|------------|
| PS-01 | All authentication handled by Azure EntraID | Azure EntraID |
| PS-02 | Service-to-service auth via Azure Managed Identities; no stored secrets | Managed Identities |
| PS-03 | Segmented VNet architecture; no public IPs on M2LB services | Azure VNet / Private Endpoints |
| PS-04 | All entities identified with UUID v4; BiRK IDs as secondary references in adapter only | UUID v4 |
| PS-05 | Internal async domain events via Azure Service Bus; CDC imports via Event Hubs/Debezium | Service Bus, Event Hubs |
| PS-06 | All services register operations in Autorisasjonsmodulens register at startup via Service Bus | Service Bus queue |
| PS-07 | Breaking API changes introduced as new version; old versions retained ≥ 12 months | URL-path versioning `/v1/` |
| PS-08 | All services expose health-check endpoint and structured logging with `correlation_id` | Azure Monitor / IT policy |
| PS-09 | Backend services are stateless; no state stored between requests in process memory | Stateless / Azure Cache |

---

## Security & Compliance Requirements

These requirements apply to all operations within Tjenestemodulen that handle or expose
data about children.

- **Fail-Closed**: Upon authorisation lookup failure or security-critical validation errors,
  access MUST be denied (GL-25). Never grant access by default on error.
- **Read-Log**: All read operations where `barnId` is an input parameter MUST publish a
  `LeseloggHendelse` to Azure Service Bus after access is confirmed (GL-32). The event MUST
  contain only UUIDs and metadata — never personal data.
- **Outbox Pattern**: Events published to Service Bus as part of a SQL write MUST use the
  transactional outbox pattern (GL-33) to guarantee delivery without distributed transactions.
- **Classification Gate**: Security classification (PP-04) MUST be evaluated as part of the
  authorisation call — never as a post-hoc filter on query results.
- **Audit Immutability**: Audit log entries MUST be written to Azure Service Bus and persisted
  by Revisjonstjenesten to Azure Immutable Blob Storage (PP-03, ADR-003).
- **No PII in Events**: Service Bus payloads MUST contain only UUID identifiers and metadata.
  Names, national IDs, addresses, and family information are prohibited (GL-21, PP-10).
- **TLS Everywhere**: All inter-service communication MUST use TLS. All databases MUST use
  Azure SQL TDE. Minimum TLS version: 1.2 (GL-12).

---

## Source Code & Repository Structure

```
m2lb-tjeneste/
  src/
    M2LB.Tjeneste.Api/            ← API layer (controllers, DTOs, middleware)
    M2LB.Tjeneste.Domain/         ← Domain layer (entities, services, rules)
    M2LB.Tjeneste.Infrastructure/ ← Infrastructure (EF Core, Service Bus, BiRK adapter)
  tests/
    M2LB.Tjeneste.Unit/           ← Unit tests
    M2LB.Tjeneste.Integration/    ← Integration tests
  specs/                           ← Reference to relevant spec-kit documents
  .pipeline/                       ← Azure DevOps pipeline YAML
  README.md
```

**Database schemas:**
- `birk_staging` — mirrors BiRK tables including naming; temporary, removed when BiRK retires
- `tjeneste` — M2LB domain model; transformed and denormalised from staging; BiRK-agnostic

**Data flow:**
```
BiRK → endringsstrøm → integrasjonslag (filtrering + navnemapping) → birk_staging → tjeneste
```

---

## Development Guidelines Reference

All 34 platform guidelines (GL-01–GL-34) from *M2LB Utviklingsretningslinjer v2.0* are binding.
Critical gates for this module:

| ID | Rule | Why Critical Here |
|----|------|-------------------|
| GL-08 | Call authorisation eval API — no local access rules | Every `GET /tjenester/{barnId}` requires eval |
| GL-09 | Register operations at startup via Service Bus | `Tjeneste:Se`, `Tjeneste:List` must be registered |
| GL-10 | Respect security classification + write audit trail | PP-04 absolute; children may be Kode 6/7 |
| GL-19 | Adapter layer absorbs BiRK complexity | MP-03, MP-05: BiRK names never enter domain |
| GL-22 | Event consumers are idempotent | `BarnRegistrert` consumer must handle re-delivery |
| GL-32 | Read-log for barnespesifikk reads via Service Bus | GDPR Art. 15 + Art. 5(2) accountability |
| GL-33 | Outbox pattern for guaranteed Service Bus publishing | `TjenesteOpprettet` must not be lost |

---

## Governance

This constitution is subordinate to the M2LB Platform Constitution (v4.0).
Where this constitution is silent, the platform constitution governs.
Where this constitution elaborates, it does so within the platform constitution's bounds.

**Amendment procedure:**
1. Written proposal with technical justification
2. Architecture review by solution architect
3. Approval from solution architect and module owner
4. Update of this document, any affected ADRs, and dependent spec-kit documents before
   the change takes effect

**Versioning policy (semantic):**
- **MAJOR**: Principle removal, redefinition, or backward-incompatible governance change
- **MINOR**: New principle, new section, or materially expanded guidance
- **PATCH**: Clarifications, wording, typo corrections

**Compliance review:** All PRs to `m2lb-tjeneste` MUST verify compliance with PP-01–PP-09,
MP-01–MP-05, and the critical GL gates listed above before merging.

> A request to deviate from this constitution is a signal for an amendment process —
> not a signal that the constitution can be ignored.

---

**Version**: 1.0.0 | **Ratified**: 2026-03-01 | **Last Amended**: 2026-04-13
