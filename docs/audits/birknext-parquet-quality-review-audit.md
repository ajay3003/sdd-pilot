# BirkNext — Parquet Quality Review: independent audit (2026-11-10)

Branch `008-traceability-first`. **Baseline `f55c7c49`**. Working tree at start: only the user's `appsettings.Local.json` (modified) and other
sessions' untracked files (`AIAssisted/testdata/`, `docs/audits/birknext-final-*-2026-11-07.md`, `birknext-release-assurance-decision-2026-11-07.md`,
`birknext-automatic-integration-test-generation-audit.md`). None overlaps Parquet, Source Analysis, Test Data or Execution Evidence; they are
not staged by this work.

## 1. What exists (read from code, not from earlier reports)

| Area | Implementation | Observed |
|---|---|---|
| Reader | `Services/Parquet/ParquetFile.cs` + `ThriftCompact.cs` (in-house) | flat columns, UNCOMPRESSED/SNAPPY/GZIP, PLAIN/dictionary/RLE; repeated columns, ZSTD/LZ4/BROTLI and DELTA encodings marked *not readable* with a reason. Kept as the only engine. |
| Sessions | `ParquetInspectionService.OpenAsync/Session/Close/Cleanup` | 64 MB enforced three times (client, request limit 65 MB, streamed byte count). Two-hour lifetime written into a `.json` beside a BirkNext-named copy in `App_Data/parquet-sessions`. Expiry is lazy (on access) and the sweep runs only when a new file is opened. |
| Metadata | `Overview()` | footer row count, row groups, codecs, encodings, statistics labelled `FromMetadata`, SHA-256 of the bytes, 16-hex schema fingerprint. |
| Rows | `Rows()` | 1–200 rows, projection (default first 12 readable), list of filters combined with AND; page-only vs explicit full scan (≤ 2,000,000 rows) labelled. |
| Profile | `Profile()` | exact full scan; distinct counting capped at 100,000; mean of numeric values. No category distribution. |
| Validation | `Validate()` | 7 kinds (ColumnExists, TypeIs, NotNull, Unique (one column), AllowedValues, Range, RecordCount); Pass/Fail/Inconclusive; first 5 failing row numbers. |
| Comparison | `Compare()` | schema changes; records matched by **one** key column; duplicates Ambiguous; ≤ 500 listed records. |
| Evidence | `parquet_inspections` (one JSON document) | re-runs validation and comparison on save; `LinkedRunId` checked against the newest 500 runs of the environment. |
| Export | `GET evidence/{id}/export` | JSON of the stored document. |
| UI | `Pages/ParquetViewer.razor` | seven tabs (Overview, Schema, Data, Profile, Validation, Compare, Evidence), two file inputs, generic rule form, free-text run id. |
| Source contracts | — | no link between Parquet and Source Analysis. Snapshots retain XSDs and OpenAPI/GraphQL documents only (`IqrSourceStore`); Markdown contract documents are read during analysis and discarded. |
| Skolenærvær / Bufdata | `SkoleNaervaerContractProfile` (`skole-to-bufdata`, "no Parquet library"), journey steps `dataset-verify` / `dataset-pii` not runnable, `SkoleNaervaerSecurityConsumer` requirement `bufdata-exclusion` | modelled only. |

## 2. Real contract evidence (imported archives, read-only)

`AIAssisted/testdata/M2LB.zip` and `M2LB-language.zip` both hold the same
`SkoleService/specs/001-skoleservice/contracts/bufdata-leveranse.md` (SHA-256 prefix `8370301a7fde`): **contract version `1.0.0-draft`**, two
containers (`skolenaervaer-rapporter`, `skolenaervaer-dekning`), path `period=YYYY-MM/data.parquet` + `_manifest.json`, the dataset table (11
columns incl. DECIMAL and LIST/STRUCT), the coverage table (`period`, `category`, `measure`, `reason_code`, `count`), the coverage rules
(`RECEIVED + REJECTED_ONLY + MISSING = EXPECTED` per category; the two categories add up to `TOTAL`; `RECEIVED_NOT_EXPECTED` /
`REJECTED_UNATTRIBUTED` only with `TOTAL`), sort order, manifest structure and determinism (one row group, Snappy). The header records the
2026-10-20 renaming from `1.0.0-utkast` (Norwegian names) and `specs/002-translate-source-english/wire-changes.md` §3 lists the value renames
(`TOTALT`→`TOTAL`, `FORVENTET`→`EXPECTED`, …) and `periode`→`period`. **No document maps `kategori`, `maal`, `aarsakskode` or `antall`.**
The writer (`ParquetWriters.cs`) confirms the English column names.

## 3. Fixture, verified independently

`skolenaervaer-dekning-example.parquet` (1,145 bytes), read with a stdlib Python reader written for this audit (`pq_oracle.py`, not BirkNext's
reader): **15 rows, 1 row group, UNCOMPRESSED, created by "BirkNext Synthetic Parquet Fixture Writer 1.0"**; columns `periode` (UTF8, required),
`kategori` (UTF8, required), `maal` (UTF8, required), `aarsakskode` (UTF8, **optional**, 13 nulls), `antall` (INT32, required, 0…10). Values:
one period `2026-10`; categories FOSTERHJEM / PRIVAT_INSTITUSJON / TOTALT; FORVENTET = MOTTATT + MANGLER + KUN_AVVIST holds in each category and
the two categories sum to TOTALT; `aarsakskode` only on the two AVVIST_IKKE_TILORDNET rows. **The fixture uses the pre-translation
(`1.0.0-utkast`) names, so against the current `1.0.0-draft` contract it is schema-incompatible.** It is synthetic and never a production
delivery.

## 4. Screenshot / UX audit (verified in code)

| Observation | Functional or presentational | Confirmed |
|---|---|---|
| Large title + explanation, two file inputs always visible | presentational + workflow (baseline asked before the explorer is useful) | yes |
| Seven flat tabs | IA | yes |
| Overview = metadata table | presentational; no quality status, no contract | yes |
| Schema = plain type strings | presentational; no expected-vs-actual | yes |
| Data: single filter, unlabelled "and" field (Between upper bound) | **functional**: one filter only in the UI although the API takes many; "Next" disabled for every full scan (cannot page matches) | yes |
| Profile: statistics without interpretation | presentational + functional (no category distribution, no duplicate count) | yes |
| Validation: generic form (Rule/Column/Expected/Max/Values for every kind) | **functional**: no generated rules, no composite unique, no conditional/cross-column/aggregate rules, no review | yes |
| Compare requires second upload at the top | workflow | yes |
| Evidence: free-text Run ID | **functional**: unsearchable; the check misses runs older than the newest 500 | yes |
| Sessions closed on every navigation (`DisposeAsync`) | functional: no "recent files" possible | yes |

## 5. Gap matrix

| ID | Observed behaviour | Actual implementation | Confirmed gap | Root cause | Risk | Required improvement | Verification |
|---|---|---|---|---|---|---|---|
| PQ-G1 | No contract anywhere in Parquet | explorer has no Source Analysis dependency; snapshots discard Markdown | yes | contract documents not retained; no dataset-contract model | untested deliveries; manual rules | retain dataset-contract documents with the snapshot; parse column tables, rules, manifest, determinism; discovery per project + snapshot with AUTHORITATIVE / CANDIDATE / AMBIGUOUS / UNRESOLVED | unit (parser, discovery) + real M2LB import |
| PQ-G2 | Rules typed by hand | `ParquetRuleSet` from the UI only | yes | no generator | missed mandatory requirements | rule drafts classified SCHEMA_DERIVED / CONTRACT_DERIVED / APPROVED_BUSINESS_RULE / USER_AUTHORED with provenance, semantics, scope, full-scan flag, exposure, contract + rule version; unsupported statements NOT SUPPORTED, business rules NEEDS REVIEW and disabled | unit + browser |
| PQ-G3 | No expected-vs-actual schema | only baseline-vs-current schema changes | yes | — | breaking schema unnoticed | contract schema comparison (missing, unexpected, physical, logical, nullability, decimal, nested) with COMPATIBLE / COMPATIBLE_WITH_WARNINGS / INCOMPATIBLE | unit + synthetic oracle |
| PQ-G4 | Rule kinds limited | 7 kinds; Unique single column | yes | — | business rules untestable | composite unique, pattern/format, conditional null/required, value-implies-value, grouped sum identity, roll-up, sort order, row groups, codec, manifest, identifier scan | unit + synthetic oracle |
| PQ-G5 | Result lacks rule source, contract version, exact/sampled, evidence ref | `ParquetRuleResult` | yes | — | results not auditable | extend result (additive fields) | unit |
| PQ-G6 | No requirement coverage | — | yes | — | "all green" without denominator | requirement list from the contract; COVERED_PASS / COVERED_FAIL / NOT_ASSESSED / BLOCKED / NOT_SUPPORTED / NOT_APPLICABLE; denominator limitation shown | unit + browser |
| PQ-G7 | Saved evidence holds row values | `Comparison.Records[].Key` = key-column values (≤ 500) | **yes (security)** | comparison object stored as-is | protected identifiers (e.g. `birk_id`) persisted and exported | evidence keeps counts and changed-field names only; keys never stored | unit (no key in document) |
| PQ-G8 | Run link rejects existing runs | `ListAsync(Take: 500)` | yes | list used as lookup | false rejection; no relation check | direct lookup; environment + project match; correlation CONFIRMED (file SHA-256 in the run's captured evidence) / CANDIDATE (same source snapshot) / MANUAL_UNVERIFIED / NOT_CORRELATED; searchable picker | unit + browser |
| PQ-G9 | Memory growth on uploads | `OpenAsync` adds to a static cache without eviction | yes | eviction only on reload from disk | many uploads × 64 MB in memory | evict on open (≤ 4 cached) | unit |
| PQ-G10 | Unbounded live sessions on disk | no session cap | yes | — | disk filling | ≤ 20 live sessions (oldest closed) + lazy sweep kept | unit |
| PQ-G11 | Full-scan matches cannot be paged | UI disables Next when full scan | yes | UI condition | matches beyond 50 invisible | page by match offset | bUnit |
| PQ-G12 | One filter, unclear "and" | UI | yes | UI | wrong filters | filter chips, multiple filters, type-specific editors ("from/to", value list), NotIn | bUnit |
| PQ-G13 | Profile without categories/duplicates | `Profile()` | yes | — | domain summaries missing | category distributions (≤ 50 values, exact), duplicate-row count, contract-defined measure pivot | unit |
| PQ-G14 | Comparison single key, no comparability | `Compare()` | yes | — | unrelated datasets compared as regression | composite key; comparability (contract, version, schema, period); REGRESSION / IMPROVEMENT / EXPECTED_CHANGE / INCOMPATIBLE / INCONCLUSIVE / NOT_COMPARABLE; comparison against a saved evidence baseline (summary level) | unit + browser |
| PQ-G15 | No quality report | JSON export only | yes | — | — | Markdown report (14 sections) from stored evidence through the existing download helper; no row values | unit + browser |
| PQ-G16 | History cannot be reopened | list only | yes | — | — | evidence detail view; "original file no longer available" stated; no re-run | browser |
| PQ-G17 | Confidentiality not checked | — | yes | — | identifier leakage | reuse `ContractTestDataRules` identity/classification names, the 11-digit detector and approved `protected-identity` Test Data canaries; absence never proof; Kode 6/7 exclusion NEEDS DOMAIN DECISION unless correlated | unit |
| PQ-G18 | Sessions close on navigation | `DisposeAsync` | yes | — | no recent files | keep sessions until expiry/explicit close; recent list per browser validated against the server | bUnit |
| PQ-G19 | Approved business rules cannot be kept | — | yes | — | review lost | immutable approved rule-set versions per project + contract + version (one table) | unit |
| PQ-G20 | Blob Storage as a source | Blob explorer previews the first row group only | — | — | — | NOT SUPPORTED here (no full read into a session; no live Blob reads in this work) | — |

Implementation order follows the brief (A…T); see the implementation report.
