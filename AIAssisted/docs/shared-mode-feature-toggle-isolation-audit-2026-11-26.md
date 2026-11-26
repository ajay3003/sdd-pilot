# BirkNext – revisjon av Shared Mode, feature toggles og isolasjon

**Dato:** 2026-11-26  
**Branch:** 008-traceability-first  
**Beslutning:** **NO-GO for å aktivere Shared Mode nå. GO for å starte avgrenset design/implementering etter sikkerhets- og produkteieravklaringer.**  
**Audit:** Kun statisk lesing. Ingen app, database, migrasjon, nettverkstjeneste eller Azure DevOps-instans startet/kontaktet.  
**Statusord:** **CONFIRMED** direkte kildebelegg · **INFERRED** konsekvens av kode · **NOT VERIFIED** krever runtime-/IT-/produktavklaring.

## 01. Sammendrag

Dagens TesterNative kan bevares uten endring dersom Shared Mode først leveres som en **separat, eksplisitt delt deployment fra samme kodebase**: eget API-host, egen PostgreSQL/database, eget backend-data-/kildelager, egen autentiseringskonfigurasjon og en browser-klient som peker til Shared API over HTTPS. Den nåværende TesterNative-launcheren starter alltid sin lokale PostgreSQL, backend og frontend og overskriver frontend BackendUrl til localhost. Den kan ikke velge Shared Mode i dag.

Repoet har feature-visibility-flagg for navigasjon og runtime-profiler for DeveloperContainer/TesterNative. Ingen av delene velger Local kontra Shared dataområde. FeatureVisibility skjuler menyer, er bruker-/installasjonskonfigurasjon og dokumenterer selv at skjuling ikke er tilgangskontroll. RuntimeProfile velger driftsverktøy (native PostgreSQL/k6 versus utviklercontainer), ikke eierskap eller delt backend.

Et generisk frontend-oppsett kan allerede lese en felles BackendUrl fra appsettings.json og godtar HTTPS URL-er, mens localhost HTTP bare er tillatt for lokale klienter. Det er et nyttig koblingspunkt, men ikke tilstrekkelig: TesterNative-frontend-serveren lager appsettings-responsen dynamisk og setter alltid den lokale API-porten. Shared backend blir dessuten avvist av gjeldende API-policy hvis den lytter på ikke-loopback/proxy; dette er tilsiktet fordi prosjektbasert autorisasjon mangler.

**Autentisering uten Entra ID:** Det er arkitektonisk mulig å lage lokale BirkNext-kontoer med roller Admin, Testleder og Tester. Dagens LocalSession er derimot én tilfeldig launch-code-session med identiteten local-user, ikke kontoer eller roller. En Shared-lokal konto-modell må bli en separat, obligatorisk Shared-auth provider med sikker passordlagring, login/session lifecycle, lockout/rate limit, password reset/admin bootstrap og server-side prosjekt-/rolleautorisasjon. Den må ikke stole på Windows-profilen, klientinnsendt rolle/actor eller den delte lokale launch-koden. Om IT krever integrert bedriftsidentitet er **NOT VERIFIED**.

**Anbefalt tilnærming:** Én kildekodebase og felles domain/services, men to tydelig isolerte deploy/start paths. Første Shared-pilot bør bruke egen sentral API-host og browserbasert Shared-klient/distribusjon. Eksisterende TesterNative Local-pakke og launcher forblir urørt. Ikke bruk et UI-toggle, ikke del TesterNative data-root, ikke endre bare connection string, ikke sync/importer automatisk Local-data, ikke fallback til lokal database ved Shared-feil.

## 02. Nåværende TesterNative-arkitektur

```text
Eksisterende TesterNative (per Windows-user/profile)
Start-BirkNext.exe
  -> sjekker package/PC og tar mutex per data-root
  -> PrepareDatabase: bundled PostgreSQL + per-root credentials
  -> API child process, loopback URL + local DB connection
  -> frontend host, loopback URL og dynamisk BackendUrl=localhost:<API-port>
  -> browser + launch-code-cookie for denne installasjonen
  -> shutdown stopper frontend/API/PostgreSQL som launcheren eier

Per-user LocalAppData\BirkNext\TesterNative
  -> database/postgres-data, app-data/git-sources, logs, backups, runtime state
```

**CONFIRMED:** TesterLauncher.StartAndRunAsync kaller PrepareDatabase før API startes; API child environment setter connection string, loopback binding, app data, Git settings, LocalTesterMode=true. FrontendHost.AppSettings overskriver BackendUrl i response, uten å endre package appsettings-filen. Launcher har ikke Shared/skip-local-db option. Feil i lokal oppstart stanser og rydder opp eide prosesser; det finnes ikke en delt backend fallback.

Kildebelegg: launcher/TesterLauncher.cs (`LauncherOptions.Parse`, `DispatchAsync`, `StartAndRunAsync`, `PrepareDatabase`); launcher/Hosts.cs (`BackendProcess.Environment`, `Start`, `WaitHealthyAsync`, `FrontendHost.AppSettings`, `StartAsync`); launcher/Layout.cs (`DataLayout.Default`, paths); launcher/SingleInstance.cs.

## 03. Tidligere rapporter og verifisering

Begge tidligere rapporter eksisterer og ble lest: `shared-database-multi-tester-audit-2026-11-26.md` og `shared-backend-readiness-security-gap-audit-2026-11-26.md`.

| Påstand | Nåværende re-verifisering |
|---|---|
| PostgreSQL/Npgsql/EF Core | CONFIRMED: API project provider, Program.cs UseNpgsql registrations. |
| TesterNative per-user lokal PostgreSQL/API | CONFIRMED: launcher PrepareDatabase + bundled server + process environment. |
| Generic backend external PostgreSQL | CONFIRMED: DatabaseConnection.GetConnectionString accepts ConnectionStrings:Default or POSTGRES_*; TesterNative overrides parent settings. |
| Current plan optimistic concurrency / 409 | CONFIRMED for TestPlanDraft paths only, not application-wide. |
| No safe shared deployment today | CONFIRMED: API loopback binding and peer/proxy guard; no project ACL. |
| No remote backend URL anywhere | **Needs qualification:** frontend has generic BackendUrl configuration and HTTPS validator. TesterNative launcher always replaces it with local URL; no Shared mode in this package. A static web client can technically be configured to an HTTPS API, but API does not support shared network deployment today. |
| Current authentication | CONFIRMED: central guard authenticates local session or JWT; local session is one `local-user` principal. Not a multi-user account/role store. |

## 04. Eksisterende feature flags og konfigurasjon

### FeatureVisibility

`FeatureVisibility` in appsettings.json/appsettings.Development.json, `AdminService.BuildFeatureVisibility`, `SaveSettingsAsync`, AdminController endpoints and frontend FeatureVisibilityService drive menu/page visibility. Settings are written to backend content-root `appsettings.Local.json`; frontend receives changes and updates navigation without restart. FeatureVisibility panel says explicitly: hiding a page is not access control. Its role is presentation; routes and APIs remain available. It cannot choose database, storage, startup graph, identity provider or binding.

It is installation/backend-config scoped, not user/project scoped. Editing needs SystemConfiguration.Write policy or local trusted capability in current runtime. It persists and hot reloads because Program adds appsettings.Local.json with reloadOnChange. This flexibility is not acceptable as a Local/Shared security-mode selector.

### RuntimeProfile

`RuntimeProfileContracts.cs` enum contains only DeveloperContainer and TesterNative. It selects runtime providers/packaging (database/tool/runtime), and is set by launcher through BirkNext:RuntimeProfile. Unknown values fail closed. It is deployment configuration fixed until restart, but it does not define Local/Shared data ownership, frontend endpoint or auth profile. Do not overload this key silently; a new explicit deployment mode would need separately reviewed contract and compatibility defaults.

### Options/environment

Backend uses IConfiguration/appsettings/environment variables and IOptions for many component settings. TesterNative deliberately removes inherited `ConnectionStrings__*`, `BirkNext__*`, `ASPNETCORE_*` and other groups from child environment then sets its own values. This is a strong local isolation boundary. Generic API allows configured external DB. No existing feature-management package/system or mode rollout service was found. Build-time flags and feature-specific toggles exist but are not deployment trust controls.

**Summary:** Feature toggle system exists, but it is predominantly UI visibility/configuration. Runtime profiles control developer/native execution. Neither is evaluated as a safe Local/Shared selector before local PostgreSQL startup. UI feature flags cannot bypass authentication when backend is secure, but also cannot secure a route.

## 05. Local Mode startup and preservation

TesterNative order: parse fixed launcher arguments → derive per-user data layout → package/PC checks → mutex → PostgreSQL manager/prepare local DB → credentials/provisioning → optional pre-upgrade backup → start API with forced local DB and `ASPNETCORE_URLS=http://localhost:<port>` → wait health (200/401) → start frontend with generated BackendUrl → open browser → monitor process → stop frontend, backend and local DB. API migrates schema at startup. No database-start skip path or external API startup branch exists.

The minimum-risk preservation plan is not to retrofit the current launcher first. Keep current Local package artifacts and behavior unchanged. Add separately configured Shared host/client using the same repo/domain code; only after Shared authz and operational tests pass should a second optional launcher/package be considered. Shared rollout can be disabled/removed without touching LocalAppData or local DB.

## 06. Proposed Shared startup behavior (design)

Shared client reads trusted deployment-provisioned HTTPS BackendUrl; validates certificate through normal platform trust; obtains authenticated Shared account session; calls central API. It does **not** start local API/PostgreSQL, write to Local TesterNative database, or redirect to local source storage. Missing/invalid endpoint, certificate or authentication stops startup with a clear message. Connectivity loss gives “Kan ikke koble til felles BirkNext. Ingen lokale data er endret.” and retains unsaved local editor draft in browser memory/session; no offline queue or local fallback.

Shared backend startup validates required database connection, HTTPS/proxy/Host policy, account-auth configuration, signing/Data Protection keys, admin bootstrap and project authorization configuration before listening. It refuses startup when required Shared security settings are missing; no permissive fallback to LocalSession/LocalTesterMode. Shared DB migration ownership must be explicit rather than every app startup casually applying schema.

## 07. Deployment mode separation

| Boundary | Local current | Shared target | Cross-mode risk | Requirement |
|---|---|---|---|---|
| Database | PostgreSQL cluster under per-user data root | central PostgreSQL accessed only by API | same GUIDs/identities can exist in both; connection mistake writes wrong environment | separate DB endpoints, credentials, data roots; mode-specific clients; no automatic copy |
| Backend | per-user child API | centrally managed API service | local trusted capabilities could leak if same config | distinct startup profile/auth policy; fail closed |
| Source storage | per-user app-data/git-sources + DB metadata | central source/artifact store | DB records reference files not on other host | separate roots; immutable snapshots/hashes; central permissions |
| Plans/cases/suites | Local DB only | Shared DB only | same plan GUID may exist in each mode | never merge implicitly; clear display of mode; cache namespaced |
| Approvals | Local review state | Shared authenticated reviewer events | local actor labels not valid Shared principals | Shared-only roles and revision/content binding |
| Settings | local config + app-data; some per-user frontend state | central deployment/admin settings plus per-user preference | shared system settings could expose personal values | classify settings; keep preferences subject scoped and secrets separate |
| Credentials | local DPAPI CurrentUser and backend providers | Shared user accounts/delegated credentials or scoped service secrets | copying tokens/keys leaks credentials | new secret store/keys; never reuse local DPAPI files |
| Exports | local backend transfer directory/backup files | server-generated per-user/project artifacts | artifact URL/file name might cross environment | isolated storage namespace + auth on download + retention |
| Cache/session | local browser/backend cache and local session | separate Shared origin/API auth session | same ID collision/stale cached response | distinct origin/storage namespace/API client and invalidate on deployment change |
| Logs | per-user local folders | central backend logs/SIEM | local secrets or paths in central logs | distinct logging sinks, redaction, access policy |

## 08. Database isolation

Local default is per-user TesterNative PostgreSQL, loopback-only. Generic `DatabaseConnection` supports central host via configured full connection string/env; however Program registers all database contexts from one connection string and applies migrations for seven contexts at startup. There is no per-mode DatabaseContext selection or central migration-only mode. Changing only ConnectionStrings__Default in a TesterNative child would be overwritten; changing launcher to pass central DB would also expose schema startup migrations and local trust policy to shared DB.

Required: distinct Local and Shared deployment configuration, separate service credential, no Local user credential in Shared package; central API alone reaches PostgreSQL; isolated schemas/database/roles and backup; explicit migration deployment job; startup database validation before listen. Never mount central database directory into client/shared drive. No automatic Local database migration/import in initial release.

## 09. Source storage isolation

TesterNative config points Git sources to `data.AppData/git-sources`, while `BIRKNEXT_APP_DATA_DIR` points to user-specific app-data. This includes filesystem config/cache/repositories alongside DB-backed snapshot IDs and evidence. Snapshot registries and canonical data are database contexts; specification catalog stores some evidence JSON/metadata in DB; some artifacts/exports/managed stores are file-based; manual run attachments include bytea in DB. The storage model is mixed.

Shared DB does not make A's clone/settings/ZIP/cache visible to B. Shared mode needs central managed artifact/source store, project ACL, immutable content hash/snapshot ID independent of drive letter, and backups that include the store. Do not reuse `C:\Users\...`, `%LOCALAPPDATA%`, or mapped drives as identity. No complete exhaustive path-value sweep was performed; exact storage of every M2LB raw archive is NOT VERIFIED.

## 10. Settings, credentials, exports and logs

Deployment config belongs to IT-controlled installation/secret store. Feature visibility is installation-level appsettings.Local configuration. User layout/workspace/preferences should be subject-scoped in Shared mode. System/admin settings are central and separately authorized. PostgreSQL local credentials are DPAPI CurrentUser; they cannot serve a shared identity. ADO/Git/Azure credentials and Data Protection stores vary; personal tokens must be per account, while service credentials must be explicitly project-scoped and vaulted. Do not share App_Data wholesale or copy local credentials. Shared exports/backups/logs live on server and need project/download ACL, retention, encryption and audit.

## 11. Cache, session and mode switching

Frontend is a Blazor WASM client; API clients commonly share one configured backend base address. Browser local storage/session state and cached plan/project selections may survive sign-out and origin-local storage persists per origin. Current `FeatureVisibilityService` is singleton client state; `LastDiscoveryCache` is in-process per frontend instance. It does not namespace data by deployment mode because only one configured backend per instance is expected. Same plan GUID in Local and Shared could display stale state if a runtime endpoint is switched while cache/storage survives.

Initial Shared design should avoid runtime Local↔Shared switching in one live app instance. Use separate client origins or distinct local storage namespace and auth cookie names; display environment name and signed-in account. On endpoint/profile change, require app restart/reload, discard API caches and preserve/export editor draft explicitly. Never auto-upload Local plan on switch. Shared→Local likewise opens Local DB as it was, no download/sync. IDs remain separate even if values overlap.

## 12. Authentication boundaries — including local Shared accounts

Current backend: JWT bearer with optional Entra authority/audience plus LocalSession cookie. `LocalRequestGuardMiddleware` requires authenticated local session or bearer for /api/graphql in supported runtime. LocalSessionService uses one installation launch code to issue random in-memory sessions; authentication handler creates principal named `local-user`. This is local proof-of-possession, not username/password accounts or per-tester identity. TesterNative sets LocalTesterMode=true; loopback/local trusted execution grants local capabilities.

**Can Shared use local application accounts without Entra?** **Yes, as a future design, but it is not implemented.** It can be a Shared-only account store and password/session provider with role assignments (Admin, Test Lead, Tester), independent from LocalSession. Shared requests must be authenticated by backend using secure password hashing (Argon2id/bcrypt/PBKDF2 with current parameters), unique account/subject IDs, throttling/lockout, secure reset/invitation/bootstrap, MFA/step-up decision for Admin if required, secure cookies/CSRF or short-lived signed bearer, revocation/expiry, audit events, and protected secrets/keys. Exact algorithm/MFA/legal policy requires Security/IT decision. The backend must derive actor from validated principal and authorize every request. No Entra dependency is required by architecture, but identity lifecycle/password recovery becomes BirkNext/IT operational responsibility. Do not use shared launch code, client-provided role, Windows profile, or LocalTesterMode for Shared users.

Recommended first Shared pilot choice: if organization permits, integrate existing JWT/Entra to reduce password lifecycle burden; if user explicitly requires no Entra, build local account provider only in Shared deployment behind mandatory authorization. The two providers must be explicitly selected at startup and fail closed; Local mode continues its existing loopback session unchanged.

## 13. Authorization boundary and shared API

API currently refuses non-loopback binding (`Program.cs`/`LoopbackBinding`) and denies remote peer/proxy. Middleware comment states no project-level authorization and one local user sees all projects. Policies are capabilities (IntegrationConfigurationWrite, ActiveEventExecute, SystemConfigurationWrite, WorkspaceTransfer), not project roles. FeatureVisibility is not authz. Before a Shared listener, implement project roles and object-level checks throughout controllers, services, GraphQL resolvers, file download/upload, imports, admin, reset, ADO transfer and background work. Add server-side role model: Admin (deployment administration), Test Lead (review/approve/project administration per policy), Tester (edit/run), Reader (view). Project Owner/Product must approve exact permissions.

Security gates cannot be toggled by client feature flags. Shared config must prevent LocalSession/local trusted bypass, local reset/backup semantics inappropriate to central DB, and unprotected route exposure. Central authorization middleware can be default-deny but still needs resource-specific checks; CORS is not authorization.

## 14. Concurrency and approval gates

Test plan Draft has `Revision` concurrency token, base revision checks and 409 on stale writes; draft+revision row saved together. This is a per-aggregate baseline. It does not prove all other mutable entities, source imports, templates, settings or ADO mappings are concurrent-safe. Keep these controls mandatory in Shared mode; never make them feature-optional.

Review events currently embed state/actor/time/rationale in serialized plan history. Actor uses name or local placeholder; generic config-write permission does not create Test Lead separation. Approval is not shown bound to exact case content fingerprint and every edit path invalidating prior approval. Shared mode requires hash/revision-bound immutable approval, server role, stale-on-change semantics, and atomic approval-versus-edit check. Local mode can preserve present behavior.

## 15. Packaging/deployment options

| Option | Fit/evidence | Assessment |
|---|---|---|
| A. One TesterNative binary/runtime with Local/Shared user mode switch | Current launcher unconditionally initializes DB and starts local API; complexity expands trusted startup graph | Higher risk of accidental data target switch; not recommended for first rollout. |
| B. Separate launchers, one codebase | Existing launcher lifecycle isolated; new Shared launcher can avoid PostgresManager and local backend | Strong separation, but adding another native launcher still needs remote auth/client and packaging. Good later if desktop wrapper needed. |
| C. Same codebase, separate deployment configurations/artifacts | Frontend BackendUrl already config-driven; API generic connection config; no domain duplication needed | Recommended: keep local package unchanged, build central API + Shared static client/package with explicit configs. Server side needs substantial changes. |
| D. Existing TesterNative Local + browser-based Shared client | Frontend is WASM, BackendUrl can be HTTPS, no DB install on AVD clients | Best initial AVD pilot; separate origin/static client served internally, but API authz and HTTPS hosting still need work. |

Do not create separate backend business logic or second codebase. Separate startup composition/deployment profile from domain services. A Shared-only launcher could be an optional later wrapper; users should never enter database strings.

## 16. AVD and UX

For Local: preserve current no-admin native package, per-user LocalAppData, offline, local browser/session. For Shared: browser reaches internal HTTPS endpoint; no Docker/Podman/database/admin rights on tester workstation; central host must be persistent and not an AVD session. IT decides DNS/TLS/firewall, account lifecycle, server hosting, source storage and backup. FSLogix/pooled profile persistence is not verified and should not determine shared data durability.

Visible but simple environment bar: “Lokal – denne PC-en” versus “Felles arbeidsområde – <environment>” plus signed-in user and connection status. Never expose arbitrary URL/database fields to testers. Shared disconnection: clear Norwegian error; preserve unsaved input; block writes; no fallback/queued sync. Feature visibility remains display-only.

## 17. Configuration and feature-toggle decision

| Control | Put it here | Why |
|---|---|---|
| Local vs Shared deployment | immutable/administrator-provisioned startup config + separate deployment artifact/profile | decides DB, backend, storage, auth trust before startup; not ordinary System Settings |
| Shared UI optional feature within Shared installation | server-issued capability/feature flag after authz | presentation/rollout only; cannot grant access |
| User dashboard preferences | user-scoped preference storage | not shared across accounts |
| Who may edit/approve/export/admin | backend server authorization and project role checks | cannot be front-end toggle |
| Database URL/secrets/source root | protected service environment/secret store | not user-editable |

Evaluate toggle options:

- Build-time: useful for packaging/removing accidental controls; insufficient as sole security gate, hard rollback if build differs.
- Startup configuration: required selector, restart required, fail closed; controlled by IT/deployer, not user-editable. Local default remains existing TesterNative behavior for backwards compatibility; Shared deployment requires explicit affirmative setting and required security config.
- Server-controlled feature flag: optional for gradual UI rollout only after authz; does not switch DB or bypass policies.
- Runtime user toggle: reject for deployment mode; can create cross-mode cache/data confusion and unsafe switch.
- Separate deployment profile/artifact: best isolation, review and rollback; same codebase/domain services.
- Existing System Settings: not appropriate; writes hot-reload appsettings.Local and are user-facing, so wrong trust boundary for database/auth mode.

## 18. Deployment configuration matrix

| Category | Current Local | Desired Shared | Required change / risk |
|---|---|---|---|
| Backend | user-started localhost API | central service | new host profile; remote binding currently rejected |
| DB | bundled local PostgreSQL | central PostgreSQL API-only access | new secret config/least privilege; migrations currently startup |
| Source | per-user app-data Git/source files | central managed source store | new store/mode-specific root; DB-only sharing loses files |
| Authentication | local launch-code session; optional bearer | Shared account auth or approved Entra | new Shared auth provider; fail closed |
| Authorization | local trusted capabilities | project roles + object ACL | major API change |
| TLS | local HTTP allowed loopback | HTTPS required and certificate validated | IT cert/reverse proxy + code trust policy |
| Startup | always starts DB/API/frontend | no local DB/API; remote client only | separate startup graph |
| Failure | stop and report local startup failure | clear disconnected state, no local fallback | frontend error/reconnect UX |
| Backup | local launcher DB/files | central scheduled off-host | IT operations + managed stores |
| Settings | per-install appsettings.Local/App_Data | central admin settings; user prefs separate | storage ownership redesign |
| Credentials | DPAPI CurrentUser / local stores | per-user or scoped vault | no DPAPI file copy |
| Logs | per-user files | central audit/operations sink | actor/project/correlation and retention |
| Binding | loopback only | TLS endpoint via reviewed proxy/host | remove guard only after authz gate |
| Flags | FeatureVisibility UI | startup deployment mode + UI rollout flags | flags do not authorize |
| Update/rollback | package version + preupgrade backup | central controlled rollout and app/schema compatibility | migration owner/runbook |

## 19. Rollout and rollback

0. Keep existing Local package, config, data paths, behavior unchanged.  
1. Build Shared behind explicit disabled-by-default deployment configuration; no production data migration.  
2. Use isolated dev/test PostgreSQL, source store and local accounts; separate credentials.  
3. Pass authn/authz, concurrency, approval, file/source and rollback gates.  
4. Pilot with named authorized group in dedicated environment, separate DB/backup.  
5. Expand after acceptance and IT sign-off.

Rollback disables Shared client access/host deployment without changing local databases. Preserve central DB and backups for investigation/recovery; do not “rollback” by deleting it or running downgrade migrations. Local mode continues independently.

## 20. Regression/acceptance strategy (not executed)

Local tests: existing launcher starts; same local DB path; current source imports/plans/cases/reviews work offline; no Shared URL required; no LocalAppData path/credentials change; no central auth dependency.

Shared tests: explicit deployment config; local PostgreSQL/API are not started; valid HTTPS only; shared accounts and roles enforced server-side; project X/Y isolation; account/password/session expiry/revocation; shared source hash readable cross-AVD; 409 retains loser edits; approval invalidated after any covered content change; secrets are per-user/project; unauthorized ADO transfer denied; outage gives explicit error and never creates local plan.

Switch tests: Local plan stays local, Shared plan stays central, no automatic sync in either direction; repeated ID cannot cross cache; environment label/session clear; unsaved edits retained on disconnect; no route/feature flag can weaken auth. Tests use isolated environments and fake external providers only.

## 21. Implementation dependencies/backlog

| Priority | Work item | Existing/missing code | Dependencies / acceptance | Owner / risk if omitted |
|---|---|---|---|---|
| P0 | Define Local/Shared config contract and keep Local artifact immutable | RuntimeProfile exists but means runtime provider; no mode | Product/IT decide package, default, endpoint ownership; config validated before any DB/start | Dev + Product; accidental DB switch |
| P0 | Shared local-account auth choice and secure provider | LocalSession exists but one user; JWT optional | Security approves password/MFA/recovery/session/key policy; auth tests | Security/Identity + Dev; account takeover/unattributed actions |
| P0 | Project roles/object auth default deny | capability policies only, loopback guard | role matrix approved; every controller/service/GraphQL/file scoped test | Dev + Test Lead; cross-project access |
| P0 | Shared startup composition and fail closed | API starts migration, TesterNative always DB/API | central host config; missing config prevents listen; no LocalTesterMode | Dev + IT |
| P1 | Revision-bound approvals/audit | plan revision/review history partially exists | content fields and self-approval policy decided; concurrent tests | Dev + Test Lead |
| P1 | Whole-app concurrency inventory | tokens in selected entities | mutation endpoint matrix and race tests | Dev; lost updates |
| P1 | Shared source/file store | per-user app-data and mixed DB/files | IT storage ACL/backup; cross-session exact hash test | Dev + IT; source missing/inconsistent |
| P1 | Separate credentials/preferences | DPAPI local, mixed integration stores | vault or local account token store design | Security + IT |
| P2 | Shared frontend/Browser deployment | generic BackendUrl + HTTPS validation, local launcher overrides | internal static host, OIDC/local account UX, TLS | Dev + IT |
| P2 | Mode UX/cache namespaces | one configured backend per client | distinct origins/storage names, environment label, conflict/outage drafts | Frontend |
| P2 | Migration ownership/central backup | startup migrations + full backup service | release migration job, RPO/RTO, restore drill | Dev + IT |
| P3 | Optional Shared desktop launcher if required | current launcher local lifecycle | AVD/user preference feedback; no DB start in Shared | Dev + IT |

## 22. GO/NO-GO gates

| Gate | State today | Evidence / pass condition |
|---|---|---|
| 1 Local unchanged | **PARTIAL** | Existing path works in current package; preservation after future work not tested. Pass with regression acceptance and package unchanged. |
| 2 Separate Shared startup/config | **FAIL** | TesterNative unconditionally prepares DB and starts API; no Local/Shared mode. |
| 3 Shared cannot touch local DB paths | **FAIL** | No Shared path; generic external DB capability not enough. Pass if Shared launcher never starts local Postgres/API and has no Local credentials. |
| 4 Auth mandatory in Shared | **FAIL** | local session/JWT exist, but no shared local account identity profile. Pass if fail-closed startup. |
| 5 Server authorization | **FAIL** | no project RBAC; loopback is current boundary. |
| 6 Shared DB/source isolation | **FAIL** | mixed DB/files, per-user Git data; central source store absent. |
| 7 Concurrent editing | **PARTIAL** | TestPlanDraft optimistic concurrency only; other writes need inventory. |
| 8 Approval integrity | **FAIL** | not content-revision bound/test-lead enforced. |
| 9 No silent fallback | **PARTIAL** | current local startup fails rather than fallback; Shared mode has no behavior yet. Must test future design. |
| 10 No automatic data migration/sync | **PASS in current product** | no mode switch/data sync found; future Shared rollout must preserve this. |
| 11 AVD strategy approved | **NOT VERIFIED** | FSLogix, TLS, firewall, host not known. |
| 12 Backup/rollback | **PARTIAL** | encrypted DB+managed-file tooling exists locally; central schedule/DR not confirmed. |

**GO/NO-GO:** No-go for deployment/enabling Shared now. GO to proceed with implementation planning once Admin/Test Lead role semantics, account ownership/no-Entra preference, Local package preservation, and IT hosting/secret/storage constraints are confirmed. Security architecture can be designed now without modifying Local mode.

## 23. Risks and limits

- Generic `BackendUrl` accepts HTTPS remote URLs, but API refuses remote operation and current TesterNative rewrites to localhost.
- Configuring remote PostgreSQL alone would not share per-user files and would route startup migrations/local trusted auth into central DB.
- Feature visibility is presentation only; using it for mode/security is unsafe.
- LocalSession is convenient local proof-of-possession, not shared user accounts or roles.
- Unique IDs can overlap across independent DBs. Do not merge/import automatically; later migration needs preview, ID mapping, validation, provenance and explicit approval.
- Entra requirement, local-account policy, MFA requirement, AVD profile persistence, server platform, TLS CA, source store and backup RPO/RTO are not known.
- Working tree includes concurrent user edits to several files; audit did not edit them. Conclusions describe checked current files and must be bound to exact release commit before implementation.

## 24. Open questions for IT

Is Entra mandatory, or are BirkNext-managed accounts permitted? Who owns password reset, MFA, admin bootstrap and account offboarding? Approved Windows/Linux service host and database platform? Internal DNS/TLS certificate/reverse proxy? AVD network path/firewall? Shared Git/artifact store, ACL and retention? Secret vault/Data Protection key custody? Backup/restore RPO/RTO, monitoring/SIEM? Are separate Shared artifact/browser deployment packages allowed? No IT environment inspected.

## 25. Open questions for Test Lead / product owner

Should Admin be global or project-specific? Can a Tester approve own changes? Which fields invalidate approval? Are Test Lead approvals required for plan or case revision? Can testers see all project data or membership groups? Is local account UX acceptable without Entra (password recovery/MFA burden)? Is Local→Shared import desired later, or explicitly out of scope? What pilot scope and data classification?

## 26. Evidence appendix

| Evidence | Source and symbol | Conclusion |
|---|---|---|
| Prior audit findings | docs/shared-database-multi-tester-audit-2026-11-26.md; docs/shared-backend-readiness-security-gap-audit-2026-11-26.md | Read as prior evidence and rechecked against current code. |
| Existing feature visibility | backend/Services/AdminService.cs BuildFeatureVisibility, SaveSettingsAsync, ValidateSettingsUpdate; Controllers/AdminController.cs; frontend/Services/FeatureVisibilityService.cs; Components/Admin/FeatureVisibilityPanel.razor | UI/navigation flags, appsettings.Local persistence/reload, not authorization. |
| Runtime profile | shared/RuntimeProfileContracts.cs RuntimeProfile/RuntimeProfiles; backend/Services/SystemInfo/RuntimeEnvironmentService.cs | DeveloperContainer vs TesterNative runtime, not Local/Shared. |
| Frontend backend endpoint | frontend/Program.cs BackendUrl; frontend/Configuration/BackendUrlValidator.cs; wwwroot/appsettings.json | Generic one-backend endpoint; HTTPS or loopback HTTP. |
| TesterNative endpoint override | launcher/Hosts.cs FrontendHost.AppSettings/StartAsync | package setting dynamically served with local localhost API port. |
| TesterNative startup | launcher/TesterLauncher.cs LauncherOptions, DispatchAsync, StartAndRunAsync; PostgresManager.cs | always local DB prepare/start, API, frontend, lifecycle shutdown. |
| Child process isolation | launcher/Hosts.cs BackendProcess.Environment/Start | parent DB/runtime settings removed; local connection, loopback, app-data/Git paths set. |
| Local data paths | launcher/Layout.cs DataLayout.Default/Database/AppData; backend/Services/AppDataLocation.cs | LocalAppData per profile; app data override. |
| API network/auth gate | backend/Program.cs LoopbackBinding check/auth pipeline; Services/LocalSecurity/LocalRequestGuardMiddleware.cs | remote/proxy rejected; loopback trust boundary; project auth absent. |
| Local session identity | Services/LocalSecurity/LocalSessionService.cs; LocalSessionAuthenticationHandler.cs | one launch-code installation/session; principal local-user. |
| System config permission | Services/LocalSecurity/SystemConfigurationPermissions.cs; AdminController.SaveSystemSettings | configuration capability is not role model. |
| DB connections/migrations | Configuration/DatabaseConnection.cs; Program.cs UseNpgsql and seven DatabaseMigrationLock.MigrateAsync calls; Data/DatabaseMigrationLock.cs | generic external connection; startup migrations. |
| Plan concurrency | Data/AppDbContext.cs TestPlanDraftRecord config; Services/TestPlanning/TestPlanSuggestionService.cs WriteAsync/ReviewAsync | one aggregate has revision/409; approval not universal. |
| Source paths | launcher/Hosts.cs GitSources__SettingsDirectory; Services/AppDataLocation.cs; SourceAnalysis provider and stores | local files plus DB metadata; no cross-user guarantee. |
| Backup | Services/WorkspaceTransfer/FullBackupService.cs and ManagedFileStores.cs | database+managed-file backup capability; central ops unverified. |

No secret values were read. No code/config/database was changed. No app/test/migration was run. The sole created file is this report.
