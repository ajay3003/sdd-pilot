# Tester Package isolated restore — root cause and fix (task 13 follow-up)

## Root cause (three layers, all confirmed)

1. **Role.** In the Windows Tester Package the launcher creates the application role `birknext_app` with
   `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION` (`launcher/BirkNext.Launcher/PostgresManager.cs`). Isolated restore created its new
   database (`CREATE DATABASE birknext_restore_<12 hex>`) as that role, so readiness reported "may not create databases" and restore was BLOCKED.
2. **Authorization.** Backup & Transfer (`WorkspaceTransfer` policy) granted the local single-user installation only in the *Development*
   environment. The Tester Package backend runs in *Production* with the TesterNative profile, so the whole Backup & Transfer API was refused
   there, independent of the role.
3. **pg_hba.** The launcher's `pg_hba.conf` let `birknext_app` reach only the database `birknext`; even with a new restore database it could
   not have connected to restore into it.

## Fix (smallest secure change; the application role never gets CREATEDB or superuser)

| Layer | Change |
|---|---|
| Launcher | A separate role `birknext_restore_provisioner`: `LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT`; `GRANT birknext_app TO … WITH INHERIT FALSE, SET TRUE` (PostgreSQL ≥ 16) only so it may create a database **owned by** the application role; `CONNECT` only to `postgres`. Created/re-asserted idempotently on each start with the cluster's admin credentials (the trusted local provisioning mechanism), password kept in the DPAPI credential file; failure is logged and restore stays BLOCKED (never fatal). |
| pg_hba | `host postgres birknext_restore_provisioner 127.0.0.1/32 scram-sha-256` and `host "/^birknext_restore_[0-9a-f]{12}$" birknext_app 127.0.0.1/32 scram-sha-256` (exact pattern, never `all`). |
| Backend config | `Backup:RestoreProvisioning:ConnectionString` (launcher sets it; empty = not provisioned). |
| Backend readiness (`RestoreProvisioning.EvaluateAsync`) | The provisioning login is used only when, on the real server: same postmaster (same server), **not superuser**, CREATEDB, may SET ROLE to the application role. Otherwise BLOCKED with the exact required change. |
| Backend restore | Provisioning role creates the database `OWNER birknext_app`; ownership is then verified via the application connection before anything is written; restore, validation and compensating drop run as the application role (owner) exactly as before. The active database is never touched; no cutover; no forced disconnects. |
| Authorization | `WorkspaceTransfer` additionally grants the same loopback/Host/Origin/session-checked local capability for the TesterNative package (`Authorization:LocalWorkspaceTransfer`, set by the launcher). |

## Verification (disposable PostgreSQL 16.13 cluster mirroring the launcher; backend as the launcher starts it: Production + TesterNative)

Cluster `scratchpad/rp/data` (port 55990, loopback, SCRAM), roles and pg_hba identical to the launcher's (provisioning statements applied verbatim
from `PostgresManager.ProvisionerStatements`). No shared/user database involved.

| Phase | Result |
|---|---|
| Not provisioned (`rp/accept.cjs blocked`) | 7/7: app role NOCREATEDB; Backup & Transfer authorized for the TesterNative session; restore **BLOCKED** naming the required change; a real backup is created; restoring it is refused and creates no database. |
| Provisioned (`rp/accept.cjs ready`) | 13/13: provisioning role CREATEDB only; readiness READY via the provisioning role; backup created; restore **VERIFIED_WITH_WARNINGS** (only warning: secrets excluded by design) in ~2.8 s; new database `birknext_restore_5418e33d90cc` owned by `birknext_app` (checked independently with psql); active database same OID and row counts; restored tables = active tables (77); app role still NOCREATEDB; managed files restored to a separate directory. |
| Unit tests | Launcher: pg_hba rules (4, exact), provisioning statements narrow (no SUPERUSER/CREATEROLE/REPLICATION/BYPASSRLS), env carries the provisioning login only when provisioned, credentials round-trip. |

## Not verified

- The launcher's `EnsureRestoreProvisioner` against a real installed Tester Package (NOT RUN: no packaged installation in this task). Its SQL
  and pg_hba text are unit-tested and were applied verbatim to the disposable cluster.
- Windows Tester Native acceptance as a whole remains **NOT RUN**; developer/Podman testing does not prove it.
- Refusal code for a blocked restore is the generic `TOOLS_BLOCKED` (HTTP 503) although the message names the provisioning change (cosmetic).
