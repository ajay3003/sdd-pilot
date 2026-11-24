---

description: "Task list for SkoleService (formerly Skoletjenesten) — school attendance reporting from private institutions and foster homes"
---

# Tasks: SkoleService (formerly Skoletjenesten) — school attendance reporting from private institutions and foster homes

**Input**: Design documents from `SkoleService/specs/001-skoleservice/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Included. The module constitution's PP-09 ("No specification is complete without corresponding automated test cases") and research §20 require them. Within each story, test tasks come first and must fail before the implementation tasks are done.

**Organization**: Tasks are grouped by user story. The phase order follows plan.md's "Suggested build order", not raw priority. The lock (US3, P1) freezes the expected population, so it needs Utdanning (US7) and the population and coverage calculators (US6) first. See [Dependencies & Execution Order](#dependencies--execution-order).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to (US1–US8)
- Every task names its exact file path(s)

## Path Conventions

All paths are from the repository root. The module's projects:

- `SkoleService/src/M2LB.SkoleService.Api/` — Sdk.Web; translates and orchestrates only (PP-07)
- `SkoleService/src/M2LB.SkoleService.Domain/` — namespaces `Skolenaervaer`, `Utdanning`, and `Felles` for the ports and value types both contexts use
- `SkoleService/src/M2LB.SkoleService.Infrastructure/` — EF Core (two DbContexts), Wolverine, clients, encryption, delivery, jobs
- `SkoleService/tests/M2LB.SkoleService.Unit/`, `SkoleService/tests/M2LB.SkoleService.Integration/`, `SkoleService/tests/load/`

Completed tasks keep the Norwegian names of their time (`Felles`, `Rapportinntak`, `Laas/Periodelaaser.cs`, `rapportversjon`, …).
Phase 13 renamed everything to English; `specs/002-translate-source-english/name-map-review.md` maps each old name to the new one.

**Deviation from plan.md's tree**: the ports `IPersonoppslag`, `IPlasseringskilde`, `IFeltkryptering` and `IAutorisasjonsfilter` live in `Domain/Felles/Porter/`, not `Domain/Skolenaervaer/Porter/`. Utdanning uses `IPersonoppslag` and `IFeltkryptering` too, and if the ports sat in `Skolenaervaer/`, Utdanning would depend on that context's namespace (MP-07).

## External gates (not tasks; they block where marked)

| Gate | Blocks | Owner |
|---|---|---|
| G-1 Module constitution approval (flagged by user decision 2026-10-08) | Only work that depends on a later constitution change | SA, module owner, DPO |
| ~~ADR-028 Parquet.Net sign-off (T014 drafts it)~~ **Approved 2026-10-14** by Ole Kristian Sjøbakken; Parquet.Net 6.1.0 | ~~T105, T119–T122~~ unblocked, built 2026-10-14 | Tech lead |
| DEP-01, DEP-08 Person machine lookups | Go-live (stubs until then) | Development team (Person) |
| DEP-02, DEP-07 Tjeneste placements (go/no-go 1 Nov 2026) | Go-live, SC-001 (stubs until then) | Development team (Tjeneste) |
| OQ-12 delivery field mapping | Contract `1.0.0-utkast` until agreed | Product owner, Bufdata |
| Education code list values | `Utdanning:Kodelister` values in T008 | Product owner |
| OQ-25 retention values | Activating T151 | DPO |
| DPO sign-off of the EXC-05 blind-index amendment (constitution v2.4.0; Solution Architect approved 2026-10-12) | Nothing: work proceeds on the assumption that the DPO approves (module owner, 2026-10-12). If the DPO refuses, T035, T083 and T088 must be redesigned before production | DPO |
| Skoletjenesten's Entra app registration with app roles `Skole.Rapportinntak` and `Skole.Utdanningsinnmating` and a published token audience, and all Skole infrastructure (Container App, SQL, storage, Key Vault, Service Bus entities) | Deployed environments; QA end-to-end with Meldingsutvekslermottaket and SkoleAdapter | Platform team (scripts are written outside this feature) |

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution, projects, packaging, local orchestration and pipelines

- [X] T001 Create `SkoleService/M2LB.SkoleService.slnx` listing the five projects below, and `SkoleService/global.json` with `{"test":{"runner":"Microsoft.Testing.Platform"}}` (same as `TjenesteService/global.json`)
- [X] T002 [P] Create `SkoleService/src/M2LB.SkoleService.Domain/M2LB.SkoleService.Domain.csproj` (`net10.0`, Nullable, ImplicitUsings, TreatWarningsAsErrors, no package references) with empty folders `Skolenaervaer/`, `Utdanning/`, `Felles/Porter/`
- [X] T003 [P] Create `SkoleService/src/M2LB.SkoleService.Infrastructure/M2LB.SkoleService.Infrastructure.csproj` referencing Domain, with these exact pins: `M2LB.Common` 2.3.0, `Microsoft.EntityFrameworkCore.SqlServer` 10.0.12, `Microsoft.EntityFrameworkCore.Design` 10.0.12 (PrivateAssets all), `Azure.Storage.Blobs` 12.29.2. Also add `Azure.Security.KeyVault.Keys` and `Azure.Security.KeyVault.Secrets`, each pinned to its current stable version, with the versions recorded in the csproj. Do **not** add Parquet.Net (see T119)
- [X] T004 [P] Create `SkoleService/src/M2LB.SkoleService.Api/M2LB.SkoleService.Api.csproj` (Sdk.Web) referencing Domain and Infrastructure, with pins `M2LB.Common` 2.3.0, `HotChocolate.AspNetCore` 16.6.7 and `Microsoft.Extensions.Http.Resilience` 10.10.0. Add a minimal `Program.cs` (`public partial class Program;` for `WebApplicationFactory`) and `Properties/launchSettings.json`
- [X] T005 [P] Create `SkoleService/tests/M2LB.SkoleService.Unit/M2LB.SkoleService.Unit.csproj` referencing Domain, Infrastructure and Api, with `xunit.v3` 4.0.1, `Shouldly`, `NSubstitute` and `Microsoft.Extensions.TimeProvider.Testing`, pinned to the versions in `TjenesteService/tests/M2LB.TjenesteService.Unit/M2LB.TjenesteService.Unit.csproj` where they are present
- [X] T006 [P] Create `SkoleService/tests/M2LB.SkoleService.Integration/M2LB.SkoleService.Integration.csproj` referencing Api, with the Unit packages plus `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.MsSql`, `Testcontainers.Azurite` and `WireMock.Net`, pinned as in `TjenesteService/tests/M2LB.TjenesteService.Integration/M2LB.TjenesteService.Integration.csproj` where they are present
- [X] T007 [P] Create `SkoleService/Dockerfile`: multi-stage, with runtime image `mcr.microsoft.com/dotnet/aspnet:10.0-azurelinux3.0-distroless-extra`, modelled on `TjenesteService/Dockerfile`
- [X] T008 [P] Write `SkoleService/src/M2LB.SkoleService.Api/appsettings.json` with every key and default in the quickstart "Key configuration" table. That covers `Skolenaervaer:Kalender:*` (`Periodelengde` `Maaned`, `ForsteRapporteringsperiode` `2026-10`, `Laasdag` 11, `Utsettelse` `06:00:00`, `IkkeRapporteringsmaaneder` `[7]`, `Tidssone` `Europe/Oslo`) and `Skolenaervaer:Kontroll:Fravaerskoder:2.0.0` `["1","2","3","4","5","9"]`. It also covers `Skolenaervaer:Kontroll:Timetoleranse` 0.5, `Skolenaervaer:Kontroll:MaksTimerPerDag` 24 and `Skolenaervaer:Dekning:TellGraderteBarn` false. Add `Skolenaervaer:Hold:Gjenforsoeksintervall` `01:00:00`, `Skolenaervaer:Hold:SveipDebounce` `00:01:00`, `Utdanning:Ukoblet:Gjenforsoeksintervall` `01:00:00` and `Utdanning:Ukoblet:Varselgrense` `30.00:00:00`. Add `Skole:Varsel:*` with the FR-047 values (`LaasForsinkelse` 1 h, `HoldtFoerLaas` 24 h, `Inaktivitet` 24 h, `InaktivitetVindu` 1st–10th, `AvvisningsandelGrense` 0.20, `AvvisningsandelMinimum` 20) and `Skole:Oppbevaring:Aktivert` false, with no category values. Add the `PersonApi`, `TjenesteApi` and `AutorisasjonApi` `IDownstreamApi` sections (`RequestAppToken: true`), `PersonApi:Tidsavbrudd` `00:00:03` (total, retries included), `Personoppslag:Modus` and `Plasseringskilde:Modus` (`Klient`), and `Utdanning:Kodelister` with `ukjent` in each list (other values pending the product owner). Write `appsettings.Local.json` with both modes set to `Stub`
- [X] T009 Register Skole in `Aspire/Aspire/AppHost.cs` and `Aspire/Aspire/Aspire.csproj`. Add `sql.AddDatabase("SkoleDb")`, Azurite blob storage with containers `skolenaervaer-rapporter` and `skolenaervaer-dekning`, and project `skole` with references to SQL, Service Bus and blobs, `ASPNETCORE_ENVIRONMENT=Local` and `WithHttpHealthCheck("/health/ready")`
- [X] T010 [P] Add to `Aspire/Aspire/ConfigFiles/servicebus-config.json`: topics `skole.perioder`, `skole.rapporter` and `skole.utdanning`; subscription `skoletjenesten` on `person.barn` (SQL filter `sys.Label IN ('BarnRegistrert','BarnEksternIdEndret')`; no gradering event is consumed (FR-008a, DEC-122)); and subscription `skoletjenesten` on `person.person` (filter `sys.Label IN ('PersonOpprettet','PersonOppdatert')`). The queues `leselogg` and `operasjonsregistrering` already exist
- [X] T011 [P] Create `SkoleService/.pipeline/skoleservice-dev.yml` and `SkoleService/.pipeline/skoleservice-qa.yml` from the Tjeneste templates. Steps: build, test, an idempotent EF script per context (`--context SkolenaervaerDbContext` and `--context UtdanningDbContext`) plus `wolverine-schema.sql`, image push, migrate, and deploy to `ca-m2lb-skole-<env>-nwe-001`
- [X] T012 [P] Create `SkoleService/.pipeline/wolverine-schema.sql` from `TjenesteService/.pipeline/wolverine-schema.sql`, with schema `wolverine`. *Implemented as the `db-apply` step instead: Tjeneste replaced `wolverine-schema.sql` with `dotnet M2LB.TjenesteService.Api.dll db-apply` in PR 15839 (2026-10-12), so the source file no longer exists; the Skole pipelines publish the Api and run `db-apply` before the EF scripts*
- [X] T013 [P] Wire Skole into `.pipeline/runtests.yml`: path trigger `SkoleService/*`, `Skole` in the services list, and a `Test_Skole` job running both test projects as `Test_Tjeneste` does. Also add Skole to the root `.order` and the module list in the root `README.md`
- [X] T014 [P] Draft ADR-028 "Parquet.Net for the Bufdata delivery" in `docs/M2LB-ADR-(Architecture-Decisions-Record).md`, following governance §2.6 and using research §13 (pure managed code; ParquetSharp needs native Arrow binaries that don't fit the distroless image; Apache Arrow .NET has a larger API surface). The tech lead must sign off before T119

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Calendar, ports, both DbContexts, Wolverine, auth, health, encryption, error mapping, jobs and test fixtures. Every story needs these.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Domain foundation

- [X] T015 [P] Create value object `Rapporteringsperiode` (`YYYY-MM`: parse/format, `FraDato`, `TilDato`, `Inneholder(DateOnly)`, `Neste()`, `Forrige()`; an invalid value throws) in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Rapporteringsperiode.cs`
- [X] T016 [P] Create enum `Sikkerhetsnivaa` (0 `Ingen`, 1 `SkjultAdresse`, 2 `Kode7`, 3 `Kode6`) with `KreverGradertTilgang()` (≥ 2), and `Graderingsregel.ErSynlig(barnId, tillatt, gradertTilgang, Sikkerhetsnivaa?)` (`barn ∈ tillatt ∧ (¬kreverGradertTilgang ∨ barn ∈ gradertTilgang)`; unknown gradering counts as `kreverGradertTilgang`, MP-05). Files: `SkoleService/src/M2LB.SkoleService.Domain/Felles/Sikkerhetsnivaa.cs`, `SkoleService/src/M2LB.SkoleService.Domain/Felles/Graderingsregel.cs`
- [X] T017 [P] Create settings records `Kalenderinnstillinger`, `Kontrollinnstillinger` (absence codes per schema version, `Timetoleranse`, `MaksTimerPerDag`), `Dekningsinnstillinger` (`TellGraderteBarn`) and `Holdinnstillinger` in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Innstillinger.cs`
- [X] T018 Implement `Rapporteringskalender` (MP-09, research §11) in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Rapporteringskalender.cs`. Inputs are `TimeProvider`, `Kalenderinnstillinger` and `TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo")`. Methods:
  - `OsloDato(DateTimeOffset)`.
  - `TolkTidspunkt(string xsDateTime)`: a value with an offset is converted to Europe/Oslo; one without is read as Oslo local time (DEC-121).
  - `ErRapporteringsperiode`, `PlanlagtLaastidspunkt(periode)`: `Laasdag` of the next month at 00:00 Oslo plus `Utsettelse`, returned as UTC.
  - `ForstePeriode`.
  - `KandidatperioderForInnsending(DateOnly innsendingsdato)`: the periods the FR-011 window allows before lock status is applied.
- [X] T019 [P] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/RapporteringskalenderTests.cs`. Use `FakeTimeProvider` and cover:
  - Lock for 2026-10 at `2026-11-11T05:00:00Z` (06:00 Oslo, CET).
  - A lock time that falls in CEST.
  - July is non-reporting, so no lock time is computed for it.
  - Periods before 2026-10 are rejected.
  - Parsing with and without an offset, and the date part in Oslo near midnight.
  - The FR-011 example on 5 November.
  - Two candidate periods between the 1st and the 11th.
- [X] T020 [P] Create port `IPersonoppslag` in `SkoleService/src/M2LB.SkoleService.Domain/Felles/Porter/IPersonoppslag.cs` with `SlaaOppBarnAsync(Barneidentifikator)`, `HentBarnAsync(IReadOnlyCollection<Guid>)` and `SlaaOppEksternIdAsync(int)`. Results:
  - `Funnet`: `BarnRegistreringId`, `BirkId`, `Foedselsdato?`, `UsikkerFoedselsdato?`, `Sikkerhetsnivaa?`.
  - `IkkeFunnet`.
  - `Utilgjengelig`.
  - For the batch call, found children plus `IkkeFunnet` ids.

  Also create `Barneidentifikator` (type `Foedselsnummer`/`DufNummer`, `Verdi`; `ToString()` returns `***`) in `SkoleService/src/M2LB.SkoleService.Domain/Felles/Barneidentifikator.cs`
- [X] T021 [P] Create port `IPlasseringskilde.HentPlasseringerAsync(Rapporteringsperiode, IReadOnlyCollection<Guid>? barnFilter, CancellationToken)` returning `Plassering(BarnRegistreringId, Plasseringskategori {Fosterhjem, PrivatInstitusjon, StatligInstitusjon, Annen}, FraDato, TilDato?)` in `SkoleService/src/M2LB.SkoleService.Domain/Felles/Porter/IPlasseringskilde.cs`
- [X] T022 [P] Create port `IFeltkryptering` in `SkoleService/src/M2LB.SkoleService.Domain/Felles/Porter/IFeltkryptering.cs`:
  - `KrypterAsync(Noekkelformaal {HoldtRapport, UkobletUtdanningspost}, ReadOnlyMemory<byte>)` returns `Kryptert(Innhold, Nonce, Tag, InnpakketDatanoekkel, NoekkelId)`.
  - `DekrypterAsync(Noekkelformaal, Kryptert)`.
  - `BlindIndeksAsync(string)` returns `byte[32]`.
  - `KrypteringUtilgjengeligException`.
- [X] T023 [P] Create port `IAutorisasjonsfilter` in `SkoleService/src/M2LB.SkoleService.Domain/Felles/Porter/IAutorisasjonsfilter.cs`:
  - `HentBarnMedRelasjonAsync(Guid bruker)`.
  - `HentEffektiveTilgangerAsync(Guid bruker, Guid barnId)` returns `(bool HarOperasjon, bool HarSeGradertBarn)` for a named operation.
  - `AutorisasjonUtilgjengeligException`.
- [X] T024 Create entity `Periodelaas` (table `rapporteringsperiode`). It holds `Periode`; `Status` {`Aapen`, `Laaser`, `Laast`, `Levert`}; `PlanlagtLaastidspunkt`; `EffektivtLaastidspunkt`; nullable `LaasStartet`, `LaastTidspunkt` and `LevertTidspunkt`; and nullable `TellGraderteBarn`. Behaviour is added in T111. File: `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Periodelaas.cs`

### Persistence

- [X] T025 Create `SkolenaervaerDbContext` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Skolenaervaer/SkolenaervaerDbContext.cs`:
  - `HasDefaultSchema("skolenaervaer")` and `MigrationsHistoryTable("__EFMigrationsHistory", "skolenaervaer")`.
  - snake_case names through a model-building convention, as Tjeneste does (no new package).
  - Type conventions: `DateOnly`→`date`, instants→`datetimeoffset(7)`, hours `HasPrecision(6, 2)`, enums as `tinyint`.
  - Configurations under `Persistence/Skolenaervaer/Configurations/`:
    - `rapporteringsperiode`: `periode` `char(7)` PK, `status` `tinyint`, the two lock times `datetimeoffset` not null, `laas_startet`, `laast_tidspunkt` and `levert_tidspunkt` nullable, `tell_graderte_barn` `bit` null, `rowversion`.
    - `prosessert_hendelse`: `hendelses_id` PK, `hendelses_type`, `mottatt_tidspunkt`.
    - `jobblaas`: `jobbnavn` PK, `innehaver`, `utloeper`.
- [X] T026 Create `UtdanningDbContext` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Utdanning/UtdanningDbContext.cs` with `HasDefaultSchema("utdanning")`, its own `MigrationsHistoryTable("__EFMigrationsHistory", "utdanning")`, the same conventions as T025, and `prosessert_hendelse`
- [X] T027 Add the initial migration `Grunnlag` for both contexts with the two `dotnet ef migrations add` commands in quickstart.md. Output to `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Skolenaervaer/Migrations/` and `.../Persistence/Utdanning/Migrations/`
- [X] T028 Implement job leases in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/Jobblaas.cs` (acquire, renew and release on `jobblaas` with a conditional update or insert and an expiry; PS-09). Add a base class `LeasetJobb : BackgroundService` with an interval, `TimeProvider` and run-on-one-replica semantics in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/LeasetJobb.cs`
- [X] T029 Implement `PeriodeoppretterJobb` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/PeriodeoppretterJobb.cs`. At startup and daily, it idempotently inserts an `Aapen` `rapporteringsperiode` row (planned = effective lock time from the calendar) for every reporting period from `ForsteRapporteringsperiode` through next month, and skips non-reporting months (FR-012, FR-023)

### External ports

- [X] T030 Implement `PersonoppslagKlient : IPersonoppslag` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Person/PersonoppslagKlient.cs`. It uses `IDownstreamApi` (`PersonApi`, `RequestAppToken: true`) and `AddStandardResilienceHandler()` against the proposed endpoints in `contracts/avhengigheter/person-maskinoppslag.md`, all `POST`:
  - `oppslag-identifikator`.
  - `hent`: paged at 1000 ids.
  - `oppslag-eksternid`.

  Mapping:
  - 404 → `IkkeFunnet`.
  - 409 on `eksternid` → `Utilgjengelig`, so the record is held and no child is guessed (MP-02).
  - 401, 403, 5xx or a timeout → `Utilgjengelig`.
  - A found child without `sikkerhetsnivaa` → `Utilgjengelig` from the single lookups (FR-002). The batch call passes it through as `Sikkerhetsnivaa = null` (FR-008a).

  The intake lookup uses a total timeout from `PersonApi:Tidsavbrudd` (default 3 s, retries included; SC-010), so a slow or hanging Person never pushes an intake answer past the 5-second budget in `contracts/rapportinntak.md` "Response time". Request and response bodies are never logged
- [X] T031 [P] Implement `PersonoppslagStub` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Person/PersonoppslagStub.cs`. It reads `SkoleService/src/M2LB.SkoleService.Api/lokal/person-stub.json` with reload on change, so quickstart scenario 4 can add child C, and is registered when `Personoppslag:Modus = Stub`
- [X] T032 [P] Implement `PlasseringskildeKlient` (`TjenesteApi`, `POST /api/tjeneste/v1/internt/plasseringer/periode`, following `fortsettelse` paging, resilience handler) and `PlasseringskildeStub` (reads `lokal/plasseringer-stub.json`) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Tjeneste/`
- [X] T033 [P] Create the stub data in `SkoleService/src/M2LB.SkoleService.Api/lokal/`:
  - `person-stub.json`:
    - Child A: unclassified, exact birth date 2015-03-02, with `eksternId`.
    - Child B: `sikkerhetsnivaa` 3, school age, with `eksternId`.
    - Child C: not present, but documented in a comment field for scenario 4.
  - `plasseringer-stub.json`: A in `Fosterhjem` and B in `PrivatInstitusjon`, both covering 2026-10 and 2026-11.
- [X] T034 Implement `Autorisasjonsfilter : IAutorisasjonsfilter` with M2LB.Common's Autorisasjon client (`GetBarnWithAccessAsync`, `GetEffectiveAccessesAsync`, evaluating `Person:SeGradertBarn` together with the read's operation) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Autorisasjon/Autorisasjonsfilter.cs`. Any failure throws `AutorisasjonUtilgjengeligException` (fail-closed, GL-25)

### Encryption (EXC-02, EXC-05)

- [X] T035 Implement `KeyVaultFeltkryptering : IFeltkryptering` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Kryptering/KeyVaultFeltkryptering.cs`:
  - Encryption: AES-256-GCM (`AesGcm`, 12-byte nonce, 16-byte tag) with a fresh random data key, wrapped RSA-OAEP-256 through `CryptographyClient`. The key per purpose comes from `KeyVault:Noekler:HoldtRapport` (`skole-holdt-rapport`) and `KeyVault:Noekler:UkobletUtdanningspost` (`skole-ukoblet-utdanningspost`).
  - Blind index: HMAC-SHA256 under secret `skole-ukoblet-blindindeks`, cached in memory.
  - Every Key Vault failure throws `KrypteringUtilgjengeligException`; plaintext is never stored.

  Also add `LokalFeltkryptering` (an AES key from user-secrets, registered only when `IsLocal()`) in `.../Kryptering/LokalFeltkryptering.cs`
- [X] T036 [P] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Kryptering/FeltkrypteringTests.cs`: round trip for both implementations, a tampered tag or ciphertext fails, a fresh nonce per call, a deterministic blind index, and a Key Vault exception (substituted `CryptographyClient`) surfacing as `KrypteringUtilgjengeligException`

### Messaging

- [X] T037 Configure Wolverine in `SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/WolverineOppsett.cs`, with an extension called from `Program.cs`:
  - `builder.Host.AddM2LbWolverine(config, "M2LB.SkoleService", useEntityFrameworkCoreTransactions: true, …)`.
  - `PersistMessagesWithSqlServer(connStr, schema: "wolverine")`, with `AutoBuildMessageStorageOnStartup = CreateOrUpdate` only when `IsLocal()`.
  - `AutoApplyTransactions`, `UseDurableOutboxOnAllSendingEndpoints`, `UseDurableInboxOnAllListeners` and `UseDurableLocalQueues`.
  - `Discovery.IncludeAssembly` for Api and Infrastructure, and `MultipleHandlerBehavior.Separated`.
  - EF Core integration for both DbContexts.
  - Publish routes: `skole.perioder`, `skole.rapporter`, `skole.utdanning`, `leselogg` and `operasjonsregistrering`.
  - Listeners on `person.barn` and `person.person`, subscription `skoletjenesten`.
- [X] T038 [P] Create the GL-20 envelope `Hendelseskonvolutt<T>` (`hendelsesId`, `hendelsesType`, `hendelsesTidspunkt`, `utfoertAv`, `korrelasjonId`, `kilde: "Skoletjenesten"`, `data`; camelCase JSON) and a publish helper that sets Service Bus `Subject` = `hendelsesType`, `MessageId` = `hendelsesId` and `CorrelationId` = `korrelasjonId`, in `SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/Hendelseskonvolutt.cs`
- [X] T039 [P] Create the Person event records in `SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/Person/PersonHendelser.cs`:
  - `BarnRegistrert`: `barnRegistreringId`, `eksternId`.
  - `BarnEksternIdEndret`: `barnRegistreringId`, `nyttEksternId`.
  - `PersonOpprettet`.
  - `PersonOppdatert`: `foedselsnummerEndret`, `dufNummerEndret`.
  - Each carries the envelope's `hendelsesId` and `hendelsesTidspunkt`.

  Also write `PersonKonvoluttMapper : IAzureServiceBusEnvelopeMapper` in `.../Messaging/Person/PersonKonvoluttMapper.cs`. It unwraps Person's `EventEnvelope.data` by subject, with `TjenesteService/src/M2LB.TjenesteService.Infrastructure/Messaging/Mappers/BarnRegistrertMapper.cs` as the precedent.
- [X] T040 [P] Implement the per-context dedupe helper `ProsessertHendelse.ErBehandletAsync/MarkerAsync` on `prosessert_hendelse`, keyed by the envelope's `hendelsesId` and written in the handler's transaction (GL-22), in `SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/ProsessertHendelse.cs`
- [X] T041 Implement operation registration at startup in `SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/Operasjonsregistrering.cs`, modelled on `TjenesteService/src/M2LB.TjenesteService.Infrastructure/Messaging/OperationsRegistrationService.cs`. It sends `RegistrerOperasjonerCommand` with the five operations, names, descriptions and classifications exactly as in `contracts/hendelser.md` (`Skole:HosteRapportering` 0, `Skole:SeOppfolgingsliste` 1, `Skole:HentUtdanningspostidentiteter` 0, `Skole:SeDekningsstatistikk` 0, `Skole:UtsetteLaas` 0). Startup fails if sending fails (GL-09). Note: the contract says `tjenesteEksternId: "skole"`, while Tjeneste uses `"M2LB.Tjeneste"`; confirm the convention with the development team (Autorisasjon) and update hendelser.md if it changes

### API host

- [X] T042 Implement authentication and policies in `SkoleService/src/M2LB.SkoleService.Api/Auth/Autentisering.cs`:
  - `AddM2LbAuthentication`.
  - Named policies `Skole.Rapportinntak` and `Skole.Utdanningsinnmating` (`RequireRole`, research §16). Machine access through app roles is approved as EXC-06; any other machine path needs its own exception.
  - M2LB.Common's dynamic `Modul:Operasjon` policies.

  Local-only: `SkoleService/src/M2LB.SkoleService.Api/Auth/DevAuthHandler.cs` (`Bearer <guid>` = user oid; grants both app roles) and `AddM2LbAutorisasjonDev()`, following `TjenesteService/src/M2LB.TjenesteService.Api/Auth/DevClients.cs`
- [X] T043 Implement problem-details mapping for both REST surfaces in `SkoleService/src/M2LB.SkoleService.Api/Feilhaandtering/Problemdetaljer.cs`:
  - Strict JSON (`UnmappedMemberHandling.Disallow`, required members, string enums only; non-finite numbers → 400).
  - `DbUpdateConcurrencyException` and SQL errors 2601/2627 → 409.
  - `KrypteringUtilgjengeligException`, transient SQL errors and timeouts → 503.
  - `Avvist(aarsakskode)` → 422 with `aarsakskode`.
  - Bodies are `application/problem+json` with `korrelasjonId` and never contain an identifier or exception text (FR-045b, FR-004).
- [X] T044 [P] Unit tests for T043 in `SkoleService/tests/M2LB.SkoleService.Unit/Api/ProblemdetaljerTests.cs`: each exception maps to the right status, and no input value appears in the body
- [X] T045 Implement PII guards in `SkoleService/src/M2LB.SkoleService.Infrastructure/Logging/Identifikatormaskering.cs`: a Serilog destructuring policy that masks `Barneidentifikator`, reporter identity and `barnEksternId`, plus request logging that logs no bodies or query strings (GL-28, FR-004, FR-005, FR-040d)
- [X] T046 [P] Create `SkoleMetrikker` (`Meter` `M2LB.SkoleService`) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Telemetri/SkoleMetrikker.cs` with counter `skole.rapporter.besvart` (tags `utfall`, `aarsakskode`) and a registration hook for observable gauges added by later tasks. Tags are allow-listed, with no identifiers
- [X] T047 Implement `SkoleService/src/M2LB.SkoleService.Api/Helse/KeyVaultHelsesjekk.cs`, a check tagged `ready` that probes `IFeltkryptering` and caches the result for 60 s
- [X] T048 Compose `SkoleService/src/M2LB.SkoleService.Api/Program.cs`:
  - `AddM2LbLogging("M2LB.SkoleService")`, `AddM2LbObservability("M2LB.SkoleService")`, `UseM2LbCorrelationId()`, `MapM2LbVersionEndpoint("skole")`.
  - Options from T017 bound with `ValidateOnStart`; startup fails if `Periodelengde ≠ Maaned`, `Laasdag` is outside 1–28, or a code list lacks `ukjent`.
  - `TimeProvider.System`, both DbContexts on connection string `SkoleDb`, and the ports registered by `Modus` (Stub/Klient).
  - Auth (T042), Wolverine (T037), operation registration (T041) and the jobs (T029).
  - `MapM2LbHealthEndpoints()`: `/health/live`, `/health/startup` (answering like live, because the platform's container-app module probes it) and `/health/ready` anonymous; ready = `AddDbContextCheck` for both contexts plus T047, written with `HealthResponseWriter.WriteReady`. Person, Tjeneste and Autorisasjon are never in readiness (FR-046).
  - Migrations applied at startup only when `IsLocal()`.

### Test infrastructure

- [X] T049 Create the integration fixtures in `SkoleService/tests/M2LB.SkoleService.Integration/Fixtures/`, modelled on `TjenesteService/tests/M2LB.TjenesteService.Integration/Fixtures/`:
  - `SkoleWebApplicationFactory.cs`: Testcontainers MsSql 2022 and Azurite; WireMock servers for Person, Tjeneste and Autorisasjon; a `FakeTimeProvider` override; Wolverine external transports stubbed; local encryption.
  - `TestAuthHandler.cs`: app roles plus user oid.
  - `IntegrationTestCollection.cs`.
  - `PersonWireMock.cs`, `TjenesteWireMock.cs` and `AutorisasjonWireMock.cs`: helpers for children A, B and C and for relation and grant setups.
  - `PublisertSpion.cs`: captures outgoing messages and envelopes.
  - `LoggSpion.cs`: captures all log output for PII assertions.
  - `DatabaseSkanner.cs`: scans every text and binary column in the `skolenaervaer` and `utdanning` schemas for a given value.
- [X] T050 [P] Architecture tests, by reflection without NetArchTest, in `SkoleService/tests/M2LB.SkoleService.Unit/Arkitektur/ArkitekturTests.cs`:
  - No type in `M2LB.SkoleService.Domain.Skolenaervaer` references `M2LB.SkoleService.Domain.Utdanning` other than `IUtdanningsgrunnlag` and `Utdanningsstatus`.
  - Domain references neither Api nor Infrastructure.
  - `Persistence.Skolenaervaer` and `Persistence.Utdanning` do not reference each other's entities (MP-07).
  - No type outside a `*.Utdanning` namespace (Domain or Infrastructure, e.g. `Laas`, `Dekning`, `Oppfolging`) references an Utdanning type other than `IUtdanningsgrunnlag`, `Utdanningsstatus` and `Utdanningskobling`.
- [X] T051 [P] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Helse/HelseTests.cs`:
  - `/health/live` and `/health/startup` return 200.
  - `/health/ready` returns 200 while the Person, Tjeneste and Autorisasjon WireMocks answer 500.
  - Ready returns 503 when the Key Vault check fails.
  - Bodies reveal only ready/not ready.
- [X] T052 [P] Integration test in `SkoleService/tests/M2LB.SkoleService.Integration/Messaging/OperasjonsregistreringTests.cs`: at startup exactly one `RegistrerOperasjonerCommand` is sent with the five operations from hendelser.md
- [X] T053 [P] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Jobber/JobblaasTests.cs` (T028, PS-09): two `LeasetJobb` instances on one database with `FakeTimeProvider`; exactly one runs per interval; a holder that stops renewing loses the lease after its expiry and the other takes over; a released lease is taken at once; the lease row never has two holders
- [X] T054 [P] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Jobber/PeriodeoppretterJobbTests.cs` (T029, FR-012, FR-023): from `ForsteRapporteringsperiode` through next month an `Aapen` row exists per reporting period with planned = effective lock time from the calendar; July is skipped; a second run, and a run on a second replica, insert nothing new

**Checkpoint**: The solution builds, the service starts under Aspire, health is green, operations are registered, and fixtures and architecture tests run.

---

## Phase 3: User Story 1 - Accept a valid report without keeping the child's national ID (Priority: P1) 🎯 MVP start

**Goal**: Intake accepts, rejects or flags a report for a child Person knows, stores it against the M2LB identity, and never keeps the national identifier.

**Independent Test**: Submit valid, invalid and inconsistent reports for children known to Person. Verify the stored outcome, the rejection reasons and the flags, and that no national identifier exists anywhere afterwards (quickstart scenarios 1–3).

**Interim behaviour until later stories land** (each with a temporary failure, so nothing is lost; FR-045b):

| Case | Answer for now | Replaced by |
|---|---|---|
| Child unresolved, or Person unavailable | 503 | T127 (US4) |
| `Trukket`, `Slettet` or `Tillegg` | 503 | T138 (US5) |

### Tests for User Story 1 ⚠️

- [X] T055 [P] [US1] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/GyldighetskontrollTests.cs`, one or more per code:
  - `MANGLER_IDENTIFIKATOR`.
  - `UGYLDIG_TIMEVERDI`: negative planned, actual or absence hours, and hours > `MaksTimerPerDag` × inclusive days in the date range (scenario 11).
  - `STARTDATO_ETTER_SLUTTDATO`.
  - `UTENFOR_GYLDIGHETSVINDU`: dates span two periods, start after the submission date, the period is not `Aapen`, the period has not started, or it lies before 2026-10.
  - An end date after the submission date in the current period is valid, and partial periods are valid.
  - `IKKE_RAPPORTERINGSPERIODE` for July.
  - `UVENTET_MELDINGSFORBINDELSE` for `ApplikasjonsKvittering`, `SaksbehandlerKvittering`, `Svar` and `Feil`.
  - The FR-011 example on 5 November.
- [X] T056 [P] [US1] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/KvalitetskontrollTests.cs`:
  - Duplicate codes are merged by sum with no flag, and `01` and `1` stay distinct (ordinal string comparison).
  - `FRAVAERSSUM_AVVIKER` is set when |sum − (planned − actual)| > `Timetoleranse`; exactly 0.5 is not flagged.
  - `NAERVAER_OVER_PLAN` is set for any excess and suppresses `FRAVAERSSUM_AVVIKER`.
  - `UKJENT_FRAVAERSKODE` for `01` or a code outside the version's list.
  - `MANGLER_FRAVAERSFORDELING` only when there is no breakdown and planned > actual; none when planned = actual.
  - `SENDT_FOR_PERIODESLUTT` when the end date is after the submission date.
  - Hours rounded to 2 decimals with `MidpointRounding.AwayFromZero` before the checks.
- [X] T057 [P] [US1] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/RapportTests.cs`: the same key gives a new version and the old one is kept; the latest send time is current, with ties broken by `meldingId` ordinal; an older version arriving late is stored but not current (FR-022); flags belong to the version (scenario 10)
- [X] T058 [P] [US1] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Rapportinntak/RapportinntakKontraktTests.cs`:
  - 200 with the exact body `{"utfall":"Mottatt"}`.
  - 422 `application/problem+json` with `aarsakskode` for each of the six published codes.
  - 400 for malformed JSON, an unknown property, an unknown enum value, a missing required field and a non-finite number.
  - 400, never 500 or 503, for a value one character over its `maxLength`: `fravaer[].kode` (17), `rapportoer.identitet` (12), `barn.verdi` (13) and `skjemaversjon` (17).
  - 401 without a token, and 403 without `Skole.Rapportinntak`.
  - `X-Correlation-Id` echoed into logs.
  - No problem body contains the child's or the reporter's identifier.
- [X] T059 [P] [US1] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Rapportinntak/RapportinntakIdempotensTests.cs`: the same `meldingId` sent twice gives an identical status and body byte for byte and no new rows, for both a 200 and a 422 outcome (FR-018, SC-007)
- [X] T060 [P] [US1] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Rapportinntak/RapportinntakPersonvernTests.cs` (SC-002), after a valid and a rejected report:
  - `DatabaseSkanner` finds the child's fødselsnummer in no column.
  - The reporter's identity appears only in `rapport.rapportoer_identitet` and `avvisning.rapportoer_identitet`.
  - `LoggSpion` finds neither identifier.
  - `SkolenaervaersrapportEndret` carries only `rapportId`, `barnRegistreringId`, `periode`, `status` and `versjonId`.

### Implementation for User Story 1

- [X] T061 [P] [US1] Create the domain types in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/`:
  - `Rapportoer.cs`: `Rapportoertype {Person, Organisasjon}` and `Identitet`.
  - `Fravaerslinje.cs`: `Kode` string, `Timer`.
  - `Kvalitetsflagg.cs`: `[Flags]` with `FRAVAERSSUM_AVVIKER`=1, `NAERVAER_OVER_PLAN`=2, `UKJENT_FRAVAERSKODE`=4, `MANGLER_FRAVAERSFORDELING`=8, `SENDT_FOR_PERIODESLUTT`=16, `FLERE_RAPPORTOERER`=32, `OVERLAPPENDE_RAPPORTER_SAMME_RAPPORTOER`=64.
  - `Aarsakskode.cs`: the six published codes plus `UKJENT_BARN`.
  - `Meldingsforbindelsestype.cs`: the nine values from the OpenAPI enum.
  - `InnkommendeRapport.cs`: domain input, hours as `double`, period text as received.
- [X] T062 [US1] Implement `Gyldighetskontroll.Kontroller(InnkommendeRapport, Rapporteringskalender, Func<Rapporteringsperiode, Periodelaas?>, Kontrollinnstillinger)` returning `Gyldig(periode, fra, til, innsendingsdato)` or `Avvist(kode, periode?)` in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Gyldighetskontroll.cs`. All rules are identity-independent (FR-007). Hours are range-checked as `double` before conversion (research §2). Rules are checked in the published code table's order
- [X] T063 [US1] Implement `Kvalitetskontroll` (conversion to `decimal` rounded to 2 decimals, merging of duplicate codes, flags for FR-014 to FR-016 and FR-015c from `Kontrollinnstillinger`) in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Kvalitetskontroll.cs`
- [X] T064 [US1] Implement the aggregates in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/`:
  - `Rapport.cs`:
    - Key: `BarnRegistreringId`, `Periode`, `Rapportoer`, `FraDato`, `TilDato`.
    - `Status {Godkjent, Trukket, Slettet, Levert, Tilbakeholdt}`, `GjeldendeVersjonId`, `BirkId`, `KjedeStartMeldingId`, `Kilde = "ALTINN"`.
    - `LeggTilVersjon` follows FR-022.
  - `Rapportversjon.cs`: append-only, with `MeldingId`, `SendtTidspunkt`, `RegistrertTidspunkt`, `Meldingsforbindelse?`, `Skjemaversjon`, `TimerEtterPlanen`, `TimerFaktiskNaervaer`, `Kvalitetsflagg` and `Fravaerslinjer`. Invariant: `FRAVAERSSUM_AVVIKER` is never set together with `NAERVAER_OVER_PLAN`.
  - `Avvisning.cs`: `Kilde {Inntak, Laas}`. The child and reporter are set only when the child resolved.
- [X] T065 [US1] Create EF configurations in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Skolenaervaer/Configurations/`, following data-model.md:
  - **`rapport`**:
    - Columns: `rapport_id` PK; `barn_registrering_id` not null; `periode` `char(7)`; `rapportoer_type` `tinyint`; `rapportoer_identitet` `varchar(11)`; `fra_dato` and `til_dato` `date`; `birk_id` `varchar(32)`; `kjede_start_melding_id`; `status` `tinyint`; `gjeldende_versjon_id`; `kilde` `varchar(8)`; `opprettet_tidspunkt`; `rowversion`.
    - **Unique** (`barn_registrering_id`, `periode`, `rapportoer_type`, `rapportoer_identitet`, `fra_dato`, `til_dato`).
    - Indexes (`periode`, `status`) and (`kjede_start_melding_id`).
  - **`rapportversjon`**: `versjon_id` PK; `rapport_id` FK; `melding_id` unique; `sendt_tidspunkt`; `registrert_tidspunkt`; `meldingsforbindelse` `tinyint` null; `skjemaversjon` `varchar(16)`; `timer_etter_planen` and `timer_faktisk_naervaer` `decimal(6,2)`; `kvalitetsflagg` `int`.
  - **`fravaerslinje`**: PK (`versjon_id`, `kode`); `kode` `nvarchar(16)`; `timer` `decimal(6,2)`.
  - **`avvisning`**: `avvisning_id` PK; `melding_id`; `aarsakskode` `varchar(40)`; `periode` `char(7)` null; `barn_registrering_id` null; `rapportoer_type` `tinyint` null; `rapportoer_identitet` `varchar(11)` null; `kilde` `tinyint`; `avvist_tidspunkt`.
  - **`meldingsmottak`**: `melding_id` PK; `mottatt_tidspunkt`; `sendt_tidspunkt`; `utfall` `tinyint` {`Mottatt`, `Avvist`}; `aarsakskode` `varchar(40)` null; `behandling` `tinyint` {`Godkjent`, `Holdt`, `KjedeAvsluttet`, `IngenEffekt`, `Tillegg`, `Avvist`}; `kjede_start_melding_id`; `korrelasjon_id`.
- [X] T066 [US1] Add migration `Rapportinntak` for `SkolenaervaerDbContext` (command in quickstart.md)
- [X] T067 [US1] Create request DTOs that exactly mirror `contracts/rapportinntak.openapi.yaml` (`additionalProperties: false`, required fields, string enums, every `maxLength` (`skjemaversjon` 16, `barn.verdi` 12, `rapportoer.identitet` 11, `fravaer[].kode` 16), nullable `barn` and `meldingsforbindelse`) and map them to `InnkommendeRapport` in `SkoleService/src/M2LB.SkoleService.Api/Rest/Rapportinntak/RapportForespoersel.cs`
- [X] T068 [US1] Implement `Rapportinntaksbehandler` in `SkoleService/src/M2LB.SkoleService.Api/Rest/Rapportinntak/Rapportinntaksbehandler.cs`, following research §6. Steps:
  1. Idempotency check on `meldingsmottak`: replay the stored outcome. The 422 body is rebuilt deterministically from `aarsakskode` and the stored `korrelasjon_id`.
  2. Person lookup (`SlaaOppBarnAsync`), outside the transaction.
  3. Open the transaction and read the period's `rapporteringsperiode` row with `FromSql("… WITH (HOLDLOCK, ROWLOCK) WHERE periode = {0}")`, and pass its status to `Gyldighetskontroll`. Every write path below (accept, reject, hold, chain close) runs in this transaction, so the lock's claim waits for it (research §12 step 1).
  4. Validity rules: a rejection gets an `avvisning` attributed to the child and reporter when the child resolved, whatever its gradering (FR-013).
  5. Resolved and valid: quality rules, then create or version the `Rapport` and send `SkolenaervaersrapportEndret` through the outbox.
  6. Store `meldingsmottak` in the same transaction, commit, then answer.

  Interim branches: an unresolved child or unavailable Person answers 503 with no state written, and `Trukket`/`Slettet`/`Tillegg` answers 503. `Oppdatering`, and `Duplikat` with a new id, are handled as a submission for their key. The national identifier lives only in request memory (FR-004)
- [X] T069 [US1] Map `POST /api/skole/v1/skolenaervaer/rapporter` in `SkoleService/src/M2LB.SkoleService.Api/Rest/Rapportinntak/RapportinntakEndepunkt.cs`. Policy `Skole.Rapportinntak`; required `X-Correlation-Id` header; 200/400/409/422/503 through T043. Concurrent duplicates of one `meldingId` (PK violation) answer 409
- [X] T070 [US1] Emit `skole.rapporter.besvart{utfall, aarsakskode}`, and record the last time a report was received (for FR-026a (b)), from `SkoleService/src/M2LB.SkoleService.Api/Rest/Rapportinntak/Rapportinntaksbehandler.cs` through `SkoleMetrikker`. Write one structured log line per request with only `meldingId`, `periode`, `utfall` and `aarsakskode`

**Checkpoint**: Quickstart scenarios 1–3 pass. Reports for known children are accepted, rejected or flagged, and no national identifier is stored.

---

## Phase 4: User Story 2 - Process reports for security-classified children and enforce gradering on access (Priority: P1)

**Goal**: Kode 6/7 children are processed like any other child, and the service keeps no record of anyone's gradering: every read looks it up in Person and evaluates it fail-closed (FR-008a, DEC-122). Processing is US1's pipeline, so this story is mostly tests; enforcement on read is built in US8 (T146) and withholding at lock in US3.

**Independent Test**: Submit reports for a Kode 6, a Kode 7 and an unclassified child, and verify all three are stored and that no gradering is stored for any of them. The read scenarios (2, 3) are verified in US8 (T141). The lock scenarios (4, 5) are verified in US3 (T106), and scenario 6 in US4 (T123).

### Tests for User Story 2 ⚠️

- [X] T071 [P] [US2] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Felles/GraderingsregelTests.cs`: `KreverGradertTilgang` for levels 0–3; `ErSynlig` across the four combinations of relation and grant; unknown gradering → not visible without the grant
- [X] T072 [P] [US2] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Rapportinntak/GradertBarnInntakTests.cs`:
  - Reports for B (Kode 6) and a Kode 7 child get 200, are stored and versioned like any other, and no column outside `dekningsgrunnlag_barn.sikkerhetsnivaa_ved_laas` holds a gradering (schema check through `DatabaseSkanner`).
  - A rejection for B carries B's id.
  - A Person answer without `sikkerhetsnivaa` is never stored as unclassified (interim 503 now; held after T127).


**Checkpoint**: Kode 6/7 reports are stored like any other, and no gradering is stored.

---

## Phase 5: User Story 7 - Maintain education master data (Priority: P2; prerequisite for the lock)

**Goal**: Utdanning ingests SkoleAdapter's changes with temporal history, resolves each child through Person, holds unresolved records encrypted, and links them later.

**Independent Test**: Send creates, updates and deletes; verify the stored and as-of state. Send a record for an unknown child, register the child, and verify it links and leaves no child reference (quickstart scenarios 7–8).

### Tests for User Story 7 ⚠️

- [X] T073 [P] [US7] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Utdanning/UtdanningsendringTests.cs`:
  - A create for a known id is an update.
  - An identical update has no effect and creates no version.
  - An update for an unknown id is a create.
  - A delete for an unknown id is a no-op.
  - A delete of a linked record is soft.
  - A delete of an unlinked record is a hard delete.
  - An update resolving to another child gives `Flyttet`.
  - An unresolvable update of a linked record is held, and the record is unchanged.
  - Overlap is inclusive, including open-ended records; `tilDato < fraDato` covers no day.
- [X] T074 [P] [US7] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Utdanning/UtdanningInnmatingKontraktTests.cs`:
  - Identical 200 `{"utfall":"Mottatt"}` for linked and unlinked creates.
  - 422 `MANGLER_POSTIDENTITET` for a missing or malformed `postId` in the body or path; `MANGLER_BARNREFERANSE`; `MANGLER_FRADATO`; `UGYLDIG_DATO` for an unreadable `fraDato` or `tilDato`.
  - 400 with problem type `…/kontraktsavvik` for a code outside the lists, an unknown field or a malformed body.
  - 400 for a missing `Idempotency-Key` or one longer than 200 characters.
  - A replay with the same `Idempotency-Key` is identical.
  - 401 and 403 without `Skole.Utdanningsinnmating`.
  - Every write answers 200 with the exact body `{"utfall":"Mottatt"}` (never 204, an empty body or 404), for both record types: `PUT` for an unknown `postId` (applied as a create), `POST` for a known `postId` (applied as an update), `DELETE` for an unknown `postId` (no-op), `DELETE` of a linked record, `DELETE` of an unlinked record, and a replayed `DELETE`.
  - An explicit `"fraDato": null` gives 422 `MANGLER_FRADATO` and an explicit `"barnEksternId": null` gives 422 `MANGLER_BARNREFERANSE`, never 400; an absent `tilDato` is accepted as open-ended.
- [X] T075 [P] [US7] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Utdanning/UkobletUtdanningspostTests.cs`:
  - An unknown `eksternId` gives an `ukoblet_utdanningspost` row and `DatabaseSkanner` finds the plaintext `eksternId` nowhere.
  - The record is not visible through `IUtdanningsgrunnlag`.
  - A `BarnRegistrert` event with that `eksternId` links the record, deletes the `ukoblet` row with its blind index, and publishes `UtdanningspostEndret` `Opprettet`.
  - The `BarnEksternIdEndret` path does the same.
  - Person unavailable or a 409 → held.
  - A newer held update replaces an older one.
  - A delete of an unlinked record removes the row.
  - A Key Vault failure → 503.
  - A linked record is never unlinked (SC-011).
- [X] T076 [P] [US7] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Utdanning/PostidentiteterTests.cs`: only current records, unlinked included and deleted excluded; paging with `fortsettelse` (null on the last page); `sideStoerrelse` 1–10000 with default 5000; no fields in the response; `GET /kodelister` returns the lists with `ukjent` and a version
- [X] T077 [P] [US7] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Utdanning/UtdanningsgrunnlagTests.cs`: `HentForPeriodeAsync` reports a record of any type, `ukjent` included, overlapping the period; `ManglendeSkoletilbudHelePerioden` only when the record covers every day; linked and active records only; the as-of state for a past period comes from the version tables (FR-039); `barnFilter` restricts the result

- [X] T078 [P] [US7] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Utdanning/UkobletUtdanningspostJobbTests.cs` (T090, FR-040c): with `FakeTimeProvider` past `Utdanning:Ukoblet:Gjenforsoeksintervall`, an unlinked record whose child Person now finds is linked, its `ukoblet` row and blind index are deleted and `UtdanningspostEndret` `Opprettet` is published; a still-unknown child stays unlinked with `antall_forsoek` + 1; Person unavailable leaves every record unchanged; only one replica runs the job (lease)

### Implementation for User Story 7

- [X] T079 [P] [US7] Implement `Kodelister` (lists for education type, school type and missing-offer reason, with a version, from `Utdanning:Kodelister`; each list must contain `ukjent`; `ErGyldig(liste, kode)`) in `SkoleService/src/M2LB.SkoleService.Domain/Utdanning/Kodelister.cs`
- [X] T080 [P] [US7] Create the domain records in `SkoleService/src/M2LB.SkoleService.Domain/Utdanning/`:
  - `Utdanningspost.cs`: base with `PostId`, `BarnRegistreringId`, `FraDato`, `TilDato?`, `ErAktiv`, `Overlapper(Rapporteringsperiode)`, `DekkerHele(Rapporteringsperiode)`.
  - `Utdanningsforhold.cs`: `Utdanningstype`, `Skoletype`.
  - `ManglendeSkoletilbud.cs`: `Aarsak`.
  - `Endringstype.cs`: `Opprettet`, `Endret`, `Flyttet`, `Slettet`.
- [X] T081 [P] [US7] Create `IUtdanningsgrunnlag`, `Utdanningsstatus(bool HarUtdanningsforhold, bool ManglendeSkoletilbudHelePerioden)` and `Utdanningskobling` exactly as in data-model.md, plus `Utdanningspostfelter` (`PostId`, `Posttype`, `FraDato`, `TilDato?`, and `Utdanningstype`/`Skoletype` or `Aarsak`; no child reference), in `SkoleService/src/M2LB.SkoleService.Domain/Utdanning/IUtdanningsgrunnlag.cs`
- [X] T082 [US7] Implement `Utdanningsendring.Avgjoer(...)`, which returns `Opprett`, `Oppdater`, `IngenEffekt`, `Flytt`, `MykSlett`, `HoldUkoblet` or `SlettUkoblet` per FR-040b to FR-040d, in `SkoleService/src/M2LB.SkoleService.Domain/Utdanning/Utdanningsendring.cs`
- [X] T083 [US7] Create EF configurations in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Utdanning/Configurations/`:
  - **`utdanningsforhold`**: `post_id` PK (never generated here); `barn_registrering_id` not null; `fra_dato` `date`; `til_dato` `date` null; `utdanningstype` `varchar(32)`; `skoletype` `varchar(32)`; `er_aktiv` `bit`; `opprettet_tidspunkt`; `endret_tidspunkt`; `rowversion`.
  - **`utdanningsforhold_versjon`** (append-only): `post_id`, `versjon_nr`, `registrert_tidspunkt`, `endringstype`, plus the full field set.
  - **`manglende_skoletilbud`**: as `utdanningsforhold`, but with `aarsak` `varchar(32)` instead of the two type columns.
  - **`manglende_skoletilbud_versjon`**.
  - **`ukoblet_utdanningspost`**: `id` PK; `post_id` unique per `posttype`; `posttype` `tinyint`; `gjelder` `tinyint` {`NyPost`, `OppdateringAvKobletPost`}; `barnreferanse_blindindeks` `binary(32)` indexed; `kryptert_innhold` `varbinary(max)`; `nonce` `binary(12)`; `tag` `binary(16)`; `innpakket_datanoekkel` `varbinary(512)`; `noekkel_id` `nvarchar(256)`; `mottatt_tidspunkt`; `antall_forsoek`; `siste_forsoek`.
  - **`endringsmottak`**: `idempotensnokkel` `nvarchar(200)` PK; `mottatt_tidspunkt`; `utfall` `tinyint`; `aarsakskode` `varchar(40)` null; `post_id` null.
- [X] T084 [US7] Add migration `Utdanning` for `UtdanningDbContext` (command in quickstart.md)
- [X] T085 [US7] Implement `Utdanningsmottak` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Utdanning/Utdanningsmottak.cs`:
  - Idempotency on `endringsmottak`.
  - `SlaaOppEksternIdAsync` for creates and updates; deletes need no lookup.
  - Apply T082. A resolved change upserts the record, appends a version row, and sends `UtdanningspostEndret` (`postId`, `posttype`, `barnRegistreringId`, `endringstype`) through the outbox for linked records only.
  - An unresolved change is encrypted (`barnEksternId`, fields and operation) with a blind index, and a newer held change for the same `post_id`/`posttype` replaces the older one.
  - The child reference is discarded once the record is linked (FR-040c).
- [X] T086 [US7] Map the endpoints in `SkoleService/src/M2LB.SkoleService.Api/Rest/Utdanning/UtdanningEndepunkter.cs`, with DTOs in `.../Rest/Utdanning/Utdanningsforespoersler.cs`:
  - Routes under `/api/skole/v1/utdanning`, policy `Skole.Utdanningsinnmating`:
    - `POST /utdanningsforhold` and `POST /manglende-skoletilbud`.
    - `PUT /utdanningsforhold/{postId}` and `PUT /manglende-skoletilbud/{postId}`.
    - `DELETE /utdanningsforhold/{postId}` and `DELETE /manglende-skoletilbud/{postId}`.
    - `GET /postidentiteter`.
    - `GET /kodelister`.
  - Required `Idempotency-Key` header (maxLength 200).
  - Dates are taken as strings and parsed `yyyy-MM-dd`; a failure → 422 `UGYLDIG_DATO`.
  - A code outside `Kodelister` or an unknown field → 400 problem type `…/kontraktsavvik`.
  - Status mapping through T043.
- [X] T087 [US7] Implement the `postidentiteter` query: current, active, linked `post_id`s unioned with unlinked `NyPost` rows for the `posttype`, ordered by `post_id`, with an opaque base64 continuation token. File: `SkoleService/src/M2LB.SkoleService.Infrastructure/Utdanning/Postidentiteter.cs`
- [X] T088 [US7] Implement `Utdanningskobler` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Utdanning/Utdanningskobler.cs`:
  - `KobleAsync(eksternId, barnRegistreringId)` finds rows by blind index.
  - `FinnKoblingerAsync(ct)` decrypts and looks up through DEP-08 with no writes, and returns `Utdanningskobling`s (decrypted fields in `Utdanningspostfelter`, never the child reference).
  - `KobleAsync(koblinger, ct)` applies them on the scoped `UtdanningDbContext`, skipping any whose `ukoblet` row is gone or has a newer `mottatt_tidspunkt`.
  - `ForsoekAlleUkobledeAsync(ct)` = find, then link in its own transaction; used by the hourly job and the event handlers.

  Linking applies the held change and deletes the `ukoblet` row with its reference and index
- [X] T089 [US7] Implement the handlers `BarnRegistrertHandler` and `BarnEksternIdEndretHandler` (Utdanning chain, dedupe in `utdanning.prosessert_hendelse`, calling `Utdanningskobler.KobleAsync`) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/Utdanning/`
- [X] T090 [US7] Implement `UkobletUtdanningspostJobb` (leased, every `Utdanning:Ukoblet:Gjenforsoeksintervall`, calls `ForsoekAlleUkobledeAsync`, increments `antall_forsoek`) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/UkobletUtdanningspostJobb.cs`
- [X] T091 [US7] Implement `Utdanningsgrunnlag : IUtdanningsgrunnlag` on `UtdanningDbContext` (linked, active records; the state as of an instant comes from the version tables; `FinnKoblingerAsync` and `KobleAsync` delegate to `Utdanningskobler` and never commit) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Utdanning/Utdanningsgrunnlag.cs`
- [X] T092 [US7] Register observable gauges for the number of unlinked records and the age of the oldest (counts only) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Telemetri/SkoleMetrikker.cs`

**Checkpoint**: Quickstart scenarios 7–8 pass. Linked education state is readable through `IUtdanningsgrunnlag`, and unlinked records are invisible.

---

## Phase 6: User Story 6 - Expected population and coverage breakdown (Priority: P2; prerequisite for the lock)

**Goal**: Pure, explainable calculators that decide who was expected and put each child in exactly one coverage bucket, plus the frozen-basis tables.

**Independent Test**: The unit tests of the calculators cover acceptance scenarios 1–10 from fixed inputs. The end-to-end check through a real lock is in US3 (T106).

### Tests for User Story 6 ⚠️

- [X] T093 [P] [US6] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/Dekning/SkolealderTests.cs`:
  - A period in month m of year Y belongs to school year Y0 = Y if m ≥ 8, else Y−1; cohorts born Y0−15 … Y0−6.
  - 2026-10 → born 2011–2020; 2027-03 → born 2011–2020.
  - The provisional birth date is used when there is no exact one.
  - Neither → `ManglerFodselsdato`.
- [X] T094 [P] [US6] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/Dekning/RapportoerkategoriTests.cs`: most placement days wins; on a tie, the placement ending last; if both end on the same day, `Fosterhjem`; `StatligInstitusjon` and `Annen` placements are ignored (FR-027a)
- [X] T095 [P] [US6] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/Dekning/ForventningsberegnerTests.cs`:
  - Scenarios 1, 2, 3, 4 and 10.
  - Each `vurdering` value: `Forventet`, `IkkeSkolepliktig`, `ManglerUtdanningsforhold`, `ManglendeSkoletilbudHelePerioden`, `KunStatligInstitusjon`, `ManglerFodselsdato`, `IkkePlassert`.
  - Kode 6/7 children: `medregnet_levert` is always false, and `medregnet_full` follows the option.
  - A missing classification is frozen as 3.
  - A child named only on a rejection record (not placed, no accepted report) is frozen with `IkkePlassert`, no bucket, and its gradering at lock.
- [X] T096 [P] [US6] Property-style unit tests (seeded random populations, `[Theory]` over seeds) in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/Dekning/DekningsberegnerTests.cs`:
  - `Mottatt + KunAvvist + Mangler = Forventet` for each variant and category, and the categories sum to `Totalt`.
  - *Rejected only* is attributed through the rejection's child (scenario 9).
  - Withdrawn or deleted reports count as no report.
  - `MottattIkkeForventet` and `AVVIST_IKKE_TILORDNET` per reason appear as totals only.
  - `UkjentBarn` appears only in `Full` with the option on.
  - `Levert` always leaves out Kode 6/7 children and `UKJENT_BARN`.
  - A rejection whose child is resolved but not expected (placed or not) counts under `AVVIST_IKKE_TILORDNET` by reason, not as `KunAvvist`.
  - A rejection for a child frozen as Kode 6/7 is left out of `AVVIST_IKKE_TILORDNET` in `Levert`, and in `Full` unless the option is on (FR-009).
  - Every summary figure can be recomputed from the `Grunnlagsbarn` set plus the rejection records alone (MP-06, SC-006).

### Implementation for User Story 6

- [X] T097 [P] [US6] Implement `Skolealder.ErSkolepliktig(fodselsaar, periode)` and `Fodselsaar.Fra(eksakt?, usikker?)` in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Dekning/Skolealder.cs`
- [X] T098 [P] [US6] Implement `Rapportoerkategori {PrivatInstitusjon, Fosterhjem}` and `Rapportoerkategorivelger.Velg(plasseringer, periode)` in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Dekning/Rapportoerkategori.cs`
- [X] T099 [US6] Implement `Forventningsberegner.Beregn(periode, plasseringer, personData, utdanningsstatus, barnMedGodkjentRapport, barnMedAvvisning, tellGraderteBarn)` in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Dekning/Forventningsberegner.cs`. It returns a `Grunnlagsbarn` per placed child, child with an accepted report, and child named on a rejection record, with these fields:
  - `vurdering`, evaluated in FR-027 order (a)→(d).
  - `rapportoerkategori`, null when the child is not expected.
  - The placement clipped to the period.
  - `sikkerhetsnivaa_ved_laas`: a missing value becomes 3.
  - `fodselsaar` and `fodselsdato_kilde`.
- [X] T100 [US6] Implement `Dekningsberegner.Beregn(grunnlag, godkjenteRapporter, avvisningerMedBarn, avvisningerUtenBarn, antallUkjentBarn, tellGraderteBarn)` in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Dekning/Dekningsberegner.cs`. It sets each child's `dekningsbucket` {`Mottatt`, `KunAvvist`, `Mangler`, `MottattIkkeForventet`} and returns summaries for variants `Levert` and `Full` × `kategori` {`Totalt`, `PrivatInstitusjon`, `Fosterhjem`} × `maal` {`Forventet`, `Mottatt`, `KunAvvist`, `Mangler`, `MottattIkkeForventet` (totals only), `UkjentBarn` (`Full` only, option on)}. It also returns rejections per `aarsakskode`, and `regelversjon` `"1"`. Attribution follows data-model.md "Rejection attribution": a rejection counts as `KunAvvist` only when its child is frozen as `Forventet`; every other rejection counts by reason, and only in a variant where its child is counted (`medregnet_levert`/`medregnet_full`)
- [X] T101 [US6] Create EF configurations in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Skolenaervaer/Configurations/`. All tables are immutable after insert:
  - **`dekningsgrunnlag`**: `periode` PK, `frosset_tidspunkt`, `regelversjon` `varchar(16)`, `tell_graderte_barn` `bit`.
  - **`dekningsgrunnlag_barn`**:
    - PK (`periode`, `barn_registrering_id`).
    - `birk_id` `varchar(32)` null; `plassering_fra` and `plassering_til` `date` null.
    - `rapportoerkategori` `tinyint` null; `sikkerhetsnivaa_ved_laas` `tinyint` not null; `fodselsaar` `smallint` null; `fodselsdato_kilde` `tinyint` null.
    - `vurdering` `tinyint` not null; `dekningsbucket` `tinyint` null.
    - `medregnet_levert` and `medregnet_full` `bit`.
  - **`dekningsoppsummering`**: `periode`, `variant`, `kategori`, `maal`, `antall`.
  - **`dekningsoppsummering_avvisning`**: `periode`, `variant`, `aarsakskode`, `antall`.

  Then add migration `Dekningsgrunnlag`
- [X] T102 [US6] Implement `Grunnlagsinnhenter.HentAsync(periode, barnFilter?, personData?, ct)` (when `personData` is given, no second Person call is made) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Dekning/Grunnlagsinnhenter.cs`. Inputs, gathered for both the lock and the follow-up lists:
  - Placements from `IPlasseringskilde`.
  - `IPersonoppslag.HentBarnAsync` in batches of ≤ 1000 for placed children, children with an accepted report, and children named on a rejection record for the period (their gradering at lock decides whether their rejections count).
  - `IUtdanningsgrunnlag`.
  - Current `Godkjent` reports and `avvisning` rows for the period.

  If any port is `Utilgjengelig`, it throws `AvhengighetUtilgjengeligException`

**Checkpoint**: The calculators pass their unit tests, and the frozen-basis tables exist.

---

## Phase 7: User Story 3 - Lock the period and deliver one final dataset (Priority: P1)

**Goal**: On the 11th, the period locks atomically and the frozen basis, coverage and dataset rows are stored. Deterministic Parquet files and manifests are written to Skole-owned storage, and exactly one `SkolenaervaersperiodeLukket` is published after them.

**Independent Test**: Accept reports, run the lock (`laas-naa` locally), and verify the files, the rows, byte-identical re-export, rejection of later submissions and a single notification (quickstart scenarios 9–11).

### Tests for User Story 3 ⚠️

- [X] T103 [P] [US3] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/LeveranseradbyggerTests.cs`:
  - Only `Godkjent` reports for children not Kode 6/7 at lock become rows.
  - Sort order is `birk_id`, `fra_dato`, `til_dato`, `rapportoer_type`, `rapportoer_identitet`.
  - `FLERE_RAPPORTOERER` is set on every row when there is more than one reporter, and never for one reporter with two reports.
  - `OVERLAPPENDE_RAPPORTER_SAMME_RAPPORTOER` is set for 1–15 and 1–31, but not for 1–10 and 20–31.
  - `fravaer` is a canonical JSON array sorted by code.
  - Version flags and lock flags are combined into one bit set.
- [X] T104 [P] [US3] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/PeriodelaasTests.cs`: transitions `Aapen→Laaser→Laast→Levert` only, each other transition throws; `Utsett` only while `Aapen` and only to a later time
- [X] T105 [P] [US3] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Leveranse/ParquetDeterminismeTests.cs` (after T119):
  - The same frozen rows written twice give identical bytes.
  - Column names, order and types match `contracts/bufdata-leveranse.md`: `DATE` as `INT32`, `DECIMAL(6,2)` as `FIXED_LEN_BYTE_ARRAY`, `fravaer` as `LIST<STRUCT<kode,timer>>`, `kvalitetsflagg` as `LIST<UTF8>`.
  - One row group, Snappy compression.
  - The coverage file is long-format and sorted by `kategori`, `maal`, `aarsakskode`.
  - The manifest is byte-stable, with `laast` = the recorded lock time.
  - *As built: six tests, written after T119 and red (compile failure) before T120/T121. Same bytes also for rows in another order and for `120.5` vs `120.50`; an empty dataset is one row group with no rows; `created_by` is `Parquet.Net version 6.1.0 (build …)` and there is no key-value metadata. The schema test pins the as-built list shape: `fravaer` and `kvalitetsflagg` are OPTIONAL list groups (never null, empty when there are none) because Parquet.Net 6.1.0's schema API cannot declare a REQUIRED list group, and its class serializer, which can, writes `DECIMAL(6,2)` as `INT32`, not the contract's `FIXED_LEN_BYTE_ARRAY` (noted in contracts/bufdata-leveranse.md). `antall` carries the `INT_32` annotation. Flag values and the coverage sort keys are in ordinal order. The manifest uses `\n` line ends on every platform and drops trailing zeros of fractional seconds in `laast`.*
- [X] T106 [P] [US3] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Laas/PeriodelaasEndeTilEndeTests.cs`:
  - Reports for A and B; `FakeTimeProvider` is moved past the effective lock time.
  - The period goes `Laaser` → `Laast` → `Levert`.
  - Azurite holds `periode=2026-10/data.parquet` and `_manifest.json` in both containers, with the manifests written last.
  - The dataset rows equal A's accepted reports, and B's report is `Tilbakeholdt` (US2 scenario 4).
  - The files contain no fødselsnummer bytes.
  - Exactly one `SkolenaervaersperiodeLukket` with `periode`, `antallLeverteRader`, `dekning` and `korrelasjonId`, published after the files exist (SC-009).
  - With `TellGraderteBarn = true`, `dekning` is absent (US2 scenario 5).
  - *As built: each lock test owns a period (`Laasscene`) and parks every other open one. The file, manifest and `SkolenaervaersperiodeLukket` assertions were skipped until T119–T122 and now run (2026-10-14); the `TellGraderteBarn = true` notification case is a new test on its own period (`EndeTilEndeTelling`), which also checks that the delivery publishes no `LeseloggHendelse` (DEC-125). The host's `LeveransefilUtboksJobb` is off in the tests (as the planner is), and `Laasscene.LeverAsync` runs it, so the `Laast` assertions do not race the delivery; the "three files waiting" case became "one outbox row per file, `bibliotek_versjon = Parquet.Net 6.1.0`".*
  - A later report for 2026-10 gets 422 `UTENFOR_GYLDIGHETSVINDU`.
  - `SkolenaervaersrapportEndret` with status `Laast` for every report, A's and B's alike: no event tells that B was withheld (constitution: no gradering in events).
- [X] T107 [P] [US3] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Laas/LaasGjentakelseTests.cs`:
  - Re-running the lock changes nothing.
  - Re-export matches the stored SHA-256 (SC-005).
  - A different pre-existing blob raises the mismatch metric and is never overwritten.
  - July is never locked.
  - Person or Tjeneste unavailable during gather → the message retries, nothing is committed, the period stays `Laaser`, and the lock completes once they recover (FR-026).
  - A test transaction holds the `HOLDLOCK` read on the period row (as T068 does): the claim waits until it commits, and the report written in it is in the frozen data.
  - 200 concurrent reports are fired while `FakeTimeProvider` passes the lock time. Every `meldingId` answered 200 ends in `leveranserad`, as `Tilbakeholdt`, or as a `UKJENT_BARN` rejection; every one handled after the claim gets 422 `UTENFOR_GYLDIGHETSVINDU` (FR-025, SC-007).
  - Person (WireMock) and Key Vault receive no call between the start and the end of the lock's commit transaction.
  - *As built: the re-export and the pre-existing-blob cases were skipped until T119–T122 and now run (2026-10-14), each on its own period. Re-export calls `Leveranseeksport.EksporterAsync` for a `Levert` period: `Uendret`, hashes, blob ETags and the single notification unchanged; with a tampered stored hash it is `Avvik`, the counter rises and nothing is rewritten. The pre-existing blob leaves the outbox row `Feilet`, the counter at 1 after two runs, the blob as it was, no manifest and no notification.*
- [X] T108 [P] [US3] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Laas/GraderingVedLaasTests.cs`: a child whose level the Person WireMock changes to Kode 7 before the lock is withheld, with no event involved; a batch answer without `sikkerhetsnivaa` freezes the child as 3, the lock completes and the gauge counts 1 (FR-008a); a placed child with no birth date is counted on its gauge (FR-028)
- [X] T109 [P] [US3] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Drift/LaasutsettelseTests.cs`:
  - `PUT /api/skole/v1/drift/perioder/{periode}/laastidspunkt` gets 200 and writes a `laasutsettelse` row with oid, old time, new time and `korrelasjon_id`.
  - 409 `LAAS_HAR_KJOERT` once the lock has run.
  - 422 `NYTT_TIDSPUNKT_IKKE_SENERE`.
  - 404 for an unknown period or July.
  - 403 without `Skole:UtsetteLaas`.
  - 503 `AUTORISASJON_UTILGJENGELIG`.
  - Reports are still accepted after the original time while the lock is postponed.
  - `GET /api/skole/v1/drift/perioder?fra&til` lists statuses and postponements.

- [X] T110 [P] [US3] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Laas/LaasGjenopprettingTests.cs` (T114, FR-024, FR-026): a period left in `Laaser` after a simulated restart is re-sent once and completes, with no second dataset; a period `Laast` but not `Levert` has its file outbox woken and ends `Levert` with the stored SHA-256; a period already `Levert` is left alone. *As built: the `Laast`-but-not-`Levert` case was skipped until T122 and now runs (2026-10-14): the outbox rows are backed off far ahead, a run of the outbox does nothing, a new planner's first run clears the backoff, and the next run ends `Levert` with `datasett_sha256` equal to the blob's SHA-256.*

### Implementation for User Story 3

- [X] T111 [US3] Add `TaKrav(naa)`, `MarkerLaast(tidspunkt, tellGraderteBarn)`, `MarkerLevert(tidspunkt)` and `Utsett(nyttTidspunkt)` to `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Periodelaas.cs`
- [X] T112 [US3] Implement `Leveranseradbygger.Bygg(rapporterMedGjeldendeVersjon, graderingVedLaas)` → frozen rows with lock-time flags per FR-019, in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Leveranseradbygger.cs`
- [X] T113 [US3] Create EF configurations in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Skolenaervaer/Configurations/`:
  - **`laasutsettelse`** (append-only): `id` PK; `periode` FK; `gammelt_laastidspunkt`; `nytt_laastidspunkt`; `utfoert_av`; `utfoert_tidspunkt`; `korrelasjon_id`.
  - **`leveranse`**: `periode` PK; `status` `tinyint` {`Klargjort`, `Skrevet`, `Publisert`}; `antall_rader` `int`; `datasett_sha256` and `dekning_sha256` `binary(32)` null; `skjemaversjon` `varchar(16)`; `bibliotek_versjon` `varchar(32)`.
  - **`leveranserad`**: PK (`periode`, `radnr`); `rapport_id`; `versjon_id`; `birk_id` `varchar(32)`; `fra_dato`; `til_dato`; `rapportoer_type`; `rapportoer_identitet` `varchar(11)`; the hour columns `decimal(6,2)`; `fravaer` `nvarchar(max)`; `kilde` `varchar(8)`; `kvalitetsflagg` `int`.
  - **`leveransefil_utboks`**: `id` PK; `periode`; `filtype` {`Datasett`, `Dekning`, `Manifest`}; `status` {`Venter`, `Skrevet`, `Feilet`}; `antall_forsoek`; `neste_forsoek`; `feil` `nvarchar(400)`.

  Then add migration `LaasOgLeveranse`
- [X] T114 [US3] Implement `LaasplanleggerJobb` (leased, every minute) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/LaasplanleggerJobb.cs`:
  - Conditional claim through `ExecuteUpdate` (`WHERE status = Aapen AND effektivt_laastidspunkt <= @naa` → `Laaser`, `laas_startet`). On success it sends the durable local message `KjoerPeriodelaas(periode)`. The update blocks behind in-flight intake transactions holding the period row (T068), so no extra coordination is needed.
  - Restart recovery: it re-sends for periods stuck in `Laaser`, and wakes the file outbox for periods that are `Laast` but not `Levert`.
  - *As built: the claim and the message commit in one outbox transaction; the re-send runs on the first run after a start. Since T122 the same first run wakes the file outbox: it clears `neste_forsoek` on `Venter` rows of `Laast` periods.*
- [X] T115 [US3] Implement `KjoerPeriodelaasHandler` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Laas/KjoerPeriodelaasHandler.cs`. It is a no-op unless the period is `Laaser`.
  - **Gather**: `IUtdanningsgrunnlag.FinnKoblingerAsync()`, then `Grunnlagsinnhenter` (T102). On `AvhengighetUtilgjengeligException`, rethrow so Wolverine retries with backoff, configured on the message type.
  - **Commit** in one transaction over one `SqlConnection` shared by both DbContexts (`Database.UseTransaction`):
    - `IUtdanningsgrunnlag.KobleAsync(koblinger)`, with `UtdanningDbContext` enlisted in the transaction first. No Person or Key Vault call is made in the commit.
    - Re-read `IUtdanningsgrunnlag`.
    - `Forventningsberegner` and `Dekningsberegner`.
    - Insert `dekningsgrunnlag`, `dekningsgrunnlag_barn`, both summaries and `leveranserad` (T112).
    - Set reports to `Levert`/`Tilbakeholdt` and send `SkolenaervaersrapportEndret` with status `Laast` for each, never `Levert` or `Tilbakeholdt` (contracts/hendelser.md).
    - Set the period to `Laast` with `laast_tidspunkt` and the frozen `tell_graderte_barn`.
    - Insert `leveranse` `Klargjort` and three `leveransefil_utboks` rows in `Venter`.

  Nothing is committed on failure (FR-024, FR-026)

  *As built: the work is in `Infrastructure/Laas/Periodelaaser.cs`; the handler retries any failure (1 s, 2 s, 5 s, then scheduled 10 s … 5 min, indefinitely). The Utdanning context joins the transaction through `IUtdanningstransaksjon` (MP-07), and `IUtdanningsgrunnlag.HentForPeriodeAsync` got an optional `tidspunkt` so the commit reads the state it just linked (data-model.md). Rejections are counted up to the claim (`laas_startet`). `leveranse.bibliotek_versjon` was `ikke-valgt (ADR-028)` until T119 and is now `Parquetbibliotek.Versjon`.*
- [X] T116 [US3] Register the lock gauges in `SkoleService/src/M2LB.SkoleService.Infrastructure/Telemetri/SkoleMetrikker.cs`: lock state per period, children frozen as Kode 6/7 without a classification, placed children without a birth date, and a delivery hash mismatch counter (counts only; FR-008a, FR-028)
- [X] T117 [US3] Map the operations endpoints from `contracts/drift.md` in `SkoleService/src/M2LB.SkoleService.Api/Rest/Drift/DriftEndepunkter.cs`:
  - `PUT /api/skole/v1/drift/perioder/{periode}/laastidspunkt`:
    - Policy `Skole:UtsetteLaas`.
    - Conditional update `status = Aapen AND @nytt > effektivt_laastidspunkt`.
    - Writes the audit row and a structured audit log; `begrunnelse` goes to the audit log only, never to events.
    - Error codes: `LAAS_HAR_KJOERT`, `NYTT_TIDSPUNKT_IKKE_SENERE`, 404, `AUTORISASJON_UTILGJENGELIG`.
  - `GET /api/skole/v1/drift/perioder?fra=&til=`.

  *As built: M2LB.Common's `Modul:Operasjon` policies are HotChocolate-only, so both endpoints call `IAutorisasjonClient.EvaluateAsync(bruker, "Skole:UtsetteLaas")` themselves (403 when denied, 503 `AUTORISASJON_UTILGJENGELIG` on failure). The check and the update run under an `UPDLOCK` read of the period row instead of a conditional `UPDATE`; the effect is the same.*
- [X] T118 [US3] Map `POST /api/skole/v1/lokalt/perioder/{periode}/laas-naa`, which sets the effective lock time to now and is mapped only when `IsLocal()`, in `SkoleService/src/M2LB.SkoleService.Api/Rest/Lokalt/LokaltEndepunkter.cs`
- [X] T119 [US3] **Gate: ADR-028 signed off.** Add `Parquet.Net`, pinned exactly, to `SkoleService/src/M2LB.SkoleService.Infrastructure/M2LB.SkoleService.Infrastructure.csproj`, and expose its version as a constant for `leveranse.bibliotek_versjon`
  *As built: `Version="[6.1.0]"` (exact range) with a comment pointing at ADR-028; the constant is `Leveranse/Parquetbibliotek.Versjon = "Parquet.Net 6.1.0"`, and `ParquetbibliotekTests` (red first) checks it against the loaded assembly. `Periodelaaser` records it. `renovate.json` got a last `packageRules` entry `matchPackageNames: ["Parquet.Net"], enabled: false`, so Renovate never opens a PR for it; `docs/Renovate - Dependency management.md` says why. Parquet.Net comes from nuget.org; it brings Snappier, K4os.Compression.LZ4, ZstdSharp.Port, CommunityToolkit.HighPerformance and Microsoft.IO.RecyclableMemoryStream transitively.*
- [X] T120 [US3] Implement `DatasettParquetSkriver` and `DekningParquetSkriver`, deterministic per `contracts/bufdata-leveranse.md`, in `SkoleService/src/M2LB.SkoleService.Infrastructure/Leveranse/ParquetSkrivere.cs`:
  - Fixed schema and column order; one row group; Snappy; no wall-clock metadata.
  - `rapportoer_type` `PERSON`/`ORGANISASJON`.
  - Coverage `kategori` `TOTALT`/`PRIVAT_INSTITUSJON`/`FOSTERHJEM`.
  - Coverage `maal` `FORVENTET`/`MOTTATT`/`KUN_AVVIST`/`MANGLER`/`MOTTATT_IKKE_FORVENTET`/`AVVIST_IKKE_TILORDNET` from the `Levert` variant only (FR-035).

  *As built: static `DatasettParquetSkriver.SkrivAsync` and `DekningParquetSkriver.SkrivAsync` return a `Parquetfil(Innhold, AntallRader)`. Both use Parquet.Net's low-level `ParquetWriter` with an explicit schema, so `DECIMAL(6,2)` is `FIXED_LEN_BYTE_ARRAY` (`forceByteArrayEncoding`); list leaves are written with explicit Dremel levels, so an empty list is an empty list, not null. The list groups are OPTIONAL in the schema (see T105). Rows are written in `radnr` order; `fravaer` is read from the frozen canonical JSON. `AVVIST_IKKE_TILORDNET` rows come from `dekningsoppsummering_avvisning` (`Levert`), under `TOTALT` with their `aarsakskode`.*
- [X] T121 [US3] Implement `ManifestSkriver`, which writes `_manifest.json` with `periode`, `kontraktversjon` `1.0.0-utkast`, `laast` (the recorded lock time) and `filer[{navn, sha256, antallRader, bytes}]` in a fixed property order, in `SkoleService/src/M2LB.SkoleService.Infrastructure/Leveranse/ManifestSkriver.cs`
  *As built: `Utf8JsonWriter`, two-space indent, `\n` line ends, UTF-8 without BOM; `laast` in UTC (`yyyy-MM-ddTHH:mm:ss.FFFFFFFZ`); `sha256` lowercase hex. `Manifestfil.For(navn, fil)` computes the entry. One manifest per container, each listing that container's `data.parquet`.*
- [X] T122 [US3] Implement `LeveransefilUtboksJobb` (GL-33) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Leveranse/LeveransefilUtboksJobb.cs`:
  - Leased; uses `BlobServiceClient` with `DefaultAzureCredential` on `Leveranse:Lagring:BlobEndpoint`.
  - Files are regenerated from `leveranserad` and `dekningsoppsummering`.
  - Upload uses `If-None-Match: *` to `periode=YYYY-MM/data.parquet`. If the blob exists, download it and compare SHA-256: equal means done; different means the mismatch metric, status `Feilet`, and stop.
  - Each hash is recorded in `leveranse`, and the manifest is written last in each container.
  - Finally, in one transaction: `leveranse` → `Publisert`, period → `Levert` with `levert_tidspunkt`, and `SkolenaervaersperiodeLukket` through the outbox. Its `dekning` comes from `Levert` `Totalt`, and is omitted when the frozen `tell_graderte_barn` is true (FR-037).

  *As built: the job (leased, every minute) picks `Laast` periods whose outbox rows are not `Feilet` and whose `Venter` rows are past `neste_forsoek`, and hands each to `Leveranse/Leveranseeksport.cs`, which does the work above. Order per container: `data.parquet`, then `_manifest.json` (dataset container first). The stored hash is checked too: if `leveranse` already holds a hash the regenerated bytes do not match, that is a mismatch (alert, `Feilet`, nothing written). A failure that is not a mismatch counts the attempt on the `Venter` rows and backs off 1 minute doubling to 1 hour; `feil` holds the exception type and the storage status and error code only. The final transaction reads the period row `WITH (UPDLOCK, ROWLOCK)`, so it publishes once. A `Levert` period run again only verifies (`Eksportutfall.Uendret` or `Avvik`): the re-export of SC-005. The client: `Leveranse:Lagring:BlobEndpoint` is a URL (managed identity, `DefaultAzureCredential`) or, locally, a connection string (Aspire passes the Azurite `blobs` string), built on first use in `Program.cs`. `SkolenaervaersperiodeLukket.Dekning` is `[JsonIgnore(WhenWritingNull)]`, so it is absent, not `null`. Nothing in the delivery is read-logged (DEC-125); logs carry the period and file type only. Tests (red as compile failures before this task): the un-skipped cases of T106, T107, T110 and T152 above, plus the new `TellGraderteBarn` case.*

**Checkpoint**: Quickstart scenarios 9–11 pass. One deterministic dataset per period, an explainable coverage breakdown, and one notification after the files. Together with US1, US2, US7 and US6 this is the first-delivery slice.

---

## Phase 8: User Story 4 - Hold reports for children not yet known (Priority: P2)

**Goal**: Unresolved reports are held encrypted, retried on Person events and every hour, processed on resolution, and rejected at lock with the identifier deleted.

**Independent Test**: Submit a report for child C, make C known before lock, and verify processing; repeat without C, then verify rejection at lock and that no held rows remain (quickstart scenario 4, SC-003, SC-008).

### Tests for User Story 4 ⚠️

- [X] T123 [P] [US4] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Hold/HoldtRapportTests.cs`:
  - A report for unknown C gets 200 `{"utfall":"Mottatt"}`, byte-identical to an accepted report.
  - A `holdt_rapport` row exists, and `DatabaseSkanner` finds no plaintext identifier.
  - Person unavailable → held (FR-046: still ready).
  - An identity-independent rule failure (`UGYLDIG_TIMEVERDI`) → 422 and no hold.
  - A Key Vault failure → 503.
  - Adding C to the stub and publishing `BarnRegistrert` → the sweep processes the report and the row is deleted.
  - A held report that resolves as Kode 6 is processed and the row deleted (US2 scenario 6).
  - A correction held before resolution is applied in send-time order.
  - `PersonOpprettet`, and `PersonOppdatert` with `foedselsnummerEndret`, trigger a sweep; `PersonOppdatert` without either flag does not.
- [X] T124 [P] [US4] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Hold/HoldVedLaasTests.cs`:
  - Still unresolved at lock → an `avvisning` with `UKJENT_BARN`, `kilde` `Laas` and no child or reporter.
  - Zero `holdt_rapport` rows for the period afterwards (SC-003).
  - `UKJENT_BARN` is counted only in the `Full` variant with the option on, and never in `Levert`.
  - A held report that resolves in the final lookup is delivered (SC-008).

### Implementation for User Story 4

- [X] T125 [US4] Create the entity `HoldtRapport` in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/HoldtRapport.cs`
- [X] T126 [US4] Configure the tables:
  - **`holdt_rapport`**: `holdt_id` PK; `melding_id` unique; `kjede_start_melding_id`; `sendt_tidspunkt`; `periode` `char(7)`; `mottatt_tidspunkt`; `kryptert_innhold` `varbinary(max)`; `nonce` `binary(12)`; `tag` `binary(16)`; `innpakket_datanoekkel` `varbinary(512)`; `noekkel_id` `nvarchar(256)`; `antall_forsoek` `int`; `siste_forsoek` null.
  - **`sveip_forespoersel`**: `jobbnavn` PK, `forespurt_tidspunkt`.

  Files in `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Skolenaervaer/Configurations/`. Then add migration `Hold`
- [X] T127 [US4] Implement `Rapportholder` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Hold/Rapportholder.cs`. It serialises the full `InnkommendeRapport` (identifier, type, reporter and content), encrypts it with `Noekkelformaal.HoldtRapport` and stores the row. Then replace the interim 503 in `SkoleService/src/M2LB.SkoleService.Api/Rest/Rapportinntak/Rapportinntaksbehandler.cs`: an unresolved or unavailable lookup now holds the report and answers 200 with `behandling` `Holdt`
- [X] T128 [US4] Implement `HoldtRapportSveip` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Hold/HoldtRapportSveip.cs`:
  - Decrypts open-period rows in batches and looks each one up.
  - Resolved: runs the US1/US2 pipeline per chain in `sendt_tidspunkt` order. The validity window uses the original submission date and the current period status. Each row is deleted in the same transaction.
  - Unresolved: increments `antall_forsoek` and sets `siste_forsoek`.
  - Each resolution transaction first takes the same `HOLDLOCK` read of the period row as T068, and skips rows whose period is no longer `Aapen`.

  *As built: the store-a-valid-report step of the intake moved to `Infrastructure/Rapportinntak/Rapportlagring.cs`, shared by the intake, the sweep and the lock; resolution is `Rapportholder.LoesAsync`. The intake encrypts before it opens its transaction, and only a report the validity rules accept, so an invalid report is 422 whatever Key Vault does (FR-007). Integration tests deliver Person events to every handler chain (`Fixtures/Personhendelser.cs`), because two chains for `BarnRegistrert` make `IMessageBus.InvokeAsync` find none (`MultipleHandlerBehavior.Separated`).*
- [X] T129 [US4] Implement `HoldtRapportSveipJobb` (leased; runs every `Skolenaervaer:Hold:Gjenforsoeksintervall`, and when a `sveip_forespoersel` marker is set, debounced by `SveipDebounce`) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/HoldtRapportSveipJobb.cs`
- [X] T130 [US4] Implement the sweep-request handlers `BarnRegistrertSveipHandler`, `PersonOpprettetHandler` and `PersonOppdatertHandler` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Messaging/Skolenaervaer/`. `PersonOppdatertHandler` acts only when `foedselsnummerEndret || dufNummerEndret`. Each sets the marker, with dedupe through T040
- [X] T131 [US4] Extend `SkoleService/src/M2LB.SkoleService.Infrastructure/Laas/KjoerPeriodelaasHandler.cs`:
  - Gather: a final lookup of every `holdt_rapport` row for the period.
  - Commit: resolved reports become accepted reports before the population is frozen. Unresolved ones become `avvisning` `UKJENT_BARN` (`kilde` `Laas`, no child) and their rows are deleted.
  - The unknown count is passed to `Dekningsberegner` (FR-006, FR-009).
- [X] T132 [US4] Register the gauge for held reports per period (basis for FR-026a (a)) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Telemetri/SkoleMetrikker.cs`

**Checkpoint**: Quickstart scenarios 4 and 12 pass, and no held identifier survives a lock.

---

## Phase 9: User Story 5 - Corrections, withdrawals and more than one reporter (Priority: P2)

**Goal**: Full message-chain semantics, independent of arrival order, with concurrency safety.

**Independent Test**: Submit, correct, withdraw and delete reports from one and from two reporters, and verify versions, history and the delivered rows (quickstart scenario 5).

### Tests for User Story 5 ⚠️

- [X] T133 [P] [US5] Unit tests in `SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/MeldingskjedeTests.cs`: `Trukket`/`Slettet` close the chain at their send time; a message of the chain sent before the close has no effect and one sent after applies; `Duplikat` with a known id is a replay and with a new id a correction; `Tillegg` has no effect
- [X] T134 [P] [US5] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Rapportinntak/KorreksjonOgKjedeTests.cs`, covering acceptance scenarios 1–9:
  - `Oppdatering` with the same key gives a new current version and keeps history.
  - Two reporters → both rows flagged `FLERE_RAPPORTOERER` at lock.
  - A correction arriving before its original → the latest send time is current.
  - `Trukket` and `Slettet` → the report is not delivered and counts as no report in coverage.
  - A correction with a different key → a new report in the chain, the original unchanged.
  - `Duplikat` known/new.
  - `Tillegg` → 200 with no effect.
  - A receipt value → 422 `UVENTET_MELDINGSFORBINDELSE`.
  - 1–10 and 20–31 are not flagged; 1–15 and 1–31 are flagged `OVERLAPPENDE_RAPPORTER_SAMME_RAPPORTOER`.
  - A withdrawal before the report → an earlier message arriving later has no effect.
  - A withdrawal of a held chain deletes its `holdt_rapport` rows and records no child (FR-006, DEC-82).
- [X] T135 [P] [US5] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/Rapportinntak/SamtidighetTests.cs`: parallel messages with different `meldingId`s for the same key → one 409, which succeeds on retry, and both versions are kept; parallel copies of one `meldingId` → one outcome (FR-022, FR-045b)

### Implementation for User Story 5

- [X] T136 [US5] Create the entity `Meldingskjede` (`StartMeldingId`; `Avslutt(type, sendtTidspunkt, meldingId)`; `HarIngenEffekt(sendtTidspunkt)`) in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Meldingskjede.cs`
- [X] T137 [US5] Configure `meldingskjede`: `start_melding_id` PK; `avsluttet_type` `tinyint` null {`Trukket`, `Slettet`}; `avsluttet_sendt_tidspunkt` null; `avsluttet_melding_id` null. File: `SkoleService/src/M2LB.SkoleService.Infrastructure/Persistence/Skolenaervaer/Configurations/MeldingskjedeConfiguration.cs`. Then add migration `Meldingskjede`
- [X] T138 [US5] Implement the chain step and replace the interim 503 for `Trukket`/`Slettet`/`Tillegg` in `SkoleService/src/M2LB.SkoleService.Api/Rest/Rapportinntak/Rapportinntaksbehandler.cs`:
  - Step 3: a message of a closed chain sent before the close → `Mottatt`, `behandling` `KjedeAvsluttet`.
  - `Trukket`/`Slettet` close the chain, set every report of the chain with no version sent after the close to `Trukket`/`Slettet` with an event, and delete that chain's `holdt_rapport` rows.
  - A withdrawal before its report is recorded against the chain.
  - `Oppdatering` with a different key creates a new report in the same chain (FR-021).
  - `Tillegg` → logged, `Mottatt`, `behandling` `Tillegg`.
- [X] T139 [US5] Make every new version update `rapport.gjeldende_versjon_id`, so `rowversion` changes and a concurrent version add collides into 409, in `SkoleService/src/M2LB.SkoleService.Domain/Skolenaervaer/Rapport.cs`

  *As built: the domain cannot force an update when the current version does not change, so `Infrastructure/Rapportinntak/Rapportlagring.cs` marks `gjeldende_versjon_id` modified for every new version. `Rapport` got `Avslutt` (withdraw or delete unless a version was sent after the close), and a version that becomes current on a withdrawn report makes it `Godkjent` again, since it was sent after the close (FR-020). `Trukket`, `Slettet` and `Tillegg` skip the Person lookup; a withdrawal records no child.*

**Checkpoint**: Quickstart scenario 5 passes, and the outcome is independent of arrival order.

---

## Phase 10: User Story 8 - Follow-up lists for caseworkers (Priority: P3)

**Goal**: GraphQL follow-up lists and the statistical coverage operation. Gradering is evaluated inside the query, and every child returned is read-logged before data is returned.

**Independent Test**: With a known relation, query both lists. Verify their content, that children outside the relation are hidden, that Kode 6/7 is shown only with `Person:SeGradertBarn`, and that each child is read-logged (quickstart scenario 6, SC-004).

### Tests for User Story 8 ⚠️

- [X] T140 [P] [US8] Schema snapshot test in `SkoleService/tests/M2LB.SkoleService.Integration/GraphQL/SchemaSnapshotTests.cs`, with the snapshot in `.../GraphQL/schema.snapshot.graphql` (regenerate with `UPDATE_GRAPHQL_SNAPSHOT=1`, the Hendelse/Tjeneste precedent). The first snapshot must match the types in `contracts/graphql-skole.graphql`. Note: no `object`-returning resolvers, because HotChocolate 16 drops them from the schema
  *As built: a second test checks that every object, enum and scalar type in the built schema equals the contract's (descriptions and directives ignored; HotChocolate's `ApplyPolicy` allowed).*
- [X] T141 [P] [US8] Authorisation scenario tests (SC-004, US2 scenarios 2–3) in `SkoleService/tests/M2LB.SkoleService.Integration/GraphQL/OppfolgingslisteTilgangTests.cs`:
  - A user with the relation only: B is neither shown nor counted.
  - A user with the relation and `Person:SeGradertBarn` for B: B is shown, and one `LeseloggHendelse` per returned child goes to `leselogg` with all 7 fields.
  - A child outside the relation is never shown.
  - Unknown gradering (no classification) is hidden.
  - Changing B's level in the Person WireMock between two reads changes B's visibility on the next read, with no event (FR-008a).
  - Person unreachable → error `AVHENGIGHET_UTILGJENGELIG` and no data.
  - For a relation-only user, B's id never appears in the request bodies sent to the Tjeneste WireMock, and B is not in the input to the list calculation (only V is computed).
  - Autorisasjon unavailable → error `AUTORISASJON_UTILGJENGELIG` and no data.
  - The audit message cannot be stored → no data.
  - The counting option on changes nothing.
  *As built: an audit store failure is simulated with a test trigger on `wolverine.wolverine_outgoing_envelopes`; the counting-option case runs a second host with `TellGraderteBarn = true`. Extra cases: a grant without a relation, a relation without `Skole:SeOppfolgingsliste`, a child Person no longer knows, Tjeneste unreachable, an empty visible set (no Tjeneste call), and anonymous → 401.*
- [X] T142 [P] [US8] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/GraphQL/OppfolgingslisteInnholdTests.cs`:
  - List (a) holds expected children without a `Godkjent` report.
  - List (b) holds placed, school-age children without an utdanningsforhold, linked records only.
  - Both lists have `foreloepig = true`.
  - With two open periods, the default is the one that locks first, and the other can be chosen.
  - A period that is not open → `PERIODE_IKKE_AAPEN`.
  *As built: "open" is `Aapen` and started (FR-011), so a created but future period (2026-12) and a locked one also give `PERIODE_IKKE_AAPEN`. The default-period case uses its own periods (`Laasscene.Perioder.Oppfolging`, 2031-01 and 2031-02).*
- [X] T143 [P] [US8] Integration tests in `SkoleService/tests/M2LB.SkoleService.Integration/GraphQL/DekningsstatistikkTests.cs`: with the option off at lock, variant `LEVERT` equals the delivered coverage file and `ukjentBarn` is null; with it on, variant `FULL` including `ukjentBarn`; `PERIODE_IKKE_LAAST`; 403-equivalent error without `Skole:SeDekningsstatistikk`
  *As built: the coverage file was not written until T120/T122 (ADR-028), so `LEVERT` is compared with the frozen `dekningsoppsummering` rows the file is written from (the file itself is checked by `ParquetDeterminismeTests` and `PeriodelaasEndeTilEndeTests`). The option-on lock runs a `Periodelaaser` built with `TellGraderteBarn = true`. Denial is `AUTH_NOT_AUTHORIZED`; an extra case checks Autorisasjon unavailable → `AUTORISASJON_UTILGJENGELIG`.*

### Implementation for User Story 8

- [X] T144 [US8] Set up GraphQL in `SkoleService/src/M2LB.SkoleService.Api/GraphQL/GraphQLOppsett.cs` with `AddSkoleGraphQL()` and `MapGraphQL("/graphql")`. Use HotChocolate 16.6.7 code-first with a `UUID` scalar, M2LB.Common authorisation, and an error filter. The filter maps `AutorisasjonUtilgjengeligException` → `AUTORISASJON_UTILGJENGELIG`, `AvhengighetUtilgjengeligException` → `AVHENGIGHET_UTILGJENGELIG` (Person or Tjeneste unreachable; fail closed with no data, FR-008a), plus `PERIODE_IKKE_AAPEN` and `PERIODE_IKKE_LAAST`, and never includes exception details
  *As built: `MapGraphQL("/graphql").RequireAuthorization()` (anonymous → 401). The error filter also maps a denial to `AUTH_NOT_AUTHORIZED` and logs any other failure by exception type only.*
- [X] T145 [US8] Implement `Leselogger` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Revisjon/Leselogger.cs`. It builds one `LeseloggHendelse` per child (`hendelsesId`, `hendelsesTidspunkt`, `brukerId`, `barnId`, `operasjonNavn` `Skole:SeOppfolgingsliste`, `tjenestenavn` `Skoletjenesten`, `korrelasjonId`) and commits it through the Wolverine outbox before returning; a failure throws (PP-03, GL-32)
- [X] T146 [US8] Implement `Oppfolgingstjeneste` (research §14 steps 1–5) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Oppfolging/Oppfolgingstjeneste.cs`:
  1. `HentBarnMedRelasjonAsync` gives R.
  2. `IPersonoppslag.HentBarnAsync(R)` gives gradering and birth date for R; unreachable → `AvhengighetUtilgjengeligException`.
  3. Per child in R, `HentEffektiveTilgangerAsync`; V = children with `Skole:SeOppfolgingsliste` for whom `Graderingsregel.ErSynlig` holds (no gradering → hidden).
  4. `Grunnlagsinnhenter.HentAsync(periode, V, personData)`, reusing step 2's Person answers for V, and `Forventningsberegner` compute the lists over V only.
  5. `Leselogger` runs before the result is returned.

  The default period is the open `Periodelaas` with the earliest effective lock time

  *As built: an open period is `Aapen` with its first day reached (FR-011). A child Person does not return is hidden like one without a gradering. An empty V makes no Tjeneste or Utdanning call.*
- [X] T147 [US8] Implement `OppfolgingQuery` (`oppfolgingManglendeRapporter(periode: String)`, `oppfolgingManglendeUtdanningsforhold(periode: String)`) and the types `Oppfolgingsliste`, `OppfolgingsBarn` and `Rapportoerkategori` (`PRIVAT_INSTITUSJON`, `FOSTERHJEM`) in `SkoleService/src/M2LB.SkoleService.Api/GraphQL/OppfolgingQuery.cs`
- [X] T148 [US8] Implement `DekningsstatistikkQuery` (`dekningsstatistikk(periode: String!)`) with policy `Skole:SeDekningsstatistikk`. It reads `dekningsoppsummering` for variant `Full` when `dekningsgrunnlag.tell_graderte_barn` is true, else `Levert`, and returns the types `Dekningsstatistikk`, `Dekningstall` and `AntallPerAarsak`. File: `SkoleService/src/M2LB.SkoleService.Api/GraphQL/DekningsstatistikkQuery.cs`
  *As built: `Skole:SeDekningsstatistikk` is evaluated in the resolver with `IAutorisasjonClient.EvaluateAsync`, not as a field policy: M2LB.Common's policy handler turns an unavailable Autorisasjon into a plain denial, while the contract asks for `AUTORISASJON_UTILGJENGELIG` (same reasoning as T117). The reader is `Infrastructure/Dekning/Dekningsstatistikkleser.cs`.*
- [X] T160 [US8] *Added during implementation (no task covered it):* route the read API through the proxy, as research §14 and the contract say. Add `skole-graphql-route` (`/api/skole/graphql` → `PathSet /graphql`, policy `skole`) and `skole-cluster` (`ca-m2lb-skole-<env>-nwe-001`, readiness probe `/health/ready`) to `Proxy/src/M2LB.Proxy.Api/appsettings.json` and `appsettings.Development.json`, the QA and local (`https://localhost:7219`) destinations to `appsettings.Qa.json` and `appsettings.Local.json`, and an `AzureAd:Schemes:skole` entry, the same way `tjeneste-graphql-route` is done. Locally, `Aspire/Aspire/AppHost.cs` gives the proxy a reference to `skole` and overrides the `skole-cluster` address, as for Tjeneste. **Gate:** `ClientId` (and the QA audience) are the placeholder `00000000-0000-0000-0000-000000000000` until Skoletjenesten's Entra app registration exists (see External gates); until then the proxy rejects every token on this route (401), so it fails closed

**Checkpoint**: Quickstart scenario 6 passes, and SC-004 holds for every list and lookup.

---

## Phase 11: Operations, Polish & Cross-Cutting Concerns

**Purpose**: Alerts, the retention guard, PII and load verification, and documentation

- [X] T149 [P] Implement `VarselvurderingJobb` (leased, every 5 minutes) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/VarselvurderingJobb.cs`. It emits gauges with counts only, thresholds from `Skole:Varsel:*`:
  - FR-026a (a): held reports for a period less than `HoldtFoerLaas` before its scheduled lock.
  - FR-026a (b): time since the last report inside the `InaktivitetVindu`.
  - FR-026a (c): the hourly rejected share, only when at least `AvvisningsandelMinimum` reports were answered.
  - FR-026: a period not `Laast` `LaasForsinkelse` after its scheduled time.
  - FR-040c: unlinked records older than `Varselgrense`.
  *As built: the evaluation is a pure `Varselvurdering` (with `Varselinnstillinger`) in `Infrastructure/Jobber/Varselvurdering.cs`; the job reads counts and emits gauges `skole.varsel.holdt_foer_laas{periode}`, `skole.varsel.inaktivitet`, `skole.varsel.avvisningsandel` and `…_over_grense`, `skole.varsel.laas_forsinket{periode}` and `skole.varsel.ukoblede_over_grense`. Every threshold is strict. The last report and the hourly share come from `meldingsmottak`, so they hold across replicas. The inactivity window is measured from the later of the last report and midnight (Oslo) on `FraDag`. FR-040c reads its count through a new `IUtdanningsgrunnlag.TellUkobledeMottattFoerAsync`, the one allowed bridge into Utdanning (MP-07).*
- [X] T150 [P] Unit tests for each threshold and its boundary in `SkoleService/tests/M2LB.SkoleService.Unit/Jobber/VarselvurderingTests.cs`
  *As built: written before the implementation; the red state was a compile failure, since the evaluator did not exist.*
- [X] T151 Implement the retention guard in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/OppbevaringJobb.cs`. Options `Skole:Oppbevaring:{RapporterOgVersjoner, Rapportoeridentitet, FrosnePopulasjoner, Leveransedatasett, Avvisninger, UkobledeUtdanningsposter}` have no defaults. The job refuses to start unless every value is set and `Aktivert = true`. Deletion logic is deferred until OQ-25 and the platform GDPR rule exist (mark this with a `ponytail:` comment). Unit test in `SkoleService/tests/M2LB.SkoleService.Unit/Jobber/OppbevaringJobbTests.cs`
  *As built: with `Aktivert = false` the job does not start; with `Aktivert = true` and a category missing, `StartAsync` throws and names the missing keys, so the host fails. The unit tests were written before the job (red as a compile failure).*
- [X] T152 [P] Consolidated PII tests (SC-002, SC-011) in `SkoleService/tests/M2LB.SkoleService.Integration/Personvern/PersonvernTests.cs`:
  - Run accept, hold, resolve, education unlinked→linked, and lock with files.
  - Assert no fødselsnummer, DUF-nummer or `barnEksternId` in any column of the three schemas outside the encrypted columns, in logs, published messages, problem details or Parquet bytes.
  - Reporter identity appears only in `rapport`, `avvisning`, `leveranserad` and the dataset file.
  *As built: one end-to-end test on its own period (`Laasscene.Perioder.Personvern`) covering fødselsnummer and DUF-nummer intake, hold and sweep resolution, an unlinked→linked utdanningsforhold, a rejection and the lock; `DatabaseSkanner` gained a schema list so it scans `wolverine` too. The Parquet-bytes assertion was a skipped test until T120–T122; since 2026-10-14 it is its own end-to-end test on its own period (`PersonvernFiler`): fødselsnummer, DUF-nummer, `barnEksternId` and `barnRegistreringId` are in neither file (raw bytes and decoded values) nor manifest; the reporter and BirkIDs are in the dataset only, and in no log line or message. The test passed on first run: it guards behaviour built in earlier phases. Fixture fix found on the way: M2LB.Common's `UseSerilog` makes every host replace the static `Log.Logger` and close it on dispose, so after a test with a second host (`WithWebHostBuilder`) the shared host's log lines were dropped; log-based assertions (`LaasGjentakelseTests`' commit window, the "not logged" PII checks) then failed or passed by test order. `SkoleWebApplicationFactory.Avledet(...)` restores the logger, and the five second-host tests use it.*
- [X] T153 [P] Wolverine handler-discovery regression test (Hendelse precedent) in `SkoleService/tests/M2LB.SkoleService.Integration/Messaging/HandlerOppdagelseTests.cs`: every handler type in Api and Infrastructure is discovered
  *As built: handlers are found by Wolverine's naming convention in both assemblies (six today), with a guard test that the scan is not empty. Passed on first run (regression guard).*
- [X] T154 [P] OpenAPI snapshot test for both REST contracts against `contracts/rapportinntak.openapi.yaml` and `contracts/utdanning-innmating.openapi.yaml` in `SkoleService/tests/M2LB.SkoleService.Integration/Kontrakter/OpenApiSnapshotTests.cs`. First confirm that `Microsoft.AspNetCore.OpenApi` is allowed under the governance doc; if it is not, replace this with request/response schema assertions in T058 and T074
  *As built: `Microsoft.AspNetCore.OpenApi` is on neither the Approved nor the Not-allowed list in `docs/shared-libraries-and-dependencies-governance.md` (§2.3, §2.6 require an ADR and tech-lead sign-off for a new library), so the fallback was taken: schema assertions for both contracts in `SkoleService/tests/M2LB.SkoleService.Integration/Kontrakter/KontraktSkjemaTests.cs` (every documented request property accepted; response bodies, media types and published codes exactly as documented; `postidentiteter` and `kodelister` shapes) rather than edits to T058/T074.*
- [X] T155 [P] Write the k6 load test `SkoleService/tests/load/rapportinntak.js` (SC-010): 10,000 reports for one period at a peak of 2,000/hour, asserting p95 < 5 s and that every `meldingId` got 200 or 422 (none lost). It runs against QA before go-live
  *As built: written, not run: k6 is not installed on the build machine and the script targets QA. Without `BARN_FIL` every report takes the (heavier) hold path.*
- [X] T156 [P] Keep Skoletjenesten's part of the shared operations guide `docs/operations/skolenaervaer-drift.html` in step with the implementation: Del IV, the Skoletjenesten tables in sections 18–20, runbook S1–S8, and the Skoletjenesten rows of the infrastructure order in section 22. Cover the alert rules (metric names, thresholds, Azure Monitor rule definitions, and the GL-33 hash mismatch) and the storage account requirements: private endpoint, shared key disabled, Bufdata MI *Storage Blob Data Reader* on both containers (Bufdata's reads are not read-logged, DEC-125), Key Vault keys `skole-holdt-rapport` and `skole-ukoblet-utdanningspost`, secret `skole-ukoblet-blindindeks`, and MI *Key Vault Crypto User* scoped to the keys
  *As built: Part IV updated (follow-up lists and statistics, jobs, the ADR-028 gap in `Levert`), section 18 (statistics users), section 20 (metric per alert, KQL rule pattern, GL-33 hash-mismatch counter), runbook S1 and S6, and the Skoletjenesten rows of section 22 (`db-apply`, `alerts_skole.tf`, the proxy route). The storage and Key Vault requirements were already in sections 18 and 22.*
- [X] T157 [P] Write `SkoleService/README.md` and `SkoleService/CLAUDE.md` (module overview, run, test and migration commands from quickstart.md, modelled on `TjenesteService/CLAUDE.md`)
- [X] T158 Refresh the platform docs (`docs/architecture/modules.md`, `docs/architecture/messaging.md`, `docs/architecture/containers.md`) with the `architecture-overview` skill, and module docs in `SkoleService/autodoc/` with the `maintain-docs` skill
  *As built: Skole-only additions to `docs/architecture/{modules,messaging,containers}.md` and `CHANGELOG.md`; header counts left for the parallel MeldingsutvekslerReceiver/SkoleAdapter updates. `SkoleService/autodoc/` created (README, architecture, dataflow, schema.graphql, openapi.yaml, message-bus.md, data-model.md, CHANGELOG). The proxy's local Aspire wiring for `skole-cluster` was added to `Aspire/Aspire/AppHost.cs` (T160).*
- [X] T159 Run quickstart.md validation scenarios 1–12 under Aspire and record the results in `SkoleService/specs/001-skoleservice/checklists/quickstart-validering.md` *(2026-10-13: run under Aspire on the merged step 5 branch. Scenarios 1–5, 7, 8 and 10–12 pass. Scenario 9 passes up to `Laast`; `Levert`, the files, the manifests and the event are gated by ADR-028 (T119–T122). Scenario 6 passes for what runs locally; its relation and grant cases are gated by Autorisasjon data, which the local allow-all client does not have (WireMock tests cover them). Found and fixed: the request log said 500 for handled 400/503 answers. 2026-10-14: scenario 9 re-run under Aspire after T105 and T119–T122 and passes through `Levert` (files, manifests, hashes, one `SkolenaervaersperiodeLukket`).)*
  *Open: each scenario is recorded with the tests that verify it without Aspire; the Aspire run itself is pending (sequential run by the coordinator, since other services share this machine).*

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup. **Blocks every story.**
- **US1 (Phase 3)**: After Foundational.
- **US2 (Phase 4)**: After US1, because its tests run through US1's intake pipeline.
- **US7 (Phase 5)**: After Foundational. Independent of US1 and US2, so it can run in parallel with them.
- **US6 (Phase 6)**: After Foundational (calculators). T102 needs `IUtdanningsgrunnlag` from US7 (T081, T091).
- **US3 (Phase 7)**: After US1, US2, US7 and US6. T119–T122 are also gated by the ADR-028 sign-off.
- **US4 (Phase 8)**: After US1. T131 extends the lock, so it also needs US3.
- **US5 (Phase 9)**: After US1. Deleting held rows in T138 needs US4.
- **US8 (Phase 10)**: After US2 (gradering predicate), US6 (calculators) and US3 (`dekningsstatistikk` reads frozen summaries).
- **Polish (Phase 11)**: After the stories it covers. T152 and T159 need everything.

### User Story Dependency Graph

```text
Setup → Foundational ─┬─► US1 ─► US2 ─┐
                      │               ├─► US3 ─┬─► US4 ─► US5
                      ├─► US7 ─► US6 ─┘        └─► US8
                      └─────────────────────────────────────► Polish
```

### Within Each User Story

- Tests are written first and must fail.
- Then domain types, the domain service, EF configuration, the migration (one at a time per DbContext; never in parallel), Infrastructure, and Api.
- Each story ends at its checkpoint.

### Parallel Opportunities

- Setup: T002–T008 and T010–T014 in parallel after T001.
- Foundational: the domain tasks (T015–T017, T020–T023), the clients and stubs (T031–T033), messaging types (T038–T040) and the test tasks (T036, T044, T050–T052) are all [P].
- After Foundational, **US1 and US7 can proceed in parallel** (different contexts, schemas and files). The US6 calculators (T093–T100) can also start alongside them, since they are pure domain.
- Within each story, all test tasks marked [P] can be written in parallel.
- Migrations on the same DbContext must be sequential (`SkolenaervaerDbContext`: T027, T066, T101, T113, T126, T137; `UtdanningDbContext`: T027, T084), because they share the model snapshot.

---

## Parallel Example: after Foundational

```bash
# Developer A — US1 tests:
Task: "Unit tests GyldighetskontrollTests in SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/GyldighetskontrollTests.cs"
Task: "Unit tests KvalitetskontrollTests in SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/KvalitetskontrollTests.cs"
Task: "Integration RapportinntakKontraktTests in SkoleService/tests/M2LB.SkoleService.Integration/Rapportinntak/RapportinntakKontraktTests.cs"

# Developer B — US7 tests and domain:
Task: "Unit tests UtdanningsendringTests in SkoleService/tests/M2LB.SkoleService.Unit/Utdanning/UtdanningsendringTests.cs"
Task: "Kodelister in SkoleService/src/M2LB.SkoleService.Domain/Utdanning/Kodelister.cs"
Task: "IUtdanningsgrunnlag in SkoleService/src/M2LB.SkoleService.Domain/Utdanning/IUtdanningsgrunnlag.cs"

# Developer C — US6 pure calculators:
Task: "SkolealderTests in SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/Dekning/SkolealderTests.cs"
Task: "RapportoerkategoriTests in SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/Dekning/RapportoerkategoriTests.cs"
Task: "DekningsberegnerTests in SkoleService/tests/M2LB.SkoleService.Unit/Skolenaervaer/Dekning/DekningsberegnerTests.cs"
```

---

## Implementation Strategy

### MVP = the first delivery (11 November 2026)

US1 alone cannot be shipped: Bufdata needs the dataset (US3), and the dataset needs the population (US6, US7). The smallest useful increment is:

1. Phase 1 Setup, then Phase 2 Foundational.
2. US1 + US2 (intake, gradering), with US7 in parallel.
3. US6 (calculators), then US3 (lock and delivery; T119+ after ADR-028).
4. **Stop and validate**: quickstart scenarios 1–3 and 7–11, plus T106 and T107. (Scenarios 4 and 12 need US4, 5 needs US5, and 6 needs US8.)

**Go-live also needs US4 and US5**, because until T127 and T138 replace them, the US1 interim 503 answers make Meldingsutvekslermottaket retry, and possibly pause, on every unknown child and every `Trukket`/`Slettet`/`Tillegg`. Plan US4 and US5 before 11 November. US8 (P3) can follow after the first delivery.

### Incremental delivery

1. Setup + Foundational: the service runs and is healthy.
2. US1 + US2: reports are stored correctly (demo: scenarios 1–3).
3. US7: education data flows from SkoleAdapter.
4. US6 + US3: the first dataset is produced locally (demo: scenario 9).
5. US4 + US5: no interim 503s; go-live candidate.
6. US8: caseworker follow-up.
7. Polish: alerts, the read-log, load test (QA), docs.

### Parallel team strategy

After Foundational: developer A takes US1 → US2 → US4 → US5 (intake chain), developer B takes US7, and developer C takes US6 → US3 (lock and delivery). US8 starts once US3 is done.

---

## Notes

- [P] = different files, no dependency on an incomplete task. [USn] maps a task to its story.
- Commit after each task or logical group, and stop at any checkpoint to validate.
- External dependencies (Person, Tjeneste, Revisjon) are stubbed (`PersonLookup:Mode`/`PlacementSource:Mode` = `Stub`, WireMock) until their contracts land; production cutover is a configuration change.
- Never put a national identifier, reporter identity, `barnEksternId` or BirkID in a log, event, metric tag or problem body. The PII tests (T060, T152) enforce this.

---

## Phase 12: Convergence

- [X] T161 Make the lock's commit read the Utdanning state it just linked: in `SkoleService/src/M2LB.SkoleService.Infrastructure/Laas/Periodelaaser.cs` read `HentForPeriodeAsync` as of an instant taken after `utdanning.KobleAsync`/`LagreAsync` (or pass the commit's `naa` through `IUtdanningsgrunnlag.KobleAsync` → `Utdanningskobler.KobleAsync` so link versions are stamped with it), and add a case to `SkoleService/tests/M2LB.SkoleService.Integration/Laas/LaasGjentakelseTests.cs` where the clock advances between linking and reading and the linked child is frozen `Forventet` per FR-024, FR-040c, SC-011, T115 (contradicts)
  - *As built: the commit reads `HentForPeriodeAsync` as of `tid.GetUtcNow()` taken after `KobleAsync`/`LagreAsync` (the first option); link versions keep their own timestamps. The test sets `Testklokke.Skritt`, so every clock read moves on.*
- [X] T162 Make the lock retry when Person's external-id lookup is unavailable during the gather: separate an ambiguous id (409) from unavailable in `SkoleService/src/M2LB.SkoleService.Domain/Felles/Porter/IPersonoppslag.cs` and `SkoleService/src/M2LB.SkoleService.Infrastructure/Person/PersonoppslagKlient.cs`, have the lock path of `Utdanningskobler.FinnKoblingerAsync` throw `AvhengighetUtilgjengeligException` (the hourly job stays tolerant), and add a case to `SkoleService/tests/M2LB.SkoleService.Integration/Laas/LaasGjentakelseTests.cs` per FR-024, FR-026, T115 (partial)
  - *As built: `Barneoppslag.Tvetydig` (409) is new; only `Utilgjengelig` makes the lock's `FinnKoblingerAsync` throw. A child found by external id without a classification (`UtenKlassifisering`) links, in the lock, the hourly retry and the Utdanning intake alike: linking needs only the identity, and the lock freezes the child as Kode 6/7 (FR-008a). The test makes only the external-id lookup answer 503 (`PersonWireMock.FeilEksternId`).*
- [X] T163 Let the lock complete when a held report's child is found without a classification: separate "found without classification" from "unavailable" in the single-lookup result (`IPersonoppslag.cs`, `PersonoppslagKlient.cs`; intake and sweep keep holding), freeze such a child as Kode 6/7 in `SkoleService/src/M2LB.SkoleService.Infrastructure/Laas/Periodelaaser.cs` and count it on `skole.laas.graderte_uten_klassifisering`, with a case in `SkoleService/tests/M2LB.SkoleService.Integration/Laas/GraderingVedLaasTests.cs` per FR-008a, FR-026 (contradicts)
  - *As built: `Barneoppslag.UtenKlassifisering(Personbarn)` is new. The intake still holds such a report; the hourly sweep now counts it as an attempt and goes on with the next one, where it used to stop the whole sweep as if Person were down. At lock the held report resolves to an accepted report, its child is frozen as Kode 6/7, so the report is locked `Tilbakeholdt` and published `Laast` like any other, and the gauge counts children from the held reports' lookups as well as from Person's batch. Decision (FR-008a, DEC-79 and the edge case "Person answers at lock but has no classification"): resolve and withhold, rather than reject `UKJENT_BARN`; the child is known, so it is not an unknown child.*
- [ ] T164 Evaluate the inactivity window only after a reporting period (not 1–10 August after July, nor before the first period's window) in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobs/AlertAssessment.cs` / `AlertAssessmentJob.cs`, with boundary tests in `SkoleService/tests/M2LB.SkoleService.Unit/Jobs/AlertAssessmentTests.cs` per FR-026a (b) (contradicts)
- [ ] T165 Read the current Utdanning state for the follow-up lists of an open period, also after a postponed period's planned lock time (pass the current instant from `SkoleService/src/M2LB.SkoleService.Infrastructure/FollowUp/FollowUpService.cs` through `BasisCollector.GetAsync` (`Infrastructure/Dekning/BasisCollector.cs`), or base the default in `Persistence/Utdanning/UtdanningBasis.cs` on the effective lock time), with a case in `SkoleService/tests/M2LB.SkoleService.Integration/GraphQL/FollowUpListContentTests.cs` per FR-041, FR-023a (partial)
- [X] T166 Alert when a locked period is not delivered: a period stuck in `Laast` (delivery job failing, storage down) gave no alert, since `laas_forsinket` covers only `Aapen`/`Laaser`. Add `Skole:Varsel:LeveringForsinkelse` (default 1 h) and the gauge `skole.varsel.levering_forsinket{periode}` in `SkoleService/src/M2LB.SkoleService.Infrastructure/Jobber/Varselvurdering.cs`, `VarselvurderingJobb.cs` and `Telemetri/SkoleMetrikker.cs`, with boundary tests in `SkoleService/tests/M2LB.SkoleService.Unit/Jobber/VarselvurderingTests.cs` per FR-026 (operations review)
  - *As built: `Periodetilstand` carries the recorded lock time (`LaastTidspunkt`, column `laast_tidspunkt`), and the job now also reads `Laast` periods. The gauge is 1 per period with status `Laast` and now strictly after `laast_tidspunkt + LeveringForsinkelse`, absent otherwise; it is measured from the recorded lock time, not the scheduled one, so a late lock does not alert at once. Default in `appsettings.json`. A unit test reads the gauge through a `MeterListener`.*
- [X] T167 Point the callers at the identifier URI Terraform gives Skoletjenesten's app registration: the `audience-app-reg` module creates only `api://<client-id>` (output `skole_service_api_identifier_uri`), so the tenant pattern `api://<tenant-id>/m2lb-skole-api-<env>` used by the intake's and SkoleAdapter's scope and the proxy's `skole` audience will not exist. Change `AzureAd:Schemes:skole:Audience` in `Proxy/src/M2LB.Proxy.Api/appsettings.json` and `appsettings.Development.json` to `api://<client-id>` (QA already had it), with a guard test in `Proxy/tests/M2LB.Proxy.Unit/Configuration/SkoleSchemeTests.cs`; the scopes are intake T131 and SkoleAdapter T097 (operations review)
  - *As built: placeholder `api://00000000-0000-0000-0000-000000000000`, the same client id as `ClientId` (T160's gate), set per environment from the Terraform output. The proxy already accepts `api://{ClientId}` and `ClientId` besides `Audience`, so this aligns the configuration rather than changing what passes. Skoletjenesten itself needs no change: `AzureAd` has no `Audience`, and Microsoft.Identity.Web accepts `ClientId` and `api://<ClientId>` by default. Noted in `autodoc/README.md`.*

---

## Phase 13: English names (002-translate-source-english)

**Purpose**: Everything in English (code, database, contracts, configuration, telemetry, logs and texts), with only the
core Norwegian domain nouns kept, and the module renamed from Skoletjenesten (folder `Skole/`) to SkoleService. Design
documents: `SkoleService/specs/002-translate-source-english/` (`domain-term-inventory.md`, `name-map-review.md`,
`wire-changes.md`).

- [X] T168 Glossary of kept domain nouns, pinned names and translations, approved at Gate 1 (2026-10-16) in `SkoleService/specs/002-translate-source-english/domain-term-inventory.md`
- [X] T169 Name map of every public namespace, type, enum value, property, constant and method, approved at Gate 2 (2026-10-20) in `SkoleService/specs/002-translate-source-english/name-map-review.md`
- [X] T170 Rename the module: folder `Skole/` → `SkoleService/`, `M2LB.SkoleService.slnx`, pipelines `.pipeline/skoleservice-dev.yml`/`skoleservice-qa.yml`, Aspire resource wiring; module name SkoleService in texts, envelope `kilde` and read-log `tjenestenavn`; the other modules follow (`SkoleAdapter`'s `SkoleServiceApi`, `MeldingsutvekslerReceiver`)
- [X] T171 Rename namespaces, folders, types, members and tests per the name map (`Shared/Ports`, `ReportIntake`, `Operations`, `Local`, `Held`, `Jobs`, `Locking`, `Delivery`, `FollowUp`, `Encryption`, `Audit`, `Telemetry`; `PrivacyTests`, `ArchitectureTests`, `LockScene`, …)
- [X] T172 Rename the database: English tables and columns in both schemas (`report`, `report_version`, `fravaer_line`, `rejection`, `message_intake`, `message_chain`, `held_report`, `reporting_period`, `lock_postponement`, `dekning_basis*`, `dekning_summary*`, `delivery*`, `sweep_request`, `job_lock`, `processed_event`; `utdanningsforhold[_version]`, `manglende_skoletilbud[_version]`, `unlinked_utdanning_record`, `change_intake`), stored job names in English, and each context's migrations squashed into one `Initial` (`Persistence/Skolenaervaer/Migrations/20261020073439_Initial.cs`, `Persistence/Utdanning/Migrations/20261020073449_Initial.cs`)
- [X] T173 Apply the wire changes in `wire-changes.md` §2–§3: intake path `/skolenaervaer/reports`, app roles `Skole.ReportIntake`/`Skole.UtdanningIngest`, `{"outcome":"Received"}`, problem types under `/problems/skole/`, `correlationId`/`reasonCode`, English reason codes; Utdanning `recordId` paths, `/record-ids`, `/code-lists`, English request fields and `unknown`; operations and local routes; GraphQL fields, types and error codes (snapshot and `contracts/graphql-skole.graphql` regenerated); topics `skole.reports`/`skole.periods`, subscription `skole-service`, English event types and payloads; Bufdata contract `1.0.0-draft` with English columns, values, container and manifest; configuration keys, user secret `Encryption:LocalKey`, Key Vault names, local stubs `local/person-stub.json`/`local/placements-stub.json`; metrics and log templates
- [X] T174 Pin every name another owner fixes (`wire-changes.md` §1: the XSD-shaped intake JSON and its enum values, the GL-20 envelope, Person events and DEP-01/DEP-02 fields, Revisjon's `leselogg`, Autorisasjon's registration and `Skole:<Verb>` operation names, `er_aktiv`, `barnEksternId`, `/api/skole/v1`) with explicit attributes or constants, never `ToString()`, `nameof` or a C# member name, and guard them with contract and snapshot tests (`ReportIntakeContractTests`, `UtdanningIngestContractTests`, `ContractSchemaTests`, `SchemaSnapshotTests`, `ConfigurationBindingTests`, `ParquetDeterminismTests`)
- [X] T175 Update the module's prose documentation to the English names: `README.md`, `autodoc/` (regenerated, CHANGELOG entry 2026-10-20), `docs/`, and this feature's spec, plan, research, data model, quickstart, checklists and contracts; decision logs and completed tasks keep their wording
