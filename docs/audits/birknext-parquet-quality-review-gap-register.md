# BirkNext — Parquet Quality Review: gap register (2026-11-10)

Branch `008-traceability-first`. **Baseline `f55c7c49`**. Local commits: `52f3c395` (backend), `13f1dc5b` (UI), reports + parser fix commit
(final SHA in the session report).

## Verification summary

| Item | Status | Result |
|---|---|---|
| Parquet reader, schema, data quality, profile, comparison, contract mapping, rule generation, confidentiality, evidence, report, security (focused backend: `ParquetExplorerTests` + `ParquetQualityTests`) | PASS | 37/37 |
| Focused frontend (Parquet page, navigation, documentation catalog, guide) | PASS | 120/120 (page: 9/9) |
| Independent synthetic acceptance (unit oracle + browser) | PASS | 12 fixtures × oracle; browser SQ-01…06 |
| Real Skolenærvær sample acceptance | PASS | RQ-01…13 |
| Browser acceptance (Release build, 5 widths, restart) | PASS | **48/48** |
| Backend full regression (six memory-guarded partitions) | PASS | 1,031 + 1,098 + 830 + 652 + 520 + 2,244 = **6,375 passed, 19 skipped, 0 failed** |
| Frontend full regression | PASS | **8,559/8,559** |
| Release frontend build | PASS | published (scratch `webpub-its`) and used by the browser acceptance |

## Gap disposition

| Id | Gap | Final status | Notes |
|---|---|---|---|
| PQ-G1 | Contract discovery from Source Analysis | PASS | dataset contracts retained with snapshots; AUTHORITATIVE / CANDIDATE / AMBIGUOUS / UNRESOLVED; project + snapshot explicit |
| PQ-G2 | Rule generation with classification | PASS | CONTRACT_DERIVED / SCHEMA_DERIVED / APPROVED_BUSINESS_RULE / USER_AUTHORED; business rules NEEDS REVIEW until approved |
| PQ-G3 | Contract schema validation | PASS | missing, unexpected, physical, logical, decimal, nullability, nested (presence) |
| PQ-G4 | Rule kinds | PASS | 12 new kinds in the existing engine |
| PQ-G5 | Result provenance and scope | PASS | rule id, origin, contract version, expected/actual, rows examined, mode, evidence ref |
| PQ-G6 | Requirement coverage | PASS | six statuses; denominator stated |
| PQ-G7 | Row values in saved evidence (comparison keys) | PASS (security) | never stored; stripped from older documents on read/export |
| PQ-G8 | Run link limited to newest 500, no relation check | PASS | direct lookup; CONFIRMED / CANDIDATE / MANUAL_UNVERIFIED / NOT_CORRELATED |
| PQ-G9 / G10 | Unbounded in-memory cache / live sessions | PASS | ≤ 4 decoded, ≤ 20 live |
| PQ-G11 / G12 | Full-scan paging; single filter with unlabelled "and" | PASS | chips, typed editors, explicit scope, match paging |
| PQ-G13 | Profile interpretation | PASS | categories, duplicates, contract aggregate, EXACT labels |
| PQ-G14 | Comparison semantics | PASS | composite keys, comparability, six classes; evidence baseline at summary level |
| PQ-G15 / G16 | Quality report; history reopen | PASS | 14-section Markdown from saved evidence; "cannot be re-run" |
| PQ-G17 | Confidentiality | PARTIAL | identifier and canary scans reuse shared detectors and approved Test Data; Kode 6/7 exclusion is NEEDS DOMAIN DECISION (needs a correlated protected-child scenario with expected counts); no approved canary exists in the scratch Test Data, so the canary rule was Inconclusive in the browser (unit-tested with a canary) |
| PQ-G18 | Recent files | PASS | sessions survive navigation; per-browser list validated by the server |
| PQ-G19 | Approved rule sets | PASS | `parquet_rule_sets`, immutable versions |
| PQ-G20 | Blob Storage as a source | NOT SUPPORTED | Blob explorer previews the first row group; no full read into a session; no live Blob reads in this work |
| PQ-D1 | Renames for another artifact (database tables) taken as Parquet mappings; data-model tables taken as Parquet contracts | PASS (fixed) | found by browser acceptance; block-scoped renames, Parquet section rule |
| PQ-D2 | Wrapped rule sentences not recognised | PASS (fixed) | paragraph-aware sentences |
| PQ-G21 | Sorting in the data grid | NOT SUPPORTED | stated on the page (rows in file order) |
| PQ-G22 | Nested columns (lists/structs, e.g. the `skolenaervaer-rapporter` dataset's `fravaer`, `quality_flags`) | PARTIAL | presence and nesting checked; element values not read (the reader does not read repeated columns) |
| PQ-G23 | Manifest from storage | NOT SUPPORTED | the tester attaches `_manifest.json`; it is not read from storage (no live Blob reads) |
| PQ-G24 | Prose requirements outside dataset, Location, Manifest and Determinism sections | NOT SUPPORTED (listed) | other statements are not extracted; the coverage note states the denominator |
| PQ-G25 | Expected-change rules for baselines | PARTIAL | EXPECTED_CHANGE only for a different period; no approved per-dataset change expectations yet |
| PQ-G26 | Real `1.0.0-draft` file | NOT RUN | no file in the new layout exists locally; the sample is the legacy layout |

## Security findings

Saved evidence and exports hold no row values (comparison keys removed, category values only when the contract enumerates them, live group
details and drill-down filters never persisted); identifier and canary scans report counts and column names only; declared mappings and rule
sets are re-validated on the server (a client cannot activate an unapproved business rule); evidence save, rule approval and baseline approval
require the write policy; no endpoint takes a server path; uploads stay ≤ 64 MB, manifests ≤ 64 KB; no external call (browser acceptance: no
external request, outbound proxies to a dead port).
