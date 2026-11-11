# Backup & Transfer — runtime strategy (2026-11-11)

**One engine, one resolver.**
- Full backup and isolated restore remain `FullBackupService`: `pg_dump --format=custom` at an exported snapshot, plus the managed file stores, written into an AES-256-GCM package.
- Which `pg_dump`/`pg_restore` runs, and whether it may run, is decided only by `PostgresToolReadiness` on top of `PostgresTools`.
- The runtime profile (`BirkNext:RuntimeProfile`, set by the launcher) is never inferred from installed tools and never switched at runtime.

| Profile | Launcher | Tool provider | Database server | Restore destination |
|---|---|---|---|---|
| **DeveloperContainer** | `scripts/start-local.ps1` | `ContainerExec`: `podman exec -i <compose postgres container> pg_dump …`. The container is selected by compose labels, then **verified** to be BirkNext's server | Podman PostgreSQL 16 (compose) | New `birknext_restore_<12 hex>` on the same server |
| **TesterNative** | `Start-BirkNext.exe` | `Host` with `BinDirectory` = the package's own `runtime/postgresql/bin` (same 16.13 build as the managed server; never PATH) | Bundled PostgreSQL, launcher-managed, own port | **Blocked**: the app role is `NOCREATEDB` by design (least privilege). Backups work; restoring needs a role that may create a database, or a launcher-mediated restore (gap BT-G3) |
| Other (unconfigured source run) | — | `Host`: `BinDirectory` if configured, else PATH | Whatever the connection string names | As the role allows |

## Compatibility rules

| Rule | Effect |
|---|---|
| pg_dump major < server major | Incompatible: backup refused |
| pg_restore major < pg_dump major (readiness) | Incompatible: restore not ready |
| pg_restore major < the backup's recorded pg_dump major | That restore fails before any database is created |
| Server and connection unreachable | Database Unavailable: nothing runs |

## Consistency model (unchanged, documented)

- **Database:**
  - One PostgreSQL database; every EF Core DbContext lives in it.
  - One `pg_dump` at a `pg_export_snapshot()` taken in a READ ONLY, REPEATABLE READ transaction. Row counts and migration heads are read in the same snapshot.
- **Managed files:**
  - Hashed before the snapshot and re-read after it.
  - If they changed, retry (up to 3 attempts). Otherwise the backup is labelled `FILES_CHANGED_DURING_CAPTURE` and not called point-in-time.
- **No global atomicity claim:** across other stores (browser localStorage profiles, secrets) the backup is not atomic. Those stores are excluded by design.

## Not in a backup (verified with canaries, see the acceptance report)

- API service credentials
- Azure DevOps PAT
- Unattended login
- Local session launch code
- Browser profiles and pairing tokens
- HTTPS inspection CA
- Target Environment profiles (browser)
- Parquet session copies
- Import staging
- Logs
- `appsettings*.json`
