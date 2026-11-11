# BirkNext — PostgreSQL backup tool discovery: root cause (2026-11-11)

- **Baseline:** `b9e48ad4`.
- **Observed:**
  - Backup & Transfer shows "PostgreSQL tools: Not available", "Runtime mode: Host", "Managed files: 8", and "Create full backup" is disabled.

## Root cause

PostgreSQL itself was available. The **tool provider was never configured** for either runtime profile, so the backend fell back to `Backup:PostgresTools:Mode = Host` with `pg_dump` from PATH.

| Runtime profile | How BirkNext runs | What happened | Evidence |
|---|---|---|---|
| DeveloperContainer (`scripts/start-local.ps1`) | Backend on Windows; PostgreSQL 16 in the Podman container `birknext-studio-local_postgres_1` | This Windows host has no PostgreSQL client on PATH (`which psql pg_dump`: none). `Process.Start("pg_dump")` fails, so `VersionAsync` returns null and the UI shows "Not available". The launcher set `BirkNext__RuntimeProfile` and the connection string, but never `Backup__PostgresTools__*` | `start-local.ps1` (before), `PostgresTools.cs` |
| TesterNative (Tester Package launcher) | Bundled native PostgreSQL 16.13 in `runtime/postgresql/bin` | The launcher passed the k6 path (`PerformanceTests__Native__K6Path`) but **not** the PostgreSQL bin directory, so the in-app backup had the same failure. `pg_dump.exe` ships in the package and runs (`pg_dump (PostgreSQL) 16.13`, verified) | `launcher/BirkNext.Launcher/Hosts.cs` (before) |

Further confirmed defects in the same area:

1. **"Runtime mode: Host" was the tool mode, not the runtime profile.** The UI showed `Backup:PostgresTools:Mode`, which looks like a runtime profile.
2. **"Available" meant "`--version` started".**
   - No server-version comparison.
   - In container mode, no check that the container is the server BirkNext is connected to (a second compose project, `aiassisted_postgres_1`, can also bind 5432 on this machine).
3. **Isolated restore in the Tester Package** cannot create its database. The launcher's app role is `NOCREATEDB` (`PostgresManager.cs:134`). This was not reported anywhere before a restore attempt.
4. **The disabled button gave no reason.**

## Fix: one resolver, providers from trusted configuration

`PostgresToolReadiness` (new; it builds on the existing `PostgresTools`, so there is no second tool engine) produces a `BackupReadiness` result:

1. **Provider.** It comes only from configuration set by the launcher that owns the database:
   - Tester Package: `Backup__PostgresTools__Mode=Host` and `BinDirectory=<package>\runtime\postgresql\bin`.
   - Developer launcher: `Mode=ContainerExec` with the container found by compose labels (`com.docker.compose.project=<project>`, `com.docker.compose.service=postgres`). Exactly one match is used, or nothing.
   - Otherwise, an explicit `BinDirectory`, then PATH.
   - Nothing comes from imported project data, and no container is chosen by name similarity.
2. **Execution.** `pg_dump --version` and `pg_restore --version` must succeed. A file that merely exists is never "Ready".
3. **Identity (container).** `psql` inside the container must return the same `pg_postmaster_start_time()/database OID` as BirkNext's own connection. Otherwise the result is Blocked. This needs no privileged function.
4. **Compatibility.**
   - pg_dump major ≥ server major; otherwise Incompatible, and backup is refused.
   - pg_restore major ≥ pg_dump major; otherwise Incompatible.
   - When a restore starts, the backup manifest's pg_dump version is checked against pg_restore. A mismatch fails the restore before any database is created.
5. **Restore destination.** The role needs `rolcreatedb` (or superuser); otherwise isolated restore is Blocked and backup still works.
6. **Statuses.** Ready / Blocked / Unavailable / Incompatible / NotChecked, per item and overall for backup and for restore. The first blocker is given as text, together with the search locations and the runtime profile. No secret is included.

**Where the result is used:**
- `GET api/workspace-transfer/backups/status` returns it as `readiness`.
- `CreateAsync` refuses with `TOOLS_UNAVAILABLE` / `TOOLS_INCOMPATIBLE` / `TOOLS_BLOCKED` (503).
- `RestoreIsolatedAsync` refuses before touching anything.

## Verification

| Provider | Real execution | Result |
|---|---|---|
| Configured directory, empty | `pg_dump` cannot start | Unavailable; "PostgreSQL backup tools could not be found", lists the directory (browser BT-01…03) |
| Configured directory with the Tester Package's bundled Windows binaries (PostgreSQL 16.13) | Native `pg_dump.exe`/`pg_restore.exe` against the PostgreSQL 16.13 server | Ready after **Retry detection** (BT-04); real backup and isolated restore VERIFIED (BT-10…25) |
| Container exec, `birknext-studio-local_postgres_1` | `podman exec` pg_dump/pg_restore/psql | Identity Ready ("runs the database server BirkNext is connected to"); real backup and restore VERIFIED (BT-10C…25C) |
| Unknown container / missing configuration / unreachable database | — | Not Ready / Blocked / Unavailable (unit tests) |
| Tester Package launcher environment | — | `Backup__PostgresTools__BinDirectory` = `<package>\runtime\postgresql\bin`, `Mode=Host` (`TesterLauncherTests`) |
| Developer launcher function `Get-ComposePostgresContainer` | Extracted from `start-local.ps1` and run read-only | Returns `podman` / `birknext-studio-local_postgres_1` (the stopped `aiassisted_postgres_1` is not matched). The script parses with 0 errors. The full launcher was **not run**: it would start the services on 5000/5173 |
