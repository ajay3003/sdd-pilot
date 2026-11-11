# BirkNext — Workspace backup, export, import and recovery: acceptance (2026-11-11)

Branch `008-traceability-first`.
- **Baseline:** `fe12e79e`.
- **Feature commits:** `35a498a2`, `28e99c6c`, `7982fafd`, `e688c8e6`, `826e8717`.
- **Follow-up:** `f641cbb3` (CI compile fix, XML docs and GUID false positive).

No push, no deployment, no external system. Every database used was a temporary or scratch database; the user's `birknext` database was only read
during the audit.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN · NOT SUPPORTED.

## 1. Environment

| Item | Value |
|---|---|
| Installations | Two real BirkNext APIs (scratch Debug build of HEAD): **source** :5098 → `birknext_wtx_src_20261111`; **destination** :5096 → a fresh database per run (`…dst…`, `…dst2…`, `…dst3…`). Separate content roots, so separate file stores. |
| Frontend | The Release-published frontend served on :5198 and :5196; Playwright routes each browser context to its own API |
| Browser | Microsoft Edge (Playwright); every non-localhost request aborted |
| PostgreSQL tools | `pg_dump` / `pg_restore` 16.13 through `podman exec` (ContainerExec mode) |
| Synthetic target | `target.py` on :5097 with its own request ledger |
| Source data | Imported through the real APIs: the synthetic contracts project (`proj-a-uee.zip`, contracts, generated tests, one real run); the **real `M2LB.zip` and Tiltak 2 archives** (read-only); a composite context from the discovery's own candidates (Proposed); two Connected Systems through the setup pipeline (enabled at the origin); one release-gating Critical E2E flow. Only the catalog rows and source bindings, which have no creation API, were inserted by SQL into the scratch source database. |
| Oracles | `psql` on both scratch databases, `flows.json` on disk, the target ledger, SHA-256 computed by the harness. Never BirkNext's own summaries. |

## 2. Automated tests

| Suite | Result |
|---|---|
| `TransferCryptoAndArchiveTests` (26 incl. theory rows) | PASS |
| `FullBackupServiceTests` (5; real `pg_dump`/`pg_restore`, temporary databases) | PASS |
| `WorkspacePackageRoundTripTests` (10; two temporary databases, independent ledger) | PASS |
| `PackageSecretScannerTests` (4) | PASS |
| `WorkspaceTransferPageTests` (5, bUnit) | PASS |
| Navigation, documentation catalog and User Guide guards (updated deliberately for the new route) | PASS |
| `CompilerCompatibilityTests` (the .NET 8 CI parse guard) | PASS after `f641cbb3` |

## 3. Browser acceptance — final runs

**Source phase (web :5198 → API :5098): 22/22 PASS.**

| ID | Check | Result |
|---|---|---|
| WT-01/02 | The page opens with four areas and five operations; listed under Help & Settings | PASS |
| WT-03 | Integration scopes come from stored configuration; the known profile shows its name | PASS |
| WT-04 | The dependency review counts what is stored (5 integration definitions = psql; sources; browser test) | PASS |
| WT-05 | The security review is shown | PASS |
| WT-06 | The downloaded package's SHA-256, computed by the harness, equals the one the page shows | PASS |
| WT-07 | No secret value in the package | PASS |
| WT-08/09 | Project search; the **real M2LB** project package is created and downloads | PASS |
| WT-10 | Backup status shows the tools and exclusions | PASS |
| WT-11/12 | A full backup is created and covers every table (count from psql) | PASS |
| WT-13 | A wrong passphrase fails authentication | PASS |
| WT-14 | With the passphrase every check passes | PASS |
| WT-15 | Isolated restore into a new `birknext_restore_*` is VERIFIED | PASS |
| WT-16 | The restored database has the same snapshots as the source (psql on both) | PASS |
| WT-17 | The running source database is unchanged | PASS |
| WT-18 | Recent activity lists the exports | PASS |
| WT-L1–L3 | No page-level horizontal scroll at 1440 / 1280 / 1024 / 768 / 390 px (overview, export review, backup) | PASS |
| WT-Z | No request left localhost; no page or console error | PASS |

**Destination phase (web :5196 → API :5096): 19/19 PASS.**

| ID | Check | Result |
|---|---|---|
| WT-20 | The preview writes nothing (psql: no imported row) | PASS |
| WT-21 | Integrity, format and identities pass | PASS |
| WT-22 | The environment is unresolved by default (never matched by name) | PASS |
| WT-23 | Import into the verified destination environment completes | PASS |
| WT-24 | Both real stage sources (M2LB, Tiltak 2) arrived, each in its own project | PASS |
| WT-25 | All 5 integration definitions are in `dst-local` and **disabled** | PASS |
| WT-26 | Connected Systems arrived **disabled**, with no secret value | PASS |
| WT-27 | The browser test arrived disabled, not release-gating, re-keyed and tagged `imported:` (`flows.json`) | PASS |
| WT-28 | The source run is imported as **history**; the destination has no run of its own | PASS (after WP-D6) |
| WT-29 | Importing made no request to any target (ledger unchanged) | PASS |
| WT-30 | Readiness never says ready (credentials and DOM revalidation required) | PASS |
| WT-31 | **MU → MM keeps its finding: Incompatible, not accepted** — identical to the source | PASS |
| WT-32 | Endpoint bindings were re-keyed to the chosen environment | PASS |
| WT-33 | After explicit approval, a run in the destination reached the **local** synthetic target; outcome (1 passed, 0 failed) matches the ledger | PASS |
| WT-34 | The destination now has exactly one own run besides the imported history | PASS |
| WT-35 | Recent imports listed | PASS |
| WT-L4/L5 | No horizontal scroll at the five widths (import preview, import report) | PASS (L5 after WP-D7) |
| WT-Z | Clean | PASS |

**After-restart phase (destination API restarted): 5/5 PASS.**
- WT-40: the operations log still shows the completed import.
- WT-41: imported rows persist.
- WT-42: re-importing the same package duplicates nothing (psql counts unchanged).
- WT-43: the real M2LB project package previews as **Identical** (2 objects) against the destination.
- WT-Z: clean.

**Restored installation (BirkNext started against `birknext_restore_f8cbfc0e2cda` + restored files): 4/4 PASS.** Contracts, Critical E2E
flows, composite contexts and the original execution run are answered exactly as by the source API.

**Total browser and installation acceptance: 50/50 PASS in the final runs.**

### Run history

| Run | Result | What failed and why |
|---|---|---|
| source 1 | aborted at WT-03 | Harness: the content roots used an old `appsettings.Development.json` without `Authorization:LocalWorkspaceTransfer` (HTTP 403). The page showed the permission error. |
| source 2 | 7/22, aborted | Harness: the running API predated the string-enum change (HTTP 400); rebuilt |
| source 3 | 22/22 | — |
| destination 1 | 16/19 | WT-27 and WT-31: harness read the wrong field names (`id` / `transitions`; the stores use `Id` / `compatibility`); WT-31 had passed vacuously and was rewritten to require the finding. **WT-28: real gap WP-D6.** **WT-L5: real layout defect WP-D7.** |
| destination 2 | 18/19 | WT-L5 (CSS fix not yet published) |
| destination 3 | 19/19 | — |
| after-restart | 5/5 | — |

## 4. Real M2LB acceptance

| Check | Result |
|---|---|
| Project package for the real `m2lb` project | 1 source snapshot + 1 composite source context; other projects' stage sources (Tiltak 2) are EXTERNAL_REFERENCE, never pulled in |
| Integration package for the synthetic environment over the real sources | M2LB and Tiltak 2 snapshots both included, each as its own project; the composite context keeps 6 stored transitions |
| MU → MM after transfer | `mu->mm` stays **Incompatible** (Message schema (XML): Incompatible), not accepted — the import does not change the finding (see `birknext-mu-mm-skolenaervaer-contract-gap.md`, MM-G1) |
| No real system | No M2LB, MU, MM, Altinn or Bufdata system was contacted; the only executed request went to the local synthetic target |

## 5. Full regression

Run sequentially on the final tree (`f641cbb3`), with the existing memory guards; no timeout was changed and no test was disabled.

| Step | Result |
|---|---|
| Backend partitions 1–6 | **6,444 passed, 19 skipped, 0 failed** (1031 / 1098 / 837 / 652 / 520 / 2306) |
| Frontend full suite | **8,581 / 8,581 passed** |
| Release build `BirkNext.Api` (local .NET 9 SDK) | PASS: 0 errors, 0 CS1570 |
| Release publish of the frontend | PASS (used for the browser acceptance) |
| Earlier run on `826e8717` | backend 1 failed: the CI-compatibility guard on `WorkspacePackageService.Import.cs:215` (the real CS1003 the pipeline reported); fixed in `f641cbb3` |
| Azure DevOps pipeline (.NET 8 SDK) | NOT RUN (no pipeline was triggered; .NET 8 SDK not installed locally). The repository's .NET 8 parse guard passes. |

## 6. Not covered here

See the gap register for the following:
- per-project authorization (WP-R1) and package signatures (WP-R2);
- types not in v1 packages (WP-R3);
- module pages after import, verified through data rather than their own UI (WP-R8);
- cross-machine restore (WP-R6);
- the scratch databases kept until you approve their removal (WP-R9).
