# Data integrity review

The application uses EF Core/Npgsql with a large primary context and multiple separately versioned schemas. There are 182 tracked migration C# files across inspected migration paths (including designer files).

## Positive evidence

- ManualTestRunService pins case title, steps, expected results, requirements, test-data names, plan revision and fingerprint when a run begins (lines 25–58). Completed/abandoned records are rejected by Open (lines 194–201).
- AppDbContext marks ManualTestRunRecord.Revision as a concurrency token (lines 1209–1220); WriteAsync maps a database concurrency conflict to 409 (ManualTestRunService lines 194–225).
- Project Import commits are serialized per staging record and repeated commits return the stored result (ProjectImportService lines 66–112; ProjectImportStagingStore).
- Reset orchestration has an explicit schema registry and reports partial completion. No reset was run.

## Risks and limits

- Multiple contexts do not share a transaction for full end-to-end workflows; provenance and reconciliation across stores remain important.
- Aggregate JSON records need compatibility fixtures when schemas evolve.
- Project-key predicates do not establish caller entitlement; see SEC-01.
- Reset behavior spans multiple stores/files and may partially complete; validate on disposable PostgreSQL with simulated failure.
- Migrations were not applied, and local databases were not accessed.

## Next verification

1. Migrate empty and prior supported schemas on disposable PostgreSQL.
2. Concurrent manual-run saves at one base revision: exactly one succeeds, the other gets 409.
3. Verify snapshot immutability and idempotent import across process restart.
4. Compare reset preview with exact deleted/preserved inventory; inject collaborator failure.
5. Read/update/export historical JSON fixtures from supported releases.
