# BirkNext — Backup & Transfer acceptance (2026-11-11)

**Environment:**
- Microsoft Edge, with every non-localhost request aborted.
- The Release frontend on `:5497`.
- An isolated Development API on `:5496`, with its own content root and the disposable database `birknext_bt_20261111` on the developer PostgreSQL 16.13 server.

**Independent oracle:** `psql` against the source and restored databases, plus SHA-256 of the files on disk. It is never only the service's report.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN.

## 1. Seeded state (independently specified)

- **Database:** two synthetic source snapshots (`BT-Synth-A.zip`, `BT-Synth-B.zip`) and a schema artifact. The Skolenærvær guide was **published through the real lifecycle** (submit → approve → publish, approved by `local-user`).
- **Managed files:** Critical E2E `flows.json` and `suggestion-reviews.json`, Test Data `registry.json`, and `local-data-reset.json` (6 found, including two the API creates).
- **Canary secrets (must never travel):**

| Location | Canary |
|---|---|
| `api-service-credentials/secret-bt.bin` | `CANARY-SECRET-API-…` |
| `azure-devops/pat.bin` | `CANARY-PAT-ADO-…` |
| `browser-authentication/login.bin` | `CANARY-LOGIN-…` |
| `parquet-sessions/session.parquet` | `CANARY-PARQUET-ROW-…` |
| `local-session/launch-code.txt` | — (the real launch code) |

## 2. Browser and real PostgreSQL acceptance

| ID | Check | Host: bundled Windows binaries | Container exec |
|---|---|---|---|
| BT-SEC-1 | Without the local session, backup status is refused (401) | PASS | — |
| BT-01 | Tool directory empty: "PostgreSQL backup tools could not be found", with runtime, expected tools and "Looked in" | PASS | — |
| BT-02 | Primary action disabled **with a reason** | PASS | — |
| BT-03 | Tiles: Database Ready, Backup tools Unavailable, "Backup: Unavailable" | PASS | — |
| BT-04 | Bundled 16.13 tools copied into the directory, then **Retry detection**: Ready, with versions compared | PASS | — |
| BT-10 | Readiness Ready for backup and restore (database, pg_dump, pg_restore, compatibility, destination; plus identity for the container) | PASS | PASS |
| BT-11C | The container is verified to be BirkNext's own server | — | PASS |
| BT-12 | Passphrase mismatch shown live and blocks the action | PASS | PASS |
| BT-13 | Real encrypted backup created through the UI and listed (112 tables, 83 rows, 6 files, Complete) | PASS (0.5 s, 258 KB) | PASS (2.3 s) |
| BT-14 | File encrypted: no canary, no row text and no plaintext entry name is readable in the bytes | PASS | PASS |
| BT-15 | No `.partial`, `.zip` or `.dump` intermediate left | PASS | PASS |
| BT-16 | Wrong passphrase: "Decryption and authenticity: Fail" | PASS | PASS |
| BT-17 | Tampered copy (one byte flipped): restore FAILED, and the count of `birknext_restore_%` databases is unchanged | PASS | PASS |
| BT-18 | "The current BirkNext installation will not be changed." shown, and true: restore only creates a new database | PASS | PASS |
| BT-19 | Verify (preview) decrypts and checks hashes and the manifest before a restore | PASS | PASS |
| BT-20 | Isolated restore into a **new** database: VERIFIED_WITH_WARNINGS. The only warning is by design: "credentials… were not in the backup; re-enter them" | PASS (1.4 s) | PASS (3.1 s) |
| BT-21 | **Independent** inventory of the restored database = source (snapshot ids, schema artifact, guide revision + state + content fingerprint) | PASS | PASS |
| BT-22 | Approval provenance survives (Published, ApprovedBy, PublishedAt) | PASS | PASS |
| BT-23 | Managed files restored byte-identically (SHA-256) into a separate folder | PASS | PASS |
| BT-24 | No canary anywhere in the restored files; no excluded store folder restored | PASS | PASS |
| BT-25 | The original database is unchanged (inventory and row counts) | PASS | PASS |
| BT-26 | A **second API** started against the restored database `birknext_restore_7cf865d9b616` serves the published guide (revision 1, approved by `local-user`) and the two synthetic snapshots | PASS | |
| BT-30/31 | After an API restart (new process), backup history and restore reports persist; readiness is evaluated again | PASS | |
| BT-L | No page-level horizontal overflow at 1440, 1280, 1024, 768 and 390 px | PASS (5/5) | |
| BT-Z | No external request, page error or console error in any phase | PASS | PASS |

- **Totals:** missing 5/5, main 17/17, container 17/17, after-restart 3/3, visual 6/6, restored-API check 1/1.
- **Screenshots inspected:**
  - At 1440 px, the missing-tools state shows readiness tiles, then one blocker panel with the runtime, expected tools, the search location, and "Retry detection" next to the setup guide.
  - At 390 px, the ready state is one readable column, with the restore assurance and history rows.
  - One finding was fixed after inspection: the disabled button repeated the full path, so it now points to the blocker above.

## 3. Automated tests

| Suite | Result |
|---|---|
| Focused backend (`WorkspaceTransfer*`, including the new `PostgresToolReadinessTests`, `TesterLauncherTests`, `FullBackupServiceTests`, packages) | **72/72 PASS**, real PostgreSQL, real tools via the existing test gate |
| Focused frontend (`WorkspaceTransfer*`, User Guide) | **59/59 PASS** (after rewriting the backup-area test for the new layout and adding the missing-tools test) |
| Full frontend at `dbe5947e` | **8631/8631 PASS** |
| Full backend at `dbe5947e`, six sequential memory-guarded partitions | **6527 passed, 0 failed, 19 skipped (6546)**: 1049 / 1098 / 840 / 652 / 520 / 2368 passed |

## 4. Not run / blocked

- **Tester Package end-to-end (rebuilt package, launcher-managed cluster): NOT RUN.** The package was not rebuilt in this task. What was covered instead:
  - the launcher environment (unit test);
  - the bundled 16.13 Windows binaries actually executing the backup and restore (Host phase);
  - the restore Blocked rule (unit-tested on the role property).
- **Developer launcher end-to-end: NOT RUN** (it would start services on 5000/5173). Its container lookup was executed on its own.
- **Synthetic fixtures not exercised:**
  - D (incompatible tool version; covered by unit tests of the rule);
  - F (port conflict);
  - O (interrupted restore);
  - R (unsupported schema; covered by the existing `A_backup_from_a_newer_build_is_refused_before_any_restore`);
  - U/V (package import conflicts and cross-project rejection; covered by existing workspace package tests).
- **Performance:** only durations were measured (above). Memory and temporary disk were not measured for this small dataset. The existing design streams the dump and the encryption, and does not load the backup into memory.
