# BirkNext — Artifact versioning & provenance: acceptance (2026-11-11)

- **Branch and commits:** `008-traceability-first`, baseline `ab49d94d`, feature commit `5044ae3b`.
- **Scope of systems:** no push, no deployment, no external system. Only disposable scratch databases were used.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## 1. Environment

| Item | Value |
|---|---|
| API | The real BirkNext API (scratch Debug build of the working tree) on :5296, content root `av/root2`, database `birknext_av2_20261111` (fresh; migrations applied by the API, including `AddExecutionManifests` and its trigger, checked by psql) |
| Frontend | Release-published frontend on :5298 |
| Browser | Microsoft Edge (Playwright); every non-localhost request aborted |
| Target | Synthetic loopback target :5097 with its own request ledger |
| Data | Created through the real APIs: OpenAPI contract `av-shop` v1 (GET /pets, GET /orders/{id}, with response schemas), generated tests, authored Test Data `av-orders` (approved, pinned), later contract v2 |
| Oracles | psql on the scratch database, SHA-256 computed by the harness from stored documents, `authored.json` and `flow-versions.json` on disk, the target ledger — never BirkNext's summaries |

## 2. Automated tests

| Suite | Result |
|---|---|
| `ExecutionManifestTests` (7) | PASS |
| `ExecutionManifestPostgresTests` (1, migrated temporary database: the trigger refuses UPDATE) | PASS |
| `CriticalE2EFlowVersionHistoryTests` (3) | PASS |
| Affected areas (ExecutionManifest, CriticalE2E, ContractTest*, IntegrationSuite*, SetupLibrary, ExecutionEvidence*, Composite*): 504 tests | PASS |
| `ExecutionResultsPageTests` (12, including 2 new) | PASS |

## 3. Browser and API acceptance

**Main phase: 27/27 PASS** (final run).

| ID | Check | Result |
|---|---|---|
| AV-00/01 | Contract v1 imported; tests generated; Test Data created, approved and pinned | PASS |
| AV-02 | The run passed against the synthetic target | PASS |
| AV-03 | The target ledger saw exactly GET /pets and GET /orders/A-42 | PASS |
| AV-04 | One manifest row for the run (psql) | PASS |
| AV-05 | Contract dependency `av-shop@v1` with the SHA-256 stored in `api_contract_versions` (psql) | PASS |
| AV-06 | Each test revision pinned with the SHA-256 of its stored revision document, computed by the harness | PASS |
| AV-07 | Test Data `av-orders@v1` with the fingerprint in `authored.json` on disk | PASS |
| AV-08 | Every dependency owner-resolved; endpoint binding included | PASS |
| AV-09 | Environment, target origin and BirkNext build recorded | PASS |
| AV-10 | Right after the run: CurrentlyApplicable | PASS |
| AV-11/12 | The run page says "Current definitions", lists every dependency, shows build and dependency fingerprint | PASS |
| AV-L1 | No page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 px | PASS |
| AV-13 | Contract v2 imported | PASS |
| AV-14 | Identical re-import of v2: Unchanged; psql still 2 versions | PASS |
| AV-15 | Re-pinning the same Test Data version: no new test revision (psql) | PASS |
| AV-16 | The first run is now **Historical** and its result is still **Passed**; the contract dependency is Superseded → `av-shop@v2` | PASS |
| AV-17 | The stored manifest is byte-identical after the change (SHA-256 of the psql row) | PASS |
| AV-18 | The stored run state is unchanged (psql) | PASS |
| AV-19 | `UPDATE execution_manifests` refused by the database: "execution manifests are immutable" | PASS |
| AV-20 | The run page says "Historical result", keeps "Passed", names the newer contract | PASS |
| AV-L2 | No horizontal scroll at the five widths (historical view) | PASS |
| AV-21 | A rerun of the same pinned definitions has the same dependency fingerprint | PASS |
| AV-22 | The rerun is Historical too: tests still bound to v1 are not evidence for v2 | PASS |
| AV-23 | Critical E2E: one record per version; identical save adds none; v1 keeps its old definition (`flow-versions.json`) | PASS |
| AV-Z | No request left localhost; no page or console error | PASS |

**After-restart phase (API restarted): 5/5 PASS.**
- AV-30: still Historical, with the Passed result.
- AV-31: the manifest is byte-identical.
- AV-32: the flow history persisted.
- AV-33: the run page still says Historical.
- AV-Z: no request left localhost; no page or console error.

**Total: 32/32 PASS in the final runs.**

### Run history

| Run | Result | What failed and why |
|---|---|---|
| main 1 (database `birknext_av_20261111`) | 23/27 | Harness: the acceptance contract declared no response schemas, so the generated ContractSchema assertion was correctly **NotAssessed** and the run was NotAssessed, not Passed (AV-02, AV-16, AV-18, AV-20 expected "Passed"). The manifest and applicability checks passed. Rerun from scratch on a new database and content root with schemas added. |
| main 2 (`birknext_av2_20261111`) | 27/27 | — |
| after-restart | 5/5 | — |

## 4. Full regression

Run sequentially with the existing memory guards; no timeout changed, no test disabled.

| Step | Result |
|---|---|
| Backend partitions 1–6 (on `5044ae3b`) | **6,455 passed, 19 skipped, 0 failed** (1031 / 1098 / 840 / 652 / 520 / 2314), including the .NET 8 parse guard in partition 6 |
| Frontend full suite | **8,583 / 8,583 passed** |
| Release publish of the frontend | PASS (used for the browser acceptance) |
| Azure DevOps pipeline (.NET 8 SDK) | NOT RUN (no pipeline triggered) |

## 5. Not covered

See the gap register:
- manifests for owners other than Execution Evidence (AV-G1);
- consumers of applicability (AV-G5, AV-G6);
- a shared version-history UI (AV-G7);
- manifests in workspace packages (AV-G8);
- the scratch databases (AV-G13): dropped after your approval.
