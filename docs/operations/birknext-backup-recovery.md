# BirkNext — backup and recovery (operations)

Where to click: **Help & Settings → Backup & Transfer → Backup & Restore** (`/workspace-transfer?area=backup`). The API is under
`api/workspace-transfer/backups` and `…/restores`. Both require the **WorkspaceTransfer** capability:
- an Entra principal holding `WorkspaceTransfer` (scope, app role or permission claim); or
- for the local single-user tool, `Authorization:LocalWorkspaceTransfer = true` in Development, from a loopback client only.

## 1. Prerequisites

| Item | How |
|---|---|
| `pg_dump` / `pg_restore`, same major version as the server or newer | `Backup:PostgresTools:Mode = Host` (tools on PATH, or `BinDirectory`). The password goes in the child's `PGPASSWORD`, never on the command line. Alternatively `Mode = ContainerExec` with `Container = <postgres container>` (and `Runtime = podman` by default), which uses the container's local socket and passes no password. |
| Backup directory | `Backup:Directory`, default `{ContentRoot}/App_Data/backups`. Created owner-only (Windows: protected DACL for the user and SYSTEM; elsewhere 0700). |
| Free space | at least the database size + 256 MB on the backup drive; checked before anything is written |
| Passphrase | 12+ characters. **Not stored anywhere.** Losing it loses the backup; nobody can recover it. |

## 2. What a backup contains

- **Every table of every schema**, through a PostgreSQL custom-format dump: `public`, `delivery_reporting`, `canonical_interpretation`, `snapshot_registry`.
- **The managed file stores:**
  - Critical E2E flows, history, modules and suggestion reviews;
  - the Test Data registry, authored values, identities, canonical fixtures, schema associations and protected defaults;
  - the Local Data Reset state (reset epoch and synthetic-key floors);
  - the Microsoft sign-in configuration and Git Sources links (no tokens).
- **Never included, with the reason recorded in the manifest:**
  - DPAPI-protected credential stores (API client secrets, Azure DevOps PAT, unattended login) — they cannot be decrypted by another user or machine;
  - local session codes;
  - browser profiles, Companion builds and pairing tokens;
  - the HTTPS inspection CA (non-exportable private key);
  - Parquet explorer sessions and Project Import staging (temporary);
  - logs;
  - `appsettings*.json` (deployment configuration);
  - **Target Environment profiles**, which live in the browser and are carried by integration packages instead.

## 3. Consistency

1. The managed files are hashed.
2. A `REPEATABLE READ, READ ONLY` transaction exports a snapshot (`pg_export_snapshot`).
3. The files are read again and hashed. If any file changed, the transaction is rolled back and the capture retried, up to 3 attempts.
4. The row counts, migration state and `pg_dump --snapshot` all come from that one snapshot.

The manifest's `consistencyMethod` is `DATABASE_EXPORTED_SNAPSHOT; FILES_UNCHANGED_ACROSS_SNAPSHOT`. If a file kept changing on every attempt, it is
`…; FILES_CHANGED_DURING_CAPTURE` and the completeness is `COMPLETE_WITH_FILE_DRIFT`; such a backup is never called point-in-time. No application-wide
write lock is held.

## 4. Inspect

Enter the passphrase and press **Verify**. The backup is decrypted into an owner-only staging folder, which is deleted afterwards, and these checks run:
- decryption and authenticity (every chunk);
- the SHA-256 of every entry;
- the manifest against the header;
- dump and managed-file presence;
- compatibility: a backup containing migrations this build does not know is refused as coming from a newer BirkNext; an older one is reported as "would migrate forward";
- consistency.

Without the passphrase, only the header claim is shown.

## 5. Isolated restore

**Restore into a new isolated database:**
1. Inspects as in §4. Any failure ends the restore and no database is created.
2. Creates a **new** database `birknext_restore_{12 hex}` on the same server. An existing name is never reused or overwritten, and the active database name is refused.
3. Runs `pg_restore --single-transaction --exit-on-error`. On failure, the database this restore just created is dropped. That is the only compensating cleanup; nothing else is ever dropped.
4. Writes the managed files to `{backups}/restores/{id}/files/…`, never to the running installation.
5. Validates independently against the manifest:
   - every table and row count;
   - EF migration state per context;
   - FK constraint count;
   - every managed file's SHA-256;
   - file → database snapshot references (the same resolved and unresolved set as at backup time).
6. Writes `report.json`. The verdict is VERIFIED, VERIFIED_WITH_WARNINGS (credentials are never restored, so this is the normal outcome) or FAILED.

**There is no cutover button.** To use a restored database:
1. Choose a maintenance window and keep the report.
2. Stop BirkNext.
3. Point `ConnectionStrings:Default` at `birknext_restore_…`.
4. Copy the restored files into `{ContentRoot}/App_Data/` (`critical-e2e/`, `test-data/`, `local-data-reset.json`, …).
5. Start BirkNext. If it is a newer build, it migrates the restored database forward.
6. Re-enter credentials and pair the Companion again.
7. Keep the old database until the restored one is confirmed.

This was verified in acceptance: an API started against `birknext_restore_f8cbfc0e2cda` plus its restored files answered exactly like the source.

## 6. Failure handling

| Situation | Result |
|---|---|
| Wrong passphrase / tampered or truncated file | `Decryption and authenticity: Fail`; nothing restored, no database created |
| Backup from a newer BirkNext | `Compatibility: Fail` before any restore |
| Not enough disk space | `INSUFFICIENT_DISK_SPACE` (HTTP 507); no file written |
| Tools missing / misconfigured | `TOOLS_UNAVAILABLE` / `TOOLS_NOT_CONFIGURED` (HTTP 503) |
| A second backup or restore while one runs | `BUSY` (HTTP 409); one operation at a time |
| `pg_dump` fails mid-way | the `.partial` file is deleted; no backup is listed |

## 7. Retention

Backups and restore folders are never deleted automatically. Remove old `.bnbackup` files and `birknext_restore_*` databases deliberately, after
confirming they are no longer needed.
