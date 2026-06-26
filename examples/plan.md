# Implementation Plan: Autorisasjon — Initial

**Branch**: `001-autorisasjon-initial` | **Dato**: 2026-02-25 | **Spec**: [auth-func-requirements.md](checklists/auth-func-requirements.md)
**Input**: Feature specification from `specs/001-autorisasjon-initial/`

---

## Summary

Bygge Autorisasjonen — M2LB-plattformens eneste kilde til sannhet for alle tilgangskontrollbeslutninger. Modulen implementerer en to-domene RBAC-modell: generell tilgang (bruker + org-enhet + generell rolle) og barnespesifikk tilgang (bruker + barn + barnespesifikk rolle).

Kjernekapabiliteter:
- **GraphQL admin-API** — forvaltning av operasjoner, roller, tildelinger, relasjoner, nødtilgang, org-struktur og revisjonsspor
- **REST evalueringsAPI** — ytelseskritisk tilgangsbeslutning (p99 < 50ms) backet av Redis-cache
- **Hendelsesdrevet cache-invalidering** — Azure Service Bus Topics, kritiske tilbakekallingshendelser propagert < 5 sek
- **Uforanderlig revisjonsspor** — alle mutasjoner inkl. før/etter-tilstand, håndhevet i EF Core SaveChanges-interceptor
- **Asynkron operasjonsregistrering** — Service Bus-kontrakt for alle tjenester på plattformen

**Teknisk stack**: .NET 10 (C# 13) · Azure SQL (EF Core 10) · Azure Cache for Redis (StackExchange.Redis) · Azure Service Bus · Hot Chocolate 15 (GraphQL) · Microsoft.Identity.Web · MediatR 12

---

## Technical Context

**Language/Version**: .NET 10 (C# 13)
**Primary Dependencies**: Hot Chocolate 15 (GraphQL server), EF Core 10, StackExchange.Redis 2.8+, Azure.Messaging.ServiceBus 7.x, Microsoft.Identity.Web, MediatR 12
**Storage**: Azure SQL (primærdata + revisjonsspor), Azure Cache for Redis (evalueringscache)
**Testing**: xUnit 3.x, FluentAssertions 8, Testcontainers.MsSql + Testcontainers.Redis 4.x, WebApplicationFactory, NSubstitute 5 / FakeItEasy 8
**Target Platform**: Azure Cloud (Norway East), Linux-container, stateless (PS-09)
**Project Type**: Web service / API (REST + GraphQL)
**Performance Goals**: Evalueringsendepunkt p99 < 50ms; cache-propagering for tilbakekallingshendelser < 5 sek
**Constraints**: Norway East-region · GDPR · UUID v4 (PS-04) · Stateless (PS-09) · Ingen direkte DB-tilgang fra andre tjenester (PP-06) · Uforanderlig revisjonsspor (PP-03)
**Scale/Scope**: Hundrevis til lavt tusener av samtidige brukere; titusener av barnerelasjoner; fullt rekursivt org-hierarki

---

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Prinsipp/Standard | Krav | Status |
|-------------------|------|--------|
| PP-01 Kontraktdrevet kommunikasjon | Kun REST + GraphQL-kontrakter eksponert utad. Ingen direkte DB-tilgang fra andre tjenester. | ✅ Pass |
| PP-02 Sentralisert tilgangsbeslutning | Autorisasjonen er den eneste tilgangsautoriteten på plattformen. | ✅ Pass |
| PP-03 Uforanderlig revisjonsplikt | Alle mutasjoner skrives til uforanderlig `Revisjonsoppforing`-tabell via EF Core SaveChanges-interceptor. Ingen DELETE/UPDATE på audit-rader. | ✅ Pass |
| PP-04 Sikkerhetsgradering | Auth-modulen håndterer ikke sikkerhetsgraderte barnedata direkte — kun operasjonstilgang. | ✅ Pass (ikke aktuelt for auth-modulen selv) |
| PP-05 Data har juridisk historikk | Ingen fysisk sletting. Soft delete via `ErAktiv = false` + tilbakekallingstidsstempel overalt. | ✅ Pass |
| PP-06 Tjenesteautonomi | Eget DB-skjema (Azure SQL). Ingen ekstern tjeneste har direkte tilgang til datalagringsenheten. | ✅ Pass |
| PP-07 Forretningslogikk i domenelaget | RBAC-evalueringslogikk og alle invarianter implementert i Domain/Application-lag, ikke i API-laget. | ✅ Pass — Clean Architecture |
| PP-08 Domenespråk i kontrakter | Norsk domenespråk konsistent brukt i GraphQL SDL og OpenAPI. Ingen BiRK-begreper lekker inn. | ✅ Pass |
| PP-09 Spesifikasjon og test er uatskillelige | Alle FK-krav dekkes av tilhørende enhetstester og integrasjonstester. | ✅ Pass |
| PS-01 Azure EntraID | Microsoft.Identity.Web for JWT-validering. Ingen egendefinert autentiseringsmekanisme. | ✅ Pass |
| PS-02 Managed Identities | `DefaultAzureCredential` for alle Azure SDK-er (Service Bus, Key Vault, Redis). Ingen hemmelige connection strings. | ✅ Pass |
| PS-04 UUID v4 | Alle entiteter bruker `Guid` (UUID v4) som primærnøkkel, generert av applikasjonen. | ✅ Pass |
| PS-05 Service Bus + Event Hubs | Operasjonsregistrering (innkommende) og domenehendelser (utgående) via Azure Service Bus Topics. | ✅ Pass |
| PS-06 Operasjonsregistrering | `OperasjonsRegistreringHostedService` publiserer egne operasjoner ved oppstart (FK-1.7). | ✅ Pass |
| PS-07 12 måneders API-varsling | REST-API versjonert via URL-sti `/api/autorisasjon/v1/`. Breaking changes som ny versjon. | ✅ Pass |
| PS-08 Observabilitet | Strukturert logging (OpenTelemetry/Serilog) med `correlation_id` per forespørsel. Helsesjekk-endepunkt: `GET /api/autorisasjon/v1/helse`. | ✅ Pass |
| PS-09 Stateless | Ingen sesjonsstilstand mellom forespørsler. All tilstand i Azure SQL + Redis. | ✅ Pass |

**Ingen gate-feil. Alle konstitusjonsprinsipper er overholdt.**

---

## Architecture Notes

### Evalueringssti: Redis vs. token-claims (FK-4.6, FK-8.5, konstitusjon §2.1)

FK-4.6 og konstitusjon §2.1 angir at generelle roller og organisasjonstilknytning «følger med via EntraID og autentiseringstokenet». Presisering av implementasjonsarkitekturen:

- **Auth-modulen er kilden til sannhet** for `GenerellRolleTildeling`. Rollen administreres i auth-modulens database og caches i Redis.
- **Evalueringsendepunktet** (`POST /api/autorisasjon/v1/evaluer`) leser **eksklusivt fra Redis-cache**. Ingen token-claims benyttes i evalueringsstien. `BrukerId` utledes fra `oid`-claim i JWT.
- **FK-4.6/FK-8.5 er tilfredsstilt** ved at auth-modulens rolletildelinger er tilgjengelig for andre plattformtjenester via EntraID-tokens (rolledata speiles i EntraID-app-roller). Auth-modulen selv konsumerer disse for autentisering/identitet, ikke for evalueringslogikk.
- **Konsekvens**: `TilgangsEvalueringsService` (T083–T084) trenger ikke lese token-claims. Evalueringen er ren Redis-operasjon på hot path.

---

## Project Structure

### Documentation (this feature)

```text
specs/001-autorisasjon-initial/
├── plan.md                        # Dette dokumentet
├── research.md                    # Phase 0 — teknologibeslutninger og ADR-er
├── data-model.md                  # Phase 1 — implementeringsorientert datamodell
├── quickstart.md                  # Phase 1 — lokal oppsett og kjøring
├── auth-domain-model-no.md        # Autoritativ domenemodell (kilde for data-model.md)
├── auth-module-operations.md      # Operasjonskatalog
├── checklists/
│   └── auth-func-requirements.md  # Funksjonelle krav (inkl. avklaringer 2026-02-25)
└── contracts/
    ├── auth-api-contracts-no.md   # Prosakontrakt for GraphQL-administrasjons-API
    ├── auth-event-contracts-no.md # Service Bus hendelseskontrakter + cache-strategi
    ├── auth-graphql-sdl.graphql   # Autoritativt GraphQL-skjema (SDL)
    └── auth-rest-openapi.txt      # OpenAPI 3.1 spec for REST evalueringsAPI
```

### Source Code (repository root)

```text
src/
├── Autorisasjon.Domain/
│   ├── Entities/
│   │   ├── Tjeneste.cs
│   │   ├── Operasjon.cs
│   │   ├── GenerellRolle.cs
│   │   ├── BarnespesifikkRolle.cs
│   │   ├── GenerellRolleTildeling.cs
│   │   ├── BarneRelasjon.cs
│   │   ├── Nodtilgang.cs
│   │   ├── OrgEnhet.cs
│   │   └── Revisjonsoppforing.cs
│   ├── Enums/
│   │   ├── Klassifisering.cs
│   │   └── RevisjonsHandling.cs
│   ├── Repositories/              # Interfaces (f.eks. IBarneRelasjonRepository)
│   ├── Services/                  # Domain service interfaces
│   └── Exceptions/                # Domenespesifikke exceptions
│
├── Autorisasjon.Application/
│   ├── Commands/                  # MediatR IRequest<T> kommandoer + handlere
│   ├── Queries/                   # MediatR IRequest<T> spørringer + handlere
│   ├── Services/
│   │   ├── TilgangsEvalueringsService.cs   # Kjernelogikk for tilgangsevaluering
│   │   └── CacheInvalideringsService.cs    # Hendelse → cache-oppdateringslogikk
│   └── DTOs/
│
├── Autorisasjon.Infrastructure/
│   ├── Persistence/
│   │   ├── AutorisasjonsDbContext.cs
│   │   ├── Migrations/
│   │   ├── Repositories/          # EF Core 10-implementasjoner av domain interfaces
│   │   ├── Configurations/        # IEntityTypeConfiguration per entitet
│   │   └── Interceptors/
│   │       └── AuditInterceptor.cs  # SaveChangesInterceptor → Revisjonsoppforing
│   ├── Cache/
│   │   ├── RedisCacheService.cs
│   │   └── CacheNokler.cs         # Cache-nøkkelkonstanter (jf. hendelseskontrakt 7.3)
│   ├── ServiceBus/
│   │   ├── OperasjonsRegistreringKonsument.cs  # Innkommende fra tjenester
│   │   ├── HendelsesPublisher.cs               # Utgående domenehendelser
│   │   └── CacheOppdateringsKonsument.cs       # Internt — cache-invalidering
│   └── ExternalServices/
│       ├── EntraIdBrukerTjeneste.cs     # Microsoft Graph — brukernavn for visning
│       └── BarnStamdataKlient.cs        # Barn Stamdata — barndetaljer for visning
│
└── Autorisasjon.Api/
    ├── GraphQL/
    │   ├── Types/                 # Hot Chocolate ObjectTypeExtension per entitet
    │   ├── Queries/               # [QueryType] class-based resolvers
    │   └── Mutations/             # [MutationType] class-based resolvers
    ├── Rest/
    │   └── Controllers/
    │       └── AutorisasjonController.cs  # POST /evaluer, GET /tilganger, GET /barn, GET /helse
    ├── BackgroundServices/
    │   ├── OperasjonsRegistreringHostedService.cs  # Publiserer egne ops ved oppstart
    │   └── NodtilgangUtlopsHostedService.cs        # Periodisk utløpssjekk (1 min intervall)
    └── Program.cs

tests/
├── Autorisasjon.UnitTests/
│   ├── Domain/                    # Entitetsinvarianter, evalueringslogikk (pure functions)
│   └── Application/               # Use case-handlere med mocket infrastruktur
├── Autorisasjon.IntegrationTests/
│   ├── Api/                       # WebApplicationFactory + full stack (SQL + Redis i Testcontainers)
│   ├── Persistence/               # EF Core + Testcontainers.MsSql
│   └── Cache/                     # Redis + Testcontainers.Redis
└── Autorisasjon.ContractTests/
    ├── GraphQL/                   # Hot Chocolate schema snapshot-tester
    └── ServiceBus/                # Event schema-validering mot auth-event-contracts-no.md
```

**Structure Decision**: Clean Architecture med 4 lag. `Domain` har null NuGet-avhengigheter. `Application` avhenger kun av `Domain` og MediatR-abstraksjonene. `Infrastructure` implementerer domaine-interfaces med EF Core 10, StackExchange.Redis og Azure SDK. `Api` er den deploybare enheten som hoster REST + GraphQL + bakgrunnstjenester i én Linux-container.

---

## Complexity Tracking

> Ingen konstitusjonelle avvik — tabellen er tom.
