# BirkNext V11 — performance baseline (2026-11-27)

**Status: PENDING.** The measurements were running when this report was written (the user asked not to wait). Nothing below is estimated; every open figure is marked PENDING.

## Method
- **Environment:** isolated instance `v1env40` (ports 5097/5197, disposable database and App_Data); M2LB imported, 20 plans seeded; headless Chromium at 1440 × 900.
- **No overlap:** the chain `run_v20.sh` runs strictly in sequence (backend partitions → frontend suite → browser), so no test process competes with the timings.
- **Script:** `v1_perf.py`. Per step it records:
  - time to usable UI (until the page's ready marker is visible);
  - API requests and bytes received (response body sizes);
  - the JS heap (Chrome DevTools metrics).
- **Steps:**
  - Testoversikt and Finn og forstå: 3 runs each (median);
  - search «kvittering»;
  - the test level «Ende til ende», noting whether its results are visible without opening a group (V11);
  - opening details;
  - opening a plan;
  - opening Testgjennomføring.

## Browser results
| Step | Time to usable UI | Requests | KB | JS heap |
|---|---|---|---|---|
| Testoversikt (median of 3) | PENDING | PENDING | PENDING | PENDING |
| Finn og forstå (median of 3) | PENDING | PENDING | PENDING | PENDING |
| Search «kvittering» | PENDING | | | |
| Level «Ende til ende» (rows visible without opening a group) | PENDING | | | |
| Open details | PENDING | | | |
| Planlegg og klargjør | PENDING | | | |
| Testgjennomføring | PENDING | | | |

**Measured earlier the same day (other runs):**
- the P1 follow-up list (319 scenarios) opens in **0.07 s**;
- a Data Quality audit of all of M2LB takes **1.4 s**;
- search beyond the 2,000-candidate window is covered by V10 P1 (synthetic 12,000).

## Backend test memory
| Partition | Before V11 (peak) | After V11 (peak) | Tests (passed / failed / skipped) | Time |
|---|---|---|---|---|
| Per folder before (reference) | `Services/TestPlanning` 2.37 GB, `Integration` 2.09 GB, `Services` root 1.99 GB; partitions 0.6–3.9 GB | — | — | — |
| outside-services | PENDING | PENDING | PENDING | PENDING |
| services-1 … services-n | PENDING | PENDING | PENDING | PENDING |
| services-root | PENDING | PENDING | PENDING | PENDING |

**Per-host evidence (completed):** each disposed API host was retained, 11.7 MB per host (+70.6 MB after 6 hosts). After the fix: +0.7 MB after 6 hosts (`TestHostMemoryDiagnostics`).

**Whether the whole suite's memory improved is not claimed until the partitioned run has finished.**

**An invalid earlier attempt:** two partitioned runs had been started at the same time (one with an old build that missed the V11 fix). Both were stopped and their measurements discarded.
