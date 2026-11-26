# BirkNext: revisjon av felles database og flere testere

**Dato:** 2026-11-26  
**Branch:** `008-traceability-first`  
**Omfang:** Statisk gjennomgang av aktiv TesterNative-/API-implementasjon. Ingen appstart, DB-tilkobling, migrering eller dataendring ble utført.  
**Statusord:** **CONFIRMED** = direkte vist i aktiv kode/konfigurasjon; **INFERRED** = konsekvens av bekreftet kode; **NOT VERIFIED** = runtime-/IT-forhold som ikke kan fastslås fra repoet.

## 1. Kort konklusjon

**CONFIRMED:** Aktiv backend bruker PostgreSQL via Npgsql og EF Core, ikke SQLite. Den pakkede Windows TesterNative-launcheren starter sin egen PostgreSQL-prosess på loopback og lagrer clusteret under brukerens dataområde, normalt `%LOCALAPPDATA%\BirkNext\TesterNative\database\postgres-data`. API og frontend startes per launcher; API lytter bare på localhost. Backend har konfigurerbar PostgreSQL-tilkobling for andre runtime-profiler, men TesterNative overskriver den eksplisitt med loopback-verdier.

Testplanfunksjonen har reell revisjonshistorikk og optimistisk samtidighetskontroll: utkast har `Revision` som EF concurrency token, klientkall sender `BaseRevision`, stale updates returnerer 409, og hver lagring oppretter planrevisjon sammen med utkastendringen. Godkjenning/aksept føres i revisjonsinnholdet som en review-hendelse med aktør/tid; kildeendring kan føre akseptert case tilbake til review. Dette gir et brukbart grunnlag for samredigering **via én backend og én DB**, men beviser ikke at alle skriveveier er atomiske eller at klienten alltid sender riktig basisrevisjon.

**Dagens distribusjon er single-user.** API-koden stanser oppstart hvis binding utvides utover loopback: kildekommentaren sier eksplisitt at det mangler prosjektbasert autorisasjon og at nettverksdeling ville eksponere og tillate endring av alle prosjekter. Lokal cookie etablerer lokal sesjon, ikke Entra-bruker-/rollemodell for testplans. Derfor kan flere instanser ikke trygt dele én API-endpoint i dag, selv om backend generelt støtter ekstern PostgreSQL-tilkobling.

**Anbefaling:** Ikke legg PostgreSQL-datakatalogen (eller en databasefil) på `I:`/SMB. Behold DB på pålitelig lokal/serverdisk, og la flere brukere bruke én sentralt driftet BirkNext-backend over TLS med ekte identitet, server-side prosjekt-/rolleautorisasjon og klientkonfigurasjon. Før produksjonsdeling bør man også verifisere/forbedre alle write paths og testplan godkjenning/audit. For AVD er sentral backend/DB i administrert nettverk tryggest; per-user lokale instanser fungerer fortsatt kun som separate arbeidskopier.

## 2. Svar på hovedspørsmål

| Spørsmål | Funn |
|---|---|
| Q1 Database | **CONFIRMED:** PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL`), flere EF contexts/skjema på samme database. `Program.cs`, `DatabaseConnection.cs`, `.csproj`. |
| Q2 Fysisk plassering | **CONFIRMED:** TesterNative default `%LOCALAPPDATA%\BirkNext\TesterNative\database\postgres-data`; appdata, logger, backup, credentials er parallelle undermapper. API-dev fallback er `App_Data` for egne managed filer, men DB-default peker localhost. |
| Q3 Uavhengig eller delt? | **CONFIRMED:** Pakket launcher oppretter en separat data-root, databaseprosess, porter og credentials per Windows-bruker/installasjonsprofil. Ingen distribusjonsmekanisme for delt backend i TesterNative. |
| Q4 Felles planer/cases? | **PARTIALLY SUPPORTED:** En enkelt felles backend/DB ville holde plans tilgjengelige via API. Nåværende TesterNative-instanser har hver sin DB; den samme brukeren kan ikke automatisk se andres planer. |
| Q5 Samtidig redigering? | **PARTIALLY SUPPORTED:** Testplan-draft har revisionskontroll og 409 ved stale update; ikke inline merge. Det finnes ikke generell samtidig-redigeringslås. Samme plan konflikter avvises og krever reload. |
| Q6 Hvem opprettet/endret/godkjente? | **PARTIALLY SUPPORTED:** Testplanrecord har CreatedBy/At og UpdatedBy/At; case review history har actor, state, rationale, tidspunkt. Aktør kommer som `actor`-argument fra request-/lokalidentitetsflyt, ikke en komplett organisasjonsidentitet. |
| Q7 Delt Windows-drive? | **HIGH RISK / NOT RECOMMENDED:** Aktiv løsning er PostgreSQL databasecluster, ikke SQLite-fil. Launcherens `--data-dir` er en sti-overstyring, men klassen garanterer ikke SMB-semantikk; databasefiler skal ikke deles mellom prosesser/hosts. Selv én DB-prosess over SMB utsetter cluster for nettverksfilsystemets latens/avbrudd og støttes ikke som anbefalt drift i denne implementasjonen. |
| Q8 Tryggeste løsning? | Én sentral API-instans (eller skalert backend med migrasjonskoord./pooling) og sentral PostgreSQL på serverlagring, TLS, Entra/annen identitet, server-side RBAC, felles/tilgjengelig filstore og konsistente backup. Ikke DB-fildeling. |
| Q9 Eksisterende funksjoner? | Ekstern DB connection string i generell backend; PostgreSQL; migrations-lock via advisory lock; plan revisions og optimistic concurrency; snapshots og content hashes; kryptert full backup; begrenset lokal/kapabilitetsautorisasjon. |
| Q10 Endringer? | Delingsmodus og ikke-loopback deployment, autentisert flerbrukeridentitet, data-/prosjektroller, authorization på alle plan- og kildeendepunkter, konfigurerbar sentral klientendpoint, ekstern managed PostgreSQL drift, filstore, brukerpreferanse-separasjon, backup/restore/monitorering, gjennomgang av concurrency- og review-invarianter. |

## 3. Database og tilkoblingshåndtering

### Teknologi og runtime-kobling

**CONFIRMED:** `AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj` refererer `Npgsql.EntityFrameworkCore.PostgreSQL`; `Program.cs` kaller `DatabaseConnection.GetConnectionString(...)`, konfigurerer `AppDbContext` med `UseNpgsql`, og registrerer separate EF-kontekster (DeliveryReporting, CanonicalInterpretation, ScopeRegistry, TaskAlignment, SnapshotRegistry, IntegrationJourneys) mot samme connection string. Kontekstene eier separate skjema/migrasjonshistorikk, ikke separate SQLite-filer. Ingen aktiv `UseSqlite`, `Microsoft.Data.Sqlite`, `SqliteConnection` eller SQLite-provider ble funnet i appens backend-/launcher-kilde.

`DatabaseConnection.GetConnectionString` prioriterer `ConnectionStrings:Default`; ellers bygger den PostgreSQL fra `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` med localhost/5432/birkNext-defaults. Standard `appsettings.json` har localhost og PostgreSQL. ASP.NET konfigurasjon betyr miljøvariabler som `ConnectionStrings__Default` overstyrer JSON; `Program.cs` legger også eksplisitt til valgfri `appsettings.Local.json` og App_Data Microsoft sign-in JSON. `DatabaseSettings:Mode/Provider` er visnings-/systeminnstillinger; aktiv DB velges via `ConnectionStrings:Default`/POSTGRES_*.

**TesterNative-overstyring:** `Hosts.cs` setter child process environment eksplisitt: `ConnectionStrings__Default` til `127.0.0.1` og dynamisk port, `BIRKNEXT_APP_DATA_DIR` til data-root/app-data, `GitSources__SettingsDirectory` under samme mappe, `ASPNETCORE_URLS` til localhost. Den fjerner parent environment-variasjoner før start. Dermed kan pakkebruken ikke pekes til en sentral database bare ved å endre brukerens normale miljøvariabler. Generisk backend kan derimot bruke ekstern PostgreSQL hvis den startes separat og konfigurert slik.

### Fysisk lagring og per-bruker-eierskap

`AIAssisted/launcher/BirkNext.Launcher/Layout.cs`, `DataLayout.Default`, setter default root til `Environment.SpecialFolder.LocalApplicationData/BirkNext/<profile>`. Under den er `database/postgres-data`, `config`, `app-data`, `backups`, `logs`, `runtime-state`, `temporary`. `TesterLauncher.cs` støtter `--data-dir <full path>`, men gir ikke koordinering eller nettverksdatabase-garanti. `SingleInstance.cs` bruker mutex navngitt fra data-root; dette hindrer flere launchere med samme root på samme Windows-økt/host, ikke samtidige brukere som kjører separate roots.

`PostgresManager.cs` skriver PostgreSQL cluster config med `listen_addresses='127.0.0.1'`, SCRAM og roller begrenset til loopback. Credentials opprettes per root og lagres DPAPI CurrentUser-beskyttet i `config/db-credentials.dat`; bruker-/profilskifte kan derfor ikke dele samme fil uten redesign. Dette er per Windows-bruker/profil, ikke per logisk BirkNext-prosjekt.

### Migrationer og transaksjoner

`Program.cs` kjører `DatabaseMigrationLock.MigrateAsync` ved oppstart for syv contexts. `DatabaseMigrationLock.cs` bruker PostgreSQL session advisory lock rundt EF `MigrateAsync`; det koordinerer samtidige schema-migreringer på samme database. Det er **ikke** generell låsing av plan-/case-skriving. EF `SaveChangesAsync` gjør normalt sine endringer atomisk per context/transaksjon; separate eksterne API-kall, filskriving og ADO-operasjoner er ikke dermed én samlet transaksjon. Koden har konkrete transaksjoner på enkelte flows, men dette er ikke universelt.

## 4. Arkitektur slik den kjører

```text
Tester A (Windows user/profile)
  Browser -> per-user frontend host -> per-user API (localhost) -> bundled PostgreSQL (localhost)
                                         |-> per-user App_Data/files/settings

Tester B (annen Windows user/profile)
  Browser -> egen frontend -> egen API -> egen bundled PostgreSQL
                              |-> egen App_Data/files/settings

General API runtime (developer/hosted, not current TesterNative multi-user mode):
  configured frontend -> API -> configured PostgreSQL
```

Diagrammet er bekreftet av `TesterLauncher.cs`, `Hosts.cs`, `PostgresManager.cs`, `Program.cs`; ikke et hypotetisk SQLite-oppsett.

**Potensiell, men ikke klarert i dag:**

```text
Tester A --TLS/authenticated API--+
Tester B --TLS/authenticated API--+--> shared BirkNext API --> PostgreSQL on server storage
Tester C --TLS/authenticated API--+             +----------> shared managed source/file storage
```

API må først kunne lytte på nettverk bak TLS/reverse proxy og håndheve identitet og prosjekt-/rolleautorisasjon for alle delte data. Current `Program.cs` eksplisitt stopper bredere binding. Det finnes ingen klientinnstilling i TesterNative-launcheren for ekstern API-url, som bygger lokal frontend host og lokal backend.

## 5. Data inventory og delingsgrenser

| Kategori | Bekreftet lagring | Deling/personvern og risiko |
|---|---|---|
| Prosjekter, source contexts, testplans, test cases, suites, plan revisions, manual runs, ADO-mappings | PostgreSQL via `AppDbContext`; plans i `test_plan_drafts`/`test_plan_draft_revisions`, innhold JSON | Bør være delt per prosjekt/arbeidsgruppe. Nåværende plan `CreatedBy`/`UpdatedBy` identiteter er fritekst-/actor labels, ikke autoritative Windows/Entra FK. FK-/project authorization må legges til. IDs er Guid på plan/runs, men eksterne source/case keys kan være prosjekt-/kildeavhengige. |
| Godkjenning/review | Testplaninnhold JSON med `ReviewHistory`; generelt flere tabeller i AppDbContext med ApprovedBy/At | Bør deles og bindes til presis revisjon/fingerprint. Testplan review event er knyttet til case state i plan-revisjon, men separat approver-role håndheves ikke i `ReviewAsync`-signaturen (den tar `actor`); verifiser controller authorization. |
| Source analysis / imported snapshots | Flere deler i `AppDbContext`, `SnapshotRegistryDbContext`, `CanonicalInterpretationDbContext`; source analysis reader henter snapshots fra store. Repository-/Git source config settes i filsystem under App_Data/git-sources i TesterNative. | Selve metadata/snapshot IDs i DB kan deles, men kildecheckout/configuration kan være lokale og credentials sensitive. Kildedata og branch-/commit bør deles med stabile innholdshash, ikke maskinlokale stier. |
| Source archives/specification evidence | `SpecificationCatalogRecord` holder DocumentsJson/EntriesJson/EvidenceJson og ArchiveSha256 i DB; attachment records kan lagre bytes i DB (`ManualRunAttachmentRecord`). Andre provider stores kan være filer. | Identifiser/backup alle managed file stores før felles drift. Ikke alle source data behøver å ligge i filsystem; faktisk mønster varierer. |
| Personlige brukerpreferanser | Workspace records inkluderer `UserId`; AppDbContext har user-owned workspace indeks. Frontend har scoped/session stores. | Skal forbli brukeravgrenset. Ingen global user provisioning/foreign-key identitetsmodell påvist. Ikke slå sammen personlige workspace state blindt. |
| App/admin settings | JSON i API `App_Data` (`microsoft-sign-in.json`, flere system stores); TesterNative setter til per-user app-data. Dashboard-/systeminnstillinger kan være installasjonsomfattende. | Delte admin settings bør skilles fra personlige preferences; endring er autorisert i lokale system settings-policy for enkelte funksjoner, men policyen er ikke full RBAC. |
| Secrets/credentials | DB credentials DPAPI per CurrentUser i launcher config; ADO/Entra credentials har separate services/Data Protection eller Windows/local stores. PAT for Delivery Reporting er dokumentert kryptert med Data Protection i App_Data. | Må ikke kopieres ukritisk til delt DB eller SMB. Sentral Data Protection key ring/credential vault må planlegges, med minst privilegium og rotasjon. |
| Logs/backups/transfers | `logs`, `backups`, `app-data/transfer` i per-user data root; backend log folder config. | Ikke nødvendigvis synlige for andre klienter. Backups må inkludere DB + alle registrerte managed file stores; log-/backup ACLs sensibilitet. |

**IDs:** PostgreSQL sequences/identity values er unike innen shared DB; Guid IDs er probabilistically global. IDs fra separate lokal DB-kopier kinne kollidere dersom data kopieres/merges, mens sekvensnøkler garanterer kun innen DB. Workspace packages har origin instance/kind/original-id mapping og unik indeks (`AppDbContext`); dette er import-dedupe, ikke synkronisering/samarbeidsprotokoll.

## 6. Samtidig redigering, revisjon og godkjenning

### A. To brukere endrer samme test case / plan

`TestPlanSuggestionService.SaveAsync`, `ReviewAsync`, `AddManualCaseAsync`, project edits og `RestoreRevisionAsync` leser `TestPlanDraftRecord.Revision`; de fleste write requests krever `BaseRevision`. `AppDbContext` markerer `Revision` som EF `IsConcurrencyToken()`. `WriteAsync` oppdaterer plan JSON, legger til `TestPlanDraftRevisionRecord` og kaller samme `SaveChangesAsync`; `DbUpdateConcurrencyException` oversettes til 409 med beskjed om reload og at endring ikke ble lagret. Dermed er oppførselen **reject stale write**, ikke merge eller silent last-write-wins, for disse plan paths. Hver plan write lagrer hele serialiserte planobjektet, så klienten må hente fersk versjon og anvende endringen på nytt; automatisk feltmerge finnes ikke her.

Begrensning: Denne kontrollen er per plan-record. Flere forskjellige planer kan redigeres uavhengig. Alle mutasjoner må gå gjennom disse service-metodene og bruke base revision; det bør verifiseres for alle routes og frontend-kall før ekstern flerbrukerdrift. Det er ingen aktiv editor lock/presence.

### B. To oppretter testcases

Test case IDs genereres med `Guid` (`ManualCase`), og plan revisjon serialiseres til én record; samme plan-write concurrency token stopper parallelle writes hvor begge leste samme revisjon. `CreationRequestId` har dedupe; lokal `CreatingPlans` concurrent dictionary beskytter bare én backend-prosess, mens database-side idempotens sjekkes før opprettelse. Unik plan revision index hindrer duplisert `(DraftId, Revision)`. Hvis samtidige nye plan requests ikke deler creation id, to plans er mulig og forventet, ikke ID-kollisjon. Ingen eksplisitt suite-sequence allocator; ordering/suite memberships er lister i plan JSON og beskyttes via plan revision write.

### C. Suite/case forhold

Project suite collection og cases lagres i samme `ContentJson`; hver mutation bygger ny helhet og incrementerer revision. Dette hindrer delvise parent-child commit i DB for `WriteAsync`, men flere endringer i suite/case over ulike API kall kan være separate revisions. Stale-plan kontroll avverger parallelle overskrivninger; ingen separate FK constraints mellom innholdslister siden de er JSON. Feil i klientens nye state kan likevel lagres som neste revision dersom servicevalidering ikke dekker invariant.

### D. Approval race

`ReviewAsync` mahtaa test case state `Accepted` ja blokkere om approval blocker, unresolved conflict eller source changed. Review event records `actor`, state, rationale, timestamp og write lager planrevision. Endringer etterpå lager ny revision, slik at historisk godkjenningshendelse ligger i eldre immutable snapshot; det er ikke en signatur. Kilde-refresh setter endret Accepted-case til `UnderReview` og legger til systemreview-event. **Ikke bekreftet:** at enhver manuell edit etter approval alltid nullstiller Accepted eller at approver identiteten er autentisert/rollebeskyttet. Godkjenning bør eksplisitt knyttes til case content hash + plan revision og serveren må håndheve approver claim/rolle.

### E. Kildeimport mens plan redigeres

Plan har `SnapshotKey`, `DiscoveryId`, `DiscoveryBasis`; source analysis entities beholder snapshots og fingerprint/archive SHA. Discovery code avviser historisk resultat ved prepare og source refresh kan markere cases med `SourceChanged`. Dette gir provenance og gammelt grunnlag i plan, ikke sanntidsvarsling til alle åpne klienter. ETag i enkelte discovery GET gir cache revalidation, ikke lock. Kildeconfig eller repository content kan likevel ligge per-user filsystem; DB alene er ikke full source sharing.

## 7. Identitet, tilgang og audit trail

`Program.cs` konfigurerer JWT bearer (Entra authority/audience) og lokal-session-cookie. Local Tester Mode tillater QA-kapabiliteter for tilkoblet lokal tester. Lokal session er maskin-/launcher-sesjon og ikke Windows/Entra user identity. `Program.cs` og `LoopbackBinding` eksplisitt håndhever loopback-only og dokumenterer fravær av prosjektbasert authorization. Policies finnes for spesifikke risikofunksjoner (execution, integrations, system settings, workspace transfer), men dette er ikke generell `Project.Read/Write/Approve` policy.

Controllers må gjennomgås route-for-route før deling: noen endpoints er `[Authorize]`, flere prosjektfunksjoner er kun tilgjengelige i lokal trusted mode. Lokal trusted mode forutsetter loopback, Host, Origin og session code. **CONFIRMED:** aktuell sikkerhetsmodell sikter mot én trusted local user per instans. **NOT VERIFIED:** fulldekning av authorization på alle controllers ble ikke etablert som en uttømmende route-matrise i denne auditten; den eksplisitte loopback stop er uansett en blocker for felles nettverk.

Auditfelter finnes på flere data records: CreatedBy/At, UpdatedBy/At, SavedBy/At, ApprovedBy/At og review event actors. De er ikke alle samme kvalitet: actor argumenter kan være tekst fra UI/request; importdato eller eksternt ADO creator kan være provenance, ikke autentisert BirkNext-principal. Revisionsinnhold bevarer hva som ble lagret, men DB-admin kan endre det, og ingen tamper-evident chain/signatur er påvist.

Kan systemet svare “hvem endret case / når / hva?” **ofte ja** for testplan revisions, hvis actor er reell og edit havner i revision JSON. “Hvem godkjente akkurat dette innholdet?” **delvis**: case review event med actor/tid finnes, men spesifikk content hash + separat authorization/signature mangler. Endringer utenfor plan revision service eller eldre data kan ikke garanteres å ha komplett historikk.

## 8. Source/import-filer utenfor databasen

`Hosts.cs` sender Git source settings til `%BIRKNEXT_APP_DATA_DIR%\git-sources`. Flere stores/tillegg under `AppDataLocation.Root` er JSON/filer; Git clones/cache, diagnostic output, logs og backup er filesystem-based. Snapshot identity/provenance og analysert innhold finnes i DB contexts, men refererte repository source files kan kreve lokal clone eller remote repository credential. Derfor kan “DB delt, fil ikke delt” gi tilgjengelige ID-er og analysemetadata uten at en annen tester kan hente underlying repository checkout/attachment.

Backup-koden `FullBackupService.CreateAsync` tar PostgreSQL exported REPEATABLE READ snapshot og fanger managed file stores, hasher dem før/etter; den merker inconsistency hvis filer endret under captura. `ManagedFileStores` definerer included/excluded stores. Dette viser DB/file consistency er eksplisitt problemområde. Før sentralisering må source repositories være tilgjengelige via stable HTTPS/SSH/enterprise Git permissions, eller BirkNext store arkiv/blob snapshot i felles kontrollert storage, og backup bør inventere alle stores. Ikke bruk lokale `C:` path som delt source identity.

## 9. Nettverksdisk og SQLite/SMB vurdering

SQLite er ikke runtime database i undersøkt aktiv backend, derfor gjelder ikke SQLite WAL/journal-konfig direkte. Ingen `journal_mode`, `busy_timeout`, `FileShare` eller SQLite lock-setting er brukt til appens primære database.

For PostgreSQL er forskjellen viktig:

- **A. Flere prosesser på én host mot lokal PostgreSQL:** Serveren håndterer lock/transactions korrekt; dagens launcher bruker bare én server per user/root, og mutex begrenser én launch per samme root.
- **B. Flere remote clients til én backend med PostgreSQL på backendens lokale/serverdisk:** Anbefalt form. Bare serveren åpner DB. Koble brukere via API over TLS.
- **C. Flere backends åpner samme databasefiler over SMB:** Ikke gyldig PostgreSQL delingsmønster; cluster files eies av en database serverprosess. Å starte flere postgres prosesser på samme datadir feiler med server lock eller er farlig ved feil antagelser; del datadir mellom hosts er ikke støttet. Bruk PostgreSQL client/server-protokoll i stedet.
- **Én PostgreSQL serverprosess med datadir på SMB:** Repoet dokumenterer ikke støtte eller validering for dette. PostgreSQL forventer reliable filesystem locking, fsync og write ordering; SMB reconnect/cache/availability oppfører seg ikke som lokal POSIX/NTFS disk. Ikke anbefalt og ikke verifisert av launcherens database health checks. Launcher data-root override aksepterer path, men det er ikke godkjenning av network database directory.

SQLite mot SMB ville også være avvist som løsning her: ingen SQLite implementasjon finnes, og filbasert multi-host locking/reliability er filesystem-avhengig; WAL krever delt memory-mapped WAL koordinering og er ikke et trygt generelt SMB-oppsett. PostgreSQL server håndterer nettverksklienter og concurrent writes bedre.

**Risiko:** Høy for data-integritet og recovery ved DB cluster på SMB; middels-høy operasjonell risiko selv for én serverprosess ved nettverksbrudd. Nøyaktige SMB semantics i deres IT-miljø **NOT VERIFIED**. Ikke utfør prøveoppsett med produksjonsdata.

## 10. AVD og Windows deployment

TesterNative er Windows-pakket: bundled PostgreSQL binaries/tools, launcher starter/stopper postgres, backend og frontend, og bruker job object for prosesser. Default DB er lokal per-user profile. API/DB binder loopback, frontend knyttes til lokal backend; package krever ikke Docker/Podman eller admin service, men IT application-control/AV kan blokkere executable (launcher feilmelding beskriver dette).

I AVD pooled/nonpersistent hosts kan `%LOCALAPPDATA%` slettes/resettes ved logoff/reprovisioning med mindre FSLogix/profile persistence beholder det; dette er miljøspesifikt (**NOT VERIFIED**). Med separate Windows user profiles har hver tester separat database, selv på samme session host. `--data-dir` på nettverk kan omgå default path, men er ikke trygg multi-user store. Samme data-root shared across sessions kan credential DPAPI CurrentUser, mutex, PostgreSQL process ownership, ports, ACL og file locking collide/fail.

Sentral løsning krever IT for server/VM/container policy (ikke Docker på client), DNS/TLS certificate, network routing/firewall 443, service account, managed PostgreSQL (or existing platform), backup destination/key custody, monitor/patch/restore ownership, Entra registration/claims and source Git/network access. AVD clients require supported endpoint/configuration and session-scoped browser auth. RemoteApp does not change the per-user DB model.

## 11. Backup/recovery and operations

`FullBackupService` provides encrypted `.bnbackup` PostgreSQL custom-format export plus managed files, metadata/migration heads and consistency method; backup uses PostgreSQL exported REPEATABLE READ snapshot. `TesterLauncher` creates local backup before build migration and supports manual backup command; backups land in per-user `backups`. Restore is designed as isolated restore database rather than overwrite in place. `PostgresManager` has local database backup operations.

This is meaningful baseline, but no evidence this is centrally scheduled, retained off-host, monitored, or exercised for organization RPO/RTO. Existing local backup does not automatically protect a central service. Full consistency only includes configured ManagedFileStores; manifest reports excluded components and dangling snapshot references. Restoring a prior full backup necessarily rolls plans/revisions back to its snapshot time; post-backup changes are absent. Multiple testers require scheduled backups, retention, isolated restore drills, PITR/WAL archive if required, off-host encryption key recovery, and file store coverage.

Migration lock prevents migration races, not compatibility between old/new app versions concurrently connected. Central rollout should run a single controlled migration process/compatible deployment, backup before migration, and avoid mixed builds unless migrations are backward compatible.

## 12. Security and credential assessment

- TesterNative PostgreSQL credentials are random and DPAPI-protected per Windows CurrentUser; local DB server listens only on 127.0.0.1. Shared DB credentials generated per local install cannot be reused as central service credentials.
- API general connection string may contain credentials in environment/config; appsettings local default credentials are development-only and must not be deployed unchanged. Never print actual secrets.
- CORS allows configured frontend origin and credentials; API's localhost-only binding is a security boundary. Network deployment needs HTTPS termination, strict origin, cookies/SameSite/CSRF review, rate limits and headers/proxy trust controls.
- Entra bearer authentication is configured, but no project/user membership/role data model was confirmed for test planning. Authentication alone does not prevent every authenticated user from seeing/altering all projects.
- PAT/token stores and Data Protection keys must be separated by user or protected centrally; sharing App_Data may expose secrets/preferences. Avoid tokens in shared DB unless per-user secret vault design and encryption/key access are approved.
- Source code, test packages, attachments and logs may contain confidential organization data. Apply project-level ACL at API layer, storage ACL, encryption at rest, TLS in transit, backup encryption and audit access.

## 13. Architecture option decision matrix

Qualitative ratings are for the stated multi-tester goal, not a performance benchmark.

| Criterion | A. Separate local DB/tester | B. Shared DB files/DB dir on SMB | C. Shared backend + central DB | D. Central relational DB/provider | E. One backend + local-hosted DB |
|---|---|---|---|---|---|
| Existing BirkNext support | **SUPPORTED** locally | **HIGH RISK** | **PARTIALLY SUPPORTED** DB/provider; network app is blocked | **PARTIALLY SUPPORTED** PostgreSQL; SQL Server not wired | **PARTIALLY SUPPORTED** if single API can bind remote; current guard blocks |
| Implementation effort | **SUPPORTED** | **REQUIRES CHANGES**; still unsafe for shared files | **REQUIRES CHANGES** (auth, bind, client, ops) | PostgreSQL **PARTIALLY SUPPORTED**; other provider **REQUIRES CHANGES** | **REQUIRES CHANGES** and host ops |
| Concurrent edits | isolated/no sharing | **HIGH RISK** process/filesystem; no service mediation | Plan edits **PARTIALLY SUPPORTED** with revision/409 | Same as C, depending provider/migration | Same as C |
| Data integrity | **SUPPORTED** per independent workspace | **HIGH RISK** | **PARTIALLY SUPPORTED** code semantics; needs path validation | **PARTIALLY SUPPORTED** | **PARTIALLY SUPPORTED** |
| Authentication/authorization | **SUPPORTED** local trust boundary, single user | **NOT SUPPORTED** multi-user project authorization | **REQUIRES CHANGES** for project RBAC | **REQUIRES CHANGES** same app-level RBAC | **REQUIRES CHANGES** same |
| Shared test plans | **NOT SUPPORTED** automatically | technically shared state but **NOT RECOMMENDED** | **REQUIRES CHANGES** then supported | **REQUIRES CHANGES** | **REQUIRES CHANGES** |
| Shared source snapshots/files | **NOT SUPPORTED** cross install | DB refs only; files may be missing | **REQUIRES CHANGES** file/config store | **REQUIRES CHANGES** file/config store | **REQUIRES CHANGES** file/config store |
| AVD suitability | **PARTIALLY SUPPORTED** (profile persistence dependent) | **HIGH RISK** | **REQUIRES CHANGES**, strongest target after IT host | **REQUIRES CHANGES**, strong if IT-managed | **REQUIRES CHANGES**; single host availability bottleneck |
| IT infrastructure | low | file share plus complex failure handling | API host, PostgreSQL, TLS/DNS/network | DB platform plus API host | persistent VM/local disk, network access |
| Backup/recovery | local backup exists; **PARTIALLY SUPPORTED** | backup consistency risky | existing backup primitives, central ops **REQUIRES CHANGES** | platform-specific DB + managed files **REQUIRES CHANGES** | local backup supported but host failure risk |
| Performance | **NOT VERIFIED** | **NOT VERIFIED** | **NOT VERIFIED**; normal client/server pattern | **NOT VERIFIED** | **NOT VERIFIED** |
| Operational complexity | low per user, duplicate admin | high; **NOT RECOMMENDED** | moderate/high, central ops | moderate/high | moderate; single-host availability |
| Scalability | no collaboration; per-user | poor/unsafe | appropriate for team scale subject to capacity tests | appropriate relational model | limited by one host/backend |
| Decision | **SUPPORTED** single-user only | **NOT RECOMMENDED** | **RECOMMENDED TARGET after changes** | PostgreSQL recommended; SQL Server provider not present | possible interim only after security/network work |

**Option D nuance:** Database provider currently hardcoded to Npgsql across all DbContexts and migrations use PostgreSQL-specific SQL/schema/JSONB/identity features. SQL Server is not a drop-in configured alternative. PostgreSQL central is the low-change relational choice; it still requires central deployment/security changes.

## 14. Recommended target and minimum viable shared testing

Smallest safe path: one maintained BirkNext API in an IT-managed Windows/Linux host, connected to central PostgreSQL on local server storage; users in AVD browsers connect to API over TLS. Do not expose PostgreSQL port to clients; backend service account alone reaches database. Initially a single API process avoids mixed-version migration/startup complexity; PostgreSQL can be managed separately. If app is hosted behind a proxy, only trusted forwarding configuration is accepted.

Minimum required before Tester A creates a plan, Tester B opens it, Tester C adds a case, Tester A reviews, Tester Lead approves a revision and everyone sees current data:

1. Remote client endpoint configurable and supported by packaged frontend; backend no longer forced to loopback only after security gate passes.
2. Stable Entra (or approved enterprise) user identity in each API request; server-side project roles for view/edit/review/approve/delete/export/settings.
3. Audit actor derived from validated principal, never client-supplied free text; approvals bind identity, plan/case revision and content fingerprint.
4. Keep plan BaseRevision/EF concurrency token/409 behavior; verify all write endpoints do it and make all parent-child changes + revision insertion atomic. Add user-facing reload/reapply conflict workflow.
5. Explicit approval invalidation when approved content changes; enforce approver role and immutable event trail.
6. Shared/stable source snapshot and imported-file access; keep snapshot IDs/fingerprints, expose stale-source state across sessions.
7. Separate shared project/system settings from user preferences and secrets. Store credentials in per-user or managed vault, not a shared preferences row.
8. Central PostgreSQL deployment, TLS, firewall, connection pool limits, health/monitoring, migrations deployment procedure, encrypted scheduled backup with restore tests and included managed-file stores.

Current app: **cannot deliver this workflow across testers today** because each TesterNative instance has its own DB/API and API forbids network bind. Once clients are deliberately routed to one API, plan editing has a useful concurrency baseline, not full collaboration UX or complete security.

## 15. Risks and mitigations

| Risk | Evidence/impact | Mitigation |
|---|---|---|
| Separate DBs mistaken for shared data | per-user DataLayout + PostgreSQL startup | Make central endpoint and data ownership explicit; avoid manual DB copying as sync. |
| Exposing API without project authorization | Program.cs blocks non-loopback and names risk | Implement/test complete route and resource-level authorization before network binding. |
| Shared DB files over SMB | local PostgreSQL process, no remote cluster support | Central PostgreSQL client/server; keep DB cluster on reliable server-local storage. |
| Stale plan edits | optimistic concurrency exists, 409 can interrupt work | Preserve base revision; present conflict state; automate tests on separate isolated DB only in future implementation task. |
| Approval remains on changed content | approval history not cryptographic/content signature | Approval tied to content hash/revision; invalidate on mutation; authorize and audit principal. |
| Source files absent on another AVD | Git config/settings per-user app-data | Shared remote Git access or managed source artifact store; include files in backups. |
| AVD profile loss | database defaults under LocalAppData | For local-only use, IT confirms FSLogix/profile persistence; for collaboration use central service. |
| Secret exposure by shared settings | per-user DPAPI and PAT/App_Data patterns | Keep secrets personal/vault-managed; do not copy DPAPI files or share key ring without design. |
| Backup not complete/restore rollback | DB + managed files, backup snapshot timestamp | Scheduled off-host backup, retention, key escrow, restore drills, RPO/RTO, monitor dangling references. |
| Mixed version/schema startup | migration lock is mutual exclusion only | Controlled deploy, pre-upgrade backup, backward-compatible rolling migration policy. |

## 16. IT prerequisites / open questions

IT must clarify: AVD pooled vs personal hosts; FSLogix and LocalAppData persistence; approved central Windows service/VM or Linux host; PostgreSQL service offering/version/patching/HA; DNS name and TLS certificate; firewall paths from AVD subnet; Entra app registration/tenant/claims and group ownership; project role owner and joiner/leaver process; permitted source Git repositories and credential model; source artifact/file storage; backup location, encryption key custodian, retention, restore RPO/RTO; log retention/SIEM; network policy around bundled executables and reverse proxy. None are established by repository code.

Open application questions to resolve in design: Which exact manual testing controllers/persistence are in scope (current visible “Test Plan” path is `TestPlanSuggestionService`); whether all revisions/events persist actors from `HttpContext.User`; whether accepted case content edits reset review state; whether ADO transfer and source import routes get equivalent base-revision checks; what local stores are required for source reproducibility; whether multi-instance API deployments are desired.

## 17. Transaction and integrity scope notes

Confirmed atomic pattern: testplan draft JSON update + revision row in one `SaveChangesAsync` in `WriteAsync`, guarded by concurrency token. `SaveSnapshotAsync` uses unique `(DraftId, Sequence)` and `(DraftId, ContentFingerprint)`; on concurrent duplicate it rereads and returns existing identical snapshot. EF generated identity/Guid IDs prevent ordinary same-db primary-key collision.

Not generalized: project plan creation adds plan + initial revision and calls one SaveChanges; create idempotency dictionary is process-local. Source file writes and database metadata may be separate operations. Azure DevOps external calls then link/history saves cannot be one database transaction with remote ADO. Separate settings JSON stores use individual file-safe writes in some cases, but no global DB/files transaction. Suite content lives embedded in plan JSON, so no relational FK can enforce suite membership. No evidence that all imports, test case write paths, export mapping, and config operations share one transaction or use concurrency tokens; avoid claiming universal atomicity.

## 18. Evidence appendix (source paths and methods)

Paths relative to repository root `C:\Users\ajaan\source\sdd-repos\BirkNext`.

| Evidence | File / method | What it establishes |
|---|---|---|
| Database provider | `AIAssisted/backend/BirkNext.Api/BirkNext.Api.csproj`; `Program.cs`, `DatabaseConnection.GetConnectionString`, `AddDbContext` registrations | Npgsql dependency, configured PostgreSQL connection and contexts. |
| Connection precedence | `AIAssisted/backend/BirkNext.Api/Configuration/DatabaseConnection.cs`, `GetConnectionString` / `BuildConnectionString` | `ConnectionStrings:Default`, then POSTGRES_* then localhost defaults. |
| Runtime config/local-only | `AIAssisted/backend/BirkNext.Api/Program.cs`, lines around CORS, loopback validation, auth, DbContext, startup migrations | Local binding hard stop, Entra/JWT/local session auth, Npgsql contexts, migrations. |
| User paths | `AIAssisted/launcher/BirkNext.Launcher/Layout.cs`, `DataLayout`, `Default`, `Create` | LocalAppData default and database/config/appdata/backups layout; `--data-dir` explanatory error. |
| Local service/process | `AIAssisted/launcher/BirkNext.Launcher/TesterLauncher.cs`, `RunAsync`, `StartAndRunAsync`; `SingleInstance.cs`, `TryAcquire` | one launcher per data root, starts/stops own API and DB. |
| Loopback Postgres | `AIAssisted/launcher/BirkNext.Launcher/PostgresManager.cs`, config generation/start; `Hosts.cs`, `BackendProcess.Environment` | PostgreSQL listens 127.0.0.1, API DB connection set by launcher, appdata and source settings. |
| Credential scope | `AIAssisted/launcher/BirkNext.Launcher/Support.cs`, `DatabaseCredentials.Save/Load`, `AppConnectionString`, `OwnerOnly.Apply` | DPAPI CurrentUser, local loopback connection and ACL. |
| DB migration lock | `AIAssisted/backend/BirkNext.Api/Data/DatabaseMigrationLock.cs`, `MigrateAsync` | PostgreSQL advisory lock scopes migration only. |
| Data root | `AIAssisted/backend/BirkNext.Api/Services/AppDataLocation.cs`, `Root`, `SignInSettingsPath` | `BIRKNEXT_APP_DATA_DIR` override/default App_Data. |
| Plan entity/concurrency | `AIAssisted/backend/BirkNext.Api/Data/AppDbContext.cs`, `TestPlanDraftRecord` configuration and `TestPlanDraftRevisionRecord` | `Revision` concurrency token; revisions unique per draft/revision. |
| Plan create/edit/review | `AIAssisted/backend/BirkNext.Api/Services/TestPlanning/TestPlanSuggestionService.cs`, `CreateProjectAsync`, `SaveAsync`, `ReviewAsync`, `AddManualCaseAsync`, `WriteAsync`, `RestoreRevisionAsync` | base revision, actor events, stale update rejection, revision snapshots, accepted blockers. |
| Plan source refresh | `AIAssisted/backend/BirkNext.Api/Services/TestPlanning/TestPlanSuggestionService.cs`, `BuildCasesAsync`; `TestPlanSuggestionService.Sources.cs` | SourceChanged review reset and snapshot/discovery basis. |
| Test plan external integration | `AIAssisted/backend/BirkNext.Api/Services/TestPlanning/AdoTestPlanTransferService.cs` | External ADO calls and local transfer/link recording are separate domains. |
| Source metadata | `AIAssisted/backend/BirkNext.Api/Services/SourceAnalysis/ReviewSourceEvidenceProvider.cs`; source analysis db contexts | snapshot IDs, hashes and source provenance; shared source provider architecture. |
| File paths | `AIAssisted/backend/BirkNext.Api/Program.cs` `GitSources__SettingsDirectory`; `AppDataLocation.cs` | repository configuration/files are under per-user app-data in TesterNative. |
| Backup | `AIAssisted/backend/BirkNext.Api/Services/WorkspaceTransfer/FullBackupService.cs`, `CreateAsync`, `CaptureFiles`, `HashStores` | PostgreSQL repeatable-read dump + managed-file capture consistency and encryption flow. |
| Auth scope evidence | `AIAssisted/backend/BirkNext.Api/Program.cs` loopback guard and authentication policy; `AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalSessionService.cs`; controllers | local identity/capability model; no global project RBAC established. |

### Inspection limits

Static inspection only. No runtime path, installed package, AVD tenant policy, file share semantics, production database, secrets, network paths, live Azure DevOps instance, or live backup was accessed. No benchmarks or concurrency experiment was performed. The branch had pre-existing uncommitted user changes at inspection time; none were edited by this audit. Conclusions describe repository implementation on the audit date and must be revalidated against the exact deployed Tester Package build.
