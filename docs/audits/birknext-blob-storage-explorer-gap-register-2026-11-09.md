# BirkNext — Azure Blob Storage Explorer — gap register (2026-11-09)

Branch `008-traceability-first` (local only). Initial SHA `5e30f803`; final SHA = the commit carrying this file.
Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED.

**Overall: PARTIAL.** The read-only explorer, previews, expectation checks, execution correlation and observations are verified end to end
against a synthetic local store. Nothing was verified against a real Azure storage account.

| Item | Status | Note |
|---|---|---|
| Browse, properties, previews, checks, correlation, observations (synthetic store) | PASS | acceptance 44/44 |
| Read-only API surface (no write operation exists) | PASS | `BlobExplorerTests.Neither_the_API_nor_the_stores_can_write`, BS-53, BS-B10 |
| Folder expectation searching all levels (BS-35) | PASS | defect found by acceptance, regression test added, fixed |
| Real Azure storage account (identity, RBAC, private endpoint, paging tokens, flat listing) | **NOT RUN** | no external access in this queue; `AzureBlobReadStore` is compiled and unit-surface tested only |
| Azurite emulator run | **NOT RUN** | no container image downloaded (treated as external access) |
| Hierarchical-namespace (ADLS Gen2) accounts | **NOT RUN** | listing semantics not verified |
| Correlation when an application does not propagate the execution id | NOT SUPPORTED (by design) | reported as Not correlated; time is never proof |
| Large blobs | PARTIAL | previews read a bounded first part and say so; checks on a partial read never judge syntax/content |
| Automatic retention of observations | **NOT RUN** | manual deletion only (scheduled retention stays open across BirkNext) |

## Regression

Clean tree = `5e30f803` + exactly the files of this commit, sequential partitions under the memory guard:

| Suite | Result |
|---|---|
| Backend partitions 1–6 | 1031/0/2, 1098/0/5, 830/0/0, 652/0/1, 519/**1**/3, 2136/0/8 (passed/failed/skipped) |
| Frontend | 8493 passed, **21 failed** |

All 22 failures had one cause outside the product: the scratch tree had been created from `git archive HEAD AIAssisted` only, so the
repo-root `examples/personSpec.md` (21 frontend tests) and the repo-root CI pipeline file (`PipelineReviewTests.Repository_CI_pipeline_…`)
were absent. After adding the repo-root files from HEAD, the affected classes reran **29/29** (backend) and **139/139** (frontend) PASS.
Net: backend 6267 passed / 0 failed, frontend 8514 / 0.

Scratch resources (not committed): `scratchpad/bvalidation/`, `scratchpad/webpub-bs/`, `scratchpad/wtb/`, DB `birknext_bs_20261109`.
