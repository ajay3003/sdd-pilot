# BirkNext — Parquet Data Explorer — acceptance (2026-11-09)

Branch `008-traceability-first`. Source: `514478d2` + this change (committed with these reports).

**Result: 44/44 PASS** — API 23/23, after restart 4/4, browser 17/17. Local files only: the fixtures were written by a standard-library Python
writer (`Fixtures/make_fixtures.py`) with an independent oracle (`oracle.json`) computed while writing. **No third-party Parquet writer (Spark,
pyarrow, Parquet.Net) produced any file checked here** — no package was downloaded.

## Environment (isolated)

| Item | Value |
|---|---|
| API | The real BirkNext API (clean build) on `localhost:5098`, content root without a Local settings file, outbound proxies to a dead port |
| Database | `birknext_pq_20261109` (new, disposable) |
| Files | `children_v1.parquet` (1 000 rows, Snappy, 3 row groups, dictionary + plain, nulls), `children_v2.parquet` (GZIP, data page v2, schema +school −flag, records added/removed/changed), `nested_schema.parquet`, `zstd_declared.parquet`, `bad_magic`, `bad_footer_length`, `truncated`, `empty`; SHA-256 recorded before and compared after |
| Web / browser | Published frontend on `localhost:5198`; Edge (Playwright, headless); non-localhost requests aborted (0) |

## API — `pqvalidation/accept.cjs main` / `after-restart`

| ID | Check | Result |
|---|---|---|
| PQ-01 | v1 opened (Snappy, 3 row groups) | PASS |
| PQ-02 | Columns and logical types as written | PASS |
| PQ-03 | Null counts from metadata equal the oracle | PASS |
| PQ-04 | Row 1 decodes exactly (string, int, dictionary, date, timestamp, decimal, double, boolean) | PASS |
| PQ-05 | A page spanning row groups 1→2 | PASS |
| PQ-06 | A page filter says it covers only the page | PASS |
| PQ-07 | Full-scan filter counts every match (ACTIVE = oracle) | PASS |
| PQ-08 | Typed numeric filter (age > 15 = oracle) | PASS |
| PQ-09 | Profile: exact nulls per column, distinct ids, min/max | PASS |
| PQ-10 | Rules: unique id, allowed status, count, age range pass; name NotNull fails with 58 rows and row numbers | PASS |
| PQ-11 | v2 opened (GZIP, data page v2, schema changed) | PASS |
| PQ-12 | Compare by id equals the oracle (schema +school −flag; added/removed/changed/unchanged) | PASS |
| PQ-13 | ZSTD: opened, columns reported not readable (no guessing) | PASS |
| PQ-14 ×4 | bad magic, truncated, bad footer length, empty — each refused with a reason | PASS |
| PQ-15 | Evidence saved with rule results, fingerprint and the linked run | PASS |
| PQ-16 | A run that does not exist cannot be linked | PASS |
| PQ-17 | Stored evidence contains no row values | PASS (after DEF-2) |
| PQ-18 | Export: self-describing, no row values | PASS |
| PQ-19 | The original files are unchanged (SHA-256) | PASS |
| PQ-20 | After restart: saved evidence reopens without the source file | PASS |
| PQ-21 | After restart: an unexpired session still opens from its temporary copy | PASS |
| PQ-22 | Closing removes the temporary copy | PASS |
| PQ-23 | Local evidence deletion | PASS |

## Browser — `pqvalidation/browser.cjs`

| ID | Check | Result |
|---|---|---|
| PQ-B1…B3 | Upload → Overview row count; column metadata with null counts; schema tree with logical types | PASS |
| PQ-B4…B6 | Data page decoded, nulls shown as null; pagination; page filter scope stated | PASS |
| PQ-B7…B8 | Full-scan count and profile nulls equal the oracle | PASS |
| PQ-B9 | Rules: NotNull(name) Fail with row numbers, Unique(id) Pass | PASS |
| PQ-B10a | Comparison direction is stated (baseline → current) | PASS (after DEF-3) |
| PQ-B10 | Compare by id: schema changes and record counts equal the oracle (Added 15, Removed 10, Changed 19, Unchanged 971, Ambiguous 0) | PASS |
| PQ-B11…B13 | Evidence saved and listed; export without row values; local deletion after a confirming second click | PASS |
| PQ-B14 | No horizontal page scroll at 1440/1280/1024/768/390 (Overview, Data, Compare) | PASS |
| PQ-B15 | Keyboard: tabs reachable in order | PASS |
| PQ-Z1 | No page errors, console errors or external requests | PASS |

## Defects found and fixed during acceptance

| ID | Found by | Defect | Fix |
|---|---|---|---|
| PQ-DEF-1 | Build | Page crashed without a review context factory in some hosts | null-safe context use |
| PQ-DEF-2 | PQ-17 | The check itself was too strict (rule text "ACTIVE" is not a row value) | check limited to row-only values; DB reset and rerun |
| PQ-DEF-3 | PQ-B10 | Which file was the baseline was unclear, so a tester could read Added/Removed the wrong way round | baseline label, stated direction, **Swap** button + bUnit test |
