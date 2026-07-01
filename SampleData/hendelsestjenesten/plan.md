# Implementation Plan: Hendelsestjenesten

**Branch**: `001-hendelsestjenesten` | **Date**: 2026-04-27 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `specs/001-hendelsestjenesten/spec.md`

---

## Summary

Hendelsestjenesten gir saksbehandlere i barnevernet en pålitelig, tilgangsstyrt
hendelsestidslinje per barn. Tjenesten mottar hendelsesdata fra BiRK via Hendelsesadapteren
over REST, lagrer disse med uforanderlig versjonert historikk i Azure SQL, og eksponerer
dataene til saksbehandlere via GraphQL. Domenehendelser og revisjonslogg publiseres atomisk
via utboks-mønster til Azure Service Bus.

**Key constraints**: No hard deletes ever. BarnId links exactly once (null → UUID). All
operations evaluated by Autorisasjon API (fail-closed). Outbox mandatory for all Service Bus
publishes.

---

## Technical Context

**Language/Version**: C# 14 / .NET 10 LTS
**Primary Dependencies**: ASP.NET Core 10, EF Core 10, Hot Chocolate 15 (GraphQL),
`Azure.Messaging.ServiceBus` 7.x, OpenTelemetry .NET, Serilog
**Storage**: Azure SQL (EF Core 10, code-first migrations)
**Testing**: xUnit, FluentAssertions, Testcontainers (SQL Server image)
**Target Platform**: Azure App Service (or AKS) behind YARP reverse proxy
**Project Type**: Web service — REST intake API + GraphQL read API
**Performance Goals**: P95 < 2 s for `hentHendelserForBarn` at 2 000 concurrent users
**Constraints**: 99.9% uptime; 10-year data retention; no hard deletes; fail-closed on auth failure
**Scale/Scope**: Up to 2 000 concurrent saksbehandlere; unlimited events per barn

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Rule | Status | Notes |
|------|--------|-------|
| PP-01 API-First: contracts approved before implementation | ✅ PASS | REST (intake) + GraphQL (reads) contracts in `docs/` |
| PP-02 Zero-Trust: all access via Autorisasjon API | ✅ PASS | All 5 operations call `POST /api/autorisasjon/v1/evaluer`; fail-closed |
| PP-02 Leselogg for barnId read ops (GL-32) | ✅ PASS | `hentHendelserForBarn` + `hentHendelse` publish to `revisjon.leselogg` |
| PP-03 DDD: service owns its data | ✅ PASS | Azure SQL owned exclusively; no cross-service JOINs |
| PP-03 UUID v4 for all entities (PS-04) | ✅ PASS | All entity IDs are platform-generated UUIDs |
| PP-03 BiRK IDs are secondary references | ✅ PASS | `BirkHendelsesId`, `BirkTiltakPK` not in API contracts |
| PP-04 Event-driven: outbox for all Service Bus publishes (GL-33) | ✅ PASS | `HendelsesPublisering` outbox table; background publisher |
| PP-04 No personal data in event payloads (GL-21) | ✅ PASS | `HendelsesRegistrert` contains only UUIDs + metadata |
| PP-04 Idempotent consumers (GL-22) | ✅ PASS | `TjenesteOpprettet` consumer checks MessageId |
| H-01 Versioned history is non-negotiable | ✅ PASS | `HendelsesVersjon` append-only; no delete operations anywhere |
| H-02 HendelsesType is structured data in DB tables | ✅ PASS | Reference table seeded at migration; no hardcoded enums |
| H-03 BarnId primary; BirkTiltakPK secondary | ✅ PASS | BarnId from Personmodulen; BirkTiltakPK only for async linking |
| H-04 Loosely coupled to faglig oppfølging | ✅ PASS | Publishes `HendelsesRegistrert`; no downstream dependencies |
| H-05 Involverte support structured + unstructured | ✅ PASS | `InternBrukerId` (M02+) and `EksternBeskrivelse` (M01/BiRK) |
| H-07 Type-specific extensions on shared core | ✅ PASS | `InngrepDetalj` + `RommingsDetalj` on shared `HendelsesVersjon` |
| PS-01 EntraID sole identity provider | ✅ PASS | OAuth2/OIDC Bearer validated against EntraID |
| PS-02 Managed Identity for service-to-service auth | ✅ PASS | Adapter uses Managed Identity; Key Vault for all secrets |
| PS-06 Operations registered at startup (GL-09) | ✅ PASS | 5 operations published to Service Bus at startup; service refuses start on failure |
| PS-08 Observability (health, Serilog, OTel) | ✅ PASS | `/helse/live`, `/helse/ready`; Serilog JSON; OpenTelemetry to Azure Monitor |
| GL-10 Classification + audit trail | ✅ PASS | Leselogg via outbox; Autorisasjon evaluated for every operation |
| GL-25 Fail-closed on auth failure | ✅ PASS | HTTP 503 returned when Autorisasjon API unreachable |
| GL-26 No secrets in code or appsettings | ✅ PASS | All secrets from Azure Key Vault at runtime |
| GL-33 Outbox mandatory | ✅ PASS | Both `HendelsesRegistrert` and leselogg via outbox table |

**All constitution gates pass. No violations. No complexity justification required.**

---

## Project Structure

### Documentation (this feature)

```text
specs/001-hendelsestjenesten/
├── plan.md              # This file
├── research.md          # Phase 0: Technology decisions
├── data-model.md        # Phase 1: Persistence schema
├── quickstart.md        # Phase 1: Local development guide
├── contracts/
│   ├── rest-intake.md   # Phase 1: REST API summary
│   ├── graphql-read.md  # Phase 1: GraphQL API summary
│   └── events.md        # Phase 1: Service Bus contracts
└── tasks.md             # Phase 2: /speckit-tasks output (not created here)
```

**Existing design documents in `docs/`** (authoritative references, not duplicated here):
- `docs/Hendelsestjenesten-—-Domenemodell.md` — full entity model v0.3
- `docs/Hendelsestjenesten-—-REST-API.md` — full OpenAPI spec v1.0.0
- `docs/Hendelsestjenesten-GraphQLskjema-(SDL).md` — full GraphQL SDL
- `docs/Hendelsestjenesten-—-Hendelseskontrakter.md` — full event contracts
- `docs/Hendelsestjenesten-—-Brukerhistorier-og-funksjonelle-krav.md` — user stories
- `docs/Hendelsestjenesten-—-Testspesifikasjon.md` — test specification
- `docs/Hendelsestjenesten-—-Operasjonskatalog.md` — operations catalogue

### Source Code (repository root)

```text
src/
  M2LB.Hendelse.Api/
  ├── Controllers/
  │   ├── InnmatingController.cs       ← PUT /innmating/inngrep, /innmating/romming
  │   ├── ReferansedataController.cs   ← GET /referansedata/*
  │   └── HelseController.cs           ← GET /helse/live, /helse/ready
  ├── GraphQL/
  │   ├── HendelseQuery.cs             ← hentHendelserForBarn, hentHendelse
  │   └── Types/                       ← Hot Chocolate type definitions
  ├── Middleware/
  │   └── KorrelasjonsIdMiddleware.cs  ← Propagates W3C TraceContext
  ├── Startup/
  │   └── OperasjonRegistrering.cs     ← Publishes ops to Service Bus at startup
  └── DTOs/

  M2LB.Hendelse.Domain/
  ├── Entities/
  │   ├── Hendelse.cs
  │   ├── HendelsesVersjon.cs
  │   ├── Involvert.cs
  │   ├── InngrepDetalj.cs
  │   └── RommingsDetalj.cs
  ├── ReferenceData/
  │   ├── HendelsesType.cs
  │   ├── HjemmelType.cs
  │   ├── RommingKategoriType.cs
  │   └── TvangsProtokollStatusType.cs
  ├── Services/
  │   ├── HendelsesInnmatingTjeneste.cs    ← Idempotent intake, versioning, outbox insert
  │   ├── HendelsesLeseTjeneste.cs         ← GraphQL data fetch + leselogg outbox
  │   └── BarnKoblingTjeneste.cs           ← Async BarnId linking via TjenesteOpprettet
  └── Interfaces/
      └── IHendelsesRepository.cs

  M2LB.Hendelse.Infrastructure/
  ├── Data/
  │   ├── HendelseDbContext.cs
  │   ├── Repositories/
  │   │   └── HendelsesRepository.cs
  │   └── Migrations/
  ├── ServiceBus/
  │   ├── OutboxPublisher.cs               ← IHostedService: polls + publishes outbox
  │   ├── TjenesteOpprettetConsumer.cs     ← IHostedService: consumes TjenesteOpprettet
  │   └── OperasjonRegistreringClient.cs
  └── Authorization/
      └── AutorisasjonClient.cs            ← POST /api/autorisasjon/v1/evaluer

tests/
  M2LB.Hendelse.Unit/
  M2LB.Hendelse.Integration/
specs/
.pipeline/
README.md
```

**Structure Decision**: Standard M2LB 3-layer architecture (Api / Domain / Infrastructure) per
constitution. `m2lb-hendelser-adapter` is a separate repository — not included here.

---

## Complexity Tracking

> No constitution violations — section empty per instructions.
