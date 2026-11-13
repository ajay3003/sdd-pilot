# BirkNext — final release readiness (2026-11-13)

**Decision: NOT READY FOR TESTER DISTRIBUTION — conditionally acceptable for a controlled internal trial.**

The code is ready. The tester ZIP passes every check that could be run on the developer machine. What remains:
- No clean-machine or AVD validation.
- No authoritative Azure DevOps build of this commit.
- An open memory risk: a browser burst can take the backend to about 2.9 GB.

Ending a release decision with PASS requires the areas marked NOT RUN below; this document does not claim them.

| | |
|---|---|
| Baseline at task start | `5a4f7605`; continued from `4332cf3f` |
| Final source commit | `a6598fb7`, plus this report's commit |
| Tester ZIP accepted | `BirkNext-Tester-Windows-x64-1.0.0-local.zip`, sha256 `f6b92fc6f656791fbf3be5c6f8a2fd4ee56ccbe0407a8e6d55e80c79f12cf4ac`, built from `a6598fb7` (local build, provenance "dirty", not distributable) |
| Commits in this task | `b88a25e0` hidden-file allowlist · `26eda8d1` launcher data-folder error · `bc8a893b` technology-coverage snapshot read · `59531f1c` Workstation GC for the tester backend · `a6598fb7` Blazor integrity gate |

## Per-area classification

| Area | Status | Evidence |
|---|---|---|
| Application code (this task's changes) | **VERIFIED** | Focused backend tests 77/77 (launcher, local session, baseline, coverage, pipeline freshness), each fix with a test; negative control for the coverage read |
| Backend regression (all partitions) | **PARTIAL** | Tested source `a6598fb7` (scratch tree, content-identical to HEAD). **6 322 passed, 0 failed, 13 skipped** in partitions 1–5, 6a, 6b, 6c1 and 6c2. **6c3 BLOCKED**: aborted twice by the memory guard (free memory under 1.5 GB) with no test failure; see below |
| Frontend regression | **VERIFIED** | `BirkNext.Web.Tests`: **8 735/8 735 passed**, 0 skipped, on `a6598fb7` |
| Release build | **VERIFIED_WITH_WARNINGS** | The official package script published the shipped projects in Release from `a6598fb7`: API (self-contained win-x64), Web and Launcher, with 0 errors. A separate Release build of the test projects was **NOT RUN**: there was no memory headroom (see below) |
| Recommended Workflow, all eight views | **VERIFIED_WITH_WARNINGS** | 8/8 views reach real, view-specific next steps (browser, isolated API). Warning: the first cold load needs 17–24 s and the API peaks at 2.66–2.9 GB |
| Workspace Views (navigation, All Modules, scaling) | **VERIFIED** | All Modules 7/7, scaling 1/1, earlier main phase 28/28. The sidebar "Other view" clutter is the next queued task, not part of this decision |
| Responsive (1440/1280/1024/768/390) | **VERIFIED_WITH_WARNINGS** | 6 pages: no horizontal overflow; badges ≥ 12 px and not clipped (Dashboard, Source Analysis); selector reachable through the menu at 390 px. Warning: Relationship Review badges are not verified (not analyzed on this workspace) |
| Test Plan Suggestions / MT-7 | **VERIFIED_WITH_WARNINGS** | Manual Tester workflow shows the Test Plan proposal steps. Page load 13.9 s warm (timings in the performance report) |
| Performance / memory | **PARTIAL** | No leak. Two root causes fixed (Server GC in the package; full snapshot reads in technology coverage). **Open:** source-setup, bootstrap and documents still allocate hundreds of MB per call, and parallel loads peak at 2.66–2.9 GB (gap TPS-P2) |
| Windows tester ZIP (build, integrity, contents) | **VERIFIED** | Checksum, 2 741 entries, hidden-file gate, Blazor integrity 86/86, final launcher present (fix strings in the EXE) |
| Launcher (startup, restart, recovery, failures) | **VERIFIED** | 84/84: first and second start, persistence, k6, foreign or interrupted or newer database, port conflicts, crash recovery, backup and restore, FX-01..06 (including the fixed unwritable data folder) |
| Local connection security | **VERIFIED** | BOOT-01..12 plus browser UI-00..20 (21/21): automatic connect in 5.2 s; consumed, invalid and expired tokens and an invalid cookie get the code dialog; no token in logs |
| Developer-machine acceptance | **VERIFIED** | Everything above ran on the developer machine with isolated data and ports (not 5000/5173) |
| Clean Windows machine | **NOT_RUN** | No clean machine available |
| Azure Virtual Desktop | **NOT_RUN** | No AVD session |
| GitHub Actions hosted run | **NOT_VERIFIED / NOT_RUN** | Fixes are committed locally (`4332cf3f`, `b88a25e0`, `a6598fb7` preflight); nothing pushed; no hosted run |
| Azure DevOps pipeline (authoritative for tester distribution) | **NOT_RUN** | No trigger allowed. Its YAML is unchanged; it calls `build-tester-package.ps1`, which now includes the integrity gate |

## Regression detail (`scratchpad/final/rr13.log`, `rerun6c.ps1`)

| Host | Passed | Failed | Skipped |
|---|---|---|---|
| Partition 1 | 1 051 | 0 | 2 |
| Partition 2 | 1 099 | 0 | 5 |
| Partition 3 | 840 | 0 | 0 |
| Partition 4 | 652 | 0 | 1 |
| Partition 5 | 521 | 0 | 3 |
| Partition 6a (controllers) | 142 | 0 | 0 |
| Partition 6b (remaining services) | 1 002 | 0 | 0 |
| Partition 6c1 (`Unit`) | 788 | 0 | 1 |
| Partition 6c2 (`DeliveryReporting`, `ProjectImport`) | 227 | 0 | 1 |
| Partition 6c3 (`Integration`, `BrowserAuthentication`, root and other namespaces) | **BLOCKED** | — | — |
| Frontend (`BirkNext.Web.Tests`) | 8 735 | 0 | 0 |

**6c3 BLOCKED, not failed:**
- Partition 6c was aborted once as a whole. On rerun as three hosts, 6c3 was aborted again by the guard: free memory fell from 3.1 GB to 1.29 GB at a 1.8 GB test-host peak.
- At that point the machine had **about 1.5 GB available** in total, with the WSL VM at 3.7 GB and a separate `BirkNext.Api` development instance on port 5000 at 2.7 GB and growing.
- That development instance is not part of this task and was not touched.
- No test in 6c3 failed before the abort. It must be rerun when memory allows; until then, backend regression is PARTIAL.
- The skips are existing environment-gated tests (real ZAP, real M2LB archives), none skipped by this task.

## Defects found and fixed in this task
1. **The launcher crashed on an unwritable data folder:** unhandled exception 0xE0434352, and a double-clicked window closed straight away. Fixed in `26eda8d1`; FX-04 passes on the ZIP.
2. **The tester backend ran Server GC:** on 24 threads it reached 3.2 GB under sequential use, against 1.0–1.3 GB for Workstation GC. Fixed in `59531f1c`; the packaged backend has 20 threads and 151 MB idle, against 44 threads and 475 MB before.
3. **Technology coverage deserialized every snapshot in full** per request. Fixed in `bc8a893b`: 3.5 s → 2.0 s.
4. **A ZIP whose app never starts could be produced:** a stale incremental frontend publish led to a Subresource Integrity failure. The build now refuses it (`a6598fb7`). Found only by the real browser check on the real ZIP.

## Open defects and risks (not fixed)
- **TPS-P2 memory bursts:** parallel Recommended Workflow and Dashboard loads peak at about 2.9 GB private. On an 8 GB tester machine this is a real risk. Next step: summary or identity reads for source-setup, and a database-side projection for identity reads.
- **Recommended Workflow is slow on first load:** about 23 s on a full reload, with source-setup at 6.6–7.7 s per call.
- If the launcher process is killed externally, PostgreSQL keeps running. It is reused on the next start or stopped by `--stop`, and it is never deleted.
- **Build provenance:** local builds are "dirty" because of untracked documents in the working tree. Distribution must come from the Azure DevOps pipeline.

## Harness root causes (not product defects)
The following were all fixed in the scratch harness, and none was counted as a product result:
- the 260-character path limit of Expand-Archive;
- `python -I` imports;
- the cp1252 console;
- `BackendUrl` reset to :5000 after a republish;
- a browser wait that raced the asynchronous session gate and did not survive the app's URL replacement;
- my own 25-minute timeout killing one browser run (rerun completely);
- the badges check on an unanalyzed page.
