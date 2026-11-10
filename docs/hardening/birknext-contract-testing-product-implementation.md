# BirkNext — Contract Testing complete product hardening: implementation (2026-11-10)

Branch `008-traceability-first`. Baseline `b8df19f0`. Findings referenced as CTP-A* are in `docs/audits/birknext-contract-testing-product-audit.md`.

## Workflow after this change

Project Import (once) → **Contract Testing opens on Source Analysis** → contracts grouped by module, searchable, each with its registry state
(New / Changed → vN+1 of *key* / Up to date / Archived) → select a module or everything → **Import selected** (with "Generate tests and synthetic
test data right away", on by default) → per contract: imported or new version, tests created, test data bound / needing review / needing a value →
contract page: operations with test-data state and data-changing marks → tests with readiness summary and filters → coverage split into
definition and execution → hand-off to the Integration Suite Generator. Nothing is sent to any API until an explicit, authorised run; data-changing
calls additionally need the explicit confirmation.

## Backend (existing owners; one thin orchestration service)

| Change | Where | Finding |
|---|---|---|
| Source discovery returns **one row per source contract** (newest snapshot that holds it), with `Module`, `Protocol`, `State`, `ContractKey`/`ContractVersion` (the contract that continues it), `Snapshots`, `HasDocument`; sorted by module and file | `ContractRegistryService.SourceCandidatesAsync`, `SourceContractCandidate` | A2 |
| **Continuation in the registry**: importing a source contract without a target continues the contract that already holds a version from the same source contract (same file + type, any snapshot, same protocol, not archived) — its next version, never a second contract. An explicit name still creates a separate contract (the tester's choice). The Integration Suite Generator benefits automatically (it calls the same import) | `ImportFromSourceAsync`, `ContinuedContractAsync` | A3 |
| **Fingerprint by document**: with retained text the fingerprint is the document's SHA-256, so the same document in another snapshot/archive is the same version; earlier stored fingerprints (evidence + document) are still recognised (`SameSource`), so existing versions do not flip to Changed | `SourceFingerprint`, `LegacySourceFingerprint` | A4 |
| **Bulk import** `POST api/contract-testing/contracts/from-source/bulk` (configuration-write policy, ≤ 200 items): per item the one registry import, then (optional, default on) the one test generator and the one test-data generator; reports outcome (Imported / NewVersion / Unchanged / Failed), tests created/existing/not testable, test data per state | `ContractBulkImportService` (orchestration only), `ContractTestingController` | A5 |
| A blocked run without a run-level blocker names the calls' own reasons (e.g. the missing data-changing confirmation) instead of only "Every call was blocked before sending." | `ExecutionRunService` | acceptance CTP-D1 |
| Coverage metrics carry a **group** (Definition / Execution) and an `Assessed` rule (0/0 or nothing executed = not assessed); new definition metric **operations whose positive tests have their Test Data** (pinned, or no required value) | `ContractCoverageMetric`, `ContractTestService.CoverageAsync` | A6, A7 |

## Frontend (`ContractTesting.razor`, `IntegrationSuitesPage.razor`)

| Change | Finding |
|---|---|
| Stats strip (contracts, operations, tests, environment); import moved to the main column; Source Analysis is the **default** import source and loads without a click; File and URL remain | A1, A15 |
| Source list: summary (n new / changed / up to date), search (name, file, module), state filter, collapsible module groups with a group checkbox, per-row state pill, validation depth, "Import only this one"; sticky bulk bar "Import selected (n)" + "Generate tests and synthetic test data right away"; result table per contract with links | A2, A5 |
| Contract list: grouped REST / GraphQL, search when more than five | A15 |
| Contract header: validation depth pill (Full schema / Structural) and **Build an integration suite →** (snapshot, scope `api`, module preselected); the suite page accepts `snapshot`, `scope`, `modules` and still generates only on click | A13, A14 |
| Operations: primary **Generate tests and test data**, selection + "Generate for selected", search (> 8 operations), per-operation tests count and test-data state (bound / no input needed / needs *x* / no tests), **changes data** pill on POST/PUT/PATCH/DELETE and GraphQL mutations, accurate message (subscriptions are the untestable case) | A8 |
| Tests: counts (ready / not ready / blocked / never run), **What stops tests from running** grouped by readiness area (from backend readiness), search and filters (ready, not ready, blocked, missing test data, positive, negative, changes data, affected by a newer version); **Select every ready test leaves data-changing tests out until the confirmation is ticked**, and a warning names selected data-changing tests that would not be sent | A9, A10, safety |
| Chains: readiness per chain (all steps ready / n not ready / steps of another contract / changes data) | A11 |
| Changes: summary (changes, potentially breaking, compatible, tests affected) and "Only potentially breaking changes" | A12 |
| Coverage: two groups (Definition — what is prepared; Execution — what documented runs show) as meters; **NOT ASSESSED** instead of `0 / 0` or a 0 % with nothing executed | A6 |
| CSS: stats, filter bars, groups, meters, pills; mobile: search full width, two-column stats; no page-level horizontal scroll | A15 |

## Safety (unchanged gates, made visible)

Import, generation and test-data generation never send a request (acceptance ledger empty). Data-changing calls need the explicit confirmation in
the run request (backend gate unchanged); the UI no longer pre-selects them. Identities, credentials and Kode 6/7 classifications are never
generated (unchanged rules). Approved or tester-edited Test Data sets are never overwritten (unchanged; verified again on a real re-analysis).

## Tests

- Backend: `ContractBulkImportTests` (3): discovery once per source contract with states across three snapshots, bulk import with tests + data,
  idempotent repeat, a changed file as the next version of the same contract (registry count stays 2), single import continuation without a target,
  the same document with different analyser surroundings is Up to date. `ContractTestDataTests` extended: an unconfirmed data-changing run sends
  nothing and names the reason. Layout: wide tables contain absolutely positioned descendants (`.ct-scroll { position: relative }`). Existing `SourceContractImportTests`, `ContractTestingTests`,
  `ContractTestDataTests`, `IntegrationSuiteTests` unchanged and passing.
- Frontend: `ContractTestingPageTests` (+8): default Source Analysis import with groups, states, search and filter; bulk request and result;
  ready selection without data-changing tests; blockers by area and test search; operation marks and data state, generate selected; coverage
  groups and NOT ASSESSED; suite hand-off link and validation depth; change summary. `IntegrationSuitesPageTests` (+1): hand-off preselects and
  generates only on click.
