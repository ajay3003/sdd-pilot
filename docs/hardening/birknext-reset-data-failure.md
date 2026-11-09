# BirkNext — Reset Data failure: root cause, fix and regression (2026-11-09)

Branch `008-traceability-first`. Reported: the Reset Data dialog showed **"Reset failed. See server logs for details."**

## What Reset Data does

System Settings → Maintenance → **Reset data…** (`DataResetPanel`) → `POST api/admin/data-reset` (`AdminController.DataReset`) →
`DataResetService` → `LocalDataResetCoordinator` → `AdminService.ResetLocalDatabaseAsync`. Guards: admin enabled, typed/acknowledged
confirmation, `AdminSettings:AllowLocalDatabaseReset`, Local database on a loopback host, non-production backend, one reset at a time, no
running CDC/performance run. It deletes the rows of **every application table of this build's EF model** in one transaction (installation
settings, schema and migration history are preserved), advances the reset epoch, then clears BirkNext's own out-of-database state.
It never touches external systems (Azure, Altinn, TT02, MU, MM, Service Bus, Bufdata, Git) — it resets local BirkNext data, not target-system state.

## Evidence (local log, `logs/backend-serilog-20261109.log`)

| Item | Value |
|---|---|
| Request | `POST http://localhost:5000/api/admin/data-reset` (scope *FullIncludingProtected*, after `GET …/plan`) at 20:38:54 |
| Response | **500**, `DataResetResult` "Reset failed. See server logs for details." |
| Trace | ASP.NET request trace in the same log (no correlation id was returned to the UI) |
| Exception | `Npgsql.PostgresException 42P01: relation "integration_suites" does not exist` at `AdminService.ResetLocalDatabaseAsync` (`DELETE FROM …`) |
| Effect | transaction not committed → rolled back; nothing was deleted |

## Root cause

The running API (started 19:27 by `scripts/start-local.ps1`, `dotnet run --no-build` of the working tree) was built while the in-progress
integration-suite change had already added `integration_suites` to the EF model but before its migration existed. Startup applied every
migration *it knew*, so the database lacked a table the model had. The reset iterates the **model's** tables and issued `DELETE FROM
integration_suites`, which failed; the generic catch replaced the cause with "See server logs".

The defect is general: whenever the build's model is ahead of the database (pending migration, or a model table without a migration), the
reset fails half way with an opaque message. The UI also turned any non-JSON response or network error into `ex.Message` text.

## Fix

- **Schema check before deleting** (`AdminService.SchemaMismatchAsync`): pending migrations, and (PostgreSQL) model tables missing in the
  database. A mismatch is refused before anything is deleted: *"Reset blocked: the database schema does not match this BirkNext build
  (1 table(s) missing in the database: integration_suites). Nothing was deleted. Restart BirkNext so it applies its database migrations
  (or rebuild it if the build is newer than its migrations), then reset again."* → status **Blocked**, HTTP **409**.
- **Classified failure with a reference**: any other failure rolls back and returns *"Reset failed: the database operation could not be
  completed. Nothing was deleted (the change was rolled back). Reference: 1a2b3c4d (server log)."*; the reference is logged with the
  exception. No SQL state, provider name or exception text reaches the UI.
- **Frontend client**: 401/403 → *"Reset blocked: insufficient permissions. Nothing was changed."*; non-result responses → HTTP status
  only; unreachable backend → no exception text.
- **Dialog**: shows *Requested* (scope), the message, *Changed* (nothing / see domains) and *Next* (restart, wait, ask an administrator,
  retry with the reference). Success is still shown only for Completed/PartiallyCompleted.

Unchanged: authorization, guards, confirmation, transaction scope, reset semantics, preserved data, epoch and audit logging. No foreign key
or permission is bypassed; nothing falls back to a destructive alternative.

## Tests

| Area | Test | Result |
|---|---|---|
| Red first | `AdminServiceResetDatabaseTests` against the unfixed code: missing table, pending migration and mid-reset failure returned "Reset failed. See server logs for details." | **3 FAIL** (defect reproduced), 2 pass |
| Backend (temporary PostgreSQL DB per test, dropped after) | successful reset; model table missing → blocked, nothing deleted; pending migration → blocked; database failure mid-reset (trigger) → full rollback + reference, no internals; concurrent resets → one at a time | **5/5 PASS** |
| Backend related | `AdminServiceReset*`, `LocalDataReset*`, `DataReset*`, `IntegrationSuiteTests` | **44/44 PASS** |
| Frontend | `DataResetPanelTests` (+ schema-blocked dialog, 401/403, 500 pass-through, non-JSON 502, unreachable backend), `SystemSettings*`, `IntegrationSuitesPageTests` | **90/90 PASS** |
| Browser (Edge headless, isolated API :5098 on scratch DB `birknext_rst_20261109`, published web :5198, external requests blocked) | complete schema → reset completes, lands on Dashboard, rows gone, POST 200 (RB-01…03); defect reproduced in the scratch DB (table dropped) → "Reset blocked", schema + table named without internals, Requested/Changed/Next shown, nothing deleted, POST **409**, fits 390 px (RB-10…15) | **PASS** |
| Browser console | only entry: the browser's own network log of the intended 409 response; no page error, no external request | PASS (expected network log) |

Not applicable / not covered: *protected / referenced records* and *cross-project isolation* — Reset Data is installation-wide by design
(no per-project or per-record scope; protected Test Data handling is unchanged and covered by the existing Test Data reset tests).

## What the tester does now

The running local API must be restarted (or rebuilt and restarted) so it applies the `AddIntegrationSuites` migration; then Reset Data works.
Until then, the fixed build reports the mismatch instead of failing. The live stack was not restarted or reset by this work.
