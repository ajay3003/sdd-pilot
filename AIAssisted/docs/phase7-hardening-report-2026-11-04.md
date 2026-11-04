# Phase 7 hardening — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed. This pass made what exists **trustworthy to run**: it protects the person's data and fixes the causes of load-only failures. It does not add product concepts.

## PRODUCTION READINESS & TECHNICAL DEBT AUDIT MAP (what this pass covered)

| Area | Finding | Risk | Decision |
|---|---|---|---|
| Test isolation / persistence | 5 test classes built their own PostgreSQL connection strings from `ConnectionStrings__Default` (else `Database=birknext`). Today they create temporary databases, but nothing prevented a test from opening the configured (real) database. | **High** (user data) | **Fixed**: one guarded helper, `TestPostgres`, plus a source-scanning guard test |
| Migrations (`DatabaseMigrationLock`) | Processes waiting for the advisory lock used EF's default 30 s command timeout. A fresh or busy database migrating longer than 30 s made the waiting process **fail** instead of finding nothing left to apply. Seen as the "flaky" `ConcurrentMigrationsOfAFreshDatabase` test (failed at 48 s). | **High** (two API instances or parallel CI hosts on a new environment) | **Fixed** in product code: the lock wait has no command timeout; only cancellation ends it |
| Performance trial requests | Per-request timeout hard-coded at 15 s, so trial tests failed under a loaded runner (22–24 s) | Low (product), noisy CI | Timeout is now `PerformanceTestOptions.TrialRequestTimeout` (default unchanged, 15 s); tests use 60 s |
| Loopback test server | 15 s hang guard fired under full-suite starvation (21–38 s) although nothing hung | Noisy CI | Guard is now 90 s (still a hang guard, not a performance limit) |
| Schema Studio regexes | 50 ms timeouts on fixed, linear patterns could throw on a starved thread (test flake; possible on a slow device). The 50 ms guard on user XSD patterns silently returned "no meaning". | Medium (UI exception) | Fixed patterns now 1 s; user-pattern ReDoS guard 250 ms (still bounded, pattern ≤ 300 chars) |
| Activity Center test | 5 s wait for completion propagation under load | Noisy CI | 30 s wait |
| Line endings / scratch build | The scratch worktree missed committed files (sync copied only uncommitted changes) | Process | Sync now also copies recently committed files; a full content comparison found one stale file, which was corrected |
| Literal-text parameter bindings (`Param="_field"` on string parameters) | Found in Correlation Review (Explore never worked) and 2 delete flows | Medium | Fixed in #24 |

## What changed

### Real-database guard

`TestInfrastructure/TestPostgres` is the only way a test opens a real PostgreSQL connection:
- `Admin()` returns the `postgres` maintenance database, for CREATE / DROP DATABASE only.
- `Temporary(name)` accepts only `birknext_<purpose>_<12 hex>`, and **never the database named in the configured connection string**, whatever it is called (`birknext`, `birknext_ci2`, …).

All 5 real-PostgreSQL test classes use it: migration lock, artifact repair, delivery reporting ×2, external integrations.

`TestPostgresGuardTests` (11 cases):
- the accept / refuse table, including `birknext`, `birknext_ci2`, `postgres`, `birknext_test` and foreign names;
- unique temporary names;
- **a source scan**: no test file reads `ConnectionStrings__Default` or names `Database=birknext;` except the helper and the string-formatting `DatabaseConnectionTests`.

### Migration lock

The advisory-lock wait has no command timeout (the previous value is restored right after). Waiting processes now wait for the migrating one and then find nothing to apply, which is exactly what the lock is for.

### Load-sensitive timeouts

Trial request timeout as an option; loopback hang guard; Activity Center wait; Schema Studio regex timeouts. Product defaults are unchanged, except the Schema Studio regex limits (50 ms → 1 s fixed patterns, 250 ms user patterns).

## Tests

- Targeted: guard, migration lock, artifact repair, delivery reporting DB, external integrations DB, loopback server, performance request/response: **54 passed**.
- Full suites: frontend **8025 / 8025**, backend **5860 passed / 13 skipped / 0 failed**. These are the first backend runs this session without a load failure.

## Not done (Phase 7 debt, not attempted unattended)

- **Performance budgets per page / API on realistic projects:** the measurements in the #23–#26 reports cover the new endpoints only.
- **Accessibility audit across all pages:** new pages use label + icon and no colour-only state.
- **Dark-mode verification:** the app's theme toggle is not driven by the OS preference in Playwright runs.
- **Upgrade / migration rehearsal** from an older production snapshot of the schema.
- **Removal of legacy engines** (old traceability pages behind `LegacyTraceabilityNavigationEnabled`, `UnifiedImpactAnalysisPanel` vs canonical impact).
- **Operational diagnostics** for Git Sources sign-in and governance evaluation.

## Confirmations

- No test can open the configured BirkNext database; this is enforced by code and by a source-scanning test.
- The real `birknext` database was not modified; all acceptance ran on the throwaway `birknext_ci2`.
- No real external traffic; `appsettings.Local.json` not intentionally modified; user Test Data preserved; nothing pushed.
