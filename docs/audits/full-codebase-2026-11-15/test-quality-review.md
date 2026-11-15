# Test quality review

## Structure

Tracked projects include backend xUnit, frontend bUnit, Playwright browser tests and real-project acceptance. Backend tests declare both EF Core InMemory and Testcontainers PostgreSQL. The repository contains focused tests for archive validation, project import, migration integrity, authorization, manual planning/runs, traceability and release readiness.

## Confidence limitations

No tests or builds were run. Existing logs are not evidence of current HEAD behavior, especially with the dirty tree. No coverage, mutation, flake, order-dependence or quarantine analysis was performed. Test-file count does not prove assertion strength.

InMemory tests do not prove PostgreSQL index, locking, transaction, migration or provider behavior. Separate those guarantees in CI.

## Gaps to close

- Add principal-to-project negative authorization tests for every resource endpoint (SEC-01).
- Exercise concurrent manual run writes on PostgreSQL and verify exactly one stale writer receives 409.
- Ensure every DbContext has migration/reset coverage.
- Use fault injection for import partial failure, cross-schema reset failure and stale evidence in delivery.
- Browser tests should cover keyboard flow, navigation during autosave, connection recovery and deep links with isolated state.
- Record guarded/skipped external tests explicitly; do not represent them as runtime verification.
