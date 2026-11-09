# BirkNext — Parquet Data Explorer — gap register (2026-11-09)

Branch `008-traceability-first` (local only). Initial SHA `514478d2`; final SHA = the commit carrying this file.
Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED.

**Overall: PARTIAL.** A self-contained reader (no package, no Python runtime) opens, previews, filters, profiles, validates and compares flat Parquet
files and saves evidence without row values; verified 44/44 against an independent oracle. Nested data, some codecs and files from third-party
writers are not verified.

| Capability | Status | Notes |
|---|---|---|
| Open a local file (≤ 64 MB), temporary BirkNext-named copy, 2 h sessions, close removes the copy | PASS | PQ-01, PQ-21, PQ-22 |
| Refuse corrupt / truncated / empty / encrypted-footer files with a reason | PASS | PQ-14 ×4, tests |
| Overview and statistics from file metadata (labelled; nothing inferred) | PASS | PQ-03 |
| Schema tree (physical, logical, repetition, readability) | PASS | PQ-02, PQ-B3 |
| Flat columns: PLAIN, dictionary (RLE_DICTIONARY / PLAIN_DICTIONARY), RLE/bit-packed levels, data page v1 + v2 | PASS | PQ-04, PQ-11 |
| Codecs UNCOMPRESSED, SNAPPY, GZIP | PASS | |
| Codecs ZSTD, LZ4, LZ4_RAW, BROTLI | **NOT SUPPORTED** | the column is reported not readable with the reason (PQ-13); no guessing |
| DELTA_* and BYTE_STREAM_SPLIT encodings | **NOT SUPPORTED** | reported not readable |
| Nested / repeated columns (lists, maps, structs) | **NOT SUPPORTED** | shown in the schema tree, values not read |
| INT96 timestamps | PARTIAL | converted (legacy Julian day + nanos); not verified against a third-party writer |
| Files written by Spark / pyarrow / Parquet.Net / Synapse | **NOT RUN** | no third-party writer available offline; the writer used is ours, the oracle is independent of the reader |
| Page vs full-scan filters (typed) | PASS | PQ-06…08 |
| Profile (exact nulls, distinct up to 100 000, min/max/mean) | PASS | PQ-09 |
| Rules (ColumnExists, TypeIs, NotNull, Unique, AllowedValues, Range, RecordCount) with row numbers, never values | PASS | PQ-10, PQ-B9 |
| Schema + keyed record comparison, stated direction and Swap | PASS | PQ-12, PQ-B10a, PQ-B10 |
| Evidence without row values; link to an existing run of the same environment; export; local deletion | PASS | PQ-15…18, PQ-23 |
| Original file never modified | PASS | PQ-19 |
| Files larger than 64 MB / streaming from storage | **NOT SUPPORTED** | bounded by design |
| Opening Parquet from Blob Storage | see the Blob Storage gap register | the same engine is called by the Blob explorer preview |
| Automatic retention of evidence | **NOT RUN** | manual deletion only (scheduled retention stays open across BirkNext) |
| UI responsive 1440–390, keyboard, no external requests | PASS | PQ-B14, PQ-B15, PQ-Z1 |

## Regression

Run on a clean tree equal to `514478d2` + exactly the files of this commit (the first run was cut off when the session ended after
partitions 1–2; it was rerun in full during crash recovery):

| Suite | Result |
|---|---|
| Backend partitions 1–5 | 1031/0/2, 1098/0/5, 830/0/0, 652/0/1, 520/0/3 (passed/failed/skipped) |
| Backend partition 6 | 2122 passed, **1 failed**, 8 skipped — `ScenariosQueryTests.Scenarios_With100Records_ReturnsAllOrderedDescAndWarmedP95CompletesWithinTwoSeconds` (wall-clock p95 2710 ms > 2000 ms; median 112 ms, two outliers > 2.7 s while the partition ran at 4 GB peak). The Scenarios query is not touched by this change; the class reran **5/5 PASS** in isolation. Recorded as a load-sensitive timing test, not relaxed or excluded. |
| Frontend | 8508/0/0 |

Scratch resources (not committed): `scratchpad/pqvalidation/`, DB `birknext_pq_20261109`.
