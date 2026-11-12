# Workspace persistence — acceptance (tasks 19B and 19C)

## 19B — baseline full regression on `6b682770`

The scratch tree was verified identical to HEAD (117 files changed since task 12, compared byte for byte).
- The in-run test build was refused by the memory guard (free memory 1.1 GB), so the counts below ran on test binaries built from **identical
  sources**: the backend at the contract-fix build, the frontend at the wording-fix build.
- Partitions 1–5 were first refused before starting (free memory 1.5 GB) and were run separately once 7.7 GB was available.

| Phase | Passed | Skipped | Failed | Total |
|---|---|---|---|---|
| Backend 1 / 2 / 3 / 4 / 5 | 1 049 / 1 098 / 840 / 652 / 521 | 2 / 5 / 0 / 1 / 3 | 0 | 1 051 / 1 103 / 840 / 653 / 524 |
| Backend 6a / 6b / 6c | 139 / 956 / 1 310 | 0 / 0 / 8 | 0 / 0 / **1** | 139 / 956 / 1 319 |
| **Backend total** | **6 565** | **19** | **1** | **6 585** |
| **Frontend** | **8 692** | 0 | 0 | 8 692 |

- **The one failure:** `ProjectImportPipelineTests.DocumentsOnlyImport_WithoutEnvironment_IsNotAFailure` hit its 100-second HttpClient timeout
  (2 min 45 s) while the machine was under memory pressure. Rerun alone, the class passed 8/8 in 12 s. Classification: **environment / resource
  timing, not a product defect**. No test was changed.
- **The Contract Testing snapshot-count fix (`075c006c`)** is covered by a full partition: 6b passed 956/956, including `ContractBulkImportTests`.

## 19C — focused tests (on the 19C tree, gated on a successful build)

- **Backend:** workspace persistence, lifecycle documents, body store, workspace packages, Local Data Reset and migrations: **147/147**. This
  includes 5/35/75 MB round trips and the real-PostgreSQL immutability test.
- **Frontend:** uploader, workspace persistence, auto-save, session and layout: **150/150**.

## 19C — real browser acceptance

- **Install:** the isolated install with the real failing state: database `birknext_mb`, whose last good save was a 23 MB lifecycle (1 368
  revisions; every later save had failed).
- **Upgrade:** migration `AddWorkspaceDocumentBodies` was applied as an upgrade at API start.
- **Imports:** real archives from `C:\Users\ajaan\Downloads`, imported through the Project Import UI.
- **Harness:** `scratchpad/mb/ws-accept.cjs`.

| Id | Check | Result |
|---|---|---|
| WS-1 | Auto-save after each additional large import (M2LB Skole.zip, then M2LB (7).zip and M2LB.zip) returns 200 | PASS |
| WS-2 | Auto-save request < 5 MB; uploads < 24 MB per request | **FAIL against the 5 MB criterion.** Upload batches: one of 7.6 MB; none for the re-zip, whose bodies were already stored. The auto-save request grew from 3.1 MB to 5.5 MB over the extra imports: revision metadata plus generated-document text, still inline, about 1 MB per import. Far below 30 MB, but it still grows; gap WP-1. |
| WS-3 | No "Not saved" indicator after a successful save | PASS |
| — | Stored state after upgrade | Lifecycle 23 MB → **2.4 MB**, then **4.4 MB** with 2 620 revisions. **0 inline bodies.** Body store: 406 → **711 distinct bodies, 13 MB** (identical documents of the M2LB versions stored once). Trigger present. |
| WS-4 | After an API restart, every revision with a body gets it back, byte-identical to its address | PASS: 2 620 of 2 620, 0 mismatches |
| WS-5 | Restored document text exceeds the old 30 MB limit | PASS: **40 302 404 characters** |
| WS-6 | Import history survives | Harness selector wrong (0 rows found). **Verified in the database instead:** 7 import records; `M2LB (3).zip` keeps its 2 history entries; the new imports have 1 each. |
| Z | No console or page error; nothing left localhost | PASS (save, save with two imports, reload) |

**Data loss caused by the original bug (observed, not reversible):** two import records from the earlier session, M2LB (7).zip and M2LB.zip,
existed only in that browser's state when every save was refused, so they are not in the saved workspace. The fix prevents this from recurring;
it cannot recover state that was never stored.

## Covered by tests only (not by the browser)

| Case | Test |
|---|---|
| Interrupted or partial write: nothing written when a body is unresolved | `WorkspaceDocumentBodiesTests.A_manifest_save…`, `…A_claimed_body…` |
| Duplicate retry / concurrent identical upload | `…An_upload_under_a_wrong_address…` (idempotent re-upload); `StoreAsync` treats a concurrent insert of the same address as success |
| Malformed or corrupted payload (wrong address) | `…An_upload_under_a_wrong_address_refuses_the_whole_batch` |
| Legacy migration | `…A_legacy_row_with_inline_bodies…` and the real upgrade above |
| Failure status in the UI | `WorkspaceDocumentBodyUploaderTests.The_auto_save_state_is_failed_with_the_reason_never_saved` |

**NOT RUN:**
- storage exhaustion;
- a browser refresh during a save;
- transport interruption in the browser.

## Gaps

| Id | Pri | Gap |
|---|---|---|
| WP-1 | P3 | The lifecycle metadata and generated-document contents still travel whole on every save (about 1 MB per large import). Bodies were the 30 MB driver and are fixed. Moving `GeneratedDocumentContents` into the same store is the next step if workspaces reach about 25 imports. |
| WP-2 | P3 | Orphaned bodies (from an interrupted save, or removed revisions) are not collected; they are harmless and cleared by Local Data Reset. |
