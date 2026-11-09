# BirkNext — Parquet Data Explorer — implementation (2026-11-09)

Branch `008-traceability-first` (local only). Audit: `docs/audits/birknext-parquet-viewer-audit-2026-11-09.md`.

## Flow (local file first, as requested)

Open → Overview → Schema → Data → Filter → Profile → Validate → Compare → Save Evidence, at `/parquet-viewer` (Quality & Testing), with a User
Guide entry. The same engine is the one a Blob explorer will call (no separate cloud implementation).

| Part | File |
|---|---|
| Reader (footer, schema, pages, encodings, codecs, value conversion) | `backend/.../Services/Parquet/ParquetFile.cs`, `ThriftCompact.cs` (+ Snappy) |
| Sessions, rows, filters, profile, rules, comparison, evidence | `Services/Parquet/ParquetInspectionService.cs` |
| API `api/parquet` | `Controllers/ParquetController.cs` |
| Contracts | `shared/ParquetContracts.cs` |
| Table `parquet_inspections` (migration `AddParquetInspections`) | `Models/ExecutionEvidenceRecords.cs`, `Data/AppDbContext.cs` |
| UI | `Pages/ParquetViewer.razor`, `Services/ParquetApiService.cs`, navigation + User Guide |

## Behaviour

- **Opening**: ≤ 64 MB; header and footer magic, footer length, schema child counts, row-group sums, chunk paths and page sizes are validated; a
  corrupt, truncated, empty or encrypted-footer file is refused with a reason (no internal path in the message). The upload is copied to a
  BirkNext-named temporary file (`App_Data/parquet-sessions/{id}.parquet`), never the uploaded name; sessions expire after 2 h; closing removes the
  copy. The tester's original file is never touched (BirkNext never has its path).
- **Overview** labels statistics as the file's metadata (`FromMetadata`) or `NotAvailable`; nothing is inferred.
- **Schema**: tree with physical type, logical annotation, repetition, fixed length, and per-column readability with the reason.
- **Data**: projected, bounded pages (≤ 200 rows) that decode only the row groups overlapping the range; nulls shown as null; binary values
  summarised (`binary (n bytes)`), never rendered.
- **Filters**: Equals, NotEquals, Contains, GreaterThan, LessThan, Between, IsNull, NotNull, In — typed (numbers, decimals, dates, timestamps,
  booleans, strings); an unreadable comparison value never matches. Without "scan the whole file" the scope says *this page only*; with it, an
  explicit full scan (≤ 2 000 000 rows) counts every match.
- **Profile** (explicit full scan): exact nulls / non-null / distinct (exact up to 100 000, then "counting stopped"), min, max, mean.
- **Rules** (versioned rule set): ColumnExists, TypeIs (physical or logical), NotNull, Unique, AllowedValues, Range, RecordCount. Each result has
  outcome, expected, actual, scope (Schema / File metadata / Full scan), rows examined and up to 5 failing row **numbers** — never values.
- **Compare**: schema changes (added, removed, type, nullability); with a key column, record-level Added / Removed / Changed / Unchanged / Ambiguous
  (duplicate keys) and changed field names (values not shown), bounded to 500 listed differences with complete counts; without a key, schema and
  counts only. Row order is never identity.
- **Evidence** (write permission): file name, SHA-256, size, rows, columns, schema fingerprint, rule results, comparison counts, a declared link to
  an existing Execution Evidence run of the same environment, note — **no row values**; listed, exported (JSON), deleted locally.

## Tests

`ParquetExplorerTests` (15, against fixtures written by `Fixtures/make_fixtures.py` — a standard-library Python writer — and its `oracle.json`):
metadata and logical types, exact values across row groups, data page v2 + GZIP, page vs full-scan filters, exact profile, rules, keyed and unkeyed
comparison, nested groups and ZSTD reported, corrupt files refused, sessions and source preservation, evidence without row values and run linking,
malformed Snappy/RLE. `ParquetViewerPageTests` (5, incl. Swap). Navigation, documentation and source-ownership guards updated (the Parquet upload is listed as a
data-file upload, never a source upload).
