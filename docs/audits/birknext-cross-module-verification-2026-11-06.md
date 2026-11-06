# BirkNext Cross-Module Verification

**Execution baseline:** branch `008-traceability-first`, HEAD `546584f1`; **final observed HEAD:** `63ee0980`. The intervening topology worklist commit was not verified. This pass did not execute synthetic application workflows or a browser. The table is a verification disposition, not a claim of end-to-end success.

| Flow | Producer → consumer path | Current verification | Missing evidence / next check |
|---|---|---|---|
| Project import | Archive preview → staged commit → `ProjectImportService` → source snapshot → project/source selection | Import service and tests were included in current-source focused API test run; 90 tests passed across selected filters. Source implementation references: `AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs:95-143`; tests `AIAssisted/backend/BirkNext.Api.Tests/ProjectImport/ProjectImportServiceTests.cs:212-240`. | No disposable database workflow, interruption/retry, repeated UI commit, second project navigation, or snapshot-consumer verification. |
| Traceability | Source evidence → requirements/relationships → traceability UI → delivery readiness | Not exercised. Existing prior source audit is historical only. | Need actual service and UI run with versioned synthetic source; check provenance, candidate/confirmed status, and stale source invalidation. |
| Integration | Source Analysis → topology → catalog/bindings → Relationship Review → IQR | Not exercised. Integration Topology UI, CSS, tests were concurrently modified and deliberately not tested. | Rebuild after changes settle; synthetic records must retain source binding, relationship identity, state and environment. |
| Test Data / Schema | Test Data assets → XML fixture → XSD validity → business validation → journey readiness | Not exercised in running application. | Use isolated synthetic fixture and assert saved/approved distinction, XSD/business status separation, duplicate generation and revision invalidation. |
| Reviews → release | AQR/FQR/performance/M01/Skolenærvær → review history → delivery readiness/report | Not exercised. | Need synthetic result histories with run/source/snapshot/environment IDs, partial failures and stale evidence; compare UI/export state. |

## Verified narrow slice

`ProjectImportServiceTests` ran as part of a 90-test focused API partition (current test project source compiled in Release; API project had a successful Release build). This verifies only assertions selected by the test filter. It does not establish end-to-end import or cross-module evidence consumption.

## Status invariants

No runtime status was inspected in this pass. The invariants “blocked ≠ failed”, “historical passed ≠ current ready”, “candidate ≠ confirmed”, “XSD valid ≠ business valid”, and “no findings ≠ passed” remain **not tested here**. No claims about false-positive/false-negative readiness are made.

See BQA-004 and BQA-005 in the [gap register](birknext-verified-gap-register-2026-11-06.md).

## Verification completion — current source `7410e179` (2026-11-06)

The sections above are the **historical** audit at `546584f1` (final observed HEAD `63ee0980`). They are kept unchanged. This section records the
completion run against the current source. Source state verified: branch `008-traceability-first`, HEAD `7410e1793be162bd589762c9e65749d0f8621c46`; the isolated build worktree's
3,541 source files (`AIAssisted/`, `scripts/`) are byte-identical to that commit (blob hashes compared; only 7 `AIAssisted/docs/*.md` reports absent).
Uncommitted user files left untouched: `AIAssisted/backend/BirkNext.Api/appsettings.Local.json`, `AIAssisted/testdata/`. No push, no deployment,
no external system (M2LB, TT02, Altinn, MU, MM, Bufdata, Azure DevOps) contacted; browser runs abort every non-localhost request.

### Synthetic cross-module workflows

| Workflow | Verified now | Not verified (and why) |
|---|---|---|
| A Source Import → Snapshot Registry → Source Analysis | Backend import/snapshot/analysis suites (partitions 1 and 6, all passing); Snapshots browser journey 18/18 (registry KPIs incl. attention, Terraform rule, project, honest "not all stores checked"); Source Analysis page loads. | A browser import of a new archive (writes) and an interrupted import (no safe fixture). |
| B Source Evidence → Canonical Interpretation → Relationship Review | Canonical interpretation and relationship suites (partition 4/6); Topology journeys 14/14 + 11/11 (grouping by fix action/owner, resolved vs unresolved). | A new relationship decision in the browser (writes). |
| C Target Environment → Authentication → API Quality readiness | `auth-e2e` 25/25 (controlled `.invalid` detection response), `aqr-e2e` 8/8 (Attended vs None on protected targets), AQR synthetic results 46/46 (blocked ≠ failed, stored ≠ current, authentication ≠ authorization). | A real attended session against a real target. |
| D Test Data → XML Fixture → Consumer Binding → Readiness | `next58-e2e` 25/25 (eight readiness dimensions, runtime Not assessed); XML fixture journey (Configure → Review & validate → Save steps; saved and validation shown separately; no Passed); backend Test Data suites. | Save / validate-record / bind in the browser (writes); the real Tiltak 2 import → autodraft → save → reload flow (**NOT RUN**: needs the real archive and writes). |
| E Integration Catalog → IQR → Topology | `cs-catalog-e2e` 15/15, `iqr-e2e` 10/10, topology suites; backend integration suites. | Confirming a mapping (writes; never automatic). |
| F Requirements Traceability → Delivery/Release | `fqr-trace-e2e` 12/12 (no unknown-as-zero, Plan → Tasks Not assessed); Release Readiness and Delivery Overview load and pass axe. | Release freeze/compare actions (writes). |
| G M01 Static Review → Security Matrix → Runtime Not Assessed | `m01-snt-e2e` 11/11 (static and runtime separate; runtime RUNTIME UNVERIFIED without an approved context); backend SecurityClassification suites. | Runtime verification (needs an approved DEV/QA context — external). |
| H Performance Scenario → Readiness → Blocked Execution | `next58-e2e` workload/thresholds/resources/results in the browser (never saved, no run); backend PerformanceTests suites incl. readiness/credential tests. | Any load execution (by rule). |
