# BirkNext — self-contained Windows tester distribution: acceptance (2026-11-11)

- **Code under test:** `6dc8db98` and `f469ded5` on `008-traceability-first`.
- **Final package:** `BirkNext-Tester-Windows-x64-1.0.0-local.zip`. Its identity is `1.0.0-local+f469ded5….dirty`; the "dirty" flag comes from unrelated uncommitted files in the working tree, not from BirkNext code.
- **Packages built:** four local packages (pkg1–pkg4), all with `scripts/build-tester-package.ps1` from pinned inputs.
- **Environment:** this developer PC (Windows 11 Enterprise x64). All runs used isolated folders under the session scratchpad, with throwaway data folders and synthetic data.
  - Only localhost was contacted.
  - No developer database, Podman container or port 5000/5173 service was touched.

Statuses: PASS · FAIL · PARTIAL · BLOCKED · NOT RUN.

## 1. Clean-PC acceptance

**BLOCKED / NOT RUN.** No clean Windows VM is available here.

- This PC has Podman, WSL, the .NET SDK and the VC++ runtime installed. Removing them from PATH is **not** equivalent to a clean PC, and was not attempted.
- The pipeline job `BuildWindowsTesterPackage` (windows-latest agent: no PostgreSQL or k6, but with a .NET SDK) runs the same validate-and-start check, but **was not triggered**.

**Remaining clean-PC steps:**
1. On a fresh Windows 10/11 x64 VM without Podman, Docker, WSL, .NET, PostgreSQL or k6, but with the VC++ 2015–2022 x64 redistributable, extract the archive and run `Start-BirkNext.exe`.
2. Repeat on a VM **without** the VC++ redistributable; expect the named prerequisite message.
3. Repeat with AppLocker/WDAC policies and SmartScreen, as on a managed PC.

## 2. Package (final pkg4)

| Check | Result |
|---|---|
| Archive written with `.sha256`; `package-archive.ps1 -Verify` | PASS |
| Size | 247 MB zip / 576 MB extracted |
| Files with SHA-256 in `package-manifest.json` | 2,739 |
| Migrations recorded | 78 |
| Build manifest consistent (backend, frontend, Companion) | PASS |
| PostgreSQL 16.13 archive SHA-256 = lock | PASS |
| k6 1.0.0 archive SHA-256 = lock = upstream checksums | PASS |
| License notice SHA-256 = lock | PASS |
| Self-contained backend: `BirkNext.Api.exe` + .NET 8 runtime | PASS |
| Single-file `Start-BirkNext.exe` | PASS |
| No `appsettings.Development.json` / `appsettings.Local.json` / `.env` in the package | PASS |
| Deepest relative path | 121 characters (see SC-G9) |

## 3. Isolated TesterNative acceptance (harness `acc.py`, real launcher and binaries)

**Final: 49 PASS, 0 FAIL.**

| Area | IDs | Result |
|---|---|---|
| First run (validate, initdb, SCRAM roles, migrations of 4 contexts, health, frontend, deep link, session gate, runtime profile, clean stop) | TN-01…17 | 17 PASS. Ready 33–51 s after start; initdb ≈ 23 s |
| Security | TN-04…09 | Listens only on 127.0.0.1; password required; no `postgres/postgres`; no `trust`; DPAPI credentials; no password values in launcher, PostgreSQL or backend logs |
| Restart and persistence | TN-20…26 | Same cluster; identity verified; no initdb; k6 run and definitions persisted; a second launch reports the running instance; `--status`; `--stop` |
| Native k6 through BirkNext's authorized API | K6-01…05, 99 | Definition, then a network check by the bundled k6 (HTTP 200 from a synthetic 127.0.0.1 server), run, **Completed on `process.native`, k6 1.0.0, 50 requests, p95 1.5 ms, 0 % errors, verdict Pass**. Impossible p95 threshold: verdict **Fail** |
| Launcher crash | TN-18/19 (pkg1) | The killed launcher's PostgreSQL was reused only after identity verification |
| PostgreSQL crash | CR-00…04 | Postmaster killed (by the PID in our own `postmaster.pid`); PostgreSQL's own crash recovery on the next start; committed run intact |
| Backup and restore | BK-01…03 | `--backup` verified with `pg_restore --list`. Restore into a separate throwaway cluster: 6 definitions, 4 runs (the k6 run found), 68 migrations. A truncated backup fails to restore |
| Failure handling | DB-*, PROFILE-*, DIAG | Foreign files refused and left untouched; interrupted first run moved aside and kept; foreign listener on 55432 left alone (55433 used and persisted, then reused); PG_VERSION 17 refused with nothing started; DeveloperContainer and unknown profiles exit 64; diagnostics archive without credentials or password values |

### Additional runs on the final package (pkg4)

| ID | Check | Result |
|---|---|---|
| UP-01 | **Real upgrade:** pkg4 on the data folder last opened by pkg3 (different build) | PASS. Full re-verification of 2,739 files (new package root); cluster verified; `birknext-…-pre-upgrade.dump` (234 KB) written and verified **before** the new backend started; ready in 29 s; `last-run.json` updated |
| UP-02 | Restart with the same build | PASS. No second backup; ready in 4–6 s |
| NS-01 | Schema ahead of the build (synthetic future migration row in the disposable cluster) | PASS. Exit 2: "The database requires a newer BirkNext than this package."; PostgreSQL left stopped; normal start after the row was removed |
| PK-01…04 | Tampered `k6.exe`; missing `postgres.exe`; corrupt runtime manifest; missing `package-manifest.json` | PASS: "has been changed after it was built", "files are missing", "damaged", "incomplete". PK-04 crashed on pkg3 and was fixed (see run history) |

## 4. Browser acceptance (Microsoft Edge, the packaged launcher and pkg4)

Only `localhost:5481` (frontend) and `localhost:5480` (API) were contacted; every other origin is aborted by the harness.

| Phase | Result |
|---|---|
| main | **13/13 PASS**: Project Quality loads; System Settings Runtime & Environment = TesterNative, Windows Tester Package, PostgreSQL Connected 16.13, bundled k6.exe 1.0.0 Available, no switch; schema 4/4 Current; synthetic project import (`pq-synth`); Source Analysis; Recommended Workflow; Performance Test Review shows the native run; Test Execution & Evidence; no console or page errors; no 5000/5173 requests |
| after-restart | **11/11 PASS** (fourth run; see below): the same project is current after restart; the native run is still listed |

Visual check: screenshot `rte-main.png` (the Runtime & Environment card) was inspected. It is compact and readable, with no layout defect.

## 5. Run history (original failures, reported as found)

| Run | Failure | Cause | Resolution |
|---|---|---|---|
| pkg1 K6-01 | 400 | Harness used workload purpose `Smoke` (not an enum value) | Harness fixed (`Baseline`) |
| pkg1 K6-02 | **403** | **Product:** active tests not authorized in a Production backend (local trusted execution was Development-only) | Owner decision: TesterNative honours it under the same checks |
| pkg1 TN-19 | FAIL | A stale `session.json` from the killed launcher let the harness read a not-yet-ready instance | **Product:** the launcher removes stale session/stop files once it holds the lock. Harness checks the launcher PID |
| pkg1 | file busy | **Product:** PostgreSQL inherited the launcher's redirected output handle | `pg_ctl` started through ShellExecute (no inherited handles) |
| pkg3 K6-02 | 422 | Readiness requires the load generator's own network check (as in the UI) | Harness performs it. **Product:** native wording of the readiness item |
| pkg3 failures | harness crash | Port 55432 still held: **product:** `--backup` left PostgreSQL running | The launcher stops the database after `--backup` |
| pkg3 PK-04 | **crash** (unhandled exception) | **Product:** `ManifestHash` read a missing `package-manifest.json` before the existence check | Checked first; a catch-all turns unexpected errors into a tester message plus log |
| pkg4 after-restart (2 runs) | BR-12 FAIL | Console error from `POST api/recommended-workflow/build-steps` `net::ERR_ABORTED`: the harness navigated away while the dashboard was still loading | Harness waits for network idle (then 11/11). The frontend logging a navigation abort at Error level is SC-G10 |

## 6. Developer mode (DeveloperContainer)

| Check | Result |
|---|---|
| Podman PostgreSQL, container k6, `start-local.ps1` behaviour unchanged except the profile variable | Static review PASS; full backend/frontend regression (§7) |
| Live developer start | **NOT RUN.** The developer's own instance holds ports 5000/5173 and must not be touched |
| Container k6 runner | Unit tests (`PerformanceTestReviewTests` over the Podman runtime) PASS; no live container run in this task |

## 7. Regression

| Suite | Result |
|---|---|
| Focused: performance tests + launcher + native k6 + local trusted execution | 400/400 PASS (final), 78/78 (launcher subset) |
| Focused frontend: runtime card, build card, Help Center, System Settings, user guide, documentation, performance | 389/389 PASS |
| Full backend (6 sequential, memory-guarded partitions; code at `f469ded5`) | **6,492 passed, 19 skipped, 3 failed** (partitions: 1,049 / 1,097+1 failed / 840 / 652 / 520 / 2,334+2 failed) |
| Full frontend | **8,612 passed, 3 failed** |

The original failures, as found:

| Failure | Cause | Outcome |
|---|---|---|
| `TestPostgresGuardTests.No_test_reads_the_configured_connection_or_names_the_real_database` | **Real:** the new `TesterLauncherTests` contained a connection-string literal naming the real database (it only formatted strings, never connected) | Fixed: the assertions parse the string and compare with the credentials; guard + launcher tests 24/24 PASS |
| `AltinnTestingTests.ASubmission_EvidencesTheSixAltinnSteps…` (no 11-digit number in the stored run) | Pre-existing intermittent assertion: random hex (fingerprints, ids) can contain an 11-digit run; the test already strips the run id. Code not changed by this work | 5/5 PASS alone; not weakened; recorded as gap SC-G16 |
| `ScenariosQueryTests…WarmedP95CompletesWithinTwoSeconds` (p95 5,611 ms) | Known load-sensitive timing test (UD-G11); a parallel read-only analysis agent was running | 5/5 PASS alone; threshold unchanged |
| 3 frontend bUnit waits (`AuthenticationPaneSemanticsTests…`, `FrontendQualityReviewActiveEnginesUITests…`, `ActiveTargetEnvironmentUITests…`) | `WaitForFailedException` under the same load | 3/3 PASS alone; timeouts unchanged |
