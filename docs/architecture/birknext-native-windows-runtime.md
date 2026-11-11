# BirkNext — native Windows runtime (TesterNative)

- **Status:** implemented 2026-11-11 on `008-traceability-first`.
- **Related documents:** runtime profiles `birknext-runtime-profiles.md`; installation `../deployment/birknext-windows-tester-installation.md`; upgrade and recovery `../deployment/birknext-windows-tester-upgrade-recovery.md`; SBOM `../deployment/birknext-windows-package-sbom.md`.

The Windows Tester Package runs BirkNext on a Windows PC with **no Podman, Docker, WSL2, Hyper-V, separate .NET, separate PostgreSQL or separate k6**.

It is the **same application**: same backend, frontend, API, EF Core model and migrations, test engines and evidence model. Only the runtime providers differ:

| Concern | DeveloperContainer (source checkout) | TesterNative (Windows Tester Package) |
|---|---|---|
| Entry point | `scripts/start-local.ps1` | `Start-BirkNext.exe` |
| PostgreSQL | `postgres:16` container (Podman/Docker) | Bundled PostgreSQL 16.13, one cluster per Windows user, started with the bundled `pg_ctl` |
| k6 | `docker.io/grafana/k6:1.0.0` container (`PodmanContainerExecutionRuntime`) | Bundled `k6.exe` 1.0.0 (`NativeProcessExecutionRuntime`) |
| .NET | Installed SDK/runtime | Self-contained win-x64 (backend and launcher) |
| Frontend host | `dotnet-serve` / `dotnet run` | The launcher (Kestrel, loopback) |
| Mutable data | `<backend>/App_Data`, the Podman volume | `%LOCALAPPDATA%\BirkNext\TesterNative\…` |

## 1. Package layout

```text
BirkNext-Tester/                          (extracted from BirkNext-Tester-Windows-x64-<build version>.zip; read-only, replaced by an upgrade)
├── Start-BirkNext.exe                    launcher (self-contained single file, asInvoker)
├── AIAssisted/backend/                   BirkNext.Api.exe, self-contained win-x64
├── AIAssisted/frontend/wwwroot/          Blazor WebAssembly
├── runtime/postgresql/{bin,lib,share}    PostgreSQL 16.13 (EDB Windows x64 binaries, pruned: no pgAdmin, StackBuilder or test tools)
├── runtime/k6/k6.exe                     k6 1.0.0
├── config/runtime-manifest.json          default profile, component versions, k6.exe SHA-256
├── licenses/                             PostgreSQL, k6 (AGPL + source offer), third-party notices
├── docs/                                 installation, upgrade/recovery, SBOM
├── build-manifest.json                   build identity (same schema as other BirkNext packages)
└── package-manifest.json                 every file with its SHA-256, and the 78 migration ids
```

## 2. Per-user data (never inside the package)

```text
%LOCALAPPDATA%\BirkNext\TesterNative\     owner-only ACL (this user + SYSTEM, inheritance removed)
├── database/postgres-data/               the cluster
├── config/                               cluster.json (identity), ports.json, db-credentials.dat (DPAPI, CurrentUser)
├── app-data/                             the backend's App_Data (BIRKNEXT_APP_DATA_DIR): local session, Test Data catalog, Critical E2E, …
├── logs/                                 launcher-*.log (14 kept), postgresql/ (rotated daily/20 MB, 7-day cycle), backend/, backend-console.log
├── backups/                              pg_dump archives (pre-upgrade: newest 5 kept; manual: never deleted)
├── diagnostics/                          --diagnostics archives (no credentials)
├── runtime-state/                        session, last run, stop request, initialization marker, package verification
└── temporary/
```

The folder is named after the profile, so it can never be the developer's `%LOCALAPPDATA%\BirkNext` (Git Sources settings) or the Podman volume.

## 3. Start sequence (`Start-BirkNext.exe`)

1. **Read the package.** Read `config/runtime-manifest.json`, then select the profile: the `--runtime-profile` argument, else the package default (`TesterNative`), else fail. `DeveloperContainer` is refused here.
2. **Check prerequisites.** Windows 10 1809+/11 x64, the Visual C++ 2015–2022 x64 runtime and at least 1 GB free.
3. **Verify the package.**
   - The first start of a package build hashes every file against `package-manifest.json`.
   - Later starts hash the runtime executables only.
   - A missing or changed file stops the start.
4. **Take the single-instance lock.** A per-user named mutex per data folder. A second launch reports the running URL. Stale session and stop files from a killed launcher are removed.
5. **Prepare the database.**
   - `NotInitialized`: first run (§4).
   - `InterruptedInitialization`: the partial folder is moved aside and kept, then a first run follows.
   - `UnknownFiles`, `MissingFiles` and `IncompatibleVersion` stop the start with a tester message. Nothing is deleted or re-created.
   - `Initialized`:
     - if `pg_ctl status` shows our server already running, it is reused only after identity verification;
     - otherwise it is started (a foreign listener on the port is never touched: BirkNext moves to the next free port and persists it), then identity-verified.
6. **Back up before an upgrade.** When the package build differs from the build that last opened the database, a verified `pg_dump` runs first (§6).
7. **Start the backend.**
   - `BirkNext.Api.exe` runs in a kill-on-close job object.
   - Its whole configuration is in its process environment only (§5).
   - It applies EF Core migrations for all four contexts under its advisory lock before it listens.
8. **Check health.** `GET /api/system/build-info` must answer 200 or 401 within 5 minutes. A backend that exits during startup is explained from its console output (for example "requires a newer BirkNext").
9. **Serve the frontend.**
   - Kestrel on `http://localhost:<frontend>`.
   - `appsettings.json` is served with the actual backend address.
   - Deep links fall back to `index.html`.
   - `Blazor-Environment: Development` keeps the existing local-launcher convention: an http loopback backend is accepted only in Development.
10. **Open the browser.**
    - Microsoft Edge (or the default browser) opens.
    - The connection code is shown in the launcher window, never logged.
    - Stop with Ctrl+C, by closing the window, or with `Start-BirkNext.exe --stop`. Shutdown stops the frontend, then the backend, then PostgreSQL (`pg_ctl stop -m fast`), and clears the session.

## 4. First run and database security

- **initdb:**
  - `initdb -U birknext_admin --pwfile <temp file, deleted> --auth=scram-sha-256 --encoding=UTF8 --locale-provider=icu --icu-locale=en-US --locale=C`;
  - an "initializing" marker is written before it and removed only after the identity record is written.
- **`birknext.conf`** (included from `postgresql.conf`, rewritten at every start):
  - `listen_addresses='127.0.0.1'`, the chosen port, `unix_socket_directories=''`;
  - `password_encryption=scram-sha-256`;
  - the logging collector, writing to the data folder's `logs/postgresql`.
- **`pg_hba.conf`:** only
  - `host birknext birknext_app 127.0.0.1/32 scram-sha-256`
  - `host all birknext_admin 127.0.0.1/32 scram-sha-256`
  - No `trust`, no remote addresses.
- **Roles:**
  - `birknext_app` is `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION`;
  - it owns database `birknext` and its `public` schema;
  - `CONNECT` on `postgres` is revoked from `PUBLIC`.
  - The application never uses the administrative role.
- **Passwords:** 32 characters from a no-quoting alphabet (~190 bits), stored with DPAPI for the current Windows user. They reach `pg_dump` and the backend only through the child process environment.
- **Identity:** `cluster.json` records the PostgreSQL system identifier and data directory. A running server is "ours" only when it accepts our credentials and reports both. An open port proves nothing.
- **Process:** PostgreSQL runs as this user (no Windows service, no elevation). It is started through ShellExecute with a hidden window, so it inherits no launcher handles and survives a launcher crash. Crash recovery and stale `postmaster.pid` handling are PostgreSQL's own.

Compatibility with the developer container (`postgres:16`, 16.13, UTF8, `en_US.utf8`, only `plpgsql`):
- the same major and minor version;
- UTF8;
- ICU `en-US` collation instead of glibc `en_US.utf8`. Ordering of non-ASCII text can differ slightly; no BirkNext migration depends on it.
- Advisory locks, JSONB and the 78 migrations need nothing else.

## 5. Backend configuration (TesterNative)

Set by the launcher in the backend's process environment only:

| Setting | Value |
|---|---|
| `BirkNext__RuntimeProfile` / `BirkNext__Distribution` | `TesterNative` / `WindowsTesterPackage` |
| `ConnectionStrings__Default` | `Host=127.0.0.1;Port=<db>;Database=birknext;Username=birknext_app;Password=…` (never in a file or log) |
| `ASPNETCORE_URLS`, `FRONTEND_ORIGIN` | `http://localhost:<backend>`, `http://localhost:<frontend>` |
| `PerformanceTests__Runner`, `__Native__K6Path`, `__Native__K6Sha256` | `Native`, the bundled `k6.exe`, its pinned SHA-256 |
| `Authorization__LocalTrustedExecution` | `true`. With the TesterNative profile, local trusted execution is honoured like a Development backend, under unchanged loopback-peer, loopback-Host, exact-Origin and local-session checks (decided by the product owner 2026-11-11). |
| `BIRKNEXT_APP_DATA_DIR`, `LoggingSettings__LogPath`, `GitSources__SettingsDirectory` | inside the per-user data folder |

Inherited `ConnectionStrings__*`, `BirkNext__*`, `PerformanceTests__*` and `ASPNETCORE_*` variables are removed first, so a developer's environment cannot point the tester backend at another database. With TesterNative the backend also refuses to start when a database schema is ahead of its build (no automatic downgrade).

## 6. Upgrades

- **Application upgrade:** extract the new package to a new folder and start it. The data folder is shared. A verified pre-upgrade `pg_dump` is taken before the new backend migrates.
  - Rollback to the old package is safe only while the schema did not change.
  - The old TesterNative backend refuses a newer schema with "The database requires a newer BirkNext". Recovery is to restore the pre-upgrade backup (see the upgrade guide).
- **PostgreSQL major upgrade:** never automatic. A data directory of another major version stops the start (`IncompatibleVersion`); an older binary is never started on a newer directory. The documented procedure is dump with the old package and restore with the new one.

## 7. k6 (native runner)

`NativeProcessExecutionRuntime` implements the existing `IContainerExecutionRuntime`. The one k6 provider (`K6PerformanceTestProvider`) runs on it unchanged, with the same:
- script generator;
- safety limits;
- authorization;
- summary parser;
- threshold evaluation;
- run store;
- evidence and provenance (`RuntimeId = process.native`, image digest = the executable's SHA-256).

Differences are reported, not hidden:

| Container behaviour | Native behaviour |
|---|---|
| Mounted `/birknext/in`, `/birknext/out` | Host paths of the run's own directories |
| Loopback target via `host.containers.internal` | Target addressed exactly as configured (the PC's own network, proxy and DNS) |
| Memory/CPU/PID limits | Not applied (`UnappliedLimits`; readiness says "Not applied (native runner)"); BirkNext's load limits still apply |
| Graceful stop (SIGTERM → partial summary) | Not available: cancellation terminates the process tree; no partial summary |
| CA bundle via `SSL_CERT_FILE` | **Refused**: k6 on Windows uses the Windows certificate store and ignores `SSL_CERT_FILE`. Trust the corporate CA in Windows instead |
| Image pull | Never: the executable is bundled and verified against its pinned SHA-256 before every use |
| Environment | Allow-list: SystemRoot/SystemDrive/windir, per-run TEMP/profile folders, the provider's allow-listed variables; never the backend's environment |

k6 never runs at startup. A run starts only through Performance Test Review's authorized run endpoint.
